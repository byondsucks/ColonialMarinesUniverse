using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.CMU14.Ops.ThirdParty;
using Content.Server.CMU14.Survivors.Setup;
using Content.Shared.Body;
using Content.Shared.CMU14.Survivors;
using Content.Shared.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Threats;
using Content.Shared.Humanoid;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Tests;
using NUnit.Framework;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager;

namespace Content.CMU.Tests.Round;

/// <summary>
///     Focused coverage of the survivor group faction draw and vote, the pre-spawn roster, the role
///     composition and the own-character choice. These are the pure rules the setup system runs on, so
///     they are testable without a live round.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class CMUSurvivorSetupTest : ContentUnitTest
{
    protected override Type[] ExtraComponents => [typeof(InitialBodyComponent)];

    private const string ProfilePrototypes = """
        - type: entity
          id: TestHuman

        - type: skinColoration
          id: HumanToned
          strategy: !type:HumanTonedSkinColoration {}

        - type: species
          id: Human
          name: test-species-human
          roundStart: true
          prototype: TestHuman
          dollPrototype: TestHuman
          skinColoration: HumanToned
        """;

    private static readonly NetUserId Alice = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly NetUserId Bob = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly NetUserId Carol = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    private const string TestSurvivorSpawn = """
        - type: partySpawn
          id: CMUTestSurvivorSpawn
          leadersToSpawn:
            TestSurvivorLeader: 1
          gruntsToSpawn:
            TestSurvivorSurgeon: 1
            TestSurvivorFighter: 2
          scalewithpop: false
          spawnTogether: true
        """;

    [OneTimeSetUp]
    public void InitializeProfilePrototypes()
    {
        IoCManager.Resolve<ISerializationManager>().Initialize();

        var prototypeManager = IoCManager.Resolve<IPrototypeManager>();
        prototypeManager.Initialize();
        prototypeManager.LoadString(ProfilePrototypes);
        prototypeManager.ResolveResults();
        prototypeManager.LoadString(TestSurvivorSpawn);
        prototypeManager.ResolveResults();
    }

    // --- Vote resolution ---------------------------------------------------------------------------

    [Test]
    public void VoteLeadersAreTheFactionsWithTheMostVotes()
    {
        var votes = new Dictionary<string, int> { ["A"] = 4, ["B"] = 1, ["C"] = 4 };

        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "A", "B", "C" }, votes);

        Assert.That(leaders, Is.EquivalentTo(new[] { "A", "C" }));
    }

    [Test]
    public void ACharacterWithAClearPluralityWinsRegardlessOfFactionWeight()
    {
        var votes = new Dictionary<string, int> { ["A"] = 3, ["B"] = 1 };
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "A", "B" }, votes);

        // B is ten times as likely in the tie-break lottery, but it simply has fewer votes.
        var winner = SurvivorVoteTally.PickFromLeaders(leaders, id => id == "B" ? 10 : 1, roll: 9);

        Assert.That(winner, Is.EqualTo("A"));
    }

    [Test]
    public void ATieIsBrokenProportionallyToFactionWeight()
    {
        var votes = new Dictionary<string, int> { ["A"] = 2, ["B"] = 2 };
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "A", "B" }, votes);
        int Weight(string id) => id == "A" ? 1 : 3;

        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, roll: 0), Is.EqualTo("A"));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, roll: 1), Is.EqualTo("B"));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, roll: 3), Is.EqualTo("B"));
    }

    [Test]
    public void NoVotesLeavesEveryFactionInTheLottery()
    {
        var votes = new Dictionary<string, int>();
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "A", "B" }, votes);

        Assert.That(leaders, Is.EquivalentTo(new[] { "A", "B" }));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, _ => 1, roll: 0), Is.EqualTo("A"));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, _ => 1, roll: 1), Is.EqualTo("B"));
    }

    [Test]
    public void OnlyEligibleFactionsCanBeVotedFor()
    {
        // The candidate list is the eligibility gate: factions the pool builder filtered out are not
        // counted, so a vote for them can never resolve the group onto them.
        var votes = new Dictionary<string, int> { ["B"] = 5 };
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "A", "C" }, votes);

        Assert.That(leaders, Is.EquivalentTo(new[] { "A", "C" }));
        Assert.That(leaders, Has.No.Member("B"));
    }

    [Test]
    public void NoCandidatesResolveToNoFaction()
    {
        Assert.That(SurvivorVoteTally.GetVoteLeaders(Array.Empty<string>(), new Dictionary<string, int>()), Is.Empty);
        Assert.That(SurvivorVoteTally.PickFromLeaders(Array.Empty<string>(), _ => 1, 0), Is.Null);
    }

    [Test]
    public void GroupAssignmentSpawnsTheVotedFactionAndDropsTheRest()
    {
        var votes = new Dictionary<string, int> { ["CLF"] = 3, ["WY"] = 1, ["IASF"] = 0 };
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "CLF", "WY", "IASF" }, votes);
        var winner = SurvivorVoteTally.PickFromLeaders(leaders, _ => 1, roll: 0);

        Assert.That(winner, Is.EqualTo("CLF"));
        Assert.That(leaders, Has.Count.EqualTo(1), "a clear winner must not be re-rolled");
    }

    // --- Pre-spawn roster ---------------------------------------------------------------------------

    [Test]
    public void RosterShowsTheWholeGroupOnceSortedByName()
    {
        var entries = new[]
        {
            new SurvivorRoster.Entry(Carol, "Vasquez", "Rifleman", "Vasquez", "CLF"),
            new SurvivorRoster.Entry(Alice, "Apone", null, null, null),
            new SurvivorRoster.Entry(Bob, "Hicks", "Corpsman", "Hicks", "WY"),
            new SurvivorRoster.Entry(Alice, "Apone (duplicate send)", null, null, null),
        };

        var roster = SurvivorRoster.Build(entries);

        Assert.That(roster.Select(member => member.Name),
            Is.EqualTo(new[] { "Apone", "Hicks", "Vasquez" }),
            "the roster is everyone in the group, ordered by name");
        Assert.That(roster.Count(member => member.User == Alice), Is.EqualTo(1),
            "a player must never be listed twice");
        Assert.That(roster.Single(member => member.User == Carol).Vote, Is.EqualTo("CLF"),
            "each member's voted faction is visible before spawning");
        Assert.That(roster.Single(member => member.User == Alice).Role, Is.Null,
            "players who have not picked a role yet are shown without one");
    }

    [Test]
    public void DisconnectedPlayersAreDroppedFromTheVote()
    {
        var held = new[] { Alice, Bob, Carol };

        var gone = SurvivorRoster.FindDisconnected(held, user => user != Bob);

        Assert.That(gone, Is.EqualTo(new[] { Bob }), "only players that lost their session are pruned");
        Assert.That(SurvivorRoster.FindDisconnected(held, _ => true), Is.Empty);
    }

    [Test]
    public void RosterIsConsistentAcrossPlayers()
    {
        var entries = new[]
        {
            new SurvivorRoster.Entry(Alice, "Apone", "Rifleman", "Apone", "CLF"),
            new SurvivorRoster.Entry(Bob, "Hicks", "Corpsman", "Hicks", "CLF"),
        };

        // Every client builds the roster from the same server-side membership, so the lists match.
        var first = SurvivorRoster.Build(entries);
        var second = SurvivorRoster.Build(entries.Reverse());

        Assert.That(first.Select(member => member.Name), Is.EqualTo(second.Select(member => member.Name)));
    }

    // --- Own character ------------------------------------------------------------------------------

    [Test]
    public void ThePlayersOwnCharacterIsPreserved()
    {
        var saved = HumanoidCharacterProfile.DefaultWithSpecies().WithName("Vasquez");
        var characters = new Dictionary<int, HumanoidCharacterProfile> { [2] = saved };

        var resolved = SurvivorCharacterSelection.Resolve(2, characters);

        Assert.That(resolved, Is.SameAs(saved), "the saved profile is used as-is, not regenerated");
        Assert.That(resolved.Name, Is.EqualTo("Vasquez"));
        Assert.That(resolved.Species, Is.EqualTo(saved.Species));
        Assert.That(resolved.Appearance, Is.EqualTo(saved.Appearance), "appearance must survive the survivor roll");
    }

    [Test]
    public void RandomCharacterIsOnlyTheFallback()
    {
        var saved = HumanoidCharacterProfile.DefaultWithSpecies().WithName("Vasquez");
        var characters = new Dictionary<int, HumanoidCharacterProfile> { [0] = saved };
        var fallback = HumanoidCharacterProfile.DefaultWithSpecies().WithName("Random Survivor");
        HumanoidCharacterProfile Fallback() => fallback;

        var noChoice = SurvivorCharacterSelection.Resolve(null, characters, Fallback);
        var missingSlot = SurvivorCharacterSelection.Resolve(7, characters, Fallback);

        Assert.That(noChoice, Is.SameAs(fallback), "no saved character picked means a random one");
        Assert.That(missingSlot, Is.SameAs(fallback), "a stale slot index also falls back to a random one");
    }

    [Test]
    public void ChoosingACharacterDoesNotMutateTheSavedProfile()
    {
        var saved = HumanoidCharacterProfile.DefaultWithSpecies().WithName("Vasquez");
        var appearance = saved.Appearance;
        var characters = new Dictionary<int, HumanoidCharacterProfile> { [0] = saved };

        _ = SurvivorCharacterSelection.Resolve(0, characters);

        Assert.That(saved.Name, Is.EqualTo("Vasquez"));
        Assert.That(saved.Appearance, Is.SameAs(appearance));
    }

    // --- Spawning after the vote --------------------------------------------------------------------

    [Test]
    public void EachChosenRoleSpawnsOneBody()
    {
        var chosen = new List<ThirdPartySystem.SurvivorAssignment>
        {
            Assignment(Alice, "SurvivorRifleman", leader: false),
            Assignment(Bob, "SurvivorRifleman", leader: false),
            Assignment(Carol, "SurvivorLeader", leader: true),
        };

        var grunts = ThirdPartySystem.CountChosenBodies(chosen, leaders: false);
        var leaders = ThirdPartySystem.CountChosenBodies(chosen, leaders: true);

        Assert.That(grunts, Is.EqualTo(new Dictionary<string, int> { ["SurvivorRifleman"] = 2 }));
        Assert.That(leaders, Is.EqualTo(new Dictionary<string, int> { ["SurvivorLeader"] = 1 }));
    }

    [Test]
    public void SurvivalBodyListIsCappedToThePlayersWhoRolled()
    {
        var template = new Dictionary<string, int> { ["SurvivorLeader"] = 1, ["SurvivorRifleman"] = 6 };

        var trimmed = ThirdPartySystem.TrimBodiesTo(template, 3);

        Assert.That(trimmed, Is.EqualTo(new Dictionary<string, int>
        {
            ["SurvivorLeader"] = 1,
            ["SurvivorRifleman"] = 2,
        }));
        Assert.That(ThirdPartySystem.TrimBodiesTo(template, 0), Is.Empty);
        Assert.That(ThirdPartySystem.TrimBodiesTo(template, 100), Is.EqualTo(template));
    }

    // --- Two-option faction draw ---------------------------------------------------------------------

    private static readonly string[] FactionPool =
    {
        "FactionA", "FactionB", "FactionC", "FactionD", "FactionE",
        "FactionF", "FactionG", "FactionH", "FactionI", "FactionJ",
    };

    [Test]
    public void TheGroupIsOfferedTwoDistinctDrawnFactions()
    {
        var options = SurvivorFactionDraw.PickDistinct(FactionPool, _ => 1, _ => 0);

        Assert.That(options, Has.Count.EqualTo(2), "exactly two factions are offered");
        Assert.That(options, Is.Unique, "the same faction must not be offered twice");
        Assert.That(FactionPool, Is.SupersetOf(options), "both options come from the eligible pool");
    }

    [Test]
    public void TestingModeOffersEveryEligibleFactionOnTheBallot()
    {
        var options = SurvivorFactionDraw.AllOptions(FactionPool);

        Assert.That(options, Is.EquivalentTo(FactionPool), "every eligible faction is votable in testing mode");
        Assert.That(options, Is.Unique, "no faction appears twice on the ballot");
        Assert.That(options, Is.Ordered, "the ballot order is stable so the list does not reshuffle between refreshes");

        Assert.That(SurvivorFactionDraw.AllOptions(new[] { "Same", "Same", "Other" }), Is.EqualTo(new[] { "Other", "Same" }),
            "duplicate pool entries collapse to one option");
        Assert.That(SurvivorFactionDraw.AllOptions(Array.Empty<string>()), Is.Empty, "an empty pool offers nothing");
    }

    [Test]
    public void TheNormalDrawStillOffersOnlyTwoFactions()
    {
        // The testing CVar defaults to off, so production keeps the two-faction draw.
        Assert.That(SurvivorFactionDraw.OptionCount, Is.EqualTo(2));
        Assert.That(SurvivorFactionDraw.PickDistinct(FactionPool, _ => 1, _ => 0), Has.Count.EqualTo(2));
    }

    [Test]
    public void TheDrawNeverOffersTheSameFactionTwice()
    {
        for (var start = 0; start < FactionPool.Length; start++)
        {
            var state = start;
            var options = SurvivorFactionDraw.PickDistinct(FactionPool, _ => 1, max => state++ % max);

            Assert.That(options, Is.Unique, $"starting roll {start} produced a duplicate option");
            Assert.That(options, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void AFactionPoolSmallerThanTwoOptionsDegradesSafely()
    {
        Assert.That(SurvivorFactionDraw.PickDistinct(new[] { "OnlyFaction" }, _ => 1, _ => 0),
            Is.EqualTo(new[] { "OnlyFaction" }),
            "a single eligible faction is the group's faction without a vote");
        Assert.That(SurvivorFactionDraw.PickDistinct(Array.Empty<string>(), _ => 1, _ => 0), Is.Empty,
            "no eligible faction offers no options");
        Assert.That(SurvivorFactionDraw.PickDistinct(new[] { "Same", "Same" }, _ => 1, _ => 0),
            Has.Count.EqualTo(1), "duplicate pool entries are a single faction");
    }

    [Test]
    public void TheDrawIsWeightedByFactionWeight()
    {
        var pool = new[] { "common", "rare" };
        int Weight(string id) => id == "common" ? 3 : 1;

        Assert.That(SurvivorFactionDraw.PickDistinct(pool, Weight, _ => 0, 1), Is.EqualTo(new[] { "common" }));
        Assert.That(SurvivorFactionDraw.PickDistinct(pool, Weight, _ => 2, 1), Is.EqualTo(new[] { "common" }));
        Assert.That(SurvivorFactionDraw.PickDistinct(pool, Weight, _ => 3, 1), Is.EqualTo(new[] { "rare" }));
    }

    [Test]
    public void TheWholeGroupTakesTheOptionWithMoreVotes()
    {
        var votes = new Dictionary<string, int> { ["DrawnOne"] = 4, ["DrawnTwo"] = 3 };
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "DrawnOne", "DrawnTwo" }, votes);

        Assert.That(leaders, Is.EqualTo(new[] { "DrawnOne" }));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, _ => 1, 0), Is.EqualTo("DrawnOne"),
            "one faction wins for the entire group; the group is never split between the options");
    }

    [Test]
    public void ATiedTwoOptionVoteFallsBackToFactionWeights()
    {
        var votes = new Dictionary<string, int> { ["DrawnOne"] = 2, ["DrawnTwo"] = 2 };
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "DrawnOne", "DrawnTwo" }, votes);
        int Weight(string id) => id == "DrawnOne" ? 3 : 1;

        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, 0), Is.EqualTo("DrawnOne"));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, 2), Is.EqualTo("DrawnOne"));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, 3), Is.EqualTo("DrawnTwo"));
    }

    [Test]
    public void ANoVoteTwoOptionVoteFallsBackToFactionWeights()
    {
        var leaders = SurvivorVoteTally.GetVoteLeaders(new[] { "DrawnOne", "DrawnTwo" },
            new Dictionary<string, int>());
        int Weight(string id) => id == "DrawnOne" ? 1 : 3;

        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, 0), Is.EqualTo("DrawnOne"));
        Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, Weight, 1), Is.EqualTo("DrawnTwo"));
    }

    // --- Survivor roll cap ---------------------------------------------------------------------------

    private static Dictionary<NetUserId, ProtoId<JobPrototype>> Rollers(int count)
    {
        var rollers = new Dictionary<NetUserId, ProtoId<JobPrototype>>();
        for (var i = 0; i < count; i++)
        {
            var guid = Guid.Parse($"00000000-0000-0000-0000-{(i + 1):000000000000}");
            rollers[new NetUserId(guid)] = new ProtoId<JobPrototype>("AU14JobThirdPartyMember");
        }

        return rollers;
    }

    [Test]
    public void MoreRollersThanScheduledOnlyTheScheduledNumberParticipate()
    {
        var rollers = Rollers(10);

        var selected = SurvivorRollCap.Apply(rollers, scheduled: 4, roll: _ => 0);

        Assert.That(selected, Has.Count.EqualTo(4), "the scheduled survivor count is a hard cap");
        Assert.That(rollers.Keys, Is.SupersetOf(selected.Keys), "only eligible rollers can be selected");
    }

    [Test]
    public void FewerRollersThanScheduledAllRollersParticipate()
    {
        var rollers = Rollers(3);

        var selected = SurvivorRollCap.Apply(rollers, scheduled: 4, roll: _ => 0);

        Assert.That(selected, Is.EquivalentTo(rollers), "every eligible roller participates when there is room");
    }

    [Test]
    public void ZeroOrInvalidScheduledCountsSelectNobody()
    {
        var rollers = Rollers(5);

        Assert.That(SurvivorRollCap.Apply(rollers, 0, _ => 0), Is.Empty,
            "no scheduled survivors means no survivor group");
        Assert.That(SurvivorRollCap.Apply(rollers, -3, _ => 0), Is.Empty,
            "an invalid count is treated as zero");
    }

    [Test]
    public void TheRollSampleIsStableAndDeterministic()
    {
        var rollers = Rollers(10);
        var ordered = rollers.Keys.OrderBy(id => id.UserId).ToList();

        var first = SurvivorRollCap.Apply(rollers, 4, _ => 0);
        var second = SurvivorRollCap.Apply(rollers, 4, _ => 0);

        Assert.That(first.Keys, Is.EqualTo(second.Keys), "the same roll produces the same roster");
        Assert.That(first.Keys, Is.EquivalentTo(ordered.Take(4)), "a zero roll takes the first players in order");
    }

    [Test]
    public void DifferentRollsSelectDifferentPlayers()
    {
        var rollers = Rollers(10);

        var first = SurvivorRollCap.Apply(rollers, 4, _ => 0);
        var second = SurvivorRollCap.Apply(rollers, 4, max => max - 1);

        Assert.That(first.Keys, Is.Not.EquivalentTo(second.Keys),
            "the sample is random instead of always taking the first players");
    }

    // --- Faction role composition --------------------------------------------------------------------

    [Test]
    public void PositionsAreAssignedInPriorityOrder()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Rifleman", 4, SurvivorRoleCategory.Rifleman),
            Role("Liaison", 1, SurvivorRoleCategory.Liaison),
            Role("WeaponSpecialist", 1, SurvivorRoleCategory.WeaponSpecialist),
            Role("Technician", 1, SurvivorRoleCategory.Technician),
            Role("Medic", 1, SurvivorRoleCategory.Medic),
            Role("SquadLeader", 1, SurvivorRoleCategory.SquadLeader),
            Role("Commander", 1, SurvivorRoleCategory.Commander),
        };

        var slots = SurvivorRoleComposition.Build(template, groupSize: 7);

        Assert.That(slots.Select(slot => slot.Proto), Is.EqualTo(new[]
        {
            "Commander", "Medic", "Technician", "SquadLeader", "WeaponSpecialist", "Liaison", "Rifleman",
        }), "composition follows commander, medic, technician, squad leader, specialist, liaison, then a rifleman");
    }

    [Test]
    public void TheFightingRolesComeBeforeTheLiaisonAndTheSynthLast()
    {
        // The shape of a W-Y PMC crash-landing squad: no weapon specialist, a liaison and a support synth.
        List<SurvivorTemplateRole> Template() => new()
        {
            Role("Commander", 1, SurvivorRoleCategory.Commander),
            Role("SquadLeader", 1, SurvivorRoleCategory.SquadLeader),
            Role("Medic", 1, SurvivorRoleCategory.Medic),
            Role("Technician", 1, SurvivorRoleCategory.Technician),
            Role("Liaison", 1, SurvivorRoleCategory.Liaison),
            Role("Synth", 1, SurvivorRoleCategory.Support),
            Role("Rifleman", 1, SurvivorRoleCategory.Rifleman),
        };

        Assert.Multiple(() =>
        {
            Assert.That(SurvivorRoleComposition.Build(Template(), 3).Select(slot => slot.Proto),
                Is.EqualTo(new[] { "Commander", "Medic", "Technician", "Synth" }),
                "the commander and the two trades come first, and the synthetic is always offered");

            Assert.That(SurvivorRoleComposition.Build(Template(), 4).Select(slot => slot.Proto),
                Is.EqualTo(new[] { "Commander", "Medic", "Technician", "SquadLeader", "Synth" }),
                "the squad leader is the fourth position");

            Assert.That(SurvivorRoleComposition.Build(Template(), 5).Select(slot => slot.Proto),
                Is.EqualTo(new[] { "Commander", "Medic", "Technician", "SquadLeader", "Liaison", "Synth" }),
                "with no weapon specialist the fifth slot is the liaison");

            Assert.That(SurvivorRoleComposition.Build(Template(), 6).Select(slot => slot.Proto),
                Is.EqualTo(new[] { "Commander", "Medic", "Technician", "SquadLeader", "Liaison", "Rifleman", "Synth" }),
                "a fighting rifleman comes before the synthetic, which still gets its slot");

            Assert.That(SurvivorRoleComposition.Build(Template(), 7).Select(slot => slot.Proto),
                Is.EqualTo(new[] { "Commander", "Medic", "Technician", "SquadLeader", "Liaison", "Rifleman", "Rifleman", "Synth" }),
                "the synthetic is the last slot and sits past the group size");

            Assert.That(SurvivorRoleComposition.Build(Template(), 9).Select(slot => slot.Proto),
                Is.EqualTo(new[] { "Commander", "Medic", "Technician", "SquadLeader", "Liaison", "Rifleman", "Rifleman", "Rifleman", "Rifleman", "Synth" }),
                "everyone past the named positions is a rifleman, then the synthetic");
        });
    }

    [Test]
    public void TheSyntheticSlotIsOfferedEvenForATinyGroup()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Commander", 1, SurvivorRoleCategory.Commander),
            Role("Synth", 1, SurvivorRoleCategory.Support),
            Role("Rifleman", 1, SurvivorRoleCategory.Rifleman),
        };

        var slots = SurvivorRoleComposition.Build(template, groupSize: 1);

        Assert.That(slots.Select(slot => slot.Proto), Is.EqualTo(new[] { "Commander", "Synth" }),
            "a single-survivor group is still offered the synthetic position");
    }

    [Test]
    public void CommanderAndSquadLeaderAreBothAssignedWhenSupported()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Commander", 1, SurvivorRoleCategory.Commander),
            Role("SquadLeader", 1, SurvivorRoleCategory.SquadLeader),
            Role("Rifleman", 5, SurvivorRoleCategory.Rifleman),
        };

        var slots = SurvivorRoleComposition.Build(template, groupSize: 4);

        Assert.That(slots.Select(slot => slot.Proto), Is.EqualTo(new[]
        {
            "Commander", "SquadLeader", "Rifleman", "Rifleman",
        }), "both leadership positions are fielded; they are not merged into one");
    }

    [Test]
    public void GenericLeadersFillCommanderThenSquadLeader()
    {
        var twoLeaders = new List<SurvivorTemplateRole>
        {
            Role("Leader", 1, SurvivorRoleCategory.Leader),
            Role("DeputyLeader", 1, SurvivorRoleCategory.Leader),
            Role("Rifleman", 5, SurvivorRoleCategory.Rifleman),
        };
        var oneLeader = new List<SurvivorTemplateRole>
        {
            Role("Leader", 1, SurvivorRoleCategory.Leader),
            Role("Rifleman", 5, SurvivorRoleCategory.Rifleman),
        };

        Assert.That(SurvivorRoleComposition.Build(twoLeaders, 4).Select(slot => slot.Proto),
            Is.EqualTo(new[] { "Leader", "DeputyLeader", "Rifleman", "Rifleman" }),
            "unclassified factions with two leader bodies still field both leadership positions");
        Assert.That(SurvivorRoleComposition.Build(oneLeader, 4).Select(slot => slot.Proto),
            Is.EqualTo(new[] { "Leader", "Rifleman", "Rifleman", "Rifleman" }),
            "a single leader body fills one position and is never duplicated");
    }

    [Test]
    public void MissingSpecialRolesAreSkippedWithoutInvention()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Leader", 1, SurvivorRoleCategory.Leader),
            Role("Rifleman", 6, SurvivorRoleCategory.Rifleman),
        };

        var slots = SurvivorRoleComposition.Build(template, groupSize: 3);

        Assert.That(slots.Select(slot => slot.Proto), Is.EqualTo(new[] { "Leader", "Rifleman", "Rifleman" }),
            "a faction without medic, technician, specialist or liaison roles fields no such slots");
    }

    [Test]
    public void SpecialRolesAreNotDuplicatedToFillSlots()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Commander", 2, SurvivorRoleCategory.Commander),
            Role("Medic", 1, SurvivorRoleCategory.Medic),
            Role("Rifleman", 6, SurvivorRoleCategory.Rifleman),
        };

        var slots = SurvivorRoleComposition.Build(template, groupSize: 6);

        Assert.That(slots.Count(slot => slot.Category == SurvivorRoleCategory.Commander), Is.EqualTo(1),
            "a special position is filled once even when the template has more bodies");
        Assert.That(slots.Count(slot => slot.Category == SurvivorRoleCategory.Medic), Is.EqualTo(1));
        Assert.That(slots.Count(slot => slot.Category == SurvivorRoleCategory.Rifleman), Is.EqualTo(4),
            "everyone else becomes a rifleman");
    }

    [Test]
    public void GroupsLargerThanTheTemplateGetExtraRiflemen()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Commander", 1, SurvivorRoleCategory.Commander),
            Role("Goon", 2, SurvivorRoleCategory.Rifleman),
            Role("Shotgun", 1, SurvivorRoleCategory.Rifleman),
        };

        var slots = SurvivorRoleComposition.Build(template, groupSize: 8);

        Assert.That(slots, Has.Count.EqualTo(8), "a group larger than the template still gets a body each");
        Assert.That(slots.Count(slot => slot.Proto == "Commander"), Is.EqualTo(1));
        Assert.That(slots.Skip(1).Select(slot => slot.Proto), Is.EqualTo(new[]
        {
            "Goon", "Goon", "Shotgun", "Goon", "Goon", "Goon", "Goon",
        }), "the template's riflemen come first, then the primary rifleman body is reused");
    }

    [Test]
    public void FinalSlotCountMatchesTheGroupWhenARiflemanExists()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Commander", 1, SurvivorRoleCategory.Commander),
            Role("Medic", 1, SurvivorRoleCategory.Medic),
            Role("Goon", 1, SurvivorRoleCategory.Rifleman),
        };

        for (var groupSize = 1; groupSize <= 12; groupSize++)
        {
            Assert.That(SurvivorRoleComposition.Build(template, groupSize), Has.Count.EqualTo(groupSize),
                $"a group of {groupSize} survivors must get one slot each");
        }
    }

    [Test]
    public void NoRiflemanBodyLeavesSlotsShort()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Commander", 1, SurvivorRoleCategory.Commander),
        };

        Assert.That(SurvivorRoleComposition.HasRiflemanBody(template), Is.False);
        Assert.That(SurvivorRoleComposition.Build(template, groupSize: 5), Has.Count.EqualTo(1),
            "without a rifleman body only the special positions can be filled");
    }

    [Test]
    public void SupportRolesFillTheirOwnCompositionSlot()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Commander", 1, SurvivorRoleCategory.Commander),
            Role("Synth", 1, SurvivorRoleCategory.Support),
            Role("Goon", 2, SurvivorRoleCategory.Rifleman),
        };

        var slots = SurvivorRoleComposition.Build(template, groupSize: 4);

        // A faction's synthetic always spawns, so its slot is offered even when the group is already full - it
        // is the one position that can exceed the group size. Only a synthetic character can claim it.
        Assert.That(slots.Select(slot => slot.Proto), Is.EqualTo(new[] { "Commander", "Goon", "Goon", "Goon", "Synth" }),
            "the group fills first, then the synthetic slot is added past the group size");
    }

    [Test]
    public void TheRoundOffersTheScheduledSquadSize()
    {
        Assert.That(SurvivorRoleComposition.GetSlotCount(4, 1), Is.EqualTo(4),
            "a scheduled squad of four offers four slots even with one survivor");
        Assert.That(SurvivorRoleComposition.GetSlotCount(4, 4), Is.EqualTo(4));
        Assert.That(SurvivorRoleComposition.GetSlotCount(0, 2), Is.EqualTo(2),
            "without a scheduled count the slots still cover the held survivors");
        Assert.That(SurvivorRoleComposition.GetSlotCount(-1, 3), Is.EqualTo(3));
        Assert.That(SurvivorRoleComposition.GetSlotCount(2, 5), Is.EqualTo(5),
            "the slot count never drops below the held survivors");
    }

    [Test]
    public void AScheduledSquadShowsEveryPositionEvenWhenFewerPlayersRolled()
    {
        var template = new List<SurvivorTemplateRole>
        {
            Role("Leader", 1, SurvivorRoleCategory.Leader),
            Role("Surgeon", 1, SurvivorRoleCategory.Medic),
            Role("Fighter", 2, SurvivorRoleCategory.Rifleman),
        };

        var slots = SurvivorRoleComposition.Build(template, SurvivorRoleComposition.GetSlotCount(4, 1));

        Assert.That(slots.Select(slot => slot.Proto),
            Is.EqualTo(new[] { "Leader", "Surgeon", "Fighter", "Fighter" }),
            "the whole scheduled squad is offered, not just one slot per held player");
    }

    [Test]
    public void PartySpawnRolesDefaultToLeaderAndRifleman()
    {
        var spawn = IoCManager.Resolve<IPrototypeManager>().Index<PartySpawnPrototype>("CMUTestSurvivorSpawn");
        SurvivorRoleCategory? Category(string proto)
            => proto == "TestSurvivorSurgeon" ? SurvivorRoleCategory.Medic : null;

        var template = SurvivorRoleComposition.BuildTemplate(spawn, Category);
        var slots = SurvivorRoleComposition.Build(template, groupSize: 4);

        Assert.That(slots.Select(slot => slot.Proto),
            Is.EqualTo(new[] { "TestSurvivorLeader", "TestSurvivorSurgeon", "TestSurvivorFighter", "TestSurvivorFighter" }),
            "unknown leader bodies are generic leaders, unknown grunt bodies are riflemen");
    }

    // --- Role claim raffle ----------------------------------------------------------------------------

    [Test]
    public void TheRaffleClockStartsWithTheFirstClaimAndIsNotExtended()
    {
        var now = TimeSpan.FromSeconds(100);
        var duration = TimeSpan.FromSeconds(10);

        var first = SurvivorRoleRaffle.NextClaimDeadline(null, now, duration);
        var second = SurvivorRoleRaffle.NextClaimDeadline(first, now + TimeSpan.FromSeconds(4), duration);

        Assert.That(first, Is.EqualTo(now + duration), "the first claim starts a ten second window");
        Assert.That(second, Is.EqualTo(first), "a later claim must not extend the raffle");
    }

    [Test]
    public void TheRaffleWinnerIsDrawnFromTheClaimants()
    {
        var first = new NetUserId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var second = new NetUserId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var third = new NetUserId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        var claimants = new[] { first, second, third };

        Assert.That(SurvivorRoleRaffle.PickWinner(Array.Empty<NetUserId>(), _ => 0), Is.Null,
            "a slot with no claimants has no winner");
        Assert.That(SurvivorRoleRaffle.PickWinner(new[] { first }, _ => 0), Is.EqualTo(first));
        Assert.That(SurvivorRoleRaffle.PickWinner(claimants, _ => 0), Is.EqualTo(first));
        Assert.That(SurvivorRoleRaffle.PickWinner(claimants, _ => 2), Is.EqualTo(third));
        Assert.That(SurvivorRoleRaffle.PickWinner(claimants, _ => 99), Is.EqualTo(third),
            "an out-of-range roll is clamped instead of throwing");
    }

    // --- Temporary survivor testing pool -------------------------------------------------------------

    [Test]
    public void TheTestPoolListsTheConfiguredFactions()
    {
        var ids = SurvivorTestFactionPool.ParseIds(
            " CMUProdigySurvivors , CMUWYPMCSurvivors,CMUCLFCommandoSurvivors ,CLFSurvsSmall,CMUUSArmySurvivors,CMUFORECONSurvivors ");

        Assert.That(ids, Is.EqualTo(new[]
        {
            "CMUProdigySurvivors",
            "CMUWYPMCSurvivors",
            "CMUCLFCommandoSurvivors",
            "CLFSurvsSmall",
            "CMUUSArmySurvivors",
            "CMUFORECONSurvivors",
        }));
        Assert.That(SurvivorTestFactionPool.ParseIds(""), Is.Empty, "an empty value disables the override");
        Assert.That(SurvivorTestFactionPool.ParseIds(null), Is.Empty);
        Assert.That(SurvivorTestFactionPool.ParseIds("A,a,A"), Is.EqualTo(new[] { "A" }),
            "the same faction is only listed once");
    }

    private static readonly string[] SixFactionPool =
    {
        "CMUProdigySurvivors",
        "CMUWYPMCSurvivors",
        "CMUCLFCommandoSurvivors",
        "CLFSurvsSmall",
        "CMUUSArmySurvivors",
        "CMUFORECONSurvivors",
    };

    [Test]
    public void TheLargerPoolStillDrawsTwoDistinctOptions()
    {
        var queue = new Queue<int>([4, 2]);
        var options = SurvivorFactionDraw.PickDistinct(SixFactionPool, _ => 1, max => queue.Dequeue() % max);

        Assert.That(options, Has.Count.EqualTo(2));
        Assert.That(options, Is.Unique);
        Assert.That(SixFactionPool, Is.SupersetOf(options));
        Assert.That(options, Is.EqualTo(new[] { "CMUUSArmySurvivors", "CMUCLFCommandoSurvivors" }));
    }

    private static readonly string[] ThreeFactionPool =
    {
        "CMUProdigySurvivors",
        "CMUWYPMCSurvivors",
        "CLFSurvsSmall",
    };

    [Test]
    public void ThreeEligibleFactionsProduceDifferentTwoOptionPairs()
    {
        static List<string> Draw(int[] rolls)
        {
            var queue = new Queue<int>(rolls);
            return SurvivorFactionDraw.PickDistinct(ThreeFactionPool, _ => 1, max => queue.Dequeue() % max);
        }

        var first = Draw([0, 0]);
        var second = Draw([1, 1]);
        var third = Draw([2, 0]);

        foreach (var pair in new[] { first, second, third })
        {
            Assert.That(pair, Has.Count.EqualTo(2), "only two factions are offered");
            Assert.That(pair, Is.Unique, "the same faction is never offered twice");
            Assert.That(ThreeFactionPool, Is.SupersetOf(pair), "options come from the eligible pool");
        }

        Assert.That(first, Is.EqualTo(new[] { "CMUProdigySurvivors", "CMUWYPMCSurvivors" }));
        Assert.That(second, Is.EqualTo(new[] { "CMUWYPMCSurvivors", "CLFSurvsSmall" }));
        Assert.That(third, Is.EqualTo(new[] { "CLFSurvsSmall", "CMUProdigySurvivors" }));

        Assert.That(first.IndexOf("CMUWYPMCSurvivors"), Is.EqualTo(1),
            "the W-Y PMC test faction can land in the second voting position");
        Assert.That(second.IndexOf("CMUWYPMCSurvivors"), Is.EqualTo(0),
            "the W-Y PMC test faction can land in the first voting position");
    }

    [Test]
    public void EitherDrawnOptionCanTakeTheWholeGroup()
    {
        var options = new[] { "CMUWYPMCSurvivors", "CLFSurvsSmall" };

        foreach (var winner in options)
        {
            var votes = options.ToDictionary(option => option, option => option == winner ? 3 : 1);
            var leaders = SurvivorVoteTally.GetVoteLeaders(options, votes);

            Assert.That(leaders, Is.EqualTo(new[] { winner }), "the group is assigned to exactly one faction");
            Assert.That(SurvivorVoteTally.PickFromLeaders(leaders, _ => 1, roll: 0), Is.EqualTo(winner));
        }
    }

    // --- Faction loadouts -----------------------------------------------------------------------------

    [Test]
    public void LoadoutSelectionAcceptsOnlyConfiguredOptions()
    {
        var options = new SurvivorLoadoutOptions(
            new[] { "ArmorA", "ArmorB" },
            new[] { "HelmetA" },
            new[] { "RifleA", "RifleB" });

        var chosen = new SurvivorLoadoutSelection("ArmorB", "HelmetA", "RifleB");
        Assert.That(SurvivorLoadoutRules.Resolve(options, chosen), Is.EqualTo(chosen),
            "configured options are accepted as-is");

        var foreign = SurvivorLoadoutRules.Resolve(options, new SurvivorLoadoutSelection("OtherFactionArmor", null, "OtherFactionRifle"));
        Assert.That(foreign, Is.EqualTo(new SurvivorLoadoutSelection("ArmorA", "HelmetA", "RifleA")),
            "missing or cross-faction options fall back to the faction's defaults");

        var nothing = SurvivorLoadoutRules.Resolve(
            new SurvivorLoadoutOptions(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
            default);
        Assert.That(nothing, Is.EqualTo(new SurvivorLoadoutSelection(null, null, null)),
            "a faction with no options resolves to no selection");
    }


    // --- Timers and role-restricted firearms ----------------------------------------------------------

    [Test]
    public void PreparationLastsThirtySecondsAndTheRaffleFive()
    {
        Assert.That(SurvivorSetupTimings.Preparation, Is.EqualTo(TimeSpan.FromSeconds(30)),
            "the survivor preparation phase is 30 seconds");
        Assert.That(SurvivorSetupTimings.Raffle, Is.EqualTo(TimeSpan.FromSeconds(5)),
            "role raffles resolve after exactly 5 seconds");
    }

    [Test]
    public void RestrictedFirearmsAreOnlyAvailableToTheirRoles()
    {
        var restrictions = new Dictionary<string, List<SurvivorRoleCategory>>
        {
            ["SmartGun"] = new() { SurvivorRoleCategory.WeaponSpecialist },
        };
        var firearms = new[] { "Rifle", "SmartGun" };

        Assert.That(SurvivorLoadoutRules.FilterFirearms(firearms, restrictions, SurvivorRoleCategory.Medic),
            Is.EqualTo(new[] { "Rifle" }), "a corpsman must not be offered the smartgun");
        Assert.That(SurvivorLoadoutRules.FilterFirearms(firearms, restrictions, SurvivorRoleCategory.Technician),
            Is.EqualTo(new[] { "Rifle" }), "a technician must not be offered the smartgun");
        Assert.That(SurvivorLoadoutRules.FilterFirearms(firearms, restrictions, SurvivorRoleCategory.WeaponSpecialist),
            Is.EqualTo(new[] { "Rifle", "SmartGun" }), "a weapon specialist keeps the smartgun");
        Assert.That(SurvivorLoadoutRules.FilterFirearms(firearms, restrictions, null),
            Is.EqualTo(new[] { "Rifle" }), "an unclassified role only gets unrestricted firearms");

        Assert.That(SurvivorLoadoutRules.CanUseFirearm("SmartGun", restrictions, SurvivorRoleCategory.Medic),
            Is.False);
        Assert.That(SurvivorLoadoutRules.CanUseFirearm("Rifle", restrictions, SurvivorRoleCategory.Medic),
            Is.True, "unrestricted firearms stay available");
    }

    [Test]
    public void AnInvalidWeaponSubmissionFallsBackToAnAllowedWeapon()
    {
        // The role-filtered options exclude the smartgun, so even a hand-crafted request for it resolves to
        // the first weapon the role may actually use.
        var allowed = new SurvivorLoadoutOptions(
            new[] { "Armor" }, new[] { "Helmet" }, new[] { "Rifle" });

        var resolved = SurvivorLoadoutRules.Resolve(allowed,
            new SurvivorLoadoutSelection("Armor", "Helmet", "SmartGun"));

        Assert.That(resolved, Is.EqualTo(new SurvivorLoadoutSelection("Armor", "Helmet", "Rifle")));
    }

    private static SurvivorTemplateRole Role(string proto, int count, SurvivorRoleCategory category)
        => new(proto, count, category);

    private static ThirdPartySystem.SurvivorAssignment Assignment(NetUserId user, string body, bool leader)
        => new(user, body, leader, HumanoidCharacterProfile.DefaultWithSpecies(), default, null);

    // --- Teammate roster ------------------------------------------------------------------------------

    [Test]
    public void RosterCarriesCharacterRoleLoadoutAndStatus()
    {
        var self = new NetUserId(Guid.NewGuid());
        var mate = new NetUserId(Guid.NewGuid());

        var members = SurvivorRosterPreview.Build(new[]
        {
            new SurvivorRosterPreview.Candidate(self, "ckeySelf", "Vasquez", "Rifleman",
                "armor-id", "helmet-id", "firearm-id", "CLF", "ready", "QVBQ", "selected"),
            new SurvivorRosterPreview.Candidate(mate, "ckeyMate", null, null, null, null, null, null,
                "waiting", null),
        }, self);

        Assert.That(members, Has.Count.EqualTo(2), "every held teammate is listed");
        Assert.That(members[0], Does.StartWith("ckeyMate"), "teammates are ordered by account name");

        var parts = members[1].Split('|');
        Assert.That(parts, Has.Length.EqualTo(10), "ckey|character|role|armor|helmet|firearm|status|appearance|loadoutState|");
        Assert.That(parts[0], Is.EqualTo("ckeySelf"));
        Assert.That(parts[1], Is.EqualTo("Vasquez"), "the character name drives the card headline");
        Assert.That(parts[2], Is.EqualTo("Rifleman"));
        Assert.That(parts[5], Is.EqualTo("firearm-id"), "the selected primary weapon is carried");
        Assert.That(parts[6], Is.EqualTo("ready"), "the selection status is carried");
        Assert.That(parts[7], Is.EqualTo("QVBQ"), "the appearance blob is carried for the sprite preview");
        Assert.That(parts[8], Is.EqualTo("selected"), "a confirmed loadout is marked as selected");

        var waiting = members[0].Split('|');
        Assert.That(waiting[6], Is.EqualTo("waiting"));
        Assert.That(waiting[8], Is.EqualTo("none"), "nothing selected must not read as a chosen loadout");
        Assert.That(waiting[7], Is.Empty, "no appearance until the player picks a character");
    }

    [Test]
    public void RosterLinesOmitWhateverTheTeammateHasNotPickedYet()
    {
        var mate = new NetUserId(Guid.NewGuid());
        var line = SurvivorRosterPreview.Format(
            new SurvivorRosterPreview.Candidate(mate, "ckeyMate", null, null, null, null, null, null,
                "waiting", null), isSelf: false);

        Assert.That(line, Is.EqualTo("ckeyMate"), "a teammate who has picked nothing still gets a readable line");
        Assert.That(line, Does.Not.Contain(" - "), "no dangling separators");
    }

    [Test]
    public void SomeoneWhoAlreadyOwnsARoleCannotWinASecondOne()
    {
        var owner = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());
        var claimants = new[] { owner, other };
        var owned = new Dictionary<NetUserId, int> { [owner] = 0 };

        var eligible = SurvivorRoleOwnership.Eligible(claimants, slot: 1, owned);

        Assert.That(eligible, Is.EqualTo(new[] { other }),
            "a survivor who already won a role is not in the raffle for another one");

        Assert.That(SurvivorRoleOwnership.Eligible(claimants, slot: 0, owned),
            Is.EquivalentTo(claimants), "re-claiming the role they own is still allowed");

        Assert.That(SurvivorRoleOwnership.Eligible(new[] { owner }, slot: 1, owned), Is.Empty,
            "a sole claimant who owns another role leaves the slot for someone else");
    }

    // --- Faction-specific survivor subtypes ------------------------------------------------------------

    [Test]
    public void RoleNamesPreferTheFactionSubtypeOverTheInheritedBodyName()
    {
        var name = SurvivorRoleDisplay.Resolve("CMUSurvivorClfSpecOpsOperative", "CLF Spec Ops Operative", "Insurgent Reinforcements");
        Assert.That(name, Is.EqualTo("CLF Spec Ops Operative"), "the faction subtype name wins over the body's own name");
    }

    [Test]
    public void RoleNamesFallBackToTheBodyNameAndNeverBorrowAnotherFaction()
    {
        Assert.That(SurvivorRoleDisplay.Resolve("SomeRole", null, "Beefed Up Brawler"), Is.EqualTo("Beefed Up Brawler"));
        Assert.That(SurvivorRoleDisplay.Resolve("SomeRole", "   ", "Beefed Up Brawler"), Is.EqualTo("Beefed Up Brawler"));
        Assert.That(SurvivorRoleDisplay.Resolve("SomeRole", null, null), Is.EqualTo("SomeRole"));
    }

    [Test]
    public void TheSubtypeRegistryReportsMissingNamesMissingSubtypesAndWrongFactions()
    {
        var subtypes = new Dictionary<string, (string? Name, string? Party)>
        {
            ["GoodRole"] = ("CLF Spec Ops Operative", "ClfParty"),
            ["NamelessRole"] = (null, "ClfParty"),
            ["ForeignRole"] = ("BEAR Rifleman", "BearParty"),
        };

        var findings = SurvivorRoleRegistry.Validate(
            new List<(string Party, IEnumerable<string> Roles)>
            {
                ("ClfParty", new[] { "GoodRole", "NamelessRole", "ForeignRole", "UnknownRole" }),
            },
            subtypes);

        Assert.That(findings.Select(f => (f.Issue, f.Role)), Is.EquivalentTo(new[]
        {
            (SurvivorRoleRegistry.Issue.MissingName, "NamelessRole"),
            (SurvivorRoleRegistry.Issue.WrongParty, "ForeignRole"),
            (SurvivorRoleRegistry.Issue.MissingSubtype, "UnknownRole"),
        }));
    }

    [Test]
    public void AMatchingSubtypeRegistryHasNoFindings()
    {
        var subtypes = new Dictionary<string, (string? Name, string? Party)>
        {
            ["RoleA"] = ("CLF Commando", "ClfParty"),
            ["RoleB"] = ("CLF Medic", "ClfParty"),
        };

        var findings = SurvivorRoleRegistry.Validate(
            new List<(string Party, IEnumerable<string> Roles)>
            {
                ("ClfParty", new[] { "RoleA", "RoleB" }),
            },
            subtypes);

        Assert.That(findings, Is.Empty);
    }

    // --- Winning a raffle locks the role in -----------------------------------------------------------

    [Test]
    public void APlayerStillInARaffleIsNotLockedAndMaySwitchRoles()
    {
        var player = new NetUserId(Guid.NewGuid());
        var other = new NetUserId(Guid.NewGuid());

        // Joining a raffle leaves the slot ownerless: that is not a win, so no lock.
        Assert.That(SurvivorRoleClaims.IsLocked(player, currentSlot: 2, ownerOfCurrentSlot: null), Is.False);
        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 3, currentSlot: 2,
            ownerOfCurrentSlot: null, ownerOfRequestedSlot: null), Is.True,
            "a pending raffle does not lock the player out of switching");
        Assert.That(SurvivorRoleClaims.IsLocked(player, currentSlot: 2, ownerOfCurrentSlot: other), Is.False,
            "someone else's win is not this player's lock");
    }

    [Test]
    public void WinningARaffleLocksThePlayerIntoTheWonRole()
    {
        var player = new NetUserId(Guid.NewGuid());

        Assert.That(SurvivorRoleClaims.IsLocked(player, currentSlot: 1, ownerOfCurrentSlot: player), Is.True);
        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 4, currentSlot: 1,
            ownerOfCurrentSlot: player, ownerOfRequestedSlot: null), Is.False,
            "a winner cannot claim a different role");
        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 1, currentSlot: 1,
            ownerOfCurrentSlot: player, ownerOfRequestedSlot: player), Is.False,
            "a repeated or stale request for the won role is rejected rather than re-run");
    }

    [Test]
    public void LosingARaffleLeavesThePlayerFreeToClaimAgain()
    {
        var player = new NetUserId(Guid.NewGuid());

        // After a loss the player holds no slot at all, which is the state the server leaves them in.
        Assert.That(SurvivorRoleClaims.IsLocked(player, currentSlot: null, ownerOfCurrentSlot: null), Is.False);
        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 2, currentSlot: null,
            ownerOfCurrentSlot: null, ownerOfRequestedSlot: null), Is.True);
    }

    [Test]
    public void ARoleWonBySomeoneElseCannotBeClaimed()
    {
        var player = new NetUserId(Guid.NewGuid());
        var winner = new NetUserId(Guid.NewGuid());

        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 0, currentSlot: null,
            ownerOfCurrentSlot: null, ownerOfRequestedSlot: winner), Is.False,
            "the server refuses a taken slot even if a client submits it directly");
    }

    [Test]
    public void ThePublishedLockStateMatchesTheServerRule()
    {
        var player = new NetUserId(Guid.NewGuid());

        // The EUI publishes exactly this expression, so the UI cannot disagree with the claim rule.
        Assert.That(SurvivorRoleClaims.IsLocked(player, 0, player), Is.True);
        Assert.That(SurvivorRoleClaims.IsLocked(player, 0, null), Is.False);
        Assert.That(SurvivorRoleClaims.IsLocked(player, null, player), Is.False);
    }

    // --- Per-map spawn briefing -----------------------------------------------------------------------

    private static SurvivorBriefingText Briefing(string? intro, string? story) => new(intro, story);

    private static SurvivorBriefingText Defaults(string? intro, string? story) => new(intro, story);

    [Test]
    public void ABriefingFallsBackToThePartyDefaultsWhenTheScenarioHasNoEntry()
    {
        var scenario = new Dictionary<string, SurvivorBriefingText>();

        var onKnownMap = SurvivorBriefingRules.Resolve("crash_landing", Defaults("default-intro", "default-story"), scenario);
        Assert.That(onKnownMap.Intro, Is.EqualTo("default-intro"));
        Assert.That(onKnownMap.Story, Is.EqualTo("default-story"));

        var noMap = SurvivorBriefingRules.Resolve(null, Defaults("default-intro", "default-story"), scenario);
        Assert.That(noMap.Intro, Is.EqualTo("default-intro"), "a round with no planet still briefs the player");
        Assert.That(noMap.Story, Is.EqualTo("default-story"));
    }

    [Test]
    public void AScenarioEntryOverridesThePartyBriefing()
    {
        var scenario = new Dictionary<string, SurvivorBriefingText>
        {
            ["crash_landing"] = Briefing("crash-intro", "crash-story"),
        };

        var briefing = SurvivorBriefingRules.Resolve("crash_landing", Defaults("default-intro", "default-story"), scenario);
        Assert.That(briefing.Intro, Is.EqualTo("crash-intro"));
        Assert.That(briefing.Story, Is.EqualTo("crash-story"));
    }

    [Test]
    public void AScenarioMayOverrideOnlyPartOfTheBriefing()
    {
        // The uprising scenario only changes the story (uprising rather than crash landing).
        var scenario = new Dictionary<string, SurvivorBriefingText>
        {
            ["colonial_uprising"] = Briefing(null, "uprising-story"),
        };

        var briefing = SurvivorBriefingRules.Resolve("colonial_uprising", Defaults("default-intro", "default-story"), scenario);
        Assert.That(briefing.Intro, Is.EqualTo("default-intro"), "the unset half keeps the party default");
        Assert.That(briefing.Story, Is.EqualTo("uprising-story"));
    }

    [Test]
    public void AnotherScenariosEntryIsNotUsed()
    {
        var scenario = new Dictionary<string, SurvivorBriefingText>
        {
            ["crash_landing"] = Briefing("crash-intro", "crash-story"),
        };

        var briefing = SurvivorBriefingRules.Resolve("shivas_raid", Defaults("default-intro", null), scenario);
        Assert.That(briefing.Intro, Is.EqualTo("default-intro"), "a scenario with no entry never borrows another scenario's text");
        Assert.That(briefing.Story, Is.Null);
    }

    [Test]
    public void TheBriefingNamesTheVotedColony()
    {
        // With no locale loaded the resolver returns the key verbatim, so this asserts the colony argument is
        // accepted and the text still resolves; the real substitution is covered by the integration test
        // against the loaded locale.
        var briefing = SurvivorBriefingRules.Resolve(null, Defaults("briefing-intro", "briefing-story"),
            scenario: null, colonyName: "Barker's Lament");

        Assert.That(briefing.Intro, Is.EqualTo("briefing-intro"));
        Assert.That(briefing.Story, Is.EqualTo("briefing-story"));
    }

    [Test]
    public void AMissingColonyNameStillResolvesTheBriefing()
    {
        var briefing = SurvivorBriefingRules.Resolve(null, Defaults("briefing-intro", null), scenario: null,
            colonyName: "   ");
        Assert.That(briefing.Intro, Is.EqualTo("briefing-intro"), "an unset colony name falls back to a generic one");
    }

    [Test]
    public void APartyWithoutBriefingTextShowsNothing()
    {
        var briefing = SurvivorBriefingRules.Resolve("crash_landing", Defaults(null, null), scenario: null);
        Assert.That(briefing.Intro, Is.Null);
        Assert.That(briefing.Story, Is.Null, "no text is sent rather than someone else's");
    }

    [Test]
    public void ABlankLocaleKeyIsTreatedAsMissing()
    {
        var briefing = SurvivorBriefingRules.Resolve(null, Defaults("   ", null), scenario: null);
        Assert.That(briefing.Intro, Is.Null);
    }

    // --- Synthetic-only survivor roles ---------------------------------------------------------------

    [Test]
    public void ASyntheticRoleNeedsASyntheticCharacter()
    {
        Assert.That(SurvivorRoleClaims.CharacterFitsRole(roleRequiresSynthetic: true, characterIsSynthetic: true),
            Is.True, "a synthetic character may take a synthetic role");
        Assert.That(SurvivorRoleClaims.CharacterFitsRole(roleRequiresSynthetic: true, characterIsSynthetic: false),
            Is.False, "a human character may not take a synthetic role");
    }

    [Test]
    public void OrdinaryRolesDoNotCareAboutTheCharacterType()
    {
        Assert.That(SurvivorRoleClaims.CharacterFitsRole(roleRequiresSynthetic: false, characterIsSynthetic: false),
            Is.True, "a human may take any ordinary role");
        Assert.That(SurvivorRoleClaims.CharacterFitsRole(roleRequiresSynthetic: false, characterIsSynthetic: true),
            Is.True, "a synthetic may take an ordinary role too");
    }
}

