using Annotator.Core.Geometry;

namespace Annotator.Core.Scene;

/// <summary>What a <see cref="Paint"/> draws with.</summary>
public enum PaintKind : byte
{
    /// <summary>One sRGB color (<see cref="Paint.Color"/>, straight alpha).</summary>
    Solid,

    /// <summary>A linear gradient (<see cref="Paint.Gradient"/>).</summary>
    LinearGradient,

    /// <summary>A platform color resolved by the renderer at draw time (<see cref="Paint.SystemColor"/>).</summary>
    System,
}

/// <summary>Appearance-dependent colors the Mac takes from AppKit; the renderer resolves them for the current theme.</summary>
public enum SystemColor : byte
{
    /// <summary><c>NSColor.controlAccentColor</c>: selection outlines and handles.</summary>
    Accent,

    /// <summary><c>NSColor.windowBackgroundColor</c>: the ruler label's background.</summary>
    WindowBackground,

    /// <summary><c>NSColor.systemRed</c>: the outline of an image that cannot be shown.</summary>
    Red,
}

/// <summary>A gradient stop: an opaque sRGB color at a position from 0 to 1.</summary>
public readonly record struct GradientStop(double Position, RGBA Color);

/// <summary>
/// A linear gradient in the drawing's user space, as Swift <c>GradientPainter</c> draws it: stops interpolate in
/// gamma-encoded sRGB (Direct2D <c>D2D1_GAMMA_2_2</c>) and the end colors extend beyond the axis
/// (<c>D2D1_EXTEND_MODE_CLAMP</c>, Core Graphics <c>drawsBeforeStartLocation</c> and <c>drawsAfterEndLocation</c>).
/// </summary>
public sealed class LinearGradientPaint : IEquatable<LinearGradientPaint>
{
    public LinearGradientPaint(ValueList<GradientStop> stops, Point start, Point end)
    {
        Stops = stops;
        Start = start;
        End = end;
    }

    public ValueList<GradientStop> Stops { get; }

    public Point Start { get; }

    public Point End { get; }

    /// <summary>The gradient of <paramref name="gradient"/> stretched across <paramref name="bounds"/>.</summary>
    public static LinearGradientPaint Create(ColorGradient gradient, Rect bounds)
    {
        var colors = gradient.Colors.AsSpan();
        var stops = new GradientStop[colors.Length];
        for (var index = 0; index < colors.Length; index++)
        {
            stops[index] = new GradientStop(GradientGeometry.StopLocation(index, colors.Length), colors[index]);
        }
        var axis = GradientGeometry.Axis(gradient.Angle, bounds);
        return new LinearGradientPaint(ValueList.Create<GradientStop>(stops), axis.Start, axis.End);
    }

    public bool Equals(LinearGradientPaint? other) =>
        other is not null && (ReferenceEquals(this, other) || (Stops == other.Stops && Start == other.Start && End == other.End));

    public override bool Equals(object? obj) => Equals(obj as LinearGradientPaint);

    public override int GetHashCode() => HashCode.Combine(Stops, Start, End);
}

/// <summary>
/// An immutable brush. The renderer draws <see cref="Color"/> (or the gradient's or system color) with brush
/// opacity <see cref="Opacity"/>, which carries the Mac's context alpha. Equal paints compare equal, so a
/// renderer can share brushes by value or by reference.
/// </summary>
public sealed class Paint : IEquatable<Paint>
{
    private Paint(PaintKind kind, RGBA color, SystemColor systemColor, LinearGradientPaint? gradient, double opacity)
    {
        Kind = kind;
        Color = color;
        SystemColor = systemColor;
        Gradient = gradient;
        Opacity = opacity;
    }

    /// <summary>Opaque black: covers, and pixel effects when their pixels are not available.</summary>
    public static Paint Black { get; } = Solid(new RGBA(0, 0, 0));

    /// <summary>Opaque white: selection handle fills.</summary>
    public static Paint White { get; } = Solid(Palette.White);

    public static Paint Accent { get; } = System(SystemColor.Accent);

    public static Paint WindowBackground { get; } = System(SystemColor.WindowBackground);

    public static Paint Red { get; } = System(SystemColor.Red);

    public PaintKind Kind { get; }

    /// <summary>The solid color with straight alpha; unused for other kinds.</summary>
    public RGBA Color { get; }

