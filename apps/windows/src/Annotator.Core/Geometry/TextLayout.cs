// Port of the platform-neutral rules in apps/macos/Sources/PresentationRendering/NativeTextFont.swift.
namespace Annotator.Core.Geometry;

/// <summary>A width and height in points.</summary>
public readonly record struct Size(double Width, double Height)
{
    public bool Equals(Size other) => Width == other.Width && Height == other.Height;

    public override int GetHashCode() => HashCode.Combine(Width, Height);
}

/// <summary>The font family rule (D-4: system fonts on each platform).</summary>
public enum FontDesign : byte
{
    /// <summary>The platform's interface font (SF Pro on the Mac).</summary>
    System,

    /// <summary>The platform's monospaced interface font (SF Mono on the Mac).</summary>
    Monospace,
}

/// <summary>Font weights the Mac uses for annotation text, as OpenType weight classes.</summary>
public enum FontWeight : short
{
    Regular = 400,
    Semibold = 600,
}

/// <summary>Swift <c>NativeTextFont.resolve(_:scale:)</c> without AppKit: design, weight and size in points.</summary>
public readonly record struct TextFont(FontDesign Design, FontWeight Weight, double Size)
{
    /// <summary>Bold text uses the semibold weight; the font name "monospace" selects the monospaced design.</summary>
    public static TextFont Resolve(in Style style, double scale = 1) => new(
        string.Equals(style.FontName, "monospace", StringComparison.Ordinal) ? FontDesign.Monospace : FontDesign.System,
        style.Bold ? FontWeight.Semibold : FontWeight.Regular,
        style.FontSize * scale);

    public bool Equals(TextFont other) => Design == other.Design && Weight == other.Weight && Size == other.Size;

    public override int GetHashCode() => HashCode.Combine(Design, Weight, Size);
}

/// <summary>Font line metrics in points; all values are positive.</summary>
/// <param name="Ascent">AppKit <c>ascender</c>.</param>
/// <param name="Descent">AppKit <c>-descender</c>.</param>
/// <param name="Leading">AppKit <c>leading</c> (DirectWrite <c>lineGap</c>).</param>
public readonly record struct FontLineMetrics(double Ascent, double Descent, double Leading);

/// <summary>
/// Measures text on the rendering platform (DirectWrite on Windows), as decided in D-4. Implementations
/// must be deterministic for equal inputs: the scene builder caches what it measures.
/// </summary>
public interface ITextMeasurer
{
    /// <summary>
    /// The size of the text laid out with no width limit and no wrapping, using line-fragment origins and the
    /// font's leading: AppKit <c>boundingRect(with: .greatestFiniteMagnitude, options: [.usesLineFragmentOrigin,
    /// .usesFontLeading])</c>.
    /// </summary>
    Size Measure(string text, TextFont font);

    FontLineMetrics LineMetrics(TextFont font);
}

public static class TextLayout
{
    /// <summary>
    /// Swift <c>NativeTextFont.contentSize</c>: the measured size rounded up plus 2 points, at least 32 wide and
    /// at least one line high. A trailing line feed adds an empty line.
    /// </summary>
    public static Size ContentSize(string text, TextFont font, ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measurer);
        var measured = measurer.Measure(text.Length == 0 ? " " : text, font);
        var metrics = measurer.LineMetrics(font);
        var lineHeight = metrics.Ascent + metrics.Descent;
        var extraLine = EndsWithLineFeed(text) ? lineHeight + metrics.Leading : 0;
        return new Size(SwiftMath.Max(32, Math.Ceiling(measured.Width) + 2),
                        SwiftMath.Max(Math.Ceiling(lineHeight), Math.Ceiling(measured.Height + extraLine) + 2));
    }

    /// <summary>Swift <c>hasSuffix("\n")</c>, which compares characters: "\r\n" is one character, not a line feed.</summary>
    internal static bool EndsWithLineFeed(string text) =>
        text.Length > 0 && text[^1] == '\n' && !(text.Length > 1 && text[^2] == '\r');
}
