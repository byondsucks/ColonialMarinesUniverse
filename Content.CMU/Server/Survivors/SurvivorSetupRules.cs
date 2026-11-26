using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.CMU14.Survivors;
using Content.Shared.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Threats;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server.CMU14.Survivors.Setup;

/// <summary>
///     Vote tallying for the survivor group faction vote. Pure so the vote resolution can be tested
///     without a live round, and so faction eligibility/balance rules can be layered on later.
/// </summary>
public static class SurvivorVoteTally
{
    /// <summary>
    ///     The factions with the most votes. With no votes at all every candidate leads, which leaves the
    ///     choice to the faction weights.
    /// </summary>
    public static List<string> GetVoteLeaders(IReadOnlyList<string> candidateIds, IReadOnlyDictionary<string, int> votes)
    {
        if (candidateIds.Count == 0)
            return new List<string>();

        var best = candidateIds.Max(id => votes.GetValueOrDefault(id));
        return candidateIds.Where(id => votes.GetValueOrDefault(id) == best).ToList();
    }

    /// <summary>
    ///     Picks one of the leaders by weight. A single leader always wins, so a clear plurality ignores
    ///     weights; ties are broken proportionally. <paramref name="roll"/> is in [0, total weight).
    /// </summary>
    public static string? PickFromLeaders(IReadOnlyList<string> leaders, Func<string, int> getWeight, int roll)
    {
        if (leaders.Count == 0)
            return null;

        if (leaders.Count == 1)
            return leaders[0];

        var total = leaders.Sum(id => Math.Max(0, getWeight(id)));
        if (total <= 0)
            return leaders[0];

        roll %= total;
        foreach (var id in leaders)
        {
            roll -= Math.Max(0, getWeight(id));
            if (roll < 0)
                return id;
        }

        return leaders[^1];
    }
}

/// <summary>
///     Assembles the survivor group roster that every held player sees before anyone is spawned.
/// </summary>
public static class SurvivorRoster
{
    /// <summary>One row of the roster: who, which role they claimed, the character they spawn as, and their vote.</summary>
    public readonly record struct Entry(NetUserId User, string Name, string? Role, string? Character, string? Vote);

    /// <summary>
    ///     One entry per held player, ordered by name so every client sees the same list. A player can only
    ///     appear once even if the caller passes them twice.
    /// </summary>
    public static List<Entry> Build(IEnumerable<Entry> entries)
    {
        var seen = new HashSet<NetUserId>();
        var roster = new List<Entry>();
        foreach (var entry in entries)
        {
            if (seen.Add(entry.User))
                roster.Add(entry);
        }

        roster.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return roster;
    }

    /// <summary>Held players that no longer have a session, so the vote can drop them.</summary>
    public static List<NetUserId> FindDisconnected(IEnumerable<NetUserId> held, Func<NetUserId, bool> isConnected)
        => held.Where(user => !isConnected(user)).ToList();
}

/// <summary>
///     Which character a survivor spawns as.
/// </summary>
public static class SurvivorCharacterSelection
{
    /// <summary>
    ///     The player's own saved character when they picked one, otherwise a random humanoid. The saved
    ///     profile is returned as-is: selection only ever reads it, so name and appearance are preserved
    ///     and the stored profile is never overwritten.
    /// </summary>
    /// <param name="slot">Saved character slot the player picked, or null for a random character.</param>
    /// <param name="characters">The player's saved characters, or null if their preferences are unavailable.</param>
    /// <param name="randomFallback">Overrides the random character generator; tests pass a deterministic one.</param>
    public static HumanoidCharacterProfile Resolve(int? slot,
        IReadOnlyDictionary<int, HumanoidCharacterProfile>? characters,
        Func<HumanoidCharacterProfile>? randomFallback = null)
    {
        if (slot is { } index
            && characters != null
            && characters.TryGetValue(index, out var profile))
        {
            return profile;
        }

        return (randomFallback ?? RandomCharacter)();
    }

