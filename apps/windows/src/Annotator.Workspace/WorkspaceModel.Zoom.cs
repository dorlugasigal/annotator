using Annotator.Core;
using Annotator.Workspace.Capture;
using Annotator.Workspace.Shortcuts;
using Annotator.Workspace.Zoom;

namespace Annotator.Workspace;

/// <summary>An animated start of screen zoom on <see cref="Display"/>: it ramps from what that display shows, else from 1x.</summary>
/// <param name="Serial">Increases with every animated start; the overlay ramps once per serial.</param>
public sealed record ZoomEntrance(ulong Serial, DisplayId Display);

/// <summary>
/// Live zoom and the pointer lens (spec 009, ADR 0004, WIN-CAP-05, WIN-CAP-06, WIN-INP-06 to 08): a port of the zoom parts of the
/// Mac <c>WorkspaceModel</c>. The zoom state lives in the editing target's document (<see cref="CanvasDocument.Presentation"/>)
/// and shares its Undo history; the overlay shows it with motion and reports the first zoomed frame
/// (<see cref="NoteZoomPresented"/>). The zoom hooks need no permission on Windows (WIN-INP-08); screen pixels need screen
/// access in a packaged build (WIN-CAP-04), which a zoom request waits for, as on the Mac.
/// </summary>
public sealed partial class WorkspaceModel
{
    /// <summary>Mac: "Option-scroll adjusts zoom in Drawing and Using apps.", with Windows' Alt.</summary>
    public const string ZoomScrollConnected = "Alt+scroll adjusts zoom in Drawing and Using apps.";

    /// <summary>Mac: the status line while Alt+scroll is not connected.</summary>
    public const string ZoomScrollNotConnected = "Alt+scroll is not connected. Check Settings > Permissions.";

    /// <summary>Mac ZoomInputFailure.unavailable, with Windows' Alt.</summary>
    public const string ZoomScrollUnavailable = "Alt+scroll could not start. Use the zoom buttons, or try starting zoom again.";

    /// <summary>Mac ZoomInputFailure.interrupted, with Windows for macOS.</summary>
    public const string ZoomScrollInterrupted = "Windows stopped listening for zoom shortcuts, so screen zoom has stopped.";

    /// <summary>Mac toggleMagnifier's status.</summary>
    public const string LensStatus =
        "Live magnifier: desktop clicks pass through unchanged. This is a lens, not full-screen transformed zoom.";

    private const string ZoomStartingStatus = "Starting live zoom…";
    private const string ZoomCancelledStatus = "Zoom request cancelled.";
    private const string ZoomDisplayUnavailable = "A drawing display could not be prepared. Show the toolbar and retry live zoom.";
    private static readonly TimeSpan s_zoomGestureEnd = TimeSpan.FromMilliseconds(180);

    private ZoomStartController? _zoomStart;
    private ZoomStartState _zoomStartState;
    private bool _zoomGesturesAvailable;
    private string? _zoomGestureMessage;
    private double? _pendingScreenZoomScale;
    private double? _pendingScreenZoomScrollDelta;
    private double _defaultMagnification = 2;
    private bool _magnifierEnabled;
    private bool _screenZoomEnabled;
    private bool _enterScreenZoom;
    private ZoomEntrance? _zoomEntrance;
    private ulong _zoomEntranceSerial;
    private ulong _zoomScrollSerial;
    private Canvas? _zoomGestureCanvas;
    private DocumentVersion? _zoomGestureVersion;
    private IDisposable? _zoomGestureTimer;
    private PresentationMode _failedPresentationMode;

    private ZoomStartController ZoomStart => _zoomStart ??= new ZoomStartController(
        // Mac screenGranted and requestScreen: Windows' answers, and the explanation that asks while Windows has not asked yet.
        screenGranted: () => ReadScreenAccess().IsGranted,
        gesturesGranted: static () => true,
        requestScreen: () => Ensure(ScreenAccessFeature.ScreenZoom, () => ZoomStart.Refresh()),
        requestGestures: static () => { },
        start: BeginScreenZoom,
        changed: OnZoomStartChanged,
        // The bounded checks and the startup deadline publish what they change, like every other change.
        timer: _timer is { } timer ? (delay, callback) => timer(delay, () =>
        {
            using ChangeScope scope = Change();
            callback();
        }) : null,
        requiresGesturesForStart: false);

