using System.Numerics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Zoom;
using Annotator.Workspace;
using Annotator.Workspace.Zoom;
using Windows.UI.Composition;

namespace Annotator.App.Overlay;

/// <summary>
/// One display's live zoom on the overlay thread, the Windows form of the Mac's LivePresentation screen view and lens
/// (WIN-CAP-05, WIN-CAP-06). It follows the document's presentation with <see cref="ScreenZoomDisplay"/> (the 260 ms
/// entrance and 200 ms exit ramps), shows the zoomed screen from the capture service under the drawings, and anchors the
/// viewport at the pointer, so the point under the pointer stays where it is. <see cref="View"/> is the viewport that the
/// canvas's drawings, selection, effect layer and caret use in the same composition frame. The lens is its own window
/// above the canvas. Nothing runs while nothing moves: ramps take frames from the factory's clock, and the pointer is
/// followed only while zoom or the lens shows.
/// </summary>
internal sealed class CanvasZoom : IDisposable
{
    private readonly OverlayCanvasContext _context;
    private readonly DrawingCanvasFactory _factory;
    private readonly DrawingCanvas _canvas;
    private readonly ContainerVisual _content;
    private readonly Action _viewChanged;
    private readonly ScreenZoomDisplay _shown = new();
    private readonly DisplayId _display;
    private readonly double _scale;
    private readonly Size _size;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private CapturedViewPresenter? _screen;
    private LensWindow? _lensWindow;
    private CapturedViewPresenter? _lens;
    private ElementList _elements;
    private bool _elementsChanged = true;
    private EffectList? _screenEffects;
    private EffectList? _lensEffects;
    private bool _started;
    private ulong _cut;
    private ulong _entrance;
    private ulong _scrolls;
    private bool _hidden = true;
    private Point _pointer;
    private bool _pointerKnown;
    private bool _editingText;
    private Viewport? _view;
    private bool _screenShown;
    private bool _screenPresented;
    private bool _lensShown;

    /// <summary>The lens drew its first picture, so its window may show (Mac renderLens needs a captured frame).</summary>
    private bool _lensReady;
    private bool _failureReported;
    private DisplayedZoom? _reported;
    private readonly ZoomStatistics _statistics = new();
    private bool _disposed;

    /// <param name="content">The canvas's container: the zoomed screen goes below everything in it, the board included.</param>
    /// <param name="viewChanged">The viewport changed: the canvas shows its drawings, effects and caret through <see cref="View"/>.</param>
    public CanvasZoom(OverlayCanvasContext context, DrawingCanvasFactory factory, DrawingCanvas canvas, ContainerVisual content, Action viewChanged)
    {
        _context = context;
        _factory = factory;
        _canvas = canvas;
        _content = content;
        _viewChanged = viewChanged;
        _display = context.Display.Id;
        _scale = context.Display.Scale;
        _size = new Size(context.Display.Bounds.Width, context.Display.Bounds.Height);
        _pixelWidth = context.Overlay.Display.Bounds.Width;
        _pixelHeight = context.Overlay.Display.Bounds.Height;
        _shown.ExitFinished += OnExitFinished;
    }

    /// <summary>
    /// Screen zoom's viewport now, from the display's points to the whole display (Mac screenViewport), or null without screen
    /// zoom. The drawings, the selection, the effect layer and the caret are shown through it.
    /// </summary>
    public Viewport? View => _view;

    /// <summary>Whether the pointer must be followed: screen zoom or the lens shows on this display.</summary>
    public bool TracksPointer => _screenShown || _lensShown;

    /// <summary>A ramp is moving: <see cref="Step"/> wants every composition frame.</summary>
    public bool NeedsFrames => _shown.NeedsFrames;

    /// <summary>
    /// Development only (live takes): an entrance or exit ramp exists (Mac isRampingScreenZoom, also while an entrance waits for
    /// the first zoomed frame), and whether it is the exit (Mac isExitingScreenZoom).
    /// </summary>
    public (bool Ramping, bool Exiting) Ramp => (_shown.IsRampingScreenZoom, _shown.IsExitingScreenZoom);

    /// <summary>Development only (live check): what the display shows of zoom now.</summary>
    public DisplayedZoom? Shown => _reported;

