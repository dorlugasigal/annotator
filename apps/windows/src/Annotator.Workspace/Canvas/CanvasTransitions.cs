using Annotator.Core;
using Annotator.Core.Motion;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// The ink-to-shape morph after Smart Draw recognition (spec 009): over 220 ms the stroke's 96 resampled points move
/// onto the shape's outline, then the real shape fades in. It changes drawing only; the document already holds the
/// shape. Immutable.
/// </summary>
public sealed class SnapMorph
{
    public const double Duration = MotionTiming.SnapMorphDuration;

    internal SnapMorph(Element shape, Element ink, ValueList<Point> from, ValueList<Point>? to, Element? preview, ulong generation,
                       System.Collections.Immutable.ImmutableHashSet<Guid> selection, Tool tool, Rect dirty)
    {
        Shape = shape;
        Ink = ink;
        From = from;
        To = to;
        Preview = preview;
        Generation = generation;
        Selection = selection;
        Tool = tool;
        Dirty = dirty;
    }

    /// <summary>The recognized shape, as stored in the document.</summary>
    public Element Shape { get; }

    /// <summary>The pen stroke it replaced.</summary>
    public Element Ink { get; }

    /// <summary>96 points along the stroke.</summary>
    public ValueList<Point> From { get; }

    /// <summary>96 points along the shape's outline, aligned with <see cref="From"/>; null when the shape has no simple outline.</summary>
    public ValueList<Point>? To { get; }

    /// <summary>The dashed candidate shown at release, which fades out first.</summary>
    public Element? Preview { get; }

    /// <summary>The area the morph draws in, in canvas points.</summary>
    public Rect Dirty { get; }

    internal ulong Generation { get; }

    internal System.Collections.Immutable.ImmutableHashSet<Guid> Selection { get; }

    internal Tool Tool { get; }

    /// <summary>What to draw at linear time <paramref name="time"/> (Mac drawSnapMorph). Allocates the moving line.</summary>
    public SnapMorphFrame FrameAt(double time)
    {
        double progress = MotionCurves.Enter.Progress(time);
        Element? dashed = null;
        if (Preview is { } preview && progress < 1)
        {
            Element candidate = preview;
            candidate.Style.Pattern = StrokePattern.Dashed;
            candidate.Style.Opacity = 0.75 * (1 - progress);
            dashed = candidate;
        }
        if (To is not { } to)
        {
            return new SnapMorphFrame(dashed, null, Ink, 1 - progress, Shape, progress);
        }
        // The real shape fades in over the last part so rough strokes, fills and arrowheads land without a pop.
        double reveal = MotionCurves.Smooth((time - 0.55) / 0.45);
        var points = new Point[Math.Min(From.Count, to.Count)];
        for (int index = 0; index < points.Length; index++)
        {
            points[index] = From[index] + (to[index] - From[index]) * progress;
        }
        var line = new Element(Tool.Pen, points: ValueList.Create<Point>(points), style: Shape.Style, id: Ink.Id);
        line.Style.Pattern = StrokePattern.Solid;
        line.Style.Roughness = 0;
        line.Style.Pressure = false;
        line.Style.Fill = null;
        line.Style.Opacity = Shape.Style.Opacity * (0.75 + 0.25 * progress) * (1 - reveal);
        return new SnapMorphFrame(dashed, line.Style.Opacity > 0.001 ? line : null, null, 0, Shape, reveal);
    }

    /// <summary>Mac DrawingCanvas.resample: <paramref name="count"/> points evenly spaced along the polyline, keeping both ends.</summary>
    public static ValueList<Point> Resample(ReadOnlySpan<Point> points, int count)
    {
        if (count < 2 || points.Length < 2)
        {
            return [];
        }
        var distances = new double[points.Length];
        for (int index = 1; index < points.Length; index++)
        {
            distances[index] = distances[index - 1] + points[index - 1].Distance(points[index]);
        }
        double total = distances[^1];
        if (!(total > 0) || !double.IsFinite(total))
        {
            return [];
        }
        var output = new Point[count];
        output[0] = points[0];
        int cursor = 1;
        for (int sample = 1; sample < count - 1; sample++)
        {
            double position = total * sample / (count - 1);
            while (cursor < points.Length - 1 && distances[cursor] < position)
            {
                cursor++;
            }
            double segment = distances[cursor] - distances[cursor - 1];
            double fraction = segment > 0 ? (position - distances[cursor - 1]) / segment : 0;
            output[sample] = points[cursor - 1] + (points[cursor] - points[cursor - 1]) * fraction;
        }
        output[count - 1] = points[^1];
        return ValueList.Create<Point>(output);
    }

