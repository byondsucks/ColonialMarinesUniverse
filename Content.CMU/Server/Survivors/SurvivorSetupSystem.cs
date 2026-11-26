using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.CMU14.Ops.ThirdParty;
using Content.Server.CMU14.Round;
using Content.Server.CMU14.Yautja;
using Content.Server.Humanoid.Components;
using Content.Server.Humanoid.Systems;
using Content.Server.Ghost.Roles.Components;
using Content.Server.EUI;
using Content.Server.Preferences.Managers;
using Content.Shared.CCVar;
using Content.Shared.CMU14.Survivors;
using Content.Shared.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Threats;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.StatusIcon;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;
using Robust.Shared.Timing;

namespace Content.Server.CMU14.Survivors.Setup;

/// <summary>
///     Holds rolled survivors in the lobby while they vote between the two factions drawn for their group
///     and pick a role and a character, then hands the choices to <see cref="ThirdPartySystem"/> to spawn.
///     Nothing is spawned until this finishes, and every held player sees the same roster throughout.
/// </summary>
public sealed class SurvivorSetupSystem : EntitySystem
{
    [Dependency] private AuRoundSystem _auRound = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IServerPreferencesManager _prefs = default!;
    [Dependency] private IRobustSerializer _serializer = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private RandomHumanoidAppearanceSystem _randomAppearance = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SurvivorLoadoutSystem _loadouts = default!;
    [Dependency] private ThirdPartySystem _thirdParty = default!;
    [Dependency] private YautjaPredatorRoundSystem _yautja = default!;
    [Dependency] private IGameTiming _timing = default!;

    private static readonly ProtoId<JobPrototype> LeaderJob = "AU14JobThirdPartyLeader";
    private static readonly ProtoId<JobPrototype> MemberJob = "AU14JobThirdPartyMember";
    private static readonly ProtoId<JobPrototype> StrandedJob = "AU14JobStrandedThirdPartyMember"; // CMU14
    private static readonly TimeSpan VoteDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PickDuration = SurvivorSetupTimings.Preparation;

    /// <summary>How long a contested role claim stays open before the winner is drawn.</summary>
    private static readonly TimeSpan ClaimRaffleDuration = SurvivorSetupTimings.Raffle;

    private readonly List<ThirdPartyPrototype> _candidates = new();

    /// <summary>Every faction the group could be drawn as this round, shown in the window.</summary>
    private readonly List<ThirdPartyPrototype> _available = new();

    private readonly Dictionary<NetUserId, HeldSurvivor> _held = new();
    private readonly List<RoleSlot> _slots = new();
    private SurvivorSetupPhase? _phase;
    private TimeSpan _phaseEnd;
    private ThirdPartyPrototype? _winner;
    private SurvivorLoadoutPrototype? _winnerLoadout;
    private bool _predatorRoundFailed;

    /// <summary>Throttles the one-second state refresh that ticks the raffle countdown in the window.</summary>
    private TimeSpan _nextStateRefresh;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        foreach (var held in _held.Values)
        {
            held.Eui?.Close();
        }

