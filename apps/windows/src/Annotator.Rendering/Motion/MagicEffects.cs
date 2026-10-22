using System.Numerics;
using Annotator.Workspace.Motion;
using Windows.UI;
using Windows.UI.Composition;

namespace Annotator.Rendering.Motion;

/// <summary>
/// Magic's light effects with compositor animations: the sparks that follow each shape's flight (Mac MagicTrail) and the
/// toolbar's blue point, glow and spark burst (Mac MagicToolbarEffect). Every effect shares its layer's timeline and
/// vanishes at landing; nothing runs on the overlay thread while they play.
/// </summary>
public static class MagicEffects
{
    private static readonly Color Blue = Color.FromArgb(255, 64, 179, 255);
    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    /// <summary>
    /// The toolbar shrinks into a blue point, which bursts into twelve sparks (Mac MagicToolbarEffect); showing plays it
    /// backwards. Poses are sampled 61 times, linear between them, as on the Mac.
    /// </summary>
    public static void PlayPoof(VisibilityStage stage, StageImage image, PoofPlan poof, double shift)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(poof);
        Compositor compositor = stage.Compositor;
        var delay = poof.Started - shift;
        var duration = poof.Duration;
        const int samples = MagicToolbarPoof.Samples;
        var poses = new MagicToolbarPoof.Pose[samples + 1];
        for (var step = 0; step <= samples; step++)
        {
            poses[step] = MagicToolbarPoof.PoseAt(poof.From + (poof.To - poof.From) * MagicToolbarPoof.SmoothStep(0, 1, (double)step / samples));
        }
        var scales = new Vector3[samples + 1];
        var imageOpacity = new double[samples + 1];
        var glowOpacity = new double[samples + 1];
        var sparkOpacity = new double[samples + 1];
        for (var step = 0; step <= samples; step++)
        {
            scales[step] = new Vector3((float)poses[step].Scale, (float)poses[step].Scale, 1);
            imageOpacity[step] = poses[step].ImageOpacity;
            glowOpacity[step] = poses[step].GlowOpacity;
            sparkOpacity[step] = poses[step].SparkOpacity;
        }
        SpriteVisual visual = image.Image;
        visual.Scale = scales[0];
        visual.Opacity = (float)imageOpacity[0];
        visual.Offset = new Vector3(image.Pixels.Left, image.Pixels.Top, 0);
        visual.RotationAngle = 0;
        using (var scale = stage.Animations.Sampled(scales, delay, duration))
        {
            visual.StartAnimation("Scale", scale);
        }
        using (var opacity = stage.Animations.Sampled(imageOpacity, delay, duration))
        {
            visual.StartAnimation("Opacity", opacity);
        }
        var center = new Vector3(image.Pixels.Left + image.Pixels.Width / 2f, image.Pixels.Top + image.Pixels.Height / 2f, 0);
        var points = (float)stage.Scale;

