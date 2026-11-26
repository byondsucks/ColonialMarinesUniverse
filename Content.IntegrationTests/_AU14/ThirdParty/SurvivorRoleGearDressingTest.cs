using System.Threading.Tasks;
using Content.Server.CMU14.Survivors.Setup;
using System.Collections.Generic;
using Content.Shared.CMU14.Survivors;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared._RMC14.Webbing;
using Content.Shared._RMC14.Marines.Skills;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     Survivor role bodies are dressed two different ways: most factions carry an <c>EquipEntityPreset</c> on the
///     body, and a few (the CLF Spec Ops roles) only carry <c>startingGear</c> on the role's job. The spawn path only
///     ever applied the first, so a CLF Spec Ops medic arrived with nothing but the helmet, armour and gun the
///     loadout puts on.
/// </summary>
[TestFixture]
public sealed class SurvivorRoleGearDressingTest
{
    [Test]
    public async Task AJobOnlyRoleIsDressedFromItsStartingGear()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var map = await pair.CreateTestMap();

        EntityUid body = default;

        await server.WaitAssertion(() =>
        {
            var loadouts = ent.System<SurvivorLoadoutSystem>();
            body = ent.SpawnEntity("CMUSurvivorClfSpecOpsMedic", map.GridCoords);

            // The body itself carries no gear; the job does.
            loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>("CMUJobCLFSpecOpsMedic"));
        });

        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var inventory = ent.System<InventorySystem>();

            Assert.Multiple(() =>
            {
                Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var jumpsuit), Is.True,
                    "the role's job gear must put a jumpsuit on the survivor");
                Assert.That(ent.GetComponent<MetaDataComponent>(jumpsuit!.Value).EntityPrototype?.ID,
                    Is.EqualTo("CMU14FatiguesCLFSpecOpsMedic"),
                    "the jumpsuit is the one the role's gear names");
                Assert.That(inventory.TryGetSlotEntity(body, "outerClothing", out _), Is.True, "armour is worn");
                Assert.That(inventory.TryGetSlotEntity(body, "belt", out _), Is.True, "the role's belt is worn");
                Assert.That(inventory.TryGetSlotEntity(body, "back", out _), Is.True, "the role's pack is worn");
            });
        });

        // The rest of the role: its job gear must also leave exactly one gun, a worn webbing and the insurgent
        // skills. All three were missing when the role borrowered a call-in job and a call-in body.
        await server.WaitAssertion(() =>
        {
            var loadouts = ent.System<SurvivorLoadoutSystem>();
            var inventory = ent.System<InventorySystem>();
            var hands = ent.System<SharedHandsSystem>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var loadout = prototypes.Index<SurvivorLoadoutPrototype>("CMUSurvivorLoadoutClfCommando");

            // Immediate rather than queued: this test drives the pipeline by hand and must not leave timers behind.
            loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>("CMUJobCLFSpecOpsMedic"));
            loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(null, null, "RMCWeaponRifleM16A1"),
                "CMUSurvivorClfSpecOpsMedic");
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var inventory = ent.System<InventorySystem>();
            var hands = ent.System<SharedHandsSystem>();

            var guns = new List<string>();
            if (ent.TryGetComponent<HandsComponent>(body, out var handsComp))
            {
                foreach (var handId in handsComp.Hands.Keys)
                {
                    if (!hands.TryGetHeldItem((body, handsComp), handId, out var held) || held is not { } heldItem)
                        continue;

                    if (ent.HasComponent<GunComponent>(heldItem))
                        guns.Add(ent.GetComponent<MetaDataComponent>(heldItem).EntityPrototype?.ID ?? "?");
                }
            }

            foreach (var slot in new[] { "suitstorage", "back", "belt" })
            {
                if (!inventory.TryGetSlotEntity(body, slot, out var worn))
                    continue;

                if (ent.HasComponent<GunComponent>(worn!.Value))
                    guns.Add(ent.GetComponent<MetaDataComponent>(worn.Value).EntityPrototype?.ID ?? "?");

                if (ent.TryGetComponent<StorageComponent>(worn.Value, out var storage) && storage.Container != null)
                {
                    foreach (var item in storage.Container.ContainedEntities)
                    {
                        if (ent.HasComponent<GunComponent>(item))
                            guns.Add(ent.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID ?? "?");
                    }
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(guns, Has.Count.EqualTo(1),
                    $"the survivor carries exactly one gun, found [{string.Join(", ", guns)}]");
                Assert.That(guns[0], Is.EqualTo("RMCWeaponRifleM16A1"), "and it is the one they picked");

                // A worn webbing, not just the uniform's accessory holder.
                EntityUid? wornJumpsuit = inventory.TryGetSlotEntity(body, "jumpsuit", out var jumpsuit) ? jumpsuit : null;
                Assert.That(wornJumpsuit, Is.Not.Null, "the survivor wears the role's fatigues");
                Assert.That(ent.HasComponent<WebbingClothingComponent>(wornJumpsuit!.Value), Is.True,
                    "the role's fatigues must carry a worn webbing");

                // Insurgent skills from the survivor job, not a PMC contractor's.
                Assert.That(ent.TryGetComponent<SkillsComponent>(body, out var skills), Is.True, "the role has skills");
                Assert.That(skills!.Skills.Keys.Select(k => k.Id), Does.Contain("RMCSkillMedical"),
                    "a corpsman has the medical skill");
                Assert.That(skills.Skills.Keys.Select(k => k.Id), Does.Not.Contain("RMCSkillSurgeryXenoOnly"),
                    "and none of the call-in only skills");
            });
        });

        await server.WaitAssertion(() =>
        {
            if (ent.Deleted(body))
                return;

            if (!ent.Deleted(body))
                ent.DeleteEntity(body);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task APresetDressedRoleIsNotDressedTwice()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var map = await pair.CreateTestMap();

        EntityUid body = default;
        string? before = null;

        await server.WaitAssertion(() =>
        {
            var inventory = ent.System<InventorySystem>();
            body = ent.SpawnEntity("CMUSurvForeconRifleman", map.GridCoords);
            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var worn), Is.True,
                "a preset-dressed body starts clothed");
            before = ent.GetComponent<MetaDataComponent>(worn!.Value).EntityPrototype?.ID;
        });

        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var loadouts = ent.System<SurvivorLoadoutSystem>();
            var inventory = ent.System<InventorySystem>();

            // The role's job also names starting gear; it must not replace what the preset already put on.
            loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>("CMUJobSurvForeconRifleman"));

            Assert.That(inventory.TryGetSlotEntity(body, "jumpsuit", out var worn), Is.True);
            Assert.That(ent.GetComponent<MetaDataComponent>(worn!.Value).EntityPrototype?.ID, Is.EqualTo(before),
                "an already dressed survivor keeps the kit its preset gave it");
        });

        await server.WaitAssertion(() =>
        {
            if (!ent.Deleted(body))
                ent.DeleteEntity(body);
        });

        await pair.CleanReturnAsync();
    }
}
