using System.Globalization;
using System.Text;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DirectWrite;

namespace Annotator.Rendering.Text;

/// <summary>A font chosen by the D-4 rule: the family that exists on this system and its design metrics.</summary>
/// <param name="Family">The DirectWrite family name, for example "Segoe UI Variable Text".</param>
/// <param name="DesignUnitsPerEm">Units per em of the font that DirectWrite matched for the weight.</param>
public readonly record struct ResolvedFont(string Family, FontWeight Weight, ushort DesignUnitsPerEm, ushort Ascent, ushort Descent, short LineGap);

/// <summary>
/// DirectWrite text for annotation fonts (D-4, WIN-REND-03): system fonts on Windows, with frames measured here and
/// never written back into documents. Owns DirectWrite objects for one thread at a time.
/// </summary>
/// <remarks>
/// <para>Font rule: <see cref="FontDesign.System"/> uses Windows 11's interface font, Segoe UI Variable, with the optical
/// size that Windows' own type ramp uses for the point size (Small below 14, Text below 20, Display from 20; the Mac's
/// SF Pro also switches optical size by point size), and falls back to Segoe UI. <see cref="FontDesign.Monospace"/>
/// uses Cascadia Mono (shipped with Windows 11), then Consolas, then Courier New. Semibold is weight 600; a family
/// without it gets DirectWrite's nearest weight.</para>
/// <para>Metrics: AppKit's <c>ascender</c>, <c>-descender</c> and <c>leading</c> map to DirectWrite's
/// <c>DWRITE_FONT_METRICS</c> <c>ascent</c>, <c>descent</c> and <c>lineGap</c>, scaled by size / designUnitsPerEm.
/// Lines are ascent + descent + lineGap high, so frames are measured with Windows fonts (D-4). DirectWrite puts the
/// first baseline one ascent below the line top, like AppKit, but Segoe UI Variable's ascent (1.0791 em) is larger than
/// SF Pro's (0.9668 em), so the renderer moves each layout until its first baseline lands on
/// <see cref="Annotator.Core.Scene.TextRun.Baseline"/>, where the Mac draws it (<see cref="MacTextMetrics"/>).</para>
/// </remarks>
internal sealed unsafe class TextSystem : IDisposable
{
    private const string Locale = "en-us";
    private const int ENotSufficientBuffer = unchecked((int)0x8007007A);

    private static readonly string[] MonospaceFamilies = ["Cascadia Mono", "Consolas", "Courier New"];

