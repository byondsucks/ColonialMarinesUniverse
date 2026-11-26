using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.CMU14.Round;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Presets;
using Content.Shared._RMC14.Rules;
using Content.Shared.CCVar;
using Content.Shared.CMU14.Threats;
using Content.Shared.GameTicking;
using Content.Tests;
using Moq;
using NUnit.Framework;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Serialization.Manager;

namespace Content.CMU.Tests.Round;

[TestFixture]
[NonParallelizable]
public sealed class CMUSurvivorSchedulingTest : ContentUnitTest
{
    private static readonly ProtoId<GamePresetPrototype> Preset = "DistressSignal";
    private IPrototypeManager _prototypes = default!;
    private AuRoundSystem _round = default!;
    private Dictionary<NetUserId, PlayerGameStatus> _statuses = default!;
    private Mock<IConfigurationManager> _cfg = default!;
    private AuRoundSelectionState _state = default!;

    [OneTimeSetUp]
    public void InitializePrototypes()
    {
        IoCManager.Resolve<ISerializationManager>().Initialize();
        _prototypes = IoCManager.Resolve<IPrototypeManager>();
        _prototypes.Initialize();
        _prototypes.LoadString("""
            - type: gamePreset
              id: DistressSignal
            - type: threat
              id: CMUTestThreat
              roundstartspawns: CMUTestThreatSpawn
              threatratio: 0.1
              thirdpartyratio: 0.5
            - type: partySpawn
              id: CMUTestThreatSpawn
              leadersToSpawn:
                Queen: 1
              gruntsToSpawn:
                Larva: 19
            - type: thirdParty
              id: CMUTestLargeSurvivors
              partyspawn: CMUTestLargeSurvivorSpawn
              roundstart: true
              announceAsSurvivors: true
            - type: partySpawn
              id: CMUTestLargeSurvivorSpawn
              leadersToSpawn:
                Survivor: 10
            - type: thirdParty
              id: CMUTestSmallSurvivors
              partyspawn: CMUTestSmallSurvivorSpawn
              roundstart: true
              announceAsSurvivors: true
            - type: partySpawn
              id: CMUTestSmallSurvivorSpawn
              leadersToSpawn:
                Survivor: 4
            """);
        _prototypes.ResolveResults();
    }

    [SetUp]
    public void SetUpScheduler()
    {
        var players = new Mock<IPlayerManager>();
        players.SetupGet(manager => manager.PlayerCount).Returns(100);
        ICommonSession session = Mock.Of<ICommonSession>();
        players.Setup(manager => manager.TryGetSessionById(It.IsAny<NetUserId>(), out session)).Returns(true);

        var ticker = new GameTicker();
        SetField(ticker, "_playerManager", players.Object);
        typeof(GameTicker).GetProperty(nameof(GameTicker.LobbyEnabled))!.SetValue(ticker, true);
        _statuses = (Dictionary<NetUserId, PlayerGameStatus>) typeof(GameTicker)
            .GetField("_playerGameStatuses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ticker)!;
        for (var i = 0; i < 100; i++)
            _statuses.Add(new NetUserId(Guid.NewGuid()), PlayerGameStatus.NotReadyToPlay);

        var systems = new Mock<IEntitySystemManager>();
        systems.Setup(manager => manager.GetEntitySystem<PlatoonSpawnRuleSystem>())
            .Returns(new PlatoonSpawnRuleSystem());
        var entities = new Mock<IEntityManager>();
        entities.SetupGet(manager => manager.EntitySysManager).Returns(systems.Object);
        var random = new Mock<IRobustRandom>();
        random.Setup(value => value.Next(It.IsAny<int>())).Returns(0);

        // The temporary survivor test pool is off unless a test turns it on.
        _cfg = new Mock<IConfigurationManager>();
        // The survivor cap is lobby-derived, so the scheduling tests pin the ratio they assume.
        _cfg.Setup(manager => manager.GetCVar(CCVars.SurvivorCountRatio)).Returns(0.15f);
        _cfg.Setup(manager => manager.GetCVar(CCVars.SurvivorTestFactions)).Returns(string.Empty);

        _round = new AuRoundSystem();
        SetField(_round, "_prototypeManager", _prototypes);
        SetField(_round, "_playerManager", players.Object);
        SetField(_round, "_gameTicker", ticker);
        SetField(_round, "_entityManager", entities.Object);
        SetField(_round, "_random", random.Object);
        SetField(_round, "_cfg", _cfg.Object);
        _round.SetPreset(_prototypes.Index(Preset));
        _state = (AuRoundSelectionState) typeof(AuRoundSystem)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_round)!;
        var planet = new RMCPlanetMapPrototypeComponent();
        SetField(planet, nameof(planet.AllowedThreats), new List<ProtoId<ThreatPrototype>> { "CMUTestThreat" });
        SetField(planet, nameof(planet.ThirdParties), new List<ProtoId<ThirdPartyPrototype>>
        {
            "CMUTestLargeSurvivors", "CMUTestSmallSurvivors",
        });
        _state.SetPlanet("CMUTestPlanet", planet);
    }

    [Test]
    public void SurvivorRosterUsesReadyPopulationAndCappedThreatBodies()
    {
        SetReadyPlayers(40);

        Assert.That(_round.TryLockDistressSignalThirdParties(out var survivors), Is.True);

        // 100 connected players and a 20-body template must not override the four planned threat bodies.
        Assert.That(survivors, Is.EqualTo(SurvivorCountLimits.FromLobby(40, 0.15f)),
            "the announced cap is the lobby's, not the drawn parties' body sum");
        Assert.That(_round.SelectedThirdParties.Select(party => party.ID),
            Is.EqualTo(new[] { "CMUTestSmallSurvivors" }));
    }

