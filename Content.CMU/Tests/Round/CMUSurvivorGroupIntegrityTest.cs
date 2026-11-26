using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Survivors;
using NUnit.Framework;
using Robust.Shared.Network;

namespace Content.CMU.Tests.Round;

/// <summary>
///     Group-integrity rules for the survivor role picker: a raffle must produce exactly one holder, and players
///     who never picked (or lost a raffle) must be spread across the remaining roles rather than stacked on one.
/// </summary>
[TestFixture]
public sealed class CMUSurvivorGroupIntegrityTest
{
    private static NetUserId User() => new(Guid.NewGuid());

    [Test]
    public void UnplacedSurvivorsNeverShareARole()
    {
        var players = new[] { User(), User(), User(), User() };
        var taken = new List<bool> { false, false, false, false, false };

        var assigned = SurvivorSlotAssignment.AssignUnplaced(players, taken, (_, _) => true, roll => roll);

        Assert.That(assigned.Values, Is.Unique, "no two survivors may be given the same role");
        Assert.That(assigned, Has.Count.EqualTo(players.Length), "there were enough free roles for all of them");
    }

    [Test]
    public void UnplacedSurvivorsSkipRolesSomeoneAlreadyHolds()
    {
        var players = new[] { User(), User() };
        var taken = new List<bool> { true, false, false };

        var assigned = SurvivorSlotAssignment.AssignUnplaced(players, taken, (_, _) => true, roll => roll);

        Assert.That(assigned.Values, Has.None.EqualTo(0), "an already claimed role is never handed out again");
        Assert.That(assigned.Values, Is.Unique);
    }

    [Test]
    public void UnplacedSurvivorWithoutAnAcceptableRoleIsLeftOut()
    {
        var synthOnly = User();
        var ordinary = User();
        // Slot 0 is synthetic-only; only the survivor with a synthetic character may take it.
        var taken = new List<bool> { false };

        var assigned = SurvivorSlotAssignment.AssignUnplaced(
            new[] { synthOnly, ordinary },
            taken,
            (user, _) => user == synthOnly,
            roll => roll);

        Assert.That(assigned, Does.ContainKey(synthOnly), "the synthetic slot went to the synthetic survivor");
        Assert.That(assigned, Does.Not.ContainKey(ordinary), "the other survivor cannot fill a synthetic body");
        Assert.That(assigned, Has.Count.EqualTo(1), "one role, one holder");
    }

    [Test]
    public void MoreSurvivorsThanRolesLeavesTheRestUnassignedInsteadOfDoublingUp()
    {
        var players = new[] { User(), User(), User() };
        var taken = new List<bool> { false, false };

        var assigned = SurvivorSlotAssignment.AssignUnplaced(players, taken, (_, _) => true, roll => roll);

        Assert.That(assigned, Has.Count.EqualTo(2), "only as many survivors as there are free roles get one");
        Assert.That(assigned.Values, Is.Unique, "the survivor without a role must not be stacked onto a taken one");
    }

    [Test]
    public void AClaimedRaffleProducesExactlyOneHolder()
    {
        var first = User();
        var second = User();
        var third = User();

        // Two survivors already hold roles of their own; a third holds nothing and is the only eligible claimant.
        var owned = new Dictionary<NetUserId, int> { [first] = 0, [second] = 1 };
        var eligible = SurvivorRoleOwnership.Eligible(new[] { first, second, third }, slot: 2, owned);

        Assert.That(eligible, Is.EqualTo(new[] { third }),
            "survivors who already hold a role cannot win a second one");

        var winner = SurvivorRoleRaffle.PickWinner(eligible, _ => 0);
        Assert.That(winner, Is.EqualTo(third), "the raffle resolves to a single holder");
    }

    [Test]
    public void EveryoneRafflingForTheSameRoleLeavesOnlyTheWinnerHoldingIt()
    {
        var claimants = new[] { User(), User(), User() };
        var winner = SurvivorRoleRaffle.PickWinner(claimants, roll => roll);

        Assert.That(winner, Is.Not.Null);
        var holders = claimants.Where(claimant => claimant == winner).ToList();
        Assert.That(holders, Has.Count.EqualTo(1), "a role has exactly one holder after the raffle");
    }

    [Test]
    public void ClaimingAnOwnedRoleIsRefusedButSwitchingAwayFromAFreeOneIsAllowed()
    {
        var player = User();
        var other = User();

        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 1, currentSlot: null,
            ownerOfCurrentSlot: null, ownerOfRequestedSlot: other), Is.False,
            "a role another survivor holds cannot be claimed");

        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 1, currentSlot: null,
            ownerOfCurrentSlot: null, ownerOfRequestedSlot: null), Is.True,
            "a free role can be claimed");

        Assert.That(SurvivorRoleClaims.CanClaim(player, requestedSlot: 2, currentSlot: 1,
            ownerOfCurrentSlot: null, ownerOfRequestedSlot: null), Is.True,
            "a survivor who has not won yet may change which raffle they are in");
    }

}