/// <summary>
///     Armour-piercing magazine swap: survivors carry a couple of AP magazines for weapons that have them.
/// </summary>
[TestFixture]
public sealed class SurvvorAmmoUpgradeTest
{
    [Test]
    public void RecognisesArmorPiercingMagazines()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("CMMagazineSMGM63AP"), Is.True);
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("CMUMagazineRifleM16AP"), Is.True);
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("CMMagazineSMGM63APExt"), Is.True);
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("AU14MagazineMar50LMGAP"), Is.True);
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("CMMagazineSMGM63"), Is.False);
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("CMShellShotgunBuckshot"), Is.False);
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("RMCWeaponRifleSSG45"), Is.False,
                "a weapon id is not a magazine");
        });
    }

    [Test]
    public void FindsTheExactApVariantWhenItExists()
    {
        var candidates = new[] { "CMMagazineSMGM63", "CMMagazineSMGM63AP", "CMMagazineSMGM63Ext" };
        Assert.That(SurvivorAmmoUpgrade.FindArmorPiercingVariant("CMMagazineSMGM63", candidates),
            Is.EqualTo("CMMagazineSMGM63AP"));
    }

    [Test]
    public void FindsAnApVariantUnderAnotherBrand()
    {
        // RMC ships the plain M16 magazine, CMU adds the AP one.
        var candidates = new[] { "RMCMagazineRifleM16", "CMUMagazineRifleM16AP", "CMMagazineSMGM63AP" };
        Assert.That(SurvivorAmmoUpgrade.FindArmorPiercingVariant("RMCMagazineRifleM16", candidates),
            Is.EqualTo("CMUMagazineRifleM16AP"));
    }

    [Test]
    public void ReturnsNothingWhenTheWeaponHasNoApMagazine()
    {
        var candidates = new[] { "CMShellShotgunBuckshot", "CMMagazineSMGM63AP" };
        Assert.That(SurvivorAmmoUpgrade.FindArmorPiercingVariant("CMShellShotgunBuckshot", candidates), Is.Null,
            "shotgun shells have no AP magazine, so nothing is swapped");
    }

    [Test]
    public void DoesNotBorrowAnUnrelatedCalibre()
    {
        var candidates = new[] { "CMMagazineSMGM63AP" };
        Assert.That(SurvivorAmmoUpgrade.FindArmorPiercingVariant("AU14MagazineMar50LMG", candidates), Is.Null,
            "a 9mm SMG magazine is not the MAR-50's AP load");
    }

    [Test]
    public void UpgradesBetweenOneAndThreeMagazines()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(0, _ => 0), Is.EqualTo(0));
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(5, _ => 0), Is.EqualTo(1));
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(5, _ => 1), Is.EqualTo(2));
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(5, _ => 2), Is.EqualTo(3));
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(5, _ => 99), Is.EqualTo(3),
                "the roll is clamped to the configured range");
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(2, _ => 2), Is.EqualTo(2),
                "never more magazines than the survivor carries");
        });
    }
}

