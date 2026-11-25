namespace Annotator.Core.Geometry;

/// <summary>
/// Where the Mac puts text inside a frame, so Windows can put its own system fonts' glyphs there (D-4, WIN-REND-03).
/// The values come from the conformance corpus (<c>geometry/text.jsonl</c>): SF Pro and SF Mono, regular and semibold,
/// all have an AppKit ascender of 0.966796875 em and a descender of 0.2109375 em with no leading, and TextKit's line is
/// the rounded ascender plus the rounded descender. <c>NSString.draw(in:)</c> puts the first baseline one ascender
/// below the top of the frame. Windows keeps its frames (measured with DirectWrite) and its fonts' line spacing, and
/// draws the first baseline where the Mac's is: Segoe UI Variable's ascent (1.0791 em) would put it 0.11 em lower.
/// The handwriting font is the same Caveat file on both platforms: its AppKit ascender is 0.96 em and its descender
/// 0.3 em with no leading (also from the corpus), so its glyphs already sit where the Mac's do.
/// </summary>
public static class MacTextMetrics
{
    /// <summary>AppKit's <c>ascender</c> of SF Pro and SF Mono, in ems (1980 of 2048 units).</summary>
    public const double AscenderPerEm = 0.966796875;

    /// <summary>AppKit's <c>-descender</c> of SF Pro and SF Mono, in ems (432 of 2048 units).</summary>
    public const double DescenderPerEm = 0.2109375;

    /// <summary>AppKit's <c>ascender</c> of Caveat, in ems: 960 of 1000 units as Core Text's 16.16 fixed-point number.</summary>
    public const double HandwritingAscenderPerEm = 62915.0 / 65536;

    /// <summary>AppKit's <c>-descender</c> of Caveat, in ems: 300 of 1000 units as Core Text's 16.16 fixed-point number.</summary>
    public const double HandwritingDescenderPerEm = 19661.0 / 65536;

    public static double Ascender(double size, FontDesign design = FontDesign.System) =>
        size * (design == FontDesign.Handwriting ? HandwritingAscenderPerEm : AscenderPerEm);

    public static double Descender(double size, FontDesign design = FontDesign.System) =>
        size * (design == FontDesign.Handwriting ? HandwritingDescenderPerEm : DescenderPerEm);

    /// <summary>TextKit's line height: the ascender and the descender, each rounded to whole points (the leading is 0).</summary>
    public static double LineHeight(double size, FontDesign design = FontDesign.System) =>
        Math.Round(Ascender(size, design), MidpointRounding.AwayFromZero) + Math.Round(Descender(size, design), MidpointRounding.AwayFromZero);

    /// <summary>
    /// The height of Swift <c>NativeTextFont.contentSize</c> on the Mac: TextKit's lines, counting the empty line after
    /// a final line feed, rounded up plus 2 points, with one more unrounded line for a final line feed, and at least one
    /// line high. It reproduces every height in <c>geometry/text.jsonl</c>.
    /// </summary>
    public static double ContentHeight(string text, double size, FontDesign design = FontDesign.System)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = Ascender(size, design) + Descender(size, design);
        var extra = TextLayout.EndsWithLineFeed(text) ? line : 0;
        return SwiftMath.Max(Math.Ceiling(line), Math.Ceiling(LineCount(text) * LineHeight(size, design) + extra) + 2);
    }

    /// <summary>The first baseline of text drawn from the top of <paramref name="frame"/>: one ascender below its top.</summary>
    public static double FirstBaseline(Rect frame, double size, FontDesign design = FontDesign.System) => frame.Y + Ascender(size, design);

    /// <summary>
    /// The first baseline of a shape label. Both apps center a content-sized frame on the shape and draw the label from
    /// its top, but the Mac's frame has the Mac's content height, so its baseline follows that height rather than the
    /// height of the Windows frame <paramref name="frame"/>.
    /// </summary>
    public static double CenteredFirstBaseline(Rect frame, string text, double size, FontDesign design = FontDesign.System) =>
        frame.Center.Y - ContentHeight(text, size, design) / 2 + Ascender(size, design);

    /// <summary>Lines as TextKit counts them: CR LF is one break; CR, LF, NEL and the Unicode line and paragraph separators break.</summary>
    internal static int LineCount(string text)
    {
        var lines = 1;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '\r':
                    lines++;
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }
                    break;
                case '\n' or '\u0085' or '\u2028' or '\u2029':
                    lines++;
                    break;
            }
        }
        return lines;
    }
}