    /// <summary>A freshly rolled character of the default species.</summary>
    public static HumanoidCharacterProfile RandomCharacter()
        => HumanoidCharacterProfile.RandomWithSpecies(HumanoidCharacterProfile.DefaultSpecies);
}

/// <summary>
///     CMU14 TEMPORARY (survivor rework testing): parses <see cref="Content.Shared.CCVar.CCVars.SurvivorTestFactions"/>,
///     the comma-separated faction list that replaces normal survivor eligibility while testing. An empty
///     value means the override is off and normal eligibility applies.
/// </summary>
public static class SurvivorTestFactionPool
{
    /// <summary>The faction ids listed in the override, in order, without blanks or duplicates.</summary>
    public static List<string> ParseIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new List<string>();

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(entry))
                ids.Add(entry);
        }

        return ids;
    }
}

/// <summary>
///     Draws the two factions the survivor group votes between. The draw is weighted but distinct, and a
///     pool smaller than the option count simply yields fewer options.
/// </summary>
public static class SurvivorFactionDraw
{    /// <summary>How many factions the group chooses between.</summary>
    public const int OptionCount = 2;

    /// <summary>
    ///     Every distinct faction id in the pool, ordered so a ballot built from it is stable. Used by the
    ///     temporary testing mode that offers all eligible factions instead of drawing two.
    /// </summary>
    public static List<string> AllOptions(IReadOnlyList<string> pool)
        => pool.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    ///     Picks up to <paramref name="count"/> distinct faction ids from the pool, without replacement.
    ///     <paramref name="roll"/> returns a value in [0, max) and is injected so the draw is deterministic
    ///     in tests.
    /// </summary>
    public static List<string> PickDistinct(IReadOnlyList<string> pool, Func<string, int> getWeight,
        Func<int, int> roll, int count = OptionCount)
    {
        var remaining = pool.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var picked = new List<string>(Math.Min(count, remaining.Count));

        while (picked.Count < count && remaining.Count > 0)
        {
            var total = remaining.Sum(id => Math.Max(0, getWeight(id)));
            if (total <= 0)
                break;

            var value = roll(total) % total;
            var index = 0;
            for (; index < remaining.Count; index++)
            {
                value -= Math.Max(0, getWeight(remaining[index]));
                if (value < 0)
                    break;
            }

            if (index >= remaining.Count)
                index = remaining.Count - 1;

            picked.Add(remaining[index]);
            remaining.RemoveAt(index);
        }

        return picked;
    }
}

/// <summary>
///     One role body a faction's party spawn can provide, with the composition slot it fills.
/// </summary>
public readonly record struct SurvivorTemplateRole(string Proto, int Count, SurvivorRoleCategory Category);

/// <summary>
///     Caps the eligible survivor rollers to the scheduled survivor count, so exactly the players who vote
///     are the players who spawn. The sample is drawn once and reused for holding, voting and spawning.
/// </summary>
public static class SurvivorRollCap
{
    /// <summary>
    ///     Returns the rollers that participate in the survivor group. A non-positive or missing scheduled
    ///     count means the round scheduled no survivors, so nobody participates. When there are already few
    ///     enough rollers, all of them participate.
    ///     <paramref name="roll"/> returns a value in [0, max) and is injected so the sample is deterministic
    ///     in tests.
    /// </summary>
    public static Dictionary<NetUserId, ProtoId<JobPrototype>> Apply(
        IReadOnlyDictionary<NetUserId, ProtoId<JobPrototype>> rollers,
        int scheduled,
        Func<int, int> roll)
    {
        if (scheduled <= 0 || rollers.Count == 0)
            return new Dictionary<NetUserId, ProtoId<JobPrototype>>();

        if (rollers.Count <= scheduled)
            return new Dictionary<NetUserId, ProtoId<JobPrototype>>(rollers);

        // Deterministic starting order, then a partial Fisher-Yates shuffle for the sample.
        var keys = rollers.Keys.OrderBy(id => id.UserId).ToList();
        for (var i = 0; i < scheduled; i++)
        {
            var remaining = keys.Count - i;
            if (remaining <= 1)
                break;

            var offset = Math.Clamp(roll(remaining), 0, remaining - 1);
            var swap = i + offset;
            (keys[i], keys[swap]) = (keys[swap], keys[i]);
        }

        var selected = new Dictionary<NetUserId, ProtoId<JobPrototype>>(scheduled);
        for (var i = 0; i < scheduled; i++)
            selected[keys[i]] = rollers[keys[i]];

        return selected;
    }
}

