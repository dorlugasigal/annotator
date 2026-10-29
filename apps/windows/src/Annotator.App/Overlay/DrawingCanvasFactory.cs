using System.Numerics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Text;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Windows.UI.ViewManagement;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.App.Overlay;

/// <summary>
/// Creates a <see cref="DrawingCanvas"/> for every overlay and owns what the canvases of the overlay thread share: the
/// DirectWrite text measurer, the coordinator for drags between displays, the theme colors, the cursors and the frame
/// clock that paces transitions. Create it anywhere; it starts its overlay-thread services with the first canvas and is
/// disposed there by the overlay host.
/// </summary>
internal sealed class DrawingCanvasFactory : IOverlayCanvasFactory, IOverlayFrameObserver, IDisposable
{
    /// <summary>
    /// The Mac's controlAccentColor, #007AFF in reference/ux-reference/macos/design-values.json, for the selection, marquee,
    /// attach hints and eraser preview instead of the Windows accent (owner decision of 2026-10-19, specification 013).
    /// App.xaml gives the app windows the same color.
    /// </summary>
    public static readonly RGBA Accent = new(0, 0x7A / 255.0, 1);

    private readonly Action<OverlayEvent> _report;
    private readonly Action<DisplayId, CanvasRequest> _request;
    private readonly DirectWriteTextMeasurer _measurer = new();
    private readonly CanvasCoordinator _coordinator = new();
    private readonly List<DrawingCanvas> _canvases = [];
    private readonly List<DrawingCanvas> _animating = [];
    private readonly HashSet<DrawingCanvas> _tracking = [];
    private readonly Windows.System.DispatcherQueueHandler _deliverPointer;
    private readonly Dictionary<DrawingCanvas, (Windows.UI.Composition.ContainerVisual Root, Windows.UI.Composition.SpriteVisual Visual)> _backdrops = [];
    private SystemDispatcherQueue? _queue;
    private BackgroundCanvasScheduler? _scheduler;
    private FrameClock? _clock;
    private CanvasCursors? _cursors;
    private WindowsSystemColors? _colors;
    private FixedSystemColors? _referenceColors;
    private UISettings? _settings;
    private Motion.VisibilityAnimator? _visibility;
    private PointerEffectsHost? _pointerEffects;
    private Windows.UI.Composition.Compositor? _compositor;
    private Sharing.SharedRegionOutput? _sharing;
    private Annotator.Platform.Graphics.CompositorBoost? _boost;
    private ulong _effectsRetry;
    private long _pointer;
    private long _pointerSeen;
    private int _pointerPosted;
    private volatile bool _tracksPointer;
    private readonly HashSet<DrawingCanvas> _arrows = [];
    private volatile bool _constructingArrow;
    private bool _disposed;

    /// <param name="report">Shows overlay failures to the person (called on the overlay thread).</param>
    /// <param name="request">Asks the app thread for a window a canvas needs (called on the overlay thread).</param>
    public DrawingCanvasFactory(Action<OverlayEvent> report, Action<DisplayId, CanvasRequest> request)
    {
        _report = report;
        _request = request;
        _deliverPointer = DeliverPointer;
    }

    public ITextMeasurer TextMeasurer => _measurer;

    /// <summary>
    /// Mac isConstructingArrow: a clicked arrow is being built on some display. Read on the app thread; the overlay thread
    /// sets it (<see cref="NoteArrow"/>).
    /// </summary>
    public bool ConstructingArrow => _constructingArrow;

    /// <summary>A canvas started or stopped building a clicked arrow (overlay thread).</summary>
    public void NoteArrow(DrawingCanvas canvas, bool constructing)
    {
        if (constructing ? _arrows.Add(canvas) : _arrows.Remove(canvas))
        {
            _constructingArrow = _arrows.Count > 0;
        }
    }

    /// <summary>Caret positions, selections and hit tests for the text editor, on the renderer's DirectWrite layouts.</summary>
    public ITextEditingLayout TextLayout => _measurer;

    public CanvasCoordinator Coordinator => _coordinator;

    public SystemDispatcherQueue Queue => _queue ?? throw new InvalidOperationException("The canvas factory has not started.");

    public ICanvasScheduler Scheduler => _scheduler ?? throw new InvalidOperationException("The canvas factory has not started.");

