// Port of apps/macos/Sources/PresentationRendering/LaserTrailRenderer.swift.
using Annotator.Core.Geometry;

namespace Annotator.Core.Scene;

/// <summary>
/// Draws the laser pointer's trail (<see cref="LaserTrail"/>) as display items. Each layer is one filled outline of circles
/// around the trail's dots and the bands between them, all wound the same way and filled with the nonzero rule, so the
/// parts merge and a trail that crosses itself is no darker.
/// </summary>
public static class LaserTrailRenderer
{
    /// <summary>The trail as drawn at <paramref name="now"/>, as one display list; null when nothing shows.</summary>
    public static DisplayList? List(in LaserTrail trail, double now)
    {
        var builder = new DisplayListBuilder();
        Draw(trail, now, builder);
        return builder.Count == 0 ? null : builder.ToList();
    }

    /// <summary>Adds every stroke of the trail as drawn at <paramref name="now"/>, oldest first.</summary>
    public static void Draw(in LaserTrail trail, double now, DisplayListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (ref readonly LaserTrail.Shape shape in trail.Shapes(now))
        {
            Draw(shape, builder);
        }
    }

    /// <summary>
    /// Adds one stroke: for each layer, its outline filled with the layer's color (white for the core) at the color's alpha
    /// times the layer's alpha times the stroke's opacity.
    /// </summary>
    public static void Draw(in LaserTrail.Shape shape, DisplayListBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!(shape.Opacity > 0))
        {
            return;
        }
        LaserTrail.Dot[] dots = Simplify(shape.Dots.AsSpan());
        foreach (ref readonly LaserTrail.Pass pass in LaserTrail.Passes)
        {
            PathData path = Outline(dots, pass);
            if (path.IsEmpty)
            {
                continue;
            }
            RGBA color = pass.White ? new RGBA(1, 1, 1) : shape.Color;
            builder.Add(DisplayItem.FillPath(path, FillRule.NonZero,
                Paint.Solid(new RGBA(color.Red, color.Green, color.Blue, color.Alpha * pass.Alpha * shape.Opacity))));
        }
    }

    /// <summary>
    /// How far, in points, a dot's circle may move when <see cref="Simplify"/> leaves it out: well under the drawing's
    /// own flattening tolerance at any display scale.
    /// </summary>
    public const double SimplifyTolerance = 0.05;

    /// <summary>
    /// The dots worth drawing, in order. A fast drag leaves a dot every pixel or two, and each one is a circle and a band in
    /// six layers, so drawing them all on every frame left too little time to read the pointer. A dot within
    /// <see cref="SimplifyTolerance"/> of the line from the dot before to the dot after (its center and its widest layer's
    /// radius) is drawn by that line already and is left out. The ends, every empty dot and the dots next to one stay,
    /// because a layer's radius jumps where a dot's width starts.
    /// </summary>
    public static LaserTrail.Dot[] Simplify(ReadOnlySpan<LaserTrail.Dot> dots)
    {
        if (dots.Length < 3)
        {
            return dots.ToArray();
        }
        double widest = 0;
        foreach (ref readonly LaserTrail.Pass pass in LaserTrail.Passes)
        {
            widest = Math.Max(widest, pass.Scale);
        }
        var keep = new bool[dots.Length];
        keep[0] = keep[^1] = true;
        for (int index = 0; index < dots.Length; index++)
        {
            if (!(dots[index].Radius > 0))
            {
                keep[index] = true;
                keep[Math.Max(0, index - 1)] = true;
                keep[Math.Min(dots.Length - 1, index + 1)] = true;
            }
        }
        var spans = new Stack<(int First, int Last)>();
        int start = 0;
        for (int index = 1; index < dots.Length; index++)
        {
            if (keep[index])
            {
                if (index - start > 1)
                {
                    spans.Push((start, index));
                }
                start = index;
            }
        }
        while (spans.TryPop(out (int First, int Last) span))
        {
            LaserTrail.Dot first = dots[span.First], last = dots[span.Last];
            Point along = last.Center - first.Center;
            double length = along.X * along.X + along.Y * along.Y;
            double farthest = SimplifyTolerance;
            int split = -1;
            for (int index = span.First + 1; index < span.Last; index++)
            {
                LaserTrail.Dot dot = dots[index];
                Point offset = dot.Center - first.Center;
                double share = length > 0 ? Math.Clamp((offset.X * along.X + offset.Y * along.Y) / length, 0, 1) : 0;
                double error = dot.Center.Distance(first.Center + along * share)
                    + widest * Math.Abs(dot.Radius - (first.Radius + (last.Radius - first.Radius) * share));
                if (error > farthest)
                {
                    farthest = error;
                    split = index;
                }
            }
            if (split < 0)
            {
                continue;
            }
            keep[split] = true;
            if (split - span.First > 1)
            {
                spans.Push((span.First, split));
            }
            if (span.Last - split > 1)
            {
                spans.Push((split, span.Last));
            }
        }
        var kept = new List<LaserTrail.Dot>(dots.Length);
        for (int index = 0; index < dots.Length; index++)
        {
            if (keep[index])
            {
                kept.Add(dots[index]);
            }
        }
        return [.. kept];
    }

    /// <summary>One layer's outline: a circle around each dot and a band to the next dot, counterclockwise with y up.</summary>
    public static PathData Outline(ReadOnlySpan<LaserTrail.Dot> dots, LaserTrail.Pass pass)
    {
        var path = new PathBuilder(dots.Length * 8, dots.Length * 18);
        for (int index = 0; index < dots.Length; index++)
        {
            LaserTrail.Dot dot = dots[index];
            double radius = pass.Radius(dot);
            if (radius > 0)
            {
                path.MoveTo(new Point(dot.Center.X + radius, dot.Center.Y));
                path.AddArc(dot.Center, radius, 0, Math.PI * 2, clockwise: false);
                path.Close();
            }
            if (index > 0 && LaserTrail.Band(dots[index - 1], dot, pass) is { } band)
            {
                path.AddLines(band.AsSpan());
                path.Close();
            }
        }
        return path.ToPathData();
    }
}
