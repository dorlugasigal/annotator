using Annotator.Core;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Annotator.Workspace;

namespace Annotator.App.Overlay;

/// <summary>Reports from the overlay thread to the app thread.</summary>
public abstract record OverlayEvent
{
    private OverlayEvent()
    {
    }

    /// <summary>The overlays exist for these displays (at start and after every rebuild).</summary>
    public sealed record Displays(IReadOnlyList<DisplayDescriptor> Current, bool Rebuilt) : OverlayEvent;

    /// <summary>Windows refused to move keyboard focus (foreground lock).</summary>
    public sealed record Focus(InputModeChange Change) : OverlayEvent;

    /// <summary>The graphics devices changed state; <see cref="State"/> is null when creating them failed with <see cref="Error"/>.</summary>
    public sealed record Devices(DeviceState? State, Exception? Error) : OverlayEvent;

    /// <summary>A native failure in overlay work; shown to the person.</summary>
    public sealed record Failure(string Operation, Exception Error) : OverlayEvent;

    /// <summary>
    /// The motion of visibility transition <paramref name="Serial"/> started on the overlays at <paramref name="Started"/>
    /// (<see cref="System.Diagnostics.Stopwatch"/> ticks) and runs for <paramref name="Duration"/> seconds.
    /// </summary>
    public sealed record VisibilityStarted(ulong Serial, long Started, double Duration) : OverlayEvent;

    /// <summary>The toolbar's snapshot of transition <paramref name="Serial"/> is on screen, so the real toolbar can go.</summary>
    public sealed record ToolbarCovered(ulong Serial) : OverlayEvent;

    /// <summary>
    /// Transition <paramref name="Serial"/> has no toolbar snapshot (Fade, or the snapshot failed): the real toolbar plays
    /// <paramref name="Motion"/> itself, starting <paramref name="Delay"/> seconds from now.
    /// </summary>
    public sealed record ToolbarFallback(ulong Serial, Annotator.Workspace.Motion.LayerMotion Motion, double Delay) : OverlayEvent;

    /// <summary>The motion of transition <paramref name="Serial"/> ended; the workspace completes it.</summary>
    public sealed record VisibilityFinished(ulong Serial) : OverlayEvent;

    /// <summary>A status message for the person, such as why a motion could not start (Mac model.status).</summary>
    public sealed record Status(string Message) : OverlayEvent;

    /// <summary>
    /// What a display's effect layer shows now (Mac effectsReady and captureFailureMessage): whether the display has effect
    /// regions (one being drawn included), whether they show live pixels, and why capture or drawing failed, for the person
    /// (<paramref name="Message"/>) and the log (<paramref name="Detail"/>).
    /// </summary>
    public sealed record Effects(DisplayId Display, bool Regions, bool PixelsAvailable, string? Message, string? Detail) : OverlayEvent;

    /// <summary>The person switched virtual desktops while the overlays were visible (WIN-OVR-06).</summary>
    public sealed record DesktopSwitched : OverlayEvent;

    /// <summary>
    /// What a display shows of zoom changed (null: nothing), including the end of a zoom-out ramp, so the app keeps the zoom
    /// hooks for exactly their lifetime (WIN-INP-06, WIN-INP-07).
    /// </summary>
    public sealed record ZoomShown(DisplayId Display, Annotator.Workspace.Zoom.DisplayedZoom? Shown) : OverlayEvent;

    /// <summary>Screen zoom's first zoomed frame is on screen (Mac noteZoomPresented).</summary>
    public sealed record ZoomPresented(DisplayId Display) : OverlayEvent;

    /// <summary>
    /// Zoom or the lens could not go on (Mac LivePresentation.fail): the app stops it without history and shows
    /// <paramref name="Message"/>; <paramref name="Detail"/> is for the log.
    /// </summary>
    public sealed record ZoomFailed(DisplayId Display, Annotator.Core.PresentationMode Mode, string Message, string? Detail) : OverlayEvent;

    /// <summary>A canvas received Alt+scroll (Mac handleLocalZoomScroll), which the zoom hook takes when it works.</summary>
    public sealed record ZoomScroll(DisplayId Display, double Delta) : OverlayEvent;

    /// <summary>The shared region shows a frame of output generation <paramref name="Generation"/> with its current scene (Mac presented).</summary>
    public sealed record SharingPresented(ulong Generation) : OverlayEvent;

    /// <summary>The shared region covered itself while its covers, effects or drawings' visibility changed.</summary>
    public sealed record SharingWaiting(ulong Generation) : OverlayEvent;