    private readonly Dictionary<string, bool> availableFamilies = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Family, FontWeight Weight), ResolvedFont> resolved = [];
    private readonly Dictionary<TextFont, nint> formats = [];
    private IDWriteFactory* factory;
    private IDWriteFontCollection* collection;

    private TextSystem(IDWriteFactory* factory, IDWriteFontCollection* collection)
    {
        this.factory = factory;
        this.collection = collection;
    }

    /// <summary>Creates DirectWrite's shared factory (the same process-wide object for every caller) and the system font collection.</summary>
    public static TextSystem Create()
    {
        Guid iid = IDWriteFactory.IID_Guid;
        void* raw;
        Native.Check(PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iid, &raw), "DWriteCreateFactory(shared)");
        var factory = (IDWriteFactory*)raw;
        IDWriteFontCollection* collection = null;
        try
        {
            Native.Check(factory->GetSystemFontCollection(&collection, false), "IDWriteFactory::GetSystemFontCollection");
        }
        catch
        {
            Native.Release(ref factory);
            throw;
        }
        return new TextSystem(factory, collection);
    }

    internal IDWriteFactory* Factory
    {
        get
        {
            ObjectDisposedException.ThrowIf(factory == null, this);
            return factory;
        }
    }

    /// <summary>The family the D-4 rule picks for <paramref name="design"/> at <paramref name="size"/> points on this system.</summary>
    public string Family(FontDesign design, double size)
    {
        if (design == FontDesign.Monospace)
        {
            foreach (var candidate in MonospaceFamilies)
            {
                if (HasFamily(candidate))
                {
                    return candidate;
                }
            }
            return "Courier New";
        }
        var variable = size < 14 ? "Segoe UI Variable Small" : size < 20 ? "Segoe UI Variable Text" : "Segoe UI Variable Display";
        return HasFamily(variable) ? variable : "Segoe UI";
    }

    public ResolvedFont Resolve(TextFont font)
    {
        var family = Family(font.Design, font.Size);
        if (resolved.TryGetValue((family, font.Weight), out var known))
        {
            return known;
        }
        IDWriteFontFamily* fontFamily = null;
        IDWriteFont* match = null;
        try
        {
            uint index;
            BOOL exists;
            fixed (char* name = family)
            {
                Native.Check(collection->FindFamilyName(name, &index, &exists), "IDWriteFontCollection::FindFamilyName");
            }
            if (!exists)
            {
                throw new InvalidOperationException($"The font family {family} is not installed.");
            }
            Native.Check(collection->GetFontFamily(index, &fontFamily), "IDWriteFontCollection::GetFontFamily");
            Native.Check(fontFamily->GetFirstMatchingFont((DWRITE_FONT_WEIGHT)(int)font.Weight, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                                                          DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, &match), "IDWriteFontFamily::GetFirstMatchingFont");
            DWRITE_FONT_METRICS metrics;
            match->GetMetrics(&metrics);
            var result = new ResolvedFont(family, font.Weight, metrics.designUnitsPerEm, metrics.ascent, metrics.descent, metrics.lineGap);
            resolved[(family, font.Weight)] = result;
            return result;
        }
        finally
        {
            Native.Release(ref match);
            Native.Release(ref fontFamily);
        }
    }

    public FontLineMetrics LineMetrics(TextFont font)
    {
        var face = Resolve(font);
        double scale = font.Size / face.DesignUnitsPerEm;
        return new FontLineMetrics(face.Ascent * scale, face.Descent * scale, Math.Max((short)0, face.LineGap) * scale);
    }

    /// <summary>
    /// A layout with lines broken only at explicit line breaks, starting at the top of a
    /// <paramref name="maxWidth"/> by <paramref name="maxHeight"/> box, with absolute alignment and the base direction
    /// of the first strong character. The caller owns the returned reference.
    /// </summary>
    public IDWriteTextLayout* CreateLayout(string text, TextFont font, TextAlignment alignment, float maxWidth, float maxHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        var format = Format(font);
        IDWriteTextLayout* layout;
        fixed (char* chars = text)
        {
            Native.Check(factory->CreateTextLayout(chars, (uint)text.Length, format, Math.Max(0, maxWidth), Math.Max(0, maxHeight), &layout),
                         "IDWriteFactory::CreateTextLayout");
        }
        var rightToLeft = BaseDirection.IsRightToLeft(text);
        if (rightToLeft)
        {
            Native.Check(layout->SetReadingDirection(DWRITE_READING_DIRECTION.DWRITE_READING_DIRECTION_RIGHT_TO_LEFT), "IDWriteTextLayout::SetReadingDirection");
        }
        var mapped = alignment switch
        {
            TextAlignment.Center => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
            TextAlignment.Right => rightToLeft ? DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING : DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
            _ => rightToLeft ? DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING : DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
        };
        Native.Check(layout->SetTextAlignment(mapped), "IDWriteTextLayout::SetTextAlignment");
        return layout;
    }

    /// <summary>
    /// The first line's baseline below the top of <paramref name="layout"/>: the ascent of the line's tallest font, which
    /// DirectWrite's default line spacing puts there (0 for a layout without lines).
    /// </summary>
    public static float FirstBaseline(IDWriteTextLayout* layout)
    {
        uint count;
        var result = layout->GetLineMetrics(null, 0, &count);
        if (result.Value != ENotSufficientBuffer)
        {
            Native.Check(result, "IDWriteTextLayout::GetLineMetrics");
        }
        if (count == 0)
        {
            return 0;
        }
        Span<DWRITE_LINE_METRICS> lines = count <= 64 ? stackalloc DWRITE_LINE_METRICS[(int)count] : new DWRITE_LINE_METRICS[count];
        fixed (DWRITE_LINE_METRICS* pointer = lines)
        {
            Native.Check(layout->GetLineMetrics(pointer, count, &count), "IDWriteTextLayout::GetLineMetrics");
        }
        return lines[0].baseline;
    }

    /// <summary>
    /// AppKit <c>boundingRect</c> with no width limit, line-fragment origins and font leading: the widest line including
    /// trailing spaces, by the sum of line heights. A final line break adds no empty line, as in AppKit.
    /// </summary>
    public Size Measure(string text, TextFont font)
    {
        var layout = CreateLayout(text, font, TextAlignment.Left, 0, 0);
        try
        {
            DWRITE_TEXT_METRICS metrics;
            Native.Check(layout->GetMetrics(&metrics), "IDWriteTextLayout::GetMetrics");
            uint count;
            var result = layout->GetLineMetrics(null, 0, &count);
            if (result.Value != ENotSufficientBuffer)
            {
                Native.Check(result, "IDWriteTextLayout::GetLineMetrics");
            }
            double height = metrics.height;
            if (count > 1)
            {
                Span<DWRITE_LINE_METRICS> lines = count <= 64 ? stackalloc DWRITE_LINE_METRICS[(int)count] : new DWRITE_LINE_METRICS[count];
                fixed (DWRITE_LINE_METRICS* pointer = lines)
                {
                    Native.Check(layout->GetLineMetrics(pointer, count, &count), "IDWriteTextLayout::GetLineMetrics");
                }
                if (lines[(int)count - 1].length == 0)
                {
                    height -= lines[(int)count - 1].height;
                }
            }
            return new Size(metrics.widthIncludingTrailingWhitespace, height);
        }
        finally
        {
            Native.Release(ref layout);
        }
    }

    public void Dispose()
    {
        foreach (var format in formats.Values)
        {
            var pointer = (IDWriteTextFormat*)format;
            Native.Release(ref pointer);
        }
        formats.Clear();
        Native.Release(ref collection);
        Native.Release(ref factory);
    }

    private IDWriteTextFormat* Format(TextFont font)
    {
        ObjectDisposedException.ThrowIf(factory == null, this);
        if (formats.TryGetValue(font, out var known))
        {
            return (IDWriteTextFormat*)known;
        }
        if (!double.IsFinite(font.Size) || !(font.Size > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(font), font.Size, "A font size must be finite and positive.");
        }
        var family = Family(font.Design, font.Size);
        IDWriteTextFormat* format;
        fixed (char* name = family)
        fixed (char* locale = Locale)
        {
            Native.Check(factory->CreateTextFormat(name, collection, (DWRITE_FONT_WEIGHT)(int)font.Weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                                                   DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, (float)font.Size, locale, &format),
                         "IDWriteFactory::CreateTextFormat");
        }
        try
        {
            Native.Check(format->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP), "IDWriteTextFormat::SetWordWrapping");
            Native.Check(format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR), "IDWriteTextFormat::SetParagraphAlignment");
        }
        catch
        {
            Native.Release(ref format);
            throw;
        }
        formats[font] = (nint)format;
        return format;
    }

    private bool HasFamily(string family)
    {
        if (availableFamilies.TryGetValue(family, out var known))
        {
            return known;
        }
        uint index;
        BOOL exists;
        fixed (char* name = family)
        {
            Native.Check(collection->FindFamilyName(name, &index, &exists), "IDWriteFontCollection::FindFamilyName");
        }
        availableFamilies[family] = exists;
        return exists;
    }
}

/// <summary>The natural base direction: the first strong character (Unicode bidirectional rules P2 and P3).</summary>
internal static class BaseDirection
{
    /// <summary>The text editor's arrows use the same rule (<see cref="Workspace.Canvas.TextBoundaries.IsRightToLeft"/>).</summary>
    public static bool IsRightToLeft(string text) => Workspace.Canvas.TextBoundaries.IsRightToLeft(text);
}
