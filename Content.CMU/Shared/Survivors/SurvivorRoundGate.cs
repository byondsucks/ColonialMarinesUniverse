namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     Which rounds may host a survivor group. Survivors are a distress signal feature: any other gamemode has
///     no colony under attack, so the scheduler must not take survivor rollers there.
/// </summary>
public static class SurvivorRoundGate
{
    /// <summary>The round preset that fields survivors.</summary>
    public const string DistressSignalPreset = "DistressSignal";

    /// <summary>
    ///     Whether the round's gamemode hosts survivors. A known gamemode other than the distress signal never
    ///     does; a round whose preset is not decided yet (still in the lobby, or an admin forcing the round
    ///     before the gamemode vote) is not a *different* gamemode, so it is left to the rest of the survivor
    ///     checks rather than being refused outright.
    /// </summary>
    public static bool IsSurvivorRound(string? presetId)
        => presetId == null
           || string.Equals(presetId, DistressSignalPreset, StringComparison.OrdinalIgnoreCase);
}