/// <summary>
///     A survivor who picks a shotgun must be re-issued shell carrying gear, and a survivor who picks anything
///     magazine-fed must keep their magazine rigs. Medic bags, toolbelts and fuel pouches are never touched.
/// </summary>
[TestFixture]
public sealed class SurvivorAmmoRigTest
{
    [Test]
    public void ShotgunsTakeLooseShellsAndMagazineWeaponsDoNot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorAmmoRig.LoadsLooseAmmo("CMShellShotgunBuckshot"), Is.True,
                "buckshot comes loose, so the rig has to be re-issued");
            Assert.That(SurvivorAmmoRig.LoadsLooseAmmo("CMUMagazineSMGAM36"), Is.False,
                "a magazine weapon keeps its magazine rig");
            Assert.That(SurvivorAmmoRig.LoadsLooseAmmo("RMCMagazineRifleM16"), Is.False);
            Assert.That(SurvivorAmmoRig.LoadsLooseAmmo(null), Is.False, "no firearm picked yet");
            Assert.That(SurvivorAmmoRig.LoadsLooseAmmo(string.Empty), Is.False);
        });
    }

    [Test]
    public void OnlyMagazineCarriersAreReplaced()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorAmmoRig.IsMagazineRig("CMBeltMarine"), Is.True);
            Assert.That(SurvivorAmmoRig.IsMagazineRig("CMBeltMarineM16"), Is.True);
            Assert.That(SurvivorAmmoRig.IsMagazineRig("AU14PouchMagazineLargeFilledM16"), Is.True);
            Assert.That(SurvivorAmmoRig.IsMagazineRig("CMUPouchMagazineLargeFilledM16"), Is.True);

            Assert.That(SurvivorAmmoRig.IsMagazineRig("RMCPouchMedkitFillAdvanced"), Is.False, "a medkit is not a magazine rig");
            Assert.That(SurvivorAmmoRig.IsMagazineRig("RMCBeltMedicBagERTFilled"), Is.False, "so is the medic bag");
            Assert.That(SurvivorAmmoRig.IsMagazineRig("RMCPouchFuelTankFilledDutch"), Is.False, "so is the fuel-tank strap");
            Assert.That(SurvivorAmmoRig.IsMagazineRig(null), Is.False);
        });
    }

    [Test]
    public void TheBeltTakesAShellBeltAndPocketsTakeShellPouches()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorAmmoRig.RigKindForSlot("belt"), Is.EqualTo("belt"));
            Assert.That(SurvivorAmmoRig.RigKindForSlot("pocket1"), Is.EqualTo("pouch"));
            Assert.That(SurvivorAmmoRig.RigKindForSlot("pocket2"), Is.EqualTo("pouch"));
            Assert.That(SurvivorAmmoRig.RigKindForSlot("suitstorage"), Is.Null, "the weapon slot is not a rig");
            Assert.That(SurvivorAmmoRig.RigKindForSlot("back"), Is.Null);
        });
    }
}

