using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.CMU14.Round;
using Content.Server.CMU14.Survivors.Setup;
using Content.Shared.CCVar;
using Content.Shared.CMU14.Survivors;
using Content.Shared.CMU14.Threats;
using Content.Server.Ghost.Roles.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.CMU14.ThirdParty;

/// <summary>
///     Checks the survivor rework's content wiring: the role composition table against real factions and
///     the strings the survivor setup window uses. Missing either would only show up at runtime.
/// </summary>
[TestFixture]
public sealed class SurvivorSetupPrototypeTest
{
    [Test]
    public async Task SurvivorRoleCategoriesComposeRealFactions()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var roles = prototypes.EnumeratePrototypes<SurvivorRolePrototype>()
                .ToDictionary(role => role.ID, role => role.Category, StringComparer.OrdinalIgnoreCase);

            Assert.That(roles, Is.Not.Empty, "the survivor role category table must exist");
            foreach (var (id, _) in roles)
                Assert.That(prototypes.HasIndex<EntityPrototype>(id), Is.True,
                    $"survivor role '{id}' is not a real entity prototype");

            SurvivorRoleCategory? CategoryOf(string protoId)
                => roles.TryGetValue(protoId, out var category) ? category : null;

            Assert.That(CategoryOf("AU14JobWYSurvLiaison"), Is.EqualTo(SurvivorRoleCategory.Liaison),
                "the corporate liaison must be the liaison slot");

            // Every survivor faction the pool can offer needs its own named subtype for each role it
            // spawns, so the picker never shows an inherited or foreign role name.
            var survivorParties = new[]
            {
                "CMUProdigySurvivors", "CMUWYPMCSurvivors", "CMUCLFCommandoSurvivors",
                "CMUUSArmySurvivors", "CMUFORECONSurvivors", "FreeCompanyRaidersSurv", "CMUUNISCSurvivors",
                 "CMUDutchsDozenSurvivors",
            };

            var subtypes = prototypes.EnumeratePrototypes<SurvivorRolePrototype>()
                .ToDictionary(role => role.ID, role => (role.Name, role.Party?.Id), StringComparer.Ordinal);

            var roster = new List<(string Party, IEnumerable<string> Roles)>();
            foreach (var party in prototypes.EnumeratePrototypes<ThirdPartyPrototype>()
                         .Where(party => survivorParties.Contains(party.ID) || party.SurvivorPool))
            {
                if (!prototypes.TryIndex(party.PartySpawn, out PartySpawnPrototype? spawn))
                    continue;

                var roleIds = new List<string>();
                roleIds.AddRange(spawn.LeadersToSpawn.Keys);
                roleIds.AddRange(spawn.GruntsToSpawn.Keys);
                roster.Add((party.ID, roleIds));
            }

            var registeredParties = prototypes.EnumeratePrototypes<SurvivorRolePrototype>()
                .Where(role => role.Party != null)
                .Select(role => role.Party!.Value.Id)
                .Distinct()
                .ToList();

            Assert.That(registeredParties, Is.Not.Empty, "the survivor role registry must name its parties");
            Assert.That(roster.Select(entry => entry.Party), Is.SupersetOf(registeredParties),
                "every survivor faction with registered roles must be reachable");

            var findings = SurvivorRoleRegistry.Validate(roster, subtypes);
            Assert.That(findings, Is.Empty,
                "unresolved survivor subtypes: " +
                string.Join(", ", findings.Select(f => $"{f.Issue} {f.Party}/{f.Role}")));

