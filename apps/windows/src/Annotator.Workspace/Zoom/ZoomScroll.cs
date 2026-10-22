namespace Annotator.Workspace.Zoom;

/// <summary>
/// Alt+scroll (WIN-INP-06), the Windows form of the Mac's Option-scroll: which wheel events zoom changes, and by how much.
/// Platform-neutral, so the hook procedure's decisions are tested on every OS.
/// </summary>
public static class ZoomScroll
{
    /// <summary>One wheel notch on Windows (WHEEL_DELTA).</summary>
    public const int WheelNotch = 120;

    /// <summary>
    /// The Mac's points for one wheel line: a scroll event without point deltas counts 12 points per line
    /// (ZoomGestureMonitor.zoomScrollDelta), so one notch changes zoom by e^(12 x 0.012), about 15.5%.
    /// </summary>
    public const double PointsPerNotch = 12;

    /// <summary>
    /// The Mac's zoom delta for a Windows wheel event: only Alt held (no Ctrl, Shift or Win, which Windows and other apps use
    /// with the wheel), a nonzero vertical delta, positive when the wheel turns away from the person. Precision touchpads send
    /// parts of a notch, which become parts of 12 points. Null when the event is not a zoom scroll.
    /// </summary>
    public static double? Delta(int wheelDelta, bool alt, bool control, bool shift, bool windows)
    {
        if (!alt || control || shift || windows || wheelDelta == 0)
        {
            return null;
        }
        return wheelDelta * PointsPerNotch / WheelNotch;
    }

    /// <summary>
    /// Mac scrollScreenZoom: the requested zoom after a scroll of <paramref name="delta"/> points, e^(delta x 0.012) times
    /// larger with the delta limited to ±80, within 1x to 16x.
    /// </summary>
    public static double Next(double zoom, double delta) =>
        Math.Min(16, Math.Max(1, zoom * Math.Exp(Math.Min(80, Math.Max(-80, delta)) * 0.012)));
}

/// <summary>What the Alt+scroll hook does with a wheel event while it exists (WIN-INP-06).</summary>
public enum ZoomScrollRole
{
    /// <summary>No hook: every wheel event passes.</summary>
    None,

    /// <summary>
    /// Mac ToolbarZoomActivation: the toolbar is visible and screen zoom is off, so Alt+scroll up starts zoom from 1x and
    /// Alt+scroll down passes to the app under the pointer.
    /// </summary>
    Activation,

    /// <summary>Screen zoom shows on <see cref="ZoomScrollState.Display"/>: Alt+scroll over that display changes zoom.</summary>
    Zoom,
}

/// <summary>
/// The hook's view of the workspace, published by the app thread and read by the hook thread with no lock.
/// <paramref name="Display"/> is the zoomed display's area in desktop pixels, for <see cref="ZoomScrollRole.Zoom"/>.
/// </summary>
public sealed record ZoomScrollState(ZoomScrollRole Role, int Left = 0, int Top = 0, int Right = 0, int Bottom = 0)
{
    public static ZoomScrollState Off { get; } = new(ZoomScrollRole.None);

    /// <summary>
    /// Whether the hook consumes a zoom scroll of <paramref name="delta"/> at desktop pixel (<paramref name="x"/>,
    /// <paramref name="y"/>): activation takes only scrolls up (Mac shouldHandleScroll: delta &gt; 0); zoom takes scrolls over
    /// its display (Mac eventIsOnDisplay). Every other wheel event reaches the app under the pointer.
    /// </summary>
    public bool Consumes(double delta, int x, int y) => Role switch
    {
        ZoomScrollRole.Activation => delta > 0,
        ZoomScrollRole.Zoom => x >= Left && x < Right && y >= Top && y < Bottom,
        _ => false,
    };

    /// <summary>
    /// Mac ToolbarZoomActivation.shouldMonitor (Windows needs no permission, WIN-INP-08): the toolbar is visible, the
    /// workspace shows, nothing animates and screen zoom is off.
    /// </summary>
    public static bool ActivationMonitors(bool toolbarVisible, bool workspaceHidden, bool visibilityAnimating, bool screenZoomEnabled) =>
        toolbarVisible && !workspaceHidden && !visibilityAnimating && !screenZoomEnabled;
}
