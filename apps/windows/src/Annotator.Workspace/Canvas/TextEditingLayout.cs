using System.Buffers;
using System.Globalization;
using System.Text;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// Text laid out as the renderer draws it: the text with its font and alignment in a frame of <see cref="Frame"/> points,
/// moved so its first line's baseline is <see cref="Baseline"/> points below the frame's top, as the renderer moves each
/// layout onto <see cref="Annotator.Core.Scene.TextRun.Baseline"/> (where the Mac draws it).
/// </summary>
public readonly record struct TextLayoutRequest(string Text, TextFont Font, TextAlignment Alignment, Size Frame, double Baseline);

/// <summary>A caret position: its x and the top and bottom of its line, in points from the text frame's top-left corner.</summary>
public readonly record struct TextCaret(double X, double Top, double Bottom);

/// <summary>A laid-out line: [<see cref="Start"/>, <see cref="End"/>) without its line break, and its top and bottom in points.</summary>
public readonly record struct TextLine(int Start, int End, double Top, double Bottom);

/// <summary>
/// Where laid-out text lies, for the text editor's caret, selection and pointer (spec 013 S-7). The Windows implementation
/// asks DirectWrite for the renderer's own layout, so the caret and selection sit exactly on the drawn glyphs. Positions
/// are in points from the text frame's top-left corner, before any rotation; offsets are UTF-16 offsets.
/// </summary>
public interface ITextEditingLayout
{
    /// <summary>The caret before offset <paramref name="index"/>.</summary>
    TextCaret Caret(in TextLayoutRequest request, int index);

    /// <summary>Adds the rectangles that [<paramref name="start"/>, <paramref name="end"/>) covers, at least one per line it touches.</summary>
    void Range(in TextLayoutRequest request, int start, int end, List<Rect> rectangles);

    /// <summary>The caret offset nearest to <paramref name="point"/>; never after a line break on the line the point is on.</summary>
    int HitTest(in TextLayoutRequest request, Point point);

    /// <summary>The line that holds the caret at <paramref name="index"/>.</summary>
    TextLine Line(in TextLayoutRequest request, int index);
}

/// <summary>Reads and writes plain text on the system clipboard for the text editor (text fields keep their own copy and paste).</summary>
public interface ITextClipboard
{
    /// <summary>The clipboard's text, or null when it holds none.</summary>
    /// <exception cref="InvalidOperationException">The clipboard could not be read; the message is for the person.</exception>
    string? GetText();

    /// <exception cref="InvalidOperationException">The clipboard could not be written; the message is for the person.</exception>
    void SetText(string text);
}

/// <summary>
/// What the text editor draws with its live text, in canvas points: <see cref="Under"/> below the text (the selection and
/// composition backgrounds) and <see cref="Over"/> above it (composition underlines). The caret is not part of it: the
/// platform draws a blinking caret of its own.
/// </summary>
public sealed record TextEditorDecorations(DisplayList Under, DisplayList Over)
{
    public static TextEditorDecorations None { get; } = new(DisplayList.Empty, DisplayList.Empty);
}

/// <summary>Grapheme cluster and word boundaries for caret movement and deletion (UAX #29 clusters; letters and digits make words).</summary>
public static class TextBoundaries
{
    private enum WordClass
    {
        Word,
        Space,
        LineBreak,
        Other,
    }

    /// <summary>The offset after the user-perceived character that starts at <paramref name="index"/>.</summary>
    public static int NextCluster(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (index >= text.Length)
        {
            return text.Length;
        }
        if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
        {
            return index + 2;
        }
        return index + Math.Max(1, StringInfo.GetNextTextElementLength(text, index));
    }

    /// <summary>The offset where the user-perceived character before <paramref name="index"/> starts.</summary>
    public static int PreviousCluster(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (index <= 0)
        {
            return 0;
        }
        int lineStart = index - 1 > 0 ? text.LastIndexOf('\n', index - 2) + 1 : 0;
        int start = lineStart;
        while (true)
        {
            int next = NextCluster(text, start);
            if (next >= index)
            {
                return start;
            }
            start = next;
        }
    }