/// <summary>
///     Which markers may place a survivor. Survivors insert on the colony's third-party insert markers and its
///     survivor spawn points, on the colony map itself - never on a generic threat marker (that ground belongs to
///     the xenomorph threat) and never off-planet.
/// </summary>
[TestFixture]
public sealed class SurvivorSpawnMarkersTest
{
    [Test]
    public void ThirdPartyMarkersAndSurvivorSpawnPointsAreColonyGround()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorSpawnMarkers.IsColonyInsertGround(insertMarker: false, thirdPartyThreatMarker: true, survivorSpawnPoint: false),
                Is.True, "a third-party insert marker is the ground every ground party uses");
            Assert.That(SurvivorSpawnMarkers.IsColonyInsertGround(false, false, true), Is.True,
                "a survivor spawn point is colony ground");
            Assert.That(SurvivorSpawnMarkers.IsColonyInsertGround(true, false, false), Is.True,
                "an insert marker is colony ground");
        });
    }

    [Test]
    public void GenericThreatMarkersAreNeverSurvivorGround()
    {
        Assert.That(SurvivorSpawnMarkers.IsColonyInsertGround(insertMarker: false, thirdPartyThreatMarker: false, survivorSpawnPoint: false),
            Is.False, "a plain threat marker is the xenomorph threat's ground");
    }

    [Test]
    public void MarkersOffTheColonyMapAreRejected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorSpawnMarkers.IsAllowedMap(planetKnown: false, markerOnPlanet: false), Is.True,
                "before a planet is picked every map counts, so the pre-round check still sees the markers");
            Assert.That(SurvivorSpawnMarkers.IsAllowedMap(planetKnown: false, markerOnPlanet: true), Is.True);
            Assert.That(SurvivorSpawnMarkers.IsAllowedMap(planetKnown: true, markerOnPlanet: true), Is.True,
                "a marker on the colony map is valid");
            Assert.That(SurvivorSpawnMarkers.IsAllowedMap(planetKnown: true, markerOnPlanet: false), Is.False,
                "the ship and its dropships are not the colony");
        });
    }

    [Test]
    public void AGroupWithNoColonyGroundGoesByDropshipRatherThanDisappearing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorSpawnMarkers.CanInsertOnColony(0), Is.False,
                "no colony ground means no colony insert");
            Assert.That(SurvivorSpawnMarkers.CanInsertOnColony(1), Is.True);
            Assert.That(SurvivorSpawnMarkers.CanInsertOnColony(7), Is.True, "any number of markers is enough");

            Assert.That(SurvivorSpawnMarkers.FallsBackToDropship(SurvivorSpawnMarkers.CanInsertOnColony(0)), Is.True,
                "the group must always spawn, so a map without insert ground sends it down by dropship");
            Assert.That(SurvivorSpawnMarkers.FallsBackToDropship(SurvivorSpawnMarkers.CanInsertOnColony(3)), Is.False,
                "with colony ground the group inserts on the colony and never uses a dropship");
        });
    }

    [Test]
    public void AMarkerClaimedByOneGroupIsNotOfferedToTheOther()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorSpawnMarkers.MarkerIsAvailable(reservedByAnotherGroup: false), Is.True,
                "an unclaimed marker is offered to the next survivor group");
            Assert.That(SurvivorSpawnMarkers.MarkerIsAvailable(reservedByAnotherGroup: true), Is.False,
                "a faction group and a civilian group must never spawn on the same marker");
        });
    }
}

