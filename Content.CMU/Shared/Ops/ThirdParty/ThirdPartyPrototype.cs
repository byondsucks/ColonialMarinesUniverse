using Robust.Shared.Maths;
using Content.Shared.CMU14.Allegiance;
using Content.Shared.CMU14.Survivors;
using Robust.Shared.Utility;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype.Array;
using Robust.Shared.Utility;

namespace Content.Shared.CMU14.Threats;

[Prototype]
public sealed partial class ThirdPartyPrototype : IPrototype, IInheritingPrototype
{
    [ParentDataField(typeof(AbstractPrototypeIdArraySerializer<ThirdPartyPrototype>))]
    public string[]? Parents { get; private set; }

    [NeverPushInheritance]
    [AbstractDataField]
    public bool Abstract { get; private set; }
    /// <summary>
    ///     Player-facing display name for this third party (e.g., "UPP GROM Special Forces").
    ///     If not set, falls back to ID.
    /// </summary>
    [DataField("displayName")]
    private string? _displayName;

    public string? DisplayName => _displayName != null && Loc.TryGetString(_displayName, out var localized)
        ? localized
        : _displayName;

    [DataField("blacklistedThreats")]
    public List<string> BlacklistedThreats { get; private set; } = new();

    [DataField("whitelistedThreats")]
    public List<string> WhitelistedThreats { get; private set; } = new();

    // The preferred field is the string 'entrymethod' (values: "ground", "shuttle", "parachute").
    [DataField("entrymethod")]
    public string? EntryMethod { get; private set; }

    [DataField]
    public ResPath dropshippath { get; private set; } = new("/Maps/CMU14/Dropships/black_ert.yml");

    [DataField]
    public List<string> BlacklistedGamemodes { get; private set; } = new();

    [DataField]
    public List<string> WhitelistedGamemodes { get; private set; } = new();

    [DataField]
    public int weight { get; private set; } = 1;

    [DataField("maxplayers")]
    public int MaxPlayers { get; private set; } = 100;

    /// <summary>
    ///     If true, ghosts may call this party in through the ghost role menu once enough players are dead.
    ///     Meant for minor civilian drop-ins, not heavy forces.
    /// </summary>
    [DataField]
    public bool GhostsCallable { get; private set; }

    [DataField("minplayers")]
    public int MinPlayers { get; private set; }

    [DataField]
    public int GhostsNeeded { get; private set; } = 10;

    [DataField]
    public List<string> BlacklistedPlatoons { get; private set; } = new();

    [DataField]
    public List<string> WhitelistedPlatoons { get; private set; } = new();

    /// <summary>
    ///     Allegiance this party fights for. In Force on Force a party whose allegiance matches
    ///     a side's platoon is tagged with that side, so identification treats it as a teammate.
    /// </summary>
    [DataField("allegiance")]
    public ProtoId<AllegiancePrototype>? Allegiance { get; private set; }

    [DataField("roundstart", required: false)]
    public bool RoundStart { get; private set; }

    /// <summary>
    ///     Always part of the survivor faction pool, regardless of which planets list it.
    /// </summary>
    [DataField]
    public bool SurvivorPool { get; private set; }

    /// <summary>
    ///     When this party wins the survivor faction vote the round is forced into a Predator round
    ///     (Dutch's Dozen exist to hunt Yautja).
    /// </summary>
    [DataField]
    public bool ForcesPredatorRound { get; private set; }

    /// <summary>
    ///     When the party fields several roles in the same composition category (three weapon specialists,
    ///     say), one is picked at random each round instead of always the first.
    /// </summary>
    [DataField]
    public bool RandomSpecialistRoles { get; private set; }

    /// <summary>
    ///     Whether this party belongs to a faction included in the Distress Signal survivor announcement.
    /// </summary>
    [DataField]
    public bool AnnounceAsSurvivors { get; private set; }

    [DataField("partyspawn", required: true)]
    public ProtoId<PartySpawnPrototype> PartySpawn { get; private set; }

    /// <summary>
    ///     Default intro locale key shown to this party's survivors when they spawn: the short "who you are
    ///     and what state you are in" block. Overridden per map through <see cref="Scenario"/>.
    /// </summary>
    [DataField]
    public LocId? Intro { get; private set; }

    /// <summary>Default story locale key (the longer "how you got here" block), overridable per map.</summary>
    [DataField]
    public LocId? Story { get; private set; }

    /// <summary>
    ///     Colour the intro and story are printed in. Defaults to the muted gold the survivor briefing has
    ///     always used, so a faction only sets this when it wants its own tint.
    /// </summary>
    /// <remarks>
    ///     Individual runs of text can be coloured inside the locale string with chat markup
    ///     (<c>[color=#c83232]...</c>), which is applied on top of this base colour.
    /// </remarks>
    [DataField]
    public Color BriefingColor { get; private set; } = Color.FromHex("#d8b465");

    /// <summary>
    ///     Briefing overrides keyed by the planet's survivor scenario (for example <c>crash_landing</c> or
    ///     <c>colonial_uprising</c>), so the same faction reads differently depending on where the round
    ///     landed. Maps that share a scenario share one entry. A scenario only has to set the parts that
    ///     differ; the rest falls back to <see cref="Intro"/> and <see cref="Story"/>.
    /// </summary>
    [DataField]
    public Dictionary<string, ThirdPartyBriefing> Scenario { get; private set; } = new();

    /// <summary>
    ///     This faction's own job icons for the survivor role picker, keyed by composition category, so a
    ///     Dutch's Dozen role shows the freelancer icon rather than falling back to the CLF set.
    ///     Categories left out fall back to the generic icon.
    /// </summary>
    [DataField]
    public Dictionary<SurvivorRoleCategory, SpriteSpecifier> SurvivorIcons { get; private set; } = new();

    [DataField("announcearrival")]
    public string? AnnounceArrival { get; private set; } = "A responding force has made their entrance into the conflict.";

    // optional header for the arrival line instead of the plain announcement wrap,
    // e.g. Dutch's Dozen uses cmss13's "Intercepted Transmission:"
    [DataField("announcearrivaltitle")]
    public LocId? AnnounceArrivalTitle { get; private set; }

    /// <summary>
    ///     Announced when a scheduled party goes ready and starts gathering ghost volunteers,
    ///     before it deploys. Worded as responders en route so a delayed landing reads as intended.
    /// </summary>
    [DataField("announceinbound")]
    public string? AnnounceInbound { get; private set; } = "Long range arrays detect an unidentified force moving to answer the distress call. Arrival expected shortly.";

    [IdDataField]
    public string ID { get; private set; } = default!;
}

/// <summary>
///     One survivor scenario's version of a party's spawn briefing. Null fields fall back to the party's defaults.
/// </summary>
[DataDefinition]
public sealed partial class ThirdPartyBriefing
{
    [DataField]
    public LocId? Intro { get; private set; }

    [DataField]
    public LocId? Story { get; private set; }
}