    /// <summary>Windows' Ctrl+Left: the start of the word at or before <paramref name="index"/>, skipping spaces before it.</summary>
    public static int PreviousWord(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        int position = Math.Clamp(index, 0, text.Length);
        while (position > 0 && Classify(text, PreviousCluster(text, position)) == WordClass.Space)
        {
            position = PreviousCluster(text, position);
        }
        if (position == 0)
        {
            return 0;
        }
        int previous = PreviousCluster(text, position);
        WordClass kind = Classify(text, previous);
        if (kind == WordClass.LineBreak)
        {
            return previous;
        }
        position = previous;
        while (position > 0 && Classify(text, PreviousCluster(text, position)) == kind)
        {
            position = PreviousCluster(text, position);
        }
        return position;
    }

    /// <summary>Windows' Ctrl+Right: the start of the next word, after the rest of this word and the spaces that follow it.</summary>
    public static int NextWord(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        int position = Math.Clamp(index, 0, text.Length);
        if (position >= text.Length)
        {
            return text.Length;
        }
        WordClass kind = Classify(text, position);
        if (kind == WordClass.LineBreak)
        {
            return NextCluster(text, position);
        }
        if (kind != WordClass.Space)
        {
            while (position < text.Length && Classify(text, position) == kind)
            {
                position = NextCluster(text, position);
            }
        }
        while (position < text.Length && Classify(text, position) == WordClass.Space)
        {
            position = NextCluster(text, position);
        }
        return position;
    }

    /// <summary>The word, run of spaces or run of other characters around <paramref name="index"/> (a double-click).</summary>
    public static (int Start, int End) WordAt(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return (0, 0);
        }
        int position = Math.Clamp(index, 0, text.Length);
        if (position == text.Length || (Classify(text, position) == WordClass.LineBreak && position > 0))
        {
            position = PreviousCluster(text, position);
        }
        WordClass kind = Classify(text, position);
        if (kind == WordClass.LineBreak)
        {
            return (position, position);
        }
        int start = position, end = NextCluster(text, position);
        while (start > 0 && Classify(text, PreviousCluster(text, start)) == kind)
        {
            start = PreviousCluster(text, start);
        }
        while (end < text.Length && Classify(text, end) == kind)
        {
            end = NextCluster(text, end);
        }
        return (start, end);
    }

    /// <summary>The paragraph around <paramref name="index"/>, without its line break (a triple-click).</summary>
    public static (int Start, int End) ParagraphAt(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        int position = Math.Clamp(index, 0, text.Length);
        int start = position > 0 ? text.LastIndexOf('\n', position - 1) + 1 : 0;
        int end = text.IndexOf('\n', position);
        return (start, end < 0 ? text.Length : end);
    }

    /// <summary>
    /// The text's natural base direction: its first strong character (Unicode bidirectional rules P2 and P3). The renderer
    /// lays text out right to left when this is true, so the editor's arrows follow it.
    /// </summary>
    public static bool IsRightToLeft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value is 0x200F or 0x061C)
            {
                return true;
            }
            if (value == 0x200E)
            {
                return false;
            }
            if (IsRightToLeftRange(value))
            {
                if (Rune.IsLetter(rune))
                {
                    return true;
                }
                continue;
            }
            if (Rune.IsLetter(rune))
            {
                return false;
            }
        }
        return false;
    }

    private static bool IsRightToLeftRange(int value) =>
        value is >= 0x0590 and <= 0x08FF        // Hebrew, Arabic, Syriac, Thaana, NKo, Samaritan, Mandaic, Arabic Extended
            or >= 0xFB1D and <= 0xFDFF          // Hebrew and Arabic presentation forms A
            or >= 0xFE70 and <= 0xFEFF          // Arabic presentation forms B
            or >= 0x10800 and <= 0x10FFF        // Historic right-to-left scripts
            or >= 0x1E800 and <= 0x1EFFF;       // Mende Kikakui, Adlam, Arabic mathematical symbols

    private static WordClass Classify(string text, int index)
    {
        char first = text[index];
        if (first is '\n' or '\r' or '\u2028' or '\u2029')
        {
            return WordClass.LineBreak;
        }
        if (Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out _) != OperationStatus.Done)
        {
            return WordClass.Other;
        }
        if (Rune.IsWhiteSpace(rune))
        {
            return WordClass.Space;
        }
        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or UnicodeCategory.ConnectorPunctuation => WordClass.Word,
            _ => WordClass.Other,
        };
    }
}