    /// <summary>
    /// Mac DrawingCanvas.snapOutline: the shape's outline as <paramref name="count"/> points, for rectangles, diamonds,
    /// ellipses and arrows; null for other shapes.
    /// </summary>
    public static ValueList<Point>? SnapOutline(in Element element, int count)
    {
        Rect box = element.Frame;
        Point[] points;
        switch (element.Tool)
        {
            case Tool.Rectangle:
                points = [box.Origin, new Point(box.MaxX, box.Y), new Point(box.MaxX, box.MaxY), new Point(box.X, box.MaxY), box.Origin];
                break;
            case Tool.Diamond:
                points =
                [
                    new Point(box.Center.X, box.Y), new Point(box.MaxX, box.Center.Y), new Point(box.Center.X, box.MaxY),
                    new Point(box.X, box.Center.Y), new Point(box.Center.X, box.Y),
                ];
                break;
            case Tool.Ellipse:
                points = new Point[65];
                for (int index = 0; index <= 64; index++)
                {
                    double angle = index / 64.0 * 2 * Math.PI;
                    points[index] = new Point(box.Center.X + box.Width / 2 * Math.Cos(angle), box.Center.Y + box.Height / 2 * Math.Sin(angle));
                }
                break;
            case Tool.Arrow:
                if (element.Points.Count < 2)
                {
                    return null;
                }
                points = element.Points.ToArray();
                break;
            default:
                return null;
        }
        if (element.Rotation != 0)
        {
            for (int index = 0; index < points.Length; index++)
            {
                points[index] = points[index].Rotated(box.Center, element.Rotation);
            }
        }
        ValueList<Point> result = Resample(points, count);
        return result.Count == count ? result : null;
    }

    /// <summary>
    /// Mac DrawingCanvas.align: starts a closed outline at the point nearest the ink's first sample and runs it in the
    /// ink's direction; an open outline is reversed when that brings its ends nearer the ink's ends.
    /// </summary>
    public static ValueList<Point> Align(ValueList<Point> outline, ValueList<Point> ink, bool closed)
    {
        if (ink.IsEmpty || outline.Count < 3)
        {
            return outline;
        }
        Point first = ink[0];
        Point last = ink[^1];
        if (!closed)
        {
            bool keep = first.Distance(outline[0]) + last.Distance(outline[^1]) <= first.Distance(outline[^1]) + last.Distance(outline[0]);
            if (keep)
            {
                return outline;
            }
            Point[] reversed = outline.ToArray();
            Array.Reverse(reversed);
            return ValueList.Create<Point>(reversed);
        }
        static double Area(ReadOnlySpan<Point> points)
        {
            double total = 0;
            for (int index = 0; index < points.Length; index++)
            {
                Point a = points[index];
                Point b = points[(index + 1) % points.Length];
                total += a.X * b.Y - b.X * a.Y;
            }
            return total;
        }
        Point[] ring = outline.AsSpan()[..^1].ToArray();
        if ((Area(ring) < 0) != (Area(ink.AsSpan()) < 0))
        {
            Array.Reverse(ring);
        }
        int start = 0;
        for (int index = 1; index < ring.Length; index++)
        {
            if (ring[index].Distance(first) < ring[start].Distance(first))
            {
                start = index;
            }
        }
        var aligned = new Point[ring.Length + 1];
        for (int index = 0; index < ring.Length; index++)
        {
            aligned[index] = ring[(start + index) % ring.Length];
        }
        aligned[^1] = aligned[0];
        return ValueList.Create<Point>(aligned);
    }
}

/// <summary>
/// One frame of a <see cref="SnapMorph"/>: the fading dashed candidate, the moving line (or the ink fading out when the
/// shape has no outline), and the shape with its opacity. Draw each object inside an opacity layer of its alpha.
/// </summary>
public readonly record struct SnapMorphFrame(Element? Dashed, Element? Line, Element? Ink, double InkAlpha, Element Shape, double ShapeAlpha);

/// <summary>
/// Erased drawings fading after the eraser released (spec 009): from their faded strength to clear over 120 ms, shrinking
/// 3% unless Animation effects are off. Covers and pixel effects disappear at once. Immutable.
/// </summary>
public sealed class EraseFade
{
    public const double Duration = MotionTiming.EraseFadeDuration;

    internal EraseFade(ElementList elements, ulong generation, bool scales, Rect dirty)
    {
        Elements = elements;
        Generation = generation;
        Scales = scales;
        Dirty = dirty;
    }

    /// <summary>The erased drawings, as they were.</summary>
    public ElementList Elements { get; }

    /// <summary>False when Animation effects are off: the fade changes opacity only.</summary>
    public bool Scales { get; }

    /// <summary>The area the fade draws in, in canvas points.</summary>
    public Rect Dirty { get; }

    internal ulong Generation { get; }

    /// <summary>
    /// The opacity and the scale around each object's center at linear time <paramref name="time"/> (Mac drawEraseFade).
    /// Draw each object in an opacity layer.
    /// </summary>
    public (double Alpha, double Scale) FrameAt(double time) =>
        (0.22 * (1 - MotionCurves.Fade.Progress(time)),
         Scales ? 1 - 0.03 * MotionCurves.Exit.Progress(time) : 1);
}