    /// <summary>The shared region could not be shown; <paramref name="Capture"/> marks a capture failure.</summary>
    public sealed record SharingFailed(ulong Generation, string Message, bool Capture) : OverlayEvent;

    /// <summary>Capture under the shared region stopped, so sharing stops for safety.</summary>
    public sealed record SharingStopped(ulong Generation, string Message) : OverlayEvent;

    /// <summary>The person closed the shared region's window.</summary>
    public sealed record SharingClosed : OverlayEvent;
}

/// <summary>
/// The overlay thread (Gate 0 S-12): a Platform <see cref="DispatcherThread"/> that owns the graphics devices,
/// the <see cref="OverlayManager"/> and one canvas per display. The app thread only posts immutable
/// <see cref="OverlayFrame"/>s; frames are coalesced so the overlay thread applies only the latest one.
/// </summary>
public sealed class OverlayHost : IOverlayInputSink
{
    private readonly DispatcherThread _thread;
    private readonly WindowRegistry _registry;
    private readonly IWorkspaceChannel _channel;
    private readonly Action<OverlayEvent> _report;
    private readonly IOverlayCanvasFactory _factory;
    private readonly Func<IReadOnlyList<DisplayInfo>>? _displaySource;
    private readonly bool _activates;
    private readonly Dictionary<OverlayWindow, IOverlayCanvas> _canvases = [];
    private (DisplayId Display, nint Handle)[] _handles = [];
    private OverlayFrame? _latest;
    private int _queued;
    private OverlayManager? _manager;
    private GraphicsDevices? _devices;
    private ulong _focusSerial;
    private int _creating;
    private int _ready;
    private bool _stopped;

    private OverlayHost(DispatcherThread thread, WindowRegistry registry, IWorkspaceChannel channel, Action<OverlayEvent> report,
                        IOverlayCanvasFactory factory, Func<IReadOnlyList<DisplayInfo>>? displaySource, bool activates)
    {
        _thread = thread;
        _registry = registry;
        _channel = channel;
        _report = report;
        _factory = factory;
        _displaySource = displaySource;
        _activates = activates;
    }

    /// <summary>
    /// Starts the overlay thread. Devices and overlays are created there once <see cref="CreateOverlays"/> is called,
    /// after the toolbar's first frame, so the two threads never set up their composition at the same moment.
    /// </summary>
    public static OverlayHost Start(WindowRegistry registry, IWorkspaceChannel channel, Action<OverlayEvent> report, IOverlayCanvasFactory factory) =>
        new(DispatcherThread.Start("Annotator overlays"), registry, channel, report, factory, displaySource: null, activates: true);

    /// <summary>
    /// Development only (UX reference capture): overlays for made-up displays, which never take focus, so the canvas can
    /// be shown beyond the desktop's edge without covering or activating anything.
    /// </summary>
    internal static OverlayHost StartForReference(WindowRegistry registry, IWorkspaceChannel channel, Action<OverlayEvent> report,
                                                  IOverlayCanvasFactory factory, Func<IReadOnlyList<DisplayInfo>> displays) =>
        new(DispatcherThread.Start("Annotator reference overlays"), registry, channel, report, factory, displays, activates: false);

    /// <summary>
    /// Development only (live check): overlays for made-up displays on the desktop that take focus as the app's do, so
    /// real input can be checked beside a running Annotator.
    /// </summary>
    internal static OverlayHost StartForLiveCheck(WindowRegistry registry, IWorkspaceChannel channel, Action<OverlayEvent> report,
                                                  IOverlayCanvasFactory factory, Func<IReadOnlyList<DisplayInfo>> displays) =>
        new(DispatcherThread.Start("Annotator live check overlays"), registry, channel, report, factory, displays, activates: true);

    /// <summary>Development only (UX reference capture): runs <paramref name="work"/> on the overlay thread with every overlay's canvas.</summary>
    internal Task<T> RunAsync<T>(Func<IReadOnlyDictionary<OverlayWindow, IOverlayCanvas>, T> work) => _thread.RunAsync(() => work(_canvases));

    /// <summary>Creates the devices, overlays and canvases on the overlay thread. Later calls do nothing.</summary>
    public void CreateOverlays()
    {
        if (Interlocked.Exchange(ref _creating, 1) == 0 && !_thread.Queue.TryEnqueue(Initialize))
        {
            throw new InvalidOperationException("The overlay thread did not accept its first work item.");
        }
    }

