using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Core.Tests;

/// <summary>A deterministic stand-in for DirectWrite: 0.55 em per character and 1.2 em per line.</summary>
internal sealed class FakeTextMeasurer : ITextMeasurer
{
    public int MeasureCalls { get; private set; }

    public Size Measure(string text, TextFont font)
    {
        MeasureCalls++;
        var lines = text.Split('\n');
        var longest = 0;
        foreach (var line in lines)
        {
            longest = Math.Max(longest, line.Length);
        }
        var width = longest * font.Size * (font.Design == FontDesign.Monospace ? 0.6 : 0.55) * (font.Weight == FontWeight.Semibold ? 1.05 : 1);
        return new Size(width, lines.Length * font.Size * 1.2);
    }

    public FontLineMetrics LineMetrics(TextFont font) => new(font.Size * 0.95, font.Size * 0.25, 0);
}

internal static class SceneTestSupport
{
    public static SceneBuilder Builder() => new(new FakeTextMeasurer());

    public static DisplayList ListOf(SceneSnapshot scene, Guid id)
    {
        foreach (var list in scene.Elements)
        {
            if (list.ElementId == id)
            {
                return list;
            }
        }
        throw new InvalidOperationException($"The scene has no list for {id}.");
    }

    public static DisplayItem[] Items(DisplayList list, DisplayOp op) => list.Items.ToArray().Where(item => item.Op == op).ToArray();

    public static void AssertSameLists(DisplayList expected, DisplayList actual) =>
        Assert.True(expected.ContentEquals(actual), $"Expected:\n{expected}\nActual:\n{actual}");

    public static void AssertSameScenes(SceneSnapshot expected, SceneSnapshot actual)
    {
        Assert.Equal(expected.Elements.Length, actual.Elements.Length);
        for (var index = 0; index < expected.Elements.Length; index++)
        {
            AssertSameLists(expected.Elements[index], actual.Elements[index]);
        }
        AssertSameLists(expected.Selection, actual.Selection);
    }

    /// <summary>Points along every segment, 64 per line or curve, like the Swift ellipse sampler.</summary>
    public static List<Point> Samples(PathData path, int perSegment = 64)
    {
        var samples = new List<Point>();
        var current = Point.Zero;
        var first = Point.Zero;
        foreach (var element in path)
        {
            var points = element.Points;
            switch (element.Verb)
            {
                case PathVerb.MoveTo:
                    current = first = points[0];
                    samples.Add(current);
                    break;
                case PathVerb.CubicTo:
                    for (var index = 1; index <= perSegment; index++)
                    {
                        double t = (double)index / perSegment, u = 1 - t;
                        samples.Add(current * (u * u * u) + points[0] * (3 * u * u * t) + points[1] * (3 * u * t * t) + points[2] * (t * t * t));
                    }
                    current = points[2];
                    break;
                case PathVerb.QuadTo:
                    for (var index = 1; index <= perSegment; index++)
                    {
                        double t = (double)index / perSegment, u = 1 - t;
                        samples.Add(current * (u * u) + points[0] * (2 * u * t) + points[1] * (t * t));
                    }
                    current = points[1];
                    break;
                default:
                    var end = element.Verb == PathVerb.Close ? first : points[0];
                    for (var index = 1; index <= perSegment; index++)
                    {
                        samples.Add(current + (end - current) * ((double)index / perSegment));
                    }
                    current = end;
                    break;
            }
        }
        return samples;
    }

    /// <summary>The distance from a point to the drawn outline of a path.</summary>
    public static double DistanceToPath(PathData path, Point point)
    {
        var samples = Samples(path, 256);
        var best = double.PositiveInfinity;
        for (var index = 1; index < samples.Count; index++)
        {
            best = Math.Min(best, HitTesting.Distance(point, samples[index - 1], samples[index]));
        }
        return samples.Count == 1 ? point.Distance(samples[0]) : best;
    }

    /// <summary>Whether an item paints at <paramref name="point"/> (fills contain it; strokes pass within half their width plus a tolerance).</summary>
    public static bool Paints(in DisplayItem item, Point point, double tolerance = 0.5) => item.Op switch
    {
        DisplayOp.FillPath => item.Path!.Contains(point, item.FillRule) || DistanceToPath(item.Path, point) <= tolerance,
        DisplayOp.StrokePath => DistanceToPath(item.Path!, point) <= item.Stroke!.Width / 2 + tolerance,
        DisplayOp.FillRect => item.Rect.Contains(point, tolerance),
        _ => false,
    };

    /// <summary>Precomputed outlines of drawing items, for probing many points quickly.</summary>
    public sealed class PaintProbe
    {
        private readonly List<(DisplayItem Item, List<Point>? Outline)> parts = [];

        public PaintProbe(IEnumerable<DisplayItem> items)
        {
            foreach (var item in items)
            {
                parts.Add((item, item.Path is { } path ? Samples(path, 64) : null));
            }
        }

        public bool Paints(Point point, double tolerance)
        {
            foreach (var (item, outline) in parts)
            {
                switch (item.Op)
                {
                    case DisplayOp.FillPath when item.Path!.Contains(point, item.FillRule):
                        return true;
                    case DisplayOp.StrokePath when Near(outline!, point, item.Stroke!.Width / 2 + tolerance):
                        return true;
                    case DisplayOp.FillRect when item.Rect.Contains(point, tolerance):
                        return true;
                }
            }
            return false;
        }

        private static bool Near(List<Point> outline, Point point, double distance)
        {
            for (var index = 1; index < outline.Count; index++)
            {
                if (HitTesting.Distance(point, outline[index - 1], outline[index]) <= distance)
                {
                    return true;
                }
            }
            return false;
        }
    }
}