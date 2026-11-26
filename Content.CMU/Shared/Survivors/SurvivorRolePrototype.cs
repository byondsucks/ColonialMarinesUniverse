using Content.Shared.CMU14.Threats;
using Robust.Shared.Prototypes;

namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     The position a role body fills in a survivor group's composition. Positions are assigned in
///     <see cref="SurvivorRoleComposition.NamedRoles"/> order; every remaining survivor becomes a Rifleman.
///     Categories never merge roles — a faction with both a Commander and a Squad Leader fields one of each.
/// </summary>
public enum SurvivorRoleCategory : byte
{
    /// <summary>The faction's commander, if it has one.</summary>
    Commander,

    /// <summary>The faction's squad leader, if it has one.</summary>
    SquadLeader,

    /// <summary>
    ///     Generic leadership. Fills the commander position first, then the squad leader position when the
    ///     faction has a second leader body, so unclassified factions still field both.
    /// </summary>
    Leader,

    Medic,
    Technician,

    /// <summary>The faction's weapon specialist (smartgunner, heavy gunner, marksman, pilot, ...).</summary>
    WeaponSpecialist,

    /// <summary>Faction-specific non-combat VIP, such as a corporate liaison.</summary>
    Liaison,

    Rifleman,

    /// <summary>
    ///     A template role body that is not part of the survivor composition (synth, scientist, ...). It
    ///     stays available to the faction's own non-survivor spawns but is never assigned to a survivor.
    /// </summary>
    Support,
}

/// <summary>
///     Declares what a survivor role body is for, keyed by the role body's entity prototype id. A faction's
///     party spawn decides which roles exist and how many; this decides which composition position each
///     fills. Roles with no entry default to generic leadership in a party spawn's leader list and
///     Rifleman otherwise, so adding a faction needs no composition code.
/// </summary>
[Prototype]
public sealed partial class SurvivorRolePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public SurvivorRoleCategory Category { get; private set; }

    /// <summary>
    ///     Player-facing name for this faction's version of the role, shown in the survivor role picker.
    ///     Faction-specific by design: the same category reads differently for every faction, so this is
    ///     never derived from the shared job/entity name the body inherits.
    /// </summary>
    [DataField]
    public string? Name { get; private set; }

    /// <summary>Optional one-line description shown under the role name.</summary>
    [DataField]
    public string? Description { get; private set; }

    /// <summary>
    ///     The survivor faction this subtype belongs to, so a role can be validated against the party that
    ///     actually spawns it and never be shown under an unrelated faction.
    /// </summary>
    [DataField]
    public ProtoId<ThirdPartyPrototype>? Party { get; private set; }

    /// <summary>
    ///     Keep the role body's own name instead of the player's character name. Set for named characters
    ///     (Arnold 'Dutch' Schafer), where the body is a specific person rather than a role.
    /// </summary>
    [DataField]
    public bool KeepBodyName { get; private set; }

    /// <summary>
    ///     Only a player whose chosen character is a synthetic may take this role. Used by the survivor
    ///     synthetic roles, which spawn a synthetic body.
    /// </summary>
    [DataField]
    public bool RequiresSynthetic { get; private set; }
}