    /// <summary>True once the overlays exist and take frames; readable from any thread.</summary>
    public bool IsReady => Volatile.Read(ref _ready) == 1;

    /// <summary>Converts a Platform display to the workspace descriptor; the GDI device name is the display's identity.</summary>
    public static DisplayDescriptor Describe(DisplayInfo display) => new(
        new DisplayId(display.DeviceName),
        new Rect(display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height),
        new Rect(display.WorkArea.Left, display.WorkArea.Top, display.WorkArea.Width, display.WorkArea.Height),
        display.Scale);

    /// <summary>Hands the latest frame to the overlay thread. Call from any thread; earlier unapplied frames are skipped.</summary>
    public void Post(OverlayFrame frame)
    {
        Volatile.Write(ref _latest, frame);
        if (Interlocked.Exchange(ref _queued, 1) == 0 && !_thread.Queue.TryEnqueue(ApplyLatest))
        {
            Volatile.Write(ref _queued, 0);
        }
    }

    /// <summary>
    /// The overlay window of a display, or 0. Readable from any thread; the app thread uses it to give focus back
    /// to the canvas when its own popovers close while drawing.
    /// </summary>
    public nint OverlayFor(DisplayId display)
    {
        foreach ((DisplayId candidate, nint handle) in Volatile.Read(ref _handles))
        {
            if (candidate == display)
            {
                return handle;
            }
        }
        return 0;
    }

    /// <summary>Destroys the overlays and devices on their thread, then ends the thread.</summary>
    public async Task StopAsync()
    {
        await _thread.RunAsync(() =>
        {
            _stopped = true;
            DisposeCanvases();
            (_factory as IDisposable)?.Dispose();
            _manager?.Dispose();
            _manager = null;
            _devices?.Dispose();
            _devices = null;
        }).ConfigureAwait(false);
        await _thread.ShutdownAsync().ConfigureAwait(false);
    }

    private void Initialize()
    {
        StartupLog.Mark("overlay-thread");
        CreateDevices();
        try
        {
            _manager = OverlayManager.Create(new OverlayManagerOptions
            {
                Registry = _registry,
                InputSink = this,
                InitialMode = OverlayInputMode.Hidden,
                DisplaySource = _displaySource,
            });
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException)
        {
            _report(new OverlayEvent.Failure("Create the drawing overlays", error));
            return;
        }
        _manager.OverlaysRebuilt += OnOverlaysRebuilt;
        // Canvases keep visuals under their overlay's root, so they go before the manager destroys the old overlays.
        _manager.DisplaysChanging += (_, _) => DisposeCanvases();
        _manager.Failed += (_, failure) => _report(new OverlayEvent.Failure(failure.Operation, failure.Error));
        _manager.VirtualDesktopSwitched += (_, _) => _report(new OverlayEvent.DesktopSwitched());
        CreateCanvases();
        _report(new OverlayEvent.Displays([.. _manager.Displays.Select(Describe)], Rebuilt: false));
        StartupLog.Mark("overlays-ready");
        Volatile.Write(ref _ready, 1);
        ApplyLatest();
    }

    private void CreateDevices()
    {
        try
        {
            _devices = GraphicsDevices.Create();
            _devices.DeviceLost += OnDeviceLost;
            _report(new OverlayEvent.Devices(_devices.State, null));
        }
        catch (NativeCallException error)
        {
            _devices = null;
            _report(new OverlayEvent.Devices(null, error));
        }
    }

    private void OnDeviceLost(object? sender, DeviceLostEventArgs args)
    {
        // Raised on whichever thread noticed; replace the bundle on the overlay thread.
        _thread.Queue.TryEnqueue(() =>
        {
            if (_stopped || _devices is null || !ReferenceEquals(sender, _devices))
            {
                return;
            }
            _report(new OverlayEvent.Devices(args.State, null));
            _devices.DeviceLost -= OnDeviceLost;
            _devices.Dispose();
            CreateDevices();
            foreach (IOverlayCanvas canvas in _canvases.Values)
            {
                canvas.DevicesChanged(_devices);
            }
        });
    }