            // Every survivor faction briefs its players: the spawn text must exist and resolve.
            foreach (var party in prototypes.EnumeratePrototypes<ThirdPartyPrototype>()
                         .Where(party => survivorParties.Contains(party.ID) || party.SurvivorPool)
                         .Where(party => party.AnnounceAsSurvivors))
            {
                Assert.That(party.Intro, Is.Not.Null, $"{party.ID} has no spawn intro");
                Assert.That(party.Story, Is.Not.Null, $"{party.ID} has no spawn story");
                Assert.That(Loc.TryGetString(party.Intro!.Value.Id, out var intro), Is.True,
                    $"{party.ID} intro key {party.Intro.Value.Id} is missing from the locale");
                Assert.That(Loc.TryGetString(party.Story!.Value.Id, out var story), Is.True,
                    $"{party.ID} story key {party.Story.Value.Id} is missing from the locale");
                Assert.That(intro, Is.Not.Empty);
                Assert.That(story, Is.Not.Empty);

                // The text names the colony it landed on, substituted at spawn time. The name sits in the
                // intro for most factions and in the story for the corporate ones, so check the pair.
                var resolved = SurvivorBriefingRules.Resolve("crash_landing",
                    new SurvivorBriefingText(party.Intro, party.Story), null, "Barker's Lament");
                Assert.That($"{resolved.Intro} {resolved.Story}", Does.Contain("Barker's Lament"),
                    $"{party.ID} briefing must name the voted colony");
                Assert.That(resolved.Intro, Does.Not.Contain("{ $colony"), $"{party.ID} left a colony placeholder");
                Assert.That(resolved.Story, Does.Not.Contain("{ $colony"), $"{party.ID} left a colony placeholder");
            }

            // The names are faction-specific rather than inherited from the shared role bodies.
            Assert.Multiple(() =>
            {
                Assert.That(subtypes["CMUSurvivorClfSpecOpsOperative"].Name, Is.EqualTo("CLF Spec Ops Operative"),
                    "CLF survivors are Spec Ops, not the old commando cell");
                Assert.That(subtypes["CMUSurvivorClfSpecOpsLeader"].Name, Is.EqualTo("CLF Spec Ops Unit Leader"));
                Assert.That(subtypes["CMUSurvivorWyPmcMedic"].Name, Is.EqualTo("W-Y PMC Medic (CM)"));
                Assert.That(subtypes["CMUSurvDutchsDozenGrenadier"].Name, Does.Contain("Dutch's Dozen"));
            });

            foreach (var (id, (name, _)) in subtypes)
            {
                if (name != null)
                    Assert.That(name, Does.Not.Contain("Reinforcement"), $"{id} still reads generically");
            }
            Assert.That(CategoryOf("AU14JCLFSurvSurgeon"), Is.EqualTo(SurvivorRoleCategory.Medic),
                "the CLF surgeon must be the medic slot");
            Assert.That(CategoryOf("CMUSurvForeconCommander"), Is.EqualTo(SurvivorRoleCategory.Commander));
            Assert.That(CategoryOf("CMUSurvForeconSquadLeader"), Is.EqualTo(SurvivorRoleCategory.SquadLeader));

            List<string> Build(string spawnId, int groupSize)
                => SurvivorRoleComposition.Build(
                        SurvivorRoleComposition.BuildTemplate(
                            prototypes.Index<PartySpawnPrototype>(spawnId), CategoryOf),
                        groupSize)
                    .Select(slot => slot.Proto)
                    .ToList();

            // Weyland-Yutani: commander, technician, liaison, then riflemen. The support scientist is not
            // part of the composition, so the rifleman body repeats to keep the group at seven.
            // CLF: a single generic leader, a medic and fighters.
            Assert.That(Build("CLFSurvsSpawnSmall", 3), Is.EqualTo(new[]
            {
                "AU14CLFSurvLeader", "AU14JCLFSurvSurgeon", "AU14CLFSurvFighter",
            }));

            // A scheduled four-strong CLF squad offers leader, medic and two fighters.
            Assert.That(Build("CLFSurvsSpawnSmall", 4), Is.EqualTo(new[]
            {
                "AU14CLFSurvLeader", "AU14JCLFSurvSurgeon", "AU14CLFSurvFighter",
                "AU14CLFSurvFighter",
            }));

            // FORECON: commander and squad leader are separate, the smartgunner is the weapon specialist,
            // the marksman is not added as a second specialist and the synth is not assigned at all.
            // FORECON: commander, squad leader, corpsman, technician, the smartgunner as the weapon specialist,
            // the support synthetic, then riflemen. The survivor squad has its own bodies (CMUSurvForecon*).
            // Every distinct role the faction fields is offered once, in category priority order, then riflemen
            // fill the group and the synthetic is appended last: FORECON fields a smartgunner, a marksman and a
            // sniper, all weapon specialists, and each is its own position.
            Assert.That(Build("CMUSurvivorSpawnForecon", 8), Is.EqualTo(new[]
            {
                "CMUSurvForeconCommander", "CMUSurvForeconMedic", "CMUSurvForeconTechnician",
                "CMUSurvForeconSquadLeader", "CMUSurvForeconSmartGunner", "CMUSurvForeconRifleman",
                "CMUSurvForeconMarksman", "CMUSurvForeconSniper", "CMUSurvForeconSynth",
            }));

