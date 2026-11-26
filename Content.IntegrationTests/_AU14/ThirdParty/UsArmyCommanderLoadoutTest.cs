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
///     US Army survivor commander: a Mateba autorevolver with its loads in the holster belt, and the chosen primary's
///     magazines in the large magazine pouch. The belt used to be built on the pistol holster rig (which refuses a
///     revolver and its speed loaders) and the pouch kept the kit's magazines for the previous weapon, so the
///     commander spawned with an empty belt and an empty pouch.
/// </summary>
[TestFixture]
public sealed class UsArmyCommanderLoadoutTest
{
    private const string Commander = "CMUSurvUSArmyCommander";
    private const string Job = "CMUSurvivorJobUSArmyCommander";
    private const string Loadout = "CMUSurvivorLoadoutUsArmy";
    private const string Mateba = "RMCWeaponRevolverMateba";
    private const string M39 = "WeaponSMGM63";
    private const string M39Mag = "CMMagazineSMGM63";

    [Test]
    public async Task CommanderSpawnsWithMatebaAndM39AmmunitionInTheirRigs()
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

            body = ent.SpawnEntity(Commander, map.GridCoords);
            loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>(Job));
            loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(null, null, M39), Commander);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var snapshot = SurvivorLoadoutSnapshot.Capture(ent, body);
            var belt = snapshot.Nested.Where(n => n.Slot == "belt").Select(n => n.Item).ToList();
            var pouch = snapshot.Nested.Where(n => n.Slot == "pocket1").Select(n => n.Item).ToList();

            Assert.Multiple(() =>
            {
                // Sidearm: the revolver and its loads are inside the holster belt.
                Assert.That(belt, Does.Contain(Mateba), $"the Mateba is in its belt [{string.Join(", ", belt)}]");
                Assert.That(belt.Count(i => i.StartsWith("RMCSpeedLoaderMateba", System.StringComparison.Ordinal)),
                    Is.GreaterThanOrEqualTo(2), $"the belt carries Mateba loads [{string.Join(", ", belt)}]");

                // Primary: the M39 is the weapon at the ready, and only one primary exists.
                Assert.That(snapshot.Slots.GetValueOrDefault("suitstorage"), Is.EqualTo(M39),
                    $"the M39 is the primary: {snapshot.Describe()}");
                Assert.That(snapshot.Primaries, Is.EquivalentTo(new[] { M39 }),
                    $"exactly one primary at the ready: {snapshot.Describe()}");

                // Primary ammunition: in the large magazine pouch, nothing scattered elsewhere.
                var pouchMags = pouch.Count(i => i.StartsWith(M39Mag, System.StringComparison.Ordinal));
                Assert.That(pouchMags, Is.EqualTo(4),
                    $"the magazine pouch holds four M39 magazines ({pouchMags} found) [{string.Join(", ", pouch)}]");
                var stray = snapshot.Nested
                    .Where(n => n.Item.StartsWith(M39Mag, System.StringComparison.Ordinal))
                    .Where(n => n.Slot is not ("pocket1" or "suitstorage"))
                    .Select(n => $"{n.Slot}:{n.Item}")
                    .ToList();
                Assert.That(stray, Is.Empty, $"M39 magazines belong in the pouch, found [{string.Join(", ", stray)}]");

                // The rest of the commander's kit is untouched.
                Assert.That(snapshot.Slots.GetValueOrDefault("jumpsuit"), Is.EqualTo("AU14ArmyJumpsuitCavalry"));
                Assert.That(snapshot.Slots.GetValueOrDefault("outerClothing"), Is.EqualTo("RMCCoatCOServiceJacketGreen"));
                Assert.That(snapshot.Slots.GetValueOrDefault("head"), Is.EqualTo("AU14BeretUSArmy"));
                Assert.That(snapshot.Slots.GetValueOrDefault("back"), Is.EqualTo("AU14AU14MolleBackpackCOFull"));
                Assert.That(snapshot.Slots.GetValueOrDefault("pocket2"), Is.EqualTo("AU14PouchIFAKFill"),
                    "the first aid pouch is still worn");
                Assert.That(snapshot.Slots.GetValueOrDefault("belt"), Is.EqualTo("CMUMatebaBeltHighImpact"));
            });

            if (!ent.Deleted(body))
                ent.DeleteEntity(body);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     A different primary pick must produce that weapon's ammunition in the pouch while the Mateba sidearm
    ///     arrangement stays intact.
    /// </summary>
    [Test]
    public async Task AnotherPrimaryKeepsTheSidearmAndGetsItsOwnAmmunition()
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

            body = ent.SpawnEntity(Commander, map.GridCoords);
            loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>(Job));
            loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(null, null, "RMCWeaponRifleM54C"), Commander);
        });

        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var snapshot = SurvivorLoadoutSnapshot.Capture(ent, body);
            var belt = snapshot.Nested.Where(n => n.Slot == "belt").Select(n => n.Item).ToList();
            var pouch = snapshot.Nested.Where(n => n.Slot == "pocket1").Select(n => n.Item).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Slots.GetValueOrDefault("suitstorage"), Is.EqualTo("RMCWeaponRifleM54C"));
                Assert.That(belt, Does.Contain(Mateba), "the Mateba survives a different primary pick");
                Assert.That(belt.Count(i => i.StartsWith("RMCSpeedLoaderMateba", System.StringComparison.Ordinal)),
                    Is.GreaterThanOrEqualTo(2), "and it keeps its loads");
                Assert.That(pouch.Count(i => i.StartsWith("CMMagazineRifleM54C", System.StringComparison.Ordinal)),
                    Is.EqualTo(4), $"the pouch holds the M54C magazines [{string.Join(", ", pouch)}]");
                Assert.That(pouch.Any(i => i.StartsWith(M39Mag, System.StringComparison.Ordinal)), Is.False,
                    "no M39 magazines are left behind");
            });

            if (!ent.Deleted(body))
                ent.DeleteEntity(body);
        });

        await pair.CleanReturnAsync();
    }
}