    /// <summary>
    /// Follows the published workspace (Mac synchronize then refresh): the document's presentation with an entrance ramp, a
    /// scroll that retargets one, an exit ramp or an immediate stop, and the elements that the zoomed pictures protect.
    /// </summary>
    public void Present(CanvasFrame frame, ElementList elements, bool editingText)
    {
        if (_disposed)
        {
            return;
        }
        PresentationSettings settings = frame.Display?.Document.Presentation ?? new PresentationSettings(PresentationMode.Off, _shown.Magnification);
        WorkspaceSnapshot workspace = frame.Workspace;
        ulong cut = frame.Display?.PresentationCut ?? _cut;
        ulong entrance = workspace.ZoomEntrance?.Serial ?? _entrance;
        if (!_started)
        {
            // A canvas made while zoom runs (a rebuilt overlay) shows it as it is, without replaying motion.
            _started = true;
            _cut = cut;
            _entrance = entrance;
            _scrolls = workspace.ZoomScrollSerial;
            _shown.SetMode(settings.Mode);
            _shown.SetMagnification(settings.Zoom);
        }
        _hidden = workspace.WorkspaceHidden;
        _editingText = editingText;
        if (elements != _elements)
        {
            _elements = elements;
            _elementsChanged = true;
        }
        if (cut != _cut)
        {
            // Mac stopPresentationWithoutHistory: another display took over, the system suspended or zoom failed.
            _cut = cut;
            _shown.SetMode(settings.Mode);
            _shown.SetMagnification(settings.Zoom);
        }
        else if (workspace.ZoomScrollSerial != _scrolls && settings.Mode == PresentationMode.Screen && _shown.Mode == PresentationMode.Screen
                 && !_shown.IsExitingScreenZoom)
        {
            // Mac scrollScreenZoom: a running entrance eases on toward the scrolled zoom; otherwise the zoom changes at once.
            if (!_shown.RetargetZoomRamp(settings.Zoom))
            {
                _shown.SetMagnification(settings.Zoom);
            }
        }
        else
        {
            double? enteringFrom = null;
            if (entrance != _entrance && workspace.ZoomEntrance?.Display == _display)
            {
                enteringFrom = _shown.DisplayedScreenZoomFactor ?? 1;
            }
            _shown.Apply(settings, enteringFrom, workspace.AnimatesScreenZoom, ZoomVisible);
        }
        _entrance = entrance;
        _scrolls = workspace.ZoomScrollSerial;
        Refresh();
    }

    /// <summary>
    /// The canvas changed between frames: the zoomed pictures protect <paramref name="elements"/>, and text editing holds the
    /// viewport still (Mac freezeViewportForText).
    /// </summary>
    public void Update(ElementList elements, bool editingText)
    {
        if (_disposed || (elements == _elements && editingText == _editingText))
        {
            return;
        }
        if (elements != _elements)
        {
            _elements = elements;
            _elementsChanged = true;
        }
        _editingText = editingText;
        if (_screenShown || _lensShown)
        {
            Refresh();
        }
    }

    /// <summary>
    /// The pointer is at <paramref name="desktop"/> (physical desktop pixels): the viewport and the lens follow it.
    /// <paramref name="seen"/> is when the hook saw the move (Stopwatch ticks), or 0.
    /// </summary>
    public void Track(System.Drawing.Point desktop, long seen = 0)
    {
        if (_disposed)
        {
            return;
        }
        Point local = _context.Display.ToLocal(new Point(desktop.X, desktop.Y));
        if (_pointerKnown && local == _pointer)
        {
            return;
        }
        _pointer = local;
        _pointerKnown = true;
        if (TracksPointer)
        {
            Viewport? before = _view;
            Refresh();
            if (seen != 0 && _view is not null && _view != before)
            {
                _statistics.Moved(System.Diagnostics.Stopwatch.GetElapsedTime(seen).TotalMilliseconds);
            }
        }
    }

    /// <summary>Development only (zoom measurements): the counters so far, with the presenters' own.</summary>
    public ZoomStatistics Statistics(long viewRedraws) => _statistics.With(_screen?.Statistics.Frames ?? 0, _screen?.Statistics.Skipped ?? 0,
        _screen?.Statistics.LastFrameTime ?? TimeSpan.Zero, _lens?.Statistics.Frames ?? 0, viewRedraws);