/// <summary>
///     A survivor who never picked a role still gets one: a random pick out of the slots nobody claimed, so a
///     lobby AFK or an indecisive player does not go bodiless while free positions remain.
/// </summary>
[TestFixture]
public sealed class SurvivorAutoSlotTest
{
    [Test]
    public void AFreedSlotIsPickedAtRandomFromTheOnesLeft()
    {
        var free = new List<int> { 2, 5, 9 };

        Assert.Multiple(() =>
        {
            Assert.That(SurvivorSlotAssignment.RandomFreeSlot(free, _ => 0), Is.EqualTo(2),
                "the first roll takes the first free slot");
            Assert.That(SurvivorSlotAssignment.RandomFreeSlot(free, _ => 1), Is.EqualTo(5));
            Assert.That(SurvivorSlotAssignment.RandomFreeSlot(free, _ => 2), Is.EqualTo(9));

            // Rolls come from the engine's RNG, so they must not be assumed to be in range.
            Assert.That(SurvivorSlotAssignment.RandomFreeSlot(free, _ => 7), Is.EqualTo(5),
                "an out-of-range roll wraps instead of crashing");
            Assert.That(SurvivorSlotAssignment.RandomFreeSlot(free, _ => int.MinValue), Is.Not.Null,
                "a negative roll is handled too");
        });
    }

