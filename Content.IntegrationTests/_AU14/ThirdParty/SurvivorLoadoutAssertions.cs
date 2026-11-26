using System.Collections.Generic;
using System.Linq;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared._RMC14.Webbing;
using Content.Shared.Weapons.Ranged.Components;
using NUnit.Framework;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     A snapshot of what a survivor actually spawns with, so tests assert the spawned character instead of the
///     preset's declaration. Reads worn slots, the webbing inside a uniform, and the contents of every container on
///     the character, which is where previous loadout bugs hid: two primaries (one in a hand, one in suit storage),
///     magazines that silently fell through to the uniform's drop pouch, and webbing that never existed.
/// </summary>
public sealed class SurvivorLoadoutSnapshot
{
    /// <summary>Equipment slots worth reporting; the full set the survivor loadout can touch.</summary>
    public static readonly string[] InspectedSlots =
    {
        "jumpsuit", "outerClothing", "head", "back", "belt", "gloves", "shoes", "mask", "eyes",
        "suitstorage", "ears", "pocket1", "pocket2",
    };

    /// <summary>Prototype id worn in each occupied slot.</summary>
    public readonly Dictionary<string, string> Slots = new();

    /// <summary>Prototype ids held in the character's hands.</summary>
    public readonly List<string> Hands = new();

    /// <summary>Every item inside a worn container, tagged with the slot it came from.</summary>
    public readonly List<(string Slot, string Item)> Nested = new();

    /// <summary>Guns worn, held or stored, with the slot they were found in ("hand" for hands).</summary>
    public readonly List<(string Origin, string Id)> Guns = new();

    /// <summary>
    ///     Weapons the survivor carries as their primary: everything except a holstered sidearm (belt or pocket
    ///     holster) and weapon attachments such as an underbarrel grenade launcher.
    /// </summary>
    public IEnumerable<string> Primaries
        => Guns.Where(g => g.Origin is "suitstorage" or "hand").Select(g => g.Id);

    public static SurvivorLoadoutSnapshot Capture(IEntityManager ent, EntityUid body)
    {
        var snapshot = new SurvivorLoadoutSnapshot();
        var inventory = ent.System<InventorySystem>();

        foreach (var slotName in InspectedSlots)
        {
            if (!inventory.TryGetSlotEntity(body, slotName, out var worn) || worn is not { } wornUid)
                continue;

            var wornId = IdOf(ent, wornUid);
            snapshot.Slots[slotName] = wornId;
            snapshot.Capture(ent, slotName, wornUid, wornId);

            // Webbing is worn inside the uniform rather than in an equipment slot of its own.
            if (ent.TryGetComponent<WebbingClothingComponent>(wornUid, out _)
                && ent.TryGetComponent<ContainerManagerComponent>(wornUid, out var uniformContainers)
                && uniformContainers.Containers.TryGetValue("cm_clothing_webbing_slot", out var webbingSlot))
            {
                foreach (var webbing in webbingSlot.ContainedEntities)
                {
                    var webbingId = IdOf(ent, webbing);
                    snapshot.Nested.Add(("webbing", webbingId));
                    snapshot.Capture(ent, "webbing", webbing, webbingId);
                }
            }
        }

        var hands = ent.System<SharedHandsSystem>();
        if (ent.TryGetComponent<HandsComponent>(body, out var handsComp))
        {
            foreach (var handId in handsComp.Hands.Keys)
            {
                if (!hands.TryGetHeldItem((body, handsComp), handId, out var held) || held is not { } item)
                    continue;

                var id = IdOf(ent, item);
                snapshot.Hands.Add(id);
                snapshot.AddGun(ent, "hand", item, id);
            }
        }

        return snapshot;
    }

    private void Capture(IEntityManager ent, string slot, EntityUid holder, string holderId)
    {
        AddGun(ent, slot, holder, holderId);

        if (!ent.TryGetComponent<ContainerManagerComponent>(holder, out var containers))
            return;

        // Storage is one of the entity's containers too, so items are deduped by entity.
        CaptureDeep(ent, slot, holder, new HashSet<EntityUid> { holder }, 0);
    }

