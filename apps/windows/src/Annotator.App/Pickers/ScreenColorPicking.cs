using Annotator.Core;

namespace Annotator.App.Pickers;

/// <summary>The outcome of Pick from screen.</summary>
internal abstract record ScreenPickResult
{
    private ScreenPickResult()
    {
    }

    /// <summary>The person clicked: the color of that one pixel of the composed screen, opaque.</summary>
    public sealed record Picked(RGBA Color) : ScreenPickResult;

    /// <summary>Escape, another window taking over or the popover closing ended the pick without a color.</summary>
    public sealed record Cancelled : ScreenPickResult;

    /// <summary>The screen could not be read; <paramref name="Message"/> says what to do.</summary>
    public sealed record Failed(string Message) : ScreenPickResult;
}

/// <summary>
/// Pick from screen (WIN-UI-02): a magnifier loupe follows the pointer, a click reads only that pixel of the composed
/// screen and Escape cancels. It needs no permission. Use it on the app thread.
/// </summary>
internal interface IScreenColorPicker
{
    bool IsPicking { get; }

    /// <summary>Starts a pick; <paramref name="completed"/> runs once on the app thread.</summary>
    void Pick(Action<ScreenPickResult> completed);

    /// <summary>Ends a running pick without a color.</summary>
    void Cancel();
}
