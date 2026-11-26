namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     Validates the survivor subtype registry: every role an eligible survivor faction can spawn needs one
///     authoritative subtype definition, with a faction-specific name and the faction it belongs to.
/// </summary>
public static class SurvivorRoleRegistry
{
    public enum Issue
    {
        /// <summary>The party spawn lists a role body with no survivorRole subtype definition.</summary>
        MissingSubtype,

        /// <summary>The subtype exists but has no player-facing name.</summary>
        MissingName,

        /// <summary>The subtype's faction does not match the party that spawns the role.</summary>
        WrongParty,
    }

    public readonly record struct Finding(Issue Issue, string Party, string Role);

    /// <summary>
    ///     <paramref name="parties"/> lists every eligible survivor faction with the role bodies its spawn
    ///     template provides; <paramref name="subtypes"/> maps a role body id to its subtype definition
    ///     (name and owning faction).
    /// </summary>
    public static List<Finding> Validate(
        IEnumerable<(string Party, IEnumerable<string> Roles)> parties,
        IReadOnlyDictionary<string, (string? Name, string? Party)> subtypes)
    {
        var findings = new List<Finding>();
        foreach (var (party, roles) in parties)
        {
            foreach (var role in roles)
            {
                if (!subtypes.TryGetValue(role, out var subtype))
                {
                    findings.Add(new Finding(Issue.MissingSubtype, party, role));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(subtype.Name))
                    findings.Add(new Finding(Issue.MissingName, party, role));

                if (!string.Equals(subtype.Party, party, StringComparison.Ordinal))
                    findings.Add(new Finding(Issue.WrongParty, party, role));
            }
        }

        return findings;
    }
}