    /// <summary>Walks nested containers (pack → pouch → magazines, uniform → accessory webbing) up to a sane depth.</summary>
    private void CaptureDeep(IEntityManager ent, string slot, EntityUid holder, HashSet<EntityUid> seen, int depth)
    {
        if (depth > 3 || !ent.TryGetComponent<ContainerManagerComponent>(holder, out var containers))
            return;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var item in container.ContainedEntities)
            {
                if (!seen.Add(item))
                    continue;

                var id = IdOf(ent, item);
                Nested.Add((slot, id));

                AddGun(ent, slot, item, id);
                CaptureDeep(ent, slot, item, seen, depth + 1);
            }
        }
    }

    /// <summary>Records a weapon, unless it is an attachment rather than a gun in its own right.</summary>
    private void AddGun(IEntityManager ent, string origin, EntityUid item, string id)
    {
        if (!ent.HasComponent<GunComponent>(item))
            return;

        // An underbarrel grenade launcher is a gun component on an attachment, not the survivor's primary.
        if (ent.HasComponent<Content.Shared._RMC14.Attachable.Components.AttachableComponent>(item))
            return;

        Guns.Add((origin, id));
    }

    public static string IdOf(IEntityManager ent, EntityUid uid)
        => ent.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID ?? "<none>";

    /// <summary>Everything this character is wearing or carrying, for failure messages.</summary>
    public string Describe()
    {
        var slots = string.Join(", ", Slots.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
        var hands = Hands.Count == 0 ? "-" : string.Join(", ", Hands);
        var nested = Nested.Count == 0 ? "-" : string.Join(", ", Nested.Select(n => $"{n.Slot}:{n.Item}"));
        return $"slots[{slots}] hands[{hands}] nested[{nested}] guns[{string.Join(", ", Guns)}]";
    }
}

/// <summary>
///     The checks every survivor role must pass, shared so a new preset cannot quietly skip them.
/// </summary>
public static class SurvivorLoadoutAssertions
{
    /// <summary>Worn kit every survivor role is expected to have, whatever its faction.</summary>
    public static readonly string[] RequiredSlots =
    {
        "jumpsuit", "outerClothing", "head", "back", "belt",
    };

    /// <summary>Whether an id names ammunition rather than gear.</summary>
    public static bool IsAmmo(string id)
        => id.Contains("Magazine", System.StringComparison.OrdinalIgnoreCase)
           || id.Contains("SpeedLoader", System.StringComparison.OrdinalIgnoreCase)
           || id.Contains("Shell", System.StringComparison.OrdinalIgnoreCase)
           || id.Contains("Rocket", System.StringComparison.OrdinalIgnoreCase)
           || id.Contains("Tank", System.StringComparison.OrdinalIgnoreCase)
           || id.Contains("Fuel", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Asserts the shared survivor contract: dressed in the core clothing, exactly one primary weapon for an
    ///     armed role, and ammunition for it inside a worn rig. Role- and faction-specific kit (helmet, webbing,
    ///     belt, pack) is asserted where that faction issues it, not universally, so faction differences survive.
    /// </summary>
    public static void AssertCore(string role, SurvivorLoadoutSnapshot snapshot, bool expectFirearm = true)
    {
        Assert.That(snapshot.Slots.ContainsKey("jumpsuit"), Is.True,
            $"{role} spawns without its uniform: {snapshot.Describe()}");
        Assert.That(snapshot.Slots.ContainsKey("outerClothing"), Is.True,
            $"{role} spawns without its armour: {snapshot.Describe()}");

        if (!expectFirearm)
            return;

        var primaries = snapshot.Primaries.ToList();
        Assert.That(primaries.Count, Is.EqualTo(1),
            $"{role} must carry exactly one primary weapon, found [{string.Join(", ", primaries)}] " +
            $"(all guns: {snapshot.Describe()})");

        Assert.That(snapshot.Nested.Any(n => IsAmmo(n.Item)), Is.True,
            $"{role} carries no ammunition in any worn rig: {snapshot.Describe()}");
    }
}
