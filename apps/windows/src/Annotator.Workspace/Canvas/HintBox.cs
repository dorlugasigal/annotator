using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// The on-screen tip's box (Mac WorkspaceHint.draw and WorkspaceHintView): white text on black at 78% in a rounded box at
/// the canvas's top-left corner, below the Use apps button while drawing. Frames are in canvas points.
/// </summary>
public static class HintBox
{
    /// <summary>The Mac view's widest frame and its height.</summary>
    public const double MaximumWidth = 540;

    public const double ViewHeight = 130;

    /// <summary>The text's inset inside the box: 9 points left and right, 7 above and below.</summary>
    public const double HorizontalInset = 9;

    public const double VerticalInset = 7;

    public const double CornerRadius = 7;

    public const double LineSize = 11;

    public const double FooterSize = 10;

    /// <summary>NSFont.Weight.medium, OpenType weight 500.</summary>
    public const FontWeight Medium = (FontWeight)500;

    /// <summary>The box's fill: black at 78%.</summary>
    public static RGBA Fill { get; } = new(0, 0, 0, 0.78);

    /// <summary>The footer's color: white at 62%.</summary>
    public static RGBA FooterColor { get; } = new(1, 1, 1, 0.62);

    /// <summary>
    /// Mac refreshHint: the view at 16 points from the left, 50 from the top while the Use apps button shows and 14
    /// otherwise, at most 540 points wide and at least 80, and 130 high.
    /// </summary>
    public static Rect ViewFrame(double canvasWidth, bool useAppsShown) =>
        new(16, useAppsShown ? 50 : 14, Math.Min(MaximumWidth, Math.Max(80, canvasWidth - 32)), ViewHeight);

    /// <summary>
    /// Mac WorkspaceHint.draw(_:in:origin:) for the view <paramref name="view"/>: the box at the view's top-left corner,
    /// as wide as the widest line plus 18 points and as high as the Mac's lines plus 14, and one text run per line. The
    /// footer line is 10 points regular at 62% white; the others are 11 points medium white. Lines keep the Mac's TextKit
    /// line heights and first baselines (WIN-REND-03); their widths are measured on Windows (D-4).
    /// </summary>
    public static (DisplayList List, Rect Box) Build(string text, Rect view, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measurer);
        string[] lines = text.Split('\n');
        double width = Math.Min(MaximumWidth, Math.Max(80, view.Width - 16));
        var fonts = new TextFont[lines.Length];
        double textWidth = 0, textHeight = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            bool footer = lines[index] == WorkspaceHint.Footer && index == lines.Length - 1;
            fonts[index] = footer ? new TextFont(FontDesign.System, FontWeight.Regular, FooterSize) : new TextFont(FontDesign.System, Medium, LineSize);
            textWidth = Math.Max(textWidth, measurer.Measure(lines[index], fonts[index]).Width);
            textHeight += MacTextMetrics.LineHeight(fonts[index].Size);
        }
        var box = new Rect(view.X, view.Y, Math.Min(width, Math.Ceiling(textWidth) + 2 * HorizontalInset), Math.Ceiling(textHeight) + 2 * VerticalInset);
        PathData shape = new PathBuilder(4, 9).AddRoundedRect(box, CornerRadius, CornerRadius).ToPathData();
        var builder = new DisplayListBuilder().Add(DisplayItem.FillPath(shape, FillRule.NonZero, Paint.Solid(Fill)));
        double top = box.Y + VerticalInset;
        double inner = box.Width - 2 * HorizontalInset;
        for (int index = 0; index < lines.Length; index++)
        {
            TextFont font = fonts[index];
            double height = MacTextMetrics.LineHeight(font.Size);
            var frame = new Rect(box.X + HorizontalInset, top, inner, height);
            RGBA color = font.Size == FooterSize ? FooterColor : new RGBA(1, 1, 1);
            builder.Add(DisplayItem.DrawText(new TextRun(lines[index], frame, font, TextAlignment.Left, Paint.Solid(color),
                MacTextMetrics.FirstBaseline(frame, font.Size))));
            top += height;
        }
        return (builder.ToList(), box);
    }
}