/// <summary>
///     Turns a winning faction's party spawn into the group's role slots. The faction decides which roles
///     exist (through its spawn template and role categories); the group size decides how many are offered.
/// </summary>
public static class SurvivorRoleComposition
{
    /// <summary>
    ///     Composition priority: commander, medic, technician, squad leader, weapon specialist, liaison, one
    ///     rifleman, then the synthetic support slot. Everyone left over becomes a rifleman. Generic
    ///     <see cref="SurvivorRoleCategory.Leader"/> bodies fill the commander position first and the squad
    ///     leader position second.
    /// </summary>
    public static readonly SurvivorRoleCategory[] NamedRoles =
    {
        SurvivorRoleCategory.Commander,
        SurvivorRoleCategory.Medic,
        SurvivorRoleCategory.Technician,
        SurvivorRoleCategory.SquadLeader,
        SurvivorRoleCategory.WeaponSpecialist,
        SurvivorRoleCategory.Liaison,
        SurvivorRoleCategory.Rifleman,
        // The synthetic support slot is the last named position: a body that fights comes before the synth.
        SurvivorRoleCategory.Support,
    };

    /// <summary>
    ///     Reads a faction's party spawn into template roles. A role the category lookup does not know
    ///     defaults to generic leadership in the leader list and Rifleman elsewhere, so a faction needs only
    ///     list its role bodies to get a composition.
    /// </summary>
    public static List<SurvivorTemplateRole> BuildTemplate(PartySpawnPrototype spawn,
        Func<string, SurvivorRoleCategory?> categorize, Func<int, int>? roll = null)
    {
        var template = new List<SurvivorTemplateRole>(spawn.LeadersToSpawn.Count + spawn.GruntsToSpawn.Count);
        foreach (var (proto, count) in spawn.LeadersToSpawn)
            template.Add(new SurvivorTemplateRole(proto, count, categorize(proto) ?? SurvivorRoleCategory.Leader));

        foreach (var (proto, count) in spawn.GruntsToSpawn)
            template.Add(new SurvivorTemplateRole(proto, count, categorize(proto) ?? SurvivorRoleCategory.Rifleman));

        if (roll != null)
            ShuffleSameCategory(template, roll);

        return template;
    }

    /// <summary>
    ///     Factions that field several roles in one category (Dutch's Dozen has a grenadier, a minigunner and
    ///     a flamer, all weapon specialists) get one of them picked at random instead of always the first.
    /// </summary>
    public static void ShuffleSameCategory(List<SurvivorTemplateRole> template, Func<int, int> roll)
    {
        var categories = template.Select(role => role.Category).Distinct().ToList();
        foreach (var category in categories)
        {
            var indices = new List<int>();
            for (var i = 0; i < template.Count; i++)
            {
                if (template[i].Category == category)
                    indices.Add(i);
            }

            if (indices.Count < 2)
                continue;

            var picked = indices.Select(index => template[index]).ToList();
            for (var i = picked.Count - 1; i > 0; i--)
            {
                var j = Math.Abs(roll(i + 1)) % (i + 1);
                (picked[i], picked[j]) = (picked[j], picked[i]);
            }

            for (var i = 0; i < indices.Count; i++)
                template[indices[i]] = picked[i];
        }
    }

