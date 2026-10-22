// Port of the drawing decisions in apps/macos/Sources/PresentationRendering/CursorHighlightRenderer.swift and SpotlightRenderer.swift.
using Annotator.Core.Geometry;

namespace Annotator.Core.Scene;

/// <summary>
/// One frame of the cursor halo, as Swift <c>CursorHighlightRenderer.draw</c> decides it. Draw in a layer at
/// <see cref="Opacity"/> under <see cref="Transform"/>: the shape (filled for the disc, otherwise stroked) with a
/// glow shadow of <see cref="GlowRadius"/> in <see cref="Color"/> at alpha 0.75 and zero offset, then the ripple
/// without shadow at <see cref="RippleOpacity"/> inside the same layer.
/// </summary>
public sealed class CursorHighlightFrame
{
    /// <summary>
    /// Windows: the glow's Gaussian standard deviation per point of glow radius. Core Graphics blurs shadows in device
    /// pixels with a deviation of about half the blur, and the Mac draws at 2x: a quarter of the radius in points.
    /// </summary>
    public const double GlowDeviationPerRadius = 0.25;

    private CursorHighlightFrame(AffineTransform transform, double opacity, RGBA color, PathData shape, bool filled,
                                 StrokeStyle stroke, double glowRadius, PathData? ripple, double rippleOpacity, StrokeStyle? rippleStroke)
    {
        Transform = transform;
        Opacity = opacity;
        Color = color;
        Shape = shape;
        Filled = filled;
        Stroke = stroke;
        GlowRadius = glowRadius;
        Ripple = ripple;
        RippleOpacity = rippleOpacity;
        RippleStroke = rippleStroke;
    }

    /// <summary>Translation to the pointer, then the turn rotation, then the pulse or turn scale.</summary>
    public AffineTransform Transform { get; }

    /// <summary>The layer alpha: the color's alpha, applied once to everything the halo draws.</summary>
    public double Opacity { get; }

    /// <summary>The opaque halo color.</summary>
    public RGBA Color { get; }

    /// <summary>The shape around the origin, sized by the diameter.</summary>
    public PathData Shape { get; }

    public bool Filled { get; }

    /// <summary>Round caps and joins, the border width (2 points wider while a reduced-motion click shows) and its dashes.</summary>
    public StrokeStyle Stroke { get; }

    /// <summary>The shadow blur radius in points; 0 draws no glow.</summary>
    public double GlowRadius { get; }

    /// <summary>The click ripple, always stroked; null when no ripple shows.</summary>
    public PathData? Ripple { get; }

    public double RippleOpacity { get; }

    public StrokeStyle? RippleStroke { get; }

    /// <summary>
    /// Windows: the area this frame can paint, in points, with room for antialiasing: the transformed shape with half its
    /// stroke (scaled like the shape) and the glow, which Core Graphics blurs in device space so the click's scale does not
    /// grow it, and the ripple with half its stroke. The glow's Gaussian reaches three deviations
    /// (<see cref="GlowDeviationPerRadius"/>).
    /// </summary>
    public Rect PaintBounds()
    {
        double scale = Math.Sqrt(Transform.A * Transform.A + Transform.B * Transform.B);
        Rect shape = Shape.Bounds is { } local ? Transform.TransformBounds(local) : new Rect(Transform.Tx, Transform.Ty, 0, 0);
        double reach = (Filled ? 0 : Stroke.Width * scale / 2) + GlowRadius * GlowDeviationPerRadius * 3 + 1;
        Rect bounds = RectMath.Outset(shape, reach, reach);
        if (Ripple?.Bounds is { } ripple && RippleStroke is { } stroke)
        {
            double rippleReach = stroke.Width * scale / 2 + 1;
            bounds = RectMath.Union(bounds, RectMath.Outset(Transform.TransformBounds(ripple), rippleReach, rippleReach));
        }
        return bounds;
    }