    public SystemColor SystemColor { get; }

    public LinearGradientPaint? Gradient { get; }

    /// <summary>The brush opacity from 0 to 1.</summary>
    public double Opacity { get; }

    /// <summary>The alpha the paint reaches where it fully covers: color alpha times opacity (gradient stops are opaque).</summary>
    public double EffectiveAlpha => Kind == PaintKind.Solid ? Color.Alpha * Opacity : Opacity;

    public static Paint Solid(RGBA color, double opacity = 1) => new(PaintKind.Solid, color, default, null, opacity);

    public static Paint System(SystemColor color, double opacity = 1) => new(PaintKind.System, default, color, null, opacity);

    public static Paint Linear(LinearGradientPaint gradient, double opacity)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        return new Paint(PaintKind.LinearGradient, default, default, gradient, opacity);
    }

    public bool Equals(Paint? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (Kind == other.Kind && Color == other.Color && SystemColor == other.SystemColor
                && Equals(Gradient, other.Gradient) && Opacity == other.Opacity));

    public override bool Equals(object? obj) => Equals(obj as Paint);

    public override int GetHashCode() => HashCode.Combine(Kind, Color, SystemColor, Opacity);

    public override string ToString() => Kind switch
    {
        PaintKind.Solid => $"Solid({Color.Red}, {Color.Green}, {Color.Blue}, {Color.Alpha}) x {Opacity}",
        PaintKind.System => $"System({SystemColor}) x {Opacity}",
        _ => $"Linear({Gradient!.Start} -> {Gradient.End}, {Gradient.Stops.Count} stops) x {Opacity}",
    };
}

/// <summary>Line cap (Direct2D: FLAT, ROUND, SQUARE for start, end and dash caps).</summary>
public enum LineCap : byte
{
    Butt,
    Round,
    Square,
}

/// <summary>
/// Line join. Core Graphics' miter join turns into a bevel past the miter limit, which is Direct2D's
/// <c>D2D1_LINE_JOIN_MITER_OR_BEVEL</c>.
/// </summary>
public enum LineJoin : byte
{
    Miter,
    Round,
    Bevel,
}

/// <summary>
/// An immutable stroke style in user space. Dash lengths and phase are user-space lengths, as in Core Graphics;
/// Direct2D expects them in multiples of the stroke width, so the renderer divides by <see cref="Width"/>.
/// The miter limit has the same meaning in both (ratio 1 / sin(angle / 2)); Core Graphics' default is 10.
/// </summary>
public sealed class StrokeStyle : IEquatable<StrokeStyle>
{
    public StrokeStyle(double width, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter, double miterLimit = 10,
                       ValueList<double> dashes = default, double dashPhase = 0)
    {
        Width = width;
        Cap = cap;
        Join = join;
        MiterLimit = miterLimit;
        Dashes = dashes;
        DashPhase = dashPhase;
    }

    public double Width { get; }

    public LineCap Cap { get; }

    public LineJoin Join { get; }

    public double MiterLimit { get; }

    /// <summary>Alternating dash and gap lengths in user space; empty for a solid line.</summary>
    public ValueList<double> Dashes { get; }

    public double DashPhase { get; }

    public bool IsDashed => !Dashes.IsEmpty;

    /// <summary>The same style with a different width.</summary>
    public StrokeStyle WithWidth(double width) =>
        width == Width ? this : new StrokeStyle(width, Cap, Join, MiterLimit, Dashes, DashPhase);

    /// <summary>The same style without dashes.</summary>
    public StrokeStyle Solid() => IsDashed ? new StrokeStyle(Width, Cap, Join, MiterLimit) : this;

    public bool Equals(StrokeStyle? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (Width == other.Width && Cap == other.Cap && Join == other.Join && MiterLimit == other.MiterLimit
                && Dashes == other.Dashes && DashPhase == other.DashPhase));

    public override bool Equals(object? obj) => Equals(obj as StrokeStyle);

    public override int GetHashCode() => HashCode.Combine(Width, Cap, Join, MiterLimit, Dashes, DashPhase);

    public override string ToString() =>
        $"Stroke({Width}, {Cap}, {Join}, {MiterLimit}{(IsDashed ? ", dashes " + Dashes : "")})";
}
