using System;
using System.Collections.Generic;
using System.Linq;
using Content.Shared.CMU14.Survivors;
using Robust.Shared.Random;
using Content.Shared.Item;
using Content.Shared.CMU14.Threats;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Stacks;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tag;
using Content.Shared._RMC14.Roles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Server.Station.Systems;
using Content.Shared.Roles;
using Content.Shared._RMC14.Marines.Skills;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;

namespace Content.Server.CMU14.Survivors.Setup;

/// <summary>
///     Owns the faction survivor loadouts: builds the faction index from prototypes, validates selections
///     and applies the chosen armor, helmet and firearm to a spawned survivor body.
///     Everything here is driven by <see cref="SurvivorLoadoutPrototype"/> data.
/// </summary>
public sealed class SurvivorLoadoutSystem : EntitySystem
{
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private SharedContainerSystem _container = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private TagSystem _tagSystem = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private Robust.Shared.Random.IRobustRandom _random = default!;

    /// <summary>Sidearms issued by the loadout: never swept by the firearm trims.</summary>
    private readonly HashSet<EntityUid> _grantedSidearms = new();

    /// <summary>The weapon the current application issued, spared by every firearm sweep.</summary>
    private EntityUid? _chosenPrimary;

    /// <summary>How many magazines of the chosen weapon a survivor should end up carrying.</summary>
    private const int SpareMagazines = 4;

    private const string ArmorSlot = "outerClothing";
    private const string HelmetSlot = "head";
    private static readonly string[] FirearmSlots = { "suitstorage" };

    /// <summary>Where a commander's belt sidearm can be worn, in order of preference.</summary>
    private static readonly string[] SidearmSlots = { "belt", "pocket1", "pocket2", "hand" };

    /// <summary>How deep nested storage (backpack to pouch to magazine) is searched for spare magazines.</summary>
    private const int MaxStorageDepth = 3;