    /// <summary>True when the faction template has a body that can be used for riflemen.</summary>
    public static bool HasRiflemanBody(IReadOnlyList<SurvivorTemplateRole> template)
        => template.Any(role => role.Count > 0 && role.Category == SurvivorRoleCategory.Rifleman);

    /// <summary>
    ///     How many role slots the round offers. The scheduled survivor count defines the squad the round
    ///     announced, so testers can see and pick every position even when fewer players rolled. It never
    ///     drops below the number of held survivors.
    /// </summary>
    public static int GetSlotCount(int scheduledSurvivors, int heldSurvivors)
        => Math.Max(Math.Max(0, scheduledSurvivors), heldSurvivors);

    /// <summary>
    ///     Builds one slot per selected survivor. Special positions are filled in <see cref="NamedRoles"/>
    ///     priority order, at most once each and never reusing the same role body, then every remaining
    ///     survivor becomes a rifleman. Rifleman slots respect the template's bodies first and then repeat
    ///     the faction's primary rifleman body for as many players as are left, so a group larger than the
    ///     original template still gets a body for everyone. The synthetic support slot is always added last,
    ///     even past the group size, so a faction's synthetic always spawns if someone can claim it.
    /// </summary>
    public static List<SurvivorTemplateRole> Build(IReadOnlyList<SurvivorTemplateRole> template, int groupSize)
    {
        var slots = new List<SurvivorTemplateRole>();
        if (groupSize <= 0)
            return slots;

        var remaining = template.Where(role => role.Count > 0).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var category in NamedRoles)
        {
            // The synthetic is appended after the group is filled, so it is never cut off by the group size.
            if (category == SurvivorRoleCategory.Support)
                continue;

            if (slots.Count >= groupSize)
                break;

            var index = TakePosition(category);
            if (index < 0 && category is SurvivorRoleCategory.Commander or SurvivorRoleCategory.SquadLeader)
                index = TakePosition(SurvivorRoleCategory.Leader);

            if (index < 0)
                continue;

            used.Add(remaining[index].Proto);
            slots.Add(remaining[index] with { Count = 1 });
            remaining[index] = remaining[index] with { Count = remaining[index].Count - 1 };
        }

        // A faction can field several roles in one category - FORECON fields a smartgunner, a marksman and a sniper,
        // all weapon specialists - and each is a distinct position. Offer them all, in the shuffled order, rather
        // than only the first; riflemen absorb whatever the group has left.
        foreach (var role in remaining.ToList())
        {
            if (slots.Count >= groupSize)
                break;

            if (role.Count <= 0
                || used.Contains(role.Proto)
                || role.Category is SurvivorRoleCategory.Rifleman
                    or SurvivorRoleCategory.Support
                    or SurvivorRoleCategory.Leader)
            {
                continue;
            }

            var extra = remaining.FindIndex(candidate =>
                string.Equals(candidate.Proto, role.Proto, StringComparison.OrdinalIgnoreCase));
            if (extra < 0)
                continue;

            used.Add(remaining[extra].Proto);
            slots.Add(remaining[extra] with { Count = 1 });
            remaining[extra] = remaining[extra] with { Count = remaining[extra].Count - 1 };
        }

        FillRiflemen(remaining, slots, groupSize);

        // A faction's synthetic always spawns: the support slot is offered even when the group is already full.
        // Only a player whose character is synthetic can claim it - see SurvivorRolePrototype.RequiresSynthetic,
        // which the claim, the character picker and the spawn path all enforce.
        var support = TakePosition(SurvivorRoleCategory.Support);
        if (support >= 0)
            slots.Add(remaining[support] with { Count = 1 });

        return slots;

