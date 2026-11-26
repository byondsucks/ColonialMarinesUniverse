using System.Collections.Generic;
using System.Linq;
using Content.Shared.CMU14.Threats;
using Robust.Shared.Localization;

namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     A party's briefing keys: the intro and story locale ids as authored, before any map override.
/// </summary>
public readonly record struct SurvivorBriefingText(LocId? Intro, LocId? Story);

/// <summary>
///     A survivor group's spawn briefing, already resolved for the map they actually landed on. Either part
///     may be absent, in which case the player is simply shown the other one.
/// </summary>
public readonly record struct SurvivorBriefing(string? Intro, string? Story);

/// <summary>
///     Picks the intro/story text a survivor sees when they spawn. Factions key their variants by the planet's
///     survivor scenario, so the same faction reads differently on a crashing-ship map than it does on an
///     uprising map. A scenario entry wins; anything it leaves unset falls back to the party defaults, and
///     text never comes from another faction.
/// </summary>
public static class SurvivorBriefingRules
{
    /// <summary>
    ///     <paramref name="scenarioId"/> is the active planet's survivor scenario (for example
    ///     <c>crash_landing</c>); a null or unknown scenario simply uses the party defaults.
    /// </summary>
    /// <param name="colonyName">
    ///     The voted map's name, substituted into the text wherever it names the colony.
    /// </param>
    public static SurvivorBriefing Resolve(string? scenarioId, SurvivorBriefingText defaults,
        IReadOnlyDictionary<string, SurvivorBriefingText>? scenario, string? colonyName = null)
    {
        var resolvedIntro = defaults.Intro;
        var resolvedStory = defaults.Story;

        if (!string.IsNullOrWhiteSpace(scenarioId) && scenario != null && scenario.TryGetValue(scenarioId, out var overrides))
        {
            resolvedIntro = overrides.Intro ?? resolvedIntro;
            resolvedStory = overrides.Story ?? resolvedStory;
        }

        return new SurvivorBriefing(Localize(resolvedIntro, colonyName), Localize(resolvedStory, colonyName));
    }

    /// <summary>Projects a party's authored scenario overrides into the plain shape the resolver consumes.</summary>
    public static Dictionary<string, SurvivorBriefingText> Project(IReadOnlyDictionary<string, ThirdPartyBriefing>? scenario)
        => scenario == null
            ? new Dictionary<string, SurvivorBriefingText>()
            : scenario.ToDictionary(entry => entry.Key, entry => new SurvivorBriefingText(entry.Value.Intro, entry.Value.Story));

    private static string? Localize(LocId? key, string? colonyName)
    {
        if (key is not { } id || string.IsNullOrWhiteSpace(id.Id))
            return null;

        var colony = string.IsNullOrWhiteSpace(colonyName) ? "the colony" : colonyName;

        // A key with no translation is shown verbatim rather than dropped, so missing text is obvious.
        return Loc.TryGetString(id.Id, out var localized, ("colony", colony)) ? localized : id.Id;
    }
}
