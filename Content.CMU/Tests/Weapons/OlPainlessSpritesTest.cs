using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.CMU.Tests.Weapons;

/// <summary>
///     Guards Ol' Painless's sprite wiring. The engine looks in-hand states up in the Item component's sprite RSI
///     (falling back to the Sprite RSI, which only holds the world icon), so the states have to live in exactly the
///     RSI the prototype names - they were previously kept in a different one and drew nothing.
/// </summary>
[TestFixture]
public sealed class OlPainlessSpritesTest
{
    private const string PrototypePath =
        "Content.CMU/Resources/Prototypes/CMU14/Entities/Objects/Weapons/Event/ol_painless.yml";

    private const string InhandRsi =
        "CMU14/Objects/Weapons/Event/ol_painless.rsi";

    private const string WorldRsi =
        "CMU14/Objects/Weapons/Event/ol_painless_item.rsi";

    private static string RepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Content.CMU", "Resources")))
                    return dir.FullName;

                dir = dir.Parent;
            }
        }

        throw new DirectoryNotFoundException("could not locate the repository root");
    }

    /// <summary>
    ///     Resolves a texture path against the content roots, CMU-owned first. CMU textures normally live under
    ///     Content.CMU/Resources, but these weapon sheets were added to the repository's Resources tree.
    /// </summary>
    private static string Textures(params string[] parts)
    {
        var relative = Path.Combine(parts);

        foreach (var root in new[]
                 {
                     Path.Combine(RepoRoot(), "Content.CMU", "Resources", "Textures"),
                     Path.Combine(RepoRoot(), "Resources", "Textures"),
                 })
        {
            var candidate = Path.Combine(root, relative);
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException($"could not find texture path {relative}");
    }

    private static (int X, int Y, List<(string Name, int Directions)> States) ReadMeta(string rsi)
    {
        var path = Path.Combine(Textures(rsi.Split('/')), "meta.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var size = root.GetProperty("size");

        var states = new List<(string, int)>();
        foreach (var state in root.GetProperty("states").EnumerateArray())
        {
            var directions = state.TryGetProperty("directions", out var dirs) ? dirs.GetInt32() : 1;
            states.Add((state.GetProperty("name").GetString()!, directions));
        }

        return (size.GetProperty("x").GetInt32(), size.GetProperty("y").GetInt32(), states);
    }

    private static (int Width, int Height) PngSize(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);
        var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return (width, height);
    }

    [Test]
    public void ItemDeclaresTheInhandSpriteSheet()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), PrototypePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.That(text, Does.Contain($"sprite: {InhandRsi}"),
            "the Item component must name the RSI that holds the in-hand states, or the engine finds none");
    }

    [Test]
    public void InhandRsiHoldsEveryStateTheEngineAsksFor()
    {
        var (sizeX, sizeY, states) = ReadMeta(InhandRsi);
        var byName = states.ToDictionary(s => s.Name, s => s.Directions);

        foreach (var state in new[] { "inhand-left", "inhand-right", "wielded-inhand-left", "wielded-inhand-right" })
        {
            Assert.That(byName, Does.ContainKey(state), $"{InhandRsi} is missing {state}");
            Assert.That(byName[state], Is.EqualTo(4), $"{state} must carry all four directions");

            var png = Path.Combine(Textures(InhandRsi.Split('/')), state + ".png");
            var (width, height) = PngSize(png);
            Assert.That(height, Is.EqualTo(sizeY * 4), $"{state}.png must hold four stacked directions");
            Assert.That(width % sizeX, Is.Zero, $"{state}.png width must be a whole number of cells");
        }
    }

    [Test]
    public void WorldSpriteIsOneTileWideAndCentredOnItsArtwork()
    {
        var (sizeX, sizeY, states) = ReadMeta(WorldRsi);

        // The source sheet pads this state to a 64x32 cell, but the artwork fits one 32px cell. Keeping the padded
        // cell made the sprite two tiles wide, so the entity anchor (the cell centre) landed at the end of the
        // artwork and the dropped weapon appeared to pivot on its barrel rather than its middle.
        Assert.That(sizeX, Is.EqualTo(32), "the world sprite must occupy a single tile");
        Assert.That(sizeY, Is.EqualTo(32));
        Assert.That(states.Select(s => s.Name), Does.Contain("base"));

        var path = Path.Combine(Textures(WorldRsi.Split('/')), "base.png");
        var (width, height) = PngSize(path);
        Assert.That((width, height), Is.EqualTo((32, 32)));

        var (minX, maxX) = ArtBounds(path);
        var artCentre = (minX + maxX) / 2.0;
        var cellCentre = (sizeX - 1) / 2.0;
        Assert.That(System.Math.Abs(artCentre - cellCentre), Is.LessThanOrEqualTo(1.0),
            $"the artwork must be centred in its cell (art {minX}..{maxX}, centre {artCentre}, cell centre {cellCentre})");
    }

    /// <summary>Horizontal extent of the non-transparent pixels of a PNG.</summary>
    private static (int Min, int Max) ArtBounds(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (width, height) = PngSize(path);

        using var stream = new MemoryStream(bytes);
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(stream);

        var min = width;
        var max = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (image[x, y].A == 0)
                    continue;

                if (x < min) min = x;
                if (x > max) max = x;
            }
        }

        Assert.That(max, Is.GreaterThanOrEqualTo(0), $"{path} is fully transparent");
        return (min, max);
    }
}