    /// <summary>Development only (measurements): the zoomed screen's frames drawn and not drawn, while it exists.</summary>
    public EffectsStatistics? ScreenStatistics => _screen?.Statistics;

    /// <summary>Moves a ramp to <paramref name="now"/> seconds; false once nothing moves.</summary>
    public bool Step(double now)
    {
        if (_disposed)
        {
            return false;
        }
        bool moving = _shown.Step(now, stop: _hidden);
        _statistics.RampFrames++;
        Refresh();
        return moving;
    }

    /// <summary>A point of the overlay (display points) as the document has it: through a viewport frozen for text, else as is.</summary>
    public Point SourcePoint(Point output) => _view is { } view && _shown.IsFrozen ? view.SourcePoint(output) : output;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _shown.ExitFinished -= OnExitFinished;
        CloseLens();
        if (_screen is not null)
        {
            _screen.Changed -= OnScreenChanged;
            _screen.Dispose();
            _screen = null;
        }
        _factory.DisplayedZoom?.Set(_display, null);
    }

    /// <summary>Mac screenZoomIsVisible: a zoomed frame is on screen.</summary>
    private bool ZoomVisible => !_hidden && _screenShown && _screenPresented;

    /// <summary>Shows what the displayed state asks for: the zoomed screen, the viewport, the lens, and what the app reads.</summary>
    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }
        if (_elementsChanged)
        {
            _elementsChanged = false;
            _screenEffects = null;
            _lensEffects = null;
        }
        bool screen = _shown.Mode == PresentationMode.Screen && !_hidden;
        bool lens = _shown.Mode == PresentationMode.Lens && !_hidden;
        if (screen || lens)
        {
            EnsurePointer();
        }
        else
        {
            // Nothing follows the pointer now, so its moves no longer arrive: the next zoom or lens reads where it is then, as
            // the Mac reads NSEvent.mouseLocation on every refresh.
            _pointerKnown = false;
        }
        RefreshScreen(screen);
        RefreshLens(lens);
        Report();
        _factory.Track(_canvas, TracksPointer);
        if (_shown.NeedsFrames)
        {
            _factory.Animate(_canvas);
        }
    }

    private void RefreshScreen(bool screen)
    {
        Viewport? view = null;
        if (screen)
        {
            if (_editingText && !_shown.IsFrozen)
            {
                _shown.FreezeViewportForText(_size, _pointer);
            }
            else if (!_editingText && _shown.IsFrozen)
            {
                _shown.ReleaseTextViewport();
            }
            view = _shown.ScreenViewport(_size, _pointer);
        }
        else if (_shown.IsFrozen)
        {
            _shown.ReleaseTextViewport();
        }
        if (view is { } shownView && EnsureScreen() is { } presenter)
        {
            if (!_screenShown)
            {
                _screenShown = true;
                _screenPresented = false;
                _failureReported = false;
            }
            _screenEffects ??= EffectList.From(Protection(_elements), _scale, _pixelWidth, _pixelHeight);
            try
            {
                presenter.Show(new CapturedView(_screenEffects, Crop(shownView.Source)));
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
            {
                Fail(PresentationMode.Screen, ZoomMessages.Drawing(PresentationMode.Screen), error.ToString());
            }
        }
        else if (_screenShown)
        {
            _screenShown = false;
            _screenPresented = false;
            _screen?.Show(null);
            view = null;
        }
        if (view != _view)
        {
            _view = view;
            _viewChanged();
        }
    }

    private void RefreshLens(bool lens)
    {
        if (!lens)
        {
            CloseLens();
            return;
        }
        if (EnsureLens() is not { } window || _lens is not { } presenter)
        {
            return;
        }
        if (!_lensShown)
        {
            _lensShown = true;
            _failureReported = false;
        }
        // Mac renderLens: the lens shows only while the pointer is on its display.
        if (!(_pointer.X >= 0 && _pointer.Y >= 0 && _pointer.X < _size.Width && _pointer.Y < _size.Height))
        {
            window.Hide();
            return;
        }
        try
        {
            int size = (int)Math.Round(PointerLens.Diameter * _scale);
            Point origin = PointerLens.Origin(_pointer, _context.Display.WorkArea);
            Point corner = _context.Display.ToDesktop(origin);
            var frame = PixelRect.FromSize((int)Math.Round(corner.X), (int)Math.Round(corner.Y), size, size);
            Viewport source = PointerLens.SourceViewport(_pointer, _size, _shown.Magnification);
            Point guide = source.OutputPoint(_pointer);
            double pixels = size / PointerLens.Diameter;
            if (presenter.PixelWidth != size)
            {
                presenter.Resize(size, size);
            }
            _lensEffects ??= EffectList.From(_elements, _scale, _pixelWidth, _pixelHeight, includeImageLayers: true);
            presenter.Show(new CapturedView(_lensEffects, Crop(source.Source)));
            // Mac renderLens: the lens shows only once capture has a frame, and then fades in with its picture.
            if (_lensReady)
            {
                window.Show(frame, _scale, new Vector2((float)(guide.X * pixels), (float)(guide.Y * pixels)));
            }
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException or CanvasError)
        {
            Fail(PresentationMode.Lens, ZoomMessages.Drawing(PresentationMode.Lens), error.ToString());
        }
    }

    /// <summary>The view's crop in the captured frame's pixels.</summary>
    private Rect Crop(Rect source) => new(source.X * _scale, source.Y * _scale, source.Width * _scale, source.Height * _scale);

    /// <summary>Mac renderScreenZoom: the screen view shows covers and pixel effects, but not pasted images, which the canvas draws.</summary>
    private static ElementList Protection(ElementList elements)
    {
        int count = 0;
        foreach (ref readonly Element element in elements)
        {
            if (element.Tool.IsProtection)
            {
                count++;
            }
        }
        if (count == elements.Count)
        {
            return elements;
        }
        var kept = new Element[count];
        int index = 0;
        foreach (ref readonly Element element in elements)
        {
            if (element.Tool.IsProtection)
            {
                kept[index++] = element;
            }
        }
        return ElementList.Create(kept);
    }

    private void EnsurePointer()
    {
        if (_pointerKnown)
        {
            return;
        }
        if (_factory.ReferencePointer is { } scripted)
        {
            System.Drawing.Point point = scripted();
            _pointer = _context.Display.ToLocal(new Point(point.X, point.Y));
        }
        else if (Shell.WindowChrome.TryCursor() is { } cursor)
        {
            _pointer = _context.Display.ToLocal(new Point(cursor.X, cursor.Y));
        }
        else
        {
            _pointer = new Point(_size.Width / 2, _size.Height / 2);
        }
        _pointerKnown = true;
    }

    private CapturedViewPresenter? EnsureScreen()
    {
        if (_screen is not null)
        {
            return _screen;
        }
        if (_factory.Capture is not { } capture)
        {
            Fail(PresentationMode.Screen, ZoomMessages.NoCapture(PresentationMode.Screen), null);
            return null;
        }
        try
        {
            _screen = CapturedViewPresenter.Create(new CapturedViewOptions
            {
                Compositor = _context.Compositor,
                Root = _content,
                AtBottom = true,
                Capture = capture,
                Display = _context.Overlay.Display,
                PixelWidth = _pixelWidth,
                PixelHeight = _pixelHeight,
            });
            _screen.Changed += OnScreenChanged;
            return _screen;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            Fail(PresentationMode.Screen, ZoomMessages.Drawing(PresentationMode.Screen), error.ToString());
            return null;
        }
    }

    private LensWindow? EnsureLens()
    {
        if (_lensWindow is not null)
        {
            return _lensWindow;
        }
        if (_factory.Capture is not { } capture || _factory.Registry is not { } registry)
        {
            Fail(PresentationMode.Lens, ZoomMessages.NoCapture(PresentationMode.Lens), null);
            return null;
        }
        try
        {
            RGBA accent = DrawingCanvasFactory.Accent;
            _lensWindow = new LensWindow(registry, _context.Compositor, new LensWindowOptions
            {
                Accent = Windows.UI.Color.FromArgb(255, (byte)Math.Round(accent.Red * 255), (byte)Math.Round(accent.Green * 255),
                                                   (byte)Math.Round(accent.Blue * 255)),
                CornerRadius = PointerLens.CornerRadius,
                BorderWidth = PointerLens.BorderWidth,
                CrosshairArm = PointerLens.CrosshairArm,
                FadeDuration = TimeSpan.FromSeconds(PointerLens.FadeInDuration),
                FadeControl1 = new Vector2((float)Annotator.Core.Motion.MotionCurves.Fade.X1, (float)Annotator.Core.Motion.MotionCurves.Fade.Y1),
                FadeControl2 = new Vector2((float)Annotator.Core.Motion.MotionCurves.Fade.X2, (float)Annotator.Core.Motion.MotionCurves.Fade.Y2),
            });
            int size = (int)Math.Round(PointerLens.Diameter * _scale);
            _lens = CapturedViewPresenter.Create(new CapturedViewOptions
            {
                Compositor = _context.Compositor,
                Root = _lensWindow.Content,
                AtBottom = true,
                Capture = capture,
                Display = _context.Overlay.Display,
                PixelWidth = size,
                PixelHeight = size,
            });
            _lens.Changed += OnLensChanged;
            return _lensWindow;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            CloseLens();
            Fail(PresentationMode.Lens, ZoomMessages.Drawing(PresentationMode.Lens), error.ToString());
            return null;
        }
    }

    /// <summary>The lens window goes with the lens, so the next lens fades in again (Mac clears its lens panel).</summary>
    private void CloseLens()
    {
        _lensShown = false;
        _lensReady = false;
        if (_lens is not null)
        {
            _lens.Changed -= OnLensChanged;
            _lens.Dispose();
            _lens = null;
        }
        _lensWindow?.Dispose();
        _lensWindow = null;
    }

    private void OnScreenChanged(object? sender, EventArgs args)
    {
        if (_disposed || _screen is not { } screen || !_screenShown)
        {
            return;
        }
        if (screen.State == CapturedViewState.Failed)
        {
            Fail(PresentationMode.Screen, ZoomMessages.Failure(PresentationMode.Screen, screen), screen.Failure?.ToString());
            return;
        }
        if (screen.Presented && !_screenPresented)
        {
            // Mac onPresented: the cover goes, an entrance that waited for the first zoomed frame starts, and the start is done.
            _screenPresented = true;
            _shown.FirstFramePresented();
            _factory.Report(new OverlayEvent.ZoomPresented(_display));
            if (_shown.NeedsFrames)
            {
                _factory.Animate(_canvas);
            }
        }
    }

    private void OnLensChanged(object? sender, EventArgs args)
    {
        if (_disposed || _lens is not { } lens || !_lensShown)
        {
            return;
        }
        if (lens.State == CapturedViewState.Failed)
        {
            Fail(PresentationMode.Lens, ZoomMessages.Failure(PresentationMode.Lens, lens), lens.Failure?.ToString());
        }
        else if (lens.Presented && !_lensReady)
        {
            // The lens's first picture is drawn: its window shows now.
            _lensReady = true;
            Refresh();
        }
    }

    /// <summary>Mac LivePresentation.fail: the app stops zoom without history and says why, once per start.</summary>
    private void Fail(PresentationMode mode, string? message, string? detail)
    {
        if (_failureReported || message is null)
        {
            return;
        }
        _failureReported = true;
        _factory.Report(new OverlayEvent.ZoomFailed(_display, mode, message, detail));
    }

    private void OnExitFinished() => Report();

    /// <summary>Tells the app what the display shows when it changed (the hooks' lifetime and the scroll that resumes an exit).</summary>
    private void Report()
    {
        DisplayedZoom? shown = _shown.Mode == PresentationMode.Off
            ? null
            : new DisplayedZoom(_shown.Mode, _shown.Magnification, _shown.IsExitingScreenZoom);
        if (shown == _reported)
        {
            return;
        }
        bool modeChanged = shown?.Mode != _reported?.Mode || shown?.Exiting != _reported?.Exiting;
        _reported = shown;
        _factory.DisplayedZoom?.Set(_display, shown);
        if (modeChanged)
        {
            _factory.Report(new OverlayEvent.ZoomShown(_display, shown));
        }
    }
}