    public ISystemColorResolver Colors => (ISystemColorResolver?)_referenceColors ?? _colors ?? throw new InvalidOperationException("The canvas factory has not started.");

    /// <summary>Whether canvas controls such as the Use apps button use the dark theme.</summary>
    public bool IsDark => ReferenceDark ?? _colors?.IsDark ?? false;

    /// <summary>
    /// Development only (UX reference capture): the app theme to draw in instead of the person's; the accent stays the
    /// Mac's blue.
    /// </summary>
    public bool? ReferenceDark { get; init; }

    /// <summary>
    /// Development only (motion measurements): an opaque color under everything on each overlay, so a screen capture of the
    /// overlay holds only Annotator's pixels.
    /// </summary>
    public Annotator.Core.RGBA? ReferenceBackdrop { get; init; }

    /// <summary>
    /// Development only (live takes): canvases take pointer input only from <see cref="DrawingCanvas.ScriptPointer"/>, so the
    /// real pointer, wherever it rests, cannot reach a scripted drag (Windows sends a mouse move to the window under the
    /// pointer when windows change, for example when capture starts).
    /// </summary>
    public bool ReferenceScriptedInput { get; init; }

    /// <summary>
    /// Development only (live takes): where the take's scripted pointer is, in physical desktop pixels. Zoom and the lens start
    /// there instead of at the real pointer, which a take never moves; the take's moves then arrive through
    /// <see cref="PointerMoved"/> as the zoom hook's would.
    /// </summary>
    public Func<System.Drawing.Point>? ReferencePointer { get; init; }

    /// <summary>
    /// Whether canvases show the on-screen tip (Mac WorkspaceHintView) when the person's setting allows it. The UX reference
    /// and motion captures leave it out, as the Mac's canvas captures do.
    /// </summary>
    public bool ShowsHints { get; init; } = true;

    /// <summary>
    /// The app's capture service for live effects (WIN-CAP-01): each canvas shows its Blur, Pixelate and Distort regions from
    /// it. Without it effect regions stay opaque.
    /// </summary>
    public Annotator.Platform.Capture.CaptureService? Capture { get; init; }

    /// <summary>
    /// The app's window registry, where the pointer lens registers its window (WIN-CAP-06); with it and <see cref="Capture"/>,
    /// region sharing shows its output window (WIN-CAP-08). Without it there is no lens and no shared region.
    /// </summary>
    public Annotator.Platform.Windowing.WindowRegistry? Registry { get; init; }

    /// <summary>What each display shows of zoom, for the workspace on the app thread (Mac presentation?.magnification).</summary>
    public Annotator.Workspace.Zoom.DisplayedZoomBoard? DisplayedZoom { get; init; }

    /// <summary>
    /// The pointer moved to (<paramref name="x"/>, <paramref name="y"/>), physical desktop pixels, as the zoom hook saw it.
    /// Safe on any thread and cheap: while no display shows zoom or the lens it does nothing, and otherwise the overlay
    /// thread hears only of the newest position, once per turn of its queue.
    /// </summary>
    public void PointerMoved(int x, int y)
    {
        if (!_tracksPointer || _queue is not { } queue)
        {
            return;
        }
        Interlocked.Exchange(ref _pointer, ((long)x << 32) | (uint)y);
        Interlocked.Exchange(ref _pointerSeen, System.Diagnostics.Stopwatch.GetTimestamp());
        if (Interlocked.Exchange(ref _pointerPosted, 1) == 0 && !queue.TryEnqueue(Windows.System.DispatcherQueuePriority.High, _deliverPointer))
        {
            Interlocked.Exchange(ref _pointerPosted, 0);
        }
    }

    /// <summary>The toolbar's window, which the shared region captures on its own; set once the toolbar exists.</summary>
    public nint ToolbarWindow
    {
        get => Volatile.Read(ref _toolbarWindow);
        set => Volatile.Write(ref _toolbarWindow, value);
    }

    private nint _toolbarWindow;

    public CanvasCursors Cursors => _cursors ?? throw new InvalidOperationException("The canvas factory has not started.");

    /// <summary>The show and hide motion on the overlays, once the first canvas started the factory.</summary>
    internal Motion.VisibilityAnimator? Visibility => _visibility;

