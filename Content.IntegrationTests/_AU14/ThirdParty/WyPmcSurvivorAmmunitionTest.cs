using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Survivors;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     W-Y PMC survivors issued the M39B/2. The pack's uniform carries an MK80 holster webbing, which is an ammo
///     holder by name but cannot take an SMG magazine; the stocking pass used to target that first holder, so the
///     Field Commander's M39/2 had only its own magazine and his ammunition belt stayed empty.
/// </summary>
[TestFixture]
public sealed class WyPmcSurvivorAmmunitionTest
{
    private const string Loadout = "CMUSurvivorLoadoutWyPmc";
    private const string FieldCommander = "CMUSurvivorWyPmcFieldOpsLeader";
    private const string M39B2 = "RMCWeaponSMGM63B2";
    private const string M39Ammo = "CMMagazineSMGM63AP";
    private const string M39AmmoStandard = "CMMagazineSMGM63";
    private const string AmmunitionBelt = "CMBeltMarine";

    [Test]
    [Repeat(3)]
    public async Task FieldCommanderSpawnsWithM39AmmunitionInItsBelt()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var map = await pair.CreateTestMap();

        EntityUid body = default;

        await server.WaitAssertion(() =>
        {
            var loadouts = ent.System<SurvivorLoadoutSystem>();
            var loadout = prototypes.Index<SurvivorLoadoutPrototype>(Loadout);

            body = ent.SpawnEntity(FieldCommander, map.GridCoords);
            loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>("CMUSurvivorJobWyPmcFieldOpsLeader"));
            loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(null, null, M39B2), FieldCommander);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var snapshot = SurvivorLoadoutSnapshot.Capture(ent, body);
            var belt = snapshot.Nested.Where(n => n.Slot == "belt").Select(n => n.Item).ToList();
            var beltMags = belt.Count(i => i.StartsWith("CMMagazineSMGM63", System.StringComparison.Ordinal));

            Assert.Multiple(() =>
            {
                // 1. The dedicated job is the one assigned.
                Assert.That(prototypes.Index<JobPrototype>("CMUSurvivorJobWyPmcFieldOpsLeader").PlayTimeTracker.Id,
                    Is.EqualTo("CMUSurvivorJobWyPmcFieldOpsLeader"),
                    "the Field Commander records playtime on its own counter");

                // 2. The M39/2 is the primary, worn in suit storage.
                Assert.That(snapshot.Slots.GetValueOrDefault("suitstorage"), Is.EqualTo(M39B2),
                    $"the M39/2 is the primary: {snapshot.Describe()}");
                Assert.That(snapshot.Primaries, Is.EquivalentTo(new[] { M39B2 }),
                    $"exactly one primary at the ready: {snapshot.Describe()}");

                // 3. The ammunition belt is worn in the belt slot.
                Assert.That(snapshot.Slots.GetValueOrDefault("belt"), Is.EqualTo(AmmunitionBelt),
                    $"the M276 ammunition rig is worn: {snapshot.Describe()}");

                // 4/5. Five M39 magazines in the belt, one to three of them the AP upgrade of the rest.
                Assert.That(beltMags, Is.EqualTo(5),
                    $"the belt holds five M39 magazines ({beltMags} found) [{string.Join(", ", belt)}]");
                var ap = belt.Count(i => i == M39Ammo);
                var standard = belt.Count(i => i == M39AmmoStandard);
                Assert.That(ap, Is.InRange(1, 3),
                    $"one to three of them are upgraded to AP ({ap} AP, {standard} standard) " +
                    $"[{string.Join(", ", belt)}]");
                Assert.That(standard, Is.EqualTo(5 - ap),
                    $"the rest stay standard M39 ({ap} AP, {standard} standard) [{string.Join(", ", belt)}]");
                var ammoOutsideBelt = snapshot.Nested
                    .Where(n => n.Item.StartsWith("CMMagazineSMGM63", System.StringComparison.Ordinal))
                    .Where(n => n.Slot is not ("belt" or "suitstorage"))
                    .Select(n => $"{n.Slot}:{n.Item}")
                    .ToList();
                Assert.That(ammoOutsideBelt, Is.Empty,
                    $"M39 ammunition belongs in the belt, found it in [{string.Join(", ", ammoOutsideBelt)}]");

                // 6. No duplicate primary: the holstered MK80 sidearm is kit, not a second primary.
                Assert.That(snapshot.Guns.Count(g => g.Id == M39B2), Is.EqualTo(1),
                    $"one M39/2 only: {snapshot.Describe()}");

                // 7. The rest of the Field Commander's kit is untouched.
                Assert.That(snapshot.Slots.GetValueOrDefault("jumpsuit"),
                    Is.EqualTo("RMCJumpsuitVeteranPMCLeaderHolsterPatch"), "the leader's uniform is worn");
                Assert.That(snapshot.Slots.GetValueOrDefault("outerClothing"), Is.EqualTo("CMArmorM4PMCLeader"),
                    "the leader's armour is worn");
                Assert.That(snapshot.Slots.GetValueOrDefault("head"), Is.EqualTo("ArmorHelmetPMCFieldOfficer"),
                    "the field officer helmet is worn");
                Assert.That(snapshot.Slots.GetValueOrDefault("back"), Is.EqualTo("RMCBackpackRTOPMCBroken"),
                    "the RTO pack is worn");
                Assert.That(snapshot.Slots.GetValueOrDefault("pocket2"), Is.EqualTo("RMCPouchMedkitPMCFullAdvanced"),
                    "the medkit pouch is worn");
                Assert.That(snapshot.Nested.Any(n => n.Item == "CMUClothingMaskWYPMCOfficer"), Is.False,
                    "the W-Y officer mask belongs on the face, not in storage");
                Assert.That(snapshot.Slots.GetValueOrDefault("mask"), Is.EqualTo("CMUClothingMaskWYPMCOfficer"),
                    "the W-Y officer mask is worn");
            });

