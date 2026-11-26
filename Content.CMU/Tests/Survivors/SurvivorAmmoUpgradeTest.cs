using System.Collections.Generic;
using Content.Server.CMU14.Survivors.Setup;
using NUnit.Framework;

namespace Content.CMU.Tests.Survivors;

/// <summary>
///     The survivor ammunition upgrade: one to three of the magazines the kit issues are converted to the
///     armour-piercing variant. Driving the roll directly proves every upgrade count is reachable, which sampling
///     random spawns cannot.
/// </summary>
[TestFixture]
public sealed class SurvivorAmmoUpgradeTest
{
    [Test]
    public void EveryUpgradeCountIsReachable()
    {
        // The roll is a zero-based offset into [MinApMagazines, MaxApMagazines].
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(5, _ => 0), Is.EqualTo(1), "roll 0 upgrades one");
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(5, _ => 1), Is.EqualTo(2), "roll 1 upgrades two");
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(5, _ => 2), Is.EqualTo(3), "roll 2 upgrades three");
        });
    }

    [Test]
    public void TheUpgradeNeverExceedsTheMagazinesOnHand()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(0, _ => 0), Is.EqualTo(0), "nothing to upgrade");
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(1, _ => 2), Is.EqualTo(1), "one of one");
            Assert.That(SurvivorAmmoUpgrade.MagazinesToUpgrade(2, _ => 2), Is.EqualTo(2), "two of two");
        });
    }

    [Test]
    public void TheIssuedMagazineIsTheStandardLoadTheUpgradeConverts()
    {
        var candidates = new List<string>
        {
            "CMMagazineSMGM63", "CMMagazineSMGM63AP", "CMMagazineSMGM63Ext", "CMMagazineSMGM63APExt",
            "CMMagazineRifleM54C", "CMMagazineRifleM54CAP",
        };

        Assert.Multiple(() =>
        {
            // The M39B/2 ships CMMagazineSMGM63AP in its magazine slot; survivors are issued the standard load.
            Assert.That(SurvivorAmmoUpgrade.FindStandardVariant("CMMagazineSMGM63AP", candidates),
                Is.EqualTo("CMMagazineSMGM63"), "the AP magazine resolves to its standard load");
            Assert.That(SurvivorAmmoUpgrade.FindStandardVariant("CMMagazineSMGM63APExt", candidates),
                Is.EqualTo("CMMagazineSMGM63Ext"), "the extended AP magazine resolves to the extended load");
            Assert.That(SurvivorAmmoUpgrade.FindStandardVariant("CMMagazineSMGM63", candidates),
                Is.Null, "a standard magazine is not mapped to anything");

            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("CMMagazineSMGM63AP"), Is.True);
            Assert.That(SurvivorAmmoUpgrade.IsArmorPiercing("CMMagazineSMGM63"), Is.False);
        });
    }

    [Test]
    public void TheApVariantOfTheIssuedMagazineIsFound()
    {
        var candidates = new List<string> { "CMMagazineSMGM63", "CMMagazineSMGM63AP", "CMMagazineRifleM54CAP" };

        Assert.That(SurvivorAmmoUpgrade.FindArmorPiercingVariant("CMMagazineSMGM63", candidates),
            Is.EqualTo("CMMagazineSMGM63AP"), "the standard M39 magazine upgrades to the M39 AP magazine");
    }
}