/// <summary>Development only (zoom measurements, spike S-3): how fast zoom follows the pointer and how much it draws.</summary>
internal sealed class ZoomStatistics
{
    private const int Capacity = 4096;
    private readonly List<double> _latencies = [];

    /// <summary>Pointer moves that moved screen zoom's viewport.</summary>
    public long Moves { get; private set; }

    public long ScreenFrames { get; init; }

    public long ScreenSkipped { get; init; }

    public TimeSpan ScreenLastFrame { get; init; }

    public long LensFrames { get; init; }

    public long ViewRedraws { get; init; }

    public long RampFrames { get; set; }

    /// <summary>
    /// Milliseconds from the hook's pointer move to the zoomed frame and the drawings through the new viewport being
    /// submitted to composition, at <paramref name="fraction"/> of the recent moves; 0 without moves.
    /// </summary>
    public double Percentile(double fraction)
    {
        if (_latencies.Count == 0)
        {
            return 0;
        }
        double[] sorted = [.. _latencies];
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    public void Moved(double milliseconds)
    {
        Moves++;
        if (_latencies.Count == Capacity)
        {
            _latencies.RemoveAt(0);
        }
        _latencies.Add(milliseconds);
    }

    /// <summary>A copy with the presenters' counters.</summary>
    public ZoomStatistics With(long screenFrames, long screenSkipped, TimeSpan screenLastFrame, long lensFrames, long viewRedraws)
    {
        var copy = new ZoomStatistics
        {
            ScreenFrames = screenFrames,
            ScreenSkipped = screenSkipped,
            ScreenLastFrame = screenLastFrame,
            LensFrames = lensFrames,
            ViewRedraws = viewRedraws,
            RampFrames = RampFrames,
        };
        copy.Moves = Moves;
        copy._latencies.AddRange(_latencies);
        return copy;
    }
}

/// <summary>What the person reads when zoom or the lens stops working (Mac reportCaptureFailure with a presentation mode).</summary>
internal static class ZoomMessages
{
    public static string Name(PresentationMode mode) => mode == PresentationMode.Lens ? "The pointer lens" : "Live zoom";