    [Test]
    public void ASingleFreeSlotIsHandedOutWhateverTheRoll()
    {
        var free = new List<int> { 4 };

        Assert.That(SurvivorSlotAssignment.RandomFreeSlot(free, _ => 3), Is.EqualTo(4));
    }

    [Test]
    public void NoFreeSlotMeansNoRole()
    {
        Assert.That(SurvivorSlotAssignment.RandomFreeSlot(new List<int>(), _ => 0), Is.Null,
            "when every slot is claimed the survivor has nothing to be given");
    }
}

/// <summary>
///     Survivors belong to the distress signal gamemode: every other round preset must refuse to schedule them,
///     while a round whose preset is not decided yet is left to the remaining survivor checks.
/// </summary>
[TestFixture]
public sealed class SurvivorRoundGateTest
{
    [Test]
    public void OnlyTheDistressSignalGamemodeHostsSurvivors()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorRoundGate.IsSurvivorRound("DistressSignal"), Is.True);
            Assert.That(SurvivorRoundGate.IsSurvivorRound("distresssignal"), Is.True,
                "preset ids are compared case-insensitively");

            Assert.That(SurvivorRoundGate.IsSurvivorRound("Insurgency"), Is.False);
            Assert.That(SurvivorRoundGate.IsSurvivorRound("ColonyFall"), Is.False);
            Assert.That(SurvivorRoundGate.IsSurvivorRound("ForceOnForce"), Is.False);
            Assert.That(SurvivorRoundGate.IsSurvivorRound("Jailbreak"), Is.False);

            Assert.That(SurvivorRoundGate.IsSurvivorRound(null), Is.True,
                "a forced round whose gamemode vote never ran is not a different gamemode");
        });
    }
}

