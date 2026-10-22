using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;

namespace Annotator.Platform.Overlays;

/// <summary>The overlay input modes of specification 002 (Off, Use apps, Draw).</summary>
public enum OverlayInputMode
{
    /// <summary>Overlays are hidden.</summary>
    Hidden,

    /// <summary>Overlays are visible and every one is click-through; apps get all input.</summary>
    UseApps,

    /// <summary>Overlays are visible and take pointer input; a click activates the clicked overlay.</summary>
    Draw,
}

/// <summary>Why an overlay asked the app to release drawing input instead of closing.</summary>
public enum InputReleaseReason
{
    /// <summary>Alt+F4 on an active overlay (WIN-OVR-12).</summary>
    AltF4,

    /// <summary>WM_CLOSE or SC_CLOSE from somewhere else, such as another app.</summary>
    CloseRequested,
}

/// <summary>
/// Receives overlay input on the overlay thread, in message order. Implementations must not
/// block: the overlay's message loop waits for each call.
/// </summary>
public interface IOverlayInputSink
{
    /// <summary>A pointer message. <paramref name="input"/> is valid only during the call.</summary>
    void OnPointer(OverlayWindow overlay, in PointerInput input);

    /// <summary>A key message. Return true when handled; false lets Windows apply its default.</summary>
    bool OnKey(OverlayWindow overlay, in KeyInput input);

    /// <summary>
    /// Alt+F4 or a close request reached an overlay. Overlays never close themselves; release
    /// drawing input as Use apps does.
    /// </summary>
    void OnInputReleaseRequested(OverlayWindow overlay, InputReleaseReason reason);

    /// <summary>The overlay gained or lost keyboard focus.</summary>
    void OnFocusChanged(OverlayWindow overlay, bool hasFocus);
}

/// <summary>What a mode change did to keyboard focus (WIN-OVR-03).</summary>
public enum FocusChange
{
    /// <summary>Focus was left alone.</summary>
    Unchanged,

    /// <summary>The requested overlay became the foreground window.</summary>
    ActivatedOverlay,

    /// <summary>Windows refused to bring the overlay to the foreground (foreground lock).</summary>
    ActivationRefused,

    /// <summary>Focus went back to the window that was active before Draw.</summary>
    RestoredPreviousWindow,

    /// <summary>The previous window no longer exists, so focus was left alone.</summary>
    PreviousWindowGone,

    /// <summary>Windows refused to give focus back to the previous window.</summary>
    RestoreRefused,
}

/// <summary>The result of <see cref="OverlayManager.SetInputMode"/>.</summary>
public sealed record InputModeChange(OverlayInputMode Previous, OverlayInputMode Current, FocusChange Focus);

/// <summary>Raised after the overlays were rebuilt for a new display configuration.</summary>
public sealed class OverlaysRebuiltEventArgs(
    DisplaysChangedEventArgs displayChange,
    InputModeChange modeChange,
    IReadOnlyList<OverlayWindow> overlays) : EventArgs
{
    public DisplaysChangedEventArgs DisplayChange { get; } = displayChange;

    /// <summary>Draw becomes Use apps: display, scale and taskbar changes release drawing input (WIN-OVR-05).</summary>
    public InputModeChange ModeChange { get; } = modeChange;

    public IReadOnlyList<OverlayWindow> Overlays { get; } = overlays;
}

/// <summary>A native failure in background overlay work, such as restoring the stacking order.</summary>
public sealed class OverlayFailureEventArgs(string operation, Exception error) : EventArgs
{
    public string Operation { get; } = operation;

    public Exception Error { get; } = error;
}

/// <summary>Settings for <see cref="OverlayManager.Create"/>.</summary>
public sealed record OverlayManagerOptions
{
    public required WindowRegistry Registry { get; init; }

    public required IOverlayInputSink InputSink { get; init; }

    /// <summary>A compositor created on the overlay thread; null creates one that the manager owns.</summary>
    public Compositor? Compositor { get; init; }

    /// <summary>Replaces <see cref="DisplayEnumerator.GetDisplays"/>, for tests.</summary>
    public Func<IReadOnlyList<DisplayInfo>>? DisplaySource { get; init; }

    public OverlayInputMode InitialMode { get; init; } = OverlayInputMode.Hidden;

    /// <summary>
    /// Whether the foreground hook puts the windows back on top after each foreground change (WIN-OVR-04). Development
    /// tools that show a stage under Annotator's windows turn it off, so the stage never rises above them.
    /// </summary>
    public bool RestoresStackOrder { get; init; } = true;
}