        int TakePosition(SurvivorRoleCategory category)
            => remaining.FindIndex(role =>
                role.Count > 0 && role.Category == category && !used.Contains(role.Proto));
    }

    private static void FillRiflemen(List<SurvivorTemplateRole> remaining, List<SurvivorTemplateRole> slots,
        int groupSize)
    {
        var riflemen = remaining.Where(role => role.Category == SurvivorRoleCategory.Rifleman).ToList();
        if (riflemen.Count == 0)
            return;

        // Respect the faction's configured rifleman bodies first, then reuse the primary one for overflow.
        foreach (var role in riflemen)
        {
            for (var i = 0; i < role.Count && slots.Count < groupSize; i++)
                slots.Add(role with { Count = 1 });
        }

        var primary = riflemen[0];
        while (slots.Count < groupSize)
            slots.Add(primary with { Count = 1 });
    }
}

/// <summary>
///     Contested role claims. The first claim on a slot starts a fixed raffle clock; players who claim the
///     same slot afterwards join the draw without extending it, and a random claimant wins when it expires.
/// </summary>
public static class SurvivorRoleRaffle
{
    /// <summary>
    ///     The deadline for a claim. The first claim starts the clock; later claims keep the original
    ///     deadline so a contested slot always resolves after one raffle window.
    /// </summary>
    public static TimeSpan NextClaimDeadline(TimeSpan? currentDeadline, TimeSpan now, TimeSpan raffleDuration)
        => currentDeadline ?? now + raffleDuration;

    /// <summary>Picks the raffle winner from the claimants. <paramref name="roll"/> is in [0, count).</summary>
    public static NetUserId? PickWinner(IReadOnlyList<NetUserId> claimants, Func<int, int> roll)
    {
        if (claimants.Count == 0)
            return null;

        var index = Math.Clamp(roll(claimants.Count), 0, claimants.Count - 1);
        return claimants[index];
    }
}

/// <summary>Options a faction's loadout offers, as plain ids for validation and tests.</summary>
public readonly record struct SurvivorLoadoutOptions(
    IReadOnlyList<string> Armor,
    IReadOnlyList<string> Helmets,
    IReadOnlyList<string> Firearms);

/// <summary>Durations shared by the survivor setup phases, and by the tests that pin them.</summary>
public static class SurvivorSetupTimings
{
    /// <summary>The preparation phase: claiming roles, resolving raffles and confirming a loadout.</summary>
    public static readonly TimeSpan Preparation = TimeSpan.FromSeconds(30);

    /// <summary>How long a contested role claim stays open before the winner is drawn.</summary>
    public static readonly TimeSpan Raffle = TimeSpan.FromSeconds(5);
}

/// <summary>
///     Server-side validation for faction loadouts. A selection is only accepted when it is one of the
///     winning faction's configured options; anything missing or foreign falls back to that faction's
///     default, so a client can never smuggle in another faction's gear.
/// </summary>
public static class SurvivorLoadoutRules
{
    /// <summary>The requested options when the faction offers them, otherwise its configured defaults.</summary>
    public static SurvivorLoadoutSelection Resolve(SurvivorLoadoutOptions options, SurvivorLoadoutSelection requested)
        => new(
            ResolveOption(options.Armor, requested.Armor) ?? First(options.Armor),
            ResolveOption(options.Helmets, requested.Helmet) ?? First(options.Helmets),
            ResolveOption(options.Firearms, requested.Firearm) ?? First(options.Firearms));

    /// <summary>The offered option matching this id, or null when it is not on offer.</summary>
    public static string? ResolveOption(IReadOnlyList<string> options, string? protoId)
    {
        if (string.IsNullOrEmpty(protoId))
            return null;

        foreach (var option in options)
        {
            if (string.Equals(option, protoId, StringComparison.OrdinalIgnoreCase))
                return option;
        }

        return null;
    }

    /// <summary>True when a category offers a choice at all, i.e. rolling for it means something.</summary>
    public static bool IsRandomizable(IReadOnlyList<string> options) => options.Count > 1;