    /// <summary>Spotlight and Cursor halo on every overlay, once the first canvas started the factory.</summary>
    internal PointerEffectsHost PointerEffects => _pointerEffects ?? throw new InvalidOperationException("The canvas factory has not started.");

    /// <summary>
    /// The display's higher refresh rate while a pointer is pressed on any canvas (WIN-PERF-01): canvases hold it from a
    /// press to its release, once the first canvas started the factory.
    /// </summary>
    internal Annotator.Platform.Graphics.CompositorBoost Boost => _boost ?? throw new InvalidOperationException("The canvas factory has not started.");

    /// <summary>Development only (live check): the boost's requests, holds, state and last refusal; null before the first canvas.</summary>
    internal (int Requests, int Holds, bool Boosted, string? Failure)? BoostCounters =>
        _boost is { } boost ? (boost.Requests, boost.Holds, boost.IsBoosted, boost.Failure?.Message) : null;

    public void Presented(OverlayFrame frame, IReadOnlyDictionary<Platform.Overlays.OverlayWindow, IOverlayCanvas> canvases,
                          Platform.Graphics.GraphicsDevices? devices)
    {
        if (_disposed)
        {
            return;
        }
        if (frame.Workspace.EffectsRetry != _effectsRetry)
        {
            // Mac presentation?.retry(): an effect tool, showing the workspace or an unlock ends a capture pause, and
            // effect layers whose drawing failed try again.
            _effectsRetry = frame.Workspace.EffectsRetry;
            Capture?.Resume();
            foreach (DrawingCanvas canvas in _canvases)
            {
                canvas.RetryEffects();
            }
        }
        _visibility?.Presented(frame, canvases, devices);
        _pointerEffects?.Update(frame.Workspace);
        if (SharedOutput() is { } sharing)
        {
            sharing.ToolbarWindow = ToolbarWindow;
            sharing.Presented(frame, canvases, devices);
        }
    }

    /// <summary>The region sharing output, once the overlays exist and the app shares capture and windows with the canvases.</summary>
    internal Sharing.SharedRegionOutput? SharedOutput()
    {
        if (_sharing is null && !_disposed && Registry is { } registry && Capture is { } capture && _compositor is { } compositor)
        {
            _sharing = new Sharing.SharedRegionOutput(compositor, registry, capture, _report, _measurer, () => Colors, PointerEffects,
                Annotator.Workspace.Sharing.RegionSharingText.OutputTitle(BuildInfo.IsDevelopment));
            // The output plays the overlays' show and hide motion (Mac SharedWorkspaceTransition).
            if (_visibility is { } visibility)
            {
                visibility.Mirror = _sharing;
            }
        }
        return _sharing;
    }

    /// <summary>A canvas drew a change; the shared region follows it.</summary>
    public void CanvasRendered(DrawingCanvas canvas) => _sharing?.CanvasRendered(canvas);

    public void OverlaysChanging() => _visibility?.OverlaysChanging();