    private static string Again(PresentationMode mode) => mode == PresentationMode.Lens ? "Turn on the lens again." : "Start zoom again.";

    /// <summary>The overlay could not draw the zoomed screen.</summary>
    public static string Drawing(PresentationMode mode) => $"{Name(mode)} stopped because Annotator could not draw the zoomed screen. {Again(mode)}";

    /// <summary>The app has no capture service (it could not start).</summary>
    public static string NoCapture(PresentationMode mode) => $"{Name(mode)} needs screen capture, which could not start. Restart Annotator and try again.";

    /// <summary>Why a captured view failed; a display change is not reported, as the overlays are built again.</summary>
    public static string? Failure(PresentationMode mode, CapturedViewPresenter view) => view.CaptureState switch
    {
        CaptureState.Failed { Failure.Kind: CaptureFailureKind.DisplayChanged or CaptureFailureKind.DisplayUnavailable } => null,
        CaptureState.Failed { Failure.Kind: CaptureFailureKind.AccessNeeded } => Workspace.Capture.ScreenAccessText.NotAsked(Feature(mode)),
        CaptureState.Failed { Failure.Kind: CaptureFailureKind.AccessDenied or CaptureFailureKind.BorderRequired } failed =>
            Workspace.Capture.ScreenAccessText.Refused(EffectsMessages.Refusal(failed.Failure.Kind), Feature(mode)),
        CaptureState.Failed { Failure.Kind: CaptureFailureKind.Unsupported } => $"{Name(mode)} needs screen capture, which this version of Windows does not have.",
        CaptureState.Failed { Failure.Kind: CaptureFailureKind.ExclusionFailed } =>
            $"{Name(mode)} stopped because Annotator could not keep its own windows out of the zoomed screen. {Again(mode)}",
        CaptureState.Failed => $"{Name(mode)} stopped because screen capture stopped. {Again(mode)}",
        _ => Drawing(mode),
    };

    private static Workspace.Capture.ScreenAccessFeature Feature(PresentationMode mode) =>
        mode == PresentationMode.Lens ? Workspace.Capture.ScreenAccessFeature.Lens : Workspace.Capture.ScreenAccessFeature.ScreenZoom;
}