    /// <summary>A rolled option, clamped to the list, or null when the role offers nothing here.</summary>
    public static string? PickOption(IReadOnlyList<string> options, Func<int, int> roll)
    {
        if (options.Count == 0)
            return null;

        var index = Math.Clamp(roll(options.Count), 0, options.Count - 1);
        return options[index];
    }

    /// <summary>
    ///     A rolled option, or null for a category the role locks to one choice: a lock survives the roll.
    /// </summary>
    public static string? PickIfOpen(IReadOnlyList<string> options, Func<int, int> roll)
        => IsRandomizable(options) ? PickOption(options, roll) : null;

    /// <summary>
    ///     Whether this role may be offered the firearm. Unrestricted firearms are always allowed; a restricted
    ///     one needs a role that names it, so an unclassified (null) role only ever sees unrestricted firearms.
    /// </summary>
    public static bool CanUseFirearm(string firearm,
        IReadOnlyDictionary<string, List<SurvivorRoleCategory>> restrictions, SurvivorRoleCategory? category)
    {
        if (!restrictions.TryGetValue(firearm, out var allowed) || allowed.Count == 0)
            return true;

        return category is { } role && allowed.Contains(role);
    }

    /// <summary>The faction's firearms this role may be offered, in configured order.</summary>
    public static List<string> FilterFirearms(IReadOnlyList<string> firearms,
        IReadOnlyDictionary<string, List<SurvivorRoleCategory>> restrictions, SurvivorRoleCategory? category)
        => firearms.Where(firearm => CanUseFirearm(firearm, restrictions, category)).ToList();

    private static string? First(IReadOnlyList<string> options) => options.Count == 0 ? null : options[0];
}

/// <summary>Faction vote counting: one vote per survivor for the group's faction.</summary>
public static class SurvivorFactionVote
{
    /// <summary>Votes per candidate.</summary>
    public static Dictionary<string, int> CountVotes(IReadOnlyList<string> candidateIds,
        IEnumerable<string?> held)
    {
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in candidateIds)
            votes[id] = 0;

        foreach (var vote in held)
        {
            if (vote == null)
                continue;

            if (votes.TryGetValue(vote, out var count))
                votes[vote] = count + 1;
        }

        return votes;
    }
}

/// <summary>Slot bookkeeping for the end of the pick phase.</summary>
public static class SurvivorSlotAssignment
{
    /// <summary>Survivors still without a role slot when the pick phase ends.</summary>
    public static List<NetUserId> UnplacedSurvivors(IEnumerable<(NetUserId User, int? Slot)> held)
        => held.Where(entry => entry.Slot == null)
            .Select(entry => entry.User)
            .ToList();

    /// <summary>
    ///     A free slot picked at random from the ones nobody claimed, so a survivor who never picked a role
    ///     still gets one of the roles that are left instead of a fixed position. Null when the group has no
    ///     free slot left.
    /// </summary>
    public static int? RandomFreeSlot(IReadOnlyList<int> freeSlots, Func<int, int> roll)
    {
        if (freeSlots.Count == 0)
            return null;

        // Rolls come from the engine's RNG and wrap around the int range, so mask rather than Math.Abs (which
        // throws on int.MinValue).
        var picked = (int) ((uint) roll(freeSlots.Count) % (uint) freeSlots.Count);
        return freeSlots[picked];
    }

    /// <summary>
    ///     Hands every unplaced survivor a distinct free slot, in the order given (the caller shuffles), so a
    ///     group that never picked, or lost a raffle, still fills the remaining positions - and two players can
    ///     never be given the same role. <paramref name="mayTake"/> carries the per-role rules (a synthetic-only
    ///     slot needs a synthetic character); a survivor with no acceptable slot left is simply left out.
    /// </summary>
    public static Dictionary<NetUserId, int> AssignUnplaced(
        IReadOnlyList<NetUserId> unplaced,
        IReadOnlyList<bool> slotTaken,
        Func<NetUserId, int, bool> mayTake,
        Func<int, int> roll)
    {
        var taken = new List<bool>(slotTaken);
        var assigned = new Dictionary<NetUserId, int>();

        foreach (var user in unplaced)
        {
            var free = new List<int>(taken.Count);
            for (var i = 0; i < taken.Count; i++)
            {
                if (!taken[i] && mayTake(user, i))
                    free.Add(i);
            }

            if (RandomFreeSlot(free, roll) is not { } index)
                continue;

            // Occupied immediately: the next survivor sees exactly the slots still free.
            taken[index] = true;
            assigned[user] = index;
        }

        return assigned;
    }

