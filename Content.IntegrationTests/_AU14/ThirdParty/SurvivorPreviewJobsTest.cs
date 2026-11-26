using System.Linq;
using System.Threading.Tasks;
using Content.Client.Lobby.UI;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     The character editor's job preview rotates through High-priority jobs from the profile. The survivor roll is
///     the internal stranded fallback job - named "Stranded Third Party Member" and previewing as a xeno queen - so
///     the preview list expands it into the dedicated survivor roles. This is a preview-only concern: it must not
///     touch job priorities, assignment or spawning.
/// </summary>
[TestFixture]
public sealed class SurvivorPreviewJobsTest
{
    private const string Stranded = "AU14JobStrandedThirdPartyMember";

    [Test]
    public async Task TheStrandedSurvivorJobPreviewsAsTheSurvivorRoles()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();

            Assert.That(prototypes.HasIndex<JobPrototype>(Stranded), Is.True,
                "the stranded survivor job must still exist - this is a preview change only");

            var profile = HumanoidCharacterProfile.DefaultWithSpecies()
                .WithJobPriority(new ProtoId<JobPrototype>(Stranded), JobPriority.High);

            var entries = LobbyHighJobPreview.GetHighPriorityJobs(profile, prototypes);
            var ids = entries.Select(entry => entry.Job.ID).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(ids, Is.Not.Empty, "the survivor roll still produces preview entries");
                Assert.That(ids, Has.None.EqualTo(Stranded),
                    "the internal stranded job is not previewed - its preview entity is a xeno queen placeholder");
                Assert.That(ids, Has.All.StartsWith("CMUSurvivorJob"),
                    $"only dedicated survivor roles are previewed, saw [{string.Join(", ", ids)}]");
                Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count),
                    "each entry is one complete job, never a duplicate");
                Assert.That(ids.Count, Is.GreaterThanOrEqualTo(20),
                    $"the preview offers variety across the survivor roles, saw {ids.Count}");

                // The body rotates through the dedicated survivor roles, but the caption stays the job the player
                // actually rolls for.
                var strandedName = LobbyHighJobPreview.GetDisplayJobName(prototypes.Index<JobPrototype>(Stranded));
                Assert.That(strandedName, Does.Contain("Stranded"),
                    $"the rolled job's own caption is used verbatim ({strandedName})");

                foreach (var entry in entries)
                {
                    Assert.That(entry.Job.ID, Does.StartWith("CMUSurvivorJob"),
                        "the previewed body is a dedicated survivor role");
                    Assert.That(entry.JobName, Is.EqualTo(strandedName),
                        $"{entry.Job.ID} keeps the rolled job's caption ({entry.JobName})");
                }
            });

            // The priorities the player saved are untouched: this only changes what is previewed.
            var priorities = profile.GetJobPrioritiesForGamemode(null);
            Assert.That(priorities, Does.ContainKey(new ProtoId<JobPrototype>(Stranded)),
                "the player's saved job priorities are unchanged");
        });

        await pair.CleanReturnAsync();
    }
}
