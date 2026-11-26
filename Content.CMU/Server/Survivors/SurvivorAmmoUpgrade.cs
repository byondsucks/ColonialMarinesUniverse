using System.Linq;
using System;
using System.Collections.Generic;

namespace Content.Server.CMU14.Survivors.Setup;

/// <summary>
///     Some survivor weapons ship an armour-piercing magazine variant (the VAL's VAL AP, the M39's M39 AP).
///     A survivor who picks such a weapon carries a couple of AP magazines mixed in with the standard ones.
///     Factions whose weapons have no AP variant are left alone - this never invents ammunition.
/// </summary>
public static class SurvivorAmmoUpgrade
{
    /// <summary>Fewest magazines converted to AP.</summary>
    public const int MinApMagazines = 1;

    /// <summary>Most magazines converted to AP.</summary>
    public const int MaxApMagazines = 3;

    /// <summary>
    ///     True when a magazine prototype id names an armour-piercing load: the name ends in "AP", or in "AP"
    ///     followed only by a capacity/format suffix (APExt, APDrum, APExtended). Magazines are named
    ///     "&lt;weapon&gt;AP" with no separator, so the marker is matched as a suffix rather than as a word.
    ///     "HEAP" (high-explosive armour-piercing) also ends in "AP" but is a different load, so it is excluded
    ///     explicitly - treating it as an AP variant handed survivors the wrong ammunition.
    /// </summary>
    public static bool IsArmorPiercing(string prototypeId)
    {
        const string ap = "AP";
        var index = prototypeId.LastIndexOf(ap, StringComparison.Ordinal);
        if (index < 0)
            return false;

        // "HEAP" ends in "AP" and is not the armour-piercing variant of anything: skip it.
        if (index >= 2 && prototypeId[index - 2] == 'H' && prototypeId[index - 1] == 'E')
            return false;

        var tail = prototypeId[(index + ap.Length)..];
        return tail.Length == 0
               || tail.Equals("Ext", StringComparison.Ordinal)
               || tail.Equals("Extended", StringComparison.Ordinal)
               || tail.Equals("Drum", StringComparison.Ordinal);
    }

    /// <summary>
    ///     The standard magazine an AP magazine is the upgrade of, or null when the id names no standard load.
    ///     A weapon whose magazine slot ships an AP magazine (the M39B/2 starts with CMMagazineSMGM63AP) would
    ///     otherwise issue AP rounds as the survivor's whole base supply, leaving the upgrade pass nothing normal
    ///     to convert.
    /// </summary>
    public static string? FindStandardVariant(string magazine, IEnumerable<string> candidates)
    {
        if (!IsArmorPiercing(magazine))
            return null;

        var ids = candidates as IReadOnlyCollection<string> ?? candidates.ToList();

        // The id reads <weapon>AP<capacity>: strip the capacity suffix, drop the AP marker, then put the capacity
        // back, so CMMagazineSMGM63APExt maps to CMMagazineSMGM63Ext and CMMagazineSMGM63AP to CMMagazineSMGM63.
        var stem = magazine;
        var capacity = string.Empty;
        foreach (var suffix in new[] { "Extended", "Ext", "Drum" })
        {
            if (!stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            capacity = stem[^suffix.Length..];
            stem = stem[..^suffix.Length];
            break;
        }

        if (!stem.EndsWith("AP", StringComparison.OrdinalIgnoreCase))
            return null;

        var standard = stem[..^2] + capacity;
        var exact = ids.FirstOrDefault(id => string.Equals(id, standard, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        // Fall back to the same weapon without the capacity suffix.
        var plain = stem[..^2];
        exact = ids.FirstOrDefault(id => string.Equals(id, plain, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        // Otherwise the longest standard id that is a prefix of the AP id (differing brand prefixes aside).
        return ids
            .Where(id => !IsArmorPiercing(id))
            .Where(id => magazine.StartsWith(id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(id => id.Length)
            .FirstOrDefault();
    }

    /// <summary>
    ///     The armour-piercing magazine matching a standard one, or null when that weapon has no AP variant.
    ///     An exact "&lt;id&gt;AP" match wins; otherwise the AP candidate sharing the longest run of characters
    ///     with the standard magazine is used, provided it shares at least half of the standard id (that is
    ///     what lets RMC-branded and CMU-branded magazines of the same weapon find each other).
    /// </summary>
    public static string? FindArmorPiercingVariant(string magazine, IEnumerable<string> candidates)
    {
        var exact = magazine + "AP";
        string? best = null;
        var bestScore = 0;

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, magazine, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsArmorPiercing(candidate))
                continue;

            if (string.Equals(candidate, exact, StringComparison.OrdinalIgnoreCase))
                return candidate;

            // The AP variant of a magazine is that magazine's own name plus the AP marker, optionally with a
            // different brand prefix (CMU/ RMC-branded pairs of the same weapon). Anything else is a different
            // magazine - "the longest shared run" used to match CMUMagazineSMGAM36 to an MP5 HEAP drum.
            if (!SameMagazineFamily(magazine, candidate))
                continue;

            var shared = LongestCommonSubstring(magazine, candidate);
            if (shared > bestScore)
            {
                bestScore = shared;
                best = candidate;
            }
        }

        // Require a meaningful overlap so a stray AP magazine of another calibre is never picked.
        return bestScore * 2 >= magazine.Length ? best : null;
    }

    /// <summary>
    ///     True when the two ids name the same magazine ignoring the brand prefix that prefixes them
    ///     (CMU- and RMC-branded magazines of one weapon share everything after it).
    /// </summary>
    private static bool SameMagazineFamily(string standard, string candidate)
    {
        var a = WithoutBrand(standard);
        var b = WithoutBrand(candidate);
        return b.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(b, StringComparison.Ordinal);
    }

    private static string WithoutBrand(string prototypeId)
    {
        if (prototypeId.StartsWith("CMU", StringComparison.Ordinal))
            return prototypeId[3..];

        if (prototypeId.StartsWith("RMC", StringComparison.Ordinal))
            return prototypeId[3..];

        return prototypeId;
    }

    /// <summary>
    ///     How many of the carried magazines to convert, clamped to what the survivor actually has.
    ///     <paramref name="roll"/> returns a value in [0, count).
    /// </summary>
    public static int MagazinesToUpgrade(int available, Func<int, int> roll)
    {
        if (available <= 0)
            return 0;

        var span = MaxApMagazines - MinApMagazines + 1;
        var offset = Math.Clamp(roll(span), 0, span - 1);
        return Math.Min(available, MinApMagazines + offset);
    }

    /// <summary>Length of the longest substring the two ids share, case-insensitively.</summary>
    public static int LongestCommonSubstring(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;

        var previous = new int[b.Length + 1];
        var best = 0;
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            for (var j = 1; j <= b.Length; j++)
            {
                if (char.ToUpperInvariant(a[i - 1]) != char.ToUpperInvariant(b[j - 1]))
                    continue;

                current[j] = previous[j - 1] + 1;
                if (current[j] > best)
                    best = current[j];
            }

            previous = current;
        }

        return best;
    }
}