    private readonly Dictionary<string, SurvivorLoadoutPrototype> _byFaction =
        new(StringComparer.OrdinalIgnoreCase);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        RebuildIndex();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Safety net: role gear usually arrives on MapInit and drains the queue immediately. If some other
        // path never equips the body (or the gear system is not involved), apply the choice after a beat so
        // the survivor is never left with the role's default kit by accident.
        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<CMUSurvivorLoadoutPendingComponent>();
        while (query.MoveNext(out var uid, out var pending))
        {
            // Role gear and its spawn-time variants are applied while the body initialises; wait for those
            // to settle before swapping, otherwise the gear overwrites (or drops) the loadout items.
            if (now - pending.RequestedAt > TimeSpan.FromSeconds(0.1))
                ApplyPending(uid);
        }
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<SurvivorLoadoutPrototype>())
            RebuildIndex();
    }

    private void RebuildIndex()
    {
        _byFaction.Clear();

        foreach (var loadout in _prototypes.EnumeratePrototypes<SurvivorLoadoutPrototype>())
        {
            ValidateOptions(loadout, loadout.Armor, "armor");
            ValidateOptions(loadout, loadout.Helmets, "helmet");
            ValidateOptions(loadout, loadout.Firearms, "firearm");

            foreach (var (firearm, _) in loadout.FirearmRoles)
            {
                if (!loadout.Firearms.Any(option =>
                        string.Equals(option.Id, firearm, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Warning(
                        $"Survivor loadout '{loadout.ID}' restricts firearm '{firearm}' that it does not offer.");
                }
            }

            foreach (var (role, options) in loadout.RoleArmor)
                ValidateRoleOptions(loadout, role, options, "armor");

            foreach (var (role, options) in loadout.RoleHelmets)
                ValidateRoleOptions(loadout, role, options, "helmet");

            foreach (var faction in loadout.Factions)
            {
                if (!_prototypes.HasIndex(faction))
                {
                    Log.Error($"Survivor loadout '{loadout.ID}' references unknown faction '{faction}'.");
                    continue;
                }

                if (_byFaction.TryAdd(faction.Id, loadout))
                    continue;

                Log.Error(
                    $"Survivor faction '{faction}' is covered by both '{_byFaction[faction.Id].ID}' and "
                    + $"'{loadout.ID}'; keeping the first.");
            }
        }
    }

    private void ValidateOptions(SurvivorLoadoutPrototype loadout, List<ProtoId<EntityPrototype>> options, string category)
    {
        if (options.Count == 0)
        {
            Log.Warning($"Survivor loadout '{loadout.ID}' has no {category} options.");
            return;
        }

        foreach (var option in options)
        {
            if (!_prototypes.HasIndex<EntityPrototype>(option))
                Log.Error($"Survivor loadout '{loadout.ID}' references missing {category} prototype '{option}'.");
        }
    }

    /// <summary>Validates one role's override list: the role must exist and every option must resolve.</summary>
    private void ValidateRoleOptions(SurvivorLoadoutPrototype loadout, string role,
        List<ProtoId<EntityPrototype>> options, string category)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(role))
            Log.Error($"Survivor loadout '{loadout.ID}' has {category} options for unknown role '{role}'.");

        if (options.Count == 0)
        {
            Log.Warning($"Survivor loadout '{loadout.ID}' has an empty {category} list for role '{role}'.");
            return;
        }

        foreach (var option in options)
        {
            if (!_prototypes.HasIndex<EntityPrototype>(option))
            {
                Log.Error($"Survivor loadout '{loadout.ID}' references missing {category} prototype "
                    + $"'{option}' for role '{role}'.");
            }
        }
    }

    /// <summary>The loadout configured for this survivor faction, if any.</summary>
    public bool TryGetLoadout(ThirdPartyPrototype faction, out SurvivorLoadoutPrototype loadout)
        => _byFaction.TryGetValue(faction.ID, out loadout!);

    public bool HasLoadout(ThirdPartyPrototype faction) => _byFaction.ContainsKey(faction.ID);

    public SurvivorLoadoutOptions GetOptions(SurvivorLoadoutPrototype loadout)
        => new(
            loadout.Armor.Select(id => id.Id).ToList(),
            loadout.Helmets.Select(id => id.Id).ToList(),
            loadout.Firearms.Select(id => id.Id).ToList());

    /// <summary>
    ///     Options for a specific role: armor and helmet are the role's own when the loadout configures an
    ///     override for it (otherwise the faction-wide list), and firearms are filtered to what that role's
    ///     survivor category may use.
    /// </summary>
    public SurvivorLoadoutOptions GetOptions(SurvivorLoadoutPrototype loadout, string? roleProtoId)
    {
        var armor = RoleOptions(loadout.Armor, loadout.RoleArmor, roleProtoId);
        var helmets = RoleOptions(loadout.Helmets, loadout.RoleHelmets, roleProtoId);
        var firearms = roleProtoId != null
                       && loadout.RoleFirearms.TryGetValue(roleProtoId, out var roleFirearms)
                       && roleFirearms.Count > 0
            ? roleFirearms.Select(id => id.Id).ToList()
            : SurvivorLoadoutRules.FilterFirearms(
                loadout.Firearms.Select(id => id.Id).ToList(), loadout.FirearmRoles, RoleCategory(roleProtoId));
        return new SurvivorLoadoutOptions(armor, helmets, firearms);
    }

    /// <summary>
    ///     A fully random loadout for a survivor who never picked one: armor, helmet and firearm are each rolled
    ///     from the role's own options, so an AFK player still spawns dressed and armed instead of in defaults.
    /// </summary>
    public SurvivorLoadoutSelection RandomSelection(SurvivorLoadoutPrototype loadout, string? roleProtoId,
        Func<int, int> roll)
    {
        var options = GetOptions(loadout, roleProtoId);
        var defaults = SurvivorLoadoutRules.Resolve(options, default);
        return defaults with
        {
            // A category the role locks to one option is left alone: the lock survives the roll.
            Armor = SurvivorLoadoutRules.PickIfOpen(options.Armor, roll) ?? defaults.Armor,
            Helmet = SurvivorLoadoutRules.PickIfOpen(options.Helmets, roll) ?? defaults.Helmet,
            Firearm = SurvivorLoadoutRules.PickIfOpen(options.Firearms, roll) ?? defaults.Firearm,
        };
    }

    /// <summary>
    ///     Rolls the categories the loadout marks as random (the source Wet Ops insert rolls riot suit versus
    ///     jacket and helmet versus riot helmet). Fixed categories keep the player's pick. Called at spawn
    ///     time, after the role is known and before the gear is applied, so nothing overwrites the roll.
    /// </summary>
    public SurvivorLoadoutSelection Randomize(SurvivorLoadoutPrototype loadout, SurvivorLoadoutSelection selection,
        string? roleProtoId, Func<int, int> roll)
    {
        if (!loadout.RandomArmor && !loadout.RandomHelmets)
            return selection;

        var options = GetOptions(loadout, roleProtoId);
        return selection with
        {
            // Only categories that are both marked random and actually offer a choice get rolled; a role locked
            // to a single armour or helmet keeps it.
            Armor = loadout.RandomArmor
                ? SurvivorLoadoutRules.PickIfOpen(options.Armor, roll) ?? selection.Armor
                : selection.Armor,
            Helmet = loadout.RandomHelmets
                ? SurvivorLoadoutRules.PickIfOpen(options.Helmets, roll) ?? selection.Helmet
                : selection.Helmet,
        };
    }

    /// <summary>
    ///     A role's own option list when the loadout gives it one, otherwise the faction-wide list. This is
    ///     what locks a role's armor/helmet: the picker and the server-side validation both read it.
    /// </summary>
    private static List<string> RoleOptions(
        IReadOnlyList<ProtoId<EntityPrototype>> faction,
        IReadOnlyDictionary<string, List<ProtoId<EntityPrototype>>> overrides,
        string? roleProtoId)
    {
        if (roleProtoId != null && overrides.TryGetValue(roleProtoId, out var options) && options.Count > 0)
            return options.Select(id => id.Id).ToList();

        return faction.Select(id => id.Id).ToList();
    }

    /// <summary>The survivor composition category of a role prototype, or null if it is not classified.</summary>
    public SurvivorRoleCategory? RoleCategory(string? roleProtoId)
        => roleProtoId != null && _prototypes.TryIndex<SurvivorRolePrototype>(roleProtoId, out var role)
            ? role.Category
            : null;

    /// <summary>True when this role may pick the firearm under the faction's role restrictions.</summary>
    public bool CanRoleUseFirearm(SurvivorLoadoutPrototype loadout, string? roleProtoId, string firearm)
        => GetOptions(loadout, roleProtoId).Firearms
            .Contains(firearm, StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates a request against the faction's options, filling in defaults.</summary>
    public SurvivorLoadoutSelection Resolve(SurvivorLoadoutPrototype loadout, SurvivorLoadoutSelection requested)
        => SurvivorLoadoutRules.Resolve(GetOptions(loadout), requested);

    /// <summary>Validates a request against the role's allowed options, filling in defaults.</summary>
    public SurvivorLoadoutSelection Resolve(SurvivorLoadoutPrototype loadout, SurvivorLoadoutSelection requested,
        string? roleProtoId)
        => SurvivorLoadoutRules.Resolve(GetOptions(loadout, roleProtoId), requested);

    public SurvivorLoadoutSelection DefaultSelection(SurvivorLoadoutPrototype loadout)
        => SurvivorLoadoutRules.Resolve(GetOptions(loadout), default);

    /// <summary>
    ///     Applies the loadout once the survivor is actually dressed. Role gear is equipped when the body
    ///     is initialised, so this waits for that (see <see cref="CMUSurvivorLoadoutPendingComponent"/>)
    ///     instead of racing it and pushing the role's own gun and magazines onto the floor.
    /// </summary>
    public void ApplyWhenDressed(EntityUid body, SurvivorLoadoutPrototype loadout, SurvivorLoadoutSelection selection,
        string? roleProtoId = null, ProtoId<JobPrototype>? roleJob = null)
    {
        // Most factions dress a survivor from a preset on the body; a role that only carries gear on its job would
        // otherwise arrive in whatever the loadout alone put on it - which is how a CLF Spec Ops medic spawned with
        // nothing but a helmet, an armour vest and a gun.
        EnsureRoleGear(body, roleJob);

        var pending = EnsureComp<CMUSurvivorLoadoutPendingComponent>(body);
        pending.LoadoutId = loadout.ID;
        pending.Selection = selection;
        pending.Role = roleProtoId;
        pending.RequestedAt = _timing.CurTime;
        Dirty(body, pending);
    }

    /// <summary>Drains a pending loadout, called right after the role's starting gear is equipped.</summary>
    public void ApplyPending(EntityUid body)
    {
        if (!TryComp<CMUSurvivorLoadoutPendingComponent>(body, out var pending))
            return;

        RemComp<CMUSurvivorLoadoutPendingComponent>(body);

        if (_prototypes.TryIndex<SurvivorLoadoutPrototype>(pending.LoadoutId, out var loadout))
            Apply(body, loadout, pending.Selection, pending.Role);
    }

    /// <summary>
    ///     Equips the selection on a freshly spawned survivor body. Armor, helmet and firearm replace what
    ///     the role template put in those slots (the replaced item is removed outright, so the survivor is
    ///     never left standing in a pile of their old kit), and the survivor's spare magazines are swapped
    ///     for magazines of the weapon they actually chose.
    /// </summary>
    public void Apply(EntityUid body, SurvivorLoadoutPrototype loadout, SurvivorLoadoutSelection requested,
        string? roleProtoId = null)
    {
        // Resolve against the role's own options: a role that locks its armor (the PMC engineer, the heavy
        // weapons specialist) must not be re-resolved against the faction-wide list at spawn.
        var resolved = Resolve(loadout, requested, roleProtoId);

        ApplyToSlot(body, ArmorSlot, resolved.Armor);
        ApplyToSlot(body, HelmetSlot, resolved.Helmet);

        // Nothing the survivor spawns with should litter the spawn point, including for loadouts that do not
        // pick a firearm at all (the civilian kit), so this runs before the firearm branch returns.
        StripDroppedConstructionSheets(body);

        if (resolved.Firearm == null || !RoleMayCarryWeapon(roleProtoId, loadout))
            return;

        // Replacing the primary weapon must not touch a holstered sidearm, belt rig or backpack gun. Only a gun
        // actually carried in a weapon slot is deleted; a gun on the floor is only used to work out which
        // ammunition to convert, because it belongs to whoever dropped it (possibly a neighbour spawning here).
        // The role's gear preset carries a random weapon (RuMCBEARPreset has six gun+rig combos, tryEquipRandomWeapon
        // true). Snapshotting the guns it equipped lets the loadout delete exactly those once it has issued the
        // player's choice, including any the preset put outside the primary slot - which is the gun that used to be
        // forced out of its slot and left lying under the survivor.
        var gearGuns = CollectContained(body).ToList();

        var equippedPrimary = FindEquippedPrimary(body);
        var oldPrimaryUid = equippedPrimary ?? FindDroppedGun(body);
        var oldPrimary = oldPrimaryUid is { } oldUid ? PrototypeOf(oldUid) : null;
        var equipped = false;
        _chosenPrimary = null;
        foreach (var slot in FirearmSlots)
        {
            if (!ApplyToSlot(body, slot, resolved.Firearm))
                continue;

            _inventory.TryGetSlotEntity(body, slot, out _chosenPrimary);

            equipped = true;
            break;
        }

        // The old primary is replaced outright; a sidearm is never a gun in the weapon/back/belt slots, and a
        // gun lying on the floor is never deleted here (CleanupDisplacedPrimary only removes copies of the gun
        // the survivor actually carried).
        if (equippedPrimary is { } replacedGun && Exists(replacedGun))
            Del(replacedGun);

        // No carry slot available: leave the weapon with the survivor rather than silently dropping it.
        if (!equipped && _prototypes.HasIndex<EntityPrototype>(resolved.Firearm))
            Spawn(resolved.Firearm, _transform.GetMoverCoordinates(body));

        SwapMagazineRigsForShells(body, loadout, resolved.Firearm);
        SwapMagazineRigsForMagazines(body, loadout, resolved.Firearm);
        GrantCommanderSidearm(body, loadout, roleProtoId);
        // Spare ammunition is issued last, once the survivor is dressed: the role gear and its webbing are already
        // on the body by the time this runs (ApplyWhenDressed equips the job gear first), so the rigs exist and can
        // accept the magazines. Passing stockAmmo false here left every role with only the round in the weapon.
        NormalizeAmmunition(body, oldPrimary, loadout, resolved.Firearm, roleProtoId, stockAmmo: true);

        // The role gear's own gun (and its random variant) can be displaced by the force-equip a moment
        // later; remove it so the survivor is not left with their old primary on the floor. Only that
        // weapon family is touched, so a dropped sidearm is never deleted.
        if (oldPrimary != null)
            CleanupDisplacedPrimary(body, oldPrimary);

        // Last, once every equip has happened: rig swaps and the sidearm can eject a gear weapon after the earlier
        // sweep has already run.
        PurgeGearFirearms(gearGuns, resolved.Firearm, loadout, body);

        // Nothing is deferred: the loadout has to be complete when this returns. The webbing and its ammunition
        // are fitted and filled synchronously above, and the displaced gear weapon was already swept, so there is no
        // timer to re-run and no pass that depends on the survivor having been alive for a moment.
    }

    /// <summary>
    ///     Clears the construction sheets survivor gear leaves behind. Several survivor kits (the PMC and
    ///     corporate ones especially) carry metal and plasteel in the backpack; when the pack cannot hold the
    ///     whole stack the leftovers land on the floor with the survivor, which reads as free building
    ///     materials at every spawn point. Only loose world sheets next to the body are removed, never
    ///     anything a survivor is carrying.
    /// </summary>
    private void StripDroppedConstructionSheets(EntityUid body)
    {
        var coords = _transform.GetMoverCoordinates(body);
        foreach (var item in _lookup.GetEntitiesInRange<ItemComponent>(coords, 0.75f, LookupFlags.Uncontained))
        {
            var prototype = PrototypeOf(item);
            if (prototype == null)
                continue;

            if (!prototype.StartsWith("CMSheetMetal", StringComparison.Ordinal)
                && !prototype.StartsWith("CMSheetPlasteel", StringComparison.Ordinal))
            {
                continue;
            }

            QueueDel(item);
        }
    }

    /// <summary>Deletes the displaced old primary (and its random variant) if it ended up on the floor.</summary>
    private void TrimExtraFirearms(EntityUid body, SurvivorLoadoutPrototype loadout, string chosen)
    {
        var keptChosen = false;
        var extra = new List<EntityUid>();

        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                CollectExtraFirearms(item, loadout, chosen, extra, ref keptChosen, 0);
        }

        var coords = _transform.GetMoverCoordinates(body);
        foreach (var item in _lookup.GetEntitiesInRange<ItemComponent>(coords, 0.5f, LookupFlags.Uncontained))
        {
            if (!HasComp<GunComponent>(item)
                || !TryComp(item, out MetaDataComponent? meta)
                || meta.EntityPrototype?.ID is not { } id)
            {
                continue;
            }

            if (string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase) && !keptChosen)
            {
                keptChosen = true;
                continue;
            }

            if (IsFactionFirearm(loadout, id) || string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase))
                extra.Add(item);
        }

        foreach (var gun in extra)
        {
            Log.Info($"[SurvivorLoadout] removed extra {PrototypeOf(gun)} beside a survivor who picked {chosen}.");
            Del(gun);
        }
    }

    private void CollectExtraFirearms(EntityUid item, SurvivorLoadoutPrototype loadout, string chosen,
        List<EntityUid> extra, ref bool keptChosen, int depth)
    {
        if (depth > 3)
            return;

        if (HasComp<GunComponent>(item) && TryComp(item, out MetaDataComponent? meta))
        {
            var id = meta.EntityPrototype?.ID;
            var isChosen = id != null && string.Equals(id, chosen, StringComparison.OrdinalIgnoreCase);
            if (isChosen && !keptChosen)
                keptChosen = true;
            else if (isChosen || (id != null && IsFactionFirearm(loadout, id)))
                extra.Add(item);
        }

        // Not every entity carries a container manager; asking for its containers logs an error.
        if (!TryComp(item, out ContainerManagerComponent? manager))
            return;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities.ToArray())
                CollectExtraFirearms(contained, loadout, chosen, extra, ref keptChosen, depth + 1);
        }
    }

    private static bool IsFactionFirearm(SurvivorLoadoutPrototype loadout, string id)
        => loadout.Firearms.Any(firearm => string.Equals(firearm.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the survivor is carrying a gun of this prototype in any slot.</summary>
    private bool CarriesWeapon(EntityUid body, string protoId)
    {
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } item)
                continue;

            if (HasComp<GunComponent>(item)
                && TryComp(item, out MetaDataComponent? meta)
                && string.Equals(meta.EntityPrototype?.ID, protoId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void CleanupDisplacedPrimary(EntityUid body, string oldPrimary)
    {
        if (!Exists(body))
            return;

        var coords = _transform.GetMoverCoordinates(body);
        foreach (var item in _lookup.GetEntitiesInRange<ItemComponent>(coords, 0.5f, LookupFlags.Uncontained))
        {
            if (!TryComp(item, out MetaDataComponent? meta) || meta.EntityPrototype is not { } prototype)
                continue;

            var id = prototype.ID;
            if (string.Equals(id, oldPrimary, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(oldPrimary + "_", StringComparison.OrdinalIgnoreCase))
            {
                Del(item);
            }
        }
    }

    /// <summary>
    ///     The survivor's carried primary: a gun in the suit-storage weapon slot, on the back, or in the belt
    ///     itself. Sidearms kept *inside* a rig's storage are deliberately not considered primary.
    /// </summary>
    private EntityUid? FindEquippedPrimary(EntityUid body)
    {
        string[] order = { "suitstorage", "back", "belt" };
        foreach (var slot in order)
        {
            if (_inventory.TryGetSlotEntity(body, slot, out var item)
                && item is { } gun
                && HasComp<GunComponent>(gun))
            {
                return gun;
            }
        }

        return null;
    }

    /// <summary>
    ///     A gun the role gear left on the floor (a random weapon variant replaces it during initialisation).
    ///     Used only as the ammunition reference, never as something to delete: an uncontained gun within half
    ///     a tile of a group spawn may belong to another survivor.
    /// </summary>
    private EntityUid? FindDroppedGun(EntityUid body)
    {
        var coords = _transform.GetMoverCoordinates(body);
        foreach (var item in _lookup.GetEntitiesInRange<ItemComponent>(coords, 0.5f, LookupFlags.Uncontained))
        {
            if (HasComp<GunComponent>(item))
                return item;
        }

        return null;
    }

    private string? PrototypeOf(EntityUid uid)
        => TryComp(uid, out MetaDataComponent? meta) ? meta.EntityPrototype?.ID : null;

    /// <summary>
    ///     A shotgun pick replaces the survivor's magazine belt and magazine pouches with shell rigs. A belt of
    ///     rifle magazines is dead weight when the weapon takes loose shells, so the kit is re-issued for shells.
    /// </summary>
    private void SwapMagazineRigsForShells(EntityUid body, SurvivorLoadoutPrototype loadout, string chosen)
    {
        var ammo = PrimaryAmmo(loadout, chosen);
        if (!SurvivorAmmoRig.LoadsLooseAmmo(ammo?.Id))
            return;

        foreach (var slot in SurvivorAmmoRig.MagazineSlots)
        {
            if (!_inventory.TryGetSlotEntity(body, slot, out var rig) || rig is not { } old)
                continue;

            if (!SurvivorAmmoRig.IsMagazineRig(MetaData(old).EntityPrototype?.ID))
                continue;

            var replacement = SurvivorAmmoRig.RigKindForSlot(slot) == "belt"
                ? loadout.ShotgunBelt.Id
                : loadout.ShotgunPouch.Id;
            ApplyToSlot(body, slot, replacement);
        }

        if (loadout.ShotgunWebbing is { } webbing)
            SwapWebbing(body, loadout, webbing, chosen);
    }

    /// <summary>
    ///     Replaces the worn webbing with a variant that carries its own ammunition. The replacement is inserted
    ///     before the original is removed, so a refused insert cannot leave the survivor with no webbing at all.
    /// </summary>
    private bool SwapWebbing(EntityUid body, SurvivorLoadoutPrototype loadout, ProtoId<EntityPrototype> prototype,
        string chosen)
    {
        foreach (var candidate in AmmoHolders(body))
        {
            if (!IsWebbing(candidate))
                continue;

            if (!_container.TryGetContainingContainer((candidate, null, null), out var container))
            {
                Log.Warning($"[SurvivorLoadout] webbing {PrototypeOf(candidate)} is in no container");
                continue;
            }

            if (PrototypeOf(candidate) != prototype.Id)
            {
                // The webbing slot takes one item, so the original has to be freed before the replacement goes in.
                _container.Remove(candidate, container);

                // A worn webbing keeps its rounds in the clothing's storage, so deleting the webbing item leaves them
                // behind: clear the layer that is actually in use.
                var live = LiveStorageContainer(container);
                if (live != null)
                {
                    foreach (var stale in live.ContainedEntities.ToArray())
                        QueueDel(stale);
                }

                var replacement = Spawn(prototype, _transform.GetMoverCoordinates(body));
                if (!_container.Insert(replacement, container))
                {
                    Log.Warning(
                        $"[SurvivorLoadout] inserting {prototype.Id} into {container.ID} failed; restoring {PrototypeOf(candidate)}");
                    QueueDel(replacement);
                    _container.Insert(candidate, container);
                    continue;
                }

                QueueDel(candidate);
            }

            // Filled here and now: the loadout must be complete the moment it is applied, so nothing is left to a
            // prototype fill that runs later or to a delayed pass.
            FillWebbing(body, container, loadout, chosen);
            return true;
        }

        Log.Warning("[SurvivorLoadout] no worn webbing found to re-issue");
        return false;
    }

    /// <summary>
    ///     Puts the survivor's issue of ammunition into the worn webbing straight away.
    /// </summary>
    private void FillWebbing(EntityUid body, BaseContainer slot, SurvivorLoadoutPrototype loadout, string chosen)
    {
        if (PrimaryAmmo(loadout, chosen) is not { } type)
            return;

        var target = LiveStorageContainer(slot) ?? slot;

        var already = 0;
        foreach (var item in target.ContainedEntities)
        {
            if (PrototypeOf(item) == type.Id)
                already++;
        }

        for (var i = already; i < WebbingAmmoIssue; i++)
        {
            var rounds = Spawn(type, _transform.GetMoverCoordinates(body));
            if (!_container.Insert(rounds, target))
            {
                Log.Warning(
                    $"[SurvivorLoadout] the webbing refused {type.Id} ({i + 1}/{WebbingAmmoIssue}); it holds {already + i}");
                QueueDel(rounds);
                break;
            }
        }
    }

    /// <summary>The storage container a worn webbing's contents actually live in.</summary>
    private BaseContainer? LiveStorageContainer(BaseContainer slot)
    {
        if (TryComp(slot.Owner, out StorageComponent? hostStorage) && hostStorage.Container is { } live)
            return live;

        foreach (var item in slot.ContainedEntities)
        {
            if (TryComp(item, out StorageComponent? own) && own.Container is { } ownContainer)
                return ownContainer;
        }

        return null;
    }

    /// <summary>How many ammunition items a survivor's webbing is issued.</summary>
    private const int WebbingAmmoIssue = 3;

    /// <summary>
    ///     Swaps a couple of the survivor's standard magazines for the weapon's armour-piercing variant, if
    ///     that weapon has one (VAL AP, M39 AP). Weapons without an AP magazine are left untouched.
    /// </summary>
    private void UpgradeMagazinesToArmorPiercing(EntityUid body, SurvivorLoadoutPrototype loadout, string chosen)
    {
        var standard = PrimaryAmmo(loadout, chosen);
        if (standard == null)
            return;

        var ap = SurvivorAmmoUpgrade.FindArmorPiercingVariant(standard.Value.Id, ArmorPiercingCandidates());
        if (ap == null)
            return;

        var found = new List<(EntityUid Magazine, BaseContainer? Container)>();
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                CollectExactMagazine(item, standard.Value.Id, found, 0);
        }

        var count = SurvivorAmmoUpgrade.MagazinesToUpgrade(found.Count, _random.Next);
        for (var i = 0; i < count; i++)
        {
            var (old, container) = found[i];
            var replacement = Spawn(ap, _transform.GetMoverCoordinates(body));

            // The slot has to be freed before the AP round can take it, and the standard magazine has to go back if
            // the replacement does not fit: deleting first lost a magazine whenever the rig was full (four stocked,
            // three carried).
            if (container != null && _container.Remove(old, container))
            {
                if (_container.Insert(replacement, container))
                {
                    Del(old);
                    continue;
                }

                _container.Insert(old, container);
                QueueDel(replacement);
                continue;
            }

            QueueDel(replacement);
            if (GiveMagazine(body, ap))
            {
                Del(old);
                continue;
            }

            Log.Warning($"Could not upgrade {PrototypeOf(old)} to {ap}: nowhere to put the replacement, " +
                        "keeping the standard magazine.");
        }

        if (count > 0)
            Log.Debug($"Upgraded {count} magazine(s) of {standard.Value.Id} to {ap} for a survivor.");
    }

    /// <summary>Every magazine prototype the game knows about, cached.</summary>
    private List<string>? _magazineCandidates;

    private List<string> MagazineCandidates()
    {
        if (_magazineCandidates != null)
            return _magazineCandidates;

        _magazineCandidates = new List<string>();
        foreach (var prototype in _prototypes.EnumeratePrototypes<EntityPrototype>())
        {
            if (prototype.Components.ContainsKey("BallisticAmmoProvider"))
                _magazineCandidates.Add(prototype.ID);
        }

        return _magazineCandidates;
    }

    private List<string> ArmorPiercingCandidates()
    {
        var candidates = new List<string>();
        foreach (var id in MagazineCandidates())
        {
            if (SurvivorAmmoUpgrade.IsArmorPiercing(id))
                candidates.Add(id);
        }

        return candidates;
    }

    /// <summary>Collects carried items whose prototype is exactly the given magazine.</summary>
    private void CollectExactMagazine(EntityUid item, string magazine,
        List<(EntityUid Magazine, BaseContainer? Container)> found, int depth)
    {
        if (PrototypeOf(item) == magazine && HasComp<BallisticAmmoProviderComponent>(item))
        {
            _container.TryGetContainingContainer(item, out var container);

            // A round already seated in the weapon is not a spare: counting it made the stocking pass believe the
            // survivor was already carrying a magazine when they only had the one chambered in the gun.
            if (container == null || !HasComp<GunComponent>(container.Owner))
                found.Add((item, container));
        }

        if (depth >= MaxStorageDepth || !TryComp(item, out StorageComponent? storage))
            return;

        // Storage containers only. Walking every container would also reach the kit's own webbing and let the
        // replacement pass delete ammunition the survivor is supposed to keep.
        foreach (var contained in storage.Container.ContainedEntities)
            CollectExactMagazine(contained, magazine, found, depth + 1);
    }

    /// <summary>
    ///     Converts the ammunition of the weapon being replaced into ammunition for the chosen primary.
    ///     Sidearm magazines and anything that does not belong to the old primary are left alone.
    /// </summary>
    private void ReplacePrimaryAmmo(EntityUid body, string? oldPrimary, SurvivorLoadoutPrototype loadout,
        string chosen)
    {
        if (oldPrimary == null || !_prototypes.HasIndex<EntityPrototype>(chosen))
            return;

        var oldTags = MagazineTags(oldPrimary);
        var newAmmo = PrimaryAmmo(loadout, chosen);
        if (oldTags.Count == 0 || newAmmo == null)
            return;

        var stale = new List<(EntityUid Magazine, BaseContainer? Container)>();
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } item)
                continue;

            CollectPrimaryAmmo(item, oldTags, stale, 0);
        }

        foreach (var (old, container) in stale)
        {
            // Delete only: the stocking pass issues exactly the kit's ammunition into the rig. Handing out a
            // replacement here gave a second copy for every magazine converted.
            Del(old);
        }
    }

    /// <summary>Magazine tags of a weapon's feeding slot (or its internal ammo whitelist).</summary>
    private List<ProtoId<TagPrototype>> MagazineTags(string firearm)
    {
        var tags = new List<ProtoId<TagPrototype>>();
        if (!_prototypes.TryIndex<EntityPrototype>(firearm, out var gun))
            return tags;

        var probe = Spawn(firearm, MapCoordinates.Nullspace);
        try
        {
            if (HasComp<ItemSlotsComponent>(probe)
                && _itemSlots.TryGetSlot(probe, "gun_magazine", out var magazineSlot)
                && magazineSlot?.Whitelist?.Tags is { Count: > 0 } slotTags)
            {
                tags.AddRange(slotTags);

                // The slot whitelist can name the gun's own tag rather than the magazine's, which made the weapon's
                // own charge packs look foreign and stripped them. The issue magazine's tags are the reliable part.
                if (magazineSlot?.StartingItem is { } startingItem)
                    AddPrototypeTags(startingItem, tags);
            }
            else if (TryComp(probe, out BallisticAmmoProviderComponent? ammo) && ammo.Whitelist?.Tags != null)
            {
                tags.AddRange(ammo.Whitelist.Tags);
            }
            else if (TryComp(probe, out RevolverAmmoProviderComponent? revolver)
                     && revolver.Whitelist?.Tags is { Count: > 0 } revolverTags)
            {
                // A revolver has no magazine slot and no ballistic provider: its cylinder is fed by speed loaders,
                // which looked foreign to the strip pass and were deleted from the commander's belt.
                tags.AddRange(revolverTags);
            }
        }
        finally
        {
            QueueDel(probe);
        }

        return tags;
    }

    /// <summary>The chosen primary's magazine, or its cartridge for weapons that load directly.</summary>
    private EntProtoId? PrimaryAmmo(SurvivorLoadoutPrototype loadout, string firearm)
    {
        // A weapon that loads loose ammunition (shotgun shells) declares it outright: it has no magazine
        // slot and its internal cartridge is null, so without this the old weapon's mags would survive.
        if (loadout.FirearmAmmo.TryGetValue(firearm, out var explicitAmmo))
            return explicitAmmo;

        if (!_prototypes.TryIndex<EntityPrototype>(firearm, out _))
            return null;

        var probe = Spawn(firearm, MapCoordinates.Nullspace);
        try
        {
            if (HasComp<ItemSlotsComponent>(probe)
                && _itemSlots.TryGetSlot(probe, "gun_magazine", out var magazineSlot)
                && magazineSlot?.StartingItem is { } magazine)
            {
                return StandardVariantOf(magazine);
            }

            if (TryComp(probe, out BallisticAmmoProviderComponent? ammo) && ammo.Proto is { } proto)
                return StandardVariantOf(proto);

            return null;
        }
        finally
        {
            QueueDel(probe);
        }
    }

    private void CollectPrimaryAmmo(EntityUid item, List<ProtoId<TagPrototype>> oldTags,
        List<(EntityUid Magazine, BaseContainer? Container)> stale, int depth)
    {
        // Magazines feeding a different weapon (a holstered sidearm, say) are not touched.
        if (HasComp<BallisticAmmoProviderComponent>(item)
            && !HasComp<GunComponent>(item)
            && _tagSystem.HasAnyTag(item, oldTags))
        {
            _container.TryGetContainingContainer(item, out var container);
            stale.Add((item, container));
        }

        if (depth >= MaxStorageDepth || !TryComp(item, out StorageComponent? storage))
            return;

        // Storage containers only - see CollectExactMagazine: the worn webbing's contents must survive.
        foreach (var contained in storage.Container.ContainedEntities)
            CollectPrimaryAmmo(contained, oldTags, stale, depth + 1);
    }

    /// <summary>
    ///     Puts a magazine into the survivor's backpack, then any other storage they wear (webbing, pouches, belt).
    ///     Returns false when nothing has room, so the caller can stop instead of emptying the rest onto the floor -
    ///     large-calibre magazines (12.7x55mm and friends) do not fit webbing at all.
    /// </summary>
    private bool GiveMagazine(EntityUid body, EntProtoId magazine)
    {
        var spawned = Spawn(magazine, _transform.GetMoverCoordinates(body));
        if (GiveMagazine(body, spawned))
            return true;

        QueueDel(spawned);
        return false;
    }

    /// <summary>
    ///     Ensures the weapon the survivor picked has a magazine in it. Some prototypes ship with no starting
    ///     magazine (the M39 among them), which left the survivor holding an empty gun.
    /// </summary>
    private void LoadPrimaryWeapon(EntityUid body, EntProtoId magazine, string chosen)
    {
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } item
                || !string.Equals(PrototypeOf(item), chosen, StringComparison.OrdinalIgnoreCase)
                || !TryComp(item, out ItemSlotsComponent? itemSlots))
            {
                continue;
            }

            foreach (var (slotId, _) in itemSlots.Slots)
            {
                if (!slotId.StartsWith("gun_magazine", StringComparison.OrdinalIgnoreCase)
                    || !_itemSlots.TryGetSlot((item, itemSlots), slotId, out var magazineSlot)
                    || magazineSlot.HasItem)
                {
                    continue;
                }

                var loaded = Spawn(magazine, _transform.GetMoverCoordinates(item));
                if (!_itemSlots.TryInsert(item, slotId, loaded, null))
                    QueueDel(loaded);
                else
                    Log.Info($"[SurvivorLoadout] loaded {magazine.Id} into {chosen}.");

                return;
            }
        }
    }

    /// <summary>
    ///     Whether a worn item is meant to carry ammunition: a belt, a pouch, webbing or a pack. A uniform's
    ///     accessory pockets and a boot sheath are not - magazines stored there are lost to the player.
    /// </summary>
    /// <summary>
    ///     The survivor's ammunition rigs, best first. Includes webbing fitted inside the uniform, which is a
    ///     container on a worn item rather than an inventory slot, so a slot-only walk can never reach it.
    /// </summary>
    private List<EntityUid> AmmoHolders(EntityUid body)
    {
        var holders = new List<EntityUid>();
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } worn)
                continue;

            // Slot-level rigs: belt, pouches, pack.
            if (IsAmmoHolder(worn) && HasComp<StorageComponent>(worn) && !holders.Contains(worn))
                holders.Add(worn);

            // Rigs fitted inside worn clothing, notably the webbing on a uniform.
            CollectNestedAmmoHolders(worn, holders, 0);
        }

        holders.Sort((a, b) =>
        {
            var aWebbing = IsWebbing(a) ? 0 : 1;
            var bWebbing = IsWebbing(b) ? 0 : 1;
            return aWebbing.CompareTo(bWebbing);
        });

        return holders;
    }

    private bool IsWebbing(EntityUid holder)
        => MetaData(holder).EntityPrototype?.ID is { } id
           && id.Contains("Webbing", StringComparison.OrdinalIgnoreCase);

    private void CollectNestedAmmoHolders(EntityUid item, List<EntityUid> holders, int depth)
    {
        if (depth > MaxStorageDepth || !TryComp(item, out ContainerManagerComponent? containers))
            return;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
            {
                if (IsAmmoHolder(contained) && HasComp<StorageComponent>(contained) && !holders.Contains(contained))
                    holders.Add(contained);

                CollectNestedAmmoHolders(contained, holders, depth + 1);
            }
        }
    }

    /// <summary>Adds the tags a prototype carries, by spawning a throwaway instance to read them.</summary>
    private void AddPrototypeTags(string prototype, List<ProtoId<TagPrototype>> tags)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(prototype))
            return;

        var probe = Spawn(prototype, MapCoordinates.Nullspace);
        try
        {
            if (TryComp(probe, out TagComponent? tagComponent))
                tags.AddRange(tagComponent.Tags);
        }
        finally
        {
            QueueDel(probe);
        }
    }

    private bool IsAmmoHolder(EntityUid holder)
    {
        if (!TryComp(holder, out MetaDataComponent? meta) || meta.EntityPrototype?.ID is not { } id)
            return false;

        return id.Contains("Belt", StringComparison.OrdinalIgnoreCase)
               || id.Contains("Pouch", StringComparison.OrdinalIgnoreCase)
               || id.Contains("Webbing", StringComparison.OrdinalIgnoreCase)
               || id.Contains("Backpack", StringComparison.OrdinalIgnoreCase)
               || id.Contains("Satchel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The worn holders that can take magazines, belt first and then everything else that has storage. The slot
    ///     names differ per faction (pouch / pouch1 / pocket1), so the holders are found by what they are rather
    ///     than by guessing slot names.
    /// </summary>
    private List<EntityUid> RigHolders(EntityUid body)
    {
        var holders = new List<EntityUid>();
        if (_inventory.TryGetSlotEntity(body, "belt", out var belt) && belt is { } beltRig)
            holders.Add(beltRig);

        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All & ~SlotFlags.BACK);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } item
                || holders.Contains(item)
                || !HasComp<StorageComponent>(item))
            {
                continue;
            }

            holders.Add(item);
        }

        return holders;
    }

    /// <summary>Puts one magazine into a specific holder, storage first and its storage container second.</summary>
    private bool GiveMagazineTo(EntityUid body, EntityUid holder, EntProtoId magazine)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(magazine))
            return false;

        var spawned = Spawn(magazine, _transform.GetMoverCoordinates(body));

        if (TryComp(holder, out StorageComponent? storage))
        {
            if (_storage.Insert(holder, spawned, out _, storageComp: storage, playSound: false))
            {
                Log.Debug($"Stored {PrototypeOf(spawned)} in {PrototypeOf(holder)} (storage).");
                return true;
            }

            // Only the rig's own storage container may be used. Writing into whatever container the rig sits in
            // put rounds somewhere the counting pass does not read, so each pass looked empty and added another
            // full issue on top of the last.
            if (storage.Container is { } ownContainer && _container.Insert(spawned, ownContainer))
            {
                Log.Info($"[SurvivorLoadout] stored {PrototypeOf(spawned)} in {PrototypeOf(holder)} (rig container).");
                return true;
            }
        }

        if (TryComp(holder, out StorageComponent? packStorage)
            && packStorage.Container != null
            && _container.Insert(spawned, packStorage.Container))
        {
            Log.Info($"[SurvivorLoadout] stored {PrototypeOf(spawned)} in {PrototypeOf(holder)} (storage container).");
            return true;
        }

        QueueDel(spawned);
        return false;
    }

    private bool GiveMagazine(EntityUid body, EntityUid spawned)
    {
        // Organic rigs first: the webbing and belts the survivor actually wears, wherever they are attached.
        foreach (var holder in AmmoHolders(body))
        {
            if (TryComp(holder, out StorageComponent? holderStorage)
                && _storage.Insert(holder, spawned, out _, storageComp: holderStorage, playSound: false))
            {
                Log.Debug($"Stored {PrototypeOf(spawned)} in {PrototypeOf(holder)} (rig).");
                return true;
            }
        }

        // No rig at all: the survivor is not dressed yet, so leave the round unstocked and let the delayed pass
        // place it once the webbing exists. Writing it into a uniform or helmet is what scattered rounds before.
        // Last resort: a worn container that is not a recognised rig (uniform storage, for instance). A role with a
        // medical belt and a full pack still has to end up with its ammunition rather than silently losing it.
        var worn = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (worn.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } containerItem || containerItem == spawned)
                continue;

            if (TryComp(containerItem, out StorageComponent? containerStorage)
                && _storage.Insert(containerItem, spawned, out _, storageComp: containerStorage, playSound: false))
            {
                Log.Debug($"Stored {PrototypeOf(spawned)} in {PrototypeOf(containerItem)} (fallback container).");
                return true;
            }
        }

        // Nothing could take it: say so loudly instead of leaving the survivor quietly short of ammunition.
        Log.Warning($"[SurvivorLoadout] no worn container accepted {PrototypeOf(spawned)}; the survivor is short of ammunition.");
        return false;
    }

    /// <summary>Every gun the survivor is carrying or wearing, wherever it sits.</summary>
    private IEnumerable<EntityUid> CollectContained(EntityUid body)
    {
        var found = new List<EntityUid>();
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                CollectGuns(item, found, 0);
        }

        return found;
    }

    private void CollectGuns(EntityUid item, List<EntityUid> found, int depth)
    {
        if (depth > 3)
            return;

        if (HasComp<GunComponent>(item))
            found.Add(item);

        if (!TryComp(item, out ContainerManagerComponent? manager))
            return;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities.ToArray())
                CollectGuns(contained, found, depth + 1);
        }
    }

    private bool ApplyToSlot(EntityUid body, string slot, string? protoId)
    {
        if (protoId == null || !_prototypes.HasIndex<EntityPrototype>(protoId))
            return false;

        if (!_inventory.TryGetSlot(body, slot, out _))
            return false;

        // The old occupant is removed with force and reparented by the container system, which can leave it inside
        // a worn container instead of on the floor, so its ownership is not assumed here. It is deleted outright
        // only when it is genuinely free-standing, and otherwise left exactly where the inventory put it.
        EntityUid? previous = null;
        if (_inventory.TryGetSlotEntity(body, slot, out var existing) && existing is { } old)
        {
            previous = old;
            if (!_inventory.TryUnequip(body, slot, out var removed, silent: true, force: true))
            {
                Log.Error($"[SurvivorLoadout] could not clear {slot} before equipping {protoId}.");
                return false;
            }

            if (removed is { } removedItem)
                previous = removedItem;
        }

        var coords = _transform.GetMoverCoordinates(body);
        var item = Spawn(protoId, coords);
        if (!_inventory.TryEquip(body, item, slot, silent: true, force: true))
        {
            _transform.SetCoordinates(item, coords);
            Log.Error($"[SurvivorLoadout] {protoId} would not equip into {slot}.");

            if (previous is { } restore && Exists(restore) && !_container.IsEntityInContainer(restore))
                _inventory.TryEquip(body, restore, slot, silent: true, force: true);

            return false;
        }

        // Verify the slot really holds the replacement. Equipping is not the same as occupying the slot: a later
        // operation, or a forced equip elsewhere, can displace it, and reporting that is far better than assuming.
        if (!_inventory.TryGetSlotEntity(body, slot, out var occupant) || occupant != item)
        {
            Log.Error($"[SurvivorLoadout] {slot} does not hold the issued {protoId} right after equipping " +
                      $"(holds {(occupant is { } other ? PrototypeOf(other) : "nothing")}).");

            return false;
        }

        // The replaced item is deleted only when it is not sitting inside a container: an item that the inventory
        // moved into a worn pouch is not ours to destroy.
        if (previous is { } replaced && Exists(replaced) && replaced != item
            && !_container.IsEntityInContainer(replaced))
        {
            Del(replaced);
        }

        return true;
    }

    /// <summary>
    ///     Deletes the guns the role's gear supplied once the loadout has issued the chosen weapon. A survivor
    ///     carries the weapon they picked; a gear gun is not left in a slot, and not forced to the floor either.
    /// </summary>
    private void PurgeGearFirearms(IReadOnlyCollection<EntityUid> gearGuns, string chosen,
        SurvivorLoadoutPrototype loadout, EntityUid body)
    {
        var removed = 0;
        foreach (var gun in gearGuns)
        {
            if (!Exists(gun) || !HasComp<GunComponent>(gun))
                continue;

            // Provenance, not prototype: the BEAR gear preset can roll the very weapon the player picked, and a
            // prototype comparison kept that copy alongside the issued one.
            if (_chosenPrimary is { } issued && gun == issued)
                continue;

            if (_grantedSidearms.Contains(gun))
                continue;

            // A pistol inside a holster, belt or pack is the survivor's sidearm, not leftover gear - unless it is
            // another copy of the weapon they were issued, which is a duplicate of their primary.
            if (_container.IsEntityInContainer(gun)
                && !string.Equals(PrototypeOf(gun), chosen, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Del(gun);
            removed++;
        }

        // A role's starting gear can hand over a weapon in a hand as well as in a slot, which left the survivor
        // holding one gun and wearing another.
        if (TryComp<HandsComponent>(body, out var hands))
        {
            foreach (var handId in hands.Hands.Keys.ToList())
            {
                if (!_hands.TryGetHeldItem((body, hands), handId, out var held)
                || held is not { } heldItem
                    || !HasComp<GunComponent>(heldItem)
                    || _grantedSidearms.Contains(heldItem))
                {
                    continue;
                }

                if (_chosenPrimary is { } issuedGun && heldItem == issuedGun)
                    continue;

                Del(heldItem);
                removed++;
            }
        }

        // A gear gun that the equip ejected is no longer in any slot, so the snapshot no longer reaches it. Only
        // loose guns on the survivor's own tile are considered, and only if they are not the weapon this survivor
        // was given - their own pick, or a sidearm issued with it. Anybody else's gear is in a container, not here.
        var coords = _transform.GetMoverCoordinates(body);
        foreach (var near in _lookup.GetEntitiesInRange<ItemComponent>(coords, 1f, LookupFlags.Uncontained))
        {
            if (!HasComp<GunComponent>(near) || _grantedSidearms.Contains(near))
                continue;


            // The weapon this survivor was issued is never swept, even while its prototype is still resolving.
            if (_chosenPrimary is { } chosenEntity && near.Owner == chosenEntity)
                continue;

            Del(near);
            removed++;
        }

        if (removed > 0)
            Log.Info($"[SurvivorLoadout] removed {removed} gear firearm(s) for a survivor who picked {chosen}.");
    }

    /// <summary>
    ///     Re-issues the rig when the chosen firearm's magazines do not fit the standard kit: the AH-12's
    ///     12.7x55mm drums need the machinegunner belt and drum pouch.
    /// </summary>
    private void SwapMagazineRigsForMagazines(EntityUid body, SurvivorLoadoutPrototype loadout, string chosen)
    {
        if (!loadout.FirearmRigs.TryGetValue(chosen, out var rigs))
            return;

        // Every slot that holds a magazine rig is re-issued, so a kit that carries its magazines in a pouch on an
        // arbitrary slot (the BEAR PM-74) does not keep them for a weapon they no longer feed.
        foreach (var slot in SurvivorAmmoRig.MagazineSlots)
        {
            if (!_inventory.TryGetSlotEntity(body, slot, out var rig) || rig is not { } old)
                continue;

            if (!SurvivorAmmoRig.IsMagazineRig(MetaData(old).EntityPrototype?.ID))
                continue;

            var replacement = SurvivorAmmoRig.RigKindForSlot(slot) == "belt" ? rigs.Belt : rigs.Pouch;
            if (replacement is { } proto)
                ApplyToSlot(body, slot, proto.Id);
        }
    }

    /// <summary>
    ///     One pass over the survivor's kit: chambers the weapon they picked, replaces magazines that no longer
    ///     feed it, and tops the rig up to <see cref="SpareMagazines"/> - never beyond what the rig accepts, and
    ///     never onto the floor.
    /// </summary>
    private void NormalizeAmmunition(EntityUid body, string? oldPrimary, SurvivorLoadoutPrototype loadout,
        string chosen, string? roleProtoId, bool stockAmmo = true)
    {
        // A kit that issues a pre-filled webbing supplies its own ammunition: touching it at all here would strip
        // those rounds as "foreign" (a shotgun has no magazine family to match them against) and empty the webbing.
        if (loadout.ShotgunWebbing != null
            && SurvivorAmmoRig.LoadsLooseAmmo(PrimaryAmmo(loadout, chosen)?.Id))
        {
            return;
        }

        var family = MagazineTags(chosen);
        var ammo = PrimaryAmmo(loadout, chosen);

        // Retention is decided per weapon, not per tag family: every rifle magazine shares the generic
        // CMMagazineRifle tag, so an M54C magazine and a MAR-50 drum looked identical to a family test and the
        // survivor kept the magazine their old kit shipped. A gun the survivor carries - the weapon they picked
        // and any holstered sidearm - keeps only the rounds its own whitelist accepts.
        var fitsCarriedGun = BuildAmmoFitTest(body);

        // A weapon that takes loose ammunition has no magazine family; its rigs are handled separately.
        // Repeat until nothing foreign remains: inserting the replacement while walking the webbing can leave a
        // pack behind on the first sweep, which then stayed in the webbing next to the new ammunition.
        // Rounds are cleared against what the carried guns actually accept, not against the chosen weapon's tag
        // family: a weapon with an empty magazine well (the M39 ships none) declares no family, and skipping the
        // strip left the kit's magazines for the old weapon sitting in the magazine pouch - which then had no room
        // for the chosen weapon's ammunition and was not even considered a target.
        if (ammo is { } replacement)
        {
            StripForeignMagazines(body, fitsCarriedGun, replacement);
            StripForeignFromRigs(body, fitsCarriedGun, replacement);
        }

        LoadCarriedWeapons(body);

        if (ammo is not { } type)
            return;

        var carried = CountMagazines(body, family);
        var added = 0;

        // One rig, one rule. The ammunition the chosen weapon takes goes into the rig nearest the survivor (webbing
        // first), counted as items inside that rig so repeated passes are idempotent: the kit issues three, and a
        // pass that finds three does nothing. Counting rounds or measuring the whole body made earlier passes add a
        // fresh issue each time, which is what piled ammunition into unrelated storage.
        if (!stockAmmo)
            return;

        // Do not skip stocking just because the kit nominates a webbing variant: the swap only succeeds when the
        // webbing is already worn, and in the live spawn the uniform can arrive after this pass. When that happens the
        // survivor keeps the charge-pack webbing, those packs are stripped as foreign, and skipping the stock left
        // the webbing empty. Counting what is carried keeps a successful variant fill from being duplicated.

        // Count this rig only, across both layers it can live in: the rig's own storage and the storage of the
        // clothing it is fitted to. Counting the whole body counted the shells the buckshot belt is pre-filled with;
        // counting only the rig subtree missed magazines held in the clothing layer.
        // The first ammo holder is not necessarily able to take this round. A pistol-holster webbing or a medical
        // rig counts as an ammo holder by name but refuses a rifle/SMG magazine, and targeting it stopped the pass
        // at the first insert: the W-Y PMC Field Ops Leader's M39/2 kept only its own magazine while his ammunition
        // belt stayed empty, even though that belt accepts M39 magazines. Stock the first rig that accepts them.
        // A magazine rig that still carries the kit's magazines for the previous weapon cannot take this weapon's
        // rounds, and the fit test above legitimately keeps them when the survivor also carries that weapon - so the
        // rig stays full and drops out of the candidates. The rig exists to feed the chosen primary, so anything in
        // it that is not this round is cleared first.
        ClearMagazineRigs(body, type.Id);

        // Rigs that will take this round, with a rig built for magazines preferred over a general webbing pouch: the
        // uniform's webbing pouch is first in the holder order and swallows a single magazine, which used to end the
        // pass before the commander's magazine pouch was reached.
        var candidates = AmmoHolders(body).Where(holder => CanStore(holder, type)).ToList();
        var target = candidates.FirstOrDefault(holder =>
            PrototypeOf(holder)?.Contains("Magazine", StringComparison.OrdinalIgnoreCase) == true);
        if (target == default)
            target = candidates.FirstOrDefault();

        var issue = MagazineIssueFor(loadout, roleProtoId);

        if (target == default)
        {
            Log.Warning($"A survivor using {chosen} has no rig that accepts {type.Id}.");
        }
        else
        {
            // Count what every rig already holds, so a kit that ships a pre-filled webbing is not topped up twice.
            var have = candidates.Sum(holder => CountRigAmmo(body, holder, type.Id));
            for (var i = have; i < issue; i++)
            {
                // The chosen rig is the only destination. Spilling into whatever else would take the round scattered
                // ammunition into the uniform webbing and the pack; a rig that cannot hold the issue says so instead.
                if (!GiveMagazineTo(body, target, type))
                {
                    Log.Warning($"{PrototypeOf(target)} could not take another {type.Id} " +
                                $"({CountRigAmmo(body, target, type.Id)} of {issue} stocked).");
                    break;
                }

                added++;
            }
        }

        LoadPrimaryWeapon(body, type, chosen);

        Log.Info($"[SurvivorLoadout] {chosen}: carried {carried} {type.Id}, stocked {added}.");

        UpgradeMagazinesToArmorPiercing(body, loadout, chosen);

        if (carried == 0 && added == 0)
            DiagnoseMagazineStorage(body, type, chosen, oldPrimary);
    }

    /// <summary>Chambers a magazine in every gun the survivor is carrying that has none.</summary>
    private void LoadCarriedWeapons(EntityUid body)
    {
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                LoadWeapon(item, 0);
        }
    }

    private void LoadWeapon(EntityUid item, int depth)
    {
        if (depth > 3)
            return;

        if (HasComp<GunComponent>(item)
            && HasComp<ItemSlotsComponent>(item)
            && _itemSlots.TryGetSlot(item, "gun_magazine", out var slot)
            && slot is { StartingItem: { } starting })
        {
            var magazine = Spawn(starting, _transform.GetMoverCoordinates(item));
            if (!_itemSlots.TryInsert(item, "gun_magazine", magazine, null))
                QueueDel(magazine);
        }

        // Not every entity carries a container manager; asking for its containers logs an error.
        if (!TryComp(item, out ContainerManagerComponent? manager))
            return;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
                LoadWeapon(contained, depth + 1);
        }
    }

    /// <summary>
    ///     Adds the magazine families of every gun the survivor carries, so their ammunition survives
    ///     <see cref="StripForeignMagazines"/>.
    /// </summary>
    private void CollectCarriedGunFamilies(EntityUid body, List<ProtoId<TagPrototype>> into, string? exclude = null)
    {
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                CollectCarriedGunFamilies(item, into, 0, exclude);
        }
    }

    private void CollectCarriedGunFamilies(EntityUid item, List<ProtoId<TagPrototype>> into, int depth,
        string? exclude = null)
    {
        if (depth > 3)
            return;

        if (HasComp<GunComponent>(item)
            && TryComp(item, out MetaDataComponent? meta)
            && meta.EntityPrototype?.ID is { } proto
            && !string.Equals(proto, exclude, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var tag in MagazineTags(proto))
            {
                if (!into.Contains(tag))
                    into.Add(tag);
            }
        }

        // Not every entity carries a container manager; asking for its containers logs an error.
        if (!TryComp(item, out ContainerManagerComponent? manager))
            return;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
                CollectCarriedGunFamilies(contained, into, depth + 1, exclude);
        }
    }

    /// <summary>A magazine: it feeds a gun but is not one.</summary>
    private bool IsMagazine(EntityUid item)
    {
        // A gun is not a magazine, and neither is a rig: "magazine pouch" and "magazine belt" are containers, and
        // treating them as magazines had the ammunition pass strip them off the survivor entirely.
        if (HasComp<GunComponent>(item) || HasComp<StorageComponent>(item))
            return false;

        return HasComp<BallisticAmmoProviderComponent>(item)
               || PrototypeOf(item)?.Contains("Magazine", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool InFamily(EntityUid item, IReadOnlyList<ProtoId<TagPrototype>> family)
        => family.Count > 0 && _tagSystem.HasAnyTag(item, family);

    /// <summary>
    ///     Replaces magazines that no longer feed the chosen weapon with ones that do, in the same container, so a
    ///     survivor never keeps another weapon's magazines and never finds them on the ground.
    /// </summary>
    private void StripForeignMagazines(EntityUid body, Func<EntityUid, bool> fitsCarriedGun,
        EntProtoId replacement)
    {
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                StripForeignMagazines(item, fitsCarriedGun, replacement, 0);
        }
    }

    private void StripForeignMagazines(EntityUid item, Func<EntityUid, bool> fitsCarriedGun,
        EntProtoId replacement, int depth)
    {
        if (depth > 3)
            return;

        if (IsMagazine(item) && !fitsCarriedGun(item))
        {
            // Delete only. Spawning a replacement here put a second copy of the round wherever the old magazine
            // happened to sit (uniform storage, pack), and the stocking pass then added the issued amount on top.
            QueueDel(item);
            return;
        }

        // Storage containers only. Walking every container also reached the kit's own webbing and deleted the
        // charge packs the survivor keeps, which is what left the webbing empty.
        if (!TryComp(item, out StorageComponent? storage))
            return;

        foreach (var contained in storage.Container.ContainedEntities.ToArray())
            StripForeignMagazines(contained, fitsCarriedGun, replacement, depth + 1);
    }

    /// <summary>Counts magazines the survivor carries, by tag family rather than exact prototype.</summary>
    /// <summary>
    ///     Says why a survivor ended up with no magazines: for every worn holder, whether it has storage at all and
    ///     whether a container on it would accept the magazine. Logged at Error level so it reaches the console -
    ///     Log.Debug is filtered out unless the server runs with debug logging.
    /// </summary>
    private void DiagnoseMagazineStorage(EntityUid body, EntProtoId ammo, string chosen, string? oldPrimary)
    {
        var probe = Spawn(ammo, _transform.GetMoverCoordinates(body));
        var holders = new List<string>();

        try
        {
            var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
            while (slots.MoveNext(out var slot))
            {
                if (slot.ContainedEntity is not { } holder)
                    continue;

                var proto = PrototypeOf(holder) ?? "?";
                var where = $"slot{holders.Count}";
                if (!HasComp<StorageComponent>(holder))
                {
                    holders.Add($"{where}={proto} (holds no storage)");
                    continue;
                }

                var accepted = false;
                var containers = 0;
                // Not every entity carries a container manager; asking for its containers logs an error.
                if (!TryComp(holder, out ContainerManagerComponent? manager))
                    return;

                foreach (var container in manager.Containers.Values)
                {
                    containers++;
                    if (_container.CanInsert(probe, container))
                    {
                        accepted = true;
                        break;
                    }
                }

                holders.Add($"{where}={proto} containers={containers} accepts={accepted}");
            }
        }
        finally
        {
            QueueDel(probe);
        }

        Log.Error($"[SurvivorLoadout] No {ammo.Id} could be carried by a {PrototypeOf(body)} using {chosen} " +
                  $"(previous primary {oldPrimary}). Holders: {string.Join("; ", holders)}");
    }

    private int CountMagazines(EntityUid body, IReadOnlyList<ProtoId<TagPrototype>> family)
    {
        var count = 0;
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                count += CountMagazines(item, family, 0);
        }

        return count;
    }

    private int CountMagazines(EntityUid item, IReadOnlyList<ProtoId<TagPrototype>> family, int depth)
    {
        if (depth > 3)
            return 0;

        var count = InFamily(item, family) && IsMagazine(item) ? 1 : 0;

        // Not every entity carries a container manager; asking for its containers logs an error.
        if (!TryComp(item, out ContainerManagerComponent? manager))
            return count;

        foreach (var container in manager.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
                count += CountMagazines(contained, family, depth + 1);
        }

        return count;
    }

    /// <summary>
    ///     Issues a commander their belt sidearm: a Mateba autorevolver with 1-3 high-impact loads and 1-3
    ///     standard loads, in the belt holster where the kit leaves room for it.
    /// </summary>
    private void GrantCommanderSidearm(EntityUid body, SurvivorLoadoutPrototype loadout, string? roleProtoId)
    {
        if (roleProtoId == null || !loadout.CommanderRoles.Contains(roleProtoId))
            return;

        // A kit may already issue the sidearm itself (the commander wears a filled Mateba belt), in which case only
        // the gun is skipped - the loads below are still rolled, because the kit's own belt is a fixed set of
        // standard speed loaders and that is exactly what the commander should not be left with.
        if (!CarriesSidearm(body))
        {
            if (!_prototypes.HasIndex<EntityPrototype>(loadout.CommanderSidearm))
            {
                Log.Error($"Commander sidearm {loadout.CommanderSidearm} is not a prototype.");
                return;
            }

            var sidearm = Spawn(loadout.CommanderSidearm, _transform.GetMoverCoordinates(body));
            foreach (var slot in SidearmSlots)
            {
                if (_inventory.TryEquip(body, sidearm, slot, silent: true, force: true))
                    break;
            }

            if (_transform.GetParentUid(sidearm) == EntityUid.Invalid)
            {
                _transform.SetCoordinates(sidearm, _transform.GetMoverCoordinates(body));
                Log.Warning($"No free slot for the commander sidearm {loadout.CommanderSidearm}; it was placed on the ground.");
            }
        }

        // Whatever the kit shipped for the sidearm is replaced by the rolled issue, so the commander gets the mix
        // rather than the belt's default three.
        RemoveCarriedAmmo(body, loadout.CommanderSidearmAmmo);

        // The sidearm belt carries a handful of loads - three, for the Mateba belt - so each pool keeps a place
        // reserved for it: rolling three high-impact loads first filled the belt and left the commander with no
        // standard loads at all.
        var standard = _random.Next(1, 3);                       // 1-2
        var highImpact = _random.Next(1, 4 - standard);           // 1..(3 - standard)
        Log.Debug($"Commander sidearm: {highImpact} high-impact and {standard} standard loads.");

        var given = GiveSidearmAmmo(body, loadout.CommanderSidearmHighImpactAmmo, highImpact);
        GiveSidearmAmmo(body, loadout.CommanderSidearmAmmo, standard);

        if (given < highImpact)
        {
            Log.Warning($"Only {given} of {highImpact} high-impact loads for {loadout.CommanderSidearm} fit in the kit.");
        }
    }

    /// <summary>
    ///     Puts <paramref name="count"/> loads from <paramref name="ammo"/> into the survivor's kit, returning how
    ///     many actually fit.
    /// </summary>
    private int GiveRandomAmmo(EntityUid body, IReadOnlyList<EntProtoId> ammo, int count)
    {
        if (ammo.Count == 0)
            return 0;

        var given = 0;
        for (var i = 0; i < count; i++)
        {
            if (!GiveMagazine(body, _random.Pick(ammo)))
                break;

            given++;
        }

        return given;
    }

    /// <summary>Total rounds of a prototype inside a container tree, counting stack quantities.</summary>
    private int CountAmmoRounds(EntityUid root, string prototype, int depth = 0)
    {
        if (depth > 4 || !TryComp(root, out ContainerManagerComponent? containers))
            return 0;

        var total = 0;
        foreach (var container in containers.Containers.Values)
        {
            foreach (var item in container.ContainedEntities)
            {
                if (PrototypeOf(item) == prototype)
                    total += TryComp<StackComponent>(item, out var stack) ? stack.Count : 1;

                total += CountAmmoRounds(item, prototype, depth + 1);
            }
        }

        return total;
    }

    /// <summary>
    ///     Removes magazines that do not belong to the chosen weapon from the survivor's rigs, walking each rig's
    ///     containers directly. The slot-based strip can leave one behind when the rig sits inside a worn item, which
    ///     left a charge pack in the webbing next to the new ammunition.
    /// </summary>
    private void StripForeignFromRigs(EntityUid body, Func<EntityUid, bool> fitsCarriedGun, EntProtoId replacement)
    {
        foreach (var holder in AmmoHolders(body))
        {
            if (!TryComp(holder, out ContainerManagerComponent? containers))
                continue;

            foreach (var container in containers.Containers.Values)
            {
                foreach (var item in container.ContainedEntities.ToArray())
                {
                    if (!IsMagazine(item) || fitsCarriedGun(item))
                        continue;

                    // Delete only - see StripForeignMagazines.
                    QueueDel(item);
                }
            }
        }
    }

    /// <summary>
    ///     Counts items of one prototype inside a container tree. Entities are deduplicated because a worn webbing's
    ///     storage is reachable both through the uniform and through the webbing itself.
    /// </summary>
    private int CountItems(EntityUid root, string prototype, int depth = 0, HashSet<EntityUid>? seen = null)
    {
        seen ??= new HashSet<EntityUid>();
        if (depth > MaxStorageDepth || !TryComp(root, out ContainerManagerComponent? containers))
            return 0;

        var count = 0;
        foreach (var container in containers.Containers.Values)
        {
            foreach (var item in container.ContainedEntities)
            {
                if (seen.Add(item) && PrototypeOf(item) == prototype)
                    count++;

                count += CountItems(item, prototype, depth + 1, seen);
            }
        }

        return count;
    }

    /// <summary>
    ///     Rounds of one prototype belonging to a single rig: its own storage plus the storage of the clothing it is
    ///     fitted to, deduplicated. Ammunition in other rigs (belt, pouches, pack) is deliberately excluded.
    /// </summary>
    private int CountRigAmmo(EntityUid body, EntityUid rig, string prototype)
    {
        var seen = new HashSet<EntityUid>();
        var count = 0;

        void CountContainer(BaseContainer container)
        {
            foreach (var item in container.ContainedEntities)
            {
                if (seen.Add(item) && PrototypeOf(item) == prototype)
                    count++;
            }
        }

        void Walk(EntityUid root, int depth)
        {
            if (depth > MaxStorageDepth || !TryComp(root, out ContainerManagerComponent? containers))
                return;

            foreach (var container in containers.Containers.Values)
            {
                CountContainer(container);
                foreach (var item in container.ContainedEntities)
                    Walk(item, depth + 1);
            }
        }

        Walk(rig, 0);

        if (_container.TryGetContainingContainer((rig, null, null), out var host)
            && host.Owner != body
            && TryComp(host.Owner, out StorageComponent? hostStorage)
            && hostStorage.Container is { } live)
        {
            CountContainer(live);
        }

        return count;
    }


    /// <summary>Whether the survivor already carries a pistol or revolver anywhere in their kit.</summary>
    private bool CarriesSidearm(EntityUid body)
    {
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item && CarriesSidearm(item, 0))
                return true;
        }

        return false;
    }

    private bool CarriesSidearm(EntityUid item, int depth)
    {
        if (depth > MaxStorageDepth)
            return false;

        var prototype = PrototypeOf(item) ?? string.Empty;
        if (prototype.Contains("Revolver", StringComparison.OrdinalIgnoreCase))
            return true;

        if (HasComp<GunComponent>(item) && prototype.Contains("Pistol", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!TryComp(item, out ContainerManagerComponent? containers))
            return false;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
            {
                if (CarriesSidearm(contained, depth + 1))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Deletes ammunition of the given prototypes from the survivor's kit, so a rolled issue can replace a kit's
    ///     own fixed belt contents instead of stacking on top of them.
    /// </summary>
    private void RemoveCarriedAmmo(EntityUid body, IReadOnlyList<EntProtoId> prototypes)
    {
        if (prototypes.Count == 0)
            return;

        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } item)
                RemoveCarriedAmmo(item, prototypes, 0);
        }
    }

    private void RemoveCarriedAmmo(EntityUid item, IReadOnlyList<EntProtoId> prototypes, int depth)
    {
        if (depth > MaxStorageDepth)
            return;

        var prototype = PrototypeOf(item);
        foreach (var candidate in prototypes)
        {
            if (!string.Equals(prototype, candidate.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            QueueDel(item);
            return;
        }

        if (!TryComp(item, out ContainerManagerComponent? containers))
            return;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities.ToArray())
                RemoveCarriedAmmo(contained, prototypes, depth + 1);
        }
    }

    /// <summary>
    ///     Builds the test that decides whether a magazine belongs to the survivor's kit: a round is kept when a gun
    ///     they carry can actually chamber it, judged by that gun's own whitelist rather than by tag family - every
    ///     rifle magazine shares the generic rifle tag, so families cannot tell an M54C magazine from a MAR-50 drum.
    ///     Guns with no whitelist fall back to their tag family; with no guns at all, nothing is stripped.
    /// </summary>
    private Func<EntityUid, bool> BuildAmmoFitTest(EntityUid body)
    {
        var guns = new List<(EntityWhitelist? Whitelist, List<ProtoId<TagPrototype>> Tags)>();
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is { } worn)
                CollectCarriedGuns(worn, guns, 0);
        }

        if (guns.Count == 0)
            return _ => true;

        return item =>
        {
            foreach (var (whitelist, tags) in guns)
            {
                if (whitelist != null)
                {
                    if (_whitelist.IsValid(whitelist, item))
                        return true;

                    continue;
                }

                if (InFamily(item, tags))
                    return true;
            }

            return false;
        };
    }

    private void CollectCarriedGuns(EntityUid item,
        List<(EntityWhitelist? Whitelist, List<ProtoId<TagPrototype>> Tags)> into, int depth)
    {
        if (depth > MaxStorageDepth)
            return;

        if (HasComp<GunComponent>(item) && PrototypeOf(item) is { } prototype)
        {
            EntityWhitelist? whitelist = null;

            if (HasComp<ItemSlotsComponent>(item)
                && _itemSlots.TryGetSlot(item, "gun_magazine", out var magazineSlot))
            {
                whitelist = magazineSlot?.Whitelist;
            }

            if (whitelist == null && TryComp(item, out BallisticAmmoProviderComponent? ballistic))
                whitelist = ballistic.Whitelist;

            if (whitelist == null && TryComp(item, out RevolverAmmoProviderComponent? revolver))
                whitelist = revolver.Whitelist;

            into.Add((whitelist, MagazineTags(prototype)));
        }

        if (!TryComp(item, out ContainerManagerComponent? containers))
            return;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var contained in container.ContainedEntities)
                CollectCarriedGuns(contained, into, depth + 1);
        }
    }

    /// <summary>
    ///     Stocks sidearm loads in the belt that carries the sidearm, falling back to the normal rig search. Speed
    ///     loaders put into the first ammo holder instead (the uniform's webbing) read as the commander having no
    ///     spare ammunition at all, because the belt next to the revolver stays empty.
    /// </summary>
    private int GiveSidearmAmmo(EntityUid body, IReadOnlyList<EntProtoId> ammo, int count)
    {
        if (ammo.Count == 0 || count <= 0)
            return 0;

        var belt = _inventory.TryGetSlotEntity(body, "belt", out var worn) ? worn : null;
        if (belt is not { } rig || !TryComp(rig, out StorageComponent? beltStorage))
            return GiveRandomAmmo(body, ammo, count);

        var given = 0;
        for (var i = 0; i < count; i++)
        {
            var spawned = Spawn(_random.Pick(ammo), _transform.GetMoverCoordinates(body));
            if (_storage.Insert(rig, spawned, out _, storageComp: beltStorage, playSound: false))
            {
                given++;
                continue;
            }

            // The belt refused it: fall back to the generic search rather than dropping the round.
            QueueDel(spawned);
            if (!GiveMagazine(body, _random.Pick(ammo)))
                break;

            given++;
        }

        return given;
    }

    /// <summary>
    ///     Equips the role job's starting kit when the body is still undressed. Bodies dressed by a preset are left
    ///     alone, so a faction never gets its gear twice.
    /// </summary>
    public void EnsureRoleGear(EntityUid body, ProtoId<JobPrototype>? roleJob)
    {
        if (roleJob is not { } jobId || !_prototypes.TryIndex(jobId, out JobPrototype? job))
            return;

        // A job's roundComponents are applied by GhostRoleApplySpecialSystem, which survivor spawning strips as
        // call-in identity - so a survivor used to get no skills at all. The job still owns the configuration, so
        // its skill component is applied here; the rest of the round components stay stripped on purpose because
        // they carry the call-in squad's identity and gear preset.
        ApplyJobSkills(body, jobId);

        if (job.StartingGear is not { } gear)
            return;

        if (_inventory.TryGetSlotEntity(body, "jumpsuit", out _)
            || _inventory.TryGetSlotEntity(body, "outerClothing", out _))
        {
            return;
        }

        var spawning = EntityManager.System<StationSpawningSystem>();
        spawning.EquipStartingGear(body, gear, raiseEvent: false, keepOnFail: true);
        Log.Debug($"Dressed a survivor from the starting gear of job {jobId}.");
    }

    /// <summary>
    ///     Copies the role job's skill component onto the survivor, keeping the higher of the two levels.
    /// </summary>
    private void ApplyJobSkills(EntityUid body, ProtoId<JobPrototype> jobId)
    {
        if (!_prototypes.TryIndex(jobId, out JobPrototype? job))
            return;

        // A body that already declares its own skills (a synthetic, for instance) keeps them; otherwise the job's
        // skill component is copied on. AddComponent(entry) clones through the component factory, so the prototype's
        // instance is never shared with the spawned mob.
        if (HasComp<SkillsComponent>(body))
            return;

        var key = EntityManager.ComponentFactory.GetComponentName<SkillsComponent>();
        if (job.RoundComponents.TryGetValue(key, out var entry))
            EntityManager.AddComponent(body, entry);
    }

    /// <summary>
    ///     Whether this role may be issued a primary weapon at all. Support roles - the synthetics - carry no
    ///     weapon unless the loadout explicitly lists firearms for them, so the picker's fallback to the faction
    ///     list must not arm them.
    /// </summary>
    private bool RoleMayCarryWeapon(string? roleProtoId, SurvivorLoadoutPrototype loadout)
    {
        if (roleProtoId == null
            || !_prototypes.TryIndex<SurvivorRolePrototype>(roleProtoId, out var role)
            || role.Category != SurvivorRoleCategory.Support)
        {
            return true;
        }

        return loadout.RoleFirearms.ContainsKey(roleProtoId);
    }

    /// <summary>Whether a worn rig's own storage will take a round of this ammunition.</summary>
    private bool CanStore(EntityUid holder, EntProtoId prototype)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(prototype) || !TryComp(holder, out StorageComponent? storage))
            return false;

        var probe = Spawn(prototype, _transform.GetMoverCoordinates(holder));
        try
        {
            // The probe sits on the floor while the rig is worn, so the location check is skipped: it asks whether
            // the rig's own storage rules accept the round, which is the question stocking needs answered.
            return _storage.CanInsert(holder, probe, out _, storageComp: storage, ignoreLocation: true);
        }
        finally
        {
            QueueDel(probe);
        }
    }

    /// <summary>
    ///     The standard magazine for a weapon whose own magazine is an AP load. The M39B/2 ships
    ///     CMMagazineSMGM63AP, so issuing what the slot names made AP the whole base supply and left the upgrade pass
    ///     nothing normal to convert; the survivor should be issued standard magazines and let the upgrade choose
    ///     which one to three become AP.
    /// </summary>
    private EntProtoId StandardVariantOf(EntProtoId magazine)
    {
        if (!SurvivorAmmoUpgrade.IsArmorPiercing(magazine.Id))
            return magazine;

        var standard = SurvivorAmmoUpgrade.FindStandardVariant(magazine.Id, MagazineCandidates());
        return standard == null ? magazine : new EntProtoId(standard);
    }

    /// <summary>How many magazines this role is issued for its chosen weapon.</summary>
    private static int MagazineIssueFor(SurvivorLoadoutPrototype loadout, string? roleProtoId)
    {
        if (roleProtoId != null && loadout.RoleMagazineIssue.TryGetValue(roleProtoId, out var perRole))
            return Math.Max(1, perRole);

        return Math.Max(1, loadout.MagazineIssue);
    }

    /// <summary>Empties magazines that do not feed the chosen weapon out of every worn magazine rig.</summary>
    private void ClearMagazineRigs(EntityUid body, string magazine)
    {
        var slots = _inventory.GetSlotEnumerator(body, SlotFlags.All);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } holder
                || PrototypeOf(holder)?.Contains("Magazine", StringComparison.OrdinalIgnoreCase) != true
                || !TryComp(holder, out ContainerManagerComponent? containers))
            {
                continue;
            }

            foreach (var container in containers.Containers.Values)
            {
                foreach (var item in container.ContainedEntities.ToArray())
                {
                    if (PrototypeOf(item) == magazine || !IsMagazine(item))
                        continue;

                    Log.Debug($"Cleared {PrototypeOf(item)} out of {PrototypeOf(holder)} to make room for {magazine}.");
                    Del(item);
                }
            }
        }
    }
}
