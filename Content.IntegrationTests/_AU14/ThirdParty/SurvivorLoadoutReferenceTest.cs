using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Survivors;
using Content.Shared.Roles;
using Content.Shared._RMC14.Marines.Skills;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests._AU14.ThirdParty;

/// <summary>
///     CLF Spec Ops Medic is the reference survivor role: its job, skills and every piece of issued kit are asserted
///     against the entity that actually spawns, not against the preset's declaration.
/// </summary>
[TestFixture]
public sealed class SurvivorLoadoutReferenceTest
{
    private const string Body = "CMUSurvivorClfSpecOpsMedic";
    private const string Job = "CMUSurvivorJobClfSpecOpsMedic";
    private const string Loadout = "CMUSurvivorLoadoutClfCommandos";

    private const string Armor = "CMU14CLFSpecOpsArmorHunter";
    private const string Helmet = "CMU14CLFSpecOpsHelmetRiot";
    private const string Primary = "CMU14WeaponSMGMP5SD";

    [Test]
    public async Task TheSpecOpsMedicSpawnsCompleteAndOwned()
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

            body = ent.SpawnEntity(Body, map.GridCoords);
            // The production order: role gear first, then the survivor's own loadout on top of it.
            loadouts.EnsureRoleGear(body, new ProtoId<JobPrototype>(Job));
            loadouts.Apply(body, loadout, new SurvivorLoadoutSelection(Armor, Helmet, Primary), Body);
        });

        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var snapshot = SurvivorLoadoutSnapshot.Capture(ent, body);
            var skills = ent.GetComponent<SkillsComponent>(body).Skills
                .ToDictionary(kv => kv.Key.Id, kv => kv.Value);

            Assert.Multiple(() =>
            {
                // Dedicated job and its own skills, not a borrowed call-in job.
                var job = prototypes.Index<JobPrototype>(Job);
                Assert.That(job.PlayTimeTracker.Id, Is.EqualTo(Job), "the role records playtime on its own counter");
                Assert.That(skills.GetValueOrDefault("RMCSkillMedical"), Is.GreaterThanOrEqualTo(4),
                    $"a corpsman is a medical specialist: {string.Join(", ", skills)}");
                Assert.That(skills.ContainsKey("RMCSkillSurgery"), Is.True, "and can perform surgery");

                // Issued kit.
                Assert.That(snapshot.Slots.GetValueOrDefault("jumpsuit"), Is.EqualTo("CMU14FatiguesCLFSpecOpsMedic"));
                Assert.That(snapshot.Slots.GetValueOrDefault("outerClothing"), Is.EqualTo(Armor));
                Assert.That(snapshot.Slots.GetValueOrDefault("head"), Is.EqualTo(Helmet));
                // The full medical belt, not the basic one.
                Assert.That(snapshot.Slots.GetValueOrDefault("belt"), Is.EqualTo("RMCBeltMedicalDefibAnalyzerFilled"),
                    $"the role wears the full medical belt: {snapshot.Describe()}");
                Assert.That(snapshot.Nested.Any(n => n.Item == "AU14CMDefibrillator"), Is.True,
                    "that belt carries a defibrillator");
                Assert.That(snapshot.Nested.Any(n => n.Item == "CMHealthAnalyzer"), Is.True,
                    "and a health analyser");

                // A filled surgical webbing worn on the uniform, alongside the ammunition accessory webbing.
                Assert.That(snapshot.Nested.Any(n => n.Slot == "webbing" && n.Item == "CMWebbingSurgicalBlue"),
                    Is.True, $"the surgical webbing is worn on the uniform: {snapshot.Describe()}");
                foreach (var tool in new[] { "CMScalpel", "CMRetractor", "CMCautery", "CMSurgicalLine" })
                {
                    Assert.That(snapshot.Nested.Any(n => n.Item == tool), Is.True,
                        $"the surgical webbing carries {tool}: {snapshot.Describe()}");
                }
                Assert.That(snapshot.Slots.GetValueOrDefault("back"), Is.Not.Null, "the role wears its pack");

                // Exactly the weapon the survivor picked, and no second one hiding in a hand or a bag.
                Assert.That(snapshot.Primaries, Is.EquivalentTo(new[] { Primary }),
                    $"one primary only: {snapshot.Describe()}");
                Assert.That(snapshot.Hands, Does.Not.Contain(Primary),
                    "the chosen weapon is worn, so the gear's in-hand copy must be removed");

                // Ammunition for that weapon, in a rig.
                var mags = snapshot.Nested.Count(n => n.Item.Contains("MagazineSMG"));
                Assert.That(mags, Is.GreaterThanOrEqualTo(4),
                    $"the medic carries spare magazines for the MP5 ({mags} found): {snapshot.Describe()}");

                // Medical kit stays medical, in the pouches it belongs to.
                Assert.That(snapshot.Slots.GetValueOrDefault("pocket2"), Is.EqualTo("RMCPouchMedkitPMCFullAdvanced"),
                    $"the medkit pouch is worn in its pocket: {snapshot.Describe()}");
                Assert.That(snapshot.Nested.Any(n => n.Item == "CMAdvFirstAidKit"), Is.True,
                    "the advanced first aid kit is in the pack");

                // CLF wears its webbing as a uniform accessory rather than a standalone webbing slot.
                Assert.That(snapshot.Nested.Any(n => n.Item == "CMUWebbingCLFMP5Filled"), Is.True,
                    $"the uniform's accessory webbing is fitted: {snapshot.Describe()}");

                // The magazine pouch is worn, not stripped as a "magazine".
                Assert.That(snapshot.Slots.GetValueOrDefault("pocket1"), Is.Not.Null,
                    $"the magazine pouch is worn: {snapshot.Describe()}");

                // Shared survivor contract (webbing is an accessory for this faction).
                SurvivorLoadoutAssertions.AssertCore("CLF Spec Ops Medic", snapshot);
            });
        });

        await server.WaitAssertion(() =>
        {
            if (!ent.Deleted(body))
                ent.DeleteEntity(body);
        });

        await pair.CleanReturnAsync();
    }
}
