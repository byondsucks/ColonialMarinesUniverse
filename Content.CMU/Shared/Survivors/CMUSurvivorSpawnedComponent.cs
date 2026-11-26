using Robust.Shared.GameObjects;

namespace Content.Shared.CMU14.Survivors;

/// <summary>
///     Marks a body that was spawned as a survivor. Survivors are not the ERT squad their job belongs to, so
///     the job greeting (and its "you are a part of ... party" block) is skipped for them.
/// </summary>
[RegisterComponent]
public sealed partial class CMUSurvivorSpawnedComponent : Component;