/// <summary>
///     The pieces a randomly generated loadout is built from: a survivor who never confirmed picks still gets a
///     valid armor, helmet and firearm out of their own role's options, never something else.
/// </summary>
[TestFixture]
public sealed class SurvivorLoadoutPickOptionTest
{
    [Test]
    public void ARandomPickAlwaysComesFromTheRoleOptions()
    {
        var options = new List<string> { "ArmorA", "ArmorB", "ArmorC" };

        Assert.Multiple(() =>
        {
            Assert.That(SurvivorLoadoutRules.PickOption(options, _ => 0), Is.EqualTo("ArmorA"));
            Assert.That(SurvivorLoadoutRules.PickOption(options, _ => 1), Is.EqualTo("ArmorB"));
            Assert.That(SurvivorLoadoutRules.PickOption(options, _ => 2), Is.EqualTo("ArmorC"));
            Assert.That(SurvivorLoadoutRules.PickOption(options, _ => 9), Is.EqualTo("ArmorC"),
                "an out-of-range roll is clamped to the last configured option");
            Assert.That(SurvivorLoadoutRules.PickOption(options, _ => -4), Is.EqualTo("ArmorA"),
                "a negative roll is clamped to the first configured option");
        });
    }

    [Test]
    public void NoOptionsMeansNoPick()
    {
        Assert.That(SurvivorLoadoutRules.PickOption(new List<string>(), _ => 0), Is.Null,
            "a category the role does not offer falls back to the faction default");
    }