    /// <summary>Role slots the faction composition offers, sized to the faction population alone.</summary>
    public static int FactionSlotCount(int scheduledSurvivors, int factionHeld)
        => SurvivorRoleComposition.GetSlotCount(scheduledSurvivors, factionHeld);

}

/// <summary>
///     Builds the teammate roster shown in the survivor faction menu. Only the candidates handed in are
///     listed, so a caller can only expose teammates it is authorized to show, and the result is keyed by
///     session identity rather than by name.
/// </summary>
public static class SurvivorRosterPreview
{
    /// <summary>Raw teammate data gathered by the server for one held survivor.</summary>
    public readonly record struct Candidate(
        NetUserId User,
        string Ckey,
        string? Character,
        string? Role,
        string? Armor,
        string? Helmet,
        string? Firearm,
        string? Vote,
        string Status,
        string? Appearance,
        string LoadoutState = "none",
        string? RoleProto = null);

    /// <summary>
    ///     One line per teammate, ordered by account then character name, with the local player marked. Plain
    ///     text on purpose: the group panel must arrive, and the richer payload it used to carry did not.
    /// </summary>
    public static List<string> Build(IEnumerable<Candidate> candidates, NetUserId viewer)
    {
        var seen = new HashSet<NetUserId>();
        var members = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate.User))
                members.Add(candidate);
        }

        members.Sort((a, b) =>
        {
            var byCkey = string.Compare(a.Ckey, b.Ckey, StringComparison.OrdinalIgnoreCase);
            return byCkey != 0
                ? byCkey
                : string.Compare(a.Character ?? string.Empty, b.Character ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        });

        // One pipe-encoded string per teammate: split by the client, which keeps the EUI state to plain strings
        // while still carrying the character, role, selected loadout, status and appearance.
        return members
            .Select(member => string.Join('|',
                member.Ckey ?? string.Empty,
                member.Character ?? string.Empty,
                member.Role ?? string.Empty,
                member.Armor ?? string.Empty,
                member.Helmet ?? string.Empty,
                member.Firearm ?? string.Empty,
                member.Status ?? string.Empty,
                member.Appearance ?? string.Empty,
                member.LoadoutState ?? "none",
                member.RoleProto ?? string.Empty))
            .ToList();
    }

    /// <summary>
    ///     "ckey - role (character) - voted Faction", omitting whatever does not apply yet, so a teammate who has
    ///     picked nothing still shows up instead of rendering a row of dashes.
    /// </summary>
    public static string Format(Candidate candidate, bool isSelf)
    {
        var line = string.IsNullOrEmpty(candidate.Ckey) ? "(connecting)" : candidate.Ckey;

        if (!string.IsNullOrEmpty(candidate.Role))
        {
            line += string.IsNullOrEmpty(candidate.Character)
                ? $" - {candidate.Role}"
                : $" - {candidate.Role} ({candidate.Character})";
        }
        else if (!string.IsNullOrEmpty(candidate.Character))
        {
            line += $" - {candidate.Character}";
        }

        if (!string.IsNullOrEmpty(candidate.Vote))
            line += $" - voted {candidate.Vote}";

        if (isSelf)
            line += " (you)";

        return line;
    }
}

