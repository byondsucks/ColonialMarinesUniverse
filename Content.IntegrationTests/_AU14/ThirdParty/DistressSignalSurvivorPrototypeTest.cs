using System.Collections.Generic;
using System.Linq;
using Content.Server.CMU14.Threats;
using Content.Shared.CMU14.Threats;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.CMU14.ThirdParty;

[TestFixture]
public sealed class DistressSignalSurvivorPrototypeTest
{
    [Test]
    public async Task SurvivorPartiesAndAnnouncementsAreValid()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var localization = server.ResolveDependency<ILocalizationManager>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var survivorParties = prototypes
                .EnumeratePrototypes<ThirdPartyPrototype>()
                .Where(party => party.AnnounceAsSurvivors)
                .ToDictionary(party => party.ID, StringComparer.OrdinalIgnoreCase);

            // The exact set is deliberately not pinned: legacy third-party parties still carry the survivor
            // flag and are filtered out of the vote by the survivor role registry. What must hold is that every
            // survivor faction (and the civilian group) announces, and that each one is structurally valid.
            string[] expected =
            {
                "CMUProdigySurvivors", "CMUWYPMCSurvivors", "CMUCLFCommandoSurvivors",
                "CMUUSArmySurvivors", "CMUFORECONSurvivors", "FreeCompanyRaidersSurv", "CMUUNISCSurvivors",
                "CMUDutchsDozenSurvivors",
            };
            Assert.That(survivorParties.Keys, Is.SupersetOf(expected),
                "every survivor faction and the civilian group must announce as survivors");
            foreach (var (id, party) in survivorParties)
            {
                Assert.That(
                    prototypes.TryIndex(party.PartySpawn, out PartySpawnPrototype spawn),
                    Is.True,
                    $"Survivor party '{id}' references missing party spawn '{party.PartySpawn}'.");
                Assert.That(
                    ThreatVoteSelection.CalculateBodyCount(spawn, 100).Total,
                    Is.GreaterThan(0),
                    $"Survivor party '{id}' must spawn at least one player body.");
                if (party.RoundStart)
                {
                    Assert.That(
                        spawn.Scaling,
                        Is.Empty,
                        $"Round-start survivor party '{id}' uses population scaling, which would invalidate its pre-round count.");
                }
            }

            Assert.That(localization.HasString("cmu-distress-signal-survivors-spawning"), Is.True);
            Assert.That(localization.HasString("cmu-distress-signal-no-survivors"), Is.True);
            Assert.That(
                localization.GetString("cmu-distress-signal-survivors-spawning", ("count", 1)),
                Does.Contain("1 survivor"));
            Assert.That(
                localization.GetString("cmu-distress-signal-survivors-spawning", ("count", 2)),
                Does.Contain("2 survivors"));
        });

        await pair.CleanReturnAsync();
    }
}
