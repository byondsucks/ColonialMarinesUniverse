using System.Linq;
using System.Numerics;
using Content.Server.Access.Systems;
using Content.Server.CMU14.Diagnostics.Performance;
using Content.Server.CMU14.Round;
using Content.Server.CMU14.Survivors.Setup;
using Content.Server.CMU14.Threats;
using Content.Server.CMU14.VendorMarker;
using Content.Server.Chat.Systems;
using Content.Shared.Chat;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Server.Spawners.Components;
using Content.Server.CMU14.Roles; // CMU14
using Content.Server.Preferences.Managers;
using Content.Shared.CMU14.ForceOnForce;
using Content.Shared.CMU14.Preferences;
using Content.Shared.CMU14.Survivors;
using Content.Shared._RMC14.EntityPreset;
using Content.Shared.CMU14.Threats;
using Content.Shared._RMC14.Construction;
using Content.Shared._RMC14.CrashLand;
using Content.Shared._RMC14.Dropship;
using Content.Shared._RMC14.Map;
using Content.Shared.Access.Components;
using Content.Shared.CMU14.util;
using Content.Shared.GameTicking;
using Content.Shared.Humanoid;
using Content.Shared.IdentityManagement;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.ParaDrop;
using Content.Shared.Players;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using ParachuteMarkerComponent = Content.Shared.CMU14.Threats.ParachuteMarkerComponent;
using Content.Shared.Body; // CMU14
using Content.Server.Ghost.Roles.Components;
using Content.Server._RMC14.Ghost;
using Content.Shared._RMC14.Roles;
using Content.Shared._RMC14.Marines.Roles.Ranks;

namespace Content.Server.CMU14.Ops.ThirdParty;

public sealed partial class ThirdPartySystem : EntitySystem
{
    [Dependency] private ForceInterestSystem _forceInterest = default!;
    [Dependency] private ICMUServerPerformanceDiagnostics _performance = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private AuRoundSystem _auRoundSystem = default!;
    [Dependency] private Content.Server._RMC14.Marines.Roles.Ranks.RankSystem _rank = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private Content.Server.Chat.Managers.IChatManager _chatManager = default!;
    [Dependency] private SharedDropshipSystem _sharedDropshipSystem = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private IdCardSystem _idCard = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private RMCMapSystem _rmcMap = default!;
    [Dependency] private PlatoonSpawnRuleSystem _platoons = default!;
    [Dependency] private SurvivorSupplementSystem _survivorSupplement = default!; // CMU14
    [Dependency] private HumanoidProfileSystem _humanoidProfile = default!; // CMU14
    [Dependency] private SharedVisualBodySystem _visualBody = default!; // CMU14
    [Dependency] private SurvivorLoadoutSystem _survivorLoadout = default!; // CMU14
    private static readonly ProtoId<JobPrototype> ThirdPartyLeaderJobId = new("AU14JobThirdPartyLeader");
    private static readonly ProtoId<JobPrototype> ThirdPartyMemberJobId = new("AU14JobThirdPartyMember");
    private static readonly ProtoId<JobPrototype> StrandedMemberJobId = new("AU14JobStrandedThirdPartyMember"); // CMU14: survivor rollers
    private static readonly ThreatMarkerType[] ThreatMarkerTypes = Enum.GetValues<ThreatMarkerType>();
    private readonly ISawmill _sawmill = Logger.GetSawmill("thirdparty");

    /// <summary>CMU14: one survivor's choices, handed over by the survivor setup vote.</summary>
    public readonly record struct SurvivorAssignment(
        NetUserId Player,
        string Body,
        bool Leader,
        HumanoidCharacterProfile Profile,
        SurvivorLoadoutSelection Loadout,
        SurvivorLoadoutPrototype? LoadoutConfig);

    // --- State for round third party spawning ---
    private ThreatPrototype? _currentThreat;
    private int _nextThirdPartyIndex;
    private bool _survivorsSpawned; // CMU14

    /// <summary>
    ///     Markers already handed to a survivor group this round. Faction survivors and civilian survivors are
    ///     separate groups spawned at separate times, so without this both could spawn on the same marker.
    /// </summary>
    private readonly HashSet<EntityUid> _consumedSurvivorMarkers = new();

    // --- Signal modifier applied by Ambassador / AI Core consoles ---
    private float _signalIntervalMultiplier = 1f;
    private bool _spawningActive;
    private TimeSpan _spawnInterval = TimeSpan.FromMinutes(5);
    private TimeSpan _arrivalInterval = TimeSpan.FromMinutes(5);
    private float _spawnTimer;
    private List<ThirdPartyPrototype>? _thirdPartyList;
    private static readonly TimeSpan ArrivalJitter = TimeSpan.FromMinutes(10);

