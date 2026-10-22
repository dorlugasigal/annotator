using Annotator.Platform.Displays;

namespace Annotator.Platform.Text;

/// <summary>A selection as the Text Services Framework sees it, in UTF-16 offsets.</summary>
/// <param name="ActiveAtStart">The caret is at <paramref name="Start"/> (TS_AE_START).</param>
/// <param name="InterimCharacter">The selected character is one a Korean input method is still building (fInterimChar).</param>
public readonly record struct TextStoreSelection(int Start, int End, bool ActiveAtStart = false, bool InterimCharacter = false);

/// <summary>[<see cref="Start"/>, <see cref="OldEnd"/>) became [<see cref="Start"/>, <see cref="NewEnd"/>) (TS_TEXTCHANGE).</summary>
public readonly record struct TextStoreChange(int Start, int OldEnd, int NewEnd);

/// <summary>The underline an input method asks for (TF_DA_LINESTYLE).</summary>
public enum TextStoreLine
{
    None,
    Solid,
    Dot,
    Dash,
    Squiggle,
}

/// <summary>
/// How an input method wants [<see cref="Start"/>, <see cref="End"/>) of its composition drawn (TF_DISPLAYATTRIBUTE).
/// Colors are COLORREF values (0x00BBGGRR), or null for the text's own color.
/// </summary>
public readonly record struct TextStoreAttributeRun(int Start, int End, TextStoreLine Line, bool BoldLine, uint? LineColor, uint? TextColor,
                                                    uint? BackgroundColor);

/// <summary>
/// The text an editor shows, as the Text Services Framework reads and changes it (spec 013 S-7, WIN-INP-09). The app
/// implements it on the editor's thread. Input methods, the emoji panel, voice typing and the touch keyboard change it
/// only through <see cref="Replace"/> and <see cref="Select"/>, and only while they hold the store's write lock; the app
/// reports its own changes with <see cref="TextServicesSession.TextChanged"/>. Screen rectangles are in physical pixels.
/// </summary>
public interface ITextStoreDocument
{
    int Length { get; }

    /// <summary>Copies the text from <paramref name="start"/> into <paramref name="destination"/>.</summary>
    void CopyText(int start, Span<char> destination);

    TextStoreSelection Selection { get; }

    /// <summary>An input method moves the selection.</summary>
    void Select(TextStoreSelection selection);

    /// <summary>An input method replaces [<paramref name="start"/>, <paramref name="end"/>) with <paramref name="text"/>.</summary>
    TextStoreChange Replace(int start, int end, ReadOnlySpan<char> text);

    /// <summary>The screen rectangle around [<paramref name="start"/>, <paramref name="end"/>); the caret's line for an empty range.</summary>
    PixelRect TextBounds(int start, int end);

    /// <summary>The editor's rectangle on the screen.</summary>
    PixelRect ScreenBounds { get; }

    /// <summary>The caret offset at a screen point, or null when the point is outside the text and <paramref name="nearest"/> is false.</summary>
    int? OffsetAt(int x, int y, bool nearest);

    void CompositionStarted();

    /// <summary>The composition now covers [<paramref name="start"/>, <paramref name="end"/>).</summary>
    void CompositionChanged(int start, int end);

    /// <summary>How the input method wants the parts of its composition drawn, read after each of its edits.</summary>
    void CompositionStyled(ReadOnlySpan<TextStoreAttributeRun> runs);

    void CompositionEnded();
}
