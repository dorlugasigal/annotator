// Port of the platform-neutral parts of apps/macos/Annotator/InspectorColorPicker.swift (PaletteHex and the palette of
// InspectorColorPalette) and apps/macos/Annotator/InlineColorWheel.swift (WheelColor).
using System.Collections.Immutable;
using System.Globalization;
using Annotator.Core;

namespace Annotator.Workspace.ColorPicking;

/// <summary>Mac PaletteHex: colors as eight uppercase hex digits (RRGGBBAA), and six RGB or eight RGBA digits as input.</summary>
public static class PaletteHex
{
    /// <summary>
    /// The message of the error <see cref="Parse"/> throws, which the popover shows under the field. The Mac wraps it in
    /// CanvasError.invalidDocument and shows "The drawing could not be opened: " first, which is not what happened.
    /// </summary>
    public const string InvalidMessage = "Use six RGB or eight RGBA hex digits.";

    /// <summary>Each channel rounded to the nearest byte, halves away from zero (Swift's rounded()).</summary>
    public static string Format(RGBA color) => string.Create(CultureInfo.InvariantCulture,
        $"{Channel(color.Red):X2}{Channel(color.Green):X2}{Channel(color.Blue):X2}{Channel(color.Alpha):X2}");

    /// <summary>
    /// Reads six RGB digits (opaque) or eight RGBA digits, with an optional leading # and surrounding white space.
    /// Throws <see cref="InvalidHexColorException"/> for anything else; nothing invalid becomes "no fill".
    /// </summary>
    public static RGBA Parse(string draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        string value = Digits(draft);
        if (value.Length is not (6 or 8) || !value.All(char.IsAsciiHexDigit) ||
            !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint code))
        {
            throw new InvalidHexColorException();
        }
        uint rgba = value.Length == 6 ? (code << 8) | 255 : code;
        return new RGBA(((rgba >> 24) & 255) / 255.0, ((rgba >> 16) & 255) / 255.0, ((rgba >> 8) & 255) / 255.0, (rgba & 255) / 255.0);
    }

    /// <summary>True when the draft has eight digits: its alpha replaces the paint opacity (Mac applyHex).</summary>
    public static bool HasAlpha(string draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Digits(draft).Length == 8;
    }

    private static string Digits(string draft)
    {
        string value = draft.Trim();
        return value.StartsWith('#') ? value[1..] : value;
    }

    private static int Channel(double value) => (int)Math.Round(value * 255, MidpointRounding.AwayFromZero);
}

/// <summary>A hex color that is not six RGB or eight RGBA digits; its message is <see cref="PaletteHex.InvalidMessage"/>.</summary>
public sealed class InvalidHexColorException : FormatException
{
    public InvalidHexColorException()
        : base(PaletteHex.InvalidMessage)
    {
    }

    public InvalidHexColorException(string message)
        : base(message)
    {
    }

    public InvalidHexColorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The colors of the Mac's stroke and fill palette, their names, the shades of a color and the selection rule.</summary>
public static class PaintPalette
{
    /// <summary>Swatches in a row of the palette grid (Mac LazyVGrid with five fixed 38-point columns).</summary>
    public const int Columns = 5;

    private static readonly ImmutableArray<RGBA> s_colors =
    [
        new RGBA(0, 0, 0, 0), Palette.Ink, new RGBA(0.33, 0.37, 0.39), Palette.White, new RGBA(0.53, 0.4, 0.36),
        new RGBA(0.0, 0.59, 0.62), Palette.Blue, new RGBA(0.49, 0.33, 0.72), new RGBA(0.69, 0.32, 0.66), new RGBA(0.89, 0.26, 0.4),
        new RGBA(0.22, 0.62, 0.35), new RGBA(0.0, 0.62, 0.51), Palette.Yellow, new RGBA(0.95, 0.57, 0.13), Palette.Coral,
    ];

    private static readonly ImmutableArray<string> s_names =
    [
        "Transparent stroke", "Dark ink", "Slate gray", "White", "Brown", "Teal", "Blue", "Purple", "Mauve", "Rose", "Green",
        "Emerald", "Yellow", "Orange", "Coral",
    ];

    private static readonly ImmutableArray<double> s_shadeAmounts = [0.5, 0.75, 1, 1.25, 1.5];

    private static readonly ImmutableArray<string> s_shadeNames = ["Darker", "Dark", "Current", "Light", "Lighter"];

    /// <summary>The 15 palette colors. The fill palette starts with no fill (null) where the stroke palette has transparent.</summary>
    public static ImmutableArray<RGBA?> Colors(bool fill) => [.. s_colors.Select((color, index) => fill && index == 0 ? (RGBA?)null : color)];

    /// <summary>The name of the palette color at <paramref name="index"/>.</summary>
    public static string Name(int index, bool fill) => fill && index == 0 ? "None, no fill" : s_names[index];

    /// <summary>A swatch's accessibility label: the name and the hex value, or the name alone for no fill.</summary>
    public static string Label(RGBA? color, string name) => color is { } value ? $"{name}, hex #{PaletteHex.Format(value)}" : name;