    private readonly Dictionary<int, uint> _scheduledForces = new();
    private readonly Dictionary<string, uint> _automaticForces = new();
    private readonly Dictionary<uint, ThirdPartyPrototype> _queuedParties = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _spawningActive = false;
        _thirdPartyList = null;
        _currentThreat = null;
        _signalIntervalMultiplier = 1f;
        _scheduledForces.Clear();
        _queuedParties.Clear();
        _automaticForces.Clear();
        _survivorsSpawned = false; // CMU14
        _consumedSurvivorMarkers.Clear(); // CMU14: survivor ground is claimed per round
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_spawningActive || _thirdPartyList == null)
            return;
        if (_nextThirdPartyIndex >= _thirdPartyList.Count)
        {
            _spawningActive = false;
            return;
        }

        _spawnTimer += frameTime;
        ThirdPartyPrototype party = _thirdPartyList[_nextThirdPartyIndex];
        if (party.RoundStart)
        {
            _nextThirdPartyIndex++;
            return;
        }

        TimeSpan interval = TimeSpan.FromTicks((long)(_arrivalInterval.Ticks * _signalIntervalMultiplier));
        if (_spawnTimer < interval.TotalSeconds)
            return;

        _spawnTimer = 0f;
        _arrivalInterval = RollArrivalInterval();
        int roll = _random.Next(1, 101);
        int chance = Math.Clamp(party.weight * 10, 5, 100); // Example: weight 1 = 10%, weight 10 = 100%

        if (roll > chance)
        {
            _sawmill.Debug($"[ThirdPartySystem] Did not spawn ({party.ID}) (roll {roll} > {chance})");
            return;
        }

        if (_scheduledForces.TryGetValue(_nextThirdPartyIndex, out var force))
        {
            _forceInterest.SetReady(force);

            // The ghost-menu entry alone never reaches players who do not open it.
            if (!string.IsNullOrWhiteSpace(party.AnnounceInbound))
                _chat.DispatchGlobalAnnouncement(LocalizeAnnouncement(party.AnnounceInbound), string.Empty, false,
                    colorOverride: Color.DarkOrange);
        }

        _nextThirdPartyIndex++;
    }

    private TimeSpan RollArrivalInterval()
        => TimeSpan.FromMinutes(Math.Max(1, (int) _spawnInterval.TotalMinutes
            + _random.Next(-(int) ArrivalJitter.TotalMinutes, (int) ArrivalJitter.TotalMinutes + 1)));

    private static ThirdPartyAssignmentCounts CountThirdPartyAssignments(
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs)
    {
        if (assignedJobs == null)
            return default(ThirdPartyAssignmentCounts);

        var leaders = 0;
        var members = 0;
        foreach ((NetUserId _, (ProtoId<JobPrototype>? job, EntityUid _)) in assignedJobs)
        {
            if (job == ThirdPartyLeaderJobId)
                leaders++;
            else if (job == ThirdPartyMemberJobId || job == StrandedMemberJobId)
                members++;
        }

        return new(leaders, members);
    }

    private static string FormatSpawnBodies(IReadOnlyDictionary<string, int> bodies)
    {
        return bodies.Count == 0
            ? "none"
            : string.Join(", ", bodies.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    // CMU14: caps a party's body list to the number of players who rolled it
    internal static Dictionary<string, int> TrimBodiesTo(IReadOnlyDictionary<string, int> bodies, int limit)
    {
        var trimmed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string proto, int count) in bodies)
        {
            var take = Math.Min(count, limit);
            if (take <= 0)
                continue;

            trimmed[proto] = take;
            limit -= take;
        }

        return trimmed;
    }

    /// <summary>
    ///     Returns the list of queued third parties that have not yet spawned.
    /// </summary>
    public List<ThirdPartyPrototype> GetQueuedThirdParties()
    {
        return _queuedParties.Where(pair => _forceInterest.IsPending(pair.Key)).Select(pair => pair.Value).ToList();
    }

    /// <summary>
    ///     Sets the signal interval multiplier. Below 1 = signal boost, above 1 = signal jam.
    /// </summary>
    public void SetSignalIntervalMultiplier(float multiplier)
    {
        _signalIntervalMultiplier = Math.Max(0.1f, multiplier);
    }

    public float GetSignalIntervalMultiplier() => _signalIntervalMultiplier;

    // Queues the party for interest collection; spawn happens later on its own timer, so
    // there is no meaningful failure at this point and nothing is returned
    // summoningFaction is the faction (govfor/opfor) that called the party in, if any; their shuttle is held to that
    // faction's ship pad while the faction is shipside.
    public void SpawnThirdParty(ThirdPartyPrototype party, PartySpawnPrototype spawnProto, bool roundStart,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs = null, bool? overrideDropship = null,
        string? summoningFaction = null)
        => QueueThirdParty(party, spawnProto, roundStart, overrideDropship, true, assignedJobs, summoningFaction);

    /// <summary>
    ///     Spawns a third party right away, skipping the wait for interested players. Its members spawn as open ghost
    ///     roles. For admin testing; ordinary call-ins should go through <see cref="SpawnThirdParty"/>.
    /// </summary>
    public bool SpawnThirdPartyImmediately(ThirdPartyPrototype party, PartySpawnPrototype spawnProto,
        bool? overrideDropship = null, string? summoningFaction = null)
        => SpawnThirdPartyNow(party, spawnProto, false, null, overrideDropship, summoningFaction);

    private uint QueueThirdParty(ThirdPartyPrototype party, PartySpawnPrototype spawnProto, bool roundStart,
        bool? overrideDropship, bool ready,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs = null, string? summoningFaction = null)
    {
        var bodies = spawnProto.LeadersToSpawn.Concat(spawnProto.GruntsToSpawn).Concat(spawnProto.EntitiesToSpawn)
            .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Sum(pair => Math.Max(0, pair.Value)));
        var assignments = roundStart && assignedJobs != null
            ? assignedJobs.Where(pair => pair.Value.Item1 == ThirdPartyLeaderJobId
                                        || pair.Value.Item1 == ThirdPartyMemberJobId
                                        || pair.Value.Item1 == StrandedMemberJobId).ToDictionary()
            : new Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>();
        var id = _forceInterest.QueueDeployment(party.DisplayName ?? party.ID,
            _forceInterest.GetPlayableBodies(bodies),
            interested => SpawnThirdPartySteps(party, spawnProto, roundStart,
                assignments.Where(pair => interested.Contains(pair.Key)).ToDictionary(), overrideDropship, summoningFaction).GetEnumerator(),
            ready, assignments.Keys);
        _queuedParties.Add(id, party);
        return id;
    }

    private bool SpawnThirdPartyNow(ThirdPartyPrototype party, PartySpawnPrototype spawnProto, bool roundStart,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs, bool? overrideDropship,
        string? summoningFaction = null)
    {
        foreach (var step in SpawnThirdPartySteps(party, spawnProto, roundStart, assignedJobs, overrideDropship, summoningFaction))
        {
            if (step == ForceDeploymentStep.Completed)
                return true;
        }

        return false;
    }

    private IEnumerable<ForceDeploymentStep> SpawnThirdPartySteps(ThirdPartyPrototype party, PartySpawnPrototype spawnProto, bool roundStart,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs, bool? overrideDropship,
        string? summoningFaction = null, IReadOnlyList<SurvivorAssignment>? chosen = null) // CMU14
    {
        const float SpawnTogetherRadius = 8f;
        using var preparation = new ShuttlePreparation(_mapLoader);
        _sawmill.Debug($"[ThirdPartySystem] Spawning third party: ({party.ID})");
        if (spawnProto == null)
        {
            _sawmill.Error($"[ThirdPartySystem] Spawn called with null spawnProto for party ({party.ID})!");
            yield break;
        }

        // CMU14: a roundstart survivor group needs at least one rolled player, otherwise it would spawn empty bodies
        if (roundStart && party.AnnounceAsSurvivors && chosen == null)
        {
            var rolled = ThirdPartySystem.CountThirdPartyAssignments(assignedJobs);
            if (rolled.Leaders + rolled.Members == 0)
            {
                _sawmill.Debug($"[ThirdPartySystem] No players rolled survivors ({party.ID}); skipping spawn.");
                yield break;
            }
        }

        // Determine entry method. If overrideDropship is provided, it takes precedence (true => shuttle, false =>
        // ground).
        string? entryMethod = overrideDropship.HasValue
            ? overrideDropship.Value ? "shuttle" : "ground"
            : string.IsNullOrWhiteSpace(party.EntryMethod) ? "ground" : party.EntryMethod;
        // Presets can force shuttle entry; ground spawn puts parties inside enemy lines unprepared.
        // Admin overrides keep precedence. Survivors are the exception: they are colony inhabitants, so they
        // always start on the ground even in a preset that shuttles its ERT call-ins in.
        var groundEntryForbidden = party.AnnounceAsSurvivors != true
            && _auRoundSystem.SelectedPreset is { ThirdPartyForceShuttle: true };
        if (groundEntryForbidden
            && !overrideDropship.HasValue
            && string.Equals(entryMethod, "ground", StringComparison.OrdinalIgnoreCase))
            entryMethod = "shuttle";

        // A ground insertion needs marker entities to place bodies on.
        var survivorParty = party.AnnounceAsSurvivors == true;
        var hasColonyGround = HasGroundInsertMarkers(survivorParty);
        if (!hasColonyGround
            && string.Equals(entryMethod, "ground", StringComparison.OrdinalIgnoreCase))
        {
            // A survivor group always spawns. The colony is the intended location, so a map with no insert
            // ground is reported as a content gap - but the group still arrives, by dropship, rather than
            // leaving everyone waiting in the lobby.
            if (survivorParty && SurvivorSpawnMarkers.FallsBackToDropship(SurvivorSpawnMarkers.CanInsertOnColony(0)))
            {
                _sawmill.Error($"[ThirdPartySystem] Survivor party {party.ID} has no colony insert ground on the selected planet: add thirdpartyleaderspawnmarker / thirdpartymemberspawnmarker or survivor spawn points. Inserting by dropship instead.");
            }
            else
            {
                _sawmill.Warning($"[ThirdPartySystem] No ground insert markers for {party.ID}; falling back to shuttle entry.");
            }

            entryMethod = "shuttle";
        }
        _sawmill.Debug($"[ThirdPartySystem] Entry method: {entryMethod} (overrideDropship={overrideDropship})");
        if (_sawmill.Level <= LogLevel.Debug)
        {
            ThirdPartyAssignmentCounts assignmentCounts = ThirdPartySystem.CountThirdPartyAssignments(assignedJobs);
            _sawmill.Debug(
                $"[ThirdPartySystem] Spawn context: party={party.ID}, spawnProto={spawnProto.ID}, roundStart={roundStart}, threat={_currentThreat?.ID ?? "null"}, entryMethod={entryMethod}, assignedJobs={assignedJobs?.Count ?? 0}, assignedThirdPartyLeaders={assignmentCounts.Leaders}, assignedThirdPartyMembers={assignmentCounts.Members}.");
        }

        List<EntityUid> markerEntities = new();
        var markerEntitySet = new HashSet<EntityUid>();
        EntityUid mainGridUid = EntityUid.Invalid;
        var parachuteMode = false;

        void AddMarkerEntity(EntityUid uid)
        {
            if (markerEntitySet.Add(uid))
                markerEntities.Add(uid);
        }

        // Maintain compatibility with existing code that uses these locals.
        bool useDropship = entryMethod.Equals("shuttle", StringComparison.OrdinalIgnoreCase);

        if (useDropship)
        {
            // Reinforcements spawn aboard their shuttle even while all landing zones are occupied.
            // The navigation console resolves available destinations when the crew launches.
            DeserializationOptions deserializationOpts = DeserializationOptions.Default with { InitializeMaps = true };
            if (!TryLoadDropshipGrid(party.dropshippath, deserializationOpts, preparation, out mainGridUid))
                yield break;

            _sawmill.Debug($"[ThirdPartySystem] Dropship grid initialized: {mainGridUid}");

            var dropshipMapCoordinates = _transform.ToMapCoordinates(_entityManager
                .GetComponent<TransformComponent>(mainGridUid).Coordinates);
            EntityUid returnDestination;
            try
            {
                returnDestination = _entityManager.SpawnEntity(
                    "CMDropshipDestinationThirdPartyReturn",
                    dropshipMapCoordinates);
            }
            catch (Exception ex)
            {
                _sawmill.Error($"[ThirdPartySystem] Failed to spawn return destination entity at {dropshipMapCoordinates}: {ex}");
                yield break;
            }

            var returnDestinationComp = EnsureComp<ThirdPartyDropshipReturnDestinationComponent>(returnDestination);
            returnDestinationComp.Shuttle = mainGridUid;

            EnsureComp<DropshipDestinationComponent>(returnDestination);
            _sharedDropshipSystem.SetDestinationShip(returnDestination, mainGridUid);
            _sharedDropshipSystem.SetDestinationHome(returnDestination, true);

            EnsureComp<DropshipComponent>(mainGridUid);
            _sharedDropshipSystem.SetDropshipDestination(mainGridUid, returnDestination);

            if (!string.IsNullOrWhiteSpace(summoningFaction))
                EnsureComp<CMUSummonedThirdPartyShuttleComponent>(mainGridUid).Faction = summoningFaction.ToLowerInvariant();

            _sawmill.Debug($"[ThirdPartySystem] Third-party dropship {mainGridUid} loaded and waiting for manual launch.");

            // Collect markers on dropship grid
            EntityQueryEnumerator<AuInsertMarkerComponent> query = _entityManager
                .EntityQueryEnumerator<AuInsertMarkerComponent>();
            while (query.MoveNext(out EntityUid uid, out _))
            {
                if (_entityManager.TryGetComponent(uid, out TransformComponent? transform) &&
                    ThirdPartySystem.IsOnGrid(transform, mainGridUid))
                    AddMarkerEntity(uid);
            }

            EntityQueryEnumerator<ThreatSpawnMarkerComponent, TransformComponent> legacyMarkerQuery = _entityManager
                .EntityQueryEnumerator<ThreatSpawnMarkerComponent, TransformComponent>();
            while (legacyMarkerQuery.MoveNext(out EntityUid uid, out ThreatSpawnMarkerComponent? marker,
                out TransformComponent? transform))
            {
                if (!marker.ThirdParty ||
                    !ThirdPartySystem.IsOnGrid(transform, mainGridUid))
                    continue;

                AddMarkerEntity(uid);
            }

            _sawmill.Debug($"[ThirdPartySystem] Dropship markers collected: {markerEntities.Count}");

            // Spawn consoles
            EntityQueryEnumerator<VendorMarkerComponent> vmarkerQuery = _entityManager
                .EntityQueryEnumerator<VendorMarkerComponent>();
            var consoleCount = 0;
            while (vmarkerQuery.MoveNext(out EntityUid vmarkerUid, out VendorMarkerComponent? vmarkerComp))
            {
                try
                {
                    var markerXform = _entityManager.GetComponent<TransformComponent>(vmarkerUid);
                    if (markerXform.GridUid != mainGridUid)
                        continue;

                    switch (vmarkerComp.Class)
                    {
                        case PlatoonMarkerClass.DSPilot:
                            try
                            {
                                // SpawnEntity has no rotation parameter, so spawn attached to keep the marker's rotation
                                _entityManager.SpawnAttachedTo("CMComputerDropshipNavigationThirdParty",
                                    markerXform.Coordinates, rotation: markerXform.LocalRotation);
                                consoleCount++;
                            }
                            catch (Exception ex)
                            {
                                _sawmill.Error($"[ThirdPartySystem] Failed to spawn Dropship Navigation console: {ex}");
                            }

                            break;
                        case PlatoonMarkerClass.DSWeapons:
                            try
                            {
                                _entityManager.SpawnAttachedTo("CMComputerDropshipWeapons", markerXform.Coordinates, rotation: markerXform.LocalRotation);
                                consoleCount++;
                            }
                            catch (Exception ex)
                            {
                                _sawmill.Error($"[ThirdPartySystem] Failed to spawn Dropship Weapons console: {ex}");
                            }

                            break;
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Debug($"[ThirdPartySystem] Skipping vendor marker {vmarkerUid} (class={vmarkerComp.Class}) due to component error: {ex}");
                }
            }

            _sawmill.Debug($"[ThirdPartySystem] Dropship consoles spawned: {consoleCount}");
        }
        else if (entryMethod.Equals("parachute", StringComparison.OrdinalIgnoreCase))
        {
            // Parachute mode: collect parachute markers on the main map
            parachuteMode = true;
            EntityQueryEnumerator<ParachuteMarkerComponent, TransformComponent> pQuery = _entityManager
                .EntityQueryEnumerator<ParachuteMarkerComponent, TransformComponent>();
            while (pQuery.MoveNext(out EntityUid uid, out ParachuteMarkerComponent? pComp,
                out TransformComponent? pxform))
            {
                // Parachute markers are reusable and do not need to be marked as used; include all of them.
                AddMarkerEntity(uid);
            }

            _sawmill.Debug($"[ThirdPartySystem] Parachute markers collected: {markerEntities.Count}");
        }
        else
        {
            // Ground spawn: collect all markers on main map (existing behavior)
            EntityQueryEnumerator<AuInsertMarkerComponent> query = _entityManager
                .EntityQueryEnumerator<AuInsertMarkerComponent>();
            while (query.MoveNext(out EntityUid uid, out _))
            {
                AddMarkerEntity(uid);
            }

            EntityQueryEnumerator<ThreatSpawnMarkerComponent> legacyMarkerQuery = _entityManager
                .EntityQueryEnumerator<ThreatSpawnMarkerComponent>();
            while (legacyMarkerQuery.MoveNext(out EntityUid uid, out ThreatSpawnMarkerComponent? marker))
            {
                if (!marker.ThirdParty)
                    continue;

                AddMarkerEntity(uid);
            }

            // Survivors are colony inhabitants: they insert on the colony's third-party insert markers, exactly
            // like any other ground party, plus the colony's own survivor spawn points. Generic threat markers
            // belong to the xenomorph threat and ship-side markers are not the colony - neither may place a
            // survivor.
            if (party.AnnounceAsSurvivors)
            {
                var planetMap = PlanetMapId();
                markerEntities.RemoveAll(marker => !IsColonyGround(marker, planetMap));
                markerEntitySet.RemoveWhere(marker => !IsColonyGround(marker, planetMap));

                foreach (var spawnPoint in SurvivorSpawnPoints(planetMap))
                    AddMarkerEntity(spawnPoint);

                if (markerEntities.Count == 0)
                {
                    _sawmill.Error($"[ThirdPartySystem] Survivor party {party.ID} resolved no colony insert ground on planet map {planetMap?.ToString() ?? "any"}; the group should have been routed to a dropship.");
                }

                _sawmill.Debug(
                    $"[ThirdPartySystem] Survivor colony markers for {party.ID}: {markerEntities.Count} on planet map {planetMap?.ToString() ?? "any"}.");
            }

            _sawmill.Debug($"[ThirdPartySystem] Main map third-party markers collected: {markerEntities.Count}");
        }

        var candidateMapIds = new List<MapId>();
        var candidateMapIdSet = new HashSet<MapId>();
        foreach (EntityUid marker in markerEntities)
        {
            if (!_entityManager.TryGetComponent(marker, out TransformComponent? markerTransform) ||
                !candidateMapIdSet.Add(markerTransform.MapID))
                continue;

            candidateMapIds.Add(markerTransform.MapID);
        }

        MapId? mapId = candidateMapIds.Count > 0
            ? candidateMapIds[0]
            : null;
        if (_sawmill.Level <= LogLevel.Debug)
        {
            _sawmill.Debug(
                $"[ThirdPartySystem] Candidate marker maps for third party ({party.ID}): count={candidateMapIds.Count}, maps=[{string.Join(", ", candidateMapIds)}], initialMap={mapId?.ToString() ?? "null"}.");
        }

        IReadOnlyDictionary<string, int> leaderBodies = ThirdPartySystem.GetSpawnBodies(
            ThreatMarkerType.Leader,
            spawnProto.LeadersToSpawn);
        IReadOnlyDictionary<string, int> gruntBodies = ThirdPartySystem.GetSpawnBodies(
            ThreatMarkerType.Member,
            spawnProto.GruntsToSpawn);
        // CMU14 Survivors Begin: chosen survivors decide the bodies; otherwise one body per rolled player
        if (chosen != null)
        {
            leaderBodies = ThirdPartySystem.CountChosenBodies(chosen, true);
            gruntBodies = ThirdPartySystem.CountChosenBodies(chosen, false);
        }
        else if (roundStart && party.AnnounceAsSurvivors)
        {
            var rolled = ThirdPartySystem.CountThirdPartyAssignments(assignedJobs);
            leaderBodies = ThirdPartySystem.TrimBodiesTo(leaderBodies, rolled.Leaders);
            gruntBodies = ThirdPartySystem.TrimBodiesTo(gruntBodies, rolled.Members);
        }
        // CMU14 End
        IReadOnlyDictionary<string, int> entityBodies = ThirdPartySystem.GetSpawnBodies(
            ThreatMarkerType.Entity,
            spawnProto.EntitiesToSpawn);
        int leaderReq = leaderBodies.Values.Sum();
        int gruntReq = gruntBodies.Values.Sum();
        int entityReq = entityBodies.Values.Sum();

        int GetRequiredBodyCount(ThreatMarkerType markerType)
        {
            return markerType switch
            {
                ThreatMarkerType.Leader => leaderReq,
                ThreatMarkerType.Member => gruntReq,
                ThreatMarkerType.Entity => entityReq,
                _ => 0,
            };
        }

        var markerCache = new Dictionary<ThreatMarkerType, List<EntityUid>>();

        List<EntityUid> GetMarkers(ThreatMarkerType markerType)
        {
            if (markerCache.TryGetValue(markerType, out List<EntityUid>? cachedMarkers))
                return cachedMarkers;

            List<EntityUid> markers = ResolveMarkers(markerType);
            markerCache[markerType] = markers;
            return markers;
        }

        List<EntityUid> ResolveMarkers(ThreatMarkerType markerType)
        {
            TimeSpan time = _timing.CurTime;
            string markerId = spawnProto.Markers.TryGetValue(markerType, out string? id) ? id : string.Empty;
            var legacyMarkers = new List<EntityUid>();
            var legacyCooldownMarkers = new List<EntityUid>();
            // Ground another survivor group already claimed this round; used only if nothing else is left.
            var reservedGround = new List<EntityUid>();
            EntityQueryEnumerator<ThreatSpawnMarkerComponent> query = _entityManager
                .EntityQueryEnumerator<ThreatSpawnMarkerComponent>();
            while (query.MoveNext(out EntityUid uid, out ThreatSpawnMarkerComponent? comp))
            {
                // Only include markers that are of the requested type, match the optional marker ID,
                // are explicitly marked as ThirdParty, and are unused - and aren't on a Cooldown
                if (comp.ThreatMarkerType != markerType
                    || !(comp.ID == markerId || (comp.ID == string.Empty && markerId == string.Empty))
                    || !comp.ThirdParty)
                    continue;

                // CMU14: another survivor group already claimed this ground this round.
                if (party.AnnounceAsSurvivors && !SurvivorSpawnMarkers.MarkerIsAvailable(IsSurvivorMarkerReserved(uid)))
                {
                    reservedGround.Add(uid);
                    continue;
                }

                if (IsMarkerBlockedByWalls(uid))
                    continue;

                if (useDropship && mainGridUid != EntityUid.Invalid)
                {
                    if (!_entityManager.TryGetComponent(uid, out TransformComponent? tcomp)
                        || !tcomp.GridUid.HasValue || tcomp.GridUid.Value != mainGridUid)
                        continue;
                }
                else
                {
                    // Otherwise, ensure we are on the same map (if mapId set).
                    if (mapId != null && _entityManager.GetComponent<TransformComponent>(uid).MapID != mapId)
                        continue;
                }

                // Only include markers that are not already used
                // if (!comp.Used) // <- now handled by Cooldowns
                if (comp.NextAvailableAt > time)
                    legacyCooldownMarkers.Add(uid);
                else
                    legacyMarkers.Add(uid);
            }

            // CMU14: a survivor party spawns on the colony itself. Planet maps place survivor job spawn
            // points (CMSpawnPointSurvivor and friends) rather than third-party threat markers, which only
            // exist on the ERT dropship maps, so fall back to those positions.
            if (!useDropship && party.AnnounceAsSurvivors && legacyMarkers.Count < GetRequiredBodyCount(markerType))
            {
                foreach (var point in SurvivorSpawnPoints(mapId))
                {
                    if (legacyMarkers.Contains(point))
                        continue;

                    if (party.AnnounceAsSurvivors && !SurvivorSpawnMarkers.MarkerIsAvailable(IsSurvivorMarkerReserved(point)))
                    {
                        reservedGround.Add(point);
                        continue;
                    }

                    legacyMarkers.Add(point);
                }
            }

            // Every colony marker is already claimed by the other survivor group this round. Two groups on one
            // colony is normal, but riding a dropship is not: share the ground rather than spawn on the ship.
            if (!useDropship
                && party.AnnounceAsSurvivors
                && legacyMarkers.Count < GetRequiredBodyCount(markerType)
                && reservedGround.Count > 0)
            {
                foreach (var point in reservedGround)
                {
                    if (legacyMarkers.Count >= GetRequiredBodyCount(markerType))
                        break;

                    if (!legacyMarkers.Contains(point))
                        legacyMarkers.Add(point);
                }

                _sawmill.Warning(
                    $"[ThirdPartySystem] Survivor party ({party.ID}) shares colony ground another group claimed; {legacyMarkers.Count} marker(s) for {GetRequiredBodyCount(markerType)} body/bodies.");
            }

            int legacyRequiredBodyCount = GetRequiredBodyCount(markerType);
            if (roundStart && legacyMarkers.Count < legacyRequiredBodyCount)
            {
                int markersNeeded = legacyRequiredBodyCount - legacyMarkers.Count;
                int markersReused = Math.Min(markersNeeded, legacyCooldownMarkers.Count);
                if (markersReused > 0)
                {
                    legacyMarkers.AddRange(legacyCooldownMarkers.Take(markersReused));
                    _sawmill.Warning(
                        $"[ThirdPartySystem] Round-start third party ({party.ID}) is reusing {markersReused} legacy {markerType} marker(s) on cooldown because only {legacyMarkers.Count - markersReused} available marker(s) remain for {legacyRequiredBodyCount} body/bodies.");
                }
            }

            _sawmill.Debug($"[ThirdPartySystem] GetMarkers({markerType}): Found {legacyMarkers.Count} unused markers with markerId '{markerId}' on map {mapId}");
            return legacyMarkers;
        }

        bool spawnTogether = spawnProto.SpawnTogether;
        Dictionary<ThreatMarkerType, List<EntityUid>> spawnTogetherMarkers = new();
        if (spawnTogether)
        {
            var allMarkers = new List<EntityUid>();
            foreach (ThreatMarkerType type in ThreatMarkerTypes)
            {
                allMarkers.AddRange(GetMarkers(type));
            }

            if (allMarkers.Count > 0)
            {
                EntityUid centerMarker = allMarkers[_random.Next(allMarkers.Count)];
                EntityCoordinates centerCoords = _entityManager.GetComponent<TransformComponent>(centerMarker)
                    .Coordinates;
                foreach (ThreatMarkerType type in ThreatMarkerTypes)
                {
                    List<EntityUid> markers = GetMarkers(type);
                    var filtered = new List<EntityUid>();
                    foreach (EntityUid marker in markers)
                    {
                        EntityCoordinates coords = _entityManager.GetComponent<TransformComponent>(marker).Coordinates;
                        if (_transform.InRange(coords, centerCoords, 50f))
                            filtered.Add(marker);
                    }

                    // Fallback to all markers if none are in range
                    spawnTogetherMarkers[type] = filtered.Count > 0 ? filtered : markers;
                }
            }
        }

        List<EntityUid> GetSpawnMarkers(ThreatMarkerType type)
        {
            if (spawnTogether && spawnTogetherMarkers.TryGetValue(type, out List<EntityUid>? cached))
                return cached;
            return GetMarkers(type);
        }

        var spawnedLeaders = new List<EntityUid>();
        var spawnedGrunts = new List<EntityUid>();
        var spawnedEnts = new List<EntityUid>();

        // Track the last marker we used during this spawn operation
        EntityUid? lastUsedMarker = null;
        List<EntityUid> leaderMarkers = GetSpawnMarkers(ThreatMarkerType.Leader);
        List<EntityUid> gruntMarkers = GetSpawnMarkers(ThreatMarkerType.Member);
        List<EntityUid> entityMarkers = GetSpawnMarkers(ThreatMarkerType.Entity);
        if (_sawmill.Level <= LogLevel.Debug)
        {
            _sawmill.Debug(
                $"[ThirdPartySystem] Spawn plan for third party ({party.ID}): leaders[{ThirdPartySystem.FormatSpawnBodies(leaderBodies)}] requested={leaderReq} markers={leaderMarkers.Count}, grunts[{ThirdPartySystem.FormatSpawnBodies(gruntBodies)}] requested={gruntReq} markers={gruntMarkers.Count}, entities[{ThirdPartySystem.FormatSpawnBodies(entityBodies)}] requested={entityReq} markers={entityMarkers.Count}.");
        }

        if (leaderReq > 0 && leaderMarkers.Count == 0)
        {
            _sawmill.Warning($"[ThirdPartySystem] Third party ({party.ID}) requested {leaderReq} leader body/bodies but found no leader markers.");
        }

        if (gruntReq > 0 && gruntMarkers.Count == 0)
        {
            _sawmill.Warning($"[ThirdPartySystem] Third party ({party.ID}) requested {gruntReq} grunt body/bodies but found no member markers.");
        }

        if (entityReq > 0 && entityMarkers.Count == 0)
        {
            _sawmill.Warning($"[ThirdPartySystem] Third party ({party.ID}) requested {entityReq} entity spawn(s) but found no entity markers.");
        }

        List<EntityUid> FilterByType(ThreatMarkerType type)
        {
            var filtered = new List<EntityUid>();
            foreach (EntityUid marker in markerEntities)
            {
                if (_entityManager.TryGetComponent(marker,
                        out ThreatSpawnMarkerComponent? comp) &&
                    comp.ThirdParty &&
                    comp.ThreatMarkerType == type)
                    filtered.Add(marker);
            }

            return filtered;
        }

        // If parachute mode, use the parachute marker pool for all types; make local mutable copies so we can pick
        // without replacement during this spawn
        if (parachuteMode)
        {
            // Parachute markers must still have a ThreatSpawnMarkerComponent with ThirdParty==true
            leaderMarkers = FilterByType(ThreatMarkerType.Leader);
            gruntMarkers = FilterByType(ThreatMarkerType.Member);
            entityMarkers = FilterByType(ThreatMarkerType.Entity);
        }

        var unsafeLeaderMarkers = new List<EntityUid>();
        var unsafeGruntMarkers = new List<EntityUid>();
        var unsafeEntityMarkers = new List<EntityUid>();
        if (!useDropship)
        {
            var alivePlayerPositions = GetAlivePlayerPositions();

            void PreferMarkersAwayFromPlayers(ref List<EntityUid> markers, List<EntityUid> unsafeMarkers)
            {
                var safeMarkers = new List<EntityUid>(markers.Count);
                foreach (EntityUid marker in markers)
                {
                    if (IsMarkerBlockedByPlayers(marker, alivePlayerPositions))
                        unsafeMarkers.Add(marker);
                    else
                        safeMarkers.Add(marker);
                }

                markers = safeMarkers;
            }

            PreferMarkersAwayFromPlayers(ref leaderMarkers, unsafeLeaderMarkers);
            PreferMarkersAwayFromPlayers(ref gruntMarkers, unsafeGruntMarkers);
            PreferMarkersAwayFromPlayers(ref entityMarkers, unsafeEntityMarkers);
        }

        // Claim this group's ground before any body is placed, so the other survivor group cannot be handed it.
        ReserveSurvivorMarkers(party, leaderMarkers);
        ReserveSurvivorMarkers(party, unsafeLeaderMarkers);
        ReserveSurvivorMarkers(party, gruntMarkers);
        ReserveSurvivorMarkers(party, unsafeGruntMarkers);
        ReserveSurvivorMarkers(party, entityMarkers);
        ReserveSurvivorMarkers(party, unsafeEntityMarkers);

        bool ValidateMarkerPool(string bucket,
            int required,
            List<EntityUid> safeMarkers,
            List<EntityUid> unsafeMarkers)
        {
            if (required == 0)
                return true;

            int totalMarkers = safeMarkers.Count + unsafeMarkers.Count;
            if (totalMarkers == 0)
            {
                _sawmill.Warning(
                    $"[ThirdPartySystem] No {bucket} markers are available to spawn {required} body/bodies for third party ({party.ID}). Aborting spawn.");
                return false;
            }

            if (safeMarkers.Count == 0 && unsafeMarkers.Count > 0)
            {
                _sawmill.Warning(
                    $"[ThirdPartySystem] Third party ({party.ID}) has no {bucket} marker outside the eight-tile living-player range; nearby marker(s) will be used as the last-resort spawn location.");
            }

            int preferredMarkers = safeMarkers.Count > 0 ? safeMarkers.Count : unsafeMarkers.Count;
            if (preferredMarkers < required)
            {
                _sawmill.Warning(
                    $"[ThirdPartySystem] Third party ({party.ID}) needs {required} {bucket} body/bodies but only has {preferredMarkers} valid preferred marker(s); those markers will be reused only after each has been used once.");
            }

            return true;
        }

        if (!ValidateMarkerPool("leader", leaderReq, leaderMarkers, unsafeLeaderMarkers) ||
            !ValidateMarkerPool("member", gruntReq, gruntMarkers, unsafeGruntMarkers) ||
            !ValidateMarkerPool("entity", entityReq, entityMarkers, unsafeEntityMarkers))
            yield break;

        List<EntityUid> reusableLeaderMarkers = leaderMarkers.ToList();
        List<EntityUid> reusableGruntMarkers = gruntMarkers.ToList();
        List<EntityUid> reusableEntityMarkers = entityMarkers.ToList();
        List<EntityUid> reusableUnsafeLeaderMarkers = unsafeLeaderMarkers.ToList();
        List<EntityUid> reusableUnsafeGruntMarkers = unsafeGruntMarkers.ToList();
        List<EntityUid> reusableUnsafeEntityMarkers = unsafeEntityMarkers.ToList();

        _sawmill.Debug(
            $"[ThirdPartySystem] Final marker pools for third party ({party.ID}): leaders={leaderMarkers.Count} safe/{unsafeLeaderMarkers.Count} nearby, grunts={gruntMarkers.Count} safe/{unsafeGruntMarkers.Count} nearby, entities={entityMarkers.Count} safe/{unsafeEntityMarkers.Count} nearby, useDropship={useDropship}, parachuteMode={parachuteMode}.");

        // Finish preparation before creating bodies. The force scheduler resumes one body per tick.
        preparation.Accepted = true;
        yield return ForceDeploymentStep.Prepared;

        // Spawn leaders
        _sawmill.Debug("[ThirdPartySystem] Spawning leaders...");
        foreach ((string protoId, int count) in leaderBodies)
        {
            for (var i = 0; i < count; i++)
            {
                if (!TrySpawnAtMarker(protoId, leaderMarkers, unsafeLeaderMarkers, reusableLeaderMarkers,
                    reusableUnsafeLeaderMarkers, spawnedLeaders, parachuteMode, useDropship, "leader",
                    !roundStart, ref lastUsedMarker))
                    _sawmill.Warning($"[ThirdPartySystem] Failed to spawn leader {protoId}");

                yield return ForceDeploymentStep.Spawned;
            }
        }

        // Spawn grunts
        _sawmill.Debug("[ThirdPartySystem] Spawning grunts...");
        foreach ((string protoId, int count) in gruntBodies)
        {
            for (var i = 0; i < count; i++)
            {
                if (!TrySpawnAtMarker(protoId, gruntMarkers, unsafeGruntMarkers, reusableGruntMarkers,
                    reusableUnsafeGruntMarkers, spawnedGrunts, parachuteMode, useDropship, "grunt",
                    !roundStart, ref lastUsedMarker))
                    _sawmill.Warning($"[ThirdPartySystem] Failed to spawn grunt {protoId}");

                yield return ForceDeploymentStep.Spawned;
            }
        }

        // In Force on Force a party whose allegiance matches exactly one side's platoon is part
        // of that side: identification treats its members as teammates of it.
        var alignedSide = ResolveFoFAlignedSide(party);
        if (alignedSide != null)
        {
            foreach (var mob in spawnedLeaders.Concat(spawnedGrunts))
            {
                var side = _entityManager.EnsureComponent<ForceOnForceSideComponent>(mob);
                side.Side = alignedSide;
                _entityManager.Dirty(mob, side);
            }
        }

        // Spawn ents
        _sawmill.Debug("[ThirdPartySystem] Spawning ents...");
        foreach ((string protoId, int count) in entityBodies)
        {
            for (var i = 0; i < count; i++)
            {
                if (!TrySpawnAtMarker(protoId, entityMarkers, unsafeEntityMarkers, reusableEntityMarkers,
                    reusableUnsafeEntityMarkers, spawnedEnts, parachuteMode, useDropship, "ent",
                    !roundStart, ref lastUsedMarker))
                    _sawmill.Warning($"[ThirdPartySystem] Failed to spawn entity {protoId}");

                yield return ForceDeploymentStep.Spawned;
            }
        }

        _sawmill.Info(
            $"[ThirdPartySystem] Third-party spawn result for ({party.ID}): spawnedLeaders={spawnedLeaders.Count}/{leaderReq}, spawnedGrunts={spawnedGrunts.Count}/{gruntReq}, spawnedEntities={spawnedEnts.Count}/{entityReq}.");

        // After all spawns: if spawnTogether is true, mark nearby unused markers around the last used marker.
        void MarkNeighborsIfNeeded()
        {
            if (!spawnTogether || lastUsedMarker == null)
                return;

            EntityUid centerMarkerUid = lastUsedMarker.Value;
            if (!_entityManager.HasComponent<ThreatSpawnMarkerComponent>(centerMarkerUid))
                return;

            var centerXform = _entityManager.GetComponent<TransformComponent>(centerMarkerUid);
            EntityCoordinates centerCoords = centerXform.Coordinates;
            MapId centerMap = centerXform.MapID;

            EntityQueryEnumerator<ThreatSpawnMarkerComponent> query = _entityManager
                .EntityQueryEnumerator<ThreatSpawnMarkerComponent>();
            while (query.MoveNext(out EntityUid otherUid, out _))
            {
                if (otherUid == centerMarkerUid)
                    continue;

                var otherXform = _entityManager.GetComponent<TransformComponent>(otherUid);
                if (otherXform.MapID != centerMap)
                    continue;

                if (_transform.InRange(otherXform.Coordinates, centerCoords, SpawnTogetherRadius))
                {
                    if (_entityManager.TryGetComponent(otherUid,
                        out ThreatSpawnMarkerComponent? otherComp))
                    {
                        otherComp.NextAvailableAt = _timing.CurTime + otherComp.Cooldown;
                        Dirty(otherUid, otherComp);
                    }
                }
            }

        }

        // Run neighbor-marking now (only once per spawn operation, using the last used marker)
        MarkNeighborsIfNeeded();

        if (roundStart && party.AnnounceAsSurvivors) // CMU14
        {
            foreach (EntityUid survivor in spawnedLeaders.Concat(spawnedGrunts))
                _survivorSupplement.ApplyToSurvivor(survivor);
        }

        if (chosen != null) // CMU14
            AssignChosenSurvivors(party, chosen, spawnedLeaders.Concat(spawnedGrunts));

        if (roundStart && assignedJobs != null)
        {
            var leaderPlayers = new List<NetUserId>();
            var memberPlayers = new List<NetUserId>();
            foreach ((NetUserId player, (ProtoId<JobPrototype>? job, EntityUid _)) in assignedJobs)
            {
                if (job == ThirdPartyLeaderJobId)
                    leaderPlayers.Add(player);
                else if (job == ThirdPartyMemberJobId)
                    memberPlayers.Add(player);
            }

            _sawmill.Debug("[ThirdPartySystem] Assigning minds to third party entities (roundstart)");
            AssignMinds(leaderPlayers, spawnedLeaders, ThirdPartyLeaderJobId.Id, "leader");
            AssignMinds(memberPlayers, spawnedGrunts, ThirdPartyMemberJobId.Id, "member");
        }

        // Survivors are colony inhabitants, not a responding force: they never "make their entrance", so the
        // arrival announcement must not fire for them (it reached the whole lobby on every survivor spawn).
        if (party.AnnounceAsSurvivors)
        {
            // no-op
        }
        else if (!string.IsNullOrWhiteSpace(party.AnnounceArrival) && party.AnnounceArrivalTitle is { } arrivalTitle)
        {
            var arrival = LocalizeAnnouncement(party.AnnounceArrival);
            var wrapped = Loc.GetString("cmu-third-party-titled-arrival",
                ("title", FormattedMessage.EscapeText(Loc.GetString(arrivalTitle))),
                ("message", FormattedMessage.EscapeText(arrival)));
            _chatManager.ChatMessageToAll(ChatChannel.Radio, arrival, wrapped, default, false, true, Color.DarkOrange);
            _sawmill.Info($"[ThirdPartySystem] Announced arrival for third party ({party.ID}): {party.AnnounceArrival}");
        }
        else if (!string.IsNullOrWhiteSpace(party.AnnounceArrival))
        {
            _chat.DispatchGlobalAnnouncement(LocalizeAnnouncement(party.AnnounceArrival), string.Empty, false,
                colorOverride: Color.DarkOrange);
            _sawmill.Info($"[ThirdPartySystem] Announced arrival for third party ({party.ID}): {party.AnnounceArrival}");
        }

        _auRoundSystem.MarkThirdPartySpawned(party);
        yield return ForceDeploymentStep.Completed;
    }

    // announcements are raw English on most parties, a loc id is used when one exists
    private string LocalizeAnnouncement(string announcement)
        => Loc.TryGetString(announcement, out var localized) ? localized : announcement;

    private static IReadOnlyDictionary<string, int> GetSpawnBodies(
        ThreatMarkerType markerType,
        IReadOnlyDictionary<string, int> legacyBodies)
    {
        return legacyBodies
            .ToDictionary(
                body => body.Key,
                body => Math.Max(0, body.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsOnGrid(TransformComponent transform, EntityUid gridUid)
        => (transform.GridUid.HasValue && transform.GridUid.Value == gridUid) ||
            transform.ParentUid == gridUid;

    private bool TryLoadDropshipGrid(ResPath path, DeserializationOptions options,
        ShuttlePreparation preparation, out EntityUid gridUid)
    {
        gridUid = EntityUid.Invalid;
        var loadOptions = new MapLoadOptions
        {
            DeserializationOptions = options with { LogOrphanedGrids = false }
        };

        if (!_mapLoader.TryLoadGeneric(path, out LoadResult? result, loadOptions))
        {
            _sawmill.Error($"[ThirdPartySystem] Failed to load dropship map or grid: {path}");
            return false;
        }

        preparation.Result = result;
        gridUid = result.Grids.FirstOrDefault();
        if (gridUid != EntityUid.Invalid)
            return true;

        _sawmill.Error($"[ThirdPartySystem] No grids found in dropship map or grid: {path}");
        return false;
    }

    /// <summary>Failed preparation owns the entire load, including generated maps and grid children.</summary>
    private sealed class ShuttlePreparation(MapLoaderSystem loader) : IDisposable
    {
        public LoadResult? Result;
        public bool Accepted;

        public void Dispose()
        {
            if (!Accepted && Result != null)
                loader.Delete(Result);
        }
    }

    private string GetPlayerCharacterName(ICommonSession player, EntityUid? mind, string fallback)
    {
        if (mind != null &&
            TryComp(mind.Value, out MindComponent? mindComp) &&
            !string.IsNullOrWhiteSpace(mindComp.CharacterName))
            return mindComp.CharacterName;

        if (_preferences.GetPreferencesOrNull(player.UserId)?.SelectedCharacter is HumanoidCharacterProfile profile &&
            !string.IsNullOrWhiteSpace(profile.Name))
            return profile.Name;

        return fallback;
    }

    /// <summary>True when the map offers anywhere to put a ground-inserted party.</summary>
    /// <summary>
    ///     The colony's survivor spawn points: the positions planet maps give their own survivors.
    /// </summary>
    private List<EntityUid> SurvivorSpawnPoints(MapId? mapFilter)
    {
        var points = new List<EntityUid>();
        var query = _entityManager.EntityQueryEnumerator<SpawnPointComponent>();
        while (query.MoveNext(out var uid, out var spawnPoint))
        {
            if (!TryComp(uid, out TransformComponent? xform))
                continue;

            if (mapFilter != null && xform.MapID != mapFilter.Value)
                continue;

            if (spawnPoint.Job is not { } job
                || job.Id.IndexOf("survivor", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            if (IsMarkerBlockedByWalls(uid))
                continue;

            points.Add(uid);
        }

        return points;
    }

    /// <summary>The map the round's colony was loaded on, if a planet has been picked yet.</summary>
    private MapId? PlanetMapId()
    {
        var planet = _auRoundSystem.ActivePlanet;
        if (planet == null || !_entityManager.TryGetComponent(planet.Owner, out TransformComponent? planetXform))
            return null;

        return planetXform.MapID;
    }

    /// <summary>
    ///     Whether anything on the loaded maps can host a ground insertion. Survivors are limited to the colony
    ///     and to the marker categories that are colony insert ground (see <see cref="SurvivorSpawnMarkers"/>).
    /// </summary>
    private bool HasGroundInsertMarkers(bool survivorParty = false)
    {
        var planetMap = survivorParty ? PlanetMapId() : null;

        if (survivorParty && SurvivorSpawnPoints(planetMap).Count > 0)
            return true;

        var insertMarkers = _entityManager.EntityQueryEnumerator<AuInsertMarkerComponent>();
        while (insertMarkers.MoveNext(out var uid, out _))
        {
            if (!survivorParty || IsOnAllowedMap(uid, planetMap))
                return true;
        }

        var thirdPartyMarkers = _entityManager.EntityQueryEnumerator<ThreatSpawnMarkerComponent>();
        while (thirdPartyMarkers.MoveNext(out var uid, out _))
        {
            // Any third-party insert marker is colony ground - the same set every other ground party uses.
            if (!survivorParty || IsOnAllowedMap(uid, planetMap))
                return true;
        }

        return false;
    }

    /// <summary>Whether another survivor group already claimed this marker this round.</summary>
    private bool IsSurvivorMarkerReserved(EntityUid marker) => _consumedSurvivorMarkers.Contains(marker);

    /// <summary>Claims a survivor party's ground so the other population cannot spawn on it.</summary>
    private void ReserveSurvivorMarkers(ThirdPartyPrototype party, IEnumerable<EntityUid> markers)
    {
        if (party.AnnounceAsSurvivors != true)
            return;

        foreach (var marker in markers)
            _consumedSurvivorMarkers.Add(marker);
    }

    /// <summary>Whether a marker sits on the colony map (every map counts before a planet is picked).</summary>
    private bool IsOnAllowedMap(EntityUid marker, MapId? planetMap)
    {
        if (planetMap == null)
            return true;

        return TryComp(marker, out TransformComponent? xform)
               && SurvivorSpawnMarkers.IsAllowedMap(true, xform.MapID == planetMap.Value);
    }

    /// <summary>
    ///     Colony insert ground: any insert marker, any third-party insert marker, or a survivor spawn point, on the
    ///     colony map. Deliberately the same set every other ground party uses - narrowing it left colonies with no
    ///     valid marker, so the survivor group never arrived.
    /// </summary>
    private bool IsColonyGround(EntityUid marker, MapId? planetMap)
    {
        if (!IsOnAllowedMap(marker, planetMap))
            return false;

        return HasComp<AuInsertMarkerComponent>(marker)
               || HasComp<ThreatSpawnMarkerComponent>(marker)
               || HasComp<SpawnPointComponent>(marker);
    }


    /// <summary>
    ///     Whether a marker is colony insert ground: an insert marker, a third-party marker or a survivor spawn
    ///     point on the colony map.
    /// </summary>
    private bool IsColonyInsertGround(EntityUid marker, MapId? planetMap)
    {
        if (!IsOnAllowedMap(marker, planetMap))
            return false;

        var isSurvivorSpawnPoint = HasComp<SpawnPointComponent>(marker)
                                   && SurvivorSpawnPoints(planetMap).Contains(marker);
        return SurvivorSpawnMarkers.IsColonyInsertGround(
            HasComp<AuInsertMarkerComponent>(marker),
            TryComp<ThreatSpawnMarkerComponent>(marker, out var threat) && threat.ThirdParty,
            isSurvivorSpawnPoint);
    }

    /// <summary>The first rank a survivor job declares, or null.</summary>
    private ProtoId<RankPrototype>? _prototypesRank(ProtoId<JobPrototype> job)
    {
        if (!_prototypeManager.TryIndex(job, out JobPrototype? proto) || proto.Ranks is not { } ranks)
            return null;

        foreach (var (rank, entries) in ranks)
        {
            if (entries is not { Count: > 0 })
                return rank;
        }

        return null;
    }

    /// <summary>True when the role body is a specific named person rather than a role.</summary>
    private bool KeepBodyName(string bodyProto)
        => _prototypeManager.TryIndex<SurvivorRolePrototype>(bodyProto, out var role) && role.KeepBodyName;

    /// <summary>
    ///     Removes the call-in squad identity from a survivor: the ghost role they were spawned with and the
    ///     original job that would otherwise re-apply that squad's briefing to a survivor.
    /// </summary>
    private void StripCallInIdentity(EntityUid body)
    {
        RemCompDeferred<GhostRoleComponent>(body);
        RemCompDeferred<GhostTakeoverAvailableComponent>(body);
        RemCompDeferred<GhostRoleApplySpecialComponent>(body);

        if (HasComp<OriginalRoleComponent>(body))
            RemCompDeferred<OriginalRoleComponent>(body);
    }

    private void ApplyPlayerCharacterName(EntityUid mob, string characterName)
    {
        if (!HasComp<HumanoidProfileComponent>(mob))
            return;

        if (string.IsNullOrWhiteSpace(characterName))
            return;

        // A synthetic body already carries the name it generated for itself. Renaming it to the player's saved
        // human character would hand a synth a human identity, so the body keeps the name it spawned with.
        if (CMUCharacterName.HasRequiredParts(MetaData(mob).EntityName, synthetic: true))
            return;

        _metaData.SetEntityName(mob, characterName);

        if (_idCard.TryFindIdCard(mob, out Entity<IdCardComponent> idCard))
            _idCard.TryChangeFullName(idCard.Owner, characterName, idCard.Comp);

        _identity.QueueIdentityUpdate(mob);
    }

    public void StartThirdPartySpawning(ThreatPrototype threat,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs = null)
    {
        var planetInterval = _auRoundSystem.GetSelectedPlanet()?.ThirdPartyInterval;
        StartThirdPartySpawning(threat, planetInterval ?? threat.ThirdPartyInterval, $"threat={threat.ID}", assignedJobs);
    }

    public void StartThirdPartySpawning(GamePresetPrototype preset,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs = null)
    {
        StartThirdPartySpawning(null, preset.ThirdPartyInterval, $"preset={preset.ID}", assignedJobs);
    }

    private void StartThirdPartySpawning(ThreatPrototype? threat,
        int intervalSeconds,
        string scheduleContext,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs)
    {
        _currentThreat = threat;
        _thirdPartyList = _auRoundSystem.SelectedThirdParties.ToList();
        _nextThirdPartyIndex = 0;
        _spawnTimer = 0f;
        _spawnInterval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        _arrivalInterval = RollArrivalInterval();

        var roundstartCount = 0;
        foreach (ThirdPartyPrototype party in _thirdPartyList)
        {
            if (party.RoundStart)
                roundstartCount++;
        }

        ThirdPartyAssignmentCounts assignmentCounts = ThirdPartySystem.CountThirdPartyAssignments(assignedJobs);
        _sawmill.Info(
            $"[ThirdPartySystem] Starting third-party queue: {scheduleContext}, selected={_thirdPartyList.Count}, roundstart={roundstartCount}, interval={_spawnInterval}, assignedJobs={assignedJobs?.Count ?? 0}, assignedThirdPartyLeaders={assignmentCounts.Leaders}, assignedThirdPartyMembers={assignmentCounts.Members}.");

        if (_thirdPartyList.Count == 0)
        {
            _sawmill.Debug(
                "[ThirdPartySystem] No third parties selected for this planet; skipping third-party spawning.");
            _spawningActive = false;
            return;
        }

        _spawningActive = true;

        _scheduledForces.Clear();
        for (var i = 0; i < _thirdPartyList.Count; i++)
        {
            var party = _thirdPartyList[i];
            if (!_prototypeManager.TryIndex(party.PartySpawn, out var spawn))
                continue;

            if (party.RoundStart)
            {
                SpawnRoundStartParty(party, spawn, assignedJobs); // CMU14
                continue;
            }

            // A threat vote can extend a schedule which already includes round-start survivors.
            if (!_automaticForces.TryGetValue(party.ID, out var id))
            {
                id = QueueThirdParty(party, spawn, false, null, false, assignedJobs);
                _automaticForces.Add(party.ID, id);
            }

            _scheduledForces[i] = id;
        }
    }

    // A party fights for the side whose platoon shares its allegiance. When both sides rolled
    // platoons of that allegiance the tie stays unaligned and shows the question mark to both.
    private string? ResolveFoFAlignedSide(ThirdPartyPrototype party)
    {
        var ticker = _entityManager.System<GameTicker>();
        var preset = ticker.CurrentPreset?.ID ?? ticker.Preset?.ID;
        if (!string.Equals(preset, "ForceOnForce", StringComparison.OrdinalIgnoreCase))
            return null;

        if (party.Allegiance is not { } allegiance)
            return null;

        var govfor = _platoons.SelectedGovforPlatoon?.Allegiance == allegiance;
        var opfor = _platoons.SelectedOpforPlatoon?.Allegiance == allegiance;
        if (govfor == opfor)
            return null;

        return govfor ? "govfor" : "opfor";
    }

    private bool TrySpawnAtMarker(string protoId,
        List<EntityUid> markerPool,
        List<EntityUid> unsafeMarkerPool,
        List<EntityUid> reusableMarkerPool,
        List<EntityUid> reusableUnsafeMarkerPool,
        List<EntityUid> spawnedList,
        bool parachuteMode,
        bool useDropship,
        string label,
        bool trackUnclaimed,
        ref EntityUid? lastUsedMarker)
    {
        bool reusingMarker;
        bool bypassingPlayerRange;
        List<EntityUid> selectionPool;
        if (markerPool.Count > 0)
        {
            selectionPool = markerPool;
            reusingMarker = false;
            bypassingPlayerRange = false;
        }
        else if (reusableMarkerPool.Count > 0)
        {
            selectionPool = reusableMarkerPool;
            reusingMarker = true;
            bypassingPlayerRange = false;
        }
        else if (unsafeMarkerPool.Count > 0)
        {
            selectionPool = unsafeMarkerPool;
            reusingMarker = false;
            bypassingPlayerRange = true;
        }
        else
        {
            selectionPool = reusableUnsafeMarkerPool;
            reusingMarker = true;
            bypassingPlayerRange = true;
        }

        if (selectionPool.Count == 0)
        {
            _sawmill.Warning($"[ThirdPartySystem] Cannot spawn {label} ({protoId}): marker pool is empty.");
            return false;
        }

        EntityUid marker = PickRandomMarker(selectionPool, !reusingMarker && parachuteMode && !useDropship);

        if (marker == EntityUid.Invalid)
        {
            _sawmill.Warning($"[ThirdPartySystem] Cannot spawn {label} ({protoId}): selected marker is invalid.");
            return false;
        }

        if (!TryComp(marker, out TransformComponent? markerTransform))
        {
            selectionPool.Remove(marker);
            _sawmill.Warning($"[ThirdPartySystem] Skipping deleted {label} marker {marker}.");
            return false;
        }

        EntityCoordinates coords = markerTransform.Coordinates;
        try
        {
            using var cost = _performance.MeasureOperation("third-party-body-spawn", protoId);
            EntityUid ent = _entityManager.SpawnEntity(protoId, coords);

            // If parachute mode, hand off to the shared paradrop system so the entity falls from the sky.
            if (parachuteMode)
            {
                // Ensure the entity is paradroppable; SharedParaDropSystem will fall back to crash-land if missing.
                var paraComp = EnsureComp<ParaDroppableComponent>(ent);
                Dirty(ent, paraComp);

                // Raise AttemptCrashLandEvent on the grid entity that the parachute marker resides on so the para-drop
                // handler will run.
                var markerXform = _entityManager.GetComponent<TransformComponent>(marker);
                if (markerXform.GridUid.HasValue)
                {
                    var attemptEvent = new AttemptCrashLandEvent(ent);
                    RaiseLocalEvent(markerXform.GridUid.Value, ref attemptEvent);
                }
            }

            spawnedList.Add(ent);
            if (trackUnclaimed)
                _forceInterest.TrackRole(ent);

            // Put marker on a cooldown
            if (!parachuteMode
                && _entityManager.TryGetComponent(marker,
                    out ThreatSpawnMarkerComponent? markerComp))
            {
                markerComp.NextAvailableAt = _timing.CurTime + markerComp.Cooldown;
                Dirty(marker, markerComp);
            }
            // Parachute markers are intentionally NOT marked as used so they may be reused.
            lastUsedMarker = marker;
            if (!parachuteMode && !reusingMarker)
                selectionPool.Remove(marker); // prevent stacking

            if (bypassingPlayerRange)
            {
                _sawmill.Warning(
                    $"[ThirdPartySystem] Used {label} marker {ToPrettyString(marker)} inside the eight-tile living-player range to spawn {protoId} because no safe distinct marker remained.");
            }

            if (reusingMarker)
            {
                string reason = bypassingPlayerRange
                    ? "no distinct nearby marker remained"
                    : "no unused marker remained outside the eight-tile living-player range";
                _sawmill.Warning(
                    $"[ThirdPartySystem] Reused {label} marker {ToPrettyString(marker)} to spawn {protoId} for third party because {reason}.");
            }

            _sawmill.Debug($"[ThirdPartySystem] Spawned {label} {protoId} at {coords} (entity {ent})");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"[ThirdPartySystem] Failed to spawn {label} ({protoId}) at {coords}! {ex.Message}");
            return false;
        }
    }

    private bool IsMarkerBlockedByWalls(EntityUid marker)
        => _rmcMap.HasAnchoredEntityEnumerator<RMCDropshipBlockedComponent>(Transform(marker).Coordinates);

    private List<(MapId Map, Vector2 WorldPos)> GetAlivePlayerPositions()
    {
        var positions = new List<(MapId, Vector2)>();
        var query = EntityQueryEnumerator<ActorComponent, MobStateComponent, TransformComponent>();
        while (query.MoveNext(out _, out _, out var mobState, out var xform))
        {
            if (mobState.CurrentState != MobState.Alive)
                continue;

            positions.Add((xform.MapID, _transform.GetWorldPosition(xform)));
        }

        return positions;
    }

    private bool IsMarkerBlockedByPlayers(EntityUid marker, List<(MapId Map, Vector2 WorldPos)> playerPositions)
    {
        const float playerAvoidRadius = 8f;
        foreach ((MapId playerMap, Vector2 playerPos) in playerPositions)
        {
            if (playerMap != Transform(marker).MapID)
                continue;

            Vector2 diff = _transform.GetWorldPosition(Transform(marker)) - playerPos;
            if (diff.LengthSquared() <= playerAvoidRadius * playerAvoidRadius)
                return true;
        }

        return false;
    }

    private EntityUid PickRandomMarker(List<EntityUid> candidates, bool remove)
    {
        if (candidates.Count == 0)
            return EntityUid.Invalid;

        int index = _random.Next(candidates.Count);
        EntityUid marker = candidates[index];
        if (remove)
            candidates.RemoveAt(index);

        return marker;
    }

    private void AssignMinds(List<NetUserId> playerIds, List<EntityUid> spawnedList, string jobProto, string roleLabel)
    {
        var mindSystem = _entityManager.System<SharedMindSystem>();
        var roleSystem = _entityManager.System<SharedRoleSystem>();
        var ticker = _entityManager.System<GameTicker>();
        var assigned = 0;

        for (var i = 0; i < playerIds.Count && i < spawnedList.Count; i++)
        {
            NetUserId playerNetId = playerIds[i];
            EntityUid entity = spawnedList[i];
            try
            {
                if (!_playerManager.TryGetSessionById(playerNetId, out ICommonSession? session))
                    continue;

                ticker.PlayerJoinGame(session, true);

                ContentPlayerData? data = session.ContentData();
                EntityUid? mind = mindSystem.GetMind(playerNetId);
                string characterName = GetPlayerCharacterName(session, mind, data?.Name ?? "Third Party Player");
                ApplyPlayerCharacterName(entity, characterName);

                mind ??= mindSystem.CreateMind(playerNetId, characterName);
                mindSystem.SetUserId(mind.Value, playerNetId);
                mindSystem.TransferTo(mind.Value, entity);
                roleSystem.MindAddJobRole(mind.Value, silent: true, jobPrototype: jobProto);
                assigned++;
            }
            catch (Exception ex)
            {
                _sawmill.Error($"[ThirdPartySystem] Failed to assign {roleLabel} mind (player {playerNetId}, entity {entity}): {ex}");
            }
        }

        _sawmill.Info(
            $"[ThirdPartySystem] Third-party {roleLabel} mind assignment result: players={playerIds.Count}, bodies={spawnedList.Count}, assigned={assigned}, job={jobProto}.");
        if (playerIds.Count > spawnedList.Count)
        {
            _sawmill.Warning($"[ThirdPartySystem] Third-party {roleLabel} assignment had {playerIds.Count} player(s) but only {spawnedList.Count} body/bodies.");
        }
    }

    private readonly record struct ThirdPartyAssignmentCounts(int Leaders, int Members);

    // CMU14: roundstart parties are spawned either at round start (non-survivors) or reserved until the
    // survivor setup vote picks a faction.
    private void SpawnRoundStartParty(ThirdPartyPrototype party, PartySpawnPrototype spawn,
        Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)>? assignedJobs)
    {
        if (_automaticForces.ContainsKey(party.ID))
            return;

        if (party.AnnounceAsSurvivors && _survivorsSpawned) // rolled players fill one survivor group
            return;

        _automaticForces.Add(party.ID, 0);
        try
        {
            if (SpawnThirdPartyNow(party, spawn, true, assignedJobs, null) && party.AnnounceAsSurvivors)
                _survivorsSpawned = true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"[ThirdPartySystem] Exception spawning roundstart third party ({party.ID}): {ex}");
        }
    }

    /// <summary>
    ///     CMU14: stops <see cref="StartThirdPartySpawning(ThreatPrototype, Dictionary{NetUserId, (ProtoId{JobPrototype}?, EntityUid)}?)"/>
    ///     from spawning survivor parties while the held players are still choosing one.
    /// </summary>
    public void ReserveSurvivorParties()
    {
        foreach (var party in _auRoundSystem.SelectedThirdParties)
        {
            if (party.RoundStart && party.AnnounceAsSurvivors)
                _automaticForces.TryAdd(party.ID, 0);
        }
    }

    /// <summary>CMU14: spawns the survivor group the players voted for and puts each player in the body they picked.</summary>
    public bool SpawnChosenSurvivors(ThirdPartyPrototype party, IReadOnlyList<SurvivorAssignment> chosen)
    {
        if (!_prototypeManager.TryIndex(party.PartySpawn, out var spawn))
            return false;

        foreach (var step in SpawnThirdPartySteps(party, spawn, true, null, null, null, chosen))
        {
            if (step != ForceDeploymentStep.Completed)
                continue;

            _survivorsSpawned = true;
            return true;
        }

        return false;
    }

    // CMU14: one body per chosen role, so the group only ever spawns what the players picked
    internal static Dictionary<string, int> CountChosenBodies(IReadOnlyList<SurvivorAssignment> chosen, bool leaders)
    {
        var bodies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in chosen)
        {
            if (assignment.Leader == leaders)
                bodies[assignment.Body] = bodies.GetValueOrDefault(assignment.Body) + 1;
        }

        return bodies;
    }

    // CMU14: each body goes to the player who chose its role
    private void AssignChosenSurvivors(ThirdPartyPrototype party, IReadOnlyList<SurvivorAssignment> chosen,
        IEnumerable<EntityUid> bodies)
    {
        var pending = chosen.ToList();
        foreach (var body in bodies)
        {
            var protoId = Prototype(body)?.ID;
            var index = pending.FindIndex(assignment => string.Equals(assignment.Body, protoId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                continue;

            var assignment = pending[index];
            pending.RemoveAt(index);
            try
            {
                AssignSurvivorMind(party, assignment, body);
            }
            catch (Exception ex)
            {
                _sawmill.Error($"[ThirdPartySystem] Failed to assign survivor {assignment.Player} to {ToPrettyString(body)}: {ex}");
            }
        }
    }

    // CMU14: the body keeps its role gear; the chosen character supplies appearance and name
    /// <summary>
    ///     Applies a role body's <see cref="EquipEntityPresetComponent"/> immediately instead of leaving it to
    ///     MapInit, so the survivor loadout always runs against a fully equipped body and the gear's default
    ///     weapon is replaced rather than duplicated.
    /// </summary>
    private void ApplyRolePresetNow(EntityUid body)
    {
        if (!TryComp<EquipEntityPresetComponent>(body, out var preset))
            return;

        RemComp<EquipEntityPresetComponent>(body);
        _entityManager.System<EntityPresetSystem>().ApplyPreset(body, preset.Preset);
        _sawmill.Debug($"[ThirdPartySystem] Applied role preset {preset.Preset} to {ToPrettyString(body)} before the survivor loadout.");
    }

    private void AssignSurvivorMind(ThirdPartyPrototype party, SurvivorAssignment assignment, EntityUid body)
    {
        if (!_playerManager.TryGetSessionById(assignment.Player, out ICommonSession? session))
            return;

        // A survivor is not the ERT squad their role body inherits from: mark the body so the job greeting
        // (the "you are a part of ... party" block) is skipped for them.
        EnsureComp<CMUSurvivorSpawnedComponent>(body);

        // The role body names the job it belongs to in its ghost role, and with it come the faction, rank, job
        // icon and skills. Read it before the call-in identity is stripped: without it every survivor fell back
        // to the generic stranded job, which is W-Y contractor derived, so a CLF medic spawned with the W-Y
        // faction, a PMC rank, the W-Y job icon and no medical skills.
        var roleJob = TryComp<GhostRoleComponent>(body, out var ghostRole) ? ghostRole.JobProto : null;

        // Role gear can carry an EquipEntityPreset that equips its own - often randomly chosen - faction weapon.
        // EquipEntityPreset only fires on MapInit and removes itself there, so this is a no-op when the preset has
        // already run; it exists for bodies whose preset never saw MapInit. Either way the gear lands before the
        // loadout, which replaces the primary like any other starting weapon.
        ApplyRolePresetNow(body);

        var mindSystem = _entityManager.System<SharedMindSystem>();
        var roleSystem = _entityManager.System<SharedRoleSystem>();
        _entityManager.System<GameTicker>().PlayerJoinGame(session, true);

        // Named characters keep their own identity and their own look; everyone else spawns as the player's
        // character. Applying the player's profile to a named body put the player's face under that name.
        var keepBodyIdentity = KeepBodyName(assignment.Body);
        if (!keepBodyIdentity)
        {
            var profile = TryComp(body, out HumanoidProfileComponent? humanoid)
                ? assignment.Profile.WithSpecies(humanoid.Species)
                : assignment.Profile;
            _visualBody.ApplyProfileTo(body, profile);
            _humanoidProfile.ApplyProfileTo(body, profile);
        }

        if (!keepBodyIdentity)
            ApplyPlayerCharacterName(body, assignment.Profile.Name);

        // A survivor is not the call-in squad the role body came from: drop the ghost role and the original
        // job so nothing downstream (greetings, Dutch's Dozen hunting orders) treats them as that squad.
        StripCallInIdentity(body);

        EntityUid? mind = mindSystem.GetMind(assignment.Player);
        mind ??= mindSystem.CreateMind(assignment.Player, assignment.Profile.Name);
        mindSystem.SetUserId(mind.Value, assignment.Player);
        mindSystem.TransferTo(mind.Value, body);
        // The role's own job carries the faction, rank, job icon and skills; a body that is itself a job
        // prototype is used directly, and only a body that names neither falls back to the stranded job.
        var jobProto = roleJob
            ?? (_prototypeManager.HasIndex<JobPrototype>(assignment.Body)
                ? new ProtoId<JobPrototype>(assignment.Body)
                : StrandedMemberJobId);

        roleSystem.MindAddJobRole(mind.Value, silent: true, jobPrototype: jobProto.Id);

        // CMU14: swap the survivor's armor/helmet/firearm for their faction loadout choice. Queued only now,
        // after the job has been added: adding the job re-applies the role's gear preset, and a loadout applied
        // before that was overwritten by it - which left the survivor holding a second primary and, when the
        // uniform was re-equipped, wearing one whose webbing had been dropped.
        if (assignment.LoadoutConfig is { } loadout)
            _survivorLoadout.ApplyWhenDressed(body, loadout, assignment.Loadout, assignment.Body, roleJob);

        // The rank normally comes from GhostRoleApplySpecial, which survivor spawns strip, so apply the
        // job's rank here: it is what the inspect window reads.
        if (_prototypesRank(jobProto) is { } rank)
            _rank.SetRank(body, rank);

        // Show that job on the ID card, so inspecting the survivor does not report an empty job title.
        if (_prototypeManager.TryIndex(jobProto, out JobPrototype? job) &&
            _idCard.TryFindIdCard(body, out Entity<IdCardComponent> idCard))
        {
            idCard.Comp.JobTitle = job.Name;
            Dirty(idCard.Owner, idCard.Comp);
        }

        SendSurvivorBriefing(party, session);
    }

    /// <summary>
    ///     Tells the player who they are and where they are. The party's default intro/story are overridden by
    ///     the text for the map this round actually landed on, so the same faction reads differently on
    ///     Trijent than it does on Solaris Ridge.
    /// </summary>
    private void SendSurvivorBriefing(ThirdPartyPrototype party, ICommonSession session)
    {
        // Only survivor factions have a survivor briefing. A call-in party must never receive one, even if it
        // is spawned through the chosen-survivor path by an admin or a future caller.
        if (!party.AnnounceAsSurvivors)
            return;

        var planet = _auRoundSystem.ActivePlanet;
        var colonyName = string.IsNullOrWhiteSpace(planet?.VoteName) ? planet?.MapId : planet?.VoteName;

        var briefing = SurvivorBriefingRules.Resolve(
            planet?.SurvivorScenario,
            new SurvivorBriefingText(party.Intro, party.Story),
            SurvivorBriefingRules.Project(party.Scenario),
            colonyName);

        // The faction's own briefing tint, gold by default. Markup inside the locale text is rendered on top of
        // it, so a faction can highlight a line without giving up its base colour.
        var briefingColor = party.BriefingColor;

        if (briefing.Intro is { } intro)
        {
            _chatManager.ChatMessageToOne(ChatChannel.Server, intro, intro, EntityUid.Invalid, false,
                session.Channel, briefingColor);
        }

        if (briefing.Story is { } story)
        {
            _chatManager.ChatMessageToOne(ChatChannel.Server, story, story, EntityUid.Invalid, false,
                session.Channel, briefingColor);
        }
    }
}
