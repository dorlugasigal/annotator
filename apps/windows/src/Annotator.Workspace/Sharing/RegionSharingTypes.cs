using System.Globalization;
using Annotator.Core;

namespace Annotator.Workspace.Sharing;

/// <summary>
/// Port of the Mac RegionSharingState (specification 012): idle, starting, live, stopped and failed, with the words the
/// chooser, the tray and the output use.
/// </summary>
public abstract record RegionSharingState
{
    private RegionSharingState()
    {
    }

    /// <summary>Nothing was shared yet, or the person stopped sharing.</summary>
    public sealed record Idle : RegionSharingState
    {
        public static readonly Idle Instance = new();
    }

    /// <summary>Capture runs and the output waits for its first frame of the current scene.</summary>
    public sealed record Starting : RegionSharingState
    {
        public static readonly Starting Instance = new();
    }

    /// <summary>The output shows the live region.</summary>
    public sealed record Live : RegionSharingState
    {
        public static readonly Live Instance = new();
    }

    /// <summary>Sharing stopped for safety (a region, display or system change), with what the person reads.</summary>
    public sealed record Stopped(string Text) : RegionSharingState;

    /// <summary>Sharing failed, with what the person reads.</summary>
    public sealed record Failed(string Text) : RegionSharingState;

    /// <summary>Mac isCapturing: starting or live.</summary>
    public bool IsCapturing => this is Starting or Live;

    /// <summary>Mac title: the status badge's and the tray's accessible words.</summary>
    public string Title => this switch
    {
        Starting => "Starting region sharing",
        Live => "Region sharing is on",
        _ => "Region sharing is off",
    };

    /// <summary>Mac message: why sharing stopped or failed, or null.</summary>
    public string? Message => this switch
    {
        Stopped stopped => stopped.Text,
        Failed failed => failed.Text,
        _ => null,
    };
}

/// <summary>A connected display the chooser offers (Mac RegionSharingDisplay): its descriptor and the name people see.</summary>
public sealed record RegionSharingDisplay(DisplayDescriptor Descriptor, string Name)
{
    public DisplayId Id => Descriptor.Id;

    /// <summary>The whole display in points from its top-left corner (Mac localBounds).</summary>
    public Rect LocalBounds => Descriptor.Bounds;

    public double Scale => Descriptor.Scale;
}

/// <summary>How a custom selection ended (Mac RegionSelectionResult).</summary>
public abstract record RegionSelectionResult
{
    private RegionSelectionResult()
    {
    }

    /// <summary>A rectangle was dragged on <paramref name="Display"/>, in points from its top-left corner.</summary>
    public sealed record Selected(DisplayId Display, Rect Bounds) : RegionSelectionResult;

    /// <summary>Escape cancelled the selection.</summary>
    public sealed record Cancelled : RegionSelectionResult
    {
        public static readonly Cancelled Instance = new();
    }

    /// <summary>The keyboard went to a window outside the selection panels.</summary>
    public sealed record FocusLost : RegionSelectionResult
    {
        public static readonly FocusLost Instance = new();
    }
}

/// <summary>
/// What the output window (Mac SharedRegionWindow) shows, for the thread that draws it. <see cref="Cover"/> covers the
/// output opaquely until a frame of <see cref="Generation"/> is on screen, and stays while <see cref="Capturing"/> is false.
/// </summary>
/// <param name="Identity">The window's identity: the same value keeps the same window, so a meeting app keeps it.</param>
/// <param name="Display">The source display as it was when sharing started.</param>
/// <param name="Region">The shared part of the display.</param>
/// <param name="Generation">Increases whenever the output must cover what it shows.</param>
/// <param name="Cover">The words on the opaque cover.</param>
/// <param name="Capturing">Capture and draw the region; false keeps the cover.</param>
public sealed record RegionSharingOutput(ulong Identity, DisplayDescriptor Display, SharingRegion Region, ulong Generation, string Cover,
                                         bool Capturing);

/// <summary>Region sharing in a <see cref="WorkspaceSnapshot"/>: the state the tray, toolbar and Tools show, and the output.</summary>
public sealed record RegionSharingSnapshot(RegionSharingState State, ShareRegionPreset? SelectedPreset, RegionSharingOutput? Output)
{
    public static RegionSharingSnapshot Off { get; } = new(RegionSharingState.Idle.Instance, ShareRegionPreset.LeftHalf, null);

