using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Survivors;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     Spawns every registered survivor role through the production path (role gear first, then that faction's
///     survivor loadout with the weapon the role is allowed to pick) and checks the character that actually comes
///     out: its own job, the worn kit, a webbing, exactly one primary and ammunition for it.
/// </summary>
[TestFixture]
public sealed class SurvivorLoadoutAllRolesTest
{
    [Test]
    public async Task EverySurvivorRoleSpawnsWithItsOwnCompleteKit()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var prototypes = server.ResolveDependency<IPrototypeManager>();
        var map = await pair.CreateTestMap();

        var failures = new List<string>();
        var checkedRoles = 0;
        var checkedArmed = 0;

        foreach (var role in prototypes.EnumeratePrototypes<SurvivorRolePrototype>().OrderBy(r => r.ID))
        {
            if (!prototypes.TryIndex<EntityPrototype>(role.ID, out var bodyProto))
            {
                failures.Add($"{role.ID}: role body prototype is missing");
                continue;
            }

            if (!bodyProto.Components.TryGetComponent("GhostRole", out var ghostComp)
                || ghostComp is not GhostRoleComponent ghostRole
                || ghostRole.JobProto is not { } jobId
                || !prototypes.HasIndex<JobPrototype>(jobId))
            {
                failures.Add($"{role.ID}: no survivor job");
                continue;
            }

            var loadout = LoadoutFor(prototypes, role.ID);
            var firearm = PickFirearm(loadout, role);
            var armour = PickLoadoutOption(loadout?.Armor, loadout?.RoleArmor, role.ID);
            var helmet = PickLoadoutOption(loadout?.Helmets, loadout?.RoleHelmets, role.ID);

            EntityUid body = default;
            await server.WaitAssertion(() =>
            {
                var loadouts = ent.System<SurvivorLoadoutSystem>();
                body = ent.SpawnEntity(role.ID, map.GridCoords);
                loadouts.EnsureRoleGear(body, jobId);

                if (loadout != null)
                {
                    loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(armour, helmet, firearm), role.ID);
                }
            });

            await pair.RunTicksSync(2);

            await server.WaitAssertion(() =>
            {
                var snapshot = SurvivorLoadoutSnapshot.Capture(ent, body);
                var roleFailures = new List<string>();

                try
                {
                    SurvivorLoadoutAssertions.AssertCore($"{role.ID} ({role.Category})", snapshot,
                        expectFirearm: firearm != null);
                }
                catch (AssertionException ex)
                {
                    roleFailures.Add(ex.Message);
                }

                failures.AddRange(roleFailures);
                checkedRoles++;
                if (firearm != null)
                    checkedArmed++;

                if (!ent.Deleted(body))
                    ent.DeleteEntity(body);
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(checkedRoles, Is.GreaterThanOrEqualTo(60),
                $"every registered survivor role must be exercised, checked {checkedRoles}");
            Assert.That(checkedArmed, Is.GreaterThanOrEqualTo(40),
                $"most survivor roles are armed, checked {checkedArmed}");
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>The survivor loadout that serves the faction which spawns this role.</summary>
    private static SurvivorLoadoutPrototype? LoadoutFor(IPrototypeManager prototypes, string roleId)
    {
        foreach (var party in prototypes.EnumeratePrototypes<Content.Shared.CMU14.Threats.ThirdPartyPrototype>())
        {
            if (!prototypes.TryIndex(party.PartySpawn, out Content.Shared.CMU14.Threats.PartySpawnPrototype? spawn))
                continue;

            var servesRole = spawn.LeadersToSpawn.ContainsKey(roleId) || spawn.GruntsToSpawn.ContainsKey(roleId);
            if (!servesRole)
                continue;

            foreach (var loadout in prototypes.EnumeratePrototypes<SurvivorLoadoutPrototype>())
            {
                if (loadout.Factions.Contains(party.ID))
                    return loadout;
            }
        }

        return null;
    }

    /// <summary>
    ///     The first option a role may pick from a loadout category, respecting per-role overrides, so the run
    ///     exercises the same selection a player would make rather than an empty selection.
    /// </summary>
    private static string? PickLoadoutOption(List<ProtoId<EntityPrototype>>? faction,
        Dictionary<string, List<ProtoId<EntityPrototype>>>? perRole, string roleId)
    {
        if (perRole != null && perRole.TryGetValue(roleId, out var roleOptions) && roleOptions.Count > 0)
            return roleOptions[0].Id;

        return faction is { Count: > 0 } ? faction[0].Id : null;
    }

    /// <summary>The first weapon this role is allowed to pick, mirroring what the picker would offer.</summary>
    /// <summary>
    ///     The weapon the picker would offer this role, mirroring the server rule: a support role (a synthetic, a
    ///     scientist) carries no primary unless the loadout lists firearms for it explicitly.
    /// </summary>
    private static string? PickFirearm(SurvivorLoadoutPrototype? loadout, SurvivorRolePrototype role)
    {
        if (loadout == null)
            return null;

        if (loadout.RoleFirearms.TryGetValue(role.ID, out var allowed) && allowed.Count > 0)
            return allowed[0].Id;

        if (role.Category == SurvivorRoleCategory.Support)
            return null;

        return loadout.Firearms.Count > 0 ? loadout.Firearms[0].Id : null;
    }
}
