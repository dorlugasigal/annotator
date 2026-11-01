using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>What a raised lens stands for on the glass toolbar (WIN-UI-09).</summary>
public enum ToolbarLensRole
{
    /// <summary>The chosen tool: a faint accent tint. The whole lens is half as strong while apps get the clicks.</summary>
    Chosen,

    /// <summary>Apps, in the toolbar and on the one-button toolbar: the body alone.</summary>
    Neutral,

    /// <summary>
    /// A toggle that is on, the Spotlight or Zoom menu while something in it is on, Properties while they show, Board while
    /// White or Black, and the mode-strip chips: the chosen tool's lens, under a symbol in the accent color.
    /// </summary>
    On,

    /// <summary>Draw, in the toolbar and on the one-button toolbar: a faint green tint.</summary>
    Draw,

    /// <summary>The pending-zoom chip: a faint orange tint.</summary>
    Waiting,
}

/// <summary>One lens's colors: the body and the rim from top to bottom, the role's tint over the body, and how dark its shadow is.</summary>
public sealed record ToolbarLensLook(RGBA BodyTop, RGBA BodyBottom, RGBA? Tint, RGBA RimTop, RGBA RimBottom, double ShadowOpacity);

/// <summary>
/// Glass toolbar's raised lens (WIN-UI-09): what the glass toolbar draws behind a chosen or highlighted button instead of
/// the solid toolbar's flat tint, so it stays easy to see what is chosen or on over whatever shows through. Every lens is
/// the same light lens in the button's own size and radius, with a faint tint; color stays in the symbols. It is only
/// decoration. The numbers are the Mac's.
/// </summary>
public static class ToolbarLens
{
    /// <summary>The shadow's blur, about one standard deviation, as SwiftUI's shadow radius.</summary>
    public const double ShadowBlur = 3;

    /// <summary>How far the shadow sits below the lens.</summary>
    public const double ShadowOffset = 1.5;

    /// <summary>The chosen tool's whole lens while apps get the clicks.</summary>
    public const double AppsOpacity = 0.5;

    /// <summary>The Properties button's lens: radius 7, reaching 3 points past its frame on the left and right and 2 points on the top and bottom.</summary>
    public const double PropertiesRadius = 7;

    public const double PropertiesOutsetX = 3;

    public const double PropertiesOutsetY = 2;

    /// <summary>
    /// The shadow as rounded rectangles stacked from the outside in, each a step of 1.5 points closer to the shadow's own
    /// edge (positive outside it), with the share of the shadow's opacity it adds: together they follow the edge of a
    /// <see cref="ShadowBlur"/>-point Gaussian blur. The innermost step is under the lens's body.
    /// </summary>
    public static IReadOnlyList<(double Outset, double Share)> ShadowSteps { get; } =
        [(5, 0.0783), (3.5, 0.1014), (2, 0.1588), (0.5, 0.1947), (-1, 0.1869), (-2.5, 0.1582)];

    private static readonly RGBA s_white = new(1, 1, 1);

    /// <summary>
    /// The lens for <paramref name="role"/> in the light or <paramref name="dark"/> theme, with the theme's accent, green
    /// (the Draw dot's) and orange.
    /// </summary>
    public static ToolbarLensLook For(ToolbarLensRole role, bool dark, RGBA accent, RGBA green, RGBA orange)
    {
        RGBA top = White(dark ? 0.26 : 0.82), bottom = White(dark ? 0.1 : 0.5);
        RGBA? tint = role switch
        {
            ToolbarLensRole.Chosen or ToolbarLensRole.On => accent with { Alpha = dark ? 0.18 : 0.1 },
            ToolbarLensRole.Draw => green with { Alpha = dark ? 0.16 : 0.12 },
            ToolbarLensRole.Waiting => orange with { Alpha = dark ? 0.16 : 0.12 },
            _ => null,
        };
        // SwiftUI's shadow is as strong as what casts it is opaque: the body, on average, under the tint.
        double opaque = 1 - (1 - (top.Alpha + bottom.Alpha) / 2) * (1 - (tint?.Alpha ?? 0));
        return new ToolbarLensLook(top, bottom, tint, White(dark ? 0.55 : 0.95), White(dark ? 0.06 : 0.25), (dark ? 0.35 : 0.16) * opaque);
    }

    private static RGBA White(double alpha) => s_white with { Alpha = alpha };
}