    [Test]
    public void ALockedCategoryIsNeverRolled()
    {
        // A W-Y team leader is pinned to the leader armour and officer helmet: one option each.
        var leaderArmor = new List<string> { "CMArmorM4PMCLeader" };
        var openFirearms = new List<string> { "M54C2", "MK221" };

        Assert.Multiple(() =>
        {
            Assert.That(SurvivorLoadoutRules.IsRandomizable(leaderArmor), Is.False,
                "a role locked to one armour keeps it");
            Assert.That(SurvivorLoadoutRules.PickIfOpen(leaderArmor, _ => 0), Is.Null,
                "a locked category yields no roll, so the caller keeps the locked option");

            Assert.That(SurvivorLoadoutRules.IsRandomizable(openFirearms), Is.True);
            Assert.That(SurvivorLoadoutRules.PickIfOpen(openFirearms, _ => 1), Is.EqualTo("MK221"),
                "a category with a real choice is still rolled");
        });
    }

    [Test]
    public void AnEmptyCategoryHasNothingToRoll()
    {
        var none = new List<string>();

        Assert.Multiple(() =>
        {
            Assert.That(SurvivorLoadoutRules.IsRandomizable(none), Is.False);
            Assert.That(SurvivorLoadoutRules.PickIfOpen(none, _ => 0), Is.Null);
        });
    }
}

/// <summary>
///     The survivor briefing colour: gold unless the faction picks its own, with chat markup allowed inside
///     the text itself so individual lines can be highlighted.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SurvivorBriefingColorTest : ContentUnitTest
{
    private IPrototypeManager _prototypes = default!;

    [OneTimeSetUp]
    public void LoadPrototypes()
    {
        IoCManager.Resolve<ISerializationManager>().Initialize();
        _prototypes = IoCManager.Resolve<IPrototypeManager>();
        _prototypes.Initialize();
        _prototypes.LoadString(Prototypes);
        _prototypes.ResolveResults();
    }

    [Test]
    public void TheBriefingIsGoldUnlessTheFactionChoosesOtherwise()
    {
        var gold = _prototypes.Index<ThirdPartyPrototype>("CMUTestDefaultBriefing");
        Assert.That(gold.BriefingColor, Is.EqualTo(Color.FromHex("#d8b465")),
            "the survivor briefing keeps the muted gold it has always used");

        var red = _prototypes.Index<ThirdPartyPrototype>("CMUTestRedBriefing");
        Assert.That(red.BriefingColor, Is.EqualTo(Color.FromHex("#c83232")),
            "a faction can print its briefing in its own colour");
    }

    [Test]
    public void MarkupInsideTheBriefingSurvivesResolution()
    {
        var briefing = SurvivorBriefingRules.Resolve(null,
            new SurvivorBriefingText("[color=#c83232]intro[/color]", "[color=#79c07a]story[/color]"), null);

        Assert.Multiple(() =>
        {
            Assert.That(briefing.Intro, Is.EqualTo("[color=#c83232]intro[/color]"),
                "chat markup in the intro is passed through untouched");
            Assert.That(briefing.Story, Is.EqualTo("[color=#79c07a]story[/color]"),
                "chat markup in the story is passed through untouched");
        });
    }

    private const string Prototypes =
        """
            - type: partySpawn
              id: CMUTestBriefingSpawn
              leadersToSpawn:
                Survivor: 1
            - type: thirdParty
              id: CMUTestDefaultBriefing
              partyspawn: CMUTestBriefingSpawn
              intro: cmu-test-intro
            - type: thirdParty
              id: CMUTestRedBriefing
              partyspawn: CMUTestBriefingSpawn
              intro: cmu-test-intro
              briefingColor: "#c83232"
        """;
}
