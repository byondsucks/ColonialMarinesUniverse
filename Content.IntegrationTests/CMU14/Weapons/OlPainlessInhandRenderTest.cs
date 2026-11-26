using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.Utility;
using Robust.Shared.Prototypes;
using Robust.UnitTesting.Pool;

namespace Content.IntegrationTests.CMU14.Weapons;

/// <summary>
///     Asks the client-side item system what in-hand layers it resolves for Ol' Painless, using a weapon that
///     renders correctly as a reference. This exercises the same lookup the game uses when an item is held, so an
///     empty result reproduces "no in-hand sprite" with a reason rather than a guess.
/// </summary>
[TestFixture]
public sealed class OlPainlessInhandRenderTest
{
    private const string Painless = "CMUWeaponMinigunOlPainless";
    private const string Reference = "CMUWeaponSMGAM36";

    [Test]
    public async Task InhandRsiLoadsAndHoldsTheStatesTheEngineLooksUp()
    {
        await using var pair = await PoolManager.GetServerClient();

        string inhandPath = "CMU14/Objects/Weapons/Event/ol_painless.rsi";
        string referencePath = "CMU14/Objects/Weapons/HIG/a_m36.rsi";

        await pair.Client.WaitAssertion(() =>
        {
            var cache = pair.Client.ResolveDependency<IResourceCache>();

            var report = new List<string>();
            foreach (var (label, path) in new[] { ("painless", inhandPath), ("reference", referencePath) })
            {
                var resPath = new ResPath("/Textures") / path;

                // If the RSI fails to load, every state in it is unavailable - that alone would explain a gun with
                // no in-hand sprites while its separate world-sprite sheet still draws.
                Assert.That(cache.TryGetResource<RSIResource>(resPath, out var resource), Is.True,
                    $"{label}: {resPath} failed to load as an RSI");

                var rsi = resource!.RSI;
                foreach (var state in new[]
                         {
                             "inhand-left", "inhand-right", "wielded-inhand-left", "wielded-inhand-right",
                         })
                {
                    Assert.That(rsi.TryGetState(state, out var rsiState), Is.True,
                        $"{label}: {resPath} has no state {state}");

                    report.Add($"{label} {state}: {rsiState!.Frame0.Width}x{rsiState.Frame0.Height}");
                }
            }

            foreach (var line in report)
                TestContext.Out.WriteLine(line);
        });

        await pair.CleanReturnAsync();
    }
}
