using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Toolbar;

/// <summary>One piece of a glyph stroke: a straight line, or a cubic curve when it has control points.</summary>
public readonly record struct GlyphSegment(Point End, Point? Control1 = null, Point? Control2 = null)
{
    public static GlyphSegment Line(double x, double y) => new(new Point(x, y));

    public static GlyphSegment Curve(double x1, double y1, double x2, double y2, double x, double y) =>
        new(new Point(x, y), new Point(x1, y1), new Point(x2, y2));

    public bool IsCurve => Control1 is not null;
}

/// <summary>One glyph stroke: solid with round ends, or dotted.</summary>
public sealed record GlyphStroke(Point Start, GlyphSegment[] Segments, bool Dotted = false);

/// <summary>
/// The Roughness and Edges icons: hand-drawn strokes in sample points (0 to 36), drawn directly so both platforms
/// draw the same picture (Mac InspectorGlyph in NativeInspectorComponents.swift).
/// </summary>
public static class InspectorGlyph
{
    public const double Width = 1.5;
    public const double DotPhase = 1.5;

    /// <summary>A wavy line that gets looser: one smooth wave, a doubled wave, then three waves spread wider apart.</summary>
    public static GlyphStroke[] Roughness(double value)
    {
        var wave = new GlyphStroke(new Point(7, 21),
            [GlyphSegment.Curve(11, 11, 15, 11, 18, 18), GlyphSegment.Curve(21, 25, 25, 25, 29, 15)]);
        if (value <= 0)
        {
            return [wave];
        }
        if (value < 4)
        {
            return [wave, new GlyphStroke(new Point(8, 23),
                [GlyphSegment.Curve(12, 14, 16, 14, 19, 20), GlyphSegment.Curve(22, 26, 26, 24, 28, 17)])];
        }
        return
        [
            new GlyphStroke(new Point(6, 20), [GlyphSegment.Curve(10, 9, 15, 9, 18, 17), GlyphSegment.Curve(21, 25, 26, 26, 30, 14)]),
            new GlyphStroke(new Point(8, 24), [GlyphSegment.Curve(12, 14, 16, 15, 19, 21), GlyphSegment.Curve(22, 28, 26, 26, 29, 19)]),
            new GlyphStroke(new Point(7, 17), [GlyphSegment.Curve(11, 7, 14, 7, 17, 14), GlyphSegment.Curve(20, 21, 25, 22, 28, 11)]),
        ];
    }

    /// <summary>A dotted square with its top-left corner drawn solid, sharp or rounded.</summary>
    public static GlyphStroke[] Edges(bool rounded)
    {
        const double r = 6, k = r * 0.45;
        var start = new Point(9, 18);
        if (!rounded)
        {
            return
            [
                new GlyphStroke(start, [GlyphSegment.Line(9, 27), GlyphSegment.Line(27, 27), GlyphSegment.Line(27, 9), GlyphSegment.Line(18, 9)], Dotted: true),
                new GlyphStroke(start, [GlyphSegment.Line(9, 9), GlyphSegment.Line(18, 9)]),
            ];
        }
        return
        [
            new GlyphStroke(start,
            [
                GlyphSegment.Line(9, 27 - r), GlyphSegment.Curve(9, 27 - k, 9 + k, 27, 9 + r, 27),
                GlyphSegment.Line(27 - r, 27), GlyphSegment.Curve(27 - k, 27, 27, 27 - k, 27, 27 - r),
                GlyphSegment.Line(27, 9 + r), GlyphSegment.Curve(27, 9 + k, 27 - k, 9, 27 - r, 9),
                GlyphSegment.Line(18, 9),
            ], Dotted: true),
            new GlyphStroke(start, [GlyphSegment.Line(9, 9 + r), GlyphSegment.Curve(9, 9 + k, 9 + k, 9, 9 + r, 9), GlyphSegment.Line(18, 9)]),
        ];
    }

    /// <summary>The strokes as display items in <paramref name="ink"/>, as the Mac strokes them.</summary>
    public static DisplayItem[] Draw(GlyphStroke[] strokes, RGBA ink)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        Paint paint = Paint.Solid(ink);
        var solid = new StrokeStyle(Width, LineCap.Round, LineJoin.Round);
        var dotted = new StrokeStyle(Width, LineCap.Butt, LineJoin.Round, 10, [1.5, 2], DotPhase);
        var items = new DisplayItem[strokes.Length];
        for (int i = 0; i < strokes.Length; i++)
        {
            GlyphStroke stroke = strokes[i];
            var path = new PathBuilder(stroke.Segments.Length + 1, stroke.Segments.Length * 3 + 1).MoveTo(stroke.Start);
            foreach (GlyphSegment segment in stroke.Segments)
            {
                if (segment.IsCurve)
                {
                    path.CubicTo(segment.Control1!.Value, segment.Control2!.Value, segment.End);
                }
                else
                {
                    path.LineTo(segment.End);
                }
            }
            items[i] = DisplayItem.StrokePath(path.ToPathData(), stroke.Dotted ? dotted : solid, paint);
        }
        return items;
    }
}