            // US Army: the heavy gunner is the weapon specialist (not the marksman). The survivor squad has
            // its own bodies (CMUSurvUSArmy*) so its gear can never leak into the USArmy call-in parties.
            Assert.That(Build("CMUSurvivorSpawnUSArmy", 8), Is.EqualTo(new[]
            {
                "CMUSurvUSArmyCommander", "CMUSurvUSArmyMedic", "CMUSurvUSArmyTechnician",
                "CMUSurvUSArmySquadLeader", "CMUSurvUSArmyHeavyGunner", "CMUSurvUSArmyRifleman",
                "CMUSurvUSArmyMarksman", "CMUSurvUSArmyRifleman", "CMUSurvUSArmySynth",
            }));

            // IASF: commander, team lead (squad leader), medic, engineer, pilot as the specialist.
            Assert.That(Build("IASFSurvSpawnLarge", 7), Is.EqualTo(new[]
            {
                "AU14IASFSurvCommander", "AU14IASFsurvMedicalTechnician", "AU14IASFSurvCombatEngineer",
                "AU14IASFSurvTeamLead", "AU14IASFSurvPilot", "AU14IASFSurvRifleman",
                "AU14IASFSurvRifleman",
            }));

            // The W-Y PMC survivor party is the Solaris Ridge crash-landing detachment: exactly one body per
            // position, so no category is offered twice.
            Assert.That(Build("CMUSurvivorSpawnWyPmc", 7), Is.EqualTo(new[]
            {
                "CMUSurvivorWyPmcFieldOpsLeader", "CMUSurvivorWyPmcMedic", "CMUSurvivorWyPmcEngineer",
                "CMUSurvivorWyPmcLeader", "CMUSurvivorWyPmcSupervisor", "CMUSurvivorWyPmcStandard",
                "CMUSurvivorWyPmcStandard", "CMUSurvivorWyPmcSynth",
            }));

            // The Prodigy survivor group is the CMUProdigySurvivors party (asserted by the registry test); the
            // older ProdigySurvSpawnSmall party is a legacy third-party spawn that no longer hosts survivors.
            // CLF Spec Ops: unit leader, medic, technician, heavy weapons, then the operative section.
            Assert.That(Build("CMUSurvivorSpawnClfCommando", 7), Is.EqualTo(new[]
            {
                "CMUSurvivorClfSpecOpsMedic", "CMUSurvivorClfSpecOpsTechnician", "CMUSurvivorClfSpecOpsLeader",
                "CMUSurvivorClfSpecOpsHeavy", "CMUSurvivorClfSpecOpsOperative", "CMUSurvivorClfSpecOpsOperative",
                "CMUSurvivorClfSpecOpsOperative",
            }));