    [Test]
    public void RoundStartRevalidationReplacesOversizedRosterAndClearsItWithoutReadyPlayers()
    {
        SetReadyPlayers(100);
        Assert.That(_round.TryLockDistressSignalThirdParties(out var announced), Is.True);
        Assert.That(announced, Is.EqualTo(10));

        SetReadyPlayers(40);
        Assert.That(_round.TryLockDistressSignalThirdParties(out var survivors, revalidate: true), Is.True);
        Assert.That(survivors, Is.EqualTo(SurvivorCountLimits.FromLobby(40, 0.15f)));
        Assert.That(_round.SelectedThirdParties.Select(party => party.ID),
            Is.EqualTo(new[] { "CMUTestSmallSurvivors" }));

        SetReadyPlayers(0);
        Assert.That(_round.TryLockDistressSignalThirdParties(out survivors, revalidate: true), Is.True);
        Assert.That(survivors, Is.EqualTo(SurvivorCountLimits.MinSurvivors),
            "the cap comes from the lobby, so a round that runs survivors schedules at least the minimum");
        Assert.That(_round.SelectedThirdParties, Is.Empty);
    }

    [Test]
    public void RoundStartRevalidationFillsEmptyRosterButPreservesAGroupThatStillFits()
    {
        SetReadyPlayers(0);
        Assert.That(_round.TryLockDistressSignalThirdParties(out var survivors), Is.True);
        Assert.That(survivors, Is.EqualTo(SurvivorCountLimits.MinSurvivors),
            "the cap is lobby-derived, so a survivor round still schedules the minimum with nobody ready");

        SetReadyPlayers(40);
        Assert.That(_round.TryLockDistressSignalThirdParties(out survivors, revalidate: true), Is.True);
        Assert.That(survivors, Is.EqualTo(SurvivorCountLimits.FromLobby(40, 0.15f)));
        var announcedParty = _round.SelectedThirdParties.Single();

        SetReadyPlayers(100);
        Assert.That(_round.TryLockDistressSignalThirdParties(out survivors, revalidate: true), Is.True);
        Assert.That(survivors, Is.EqualTo(SurvivorCountLimits.FromLobby(40, 0.15f)),
            "a roster that still fits keeps the cap it was announced with, so the number cannot shift under the table");
        Assert.That(_round.SelectedThirdParties.Single(), Is.SameAs(announcedParty));
    }

    [Test]
    public void SurvivorTestPoolSeedsRoundStartSchedulingWithoutReadyPlayers()
    {
        SetReadyPlayers(0);
        _cfg.Setup(manager => manager.GetCVar(CCVars.SurvivorTestFactions))
            .Returns("CMUTestLargeSurvivors,CMUTestSmallSurvivors");

        Assert.That(_round.TryLockDistressSignalThirdParties(out var survivors), Is.True);
        Assert.That(survivors, Is.EqualTo(SurvivorCountLimits.FromLobby(0, 0.15f)),
            "the announced count comes from the lobby, so an empty lobby still schedules the minimum");
        Assert.That(_round.SelectedThirdParties.Select(party => party.ID),
            Is.EqualTo(new[] { "CMUTestLargeSurvivors" }),
            "a round-start test faction is scheduled so the survivor roll can run");
    }

    [Test]
    public void ScheduledSurvivorCountComesFromTheLobbyAndSurvivesAStateReset()
    {
        var expected = SurvivorCountLimits.FromLobby(40, 0.15f);
        SetReadyPlayers(40);
        Assert.That(_round.TryLockDistressSignalThirdParties(out var announced), Is.True);
        Assert.That(announced, Is.EqualTo(expected),
            "40 ready players at 0.15 survivors each");
        Assert.That(_round.ScheduledSurvivorCount, Is.EqualTo(expected));

        // Lobby bookkeeping can clear the announced count before round start; the hard cap is recomputed from
        // the lobby, so the group cannot silently vanish and cannot drift with the parties that were drawn.
        _state.DistressSignalSurvivorCount = 0;

        Assert.That(_round.ScheduledSurvivorCount, Is.EqualTo(expected));
    }

    [Test]
    public void ScheduledSurvivorCountIsIndependentOfTheDrawnParties()
    {
        SetReadyPlayers(40);
        var before = _round.ScheduledSurvivorCountFor(40);
        Assert.That(_round.TryLockDistressSignalThirdParties(out var announced), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(announced, Is.EqualTo(before),
                "the announced count is the lobby's, not the sum of whatever parties were selected");
            Assert.That(_round.ScheduledSurvivorCountFor(40), Is.EqualTo(before),
                "the count does not drift with the third-party draw");
            Assert.That(_round.ScheduledSurvivorCountFor(80), Is.EqualTo(SurvivorCountLimits.MaxSurvivors),
                "a large lobby reaches the hard ceiling");
        });
    }

    private void SetReadyPlayers(int count)
    {
        var index = 0;
        foreach (var user in _statuses.Keys.ToArray())
            _statuses[user] = index++ < count ? PlayerGameStatus.ReadyToPlay : PlayerGameStatus.NotReadyToPlay;
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .SetValue(target, value);
}
