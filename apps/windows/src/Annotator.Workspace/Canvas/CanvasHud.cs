using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// The canvas's heads-up button (Mac DrawingCanvas interactButton): "Use apps · Esc" in the top-left corner of every
/// display's canvas while drawing. A click releases drawing input like Use apps. Frames are in canvas points.
/// </summary>
public static class CanvasHud
{
    /// <summary>The words on the button.</summary>
    public const string UseAppsTitle = "Use apps · Esc";

    /// <summary>The button's accessible name (the Mac's accessibility label).</summary>
    public const string UseAppsLabel = "Use apps";

    /// <summary>The Mac's HUDFader: fade in over 0.16 s while rising 3 points, fade out over 0.12 s.</summary>
    public const double FadeInSeconds = 0.16;

    public const double FadeOutSeconds = 0.12;

    public const double Rise = 3;

    private const double FontSize = 13;

    /// <summary>The Mac button's frame: 150 by 28 points, 16 from the left and 14 from the top. Clicks inside it use apps.</summary>
    public static Rect UseAppsFrame { get; } = new(16, 14, 150, 28);

    /// <summary>The rounded bezel the Mac's rounded push button draws inside its frame: 2 points shorter at the top and bottom.</summary>
    public static Rect UseAppsBezel { get; } = new(16, 16, 150, 24);

    /// <summary>
    /// The button's frame for UI Automation, as AppKit reports the rounded button's accessibility frame and the UX reference
    /// records it: one point outside <see cref="UseAppsFrame"/> on every side (15, 13, 152, 30).
    /// </summary>
    public static Rect UseAppsAccessibleFrame { get; } = RectMath.Outset(UseAppsFrame, 1, 1);

    /// <summary>Mac setDrawingHUDVisible: every canvas shows the button while the workspace draws.</summary>
    public static bool ShowsUseApps(WorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Surfaces == SurfaceMode.Draw;
    }

    /// <summary>
    /// The button as a display list in canvas points: a rounded bezel in the theme's button colors with its title
    /// centered, readable over any desktop (standard controls follow Windows; the Mac draws a system material here). As
    /// AppKit centers a button's title, the Mac's line box (ascender plus descender) is centered in the bezel, so the
    /// title's baseline is where the Mac's is: 33 points from the canvas top in the UX reference.
    /// </summary>
    public static DisplayList UseAppsButton(bool dark)
    {
        Rect bezel = UseAppsBezel;
        PathData shape = new PathBuilder(10, 21).AddRoundedRect(bezel, 5, 5).ToPathData();
        PathData outline = new PathBuilder(10, 21).AddRoundedRect(RectMath.Outset(bezel, -0.5, -0.5), 4.5, 4.5).ToPathData();
        var font = new TextFont(FontDesign.System, FontWeight.Regular, FontSize);
        double lineHeight = MacTextMetrics.Ascender(FontSize) + MacTextMetrics.Descender(FontSize);
        var text = new Rect(bezel.X, bezel.Y + (bezel.Height - lineHeight) / 2, bezel.Width, lineHeight);
        // Windows 11 button colors: ControlFillColorDefault on the solid background, ControlStrokeColorDefault and
        // TextFillColorPrimary, opaque so the title stays readable on any desktop.
        RGBA fill = dark ? new RGBA(0x2D / 255.0, 0x2D / 255.0, 0x2D / 255.0) : new RGBA(0xFB / 255.0, 0xFB / 255.0, 0xFB / 255.0);
        RGBA stroke = dark ? new RGBA(1, 1, 1, 0.09) : new RGBA(0, 0, 0, 0.12);
        RGBA title = dark ? new RGBA(1, 1, 1) : new RGBA(0, 0, 0, 0.894);
        return new DisplayListBuilder()
            .Add(DisplayItem.FillPath(shape, FillRule.NonZero, Paint.Solid(fill)))
            .Add(DisplayItem.StrokePath(outline, new StrokeStyle(1), Paint.Solid(stroke)))
            .Add(DisplayItem.DrawText(new TextRun(UseAppsTitle, text, font, TextAlignment.Center, Paint.Solid(title))))
            .ToList();
    }
}