    public IOverlayCanvas Create(OverlayCanvasContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Start();
        _compositor ??= context.Compositor;
        var canvas = new DrawingCanvas(context, this);
        _canvases.Add(canvas);
        if (ReferenceBackdrop is { } color)
        {
            Windows.UI.Composition.SpriteVisual backdrop = context.Compositor.CreateSpriteVisual();
            backdrop.RelativeSizeAdjustment = Vector2.One;
            backdrop.Brush = context.Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, (byte)Math.Round(color.Red * 255),
                (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255)));
            context.Overlay.Root.Children.InsertAtBottom(backdrop);
            _backdrops[canvas] = (context.Overlay.Root, backdrop);
        }
        return canvas;
    }

    public void Report(OverlayEvent report) => _report(report);

    public void Request(DisplayId display, CanvasRequest request) => _request(display, request);

    /// <summary>Gives <paramref name="canvas"/> a frame on every composition frame until its transitions end.</summary>
    public void Animate(DrawingCanvas canvas)
    {
        if (_disposed)
        {
            return;
        }
        if (!_animating.Contains(canvas))
        {
            _animating.Add(canvas);
        }
        _clock!.Start();
    }

    /// <summary>Whether <paramref name="canvas"/> follows the pointer now (it shows zoom or the lens).</summary>
    public void Track(DrawingCanvas canvas, bool tracks)
    {
        if (tracks ? _tracking.Add(canvas) : _tracking.Remove(canvas))
        {
            _tracksPointer = _tracking.Count > 0;
        }
    }

    /// <summary>A canvas was disposed with its overlay.</summary>
    public void Removed(DrawingCanvas canvas)
    {
        _canvases.Remove(canvas);
        _animating.Remove(canvas);
        NoteArrow(canvas, false);
        Track(canvas, false);
        _sharing?.CanvasRemoved(canvas);
        if (_backdrops.Remove(canvas, out var backdrop))
        {
            backdrop.Root.Children.Remove(backdrop.Visual);
            backdrop.Visual.Brush?.Dispose();
            backdrop.Visual.Dispose();
        }
        if (_animating.Count == 0)
        {
            _clock?.Stop();
        }
    }

    /// <summary>The cursors canvases show now; the cursor cache keeps them when it starts over.</summary>
    public IEnumerable<nint> CursorsInUse()
    {
        foreach (DrawingCanvas canvas in _canvases)
        {
            yield return canvas.CursorHandle;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_settings is not null)
        {
            _settings.ColorValuesChanged -= OnColorValuesChanged;
        }
        if (_visibility is not null)
        {
            _visibility.Mirror = null;
        }
        _sharing?.Dispose();
        _sharing = null;
        _boost?.ReleaseAll();
        _clock?.Dispose();
        _animating.Clear();
        _visibility?.Dispose();
        _pointerEffects?.Dispose();
        _cursors?.Dispose();
        _measurer.Dispose();
    }

    /// <summary>Creates the services that belong to the overlay thread, once, on that thread.</summary>
    private void Start()
    {
        if (_queue is not null)
        {
            return;
        }
        SystemDispatcherQueue queue = SystemDispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Create drawing canvases on the overlay thread.");
        _scheduler = new BackgroundCanvasScheduler(work => queue.TryEnqueue(() => work()));
        _clock = new FrameClock(queue, OnFrame);
        _cursors = new CanvasCursors(CursorsInUse);
        _colors = new WindowsSystemColors(Accent);
        if (ReferenceDark is { } dark)
        {
            FixedSystemColors theme = dark ? FixedSystemColors.Dark : FixedSystemColors.Light;
            _referenceColors = new FixedSystemColors(_colors.Resolve(Annotator.Core.Scene.SystemColor.Accent),
                theme.Resolve(Annotator.Core.Scene.SystemColor.WindowBackground), theme.Resolve(Annotator.Core.Scene.SystemColor.Red));
        }
        _settings = new UISettings();
        _settings.ColorValuesChanged += OnColorValuesChanged;
        _queue = queue;
        _visibility = new Motion.VisibilityAnimator(queue, _report, _measurer, () => Colors);
        _pointerEffects = new PointerEffectsHost(queue, _report);
        _boost = new Annotator.Platform.Graphics.CompositorBoost();
        // A refused boost leaves the display at its rate and the drawing as it is, so it is noted for diagnostics only.
        _boost.Failed += error => StartupLog.Note("compositor-boost", error.Message);
    }

    /// <summary>Gives the newest pointer position to the canvases that follow it (overlay thread).</summary>
    private void DeliverPointer()
    {
        Interlocked.Exchange(ref _pointerPosted, 0);
        long packed = Interlocked.Read(ref _pointer);
        long seen = Interlocked.Read(ref _pointerSeen);
        var position = new System.Drawing.Point((int)(packed >> 32), (int)packed);
        foreach (DrawingCanvas canvas in _canvases)
        {
            if (_tracking.Contains(canvas))
            {
                canvas.TrackPointer(position, seen);
            }
        }
    }

    private void OnFrame(double now)
    {
        for (int index = _animating.Count - 1; index >= 0; index--)
        {
            if (!_animating[index].AdvanceTransitions(now))
            {
                _animating.RemoveAt(index);
            }
        }
        if (_animating.Count == 0)
        {
            _clock!.Stop();
        }
    }

    /// <summary>The accent color or the app theme changed (raised on another thread): redraw with the new selection colors.</summary>
    private void OnColorValuesChanged(UISettings sender, object args) => _queue?.TryEnqueue(() =>
    {
        if (_disposed || _colors is null || !_colors.Refresh())
        {
            return;
        }
        foreach (DrawingCanvas canvas in _canvases)
        {
            canvas.Redraw();
        }
    });
}
