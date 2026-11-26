using System.Collections.Generic;
using Content.Shared.CMU14.Threats;
using Robust.Shared.Prototypes;

namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     The three loadout categories a survivor picks from after the faction vote.
/// </summary>
public readonly record struct SurvivorLoadoutSelection(string? Armor, string? Helmet, string? Firearm);

/// <summary>
///     A faction's survivor equipment selection. One prototype per faction (several party prototypes can
///     share it), listing the actual armor, helmet and firearm entity prototypes the faction offers.
///     The selection UI, server validation and spawn equipment application are all driven from this data,
///     so adding or changing a faction's options never touches code.
/// </summary>
[Prototype]
public sealed partial class SurvivorLoadoutPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Third-party survivor party prototypes this loadout applies to.</summary>
    [DataField(required: true)]
    public List<ProtoId<ThirdPartyPrototype>> Factions { get; private set; } = new();

    [DataField]
    public List<ProtoId<EntityPrototype>> Armor { get; private set; } = new();

    [DataField]
    public List<ProtoId<EntityPrototype>> Helmets { get; private set; } = new();

    [DataField]
    public List<ProtoId<EntityPrototype>> Firearms { get; private set; } = new();

    /// <summary>
    ///     Belt issued instead of a magazine rig when the picked firearm loads loose shells (shotguns). Defaults
    ///     to the standard buckshot belt.
    /// </summary>
    [DataField]
    public ProtoId<EntityPrototype> ShotgunBelt { get; private set; } = "CMBeltMarineBuckshot";

    /// <summary>
    ///     Webbing issued instead of the standard one when the picked firearm loads loose shells. The kit's own
    ///     StorageFill then supplies the rounds, which is reliable - inserting ammunition into a worn webbing at
    ///     runtime keeps writing into the wrong storage layer.
    /// </summary>
    [DataField]
    public ProtoId<EntityPrototype>? ShotgunWebbing { get; private set; }

    /// <summary>Pouch issued instead of a magazine pouch when the picked firearm loads loose shells.</summary>
    [DataField]
    public ProtoId<EntityPrototype> ShotgunPouch { get; private set; } = "AU14PouchShotgunLargeBuckshot";

    /// <summary>
    ///     Rigs the survivor should be re-issued when they pick a particular firearm. The AH-12's 12.7x55mm drums
    ///     do not fit the standard magazine belt, so the BEAR kit swaps in the machinegunner belt and drum pouch.
    /// </summary>
    [DataField]
    public Dictionary<string, FirearmRigOverride> FirearmRigs { get; private set; } = new();

    /// <summary>Roles that are issued a Mateba autorevolver as their belt sidearm.</summary>
    [DataField]
    public HashSet<string> CommanderRoles { get; private set; } = new();

    /// <summary>The sidearm those roles are issued.</summary>
    [DataField]
    public EntProtoId CommanderSidearm { get; private set; } = "RMCWeaponRevolverMateba";

    /// <summary>Standard loads for it: 1-3 are issued.</summary>
    [DataField]
    public List<EntProtoId> CommanderSidearmAmmo { get; private set; } = new();

    /// <summary>High-impact loads for it: 1-3 are issued.</summary>
    [DataField]
    public List<EntProtoId> CommanderSidearmHighImpactAmmo { get; private set; } = new();

    /// <summary>
    ///     Optional per-role armor overrides, keyed by survivor role body prototype id. A role listed here
    ///     can only pick the armor it is given, so an engineer stays in engineer armor; a role with no entry
    ///     falls back to the faction-wide <see cref="Armor"/> list.
    /// </summary>
    [DataField]
    public Dictionary<string, List<ProtoId<EntityPrototype>>> RoleArmor { get; private set; } = new();

    /// <summary>
    ///     When set, the role's armor is rolled uniformly from its options at spawn instead of using the
    ///     player's pick. The source insert randomises riot suit versus jacket this way.
    /// </summary>
    [DataField]
    public bool RandomArmor { get; private set; }

    /// <summary>When set, the role's helmet is rolled uniformly from its options at spawn.</summary>
    [DataField]
    public bool RandomHelmets { get; private set; }

    /// <summary>Optional per-role helmet overrides, same rules as <see cref="RoleArmor"/>.</summary>
    [DataField]
    public Dictionary<string, List<ProtoId<EntityPrototype>>> RoleHelmets { get; private set; } = new();

    /// <summary>
    ///     Optional per-role firearm overrides, same rules as <see cref="RoleArmor"/>: a role listed here may
    ///     only pick from its own weapons. This is what keeps a grenadier's launcher and a leader's sidearm
    ///     out of everyone else's hands, which a category-wide restriction cannot express.
    /// </summary>
    [DataField]
    public Dictionary<string, List<ProtoId<EntityPrototype>>> RoleFirearms { get; private set; } = new();

    /// <summary>
    ///     Optional per-firearm role restrictions: firearm prototype id -> survivor role categories allowed
    ///     to pick it. Firearms not listed here are available to every role. Applied by the server when
    ///     building the options and when validating a selection, so a client cannot request a restricted
    ///     weapon its role may not use.
    /// </summary>
    [DataField]
    public Dictionary<string, List<SurvivorRoleCategory>> FirearmRoles { get; private set; } = new();

    /// <summary>
    ///     How many magazines of the chosen weapon the kit issues before the armour-piercing upgrade runs.
    /// </summary>
    [DataField]
    public int MagazineIssue { get; private set; } = 3;

    /// <summary>
    ///     Per-role magazine issue, for a role that is issued a different base load than the rest of its faction.
    /// </summary>
    [DataField]
    public Dictionary<string, int> RoleMagazineIssue { get; private set; } = new();

    /// <summary>
    ///     Optional per-firearm ammunition item, for weapons that load loose ammunition instead of a
    ///     magazine (a shotgun's shells, for instance). When a survivor swaps their primary for one of these,
    ///     the old weapon's magazines are converted into this item instead of the new weapon's magazine.
    ///     Weapons not listed here use their magazine slot or their internal cartridge.
    /// </summary>
    [DataField]
    public Dictionary<string, EntProtoId> FirearmAmmo { get; private set; } = new();
}

/// <summary>Replacement rig slots for a specific firearm.</summary>
[DataDefinition]
public sealed partial class FirearmRigOverride
{
    [DataField]
    public EntProtoId? Belt { get; set; }

    [DataField]
    public EntProtoId? Pouch { get; set; }
}
