using Content.Shared.Humanoid;

namespace Content.Server.Humanoid.Components;

[RegisterComponent]
public sealed partial class RandomHumanoidAppearanceComponent : Component
{
    [DataField]
    public bool RandomizeName = true;

    // CMU14: the fields below pin parts of a randomised profile, for hand-authored named characters that
    // must not roll their own appearance (Dutch's Dozen's Arnold, for instance).
    [DataField]
    public Sex? Sex;

    /// <summary>Facial-hair marking applied after randomisation.</summary>
    [DataField]
    public string? FacialHair;

    /// <summary>Age in years; the randomised profile keeps its own age when this is unset.</summary>
    [DataField]
    public int? Age;

    /// <summary>Colour applied to the hair and facial hair markings when set.</summary>
    [DataField]
    public Color? HairColor;

    /// <summary>Eye colour override.</summary>
    [DataField]
    public Color? EyeColor;

    /// <summary>
    ///     Fixed skin colour. Without it the skin tone is randomised, which is how a named character such as
    ///     Arnold 'Dutch' Schäfer ended up with an arbitrary tone.
    /// </summary>
    [DataField]
    public Color? SkinColor;

    /// <summary>
    /// Optional hairstyle applied after randomization.
    /// </summary>
    [DataField]
    public string? Hair;
}