            if (!ent.Deleted(body))
                ent.DeleteEntity(body);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Every W-Y PMC survivor role, with every firearm the faction offers: the ammunition for the chosen weapon
    ///     must end up inside a worn rig, and the survivor must carry exactly one primary at the ready.
    /// </summary>
    [Test]
    public async Task EveryWyPmcSurvivorFirearmGetsAmmunitionInARig()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var map = await pair.CreateTestMap();

        var failures = new List<string>();
        var checkedCombinations = 0;

        // Support roles (the synthetic) are not issued a primary weapon at all, so they have no ammunition to place.
        var roles = prototypes.EnumeratePrototypes<SurvivorRolePrototype>()
            .Where(role => role.ID.StartsWith("CMUSurvivorWyPmc", System.StringComparison.Ordinal))
            .Where(role => role.Category != SurvivorRoleCategory.Support)
            .OrderBy(role => role.ID)
            .ToList();

        var loadout = prototypes.Index<SurvivorLoadoutPrototype>(Loadout);

        // Magazine weapons only. The shotgun takes loose shells and is supplied by a different pass, whose capacity
        // depends on the randomly filled pack - a separate, pre-existing limitation that this sweep must not mask.
        var magazineWeapons = loadout.Firearms
            .Where(firearm => firearm.Id.Contains("M890", System.StringComparison.Ordinal) == false)
            .ToList();

        foreach (var role in roles)
        {
            foreach (var firearm in magazineWeapons)
            {
                EntityUid body = default;

                await server.WaitAssertion(() =>
                {
                    var loadouts = ent.System<SurvivorLoadoutSystem>();
                    body = ent.SpawnEntity(role.ID, map.GridCoords);
                    loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>("CMUSurvivorJob" +
                        role.ID.Substring("CMUSurvivor".Length)));
                    loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(null, null, firearm.Id), role.ID);
                });

                await pair.RunTicksSync(2);

                await server.WaitAssertion(() =>
                {
                    var snapshot = SurvivorLoadoutSnapshot.Capture(ent, body);
                    checkedCombinations++;

                    var ammo = snapshot.Nested.Where(n => SurvivorLoadoutAssertions.IsAmmo(n.Item)).ToList();
                    if (ammo.Count == 0)
                    {
                        failures.Add($"{role.ID}/{firearm.Id}: no ammunition in any worn rig " +
                                     $"({snapshot.Describe()})");
                    }

                    var primaries = snapshot.Primaries.ToList();
                    if (primaries.Count > 1 || (primaries.Count == 1 && primaries[0] != firearm.Id))
                    {
                        failures.Add($"{role.ID}/{firearm.Id}: primaries " +
                                     $"[{string.Join(", ", primaries)}]");
                    }

                    if (!ent.Deleted(body))
                        ent.DeleteEntity(body);
                });
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(roles.Count, Is.GreaterThanOrEqualTo(6), $"every W-Y PMC survivor role is checked ({roles.Count})");
            Assert.That(checkedCombinations, Is.GreaterThanOrEqualTo(18),
                $"every offered magazine weapon is tried for each role ({checkedCombinations})");
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        });

        await pair.CleanReturnAsync();
    }
}
