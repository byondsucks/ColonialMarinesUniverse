using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.CMU14.Survivors;
using Content.Shared.CMU14.Threats;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Inventory;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     Guards the wiring every survivor role depends on: the body must name its own job (otherwise the spawn falls
///     back to the generic stranded job, which is W-Y derived and hands out that job's faction, rank, icon and
///     skills), that job must exist, and the body must actually be dressed by its role gear.
/// </summary>
/// <remarks>
///     The CLF Spec Ops medic spawned with the W-Y faction, a PMC rank, a W-Y job icon, no medical skills and a
///     second primary because its body named no job. These checks cover all ten survivor factions.
/// </remarks>
[TestFixture]
public sealed class SurvivorRoleWiringTest
{
    /// <summary>The stranded fallback job: a survivor resolving to it has lost its own role identity.</summary>
    private const string StrandedJob = "AU14JobStrandedThirdPartyMember";


    [Test]
    public async Task EverySurvivorRoleNamesItsOwnJobAndSpawnsDressed()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var map = await pair.CreateTestMap();

        var bodies = new List<(string Party, string Body, EntityUid Entity)>();
        var failures = new List<string>();

        await server.WaitAssertion(() =>
        {
            // Scoped to the survivor role registry: that is exactly what the role picker offers. Legacy third-party
            // parties also carry the survivor announcement flag but are filtered out of the survivor pool.
            foreach (var role in prototypes.EnumeratePrototypes<SurvivorRolePrototype>()
                         .Where(candidate => candidate.Party != null)
                         .OrderBy(candidate => candidate.ID, StringComparer.Ordinal))
            {
                var partyId = role.Party!.Value.Id;
                var body = role.ID;

                if (!prototypes.HasIndex<EntityPrototype>(body))
                {
                    failures.Add($"{partyId}/{body}: role is registered but has no body prototype");
                    continue;
                }

                var entity = ent.SpawnEntity(body, map.GridCoords);
                bodies.Add((partyId, body, entity));
            }
        });

        // Gear is equipped on map init; give it the tick it needs before inspecting.
        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var inventory = ent.System<InventorySystem>();

            foreach (var (party, body, entity) in bodies)
            {
                if (!ent.TryGetComponent<GhostRoleComponent>(entity, out var ghostRole))
                {
                    failures.Add($"{party}/{body}: no ghost role, so the survivor cannot resolve a job");
                    continue;
                }

                if (ghostRole.JobProto is not { } jobId)
                {
                    failures.Add($"{party}/{body}: ghost role names no job, so the survivor would fall back to the stranded job");
                }
                else if (jobId.Id == StrandedJob)
                {
                    failures.Add($"{party}/{body}: resolves to the stranded job instead of its own role");
                }
                else if (!prototypes.HasIndex<JobPrototype>(jobId))
                {
                    failures.Add($"{party}/{body}: names job {jobId.Id}, which does not exist");
                }

                // A body whose gear never applied is the naked-survivor bug: no jumpsuit and no outer layer.
                var dressed = inventory.TryGetSlotEntity(entity, "jumpsuit", out _)
                              || inventory.TryGetSlotEntity(entity, "outerClothing", out _);

                // A body dressed by its role preset is clothed the moment it spawns; a body dressed by its job's
                // startingGear is clothed when the spawn path adds the job, which a bare spawn does not do.
                if (!dressed && !JobSuppliesGear(prototypes, ghostRole.JobProto))
                {
                    failures.Add(
                        $"{party}/{body}: spawned without a jumpsuit or outer layer, and its job supplies no gear");
                }
            }
        });

        await server.WaitAssertion(() =>
        {
            foreach (var (_, _, entity) in bodies)
                ent.DeleteEntity(entity);
        });

        Assert.That(bodies, Is.Not.Empty, "the survivor factions must expose at least one role body");
        Assert.That(failures, Is.Empty, string.Join("\n", failures));

        await pair.CleanReturnAsync();
    }

    /// <summary>
    ///     The commander sidearm bug: a loadout's <c>commanderRoles</c> named ids that never matched a real role
    ///     body, so no commander was ever given the sidearm its config described. Also checks that every firearm a
    ///     loadout offers can resolve its own ammunition, which is what the strip/stock passes rely on.
    /// </summary>
    [Test]
    public async Task CommanderRolesAndFirearmAmmunitionResolveForEveryLoadout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var map = await pair.CreateTestMap();

        var failures = new List<string>();
        var spawned = new List<EntityUid>();
        var itemSlots = ent.System<ItemSlotsSystem>();

        await server.WaitAssertion(() =>
        {
            var registered = prototypes.EnumeratePrototypes<SurvivorRolePrototype>()
                .Where(role => role.Party != null)
                .ToDictionary(role => role.ID, role => role.Party!.Value.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var loadout in prototypes.EnumeratePrototypes<SurvivorLoadoutPrototype>())
            {
                var factions = loadout.Factions.Select(faction => faction.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // A commander sidearm is only issued to a role this loadout knows about, and that role has to
                // belong to one of the loadout's own factions.
                foreach (var commander in loadout.CommanderRoles)
                {
                    if (!registered.TryGetValue(commander, out var party))
                        failures.Add($"{loadout.ID}: commander role {commander} is not a registered survivor role");
                    else if (!factions.Contains(party))
                        failures.Add($"{loadout.ID}: commander role {commander} belongs to {party}, which this loadout does not serve");
                }

                if (!prototypes.HasIndex<EntityPrototype>(loadout.CommanderSidearm))
                    failures.Add($"{loadout.ID}: commander sidearm {loadout.CommanderSidearm} is not a prototype");

                // Every offered firearm must either name its ammunition in the loadout or bring its own, so the
                // ammunition passes always have a family to work with (an empty family left foreign magazines in
                // the kit and stocked nothing).
                foreach (var firearm in loadout.Firearms)
                {
                    if (!prototypes.HasIndex<EntityPrototype>(firearm))
                    {
                        failures.Add($"{loadout.ID}: firearm {firearm} is not a prototype");
                        continue;
                    }

                    var gun = ent.SpawnEntity(firearm.Id, map.GridCoords);
                    spawned.Add(gun);

                    var suppliesItsOwn = false;
                    // Only guns with item slots have a magazine well; shotguns feed from a ballistic provider.
                    if (ent.HasComponent<ItemSlotsComponent>(gun)
                        && itemSlots.TryGetSlot(gun, "gun_magazine", out var magazineSlot)
                        && magazineSlot?.StartingItem != null)
                    {
                        suppliesItsOwn = true;
                    }

                    if (ent.TryGetComponent<BallisticAmmoProviderComponent>(gun, out var ballistic)
                        && (ballistic.Proto != null || ballistic.Whitelist != null))
                    {
                        suppliesItsOwn = true;
                    }

                    if (ent.TryGetComponent<RevolverAmmoProviderComponent>(gun, out var revolver)
                        && revolver.Whitelist != null)
                    {
                        suppliesItsOwn = true;
                    }

                    if (!suppliesItsOwn && !loadout.FirearmAmmo.ContainsKey(firearm.Id))
                    {
                        failures.Add(
                            $"{loadout.ID}: firearm {firearm.Id} supplies no ammunition and the loadout names none");
                    }
                }
            }
        });

        await server.WaitAssertion(() =>
        {
            foreach (var entity in spawned)
                ent.DeleteEntity(entity);
        });

        Assert.That(failures, Is.Empty, string.Join(Environment.NewLine, failures));

        await pair.CleanReturnAsync();
    }

    /// <summary>Whether the role's job issues its own starting kit when the job is added.</summary>
    private static bool JobSuppliesGear(IPrototypeManager prototypes, ProtoId<JobPrototype>? jobId)
        => jobId is { } id
           && prototypes.TryIndex(id, out JobPrototype? job)
           && (job.StartingGear != null || job.RoundComponents.ContainsKey("EquipEntityPreset"));
}
