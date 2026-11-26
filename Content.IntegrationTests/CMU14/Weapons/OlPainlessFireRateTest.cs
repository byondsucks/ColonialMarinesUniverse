using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Wieldable;
using NUnit.Framework;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.CMU14.Weapons;

/// <summary>
///     Measures the realised firing cadence of Ol' Painless against the HazOps GAU-19/H minigun. A real shooter holds
///     and wields the weapon, and the production shooting path is driven once per tick while the projectiles it
///     actually spawns are counted - configured rates alone do not prove cadence.
/// </summary>
[TestFixture]
public sealed class OlPainlessFireRateTest
{
    private const string Painless = "CMUWeaponMinigunOlPainless";
    private const string Gau = "AU14SmartGunMinigunHAZOPS";
    private const string M60 = "RMCWeaponLMGM60";
    private const string Shooter = "CMMobHuman";

    private const int MeasureTicks = 180; // three seconds at the server tick rate

    [Test]
    public async Task OlPainlessFiresAtTheMinigunRateNotTheLmgRate()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var gunSystem = ent.System<SharedGunSystem>();
        var hands = ent.System<SharedHandsSystem>();
        var wieldable = ent.System<SharedWieldableSystem>();
        var map = await pair.CreateTestMap();

        var componentLists = new System.Collections.Generic.List<string>();

        // Hands a weapon to a wielding mob, fires it once per tick and counts distinct projectiles.
        async Task<(int Shots, float FireRate, float RawFireRate, string Samples, string Components)> Measure(string prototype)
        {
            EntityUid gun = default;
            EntityUid shooter = default;
            var components = string.Empty;

            await server.WaitAssertion(() =>
            {
                shooter = ent.SpawnEntity(Shooter, map.GridCoords);
                gun = ent.SpawnEntity(prototype, map.GridCoords);
                Assert.That(ent.HasComponent<GunComponent>(gun), Is.True, $"{prototype} has no GunComponent");
                Assert.That(hands.TryForcePickupAnyHand(shooter, gun), Is.True, $"{prototype} could not be held");
                Assert.That(wieldable.TryWield(gun, shooter), Is.True, $"{prototype} could not be wielded");
            });

            // Let MapInit insert the starting magazine and let selective fire derive the fire rate.
            await pair.RunTicksSync(5);

            // Which timing-relevant components the weapon actually carries - this is what identifies the throttle.
            await server.WaitAssertion(() =>
            {
                components = string.Join(", ", ent.GetComponents(gun)
                    .Select(c => c.GetType().Name)
                    .Where(n => n.Contains("Gun") || n.Contains("Wield") || n.Contains("Stack")));
            });


            // Does an explicit refresh change the derived rate? If it does, the value was stale rather than throttled.
            float beforeRefresh = 0f;
            float afterRefresh = 0f;
            await server.WaitAssertion(() =>
            {
                if (!ent.TryGetComponent<GunComponent>(gun, out var gc))
                    return;

                beforeRefresh = gc.FireRateModified;
                gunSystem.RefreshModifiers((gun, gc));
                afterRefresh = gc.FireRateModified;
            });

            var seen = new HashSet<EntityUid>();
            var samples = new List<string>();
            var fireRate = 0f;
            var rawFireRate = 0f;

            for (var tick = 0; tick < MeasureTicks; tick++)
            {
                await pair.RunTicksSync(1);

                await server.WaitAssertion(() =>
                {
                    if (!ent.TryGetComponent<GunComponent>(gun, out var gunComp))
                        return;

                    fireRate = gunComp.FireRateModified;
                    rawFireRate = gunComp.FireRate;

                    if (tick % 30 == 0)
                        samples.Add($"{tick}:{gunComp.FireRateModified:F2}");
                    gunSystem.AttemptShoot(shooter, (gun, gunComp),
                        ent.GetComponent<TransformComponent>(shooter).Coordinates);

                    var query = ent.EntityQueryEnumerator<ProjectileComponent>();
                    while (query.MoveNext(out var uid, out _))
                        seen.Add(uid);
                });
            }

            samples.Add($"stale:{beforeRefresh:F2} refreshed:{afterRefresh:F2}");
            return (seen.Count, fireRate, rawFireRate, string.Join(" ", samples), components);
        }

        var painlessShot = await Measure(Painless);
        var gauShot = await Measure(Gau);
        var m60Shot = await Measure(M60);

        var seconds = MeasureTicks * server.Timing.TickPeriod.TotalSeconds;
        var painlessPerSecond = painlessShot.Shots / seconds;
        var gauPerSecond = gauShot.Shots / seconds;

        TestContext.Out.WriteLine(
            $"Ol' Painless: {painlessShot.Shots} shots in {seconds:F2}s = {painlessPerSecond:F2}/s " +
            $"(FireRateModified {painlessShot.FireRate:F2})");
        TestContext.Out.WriteLine(
            $"GAU-19/H:    {gauShot.Shots} shots in {seconds:F2}s = {gauPerSecond:F2}/s " +
            $"(FireRateModified {gauShot.FireRate:F2})");

        // Persist the measurements so the cadence is demonstrable outside the test runner's captured output.
        var report = string.Join(System.Environment.NewLine,
            $"Ol' Painless: {painlessShot.Shots} shots in {seconds:F2}s = {painlessPerSecond:F2}/s, FireRateModified {painlessShot.FireRate:F2}, Gun.FireRate {painlessShot.RawFireRate:F2}, samples[tick:rate] {painlessShot.Samples}, components: {painlessShot.Components}",
            $"GAU-19/H:    {gauShot.Shots} shots in {seconds:F2}s = {gauPerSecond:F2}/s, FireRateModified {gauShot.FireRate:F2}, Gun.FireRate {gauShot.RawFireRate:F2}, components: {gauShot.Components}",
            $"M60 (chassis): {m60Shot.Shots} shots = {m60Shot.Shots / seconds:F2}/s, FireRateModified {m60Shot.FireRate:F2}, Gun.FireRate {m60Shot.RawFireRate:F2}, samples {m60Shot.Samples}, components: {m60Shot.Components}");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ol_painless_firerate.txt"), report);

        Assert.That(painlessShot.Shots, Is.GreaterThan(0), "Ol' Painless did not fire at all");
        Assert.That(gauShot.Shots, Is.GreaterThan(0), "the GAU did not fire at all");
        Assert.That(painlessPerSecond, Is.GreaterThanOrEqualTo(gauPerSecond * 0.95),
            $"Ol' Painless fires {painlessPerSecond:F2}/s against the GAU's {gauPerSecond:F2}/s");

        await pair.CleanReturnAsync();
    }

}