        _held.Clear();
        _candidates.Clear();
        _available.Clear();
        _slots.Clear();
        _phase = null;
        _winner = null;
        _winnerLoadout = null;
    }

    /// <summary>True while this player is held in the lobby by the survivor setup vote.</summary>
    public bool IsHeld(NetUserId user) => _held.ContainsKey(user);

    /// <summary>
    ///     Whether a faction may appear as a survivor group option. Faction options come from the planet's
    ///     third-party list (<see cref="ThirdPartyPrototype.RoundStart"/> + <see cref="ThirdPartyPrototype.AnnounceAsSurvivors"/>);
    ///     this is the one place to exclude factions or gate them on round state, e.g. population limits.
    ///     Kept permissive on purpose: balancing rules are a later task.
    /// </summary>
    public bool IsFactionEligible(ThirdPartyPrototype party, int survivorCount)
        => party.RoundStart && party.AnnounceAsSurvivors && HasCompleteRoleRegistry(party);

    /// <summary>
    ///     A faction is only offered when every role its spawn template lists has a survivor role subtype.
    ///     Legacy third-party survivor parties still carry the survivor announcement flag from before the
    ///     registry existed; without this check they would be drawn into the vote and spawn roles the picker
    ///     can neither name nor compose.
    /// </summary>
    private bool HasCompleteRoleRegistry(ThirdPartyPrototype party)
    {
        if (!_prototypes.TryIndex(party.PartySpawn, out PartySpawnPrototype? spawn))
            return false;

        foreach (var role in spawn.LeadersToSpawn.Keys.Concat(spawn.GruntsToSpawn.Keys))
        {
            if (!_prototypes.HasIndex<SurvivorRolePrototype>(role))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Weight for a faction, used to break vote ties and to pick a faction when nobody voted.
    ///     Per-faction spawn probabilities belong here once balancing is designed.
    /// </summary>
    public int GetFactionWeight(ThirdPartyPrototype party) => Math.Max(1, party.weight);

    /// <summary>
    ///     The factions this round's group can be drawn as: every survivor faction eligible this round,
    ///     independent of the factions the third-party scheduler picked, so the scheduler can never decide
    ///     the vote. The full list is also sent to the window so players can see the whole pool.
    /// </summary>
    private List<ThirdPartyPrototype> GetFactionPool(int survivorCount)
        => _auRound.GetEligibleSurvivorFactions()
            .Where(party => IsFactionEligible(party, survivorCount))
            .ToList();

    /// <summary>
    ///     Draws the two factions offered in the group vote. If the pool only has one, that faction is the
    ///     group's faction without a vote.
    /// </summary>
    private List<ThirdPartyPrototype> DrawFactionOptions(IReadOnlyList<ThirdPartyPrototype> pool)
    {
        if (pool.Count == 0)
            return new List<ThirdPartyPrototype>();

        var byId = pool.ToDictionary(party => party.ID, StringComparer.OrdinalIgnoreCase);

        // CMU14 TEMPORARY (survivor rework testing): list every eligible faction on the ballot.
        if (_cfg.GetCVar(CCVars.SurvivorTestAllCandidates))
            return SurvivorFactionDraw.AllOptions(byId.Keys.ToList()).Select(id => byId[id]).ToList();

        var ids = SurvivorFactionDraw.PickDistinct(
            byId.Keys.ToList(),
            id => GetFactionWeight(byId[id]),
            _random.Next,
            SurvivorFactionDraw.OptionCount);

        return ids.Select(id => byId[id]).ToList();
    }

    public void Begin(Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)> assignedJobs)
    {
        if (_phase != null || _held.Count > 0)
            return;

        foreach (var (user, (job, _)) in assignedJobs)
        {
            if (job != LeaderJob && job != MemberJob && job != StrandedJob)
                continue;

            _held[user] = new HeldSurvivor
            {
                Leader = job == LeaderJob,
                // Own character is the default, so the roster shows it during the faction vote too.
                Character = _prefs.GetPreferencesOrNull(user)?.SelectedCharacterIndex,
            };
        }

        if (_held.Count == 0)
            return;

        _candidates.Clear();
        _available.Clear();
        _available.AddRange(GetFactionPool(_held.Count));
        _candidates.AddRange(DrawFactionOptions(_available));

        if (_candidates.Count == 0)
        {
            _held.Clear();
            return;
        }

        Log.Info($"Survivor setup: options=[{string.Join(", ", _candidates.Select(party => party.ID))}], held={_held.Count}");

        _thirdParty.ReserveSurvivorParties();

        if (_candidates.Count == 1)
        {
            _winner = _candidates[0];
            StartPick();
        }
        else
        {
            _phase = SurvivorSetupPhase.Vote;
            _phaseEnd = _timing.CurTime + VoteDuration;
        }

        OpenEuis();
    }

    /// <summary>Opens the setup window for every held survivor (faction or civilian).</summary>
    private void OpenEuis()
    {
        foreach (var (user, held) in _held)
        {
            if (!_player.TryGetSessionById(user, out var session))
                continue;

            held.Eui = new SurvivorSetupEui(this);
            _eui.OpenEui(held.Eui, session);
        }
    }

    public override void Update(float frameTime)
    {
        // Players can drop out mid-vote; keep the roster and the slot list in step with who is still here.
        if (PruneDisconnected())
            DirtyAll();

        // Role claims resolve on their own clock, inside the pick phase.
        if (ResolveExpiredClaims())
            DirtyAll();

        // Keep the raffle countdown in the window ticking while a claim is open.
        if (_phase == SurvivorSetupPhase.Pick && _timing.CurTime >= _nextStateRefresh)
        {
            _nextStateRefresh = _timing.CurTime + TimeSpan.FromSeconds(1);
            DirtyAll();
        }

        if (_phase == null || _timing.CurTime < _phaseEnd)
            return;

        if (_phase == SurvivorSetupPhase.Vote)
        {
            _winner = ResolveWinner();
            StartPick();
            DirtyAll();
            return;
        }

        Finish();
    }

    /// <summary>Draws winners for every role raffle whose window has run out.</summary>
    private bool ResolveExpiredClaims()
    {
        var changed = false;
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Owner != null ||
                slot.ClaimEndsAt is not { } endsAt ||
                _timing.CurTime < endsAt)
            {
                continue;
            }

            ResolveClaim(i, slot);
            changed = true;
        }

        return changed;
    }

    private void ResolveClaim(int index, RoleSlot slot)
    {
        slot.ClaimEndsAt = null;
        var claimants = slot.Claimants;
        if (claimants.Count == 0)
            return;

        // Players who already own another role do not take part, so nobody can end up with two roles.
        var owned = new Dictionary<NetUserId, int>();
        for (var i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].Owner is { } owner)
                owned[owner] = i;
        }

        var eligible = SurvivorRoleOwnership.Eligible(claimants, index, owned);
        claimants.Clear();
        if (eligible.Count == 0)
            return;

        if (SurvivorRoleRaffle.PickWinner(eligible, _random.Next) is not { } winner)
            return;

        slot.Owner = winner;

        foreach (var claimant in eligible)
        {
            if (claimant == winner)
                continue;

            if (_held.TryGetValue(claimant, out var held) && held.Slot == index)
                held.Slot = null;
        }

        if (_held.TryGetValue(winner, out var winnerHeld))
            winnerHeld.Slot = index;
    }

    public SurvivorSetupEuiState BuildState(NetUserId user)
    {
        var state = new SurvivorSetupEuiState
        {
            Phase = _phase ?? SurvivorSetupPhase.Pick,
            PhaseEnd = _phaseEnd,
            ResolvedFaction = _winner?.DisplayName ?? _winner?.ID,
            PredatorRoundFailed = _predatorRoundFailed,
            PhaseSeconds = (float) (_phase == SurvivorSetupPhase.Vote ? VoteDuration : PickDuration).TotalSeconds,
        };

        _held.TryGetValue(user, out var me);
        state.MyVote = me?.Vote;
        state.MySlot = me?.Slot;
        state.MyCharacter = me?.Character;

        // Loadout selection only unlocks once this player won a role in the raffle; firearms are filtered
        // to what that role may use.
        string? ownedRole = TryGetOwnedRole(user, out var roleProto) ? roleProto : null;
        state.HasLoadout = _winnerLoadout != null && ownedRole != null;
        if (_winnerLoadout is { } loadout && ownedRole != null)
        {
            var options = _loadouts.GetOptions(loadout, ownedRole);
            state.ArmorOptions.AddRange(options.Armor);
            state.HelmetOptions.AddRange(options.Helmets);
            state.FirearmOptions.AddRange(options.Firearms);
            // Show the selection as this role would spawn with it: a role whose armor/helmet is fixed has
            // exactly one option, so the player sees that item selected instead of the faction default.
            var effective = _loadouts.Resolve(loadout, me?.Loadout ?? default, ownedRole);
            state.MyArmor = effective.Armor;
            state.MyHelmet = effective.Helmet;
            state.MyFirearm = effective.Firearm;
            state.MyLoadoutConfirmed = me?.LoadoutConfirmed ?? false;
        }

        // Character selection follows loadout confirmation.
        state.CanPickCharacter = !state.HasLoadout || state.MyLoadoutConfirmed;

        {
            var votes = SurvivorFactionVote.CountVotes(
                _candidates.Select(party => party.ID).ToList(),
                _held.Values.Select(held => (string?) held.Vote));

            foreach (var party in _candidates)
            {
                state.Parties.Add(new SurvivorPartyOption(party.ID, party.DisplayName ?? party.ID,
                    votes.GetValueOrDefault(party.ID)));
            }
        }

        // A won role is final: the client disables the other rows and the server refuses further claims.
        state.MySlotLocked = SurvivorRoleClaims.IsLocked(user, me?.Slot,
            me?.Slot is { } lockedSlot && lockedSlot < _slots.Count ? _slots[lockedSlot].Owner : null);

        // The whole pool, not just the two options, so players can see what else exists this round.
        foreach (var party in _available)
            state.AvailableFactions.Add(party.DisplayName ?? party.ID);

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            string? claimedBy = null;
            if (slot.Owner is { } owner && _player.TryGetSessionById(owner, out var ownerSession))
                claimedBy = ownerSession.Name;

            var contestants = new List<string>(slot.Claimants.Count);
            foreach (var claimant in slot.Claimants)
            {
                if (_player.TryGetSessionById(claimant, out var claimantSession))
                    contestants.Add(claimantSession.Name);
            }

            // Ceiling so the countdown reads 5,4,3,2,1 rather than starting at 4 - but clamped to the raffle
            // length, because floating-point drift on the deadline used to make the first tick read 6.
            int? secondsLeft = slot.ClaimEndsAt is { } endsAt
                ? Math.Clamp((int) Math.Ceiling((endsAt - _timing.CurTime).TotalSeconds), 0,
                    (int) SurvivorSetupTimings.Raffle.TotalSeconds)
                : null;

            // The picker shows the faction's own subtype name, never the job/entity name the role body
            // inherits from an unrelated spawn (which is how "Insurgent Reinforcements" used to appear).
            var bodyName = _prototypes.TryIndex<EntityPrototype>(slot.Proto, out var proto) ? proto.Name : null;
            var subtype = _prototypes.TryIndex<SurvivorRolePrototype>(slot.Proto, out var role) ? role : null;
            var name = SurvivorRoleDisplay.Resolve(slot.Proto, subtype?.Name, bodyName);
            var category = _loadouts.RoleCategory(slot.Proto);
            var (iconSprite, iconState) = IconFor(category);
            state.Slots.Add(new SurvivorRoleSlot(i, name, slot.Leader, claimedBy, contestants, secondsLeft,
                category, iconSprite, iconState, RoleRequiresSynthetic(slot.Proto)));
        }

        state.MyCharacterIsSynthetic = SelectedCharacterIsSynthetic(user);

        if (_prefs.GetPreferencesOrNull(user) is { } prefs)
        {
            // A synthetic-only role can only spawn a synthetic character, so only those are offered.
            var synthOnly = TryGetOwnedRole(user, out var owned) && RoleRequiresSynthetic(owned);
            foreach (var (index, profile) in prefs.Characters)
            {
                if (profile is not HumanoidCharacterProfile humanoid)
                    continue;

                if (synthOnly && !humanoid.Synthetic)
                    continue;

                state.Characters.Add(new SurvivorCharacterOption(index, humanoid.Name));
            }
        }

        state.Roster.AddRange(BuildRoster(user));
        return state;
    }

    /// <summary>
    ///     The whole group, one row per held player, with a preview snapshot of the character they will spawn
    ///     as. Only held survivors are listed, so nobody outside the group (and no opposing faction) is ever
    ///     exposed, and the preview is dressed in the kit of the role or faction the player picked.
    /// </summary>
    private List<string> BuildRoster(NetUserId viewer)
    {
        var candidates = new List<SurvivorRosterPreview.Candidate>(_held.Count);
        foreach (var (user, held) in _held)
        {
            var ckey = _player.TryGetSessionById(user, out var session)
                ? session.Name
                : user.UserId.ToString();

            string? role = null;
            string? roleProto = null;
            if (held.Slot is { } index && index < _slots.Count && _slots[index].Owner == user)
            {
                roleProto = _slots[index].Proto;
                var bodyName = _prototypes.TryIndex<EntityPrototype>(roleProto, out var entity) ? entity.Name : null;
                var subtype = _prototypes.TryIndex<SurvivorRolePrototype>(roleProto, out var subtypeProto)
                    ? subtypeProto
                    : null;
                role = SurvivorRoleDisplay.Resolve(roleProto, subtype?.Name, bodyName);
            }

            // What this player has actually picked. Resolving the loadout here would hand back the faction/role
            // default, which is not something the player chose - so equipment is only reported once they have
            // confirmed it, and the card shows a pending state until then.
            string? armor = null;
            string? helmet = null;
            string? firearm = null;
            var loadoutState = "none";
            if (held.LoadoutConfirmed && _winnerLoadout is { } loadout)
            {
                var effective = _loadouts.Resolve(loadout, held.Loadout, roleProto);
                armor = effective.Armor;
                helmet = effective.Helmet;
                firearm = effective.Firearm;
                loadoutState = "selected";
            }
            else if (roleProto != null)
            {
                loadoutState = "pending";
            }

            // Named characters keep their own look, so the card must not show the player's character for them.
            var identity = IdentityProfile(roleProto);
            var preview = identity ?? PreviewProfile(user, held.Character);
            var character = preview?.Name;
            var vote = held.Vote == null
                ? null
                : _candidates.FirstOrDefault(party => party.ID == held.Vote)?.DisplayName ?? held.Vote;

            var status = held.LoadoutConfirmed
                ? "ready"
                : held.Slot != null
                    ? "choosing"
                    : "waiting";

            var appearance = preview is { } profile
                ? Convert.ToBase64String(SerializeAppearance(profile))
                : null;

            candidates.Add(new SurvivorRosterPreview.Candidate(user, ckey, character, role, armor, helmet,
                firearm, vote, status, appearance, loadoutState, JobOf(roleProto)));
        }

        var roster = SurvivorRosterPreview.Build(candidates, viewer);
        Log.Debug($"Survivor roster for {viewer.UserId}: held={_held.Count}, " +
                  $"listed={roster.Count}");
        return roster;
    }

    /// <summary>True when only a synthetic character may fill this role body.</summary>
    private bool RoleRequiresSynthetic(string protoId)
        => _prototypes.TryIndex<SurvivorRolePrototype>(protoId, out var role) && role.RequiresSynthetic;

    /// <summary>True when this player's selected character is a synthetic.</summary>
    /// <summary>
    ///     Whether a survivor may be given a synthetic-only role: either the character they picked in the
    ///     window is synthetic, or they have a synthetic character to fall back on.
    /// </summary>
    private bool CanTakeSyntheticRole(NetUserId user, int? character)
    {
        var characters = _prefs.GetPreferencesOrNull(user)?.Characters;
        if (SurvivorCharacterSelection.Resolve(character, characters).Synthetic)
            return true;

        return characters != null
               && characters.Values.OfType<HumanoidCharacterProfile>().Any(profile => profile.Synthetic);
    }

    private bool SelectedCharacterIsSynthetic(NetUserId user)
        => IsSynthetic(PreviewProfile(user, _prefs.GetPreferencesOrNull(user)?.SelectedCharacterIndex));

    private static bool IsSynthetic(HumanoidCharacterProfile? profile) => profile is { Synthetic: true };

    /// <summary>The character the player picked, or null while they are still on "random character".</summary>
    /// <summary>
    ///     The job behind a role body, so the roster preview can dress the character in that role's starting gear.
    /// </summary>
    private string? JobOf(string? roleProto)
    {
        if (roleProto == null || !_prototypes.TryIndex<EntityPrototype>(roleProto, out var body))
            return null;

        foreach (var (name, entry) in body.Components)
        {
            if (entry.Component is GhostRoleComponent { JobProto: { } job })
                return job.Id;
        }

        return null;
    }

    /// <summary>
    ///     Serialises a profile the way <see cref="Content.Shared.Preferences.MsgUpdateCharacter"/> does: a nested
    ///     profile inside an EUI state breaks the state's serialisation, a byte blob does not.
    /// </summary>
    private byte[] SerializeAppearance(HumanoidCharacterProfile profile)
    {
        using var stream = new MemoryStream();
        _serializer.Serialize(stream, profile);
        return stream.ToArray();
    }

    /// <summary>
    ///     The fixed appearance of a role whose body keeps its own identity (a named character), built from the body
    ///     prototype's <see cref="RandomHumanoidAppearanceComponent"/> so previews match what spawns.
    /// </summary>
    private HumanoidCharacterProfile? IdentityProfile(string? roleProto)
    {
        if (roleProto == null
            || !_prototypes.TryIndex<SurvivorRolePrototype>(roleProto, out var role)
            || !role.KeepBodyName
            || !_prototypes.TryIndex<EntityPrototype>(roleProto, out var body))
        {
            return null;
        }

        foreach (var (_, entry) in body.Components)
        {
            if (entry.Component is not RandomHumanoidAppearanceComponent appearance)
                continue;

            var baseProfile = HumanoidCharacterProfile.RandomWithSpecies(HumanoidCharacterProfile.DefaultSpecies);
            return _randomAppearance.BuildAppearance(baseProfile, appearance).WithName(body.Name);
        }

        return null;
    }

    private HumanoidCharacterProfile? PreviewProfile(NetUserId user, int? character)
    {
        if (character is not { } index)
            return null;

        var prefs = _prefs.GetPreferencesOrNull(user);
        return prefs != null
               && prefs.Characters.TryGetValue(index, out var profile)
               && profile is HumanoidCharacterProfile humanoid
            ? humanoid
            : null;
    }

    private string? CharacterName(NetUserId user, int? character)
    {
        if (character is not { } index)
            return null;

        var prefs = _prefs.GetPreferencesOrNull(user);
        if (prefs == null || !prefs.Characters.TryGetValue(index, out var profile))
            return null;

        return profile.Name;
    }

    public void Vote(NetUserId user, string partyId)
    {
        if (_phase != SurvivorSetupPhase.Vote
            || !_held.TryGetValue(user, out var held)
            || _candidates.All(party => party.ID != partyId))
        {
            return;
        }

        held.Vote = partyId;
        DirtyAll();
    }

    /// <summary>
    ///     Claims a role slot. The first claim starts the slot's 10 second raffle clock; anyone else who
    ///     claims the same slot joins the draw without extending it. When the clock runs out a random
    ///     claimant wins the slot and the others are free to claim another role.
    /// </summary>
    public void ClaimSlot(NetUserId user, int index)
    {
        if (_phase != SurvivorSetupPhase.Pick
            || !_held.TryGetValue(user, out var held)
            || index < 0
            || index >= _slots.Count)
        {
            return;
        }

        // Winning a raffle locks the survivor into that role. Applies to fresh claims, to re-claims of a
        // different slot and to stale or hand-crafted requests, because the decision is made here.
        var slot = _slots[index];
        var ownedSlot = held.Slot is { } current && current < _slots.Count ? _slots[current].Owner : null;
        if (!SurvivorRoleClaims.CanClaim(user, index, held.Slot, ownedSlot, slot.Owner))
            return;

        // Synthetic roles spawn a synthetic body, so the character the player picked has to be one. Checked
        // against the character chosen in the window (not the account's saved selection, which they may have
        // changed), and enforced here as well as in the picker because a client can send this message by hand.
        if (!SurvivorRoleClaims.CharacterFitsRole(RoleRequiresSynthetic(slot.Proto),
                IsSynthetic(PreviewProfile(user, held.Character))))
        {
            return;
        }

        // A player who has not won a slot yet may switch between raffles: leaving the old one releases the
        // claim so that slot resolves without them.
        if (held.Slot is { } previous && previous != index && previous < _slots.Count)
        {
            var previousSlot = _slots[previous];
            if (previousSlot.Owner == user)
                previousSlot.Owner = null;

            previousSlot.Claimants.Remove(user);
            if (previousSlot.Claimants.Count == 0)
                previousSlot.ClaimEndsAt = null;
        }

        if (!slot.Claimants.Contains(user))
            slot.Claimants.Add(user);

        // The first claim anchors the raffle; later claimants must not extend it.
        slot.ClaimEndsAt = SurvivorRoleRaffle.NextClaimDeadline(
            slot.ClaimEndsAt, _timing.CurTime, ClaimRaffleDuration);
        held.Slot = index;
        DirtyAll();
    }

    public void PickCharacter(NetUserId user, int? slot)
    {
        if (_phase != SurvivorSetupPhase.Pick || !_held.TryGetValue(user, out var held))
            return;

        // Character selection comes after the loadout is confirmed; without a faction loadout it is open.
        if (_winnerLoadout != null && !held.LoadoutConfirmed)
            return;

        if (slot is { } index && _prefs.GetPreferencesOrNull(user)?.Characters.ContainsKey(index) != true)
            return;

        // A synthetic-only role must keep a synthetic character.
        if (TryGetOwnedRole(user, out var ownedRole) && RoleRequiresSynthetic(ownedRole))
        {
            var chosen = PreviewProfile(user, slot);
            if (slot == null || !IsSynthetic(chosen))
                return;
        }

        held.Character = slot;
        held.Eui?.StateDirty();
    }

    /// <summary>
    ///     Changes one loadout category for this player. Only the winning faction's configured options are
    ///     accepted, and a confirmed loadout is final.
    /// </summary>
    public void SelectLoadout(NetUserId user, SurvivorLoadoutCategory category, string protoId)
    {
        if (_phase != SurvivorSetupPhase.Pick
            || _winnerLoadout is not { } loadout
            || !_held.TryGetValue(user, out var held)
            || held.LoadoutConfirmed)
        {
            return;
        }

        if (!TryGetOwnedRole(user, out var roleProto))
            return;

        var options = _loadouts.GetOptions(loadout, roleProto);
        IReadOnlyList<string>? list = category switch
        {
            SurvivorLoadoutCategory.Armor => options.Armor,
            SurvivorLoadoutCategory.Helmet => options.Helmets,
            SurvivorLoadoutCategory.Firearm => options.Firearms,
            _ => null,
        };

        if (list == null)
            return;

        // ResolveOption falls back to a default, so require an exact faction match before accepting.
        var match = SurvivorLoadoutRules.ResolveOption(list, protoId);
        if (match == null || !string.Equals(match, protoId, StringComparison.OrdinalIgnoreCase))
            return;

        held.Loadout = category switch
        {
            SurvivorLoadoutCategory.Armor => held.Loadout with { Armor = match },
            SurvivorLoadoutCategory.Helmet => held.Loadout with { Helmet = match },
            _ => held.Loadout with { Firearm = match },
        };
        DirtyAll();
    }

    /// <summary>Locks this player's loadout after validating it against the faction's options.</summary>
    public void ConfirmLoadout(NetUserId user)
    {
        if (_phase != SurvivorSetupPhase.Pick
            || _winnerLoadout is not { } loadout
            || !_held.TryGetValue(user, out var held)
            || held.LoadoutConfirmed)
        {
            return;
        }

        if (!TryGetOwnedRole(user, out var roleProto))
            return;

        held.Loadout = _loadouts.Resolve(loadout, held.Loadout, roleProto);
        held.LoadoutConfirmed = true;
        DirtyAll();
    }

    /// <summary>
    ///     Switches the round into its Predator configuration for a faction that requires it. Failure is
    ///     loud on purpose: the survivors must never spawn under the pretence of a hunt that never started.
    /// </summary>
    private void ForcePredatorRound(ThirdPartyPrototype faction)
    {
        if (_yautja.TryInitializePredatorRound(out var message))
        {
            _predatorRoundFailed = false;
            Log.Info($"[SurvivorSetup] '{faction.ID}' selected; Predator round active ({message}).");
            return;
        }

        _predatorRoundFailed = true;
        Log.Error($"[SurvivorSetup] '{faction.ID}' was selected but the Predator round could not be "
            + $"initialised: {message}");
    }

    private ThirdPartyPrototype ResolveWinner()
    {
        var ids = _candidates.Select(party => party.ID).ToList();
        var votes = SurvivorFactionVote.CountVotes(ids, _held.Values.Select(held => held.Vote));
        var leaders = SurvivorVoteTally.GetVoteLeaders(ids, votes);

        int Weight(string id) => GetFactionWeight(_candidates.First(party => party.ID == id));

        // Plurality wins; ties (and no votes) fall back to the faction weights.
        var roll = _random.Next(leaders.Sum(Weight));
        var winnerId = SurvivorVoteTally.PickFromLeaders(leaders, Weight, roll)
            ?? _candidates[0].ID;

        return _candidates.First(party => party.ID == winnerId);
    }

    /// <summary>
    ///     The winning faction's role bodies with the composition slot each fills. Bodies the role table
    ///     does not know default to generic leadership in the spawn's leader list and Rifleman elsewhere.
    /// </summary>
    private List<SurvivorTemplateRole> BuildRoleTemplate(PartySpawnPrototype spawn)
        => SurvivorRoleComposition.BuildTemplate(spawn, CategoryOf,
            _winner is { RandomSpecialistRoles: true } ? _random.Next : null);

    /// <summary>
    ///     The winning faction's own job icon for a role category, or nulls when the faction has no icon for
    ///     it (the client then falls back to the generic category icon).
    /// </summary>
    private (string? Sprite, string? State) IconFor(SurvivorRoleCategory? category)
    {
        if (category is not { } value ||
            _winner is null ||
            !_winner.SurvivorIcons.TryGetValue(value, out var icon) ||
            icon is not SpriteSpecifier.Rsi spec)
        {
            return (null, null);
        }

        return (spec.RsiPath.ToString(), spec.RsiState);
    }

    private SurvivorRoleCategory? CategoryOf(string protoId)
        => _prototypes.TryIndex<SurvivorRolePrototype>(protoId, out var role) ? role.Category : null;

    private void StartPick()
    {
        _phase = SurvivorSetupPhase.Pick;
        _phaseEnd = _timing.CurTime + PickDuration;
        _slots.Clear();

        _winnerLoadout = _winner != null && _loadouts.TryGetLoadout(_winner, out var winnerLoadout)
            ? winnerLoadout
            : null;
        if (_winner != null && _winnerLoadout == null)
            Log.Error($"Survivor faction '{_winner.ID}' has no loadout configuration; survivors will spawn with role gear only.");

        // Dutch's Dozen exist to hunt Yautja: selecting them makes this a Predator round. The rule is started
        // through the established Predator path, before any survivor body exists.
        if (SurvivorPredatorRound.ShouldForce(_winner))
            ForcePredatorRound(_winner!);

        if (_winner != null && _prototypes.TryIndex(_winner.PartySpawn, out var spawn))
        {
            // Composition is decided by the winning faction and sized to the round's scheduled squad, so
            // every announced position can be picked even when fewer survivors rolled. Commander, squad
            // leader, medic, technician, weapon specialist and liaison come first, then riflemen; a group
            // larger than the faction's template still gets riflemen generated from its rifleman body.
            var template = BuildRoleTemplate(spawn);
            var slotCount = SurvivorSlotAssignment.FactionSlotCount(_auRound.ScheduledSurvivorCount, _held.Count);
            var roles = SurvivorRoleComposition.Build(template, slotCount);
            if (roles.Count < slotCount && !SurvivorRoleComposition.HasRiflemanBody(template))
            {
                Log.Error($"Survivor faction '{_winner.ID}' has no rifleman role body configured; "
                    + $"{slotCount - roles.Count} survivor slot(s) cannot be filled. Treat this as a configuration problem.");
            }

            foreach (var role in roles)
            {
                _slots.Add(new RoleSlot
                {
                    Proto = role.Proto,
                    Leader = role.Category is SurvivorRoleCategory.Commander
                        or SurvivorRoleCategory.SquadLeader
                        or SurvivorRoleCategory.Leader,
                });
            }
        }

        // Own character is the default from the moment a player is held; random is still offered in the
        // character list, and stays the fallback when the player has no selectable saved character.
        foreach (var (user, held) in _held)
        {
            held.Slot = null;
            held.Character ??= _prefs.GetPreferencesOrNull(user)?.SelectedCharacterIndex;
            held.Loadout = _winnerLoadout != null ? _loadouts.DefaultSelection(_winnerLoadout) : default;
            held.LoadoutConfirmed = false;
        }
    }

    private void Finish()
    {
        _phase = null;
        var winner = _winner;

        // Decide any raffle still running so a late claim is resolved before the group spawns.
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Owner == null && slot.Claimants.Count > 0)
                ResolveClaim(i, slot);
        }

        // Players who never claimed (or lost a raffle) still get a role: one of the positions nobody took,
        // picked at random. A synthetic slot is only offered to someone who actually has a synthetic character
        // to put in it.
        var unplaced = SurvivorSlotAssignment.UnplacedSurvivors(
            _held.Select(pair => (pair.Key, pair.Value.Slot)));
        _random.Shuffle(unplaced);
        var taken = new List<bool>(_slots.Count);
        for (var i = 0; i < _slots.Count; i++)
            taken.Add(_slots[i].Owner != null);

        // One pass over the positions nobody took: each survivor is given a slot the moment it is picked, so two
        // players can never be handed the same role.
        var assignments = SurvivorSlotAssignment.AssignUnplaced(
            unplaced,
            taken,
            (user, index) =>
            {
                if (!RoleRequiresSynthetic(_slots[index].Proto))
                    return true;

                return _held.TryGetValue(user, out var candidate)
                       && CanTakeSyntheticRole(user, candidate.Character);
            },
            _random.Next);

        foreach (var (user, index) in assignments)
        {
            _slots[index].Owner = user;
            if (_held.TryGetValue(user, out var held))
                held.Slot = index;
        }

        foreach (var user in unplaced)
        {
            if (!assignments.ContainsKey(user))
                Log.Warning($"Survivor setup: {user.UserId} was left without a survivor role - no free slot remained.");
        }

        var chosen = new List<ThirdPartySystem.SurvivorAssignment>();
        foreach (var (user, held) in _held)
        {
            held.Eui?.Close();
            held.Eui = null;

            if (!_player.TryGetSessionById(user, out _))
                continue;

            if (held.Slot is not { } index)
                continue;

            var slot = _slots[index];
            // A player who never confirmed a loadout gets a fully random one - armor, helmet and firearm rolled
            // from their role's own options - so an AFK survivor still spawns dressed and armed.
            var loadout = _winnerLoadout != null
                ? held.LoadoutConfirmed
                    ? _loadouts.Resolve(_winnerLoadout, held.Loadout, slot.Proto)
                    : _loadouts.RandomSelection(_winnerLoadout, slot.Proto, _random.Next)
                : default;
            // Categories the faction randomises are rolled here, at spawn, so the roll is what the survivor
            // actually wears (the picker still shows the source's explicit choices).
            if (_winnerLoadout != null)
                loadout = _loadouts.Randomize(_winnerLoadout, loadout, slot.Proto, _random.Next);
            var profile = ResolveProfile(user, held.Character, RoleRequiresSynthetic(slot.Proto));
            chosen.Add(new ThirdPartySystem.SurvivorAssignment(user, slot.Proto, slot.Leader,
                profile, loadout, _winnerLoadout));
        }

        var players = _held.Keys.ToList();
        _held.Clear();

        if (winner != null && chosen.Count > 0)
        {
            try
            {
                _thirdParty.SpawnChosenSurvivors(winner, chosen);
            }
            catch (Exception ex)
            {
                Log.Error($"Survivor spawn for {winner.ID} threw: {ex}");
            }
        }

        foreach (var user in players)
        {
            if (!_player.TryGetSessionById(user, out var session) || session.AttachedEntity != null)
                continue;

            _chat.DispatchServerMessage(session, Loc.GetString("cmu-survivor-no-body-return-to-lobby"));
        }
    }

    /// <summary>
    ///     The profile the survivor spawns with. A synthetic-only role is forced onto a synthetic character:
    ///     the claimed one when it qualifies, otherwise the player's first synthetic character, so a human can
    ///     never end up in a synthetic body.
    /// </summary>
    private HumanoidCharacterProfile ResolveProfile(NetUserId user, int? character, bool requireSynthetic = false)
    {
        var characters = _prefs.GetPreferencesOrNull(user)?.Characters;
        var profile = SurvivorCharacterSelection.Resolve(character, characters);
        if (!requireSynthetic || profile.Synthetic)
            return profile;

        if (characters != null)
        {
            foreach (var (_, candidate) in characters)
            {
                if (candidate is HumanoidCharacterProfile { Synthetic: true } synthetic)
                    return synthetic;
            }
        }

        return profile;
    }

    /// <summary>Drops players who left during the vote and frees the slot they held.</summary>
    private bool PruneDisconnected()
    {
        if (_held.Count == 0)
            return false;

        var gone = SurvivorRoster.FindDisconnected(_held.Keys.ToList(),
            user => _player.TryGetSessionById(user, out _));

        if (gone.Count == 0)
            return false;

        foreach (var user in gone)
        {
            var held = _held[user];
            held.Eui?.Close();
            held.Eui = null;

            if (held.Slot is { } index && index < _slots.Count)
            {
                var slot = _slots[index];
                if (slot.Owner == user)
                    slot.Owner = null;

                slot.Claimants.Remove(user);
                if (slot.Claimants.Count == 0)
                    slot.ClaimEndsAt = null;
            }

            _held.Remove(user);
        }

        return true;
    }


    /// <summary>The role prototype this player won in the raffle, or null while they own no role.</summary>
    private bool TryGetOwnedRole(NetUserId user, out string roleProto)
    {
        roleProto = string.Empty;
        if (!_held.TryGetValue(user, out var held) || held.Slot is not { } index || index >= _slots.Count)
            return false;

        var slot = _slots[index];
        if (slot.Owner != user)
            return false;

        roleProto = slot.Proto;
        return true;
    }

    private void DirtyAll()
    {
        foreach (var held in _held.Values)
        {
            held.Eui?.StateDirty();
        }
    }

    private sealed class HeldSurvivor
    {
        public bool Leader;
        public string? Vote;
        public int? Slot;
        public int? Character;
        public SurvivorLoadoutSelection Loadout;
        public bool LoadoutConfirmed;
        public SurvivorSetupEui? Eui;
    }

    private sealed class RoleSlot
    {
        public string Proto = string.Empty;
        public bool Leader;

        /// <summary>Winner of the slot, set when the raffle resolves (or immediately if uncontested).</summary>
        public NetUserId? Owner;

        /// <summary>Players currently in the raffle for this slot.</summary>
        public readonly List<NetUserId> Claimants = new();

        /// <summary>When the raffle resolves; anchored by the first claim and never extended.</summary>
        public TimeSpan? ClaimEndsAt;
    }
}