    private void CreateCanvases()
    {
        if (_manager is null)
        {
            return;
        }
        foreach (OverlayWindow overlay in _manager.Overlays)
        {
            try
            {
                _canvases[overlay] = _factory.Create(new OverlayCanvasContext(overlay, Describe(overlay.Display), _manager.Compositor, _devices, _channel));
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
            {
                // The other displays keep their canvases; this one shows nothing and takes no drawing input.
                _report(new OverlayEvent.Failure($"Create the drawing canvas for {overlay.Display.DeviceName}", error));
            }
        }
        Volatile.Write(ref _handles, [.. _manager.Overlays.Select(overlay => (new DisplayId(overlay.Display.DeviceName), overlay.Handle))]);
    }

    private void DisposeCanvases()
    {
        Volatile.Write(ref _handles, []);
        (_factory as IOverlayFrameObserver)?.OverlaysChanging();
        foreach (IOverlayCanvas canvas in _canvases.Values)
        {
            canvas.Dispose();
        }
        _canvases.Clear();
    }

    private void OnOverlaysRebuilt(object? sender, OverlaysRebuiltEventArgs change)
    {
        DisposeCanvases();
        CreateCanvases();
        if (change.ModeChange.Focus is FocusChange.RestoreRefused)
        {
            _report(new OverlayEvent.Focus(change.ModeChange));
        }
        _report(new OverlayEvent.Displays([.. change.Overlays.Select(overlay => Describe(overlay.Display))], Rebuilt: true));
        ApplyLatest();
    }

    private void ApplyLatest()
    {
        // A full fence: a plain store here could pass the read of _latest below, and Post would then skip the newest frame.
        Interlocked.Exchange(ref _queued, 0);
        OverlayFrame? frame = Volatile.Read(ref _latest);
        if (frame is null || _manager is null || _stopped)
        {
            return;
        }
        WorkspaceSnapshot workspace = frame.Workspace;
        OverlayInputMode mode = workspace.Surfaces switch
        {
            SurfaceMode.Draw => OverlayInputMode.Draw,
            SurfaceMode.UseApps => OverlayInputMode.UseApps,
            // While drawings and tools show or hide with motion, the overlays show the snapshots and let every click through.
            _ when workspace.Visibility is not null => OverlayInputMode.UseApps,
            _ => OverlayInputMode.Hidden,
        };
        OverlayWindow? activate = null;
        if (_activates && mode == OverlayInputMode.Draw && workspace.FocusRequest is { } focus && focus.Serial != _focusSerial)
        {
            _focusSerial = focus.Serial;
            activate = _manager.Overlays.FirstOrDefault(overlay => overlay.Display.DeviceName == focus.Display.Value);
        }
        if (mode != _manager.InputMode || activate is not null)
        {
            try
            {
                InputModeChange change = _manager.SetInputMode(mode, activate);
                if (change.Focus is FocusChange.ActivationRefused or FocusChange.RestoreRefused)
                {
                    _report(new OverlayEvent.Focus(change));
                }
            }
            catch (NativeCallException error)
            {
                _report(new OverlayEvent.Failure("Change the drawing input mode", error));
            }
        }
        foreach ((OverlayWindow overlay, IOverlayCanvas canvas) in _canvases)
        {
            canvas.Present(new CanvasFrame(frame, workspace.Display(new DisplayId(overlay.Display.DeviceName))));
        }
        (_factory as IOverlayFrameObserver)?.Presented(frame, _canvases, _devices);
    }

    void IOverlayInputSink.OnPointer(OverlayWindow overlay, in PointerInput input)
    {
        if (_canvases.TryGetValue(overlay, out IOverlayCanvas? canvas))
        {
            canvas.OnPointer(in input);
        }
    }

    bool IOverlayInputSink.OnKey(OverlayWindow overlay, in KeyInput input)
    {
        if (_canvases.TryGetValue(overlay, out IOverlayCanvas? canvas))
        {
            return canvas.OnKey(in input);
        }
        const int Escape = 0x1B;
        if (input.Kind != KeyInputKind.KeyDown || input.VirtualKey != Escape || input.IsRepeat)
        {
            return false;
        }
        // An overlay whose canvas could not be created still releases input with Escape (WIN-A11Y-04).
        _channel.Send(new CanvasCommand(CanvasCommandKind.Escape, new DisplayId(overlay.Display.DeviceName)));
        return true;
    }

    void IOverlayInputSink.OnInputReleaseRequested(OverlayWindow overlay, InputReleaseReason reason) =>
        _channel.Send(new CanvasCommand(CanvasCommandKind.ReleaseInput, new DisplayId(overlay.Display.DeviceName)));

    void IOverlayInputSink.OnFocusChanged(OverlayWindow overlay, bool hasFocus)
    {
        if (_canvases.TryGetValue(overlay, out IOverlayCanvas? canvas))
        {
            canvas.OnFocusChanged(hasFocus);
        }
    }
}