    /// <summary>Five shades of <paramref name="color"/>, darker to lighter, keeping its alpha (Mac shades).</summary>
    public static ImmutableArray<RGBA> Shades(RGBA color) =>
    [
        .. s_shadeAmounts.Select(amount =>
        {
            double Component(double value) => amount <= 1 ? value * amount : value + (1 - value) * (amount - 1);
            return new RGBA(Component(color.Red), Component(color.Green), Component(color.Blue), color.Alpha);
        }),
    ];

    /// <summary>The accessibility label of the shade at <paramref name="index"/>, such as "Darker shade of selected color".</summary>
    public static string ShadeName(int index) => s_shadeNames[index] + " shade of selected color";

    /// <summary>
    /// Mac isSelected: no gradient is in use, and the swatch has the selected red, green and blue with both transparent or
    /// both visible; no fill matches no fill.
    /// </summary>
    public static bool IsSelected(RGBA? value, RGBA? selected, ColorGradient? selectedGradient)
    {
        if (selectedGradient is not null)
        {
            return false;
        }
        if (value is not { } candidate || selected is not { } current)
        {
            return value is null && selected is null;
        }
        return candidate.Red == current.Red && candidate.Green == current.Green && candidate.Blue == current.Blue &&
            (candidate.Alpha == 0 ? current.Alpha == 0 : current.Alpha > 0);
    }
}

/// <summary>
/// Mac WheelColor: hue, saturation and brightness from 0 to 1 for the inline color wheel, where the hue is the angle
/// around the wheel's center and the saturation the distance from it.
/// </summary>
public struct WheelColor : IEquatable<WheelColor>
{
    public double Hue;
    public double Saturation;
    public double Brightness;

    public WheelColor(RGBA color)
    {
        double maximum = Math.Max(Math.Max(color.Red, color.Green), color.Blue);
        double minimum = Math.Min(Math.Min(color.Red, color.Green), color.Blue);
        double delta = maximum - minimum;
        Brightness = maximum;
        Saturation = maximum == 0 ? 0 : delta / maximum;
        if (delta == 0)
        {
            Hue = 0;
        }
        else if (maximum == color.Red)
        {
            Hue = (color.Green - color.Blue) / delta % 6 / 6;
        }
        else if (maximum == color.Green)
        {
            Hue = ((color.Blue - color.Red) / delta + 2) / 6;
        }
        else
        {
            Hue = ((color.Red - color.Green) / delta + 4) / 6;
        }
        if (Hue < 0)
        {
            Hue += 1;
        }
    }

    /// <summary>The opaque color of this hue, saturation and brightness.</summary>
    public readonly RGBA Color
    {
        get
        {
            double sector = (Hue == 1 ? 0 : Hue) * 6;
            double chroma = Brightness * Saturation;
            double second = chroma * (1 - Math.Abs(sector % 2 - 1));
            double offset = Brightness - chroma;
            (double red, double green, double blue) = sector switch
            {
                >= 0 and < 1 => (chroma, second, 0.0),
                >= 1 and < 2 => (second, chroma, 0.0),
                >= 2 and < 3 => (0.0, chroma, second),
                >= 3 and < 4 => (0.0, second, chroma),
                >= 4 and < 5 => (second, 0.0, chroma),
                _ => (chroma, 0.0, second),
            };
            return new RGBA(red + offset, green + offset, blue + offset);
        }
    }

    /// <summary>
    /// Mac move(to:size:): points the hue at <paramref name="point"/> from the center of a wheel of the given size, and
    /// sets the saturation to the distance from the center, at most 1 at the rim. The center keeps the hue.
    /// </summary>
    public void Move(Point point, double width, double height)
    {
        double dx = point.X - width / 2, dy = point.Y - height / 2;
        double distance = double.Hypot(dx, dy);
        if (distance > 0)
        {
            Hue = Math.Atan2(dy, dx) / (2 * Math.PI);
            if (Hue < 0)
            {
                Hue += 1;
            }
        }
        Saturation = Math.Min(1, distance / Math.Max(1, Math.Min(width, height) / 2));
    }

    /// <summary>
    /// Mac InlineColorWheel onChange(of: color): follows a new color, but keeps the wheel's hue for grays and black, which
    /// have none, and keeps its saturation for black.
    /// </summary>
    public void Follow(RGBA color)
    {
        var updated = new WheelColor(color);
        Brightness = updated.Brightness;
        if (updated.Brightness > 0)
        {
            Saturation = updated.Saturation;
        }
        if (updated.Saturation > 0 && !(Hue == 1 && updated.Hue == 0))
        {
            Hue = updated.Hue;
        }
    }

    public readonly bool Equals(WheelColor other) => Hue == other.Hue && Saturation == other.Saturation && Brightness == other.Brightness;

    public override readonly bool Equals(object? obj) => obj is WheelColor other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Hue, Saturation, Brightness);

    public static bool operator ==(WheelColor left, WheelColor right) => left.Equals(right);

    public static bool operator !=(WheelColor left, WheelColor right) => !left.Equals(right);
}