    public bool IsCapturing => State.IsCapturing;
}

/// <summary>The words and number formats of region sharing (Mac RegionSharingController, RegionSharingControls).</summary>
public static class RegionSharingText
{
    /// <summary>The output window's title (Mac SharedRegionWindow.title); development builds say Annotator Dev.</summary>
    public static string OutputTitle(bool development) =>
        development ? "Annotator Dev - Shared Region" : "Annotator - Shared Region";

    public const string StartingCover = "Starting the region...";

    public const string UpdatingCover = "Updating the shared view...";

    public const string OffCover = "Region sharing is off";

    /// <summary>What SharedRegionWindow.close shows before the window goes.</summary>
    public const string ClosedCover = "Region stopped";

    /// <summary>The output's accessible value while it shows the live region.</summary>
    public const string LiveValue = "Live region. Choose this window in your meeting app.";

    /// <summary>The output's accessible name.</summary>
    public const string OutputLabel = "Shared region. View only.";

    public const string DisplayUnavailable = "The selected display is unavailable. Choose a connected display.";

    public const string RegionInvalid = "Choose a region inside the display, at least 32 points wide and high.";

    public const string Unavailable = "The live screen view is unavailable. Stop sharing and reopen Annotator.";

    public const string Transition = "Wait for the workspace animation to finish, then start sharing.";

    public const string CustomNeeded = "Draw or enter a custom region for this display.";

    public const string NumbersNeeded = "Enter numbers for the region's position, width and height.";

    public const string RegionChanged = "The region changed. Check its outline, then start sharing.";

    public const string DisplayDisconnected = "The selected display disconnected. Choose a connected display.";

    public const string DisplaySizeChanged = "The display size changed. Choose the region again.";

    public const string DisplayListUnreadable = "The display list could not be read. Check connected displays.";

    /// <summary>Mac suspendPresentation's message, with this computer for the Mac.</summary>
    public const string SystemPaused = "The desktop changed or this computer paused. Check the region, then start sharing.";

    /// <summary>Mac displaysChanged's message.</summary>
    public const string DisplaysChanged = "The display setup changed. Check the region, then start sharing.";

    /// <summary>Mac LivePresentation.stop's message when the live view stops under the region.</summary>
    public const string CaptureStopped = "Live screen access stopped. Check the region, then start sharing.";

    /// <summary>Mac CaptureFailure.desktopLocked, with this computer for the Mac.</summary>
    public const string DesktopLocked = "Screen capture is paused while this computer is locked. Unlock it, then resume live effects.";

    /// <summary>Mac CaptureFailure.invalidFrame: no frame arrived within the readiness wait.</summary>
    public const string NoFrame = "Screen capture did not provide a usable image.";

    /// <summary>Mac CaptureFailure.displayUnavailable.</summary>
    public const string CaptureDisplayUnavailable = "The selected display is no longer available for capture.";

    /// <summary>Mac workspaceSceneChanged's status.</summary>
    public const string AnimationStopped = "The shared animation stopped because the drawing changed.";

    /// <summary>The menu's caption while sharing.</summary>
    public const string SharingOnCaption = "Sharing is on. Hide drawings without stopping it.";

    /// <summary>
    /// Mac syncFields: a coordinate as en_US_POSIX formats it with no grouping and up to six fraction digits, such as
    /// 2560 or 853.333333.
    /// </summary>
    public static string Field(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Mac Double(text): a decimal number without spaces or grouping; null when the text is not a number.</summary>
    public static double? Number(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        const NumberStyles Styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return text.Length > 0 && double.TryParse(text, Styles, CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    /// <summary>The region's size in the display's pixels, as the chooser shows it ("2,560 × 1,440 pixels").</summary>
    public static string Pixels(Rect bounds, double scale)
    {
        long width = (long)Math.Floor(bounds.Width * scale);
        long height = (long)Math.Floor(bounds.Height * scale);
        return string.Create(CultureInfo.InvariantCulture, $"{width:N0} \u00d7 {height:N0} pixels");
    }
}
