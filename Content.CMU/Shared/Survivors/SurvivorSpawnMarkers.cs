namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     Which marker categories may place a survivor on the colony.
/// </summary>
/// <remarks>
///     Survivors are colony inhabitants. They insert through the same third-party insert markers every other
///     ground party uses (the <c>thirdparty*</c> family), and additionally through the colony's own survivor
///     spawn points. Generic threat markers belong to the xenomorph threat and must never place a survivor, and
///     nothing outside the round's planet map counts - the ship and its dropships are not the colony.
/// </remarks>
public static class SurvivorSpawnMarkers
{
    /// <summary>
    ///     Whether a marker is colony insert ground for a survivor.
    /// </summary>
    /// <param name="insertMarker">The marker carries <c>AuInsertMarkerComponent</c>.</param>
    /// <param name="thirdPartyThreatMarker">The marker is a <c>ThreatSpawnMarker</c> flagged third-party.</param>
    /// <param name="survivorSpawnPoint">The marker is a spawn point whose job is a survivor job.</param>
    public static bool IsColonyInsertGround(bool insertMarker, bool thirdPartyThreatMarker, bool survivorSpawnPoint)
        => insertMarker || thirdPartyThreatMarker || survivorSpawnPoint;

    /// <summary>
    ///     Whether a marker sits somewhere a survivor may spawn. Before a planet has been picked every map is
    ///     accepted, so the pre-round announcement check still sees the markers that exist.
    /// </summary>
    public static bool IsAllowedMap(bool planetKnown, bool markerOnPlanet)
        => !planetKnown || markerOnPlanet;

    /// <summary>
    ///     Whether a marker may still be handed to a survivor group. A marker claimed by one group this round is
    ///     not offered to the other, so a faction group and a civilian group never spawn on the same ground.
    /// </summary>
    public static bool MarkerIsAvailable(bool reservedByAnotherGroup) => !reservedByAnotherGroup;

    /// <summary>
    ///     Whether a survivor group can insert on the colony at all.
    /// </summary>
    public static bool CanInsertOnColony(int colonyMarkers)
        => colonyMarkers > 0;

    /// <summary>
    ///     Whether a survivor group with no colony ground falls back to a dropship. The group always spawns -
    ///     a map without insert ground is a content gap, not a reason to drop players back to the lobby - so the
    ///     fallback is a dropship, and the caller logs the gap for mappers to fix.
    /// </summary>
    public static bool FallsBackToDropship(bool canInsertOnColony)
        => !canInsertOnColony;
}
