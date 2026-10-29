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
        foreach (ref readonly LaserTrail.Pass pass in LaserTrail.Passes)
        {
            PathData path = Outline(shape.Dots, pass);
            if (path.IsEmpty)
            {
                continue;
            }
            RGBA color = pass.White ? new RGBA(1, 1, 1) : shape.Color;
            builder.Add(DisplayItem.FillPath(path, FillRule.NonZero,
                Paint.Solid(new RGBA(color.Red, color.Green, color.Blue, color.Alpha * pass.Alpha * shape.Opacity))));
        }
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
