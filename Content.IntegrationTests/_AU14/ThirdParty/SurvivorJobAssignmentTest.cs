using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.CMU14.Survivors;
using Content.Shared.Roles;
using Content.Shared._RMC14.Marines.Skills;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     Job-level contract for every survivor role: it must resolve to its own CMU survivor job, that job must own a
///     playtime counter of its own (the counter is what <c>SharedRoleSystem.MindGetAllRoleInfo</c> reads from
///     <c>JobPrototype.PlayTimeTracker</c>), and it must carry explicit skills rather than inherit them from the
///     call-in squad its faction also fields.
/// </summary>
[TestFixture]
public sealed class SurvivorJobAssignmentTest
{
    /// <summary>The fallback job stranded survivors use; no role may be assigned it.</summary>
    private const string StrandedJob = "AU14JobStrandedThirdPartyMember";

    [Test]
    public async Task EverySurvivorRoleHasItsOwnJobTrackerAndSkills()
    {
        await using var pool = await PoolManager.GetServerClient();
        var server = pool.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var ent = server.EntMan;

            var failures = new List<string>();
            var trackers = new Dictionary<string, string>();
            var roles = 0;

            foreach (var role in prototypes.EnumeratePrototypes<SurvivorRolePrototype>().OrderBy(r => r.ID))
            {
                roles++;

                if (!prototypes.TryIndex<EntityPrototype>(role.ID, out var body))
                {
                    failures.Add($"{role.ID}: no role body prototype");
                    continue;
                }

                // Resolve the job the way the spawn path does: through the body's ghost role.
                var ghostRole = FindGhostRole(ent, prototypes, body);
                if (ghostRole?.JobProto is not { } jobId)
                {
                    failures.Add($"{role.ID}: the body names no job");
                    continue;
                }

                if (!prototypes.TryIndex(jobId, out JobPrototype? job))
                {
                    failures.Add($"{role.ID}: job {jobId} does not exist");
                    continue;
                }

                if (jobId == StrandedJob || !jobId.Id.StartsWith("CMUSurvivorJob", System.StringComparison.Ordinal))
                {
                    failures.Add($"{role.ID}: spawns as {jobId}, which is not its own survivor job");
                    continue;
                }

                // Its own playtime counter, and a real prototype for it.
                if (job.PlayTimeTracker.Id != jobId.Id)
                    failures.Add($"{role.ID}: job {jobId} records playtime on {job.PlayTimeTracker}");

                if (!prototypes.HasIndex<Content.Shared.Players.PlayTimeTracking.PlayTimeTrackerPrototype>(
                        job.PlayTimeTracker))
                {
                    failures.Add($"{role.ID}: playtime tracker {job.PlayTimeTracker} does not exist");
                }

                if (trackers.TryGetValue(job.PlayTimeTracker.Id, out var owner))
                    failures.Add($"{role.ID}: shares playtime tracker {job.PlayTimeTracker} with {owner}");
                else
                    trackers[job.PlayTimeTracker.Id] = role.ID;

                // The character editor resolves a role's job from the body id by this convention (the roster carries
                // the body, not the job), so it must stay exact.
                if (DeriveJobId(role.ID) != jobId.Id)
                    failures.Add($"{role.ID}: job {jobId} does not follow the survivor job naming convention");

                // No inherited preview entity: the stranded job's xeno queen preview made the roster card show a
                // queen, and a non-null preview entity also makes the lobby skip applying the player's appearance.
                if (job.JobPreviewEntity != null)
                    failures.Add($"{role.ID}: job {jobId} still previews {job.JobPreviewEntity}");

                // Explicit skills, appropriate to the role.
                var skills = JobSkills(job);
                if (skills.Count < 4)
                {
                    failures.Add($"{role.ID}: job {jobId} has {skills.Count} skills");
                    continue;
                }

                switch (role.Category)
                {
                    case SurvivorRoleCategory.Medic when skills.GetValueOrDefault("RMCSkillMedical") < 3:
                        failures.Add($"{role.ID}: a medic has Medical {skills.GetValueOrDefault("RMCSkillMedical")}");
                        break;
                    case SurvivorRoleCategory.Technician
                        when skills.GetValueOrDefault("RMCSkillConstruction") < 3
                             && skills.GetValueOrDefault("RMCSkillEngineer") < 3:
                        failures.Add($"{role.ID}: a technician has neither Construction nor Engineer at 3");
                        break;
                    case SurvivorRoleCategory.Commander or SurvivorRoleCategory.SquadLeader
                        or SurvivorRoleCategory.Leader
                        when skills.GetValueOrDefault("RMCSkillFirearms") < 2:
                        failures.Add($"{role.ID}: a leader has Firearms {skills.GetValueOrDefault("RMCSkillFirearms")}");
                        break;
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(roles, Is.GreaterThanOrEqualTo(60), $"every survivor role must be checked, saw {roles}");
                Assert.That(failures, Is.Empty, string.Join("\n", failures));
            });
        });

        // A dirty pair that is never clean-returned is reported as skipped even when its assertions pass.
        await pool.CleanReturnAsync();
    }

    /// <summary>The skills the job itself declares, which is what the survivor spawn path applies.</summary>
    private static Dictionary<string, int> JobSkills(JobPrototype job)
    {
        var skills = new Dictionary<string, int>();
        if (!job.RoundComponents.TryGetValue("Skills", out var entry) || entry.Component is not SkillsComponent comp)
            return skills;

        foreach (var (skill, level) in comp.Skills)
            skills[skill.Id] = level;

        return skills;
    }

    /// <summary>Finds the ghost role on a body or on the first parent that defines one.</summary>
    private static GhostRoleComponent? FindGhostRole(IEntityManager ent, IPrototypeManager prototypes,
        EntityPrototype body)
    {
        if (body.Components.TryGetComponent("GhostRole", out var own) && own is GhostRoleComponent ghost)
            return ghost;

        foreach (var parentId in body.Parents)
        {
            if (prototypes.TryIndex(parentId, out EntityPrototype? parent))
            {
                if (FindGhostRole(ent, prototypes, parent) is { } inherited)
                    return inherited;
            }
        }

        return null;
    }

    /// <summary>The job id the character editor derives from a survivor role body id.</summary>
    private static string? DeriveJobId(string roleId)
    {
        foreach (var prefix in new[] { "CMUSurvivor", "CMUSurv", "CMU14Job", "CMUJob", "AU14Job", "AU14", "CMU" })
        {
            if (!roleId.StartsWith(prefix, System.StringComparison.Ordinal))
                continue;

            return "CMUSurvivorJob" + roleId[prefix.Length..];
        }

        return null;
    }
}
