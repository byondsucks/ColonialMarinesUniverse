using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Survivors;
using Content.Shared.Inventory;
using Content.Shared.Storage;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     The commander's randomised sidearm issue must land in the belt that carries the revolver. Stocked through the
///     generic rig search instead, the speed loaders went into the uniform's webbing and the belt beside the gun
///     stayed empty, which reads as the commander having no spare ammunition whatsoever.
/// </summary>
[TestFixture]
public sealed class CommanderSidearmAmmunitionTest
{
    private const string Standard = "RMCSpeedLoaderMateba";
    private const string HighImpact = "RMCSpeedLoaderMatebaHighImpact";

    [Test]
    public async Task TheBeltCarriesTheRolledLoadsAndTheUniformDoesNot()
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
            var loadout = prototypes.Index<SurvivorLoadoutPrototype>("CMUSurvivorLoadoutForecon");
            body = ent.SpawnEntity("CMUSurvForeconCommander", map.GridCoords);
            loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(null, null, "RMCWeaponRifleM54C"),
                "CMUSurvForeconCommander");
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var inventory = ent.System<InventorySystem>();

            Assert.That(inventory.TryGetSlotEntity(body, "belt", out var belt), Is.True,
                "the commander wears a sidearm belt");

            var beltContents = Contents(ent, belt!.Value);
            var beltId = ent.GetComponent<MetaDataComponent>(belt.Value).EntityPrototype?.ID;

            // Each pool is rolled 1-3, so at least one of each is always issued.
            Assert.Multiple(() =>
            {
                Assert.That(beltContents.Count(id => id == Standard), Is.GreaterThanOrEqualTo(1),
                    $"the belt must hold the rolled standard speed loaders ({beltId}: {string.Join(", ", beltContents)})");
                Assert.That(beltContents.Count(id => id == HighImpact), Is.GreaterThanOrEqualTo(1),
                    $"the belt must hold the rolled high-impact speed loaders ({beltId}: {string.Join(", ", beltContents)})");
                Assert.That(beltContents.Count(id => id == "RMCWeaponRevolverMateba"), Is.EqualTo(1),
                    "exactly one revolver is issued");
            });

            // The uniform, its webbing and the untouched pouches must not be used as ammunition storage.
            var slots = inventory.GetSlotEnumerator(body, SlotFlags.All);
            while (slots.MoveNext(out var slot))
            {
                if (slot.ContainedEntity is not { } worn || worn == belt.Value)
                    continue;

                var wornId = ent.GetComponent<MetaDataComponent>(worn).EntityPrototype?.ID ?? "?";
                var strays = Contents(ent, worn).Where(id => id.StartsWith("RMCSpeedLoader", System.StringComparison.Ordinal)).ToList();

                Assert.That(strays, Is.Empty,
                    $"sidearm loads belong in the belt, but {wornId} holds {string.Join(", ", strays)}");
            }
        });

        await server.WaitAssertion(() =>
        {
            ent.DeleteEntity(body);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     Every item inside an entity once, as prototype ids. The storage container is also one of the entity's
    ///     containers, so items are deduplicated by entity rather than counted twice.
    /// </summary>
    private static List<string> Contents(IEntityManager ent, EntityUid holder)
    {
        var seen = new HashSet<EntityUid>();
        var contents = new List<string>();

        if (!ent.TryGetComponent<Robust.Shared.Containers.ContainerManagerComponent>(holder, out var containers))
            return contents;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var item in container.ContainedEntities)
            {
                if (!seen.Add(item))
                    continue;

                contents.Add(ent.GetComponent<MetaDataComponent>(item).EntityPrototype?.ID ?? "?");
            }
        }

        return contents;
    }
}
