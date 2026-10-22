// Port of apps/macos/Sources/AnnotationCore/CursorHighlightSettings.swift.
// Swift nests Shape, Visibility and ClickEffect inside the struct; C# would clash with the property
// names, so they are top-level enums with the same raw values.
namespace Annotator.Core;

/// <summary>Swift `CursorHighlightSettings.Shape`.</summary>
public enum CursorHighlightShape { Ring, Disc, RoundedSquare, Square, Diamond, Hexagon, Crosshair }

/// <summary>Swift `CursorHighlightSettings.Visibility`.</summary>
public enum CursorHighlightVisibility { Always, Moving, Clicks }

/// <summary>Swift `CursorHighlightSettings.ClickEffect`.</summary>
public enum CursorClickEffect { Off, Pulse, Ripple, Turn }

public static class CursorHighlightShapeExtensions
{
    internal static readonly RawValueMap<CursorHighlightShape> RawValues =
        new("ring", "disc", "roundedSquare", "square", "diamond", "hexagon", "crosshair");

    extension(CursorHighlightShape shape)
    {
        public string RawValue => RawValues.RawValue(shape);

        public string Label => shape switch
        {
            CursorHighlightShape.Ring => "Ring",
            CursorHighlightShape.Disc => "Disc",
            CursorHighlightShape.RoundedSquare => "Squircle",
            CursorHighlightShape.Square => "Square",
            CursorHighlightShape.Diamond => "Diamond",
            CursorHighlightShape.Hexagon => "Hexagon",
            CursorHighlightShape.Crosshair => "Crosshair",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    extension(CursorHighlightShape)
    {
        public static ValueList<CursorHighlightShape> AllCases => RawValues.AllCases;

        public static CursorHighlightShape? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class CursorHighlightVisibilityExtensions
{
    internal static readonly RawValueMap<CursorHighlightVisibility> RawValues = new("always", "moving", "clicks");

    extension(CursorHighlightVisibility visibility)
    {
        public string RawValue => RawValues.RawValue(visibility);
    }

    extension(CursorHighlightVisibility)
    {
        public static ValueList<CursorHighlightVisibility> AllCases => RawValues.AllCases;

        public static CursorHighlightVisibility? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class CursorClickEffectExtensions
{
    internal static readonly RawValueMap<CursorClickEffect> RawValues = new("off", "pulse", "ripple", "turn");

    extension(CursorClickEffect effect)
    {
        public string RawValue => RawValues.RawValue(effect);

        /// <summary>Swift `rawValue.capitalized`.</summary>
        public string Label => effect switch
        {
            CursorClickEffect.Off => "Off",
            CursorClickEffect.Pulse => "Pulse",
            CursorClickEffect.Ripple => "Ripple",
            CursorClickEffect.Turn => "Turn",
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };
    }

    extension(CursorClickEffect)
    {
        public static ValueList<CursorClickEffect> AllCases => RawValues.AllCases;

        public static CursorClickEffect? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

/// <summary>Swift `CursorHighlightSettings`. Stored properties are public fields (see Element.cs).</summary>
public struct CursorHighlightSettings : IEquatable<CursorHighlightSettings>
{
    public const double ClickDuration = 0.45;

    public CursorHighlightShape Shape = CursorHighlightShape.Ring;
    public CursorHighlightVisibility Visibility = CursorHighlightVisibility.Always;
    public RGBA Color = new(1, 0.82, 0);
    public double Diameter = 36;
    public double BorderWidth = 3;
    public bool ShowClicks = true;
    public double? GlowRadius;
    public StrokePattern? BorderStyle;
    public CursorClickEffect? ClickEffect;

    public CursorHighlightSettings()
    {
    }

    public readonly double ResolvedGlow => GlowRadius ?? 0;

    public readonly StrokePattern ResolvedBorder => BorderStyle ?? StrokePattern.Solid;

    public readonly CursorClickEffect ResolvedClickEffect => ShowClicks ? ClickEffect ?? CursorClickEffect.Pulse : CursorClickEffect.Off;

    public readonly void Validate()
    {
        if (GlowRadius is { } glow && (!double.IsFinite(glow) || !(glow >= 0 && glow <= 24)))
        {
            throw CanvasError.InvalidStyle;
        }
        if (!Color.IsValid || !double.IsFinite(Diameter) || !(Diameter >= 16 && Diameter <= 160)
            || !double.IsFinite(BorderWidth) || !(BorderWidth >= 1 && BorderWidth <= 12))
        {
            throw CanvasError.InvalidStyle;
        }
    }

    public readonly bool IsVisible(double now, double? lastMovement, double? lastClick) => Visibility switch
    {
        CursorHighlightVisibility.Always => true,
        CursorHighlightVisibility.Moving =>
            (lastMovement is { } moved && now >= moved && now - moved < 0.75)
            || (lastClick is { } clicked && now >= clicked && now - clicked < ClickDuration),
        CursorHighlightVisibility.Clicks => lastClick is { } click && now >= click && now - click < ClickDuration,
        _ => false,
    };

    public static double? ClickProgress(double now, double? startedAt)
    {
        if (startedAt is not { } start || !double.IsFinite(now) || !double.IsFinite(start)
            || !(now >= start) || !(now - start < ClickDuration))
        {
            return null;
        }
        return (now - start) / ClickDuration;
    }

    public readonly bool Equals(CursorHighlightSettings other) =>
        Shape == other.Shape && Visibility == other.Visibility && Color == other.Color && Diameter == other.Diameter
        && BorderWidth == other.BorderWidth && ShowClicks == other.ShowClicks && GlowRadius == other.GlowRadius
        && BorderStyle == other.BorderStyle && ClickEffect == other.ClickEffect;

    public override readonly bool Equals(object? obj) => obj is CursorHighlightSettings other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Shape, Visibility, Color, Diameter, BorderWidth, ShowClicks);

    public static bool operator ==(CursorHighlightSettings left, CursorHighlightSettings right) => left.Equals(right);

    public static bool operator !=(CursorHighlightSettings left, CursorHighlightSettings right) => !left.Equals(right);
}
