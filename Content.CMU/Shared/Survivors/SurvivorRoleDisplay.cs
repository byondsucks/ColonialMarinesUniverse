using Robust.Shared.Localization;

namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     Resolves the name the survivor role picker shows for a role body.
/// </summary>
public static class SurvivorRoleDisplay
{
    /// <summary>
    ///     Prefers the faction-specific subtype name, then the role body's own name, then the prototype id.
    ///     A missing subtype never borrows another faction's name.
    /// </summary>
    public static string Resolve(string protoId, string? subtypeName, string? bodyName)
    {
        if (!string.IsNullOrWhiteSpace(subtypeName))
            return Localize(subtypeName);

        if (!string.IsNullOrWhiteSpace(bodyName))
            return Localize(bodyName);

        return protoId;
    }

    private static string Localize(string value)
        => Loc.TryGetString(value, out var localized) ? localized : value;
}