            // A group larger than the template keeps filling riflemen from the faction's rifleman body.
            var clfOverflow = Build("CLFSurvsSpawnSmall", 10);
            Assert.That(clfOverflow, Has.Count.EqualTo(10), "every selected survivor gets a slot");
            Assert.That(clfOverflow.Count(proto => proto == "AU14CLFSurvFighter"), Is.EqualTo(8),
                "overflow players become riflemen, not duplicates of the leader or medic");
            Assert.That(clfOverflow.Count(proto => proto == "AU14CLFSurvLeader"), Is.EqualTo(1));
            Assert.That(clfOverflow.Count(proto => proto == "AU14JCLFSurvSurgeon"), Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SurvivorTestFactionPoolOverrideProvidesTheThreeTestFactions()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var cfg = server.ResolveDependency<IConfigurationManager>();
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var systems = server.ResolveDependency<IEntitySystemManager>();
            var round = systems.GetEntitySystem<AuRoundSystem>();
            var roles = prototypes.EnumeratePrototypes<SurvivorRolePrototype>()
                .ToDictionary(role => role.ID, role => role.Category, StringComparer.OrdinalIgnoreCase);

            SurvivorRoleCategory? CategoryOf(string protoId)
                => roles.TryGetValue(protoId, out var category) ? category : null;

            try
            {
                const string configured =
                    "CMUProdigySurvivors, CMUWYPMCSurvivors ,CMUCLFCommandoSurvivors," +
                    "CMUUSArmySurvivors,CMUFORECONSurvivors";
                cfg.SetCVar(CCVars.SurvivorTestFactions, configured);

                var factions = round.GetEligibleSurvivorFactions();

                // Compared against the configured list (parsed rather than duplicated) so the assertion tracks
                // the CVar instead of a second hardcoded copy of it.
                Assert.That(factions.Select(faction => faction.ID),
                    Is.EqualTo(SurvivorTestFactionPool.ParseIds(configured)),
                    "the test override puts every configured faction in the eligible pool");

                foreach (var faction in factions)
                {
                    Assert.That(prototypes.TryIndex(faction.PartySpawn, out PartySpawnPrototype? spawn), Is.True,
                        $"{faction.ID} must have a survivor spawn template");
                    Assert.That(faction.RoundStart, Is.True, $"{faction.ID} must be a round-start survivor party");
                    Assert.That(faction.AnnounceAsSurvivors, Is.True);
                    Assert.That(faction.DisplayName ?? faction.ID, Is.Not.Null.And.Not.Empty,
                        $"{faction.ID} needs a name for the available-factions list");

                    var slots = SurvivorRoleComposition.Build(
                        SurvivorRoleComposition.BuildTemplate(spawn!, CategoryOf),
                        groupSize: 4);

                    Assert.That(slots, Is.Not.Empty, $"{faction.ID} must build a squad");
                    Assert.That(slots.Any(slot => slot.Category is SurvivorRoleCategory.Commander
                        or SurvivorRoleCategory.SquadLeader
                        or SurvivorRoleCategory.Leader), Is.True, $"{faction.ID} must field a leader");
                    // The group itself is exactly four; a faction with a synthetic also gets its support slot,
                    // which is appended past the group size by design.
                    Assert.That(slots.Count(slot => slot.Category != SurvivorRoleCategory.Support), Is.EqualTo(4),
                        $"{faction.ID} must fill the whole selected group when it has a rifleman body");
                    Assert.That(slots.Count(slot => slot.Category == SurvivorRoleCategory.Support),
                        Is.LessThanOrEqualTo(1), $"{faction.ID} fields at most one synthetic");
                }

                // The placeholder factions reuse their family's real squad template.
                Assert.That(prototypes.Index<ThirdPartyPrototype>("CMUCLFCommandoSurvivors").PartySpawn.Id,
                    Is.EqualTo("CMUSurvivorSpawnClfCommando"));
                Assert.That(prototypes.Index<ThirdPartyPrototype>("CMUUSArmySurvivors").PartySpawn.Id,
                    Is.EqualTo("CMUSurvivorSpawnUSArmy"),
                    "the survivor squad has its own spawn template, so its roles are never the call-in ones");
                Assert.That(prototypes.Index<ThirdPartyPrototype>("CMUFORECONSurvivors").PartySpawn.Id,
                    Is.EqualTo("CMUSurvivorSpawnForecon"),
                    "the survivor squad has its own spawn template, so its roles are never the call-in ones");

                // The W-Y PMC option is a real round-start survivor party that reuses the PMC squad template.
                Assert.That(prototypes.TryIndex<ThirdPartyPrototype>("CMUWYPMCSurvivors", out var wyPmcTest), Is.True,
                    "the temporary W-Y PMC survivor party must exist");
                Assert.That(wyPmcTest!.RoundStart, Is.True, "the test option must be scheduled at round start");
                Assert.That(wyPmcTest.AnnounceAsSurvivors, Is.True);
                Assert.That(wyPmcTest.EntryMethod, Is.EqualTo("ground"));
                Assert.That(wyPmcTest.PartySpawn.Id, Is.EqualTo("CMUSurvivorSpawnWyPmc"),
                    "the survivor option uses its own survivor-only squad template");

                // The normal shuttle call-in must be untouched.
                Assert.That(prototypes.TryIndex<ThirdPartyPrototype>("WYPMCParty", out var wyPmcCallIn), Is.True);
                Assert.That(wyPmcCallIn!.RoundStart, Is.False, "the normal W-Y PMC call-in must stay a call-in");
                Assert.That(wyPmcCallIn.EntryMethod, Is.EqualTo("shuttle"));

                // The survivor squad uses survivor-only roles; the call-in keeps the shared ones.
                Assert.That(prototypes.TryIndex(wyPmcTest.PartySpawn, out PartySpawnPrototype? wyPmcTestSpawn), Is.True);
                Assert.That(prototypes.TryIndex(wyPmcCallIn.PartySpawn, out PartySpawnPrototype? wyPmcCallInSpawn), Is.True);
                Assert.That(RoleIds(wyPmcTestSpawn!).All(id =>
                        id.StartsWith("CMUSurvivorWyPmc", StringComparison.Ordinal) ||
                        id.StartsWith("CMUSurvivorWyPmc", StringComparison.Ordinal)), Is.True,
                    "the survivor squad must only reference survivor-only W-Y PMC roles");
                Assert.That(RoleIds(wyPmcCallInSpawn!).All(id => id.StartsWith("AU14JobWYPMCParty", StringComparison.Ordinal)), Is.True,
                    "the shuttle call-in must keep its original W-Y PMC roles");
            }
            finally
            {
                cfg.SetCVar(CCVars.SurvivorTestFactions, "");
            }

            // Without a planet only the factions that are part of the survivor pool by themselves remain.
            Assert.That(round.GetEligibleSurvivorFactions().Select(party => party.ID),
                Is.EquivalentTo(prototypes.EnumeratePrototypes<ThirdPartyPrototype>()
                    .Where(party => party.SurvivorPool && party.AnnounceAsSurvivors)
                    .Select(party => party.ID)),
                "clearing the override returns to normal eligibility (no planet selected here)");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SurvivorSetupStringsExist()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var localization = server.ResolveDependency<ILocalizationManager>();

            string[] setup =
            [
                "cmu-survivor-setup-title",
                "cmu-survivor-setup-vote",
                "cmu-survivor-setup-pick",
                "cmu-survivor-setup-time",
                "cmu-survivor-setup-party",
                "cmu-survivor-setup-mine",
                "cmu-survivor-setup-faction",
                "cmu-survivor-setup-roles",
                "cmu-survivor-setup-phase-vote",
                "cmu-survivor-setup-phase-pick",
                "cmu-survivor-setup-vote-heading",
                "cmu-survivor-setup-loadout",
                "cmu-survivor-setup-badge-open",
                "cmu-survivor-setup-badge-yours",
                "cmu-survivor-setup-badge-taken",
                "cmu-survivor-setup-badge-raffle",
                "cmu-survivor-setup-slot-holder",
                "cmu-survivor-setup-slot-raffle-names",
                "cmu-survivor-setup-slot-free-hint",
                "cmu-survivor-setup-character",
                "cmu-survivor-setup-random",
                "cmu-survivor-setup-group",
                "cmu-survivor-setup-available",
                "cmu-survivor-setup-armor",
                "cmu-survivor-setup-helmet",
                "cmu-survivor-setup-firearm",
                "cmu-survivor-setup-confirm-loadout",
                "cmu-survivor-setup-loadout-confirmed",
                "cmu-survivor-setup-loadout-unconfirmed",
                "cmu-survivor-setup-roster-name",
                "cmu-survivor-setup-roster-details",
                "cmu-survivor-setup-roster-self",
                "cmu-survivor-setup-roster-no-role",
                "cmu-survivor-setup-roster-no-vote",
                "cmu-survivor-setup-join-blocked",
            ];

            foreach (var key in setup)
                Assert.That(localization.HasString(key), Is.True, $"missing survivor setup string '{key}'");

            Assert.That(localization.HasString("cmu-survivor-no-body-return-to-lobby"), Is.True,
                "players who miss out on a survivor slot need the return-to-lobby message");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SurvivorSpawnsUseSurvivorOnlyRolesAndCallInsKeepTheirs()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();

            (string Spawn, string Prefix)[] survivorSpawns =
            {
                ("ProdigySurvSpawnSmall", "CMU14JobProdigySurv"),
                ("WEYUSurvSpawnSmall", "AU14JobWYSurv"),
                ("CMUSurvivorSpawnWyPmc", "CMUSurvivorWyPmc"),
                ("CMUSurvivorSpawnClfCommando", "CMUSurvivorClfSpecOps"),
                ("CMUSurvivorSpawnUSArmy", "CMUSurvUSArmy"),
                ("CMUSurvivorSpawnForecon", "CMUSurvForecon"),
                ("CMUSurvivorSpawnFreeCompany", "CMUSurvFreeCompany"),
                ("CMUSurvivorSpawnUnisc", "CMUSurvUnisc"),
            };

            foreach (var (spawnId, prefix) in survivorSpawns)
            {
                var ids = RoleIds(prototypes.Index<PartySpawnPrototype>(spawnId)).ToList();
                Assert.That(ids, Is.Not.Empty, $"{spawnId} must reference survivor roles");
                Assert.That(ids.All(id => id.StartsWith(prefix, StringComparison.Ordinal)), Is.True,
                    $"{spawnId} must only reference {prefix}* survivor roles, but has [{string.Join(", ", ids)}]");

                foreach (var id in ids)
                    Assert.That(prototypes.HasIndex<EntityPrototype>(id), Is.True, $"{spawnId} references missing {id}");
            }

            // Shared call-in templates must not have been touched by the survivor split.
            string[] callInSpawns =
            {
                "WYPMCPartySpawn",
                "CLFCommandoSpawnIntel",
                "BEARSpawn",
                "UNISCTeamSpawn",
                "RaidersSpawn",
            };

            foreach (var spawnId in callInSpawns)
            {
                var ids = RoleIds(prototypes.Index<PartySpawnPrototype>(spawnId)).ToList();
                Assert.That(ids, Is.Not.Empty, $"{spawnId} must still reference its roles");
                Assert.That(ids.All(id => !id.StartsWith("CMUSurvivor", StringComparison.Ordinal)), Is.True,
                    $"{spawnId} must keep its original roles, but has [{string.Join(", ", ids)}]");
            }
        });

        await pair.CleanReturnAsync();
    }

    private static IEnumerable<string> RoleIds(PartySpawnPrototype spawn)
        => spawn.LeadersToSpawn.Keys.Concat(spawn.GruntsToSpawn.Keys);



    [Test]
    public async Task AllSurvivorRoleEntitiesAreSpawnableMobs()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var factory = server.ResolveDependency<IComponentFactory>();

            var roles = prototypes.EnumeratePrototypes<ThirdPartyPrototype>()
                .Where(party => party.RoundStart && party.AnnounceAsSurvivors)
                .SelectMany(party => prototypes.Index<PartySpawnPrototype>(party.PartySpawn).LeadersToSpawn.Keys
                    .Concat(prototypes.Index<PartySpawnPrototype>(party.PartySpawn).GruntsToSpawn.Keys))
                .Distinct()
                .ToList();

            Assert.That(roles, Is.Not.Empty, "there must be survivor roles to spawn");

            foreach (var id in roles)
            {
                var proto = prototypes.Index<EntityPrototype>(id);
                Assert.That(proto.Abstract, Is.False, $"{id} must be concrete");
                Assert.That(proto.HideSpawnMenu, Is.False, $"{id} must be visible in the admin spawn panel");
                Assert.That(proto.TryComp<MobStateComponent>(out _, factory), Is.True, $"{id} must be a mob");
                Assert.That(proto.TryComp<GhostRoleComponent>(out _, factory), Is.True, $"{id} must be a ghost role");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DutchesDozenIsASurvivorFactionThatForcesAPredatorRound()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();

            var dutch = prototypes.Index<ThirdPartyPrototype>("CMUDutchsDozenSurvivors");
            Assert.That(dutch.RoundStart, Is.True, "Dutch's Dozen must be a round-start survivor faction");
            Assert.That(dutch.AnnounceAsSurvivors, Is.True);
            Assert.That(dutch.EntryMethod, Is.EqualTo("ground"));
            Assert.That(dutch.SurvivorPool, Is.True, "it must be offered by the survivor vote without a planet listing");
            Assert.That(dutch.ForcesPredatorRound, Is.True, "selecting Dutch's Dozen forces a Predator round");
            Assert.That(dutch.RandomSpecialistRoles, Is.True, "only one of the three specialists is fielded");

            // The rule only fires for Dutch's Dozen: every other survivor faction leaves the round mode alone.
            var winners = prototypes.EnumeratePrototypes<ThirdPartyPrototype>()
                .Where(party => party.RoundStart && party.AnnounceAsSurvivors)
                .ToList();
            Assert.That(winners, Is.Not.Empty);
            foreach (var party in winners)
            {
                Assert.That(SurvivorPredatorRound.ShouldForce(party), Is.EqualTo(party.ID == "CMUDutchsDozenSurvivors"),
                    $"{party.ID} must not change the round mode");
            }

            // Every role in the squad must be a real mob with a job and gear.
            var spawn = prototypes.Index<PartySpawnPrototype>(dutch.PartySpawn);
            var roles = spawn.LeadersToSpawn.Keys.Concat(spawn.GruntsToSpawn.Keys).ToList();
            Assert.That(roles.Count, Is.EqualTo(6), "Arnold, a specialist, a medic, a rifleman and two spare specialists");
            var factory = server.ResolveDependency<IComponentFactory>();
            foreach (var role in roles)
            {
                Assert.That(role.StartsWith("CMUSurvDutchsDozen", StringComparison.Ordinal), Is.True, role);
                var entity = prototypes.Index<EntityPrototype>(role);
                Assert.That(entity.TryComp<Content.Server.Ghost.Roles.Components.GhostRoleComponent>(out var ghost, factory), Is.True,
                    $"{role} ghost role");
                Assert.That(ghost!.JobProto, Is.Not.Null, $"{role} job");
                var job = prototypes.Index<JobPrototype>(ghost.JobProto!.Value);
                Assert.That(job.StartingGear, Is.Not.Null, $"{role} gear");

                var gear = prototypes.Index<StartingGearPrototype>(job.StartingGear!.Value);
                Assert.That(gear.Equipment.ContainsKey("suitstorage"), Is.True, $"{role} weapon");
            }

            // The faction is in the survivor loadout system.
            var loadout = prototypes.EnumeratePrototypes<SurvivorLoadoutPrototype>()
                .FirstOrDefault(candidate => candidate.Factions.Any(faction => faction.Id == dutch.ID));
            Assert.That(loadout, Is.Not.Null, "Dutch's Dozen needs a survivor loadout");
            Assert.That(loadout!.Armor, Is.Not.Empty);
            Assert.That(loadout.Helmets, Is.Not.Empty);
            Assert.That(loadout.Firearms, Is.Not.Empty);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DutchesDozenFieldsArnoldOneSpecialistAMedicAndRifleman()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        await server.WaitAssertion(() =>
        {
            var prototypes = server.ResolveDependency<IPrototypeManager>();
            var dutch = prototypes.Index<ThirdPartyPrototype>("CMUDutchsDozenSurvivors");
            var spawn = prototypes.Index<PartySpawnPrototype>(dutch.PartySpawn);

            SurvivorRoleCategory? Category(string id)
                => prototypes.TryIndex<SurvivorRolePrototype>(id, out var role) ? role.Category : null;

            var specialists = new HashSet<string>();
            for (var seed = 0; seed < 12; seed++)
            {
                // Seed the shuffle so every possible specialist shows up across the rolls.
                var template = SurvivorRoleComposition.BuildTemplate(spawn, Category, _ => seed);
                var slots = SurvivorRoleComposition.Build(template, 4).Select(slot => slot.Proto).ToList();

                Assert.That(slots, Has.Count.EqualTo(4));
                Assert.That(slots[0], Is.EqualTo("CMUSurvDutchsDozenArnold"), "Arnold leads");
                Assert.That(slots[1], Is.EqualTo("CMUSurvDutchsDozenMedic"), "then the medic");
                Assert.That(slots[2], Is.AnyOf("CMUSurvDutchsDozenGrenadier", "CMUSurvDutchsDozenMinigunner",
                    "CMUSurvDutchsDozenFlamer"), "then exactly one weapon specialist");
                Assert.That(slots[3], Is.EqualTo("CMUSurvDutchsDozenRifleman"), "everyone else is a rifleman");

                specialists.Add(slots[2]);
            }

            Assert.That(specialists.Count, Is.GreaterThan(1), "the specialist is picked at random each round");
        });

        await pair.CleanReturnAsync();
    }
}
