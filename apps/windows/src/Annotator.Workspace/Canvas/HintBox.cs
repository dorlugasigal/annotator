using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// The on-screen tip's box (Mac WorkspaceHint.draw and WorkspaceHintView): white text on black at 82% in a rounded box at
/// the canvas's top-left corner, below the Use apps button while drawing, with a ✕ that turns tips off while the canvas
/// takes clicks. Frames are in canvas points.
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

    public const double LineSize = 12;

    /// <summary>Mac WorkspaceHint.closeReserve: room after the text for the ✕ that turns tips off.</summary>
    public const double CloseReserve = 18;

    /// <summary>Mac WorkspaceHint.closeHalfSize: the ✕ spans 7 by 7 points.</summary>
    public const double CloseHalfSize = 3.5;

    /// <summary>Mac WorkspaceHint.closeHitSize: the square a click on the ✕ lands in.</summary>
    public const double CloseHitSize = 24;

    public const double CloseStrokeWidth = 1.5;

    /// <summary>NSFont.Weight.medium, OpenType weight 500.</summary>
    public const FontWeight Medium = (FontWeight)500;

    /// <summary>The box's fill: black at 82%.</summary>
    public static RGBA Fill { get; } = new(0, 0, 0, 0.82);

    /// <summary>The ✕'s color: white at 70%.</summary>
    public static RGBA CloseColor { get; } = new(1, 1, 1, 0.7);

    /// <summary>
    /// Mac refreshHint: the view at 16 points from the left, 50 from the top while the Use apps button shows and 14
    /// otherwise, at most 540 points wide and at least 80, and 130 high.
    /// </summary>
    public static Rect ViewFrame(double canvasWidth, bool useAppsShown) =>
        new(16, useAppsShown ? 50 : 14, Math.Min(MaximumWidth, Math.Max(80, canvasWidth - 32)), ViewHeight);

    /// <summary>Mac WorkspaceHint.closeCenter(in:): the ✕'s center, at the right of the first line.</summary>
    public static Point CloseCenter(Rect box) =>
        new(box.MaxX - HorizontalInset - CloseHalfSize, box.Y + VerticalInset + MacTextMetrics.LineHeight(LineSize) / 2);

    /// <summary>Mac WorkspaceHint.closeFrame(in:): where a click on the ✕ lands, and its accessibility frame.</summary>
    public static Rect CloseFrame(Rect box)
    {
        Point center = CloseCenter(box);
        return new Rect(center.X - CloseHitSize / 2, center.Y - CloseHitSize / 2, CloseHitSize, CloseHitSize);
    }

    /// <summary>
    /// The ✕'s accessibility frame as the Mac's button records it: one point outside <see cref="CloseFrame"/> on every side,
    /// like <see cref="CanvasHud.UseAppsAccessibleFrame"/>.
    /// </summary>
    public static Rect CloseAccessibleFrame(Rect box) => RectMath.Outset(CloseFrame(box), 1, 1);

    /// <summary>
    /// Mac WorkspaceHint.draw(_:in:origin:closable:) for the view <paramref name="view"/>: the box at the view's top-left
    /// corner, as wide as the widest line plus 18 points (and 18 more for the ✕ when <paramref name="closable"/>) and as high
    /// as the Mac's lines plus 14, and one 12-point medium white text run per line. Lines keep the Mac's TextKit line heights
    /// and first baselines (WIN-REND-03); their widths are measured on Windows (D-4).
    /// </summary>
    public static (DisplayList List, Rect Box) Build(string text, Rect view, ITextMeasurer measurer, bool closable)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measurer);
        string[] lines = text.Split('\n');
        double width = Math.Min(MaximumWidth, Math.Max(80, view.Width - 16));
        double reserve = closable ? CloseReserve : 0;
        var font = new TextFont(FontDesign.System, Medium, LineSize);
        double lineHeight = MacTextMetrics.LineHeight(LineSize);
        double textWidth = 0;
        foreach (string line in lines)
        {
            textWidth = Math.Max(textWidth, measurer.Measure(line, font).Width);
        }
        var box = new Rect(view.X, view.Y, Math.Min(width, Math.Ceiling(textWidth) + 2 * HorizontalInset + reserve),
            Math.Ceiling(lineHeight * lines.Length) + 2 * VerticalInset);
        PathData shape = new PathBuilder(4, 9).AddRoundedRect(box, CornerRadius, CornerRadius).ToPathData();
        var builder = new DisplayListBuilder().Add(DisplayItem.FillPath(shape, FillRule.NonZero, Paint.Solid(Fill)));
        double top = box.Y + VerticalInset;
        double inner = box.Width - 2 * HorizontalInset - reserve;
        foreach (string line in lines)
        {
            var frame = new Rect(box.X + HorizontalInset, top, inner, lineHeight);
            builder.Add(DisplayItem.DrawText(new TextRun(line, frame, font, TextAlignment.Left, Paint.Solid(new RGBA(1, 1, 1)),
                MacTextMetrics.FirstBaseline(frame, LineSize))));
            top += lineHeight;
        }
        if (closable)
        {
            Point center = CloseCenter(box);
            PathData cross = new PathBuilder(4, 4)
                .MoveTo(new Point(center.X - CloseHalfSize, center.Y - CloseHalfSize))
                .LineTo(new Point(center.X + CloseHalfSize, center.Y + CloseHalfSize))
                .MoveTo(new Point(center.X + CloseHalfSize, center.Y - CloseHalfSize))
                .LineTo(new Point(center.X - CloseHalfSize, center.Y + CloseHalfSize))
                .ToPathData();
            builder.Add(DisplayItem.StrokePath(cross, new StrokeStyle(CloseStrokeWidth, LineCap.Round, LineJoin.Round), Paint.Solid(CloseColor)));
        }
        return (builder.ToList(), box);
    }
}
