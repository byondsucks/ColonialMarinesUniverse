using Content.Shared.CMU14.Survivors;

namespace Content.Server.CMU14.Survivors.Setup;

/// <summary>
///     A loadout choice waiting for the survivor to be dressed. Role gear is applied when the body is
///     initialised, which can happen after the mind is assigned; applying the loadout before that would
///     make the role's own gun and magazines overflow into the world. The gear system drains this once the
///     role's starting gear is on the mob.
/// </summary>
[RegisterComponent]
public sealed partial class CMUSurvivorLoadoutPendingComponent : Component
{
    [DataField]
    public string LoadoutId = string.Empty;

    [DataField]
    public SurvivorLoadoutSelection Selection;

    /// <summary>
    ///     The survivor role body the player claimed. The spawn-time resolve needs it, otherwise the
    ///     faction-wide option lists win and a role's own armor/helmet (a PMC engineer's, say) is lost.
    /// </summary>
    public string? Role;

    /// <summary>When the loadout was queued; used as a fallback so a body is never left undressed of choices.</summary>
    public TimeSpan RequestedAt;
}