    public static CursorHighlightFrame Create(in CursorHighlightSettings settings, Point center, double? clickProgress,
                                              Point? clickCenter = null, bool reduceMotion = false)
    {
        var effect = settings.ResolvedClickEffect;
        double? progress = clickProgress is { } raw ? SwiftMath.Min(1, SwiftMath.Max(0, raw)) : null;
        double scale = 1, rotation = 0;
        if (progress is { } value && effect != CursorClickEffect.Off && !reduceMotion)
        {
            switch (effect)
            {
                case CursorClickEffect.Pulse:
                    scale = CursorHighlightGeometry.PulseScale(value);
                    break;
                case CursorClickEffect.Turn:
                    scale = 1 - 0.1 * Math.Sin(Math.PI * value);
                    rotation = (1 - Math.Pow(1 - value, 3)) * (settings.Shape == CursorHighlightShape.Hexagon ? Math.PI / 3 : Math.PI / 2);
                    break;
            }
        }
        var transform = AffineTransform.Translation(center.X, center.Y).Rotated(rotation).Scaled(scale, scale);
        var color = new RGBA(settings.Color.Red, settings.Color.Green, settings.Color.Blue);
        var width = settings.BorderWidth + (reduceMotion && progress is not null && effect != CursorClickEffect.Off ? 2 : 0);
        ValueList<double> dashes = settings.ResolvedBorder switch
        {
            StrokePattern.Dashed => [width * 3, width * 2],
            StrokePattern.Dotted => [0.01, width * 2.5],
            _ => default,
        };
        var stroke = new StrokeStyle(width, LineCap.Round, LineJoin.Round, 10, dashes);
        var radius = settings.Diameter / 2;
        var frame = new Rect(-radius, -radius, settings.Diameter, settings.Diameter);
        var shape = CursorHighlightGeometry.Path(settings.Shape, frame);
        PathData? ripple = null;
        double rippleOpacity = 0;
        StrokeStyle? rippleStroke = null;
        if (progress is { } click && effect == CursorClickEffect.Ripple && !reduceMotion)
        {
            var expansion = settings.Diameter * 0.28 * (1 - Math.Pow(1 - click, 3));
            rippleOpacity = Math.Pow(1 - click, 2) * 0.75;
            rippleStroke = new StrokeStyle(SwiftMath.Max(1, width * (1 - click * 0.5)), LineCap.Round, LineJoin.Round);
            var origin = clickCenter ?? center;
            var area = RectMath.Outset(frame, expansion, expansion).Translated(new Point(origin.X - center.X, origin.Y - center.Y));
            ripple = CursorHighlightGeometry.Path(settings.Shape, area);
        }
        return new CursorHighlightFrame(transform, settings.Color.Alpha, color, shape, settings.Shape == CursorHighlightShape.Disc,
                                        stroke, settings.ResolvedGlow, ripple, rippleOpacity, rippleStroke);
    }
}

/// <summary>
/// The spotlight dimming, as Swift <c>SpotlightRenderer.draw</c> paints it: inside <see cref="Clip"/>, a radial
/// gradient around <see cref="Center"/> from transparent black at <see cref="InnerRadius"/> to black at alpha 0.62 at
/// <see cref="OuterRadius"/>, extended on both sides, drawn at <see cref="Opacity"/>.
/// </summary>
public readonly record struct SpotlightPaint(Rect Clip, Point Center, double InnerRadius, double OuterRadius, double Opacity)
{
    public const double EdgeAlpha = 0.62;

    /// <summary>The black alpha at a distance from the center (linear interpolation of the two stops).</summary>
    public double AlphaAt(double distance)
    {
        var t = (distance - InnerRadius) / (OuterRadius - InnerRadius);
        return Opacity * EdgeAlpha * Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// The paint for one frame; null when <paramref name="opacity"/> is 0 or less (nothing is drawn).
    /// </summary>
    /// <exception cref="RenderFailure">The opacity is not finite or the radius is not finite and positive
    /// (<see cref="RenderFailureKind.ContextUnavailable"/>, as on the Mac).</exception>
    public static SpotlightPaint? Create(Rect bounds, Point center, double radius, double opacity = 1)
    {
        if (!double.IsFinite(opacity))
        {
            throw new RenderFailure(RenderFailureKind.ContextUnavailable);
        }
        if (!(opacity > 0))
        {
            return null;
        }
        if (!double.IsFinite(radius) || !(radius > 0))
        {
            throw new RenderFailure(RenderFailureKind.ContextUnavailable);
        }
        var feather = SwiftMath.Min(64, SwiftMath.Max(12, radius * 0.25));
        return new SpotlightPaint(bounds, center, SwiftMath.Max(0, radius - feather), radius + feather, SwiftMath.Min(1, opacity));
    }
}
