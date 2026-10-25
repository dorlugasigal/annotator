// Port of apps/macos/Sources/AnnotationCore/ColorGradient.swift.
namespace Annotator.Core;

/// <summary>A gradient of 2 to 8 opaque colors at an angle from 0 to 360 degrees.</summary>
public readonly record struct ColorGradient(ValueList<RGBA> Colors, double Angle = 0)
{
    public void Validate()
    {
        var colorsValid = Colors.Count >= 2 && Colors.Count <= 8;
        foreach (ref readonly var color in Colors)
        {
            colorsValid &= color.IsValid && color.Alpha == 1;
        }
        if (!colorsValid || !double.IsFinite(Angle) || !(Angle >= 0 && Angle <= 360))
        {
            throw CanvasError.InvalidStyle;
        }
    }

    public bool Equals(ColorGradient other) => Colors == other.Colors && Angle == other.Angle;

    public override int GetHashCode() => HashCode.Combine(Colors, Angle);
}

public enum GradientPreset { Sunset, Lagoon, Ember, Spectrum, Dusk, Citrus, Ocean, Orchid, Aurora, Cherry, Forest, Copper, Neon, Tropic, Steel }

public static class GradientPresetExtensions
{
    internal static readonly RawValueMap<GradientPreset> RawValues =
        new("sunset", "lagoon", "ember", "spectrum", "dusk", "citrus", "ocean", "orchid", "aurora", "cherry",
        "forest", "copper", "neon", "tropic", "steel");

    extension(GradientPreset)
    {
        public static ValueList<GradientPreset> AllCases => RawValues.AllCases;

        public static GradientPreset? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }

    extension(GradientPreset preset)
    {
        public string RawValue => RawValues.RawValue(preset);

        /// <summary>Swift `rawValue.capitalized`.</summary>
        public string Label => preset switch
        {
            GradientPreset.Sunset => "Sunset",
            GradientPreset.Lagoon => "Lagoon",
            GradientPreset.Ember => "Ember",
            GradientPreset.Spectrum => "Spectrum",
            GradientPreset.Dusk => "Dusk",
            GradientPreset.Citrus => "Citrus",
            GradientPreset.Ocean => "Ocean",
            GradientPreset.Orchid => "Orchid",
            GradientPreset.Aurora => "Aurora",
            GradientPreset.Cherry => "Cherry",
            GradientPreset.Forest => "Forest",
            GradientPreset.Copper => "Copper",
            GradientPreset.Neon => "Neon",
            GradientPreset.Tropic => "Tropic",
            GradientPreset.Steel => "Steel",
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        public ColorGradient Gradient
        {
            get
            {
                ValueList<RGBA> colors = preset switch
                {
                    GradientPreset.Sunset => [new(0.96, 0.12, 0.67), new(1, 0.23, 0.39), new(1, 0.48, 0.2)],
                    GradientPreset.Lagoon => [new(0.13, 0.48, 0.96), new(0.05, 0.73, 0.89), new(0.05, 0.82, 0.66)],
                    GradientPreset.Ember => [new(0.91, 0.19, 0.35), new(1, 0.53, 0.16), new(1, 0.85, 0.3)],
                    GradientPreset.Spectrum =>
                    [
                        new(0.93, 0.19, 0.49), new(0.63, 0.27, 0.9), new(0.16, 0.48, 0.95),
                        new(0.03, 0.72, 0.64), new(0.98, 0.74, 0.2), new(0.97, 0.31, 0.29),
                    ],
                    GradientPreset.Dusk => [new(0.37, 0.28, 0.89), new(0.67, 0.29, 0.8), new(0.88, 0.32, 0.66)],
                    GradientPreset.Citrus => [new(1, 0.55, 0.12), new(0.97, 0.83, 0.2), new(0.42, 0.76, 0.3)],
                    GradientPreset.Ocean => [new(0.08, 0.79, 0.74), new(0.18, 0.51, 0.94), new(0.47, 0.3, 0.85)],
                    GradientPreset.Orchid => [new(0.98, 0.43, 0.69), new(0.7, 0.44, 0.91), new(0.38, 0.63, 0.97)],
                    GradientPreset.Aurora => [new(0.13, 0.72, 0.47), new(0.25, 0.55, 0.95), new(0.82, 0.33, 0.88)],
                    GradientPreset.Cherry => [new(0.86, 0.1, 0.22), new(0.95, 0.27, 0.47), new(0.6, 0.17, 0.76)],
                    GradientPreset.Forest => [new(0.07, 0.55, 0.33), new(0.25, 0.7, 0.3), new(0.62, 0.8, 0.18)],
                    GradientPreset.Copper => [new(0.72, 0.33, 0.16), new(0.88, 0.5, 0.25), new(0.86, 0.66, 0.38)],
                    GradientPreset.Neon => [new(0.95, 0.2, 0.75), new(0.35, 0.45, 1), new(0.1, 0.82, 0.78)],
                    GradientPreset.Tropic => [new(0.98, 0.62, 0.12), new(0.94, 0.3, 0.48), new(0.42, 0.3, 0.9)],
                    GradientPreset.Steel => [new(0.33, 0.4, 0.5), new(0.52, 0.6, 0.7), new(0.4, 0.5, 0.65)],
                    _ => throw new ArgumentOutOfRangeException(nameof(preset)),
                };
                return new ColorGradient(colors, 25);
            }
        }
    }
}

public partial struct Style
{
    public const double DefaultFillOpacity = 0.4;

    // Swift MemoryLayout<RGBA>.stride: four Doubles.
    private const int RgbaStride = 32;

    public readonly bool UsesGradient => StrokeGradient is not null || FillGradient is not null;

    public readonly long PaintEstimatedBytes =>
        ((long)(StrokeGradient?.Colors.Count ?? 0) + (FillGradient?.Colors.Count ?? 0)) * RgbaStride;

    public readonly double PaintOpacity(bool fill) => fill ? Fill?.Alpha ?? DefaultFillOpacity : Color.Alpha;

    public void SetPaintOpacity(double value, bool fill)
    {
        if (fill)
        {
            if (Fill is { } current)
            {
                Fill = current with { Alpha = value };
            }
        }
        else
        {
            Color = Color with { Alpha = value };
        }
    }

    public void MatchFillToStroke()
    {
        var alpha = PaintOpacity(fill: true);
        Fill = new RGBA(Color.Red, Color.Green, Color.Blue, alpha);
        FillGradient = StrokeGradient;
    }

    public void SetFillEnabled(bool enabled)
    {
        if (enabled)
        {
            if (Fill is null)
            {
                MatchFillToStroke();
            }
        }
        else
        {
            Fill = null;
            FillGradient = null;
        }
    }

    public void SetPaintColor(RGBA? value, bool fill, bool useColorAlpha = false)
    {
        if (value is not { } color)
        {
            if (fill)
            {
                Fill = null;
                FillGradient = null;
            }
            return;
        }
        if (!useColorAlpha)
        {
            color = color with { Alpha = PaintOpacity(fill) };
        }
        if (fill)
        {
            Fill = color;
            FillGradient = null;
        }
        else
        {
            Color = color;
            StrokeGradient = null;
        }
    }

    public void SetGradient(ColorGradient gradient, bool fill)
    {
        var alpha = PaintOpacity(fill);
        var fallback = (gradient.Colors.IsEmpty ? Color : gradient.Colors[0]) with { Alpha = alpha };
        if (fill)
        {
            Fill = fallback;
            FillGradient = gradient;
        }
        else
        {
            Color = fallback;
            StrokeGradient = gradient;
        }
    }
}