    /// <summary>
    /// Mac magnification: the editing target's zoom, or the zoom the next lens or zoom starts with. Setting it changes the
    /// zoom as one Undo step (the toolbar's minus and plus buttons and the Zoom tools menu's levels).
    /// </summary>
    public double Magnification => _active?.Document.Presentation.Zoom ?? _defaultMagnification;

    /// <summary>Mac magnification's setter.</summary>
    public void SetMagnification(double value)
    {
        using ChangeScope scope = Change();
        if (!double.IsFinite(value) || value < 1 || value > 16)
        {
            Report(CanvasError.InvalidGeometry);
            return;
        }
        _defaultMagnification = value;
        if (_active is not { } active)
        {
            return;
        }
        try
        {
            PresentationSettings settings = active.Document.Presentation with { Zoom = value };
            Mutate(active, (ref CanvasDocument document) => document.SetPresentation(settings));
            Synchronize();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
    }

    /// <summary>Mac zoomScrollStatus: whether Alt+scroll reaches zoom now. Windows needs no permission for it (WIN-INP-08).</summary>
    public string ZoomScrollStatus => _zoomGesturesAvailable ? ZoomScrollConnected : _zoomGestureMessage ?? ZoomScrollNotConnected;

    /// <summary>Mac toggleMagnifier: the pointer lens on, once screen access allows it, or off with any zoom.</summary>
    public void ToggleMagnifier()
    {
        using ChangeScope scope = Change();
        if (_magnifierEnabled)
        {
            StopZoom();
            return;
        }
        if (!Ensure(ScreenAccessFeature.Lens, ToggleMagnifier))
        {
            return;
        }
        if (SetZoomMode(PresentationMode.Lens))
        {
            _status = LensStatus;
        }
    }

    /// <summary>Mac toggleScreenZoom: the toolbar's Full-screen zoom and the Tools window's Screen zoom.</summary>
    public void ToggleScreenZoom()
    {
        using ChangeScope scope = Change();
        RequestScreenZoom();
    }

    /// <summary>Mac toggleScreenZoomFromShortcut: the Live zoom shortcut (Ctrl+1) starts at 1.5x, or stops zoom.</summary>
    public void ToggleScreenZoomFromShortcut()
    {
        using ChangeScope scope = Change();
        RequestScreenZoom(startingAt: 1.5);
    }

    /// <summary>
    /// Mac startScreenZoomFromOptionScroll: Alt+scroll up while the toolbar shows starts zoom from 1x and applies this first
    /// scroll, so zoom grows gradually instead of jumping to the shortcut's level. Scrolling down starts nothing.
    /// </summary>
    public void StartScreenZoomFromAltScroll(double delta)
    {
        if (!double.IsFinite(delta) || !(delta > 0))
        {
            return;
        }
        using ChangeScope scope = Change();
        RequestScreenZoom(startingAt: 1, scrollDelta: delta);
    }

    /// <summary>
    /// Mac scrollScreenZoom: Alt+scroll changes screen zoom between 1x and 16x. One Undo step covers a scroll gesture: it ends
    /// 180 ms after the last scroll, or with the next other change. Scrolling up while zoom ramps out to 1x starts it again
    /// from what the display shows.
    /// </summary>
    public void ScrollScreenZoom(double delta)
    {
        using ChangeScope scope = Change();
        if (!_hidden && double.IsFinite(delta) && delta > 0 && _active is { } exiting
            && exiting.Document.Presentation.Mode != PresentationMode.Screen
            && _displayedZoom?.Get(exiting.Display.Id) is { Exiting: true } shown)
        {
            RequestScreenZoom(startingAt: shown.Magnification, scrollDelta: delta);
            return;
        }
        if (_hidden || !double.IsFinite(delta) || delta == 0 || _active is not { } active
            || active.Document.Presentation.Mode != PresentationMode.Screen)
        {
            return;
        }
        // One scroll gesture is one Undo step. The Mac holds a transaction open; here each scroll replaces the gesture's own
        // step instead, so the document never stays mid-transaction while canvases on the overlay thread edit copies of it.
        bool continues = _zoomGestureCanvas == active && _zoomGestureVersion == active.Version && active.Document.CanUndo;
        if (!continues)
        {
            FinishStyleEditCore();
            CommitTextIfNeeded(active);
            CancelGesture(active);
            _zoomGestureCanvas = active;
        }
        try
        {
            PresentationSettings settings = active.Document.Presentation;
            settings = settings with { Zoom = ZoomScroll.Next(settings.Zoom, delta) };
            Mutate(active, (ref CanvasDocument document) =>
            {
                if (continues)
                {
                    document.Undo();
                }
                document.SetPresentation(settings);
            });
            _zoomGestureVersion = active.Version;
            _zoomScrollSerial++;
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
        _zoomGestureTimer?.Dispose();
        _zoomGestureTimer = _timer?.Invoke(s_zoomGestureEnd, () =>
        {
            _zoomGestureTimer = null;
            FinishZoomGesture();
        });
    }

    /// <summary>Mac finishZoomGesture: the scroll gesture's zoom becomes one Undo step.</summary>
    public void FinishZoomGesture()
    {
        using ChangeScope scope = Change();
        FinishZoomGestureCore();
    }

    /// <summary>Mac stopZoom: screen zoom or the lens off, as one Undo step; a pending request is cancelled.</summary>
    public void StopZoom()
    {
        using ChangeScope scope = Change();
        ZoomStart.Cancel();
        _pendingScreenZoomScale = null;
        _pendingScreenZoomScrollDelta = null;
        FinishZoomGestureCore();
        if (_active is not { } active)
        {
            return;
        }
        CommitTextIfNeeded(active);
        CancelGesture(active);
        try
        {
            PresentationSettings settings = active.Document.Presentation with { Mode = PresentationMode.Off };
            Mutate(active, (ref CanvasDocument document) => document.SetPresentation(settings));
            Synchronize();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
    }

    /// <summary>
    /// Mac handleZoomEscape (the zoom Escape hook, and Escape on a canvas while zoom or the lens is on): commits text, else
    /// cancels the edit in progress, else stops zoom and gives clicks back to the apps. The host closes the tray flyout first.
    /// </summary>
    public void HandleZoomEscape()
    {
        using ChangeScope scope = Change();
        if (_active is { Edit: { Kind: EditKind.Text } } text)
        {
            CommitTextIfNeeded(text);
            Synchronize();
        }
        else if (_active is { Edit: not null } edited)
        {
            CancelGesture(edited);
            Synchronize();
        }
        else
        {
            StopZoom();
            UseApps();
        }
    }

    /// <summary>Mac cancelPendingZoom: the pending zoom's Cancel control.</summary>
    public void CancelPendingZoom()
    {
        using ChangeScope scope = Change();
        StopZoom();
        _status = ZoomCancelledStatus;
    }

    /// <summary>
    /// Mac noteZoomPresented and noteCaptureFrame: the overlay shows the first zoomed frame or lens frame, so a starting zoom
    /// becomes active and an earlier capture failure is no longer shown.
    /// </summary>
    public void NoteZoomPresented()
    {
        using ChangeScope scope = Change();
        ZoomStart.Presented();
        _captureFailureMessage = null;
        _failedPresentationMode = PresentationMode.Off;
    }

    /// <summary>
    /// Mac noteZoomGestures(available:error:): whether the Alt+scroll and Escape hooks run, and why not (a Windows message such
    /// as <see cref="ZoomScrollUnavailable"/>).
    /// </summary>
    public void NoteZoomGestures(bool available, string? message = null)
    {
        VerifyAccess();
        if (_zoomGesturesAvailable == available && _zoomGestureMessage == message)
        {
            return;
        }
        using ChangeScope scope = Change();
        _zoomGesturesAvailable = available;
        _zoomGestureMessage = message;
    }

    /// <summary>
    /// Mac reportCaptureFailure(_:presentationMode:) for zoom: the overlay could not show screen zoom or the lens on
    /// <paramref name="display"/>, which stopped it. A failed start says why; the toolbar shows the message, and choosing the
    /// lens or zoom again tries again (Mac retryLiveEffects).
    /// </summary>
    public void ReportZoomFailure(DisplayId display, PresentationMode mode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        using ChangeScope scope = Change();
        if (_canvases.TryGetValue(display, out Canvas? canvas) && canvas.Document.Presentation.Mode != PresentationMode.Off)
        {
            FinishZoomGestureCore();
            Mutate(canvas, (ref CanvasDocument document) => document.StopPresentationWithoutHistory());
            canvas.PresentationCut++;
        }
        if (mode == PresentationMode.Screen)
        {
            ZoomStart.Fail(message);
        }
        _failedPresentationMode = mode;
        _captureFailureMessage = message;
        _status = message;
        Synchronize();
    }

    /// <summary>
    /// Mac requestScreenZoom: stops zoom when it is on, else starts it at <paramref name="startingAt"/> or the last zoom. The
    /// request waits for screen access while Windows has not asked yet; a refusal fails it at once with where to allow it,
    /// as Windows, unlike macOS, reports a refusal.
    /// </summary>
    private void RequestScreenZoom(double? startingAt = null, double? scrollDelta = null)
    {
        if (_screenZoomEnabled)
        {
            StopZoom();
            return;
        }
        ScreenAccess access = ReadScreenAccess();
        if (access.IsRefused)
        {
            FailZoomForScreenAccess(PresentationMode.Screen, ScreenAccessText.Refused(access, ScreenAccessFeature.ScreenZoom));
            return;
        }
        _pendingScreenZoomScale = startingAt;
        _pendingScreenZoomScrollDelta = scrollDelta;
        ZoomStart.Request();
    }

    /// <summary>
    /// Zoom or the lens cannot read pixels without screen access (Mac reportCaptureFailure with a presentation mode): a zoom
    /// start fails, the toolbar shows why, Settings shows it too, and its Refresh status tries again.
    /// </summary>
    private void FailZoomForScreenAccess(PresentationMode mode, string message)
    {
        if (mode == PresentationMode.Screen)
        {
            ZoomStart.Fail(message);
        }
        _failedPresentationMode = mode;
        _captureFailureMessage = message;
        _captureVerified = false;
        _status = message;
        if (!_hidden && !_toolbarVisible)
        {
            ShowToolbar();
        }
    }

    /// <summary>
    /// Mac beginScreenZoom: shows hidden drawings and tools, starts zoom (from what the display shows, or 1x, with motion, unless
    /// an Alt+scroll starts it), gives clicks to the apps and applies the first scroll.
    /// </summary>
    private void BeginScreenZoom()
    {
        if (_hidden || _transition is not null)
        {
            RevealImmediately();
        }
        double? initialScroll = _pendingScreenZoomScrollDelta;
        double scale = _pendingScreenZoomScale ?? Magnification;
        _pendingScreenZoomScale = null;
        _pendingScreenZoomScrollDelta = null;
        if (initialScroll is null && scale <= 1)
        {
            scale = 2;
        }
        _enterScreenZoom = initialScroll is null && !_reducesMotion();
        bool started = SetZoomMode(PresentationMode.Screen, scale);
        _enterScreenZoom = false;
        if (!started)
        {
            ZoomStart.Fail(_errorMessage ?? ZoomDisplayUnavailable);
            return;
        }
        UseApps();
        if (initialScroll is { } delta)
        {
            ScrollScreenZoom(delta);
        }
    }

    /// <summary>Mac setZoomMode: the editing target's presentation becomes <paramref name="mode"/>, as one Undo step.</summary>
    private bool SetZoomMode(PresentationMode mode, double? scale = null)
    {
        double zoom = scale ?? Magnification;
        if (!double.IsFinite(zoom) || zoom < 1 || zoom > 16)
        {
            Report(CanvasError.InvalidGeometry);
            return false;
        }
        _defaultMagnification = zoom;
        FinishZoomGestureCore();
        if (_active is null)
        {
            BeginDrawing(recordsUsage: false);
            UseApps();
        }
        if (_active is not { } active)
        {
            return false;
        }
        CommitTextIfNeeded(active);
        CancelGesture(active);
        try
        {
            Mutate(active, (ref CanvasDocument document) =>
            {
                document.Select([]);
                document.SetPresentation(new PresentationSettings(mode, zoom));
            });
            RecordUsage(Metrics.UsageMetric.Zoom);
            if (mode == PresentationMode.Screen && _enterScreenZoom)
            {
                _zoomEntrance = new ZoomEntrance(++_zoomEntranceSerial, active.Display.Id);
            }
            Synchronize();
            return true;
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
            return false;
        }
    }

    private void FinishZoomGestureCore()
    {
        _zoomGestureTimer?.Dispose();
        _zoomGestureTimer = null;
        if (_zoomGestureCanvas is null)
        {
            return;
        }
        _zoomGestureCanvas = null;
        _zoomGestureVersion = null;
        Synchronize();
    }

    /// <summary>Mac zoomStart's changed handler: the status line follows the request, and a failed start turns zoom off.</summary>
    private void OnZoomStartChanged(ZoomStartState state)
    {
        _zoomStartState = state;
        switch (state.Phase)
        {
            case ZoomStartPhase.ScreenPermission:
                _status = ScreenAccessText.ZoomWaiting;
                break;
            case ZoomStartPhase.Starting:
                _status = ZoomStartingStatus;
                break;
            case ZoomStartPhase.Active:
                _status = $"Live zoom is on. {ZoomScrollStatus}";
                break;
            case ZoomStartPhase.Failed:
                if (_active is { } active && active.Document.Presentation.Mode == PresentationMode.Screen)
                {
                    Mutate(active, (ref CanvasDocument document) => document.StopPresentationWithoutHistory());
                    active.PresentationCut++;
                    Synchronize();
                }
                _status = state.Message ?? ZoomDisplayUnavailable;
                break;
        }
        if (state.Phase != ZoomStartPhase.Idle && !_toolbarVisible)
        {
            ShowToolbar();
        }
    }

    /// <summary>Mac synchronize()'s zoom part: the lens and screen zoom follow the editing target's presentation.</summary>
    private void SynchronizeZoom()
    {
        foreach (Canvas canvas in _canvases.Values)
        {
            PresentationMode shown = canvas.Document.Presentation.Mode;
            if (shown != canvas.SynchronizedPresentation)
            {
                canvas.SynchronizedPresentation = shown;
                // Mac synchronize(): a lens or screen zoom that starts calls presentation.retry(), which ends a capture pause.
                if (shown != PresentationMode.Off && !_hidden)
                {
                    _effectsRetry++;
                }
            }
        }
        PresentationMode mode = _active?.Document.Presentation.Mode ?? PresentationMode.Off;
        _magnifierEnabled = mode == PresentationMode.Lens;
        bool screen = mode == PresentationMode.Screen;
        if (!screen && _zoomStartState.Phase == ZoomStartPhase.Active)
        {
            ZoomStart.Cancel();
        }
        _screenZoomEnabled = screen;
    }

    /// <summary>
    /// The zoom part of the Mac's retryLiveEffects (Settings' retry): a lens or screen zoom that failed starts again. Returns
    /// false when neither failed, so the caller retries live effects instead (<see cref="RetryLiveEffects"/>).
    /// </summary>
    public bool RetryLiveZoom()
    {
        using ChangeScope scope = Change();
        PresentationMode failed = _failedPresentationMode;
        if (failed == PresentationMode.Off)
        {
            return false;
        }
        _failedPresentationMode = PresentationMode.Off;
        if (failed == PresentationMode.Lens)
        {
            ToggleMagnifier();
        }
        else
        {
            ToggleScreenZoom();
        }
        return true;
    }

    /// <summary>
    /// The center of what the display shows (Mac insertSavedShape): the source center of screen zoom's viewport for the
    /// pointer now, else the center of the display.
    /// </summary>
    private Point VisibleCenter(Canvas canvas)
    {
        Rect bounds = canvas.Display.Bounds;
        if (canvas.Document.Presentation.Mode == PresentationMode.Screen
            && _displayedZoom?.Get(canvas.Display.Id) is { Mode: PresentationMode.Screen } shown && _pointer() is { } pointer)
        {
            try
            {
                return Viewport.CursorAnchored(bounds, shown.Magnification, canvas.Display.ToLocal(pointer)).Source.Center;
            }
            catch (CanvasError)
            {
                // An unusable factor or pointer: fall back to the display's center.
            }
        }
        return bounds.Center;
    }

    /// <summary>The zoom shortcut's keycap label, for the flyout's Stop zoom row.</summary>
    public string ZoomShortcutLabel => WindowsShortcuts.Label(GlobalShortcutAction.LiveZoom, _preferences);
}