/// <summary>
///     Role ownership rule: a survivor may hold exactly one role at a time.
/// </summary>
/// <summary>
///     Claim rules for the pick phase. Winning a raffle locks the survivor into that role; joining a raffle
///     does not, so a player still waiting on a draw may change their mind.
/// </summary>
public static class SurvivorRoleClaims
{
    /// <summary>True when this player already won the role in <paramref name="currentSlot"/>.</summary>
    public static bool IsLocked(NetUserId user, int? currentSlot, NetUserId? ownerOfCurrentSlot)
        => currentSlot != null && ownerOfCurrentSlot == user;

    /// <summary>
    ///     Whether the player's chosen character may fill this role. Synthetic-only roles (the survivor
    ///     synthetic slots) need a synthetic character, so a human character can never take one.
    /// </summary>
    public static bool CharacterFitsRole(bool roleRequiresSynthetic, bool characterIsSynthetic)
        => !roleRequiresSynthetic || characterIsSynthetic;

    /// <summary>
    ///     Whether a claim request may proceed: never once this player has won a role, and never for a slot
    ///     somebody else already won, regardless of what the client sends.
    /// </summary>
    public static bool CanClaim(NetUserId user, int requestedSlot, int? currentSlot,
        NetUserId? ownerOfCurrentSlot, NetUserId? ownerOfRequestedSlot)
    {
        if (IsLocked(user, currentSlot, ownerOfCurrentSlot))
            return false;

        return ownerOfRequestedSlot == null;
    }
}

public static class SurvivorRoleOwnership
{
    /// <summary>
    ///     Claimants allowed to win <paramref name="slot"/>. Anyone who already owns a different role is
    ///     skipped, so a player can never end up holding two roles at once.
    /// </summary>
    public static List<NetUserId> Eligible(
        IReadOnlyList<NetUserId> claimants,
        int slot,
        IReadOnlyDictionary<NetUserId, int> ownedSlots)
    {
        var eligible = new List<NetUserId>(claimants.Count);
        foreach (var claimant in claimants)
        {
            if (ownedSlots.TryGetValue(claimant, out var owned) && owned != slot)
                continue;

            eligible.Add(claimant);
        }

        return eligible;
    }
}

/// <summary>
///     Round-mode rule for survivor factions. Dutch's Dozen only exist to hunt Yautja, so selecting them
///     makes the round a Predator round; no other survivor faction changes the round mode.
/// </summary>
public static class SurvivorPredatorRound
{
    /// <summary>True when winning this faction must force the round into its Predator configuration.</summary>
    public static bool ShouldForce(ThirdPartyPrototype? winner) => winner?.ForcesPredatorRound == true;
}

/// <summary>
///     Which carrying gear a survivor's picked firearm needs. A shotgun takes loose shells, so a belt or pouch
///     issued for magazines has to be re-issued for shells; anything that is not a magazine carrier (medic bag,
///     toolbelt, fuel pouch) is left alone.
/// </summary>
public static class SurvivorAmmoRig
{
    /// <summary>The slots a shell pick may re-issue.</summary>
    public static readonly string[] MagazineSlots = { "belt", "pocket1", "pocket2" };

    /// <summary>Whether the picked firearm's ammunition comes loose rather than in magazines.</summary>
    public static bool LoadsLooseAmmo(string? ammoId) =>
        !string.IsNullOrEmpty(ammoId)
        && !ammoId.Contains("Magazine", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a worn item is a magazine belt or magazine pouch.</summary>
    public static bool IsMagazineRig(string? rigId) =>
        !string.IsNullOrEmpty(rigId)
        && (rigId.Contains("Magazine", StringComparison.OrdinalIgnoreCase)
            || rigId.Contains("BeltMarine", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Which replacement a slot takes: the belt slot takes the shell belt, the pocket slots take a shell
    ///     pouch, and every other slot takes nothing.
    /// </summary>
    public static string? RigKindForSlot(string slot) => slot switch
    {
        "belt" => "belt",
        "pocket1" or "pocket2" => "pouch",
        _ => null,
    };
}