        // One blue glow, 36 points across: white at its center, blue at 80% by 22% of its radius, clear at the edge.
        var glowSize = 36 * points;
        CompositionRadialGradientBrush gradient = compositor.CreateRadialGradientBrush();
        gradient.EllipseCenter = new Vector2(0.5f, 0.5f);
        gradient.EllipseRadius = new Vector2(0.5f, 0.5f);
        gradient.MappingMode = CompositionMappingMode.Relative;
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, White));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0.22f, Color.FromArgb(204, Blue.R, Blue.G, Blue.B)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, Blue.R, Blue.G, Blue.B)));
        SpriteVisual glow = compositor.CreateSpriteVisual();
        glow.Brush = gradient;
        glow.Size = new Vector2(glowSize, glowSize);
        glow.Offset = center - new Vector3(glowSize / 2, glowSize / 2, 0);
        glow.Opacity = (float)glowOpacity[0];
        stage.AddAbove(image, glow);
        stage.Keep(image, gradient);
        using (var fade = stage.Animations.Sampled(glowOpacity, delay, duration))
        {
            glow.StartAnimation("Opacity", fade);
        }

        // Twelve sparks of 2.5 points fly out from the center as the point bursts.
        var positions = new Vector3[samples + 1];
        var sparkSize = 2.5f * points;
        for (var index = 0; index < MagicToolbarPoof.SparkCount; index++)
        {
            for (var step = 0; step <= samples; step++)
            {
                MotionPoint offset = MagicToolbarPoof.SparkOffset(index, poses[step].Radius);
                // The Mac's offsets are y-up; the screen's y points down.
                positions[step] = center + new Vector3((float)(offset.X * points) - sparkSize / 2, (float)(-offset.Y * points) - sparkSize / 2, 0);
            }
            ShapeVisual spark = Dot(compositor, sparkSize, MagicToolbarPoof.SparkIsWhite(index) ? White : Blue, stage, image);
            spark.Offset = positions[0];
            spark.Opacity = (float)sparkOpacity[0];
            stage.AddAbove(image, spark);
            using (var move = stage.Animations.Sampled(positions, delay, duration))
            {
                spark.StartAnimation("Offset", move);
            }
            using var fade = stage.Animations.Sampled(sparkOpacity, delay, duration);
            spark.StartAnimation("Opacity", fade);
        }
    }

    /// <summary>
    /// The six warm and white sparks that trail a Magic flight (Mac MagicTrail), below the shape, on the flight's
    /// timeline: position keyframes and a fade in, hold and fade out, all eased by the flight's curve.
    /// </summary>
    /// <param name="display">The shape's display, to place the trail's y-up points on screen.</param>
    public static void PlaySparks(VisibilityStage stage, StageImage image, MagicTrailGeometry trail, MotionDisplay display, LayerMotion motion,
                                  double shift)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(motion);
        Compositor compositor = stage.Compositor;
        var delay = motion.Started - shift;
        var duration = motion.EffectiveDuration;
        var scale = stage.Scale;
        var keyTimes = new double[trail.Points.Length];
        for (var index = 0; index < keyTimes.Length; index++)
        {
            keyTimes[index] = (double)index / (keyTimes.Length - 1);
        }
        var positions = new Vector3[keyTimes.Length];
        foreach (MagicSpark spark in trail.Sparks)
        {
            var size = (float)(spark.Diameter * scale);
            for (var index = 0; index < positions.Length; index++)
            {
                Annotator.Core.Point point = display.FromMotion(spark.Positions[index]);
                positions[index] = new Vector3((float)(point.X * scale) - size / 2, (float)(point.Y * scale) - size / 2, 0);
            }
            var color = Color.FromArgb(255, (byte)Math.Round(spark.Red * 255), (byte)Math.Round(spark.Green * 255), (byte)Math.Round(spark.Blue * 255));
            ShapeVisual dot = Dot(compositor, size, color, stage, image);
            dot.Offset = positions[0];
            dot.Opacity = 0;
            stage.AddBelow(image, dot);
            using (var move = stage.Animations.Path(keyTimes, positions, MagicFlight.TimingFunction, delay, duration))
            {
                dot.StartAnimation("Offset", move);
            }
            using var fade = stage.Animations.Values(MagicTrailGeometry.FadeTimes, MagicTrailGeometry.Fade(0.9), MagicFlight.TimingFunction, delay, duration);
            dot.StartAnimation("Opacity", fade);
        }
    }

    /// <summary>
    /// Magic's light trails (Mac MagicTrail): three round-capped strokes along the flight path whose head follows the image
    /// and whose tail trails at most 110 points (28%) behind, fading in and out on the flight's curve. The path is drawn as
    /// up to 32 straight pieces; expressions trim each piece from a shared head and tail, so the compositor moves the trail
    /// without work on the overlay thread.
    /// </summary>
    public static void PlayTrails(VisibilityStage stage, StageImage image, MagicTrailGeometry trail, MotionDisplay display, LayerMotion motion,
                                  double shift)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(trail);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(motion);
        Compositor compositor = stage.Compositor;
        var delay = motion.Started - shift;
        var duration = motion.EffectiveDuration;
        var scale = stage.Scale;
        var count = trail.Points.Length;
        var points = new Vector2[count];
        var keyTimes = new double[count];
        for (var index = 0; index < count; index++)
        {
            Annotator.Core.Point point = display.FromMotion(trail.Points[index]);
            points[index] = new Vector2((float)(point.X * scale), (float)(point.Y * scale));
            keyTimes[index] = (double)index / (count - 1);
        }
        var step = Math.Max(1, (count - 1 + 31) / 32);
        var corners = new List<int>();
        for (var index = 0; index < count - 1; index += step)
        {
            corners.Add(index);
        }
        corners.Add(count - 1);

        CompositionPropertySet progress = compositor.CreatePropertySet();
        progress.InsertScalar("Head", (float)trail.Ends[0]);
        progress.InsertScalar("Tail", (float)trail.Starts[0]);
        using (var head = stage.Animations.Values(keyTimes, trail.Ends, MagicFlight.TimingFunction, delay, duration))
        {
            progress.StartAnimation("Head", head);
        }
        using (var tail = stage.Animations.Values(keyTimes, trail.Starts, MagicFlight.TimingFunction, delay, duration))
        {
            progress.StartAnimation("Tail", tail);
        }
        stage.Keep(image, progress);
        using ExpressionAnimation end = compositor.CreateExpressionAnimation("Clamp((p.Head - a) / s, 0, 1)");
        using ExpressionAnimation start = compositor.CreateExpressionAnimation("Clamp((p.Tail - a) / s, 0, 1)");
        // A piece the trail has not reached or has left draws nothing (a zero-length round cap would leave a dot).
        using ExpressionAnimation thickness = compositor.CreateExpressionAnimation("(p.Head > a && p.Tail < a + s) ? w : 0");
        foreach (ExpressionAnimation expression in (ReadOnlySpan<ExpressionAnimation>)[end, start, thickness])
        {
            expression.SetReferenceParameter("p", progress);
        }
        foreach (MagicStroke stroke in MagicTrailGeometry.Strokes)
        {
            ShapeVisual visual = compositor.CreateShapeVisual();
            visual.RelativeSizeAdjustment = Vector2.One;
            visual.Opacity = 0;
            CompositionColorBrush brush = compositor.CreateColorBrush(Color.FromArgb(255, (byte)Math.Round(stroke.Red * 255),
                (byte)Math.Round(stroke.Green * 255), (byte)Math.Round(stroke.Blue * 255)));
            var width = (float)(stroke.Width * scale);
            thickness.SetScalarParameter("w", width);
            for (var piece = 0; piece < corners.Count - 1; piece++)
            {
                int from = corners[piece], to = corners[piece + 1];
                var a = (float)trail.Ends[from];
                var span = Math.Max(1e-6f, (float)trail.Ends[to] - a);
                CompositionLineGeometry line = compositor.CreateLineGeometry();
                line.Start = points[from];
                line.End = points[to];
                line.TrimEnd = 0;
                CompositionSpriteShape shape = compositor.CreateSpriteShape(line);
                shape.StrokeBrush = brush;
                shape.StrokeThickness = 0;
                shape.StrokeStartCap = CompositionStrokeCap.Round;
                shape.StrokeEndCap = CompositionStrokeCap.Round;
                shape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
                foreach (ExpressionAnimation expression in (ReadOnlySpan<ExpressionAnimation>)[end, start, thickness])
                {
                    expression.SetScalarParameter("a", a);
                    expression.SetScalarParameter("s", span);
                }
                line.StartAnimation("TrimEnd", end);
                line.StartAnimation("TrimStart", start);
                shape.StartAnimation("StrokeThickness", thickness);
                visual.Shapes.Add(shape);
            }
            using (var fade = stage.Animations.Values(MagicTrailGeometry.FadeTimes, MagicTrailGeometry.Fade(stroke.PeakOpacity),
                       MagicFlight.TimingFunction, delay, duration))
            {
                visual.StartAnimation("Opacity", fade);
            }
            stage.AddBelow(image, visual);
            stage.Keep(image, brush);
        }
    }

    /// <summary>A round dot of <paramref name="size"/> pixels, like a small CALayer with a corner radius of half its size.</summary>
    private static ShapeVisual Dot(Compositor compositor, float size, Color color, VisibilityStage stage, StageImage image)
    {
        CompositionEllipseGeometry geometry = compositor.CreateEllipseGeometry();
        geometry.Center = new Vector2(size / 2, size / 2);
        geometry.Radius = new Vector2(size / 2, size / 2);
        CompositionColorBrush fill = compositor.CreateColorBrush(color);
        CompositionSpriteShape shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = fill;
        ShapeVisual visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(size, size);
        visual.Shapes.Add(shape);
        stage.Keep(image, shape);
        stage.Keep(image, fill);
        stage.Keep(image, geometry);
        return visual;
    }
}
