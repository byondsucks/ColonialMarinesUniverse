using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    ///     CMU14 TEMPORARY (survivor rework testing): comma-separated third-party ids the survivor group
    ///     vote draws from, replacing the planet's survivor factions and their population gates. Only the
    ///     listed factions are eligible while this is set.
    ///     Empty (the default) disables the override, so normal eligibility applies and production servers
    ///     are unaffected. Set from the development config preset only; clear it to restore normal play.
    /// </summary>
    public static readonly CVarDef<string> SurvivorTestFactions =
        CVarDef.Create("cmu.survivor.test_factions", "", CVar.SERVERONLY);

    /// <summary>
    ///     Survivors scheduled per ready player. The round's survivor cap is
    ///     round(readyPlayers * ratio) clamped to 2-10, so the number scales with the lobby and never depends
    ///     on which third-party parties were drawn. 0.15 schedules 5 at 33 players and hits the cap from ~67.
    /// </summary>
    public static readonly CVarDef<float> SurvivorCountRatio =
        CVarDef.Create("cmu.survivor.count_ratio", 0.15f, CVar.SERVERONLY);

    /// <summary>
    ///     CMU14 TEMPORARY (survivor rework testing): fixed number of survivors the round schedules, ignoring the
    ///     lobby-derived count. -1 (the default) keeps the normal ratio, so production servers are unaffected.
    ///     Set from the development config preset only; reset to -1 to restore normal play.
    /// </summary>
    public static readonly CVarDef<int> SurvivorTestCount =
        CVarDef.Create("cmu.survivor.test_count", -1, CVar.SERVERONLY);

    /// <summary>
    ///     CMU14 TEMPORARY (survivor rework testing): offer every eligible survivor faction on the ballot
    ///     instead of drawing two, so testers can see and vote for all of them in one round.
    ///     False (the default) keeps the normal two-faction draw, so production servers are unaffected.
    ///     Set from the development config preset only.
    /// </summary>
    public static readonly CVarDef<bool> SurvivorTestAllCandidates =
        CVarDef.Create("cmu.survivor.test_all_candidates", false, CVar.SERVERONLY);
}
