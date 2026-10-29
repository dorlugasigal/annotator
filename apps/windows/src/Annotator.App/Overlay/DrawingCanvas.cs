using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Accessibility;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Input;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Export;
using Annotator.Rendering.Presentation;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using CanvasButtons = Annotator.Workspace.Canvas.PointerButtons;
using CanvasSample = Annotator.Workspace.Canvas.PointerSample;
using PlatformButtons = Annotator.Platform.Input.PointerButtons;
using PlatformSample = Annotator.Platform.Input.PointerSample;

namespace Annotator.App.Overlay;

/// <summary>
/// One display's drawing canvas on the overlay thread: the platform-neutral <see cref="CanvasController"/> for gestures
/// and keys, <see cref="CanvasScene"/> for the scene and live layers, and the <see cref="OverlayPresenter"/> that draws
/// them on the overlay. Above it, in the Mac's order (WIN-OVR-04), an <see cref="EffectsPresenter"/> shows the Blur,
/// Pixelate and Distort regions from the app's capture service, a second presenter draws the selection above them, and the
/// Use apps button comes last. Pointer messages draw at once on this thread (the controller's <c>Changed</c> drives the
/// presenters); edits reach the workspace through the channel and come back as frames. Nothing runs while nothing
/// changes: transitions get frames from the factory's frame clock only while they move.
/// </summary>
internal sealed class DrawingCanvas : IOverlayCanvas
{
    private const int StackSamples = 64;

    /// <summary>A press this soon after the context menu closed without a choice is the click that closed it.</summary>
    private const long MenuDismissMilliseconds = 400;

    private static readonly HCURSOR s_arrow = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW);

    /// <summary>
    /// Mac DrawingCanvas fills the blackboard with calibrated white 0.08, which is #1A1A1A in sRGB (UX reference
    /// board-blackboard); the whiteboard is white.
    /// </summary>
    private const byte BlackboardLevel = 0x1A;

    private readonly OverlayCanvasContext _context;
    private readonly DrawingCanvasFactory _factory;
    private readonly CanvasController _controller;
    private readonly CanvasScene _scene;
    private readonly OverlayPresenter _presenter;
    private readonly EffectsPresenter? _effects;
    private readonly OverlayPresenter? _adornments;
    private readonly CanvasTextEditorHost _text;
    private readonly CanvasZoom _zoom;
    private readonly OverlayHud _hud;
    private readonly PointerEffectsLayer _pointerEffects;
    private readonly HintHud? _hint;
    private readonly OverlayAccessibility _accessibility;
    private readonly SpriteVisual _board;
    private readonly ContainerVisual _content;
    /// <summary>The pointers pressed on this canvas now; while there is one, the factory's compositor boost is held.</summary>
    private readonly List<uint> _pressed = [];
    private GraphicsDevices? _devices;
    private readonly DisplayId _display;
    private readonly double _scale;
    private readonly Rect _bounds;
    private CompositionColorBrush? _white;
    private CompositionColorBrush? _black;
    private Board _shownBoard = Board.Desktop;
    private bool _updating;
    private bool _drawing;
    private CanvasCursor _shownCursor = CanvasCursor.Arrow;
    private bool _pointerInside;
    private uint _lastTime;
    private long _timeEpoch;
    private long? _menuClosedAt;
    private CanvasMenu? _pendingMenu;
    private bool _openingFromKeyboard;
    private uint? _ignoredPointer;
    private bool _hudShown;
    private bool _overHud;
    private uint? _hudPress;
    /// <summary>Whether <see cref="_hudPress"/> is on the tip's ✕ rather than the Use apps button.</summary>
    private bool _hudPressCloses;
    private WorkspaceSnapshot? _hintWorkspace;
    private DisplaySnapshot? _hintDisplay;
    private (bool TextEditor, bool ActiveEdit, bool Shown, bool Closable) _hinted;
    private bool _hintShown;
    private bool _hintClosable;
    private readonly HintSchedule _hintSchedule = new();
    private readonly System.Diagnostics.Stopwatch _hintClock = System.Diagnostics.Stopwatch.StartNew();
    private Windows.System.DispatcherQueueTimer? _hintTimer;
    private bool _wasVisible;
    private string _summary = string.Empty;
    private bool _summaryDrawing;
    private (int Objects, int Selected, bool Drawing, bool Button) _summarized = (-1, -1, false, false);
    private bool _workspaceShown;
    private bool _pixelsAvailable;
    private ElementList _effectElements;
    private Element? _effectDraft;
    private (bool Regions, bool Available, string? Failure) _effectsReported;
    private SceneSnapshot? _splitScene;
    private SceneSnapshot _sceneBody = SceneSnapshot.Empty;
    private SceneSnapshot _sceneSelection = SceneSnapshot.Empty;
    private bool _scripting;
    private EditorNode? _editorNode;
    private bool _hasFocus;
    private int _batch;
    private bool _renderPending;
    private bool _flushQueued;
    private bool _disposed;

    public DrawingCanvas(OverlayCanvasContext context, DrawingCanvasFactory factory)
    {
        _context = context;
        _factory = factory;
        _display = context.Display.Id;
        _scale = context.Display.Scale;
        _bounds = context.Display.Bounds;
        Compositor compositor = context.Compositor;
        // Everything the canvas shows goes into one container, which hides while the workspace is hidden; the visibility
        // motion's snapshots play above it (Mac: the canvas panel is ordered out while its snapshots animate).
        _content = compositor.CreateContainerVisual();
        _content.RelativeSizeAdjustment = Vector2.One;
        context.Overlay.Root.Children.InsertAtBottom(_content);
        ContainerVisual root = _content;
        // The whiteboard and blackboard sit below the drawings as a plain composition color, with nothing to draw.
        _board = compositor.CreateSpriteVisual();
        _board.RelativeSizeAdjustment = Vector2.One;
        _board.IsVisible = false;
        root.Children.InsertAtBottom(_board);
        try
        {
            _presenter = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = compositor,
                Root = root,
                Devices = context.Devices,
                Scale = _scale,
                PixelWidth = context.Overlay.Display.Bounds.Width,
                PixelHeight = context.Overlay.Display.Bounds.Height,
                Colors = factory.Colors,
            });
        }
        catch
        {
            root.Children.Remove(_board);
            _board.Dispose();
            context.Overlay.Root.Children.Remove(_content);
            _content.Dispose();
            throw;
        }
        _devices = context.Devices;
        _presenter.StateChanged += OnPresenterStateChanged;
        double doubleClickTime = PInvoke.GetDoubleClickTime();
        // The double-click area in device-independent pixels, which are points (WIN-CORE-06).
        var doubleClickSize = new Size(PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXDOUBLECLK, 96),
                                       PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CYDOUBLECLK, 96));
        // The text editor's caret sits above the drawings and below the effect layer, as the Mac's text view is part of the
        // canvas, below its effects panel.
        _text = new CanvasTextEditorHost(context, factory, root, doubleClickTime, doubleClickSize, RequestRender);
        _hasFocus = PInvoke.GetFocus() == new HWND(context.Overlay.Handle);
        // Mac LivePresentation levels (WIN-OVR-04): the canvas, then the effect layer, then the selection above it, then the
        // Use apps button.
        _effects = CreateEffects(context, root, factory);
        _adornments = CreateAdornments(context, root, factory);
        // The Use apps button sits above the drawings, as the Mac's button is a subview above its canvas.
        _hud = new OverlayHud(compositor, root, context.Devices, factory.Colors, _scale);
        _hud.Show(CanvasHud.UseAppsButton(factory.IsDark), CanvasHud.UseAppsFrame);
        // The on-screen tip below it (Mac WorkspaceHintView), unless this is a reference capture.
        _hint = factory.ShowsHints ? new HintHud(compositor, root, context.Devices, factory.Colors, _scale, factory.TextMeasurer) : null;
        // Spotlight and Cursor halo come last, as the Mac draws them in its topmost overlay panel.
        _pointerEffects = new PointerEffectsLayer(compositor, root, context.Devices, _scale, context.Overlay.Display.Bounds.Width,
            context.Overlay.Display.Bounds.Height, factory.Colors);
        factory.PointerEffects.Attach(_pointerEffects, context.Overlay.Display.Bounds);
        _accessibility = new OverlayAccessibility(context.Overlay.Handle, context.Overlay.Display.Bounds, AccessibleTree());
        context.Overlay.Accessibility = _accessibility;
        // Live zoom: the zoomed screen goes below the board, and the drawings, effects and caret follow its viewport.
        _zoom = new CanvasZoom(context, factory, this, root, OnZoomViewChanged);
        _scene = new CanvasScene(factory.TextMeasurer);
        _controller = new CanvasController(_display, context.Channel, new CanvasOptions
        {
            TextMeasurer = factory.TextMeasurer,
            TextEditor = _text,
            Scheduler = factory.Scheduler,
            Coordinator = factory.Coordinator,
            DoubleClickTime = doubleClickTime,
            DoubleClickSize = doubleClickSize,
            ReducesMotion = ReducesMotion,
        });
        _controller.Changed += OnChanged;
        _controller.Requested += OnRequested;
        _controller.ContextMenuRequested += OnContextMenuRequested;
    }

    /// <summary>The cursor this canvas shows now (0 for the arrow).</summary>
    public nint CursorHandle { get; private set; }

    public void Present(CanvasFrame frame)
    {
        if (_disposed)
        {
            return;
        }
        if (_presenter.State is PresenterState.Failed)
        {
            // A failure is reported once; each new frame tries again on the same devices.
            _presenter.Retry();
        }
        if (_adornments?.State is PresenterState.Failed)
        {
            _adornments.Retry();
        }
        _updating = true;
        try
        {
            _controller.Update(frame.Workspace, frame.Display, frame.Frame.InputBlocked);
        }
        finally
        {
            _updating = false;
        }
        _zoom.Present(frame, _controller.State.Elements, _controller.State.Text is not null);
        // Mac needsLocalPixels: effect regions read the screen only while the workspace shows, in Draw or Use apps.
        _workspaceShown = frame.Workspace.Surfaces != SurfaceMode.Hidden;
        ShowBoard(frame.Display?.Board ?? Board.Desktop);
        Render();
        // Hidden drawings stay rendered underneath, so a show ends by uncovering pixels that are already drawn.
        _content.IsVisible = !frame.Workspace.WorkspaceHidden;
        bool drawing = frame.Workspace.Surfaces == SurfaceMode.Draw;
        if (_drawing && !drawing)
        {
            _presenter.ReleaseIdleResources();
            _adornments?.ReleaseIdleResources();
            _pointerInside = false;
            ReleasePresses();
        }
        _drawing = drawing;
        // Mac HUDFader: the button fades while the overlay stays on screen, and changes at once when it appears or goes.
        bool visible = frame.Workspace.Surfaces != SurfaceMode.Hidden;
        bool showHud = CanvasHud.ShowsUseApps(frame.Workspace);
        bool animate = visible && _wasVisible;
        _hud.SetVisible(showHud, animate, ReducesMotion());
        _wasVisible = visible;
        if (_hud.Failure is { } failure && showHud && !_hudShown)
        {
            _factory.Report(new OverlayEvent.Failure("Show the Use apps button", failure));
        }
        _hudShown = showHud;
        if (!showHud)
        {
            _hudPress = null;
            _overHud = false;
        }
        UpdateHint(frame.Workspace, frame.Display, animate);
        UpdateAccessibility(frame);
        AfterChange();
    }

    public void DevicesChanged(GraphicsDevices? devices)
    {
        if (!_disposed)
        {
            _devices = devices;
            _presenter.ReplaceDevices(devices);
            _adornments?.ReplaceDevices(devices);
            _hud.ReplaceDevices(devices);
            _pointerEffects.ReplaceDevices(devices);
            _factory.PointerEffects.DevicesChanged();
            _hint?.ReplaceDevices(devices);
        }
    }

    /// <summary>The workspace asked to resume live effects (Mac presentation?.retry()): an effect layer whose drawing failed tries again.</summary>
    public void RetryEffects()
    {
        if (!_disposed && _effects is { State: EffectsState.Failed })
        {
            _effects.Retry();
            ReportEffects();
        }
    }

    /// <summary>Development only (effects measurements): the effect layer's frame counters, or null without capture.</summary>
    internal EffectsStatistics? EffectsStatistics => _effects?.Statistics;

    /// <summary>
    /// Development only (live takes): pointer input from the take's script. With
    /// <see cref="DrawingCanvasFactory.ReferenceScriptedInput"/>, the canvas takes no other pointer input.
    /// </summary>
    internal void ScriptPointer(in PointerInput input)
    {
        _scripting = true;
        try
        {
            OnPointer(in input);
        }
        finally
        {
            _scripting = false;
        }
    }

    /// <summary>
    /// Development only (UX reference capture): renders what this canvas shows now over <paramref name="backdrop"/>
    /// (the board, the scene and live layers and the Use apps button) at <paramref name="scale"/> pixels per point with
    /// the presenter's renderer, and lists the objects it shows.
    /// </summary>
    internal CanvasReference Reference(SceneSnapshot backdrop, double scale)
    {
        GraphicsDevices devices = _devices ?? throw new InvalidOperationException("The canvas has no graphics devices.");
        using var exporter = new PngExporter(devices, _factory.TextMeasurer, _factory.Colors);
        var size = new Size(_bounds.Width, _bounds.Height);
        var layers = new List<SceneSnapshot> { backdrop };
        if (_shownBoard != Board.Desktop)
        {
            double level = _shownBoard == Board.Whiteboard ? 1 : BlackboardLevel / 255.0;
            layers.Add(SceneSnapshot.Create([new DisplayListBuilder()
                .Add(DisplayItem.FillRect(new Rect(0, 0, size.Width, size.Height), Paint.Solid(new RGBA(level, level, level))))
                .ToList()]));
        }
        layers.Add(_scene.Scene);
        layers.Add(_scene.Live);
        if (_hud.IsVisible)
        {
            layers.Add(SceneSnapshot.Create([CanvasHud.UseAppsButton(_factory.IsDark)]));
        }
        CanvasLiveState state = _controller.State;
        // While text is edited the canvas hides the edited object and its selection, but its document holds both (Mac).
        if (state.Text is not null && _controller.Document is { } edited)
        {
            return new CanvasReference(exporter.Render(layers, size, scale), edited.Elements, edited.Selection, null, size);
        }
        return new CanvasReference(exporter.Render(layers, size, scale), state.ComposeElements(), state.Selection, state.Draft?.Id, size);
    }

    /// <summary>The display this canvas covers.</summary>
    internal DisplayInfo Display => _context.Overlay.Display;

    /// <summary>What the shared region shows of this canvas now (Mac elementsForSharing).</summary>
    internal SharedCanvasContent SharedContent() => _scene.Shared(_controller.State);

    /// <summary>The laser pointer's trail as this canvas last drew it, in its display's points, or null (the shared region draws it too).</summary>
    internal DisplayList? LaserTrail => _scene.Laser;

    /// <summary>The board the canvas shows under its drawings, or null for the desktop.</summary>
    internal RGBA? BoardColor => _shownBoard switch
    {
        Board.Desktop => null,
        Board.Whiteboard => new RGBA(1, 1, 1),
        _ => new RGBA(BlackboardLevel / 255.0, BlackboardLevel / 255.0, BlackboardLevel / 255.0),
    };
    /// <summary>
    /// Draws what a new accent color or app theme changes: the drawings that paint with system colors (the selection, label
    /// backgrounds), the Use apps button and the tip. Other drawings keep their pixels, which are the same in every theme.
    /// </summary>
    public void Redraw()
    {
        if (!_disposed)
        {
            _presenter.RedrawSystemColors();
            _adornments?.RedrawSystemColors();
            _hud.Show(CanvasHud.UseAppsButton(_factory.IsDark), CanvasHud.UseAppsFrame);
            _hint?.Invalidate();
        }
    }

    /// <summary>Development only (live check): what this canvas shows now over white, as a PNG at <paramref name="scale"/> pixels per point.</summary>
    internal byte[] Picture(double scale)
    {
        GraphicsDevices devices = _devices ?? throw new InvalidOperationException("The canvas has no graphics devices.");
        SceneSnapshot white = SceneSnapshot.Create([new DisplayListBuilder()
            .Add(DisplayItem.FillRect(new Rect(0, 0, _bounds.Width, _bounds.Height), Paint.White))
            .ToList()]);
        CanvasReference picture = Reference(white, scale);
        using var exporter = new PngExporter(devices, _factory.TextMeasurer, _factory.Colors);
        return exporter.Encode(picture.Image);
    }

    /// <summary>Moves this canvas's transitions and zoom ramp to <paramref name="now"/>; false once none is left.</summary>
    public bool AdvanceTransitions(double now)
    {
        if (_disposed)
        {
            return false;
        }
        bool canvas = _controller.State.HasTransitions && _controller.AdvanceTransitions(now);
        bool zoom = _zoom.NeedsFrames && _zoom.Step(now);
        return canvas || zoom;
    }

    /// <summary>
    /// The pointer moved to <paramref name="desktop"/> (physical desktop pixels) while this canvas shows zoom or the lens;
    /// <paramref name="seen"/> is when the hook saw it (Stopwatch ticks).
    /// </summary>
    public void TrackPointer(System.Drawing.Point desktop, long seen)
    {
        if (!_disposed)
        {
            _zoom.Track(desktop, seen);
        }
    }

    /// <summary>Development only (live check): what this display shows of zoom now.</summary>
    internal Annotator.Workspace.Zoom.DisplayedZoom? ZoomShown => _zoom.Shown;

    /// <summary>Screen zoom's viewport on this display, or null (the shared region shows the display through it).</summary>
    internal Viewport? ZoomView => _zoom.View;

    /// <summary>Development only (zoom measurements): how fast zoom followed the pointer and how much it drew.</summary>
    internal ZoomStatistics ZoomStatistics => _zoom.Statistics(_presenter.Statistics.ViewRedraws + (_adornments?.Statistics.ViewRedraws ?? 0));

    /// <summary>Development only (measurements): the zoomed screen's frame counters, or null without screen zoom.</summary>
    internal EffectsStatistics? ZoomScreenStatistics => _zoom.ScreenStatistics;

    /// <summary>Development only (live takes): whether screen zoom ramps on this display, and whether that ramp is the exit.</summary>
    internal (bool Ramping, bool Exiting) ZoomRamp => _zoom.Ramp;

    public void OnPointer(in PointerInput input)
    {
        if (_disposed || (_factory.ReferenceScriptedInput && !_scripting))
        {
            return;
        }
        HandlePointer(in input);
        // After the drawing, so a press's first segment never waits for the request to Windows.
        TrackPress(in input);
    }

    /// <summary>
    /// While a pointer is pressed on this canvas, the factory's compositor boost is held: a display with Dynamic refresh rate
    /// shows the stroke at its higher rate (WIN-PERF-01). A cancel, leaving Draw or the canvas going ends the press.
    /// </summary>
    private void TrackPress(in PointerInput input)
    {
        switch (input.Kind)
        {
            case PointerInputKind.Down when !_disposed && !_pressed.Contains(input.PointerId):
                _pressed.Add(input.PointerId);
                if (_pressed.Count == 1)
                {
                    _factory.Boost.Hold();
                }
                break;
            case PointerInputKind.Up when input.Buttons == PlatformButtons.None:
                if (_pressed.Remove(input.PointerId) && _pressed.Count == 0)
                {
                    _factory.Boost.Release();
                }
                break;
            case PointerInputKind.Canceled:
                ReleasePresses();
                break;
        }
    }

    private void ReleasePresses()
    {
        if (_pressed.Count > 0)
        {
            _pressed.Clear();
            _factory.Boost.Release();
        }
    }

    private void HandlePointer(in PointerInput input)
    {
        if (input.Device is PointerDeviceKind.Pen or PointerDeviceKind.Touch && input.Kind is PointerInputKind.Move or PointerInputKind.Down
            && !input.Samples.IsEmpty)
        {
            // Raw mouse input does not carry pen and touch, so Spotlight and Cursor halo follow them from the canvas.
            Vector2 at = input.Current.Position;
            PixelRect bounds = _context.Overlay.Display.Bounds;
            _factory.PointerEffects.NoteCanvasPointer(new System.Drawing.Point(bounds.Left + (int)MathF.Round(at.X), bounds.Top + (int)MathF.Round(at.Y)),
                input.Kind == PointerInputKind.Down);
        }
        if (HandleHud(in input))
        {
            return;
        }
        CanvasButtons changed = Buttons(input.ChangedButton);
        PointerPhase phase;
        switch (input.Kind)
        {
            case PointerInputKind.Enter:
                _pointerInside = true;
                ApplyCursor();
                if (input.Buttons != PlatformButtons.None)
                {
                    return;
                }
                phase = PointerPhase.Move;
                break;
            case PointerInputKind.Move:
                _pointerInside = true;
                phase = PointerPhase.Move;
                break;
            case PointerInputKind.Down:
                _pointerInside = true;
                if (changed is not (CanvasButtons.Primary or CanvasButtons.Secondary))
                {
                    return;
                }
                if (_menuClosedAt is { } closed && Environment.TickCount64 - closed < MenuDismissMilliseconds)
                {
                    // Like the Mac, the click that closes the context menu does not draw.
                    _menuClosedAt = null;
                    _ignoredPointer = input.PointerId;
                    return;
                }
                phase = PointerPhase.Down;
                break;
            case PointerInputKind.Up:
                if (changed == CanvasButtons.Secondary && _pendingMenu is { } pending)
                {
                    // Windows shows a context menu on the release: one shown on the press would close on it.
                    _pendingMenu = null;
                    _factory.Queue.TryEnqueue(() => ShowContextMenu(pending));
                    return;
                }
                if (changed != CanvasButtons.Primary)
                {
                    return;
                }
                phase = PointerPhase.Up;
                break;
            case PointerInputKind.Leave:
                _pointerInside = false;
                phase = PointerPhase.Leave;
                break;
            case PointerInputKind.Canceled:
                _pendingMenu = null;
                phase = PointerPhase.Cancel;
                break;
            case PointerInputKind.Wheel:
                // Mac handleLocalZoomScroll: Alt+scroll that reaches the canvas changes screen zoom; the app decides.
                if (Annotator.Workspace.Zoom.ZoomScroll.Delta(input.WheelDelta, input.Modifiers.HasFlag(ModifierKeys.Alt),
                        input.Modifiers.HasFlag(ModifierKeys.Control), input.Modifiers.HasFlag(ModifierKeys.Shift),
                        input.Modifiers.HasFlag(ModifierKeys.Windows)) is { } delta)
                {
                    _factory.Report(new OverlayEvent.ZoomScroll(_display, delta));
                }
                return;
            default:
                return;
        }
        if (_ignoredPointer == input.PointerId && phase != PointerPhase.Leave)
        {
            if (phase is PointerPhase.Up or PointerPhase.Cancel)
            {
                _ignoredPointer = null;
            }
            return;
        }
        ReadOnlySpan<PlatformSample> source = input.Samples;
        if (_zoom.TracksPointer && source.Length > 0)
        {
            // Screen zoom's viewport is anchored at this sample, so the point under the pointer is the document's point.
            Vector2 last = source[^1].Position;
            _zoom.Track(new System.Drawing.Point((int)Math.Round(_context.Display.DesktopFrame.X + last.X),
                                                 (int)Math.Round(_context.Display.DesktopFrame.Y + last.Y)));
        }
        CanvasSample[]? rented = null;
        Span<CanvasSample> samples = source.Length <= StackSamples
            ? stackalloc CanvasSample[source.Length]
            : (rented = ArrayPool<CanvasSample>.Shared.Rent(source.Length)).AsSpan(0, source.Length);
        try
        {
            for (int index = 0; index < source.Length; index++)
            {
                PlatformSample sample = source[index];
                // Device pixels from the display's top-left corner become points (WIN-CORE-06); a viewport held still for
                // text maps them back to the document.
                samples[index] = new CanvasSample(_zoom.SourcePoint(new Point(sample.Position.X / _scale, sample.Position.Y / _scale)),
                                                  input.HasPressure ? sample.Pressure : 1, Timestamp(sample.Time));
            }
            var pointer = new PointerEvent
            {
                Phase = phase,
                Device = input.Device switch
                {
                    PointerDeviceKind.Pen => PointerDevice.Pen,
                    PointerDeviceKind.Touch => PointerDevice.Touch,
                    _ => PointerDevice.Mouse,
                },
                PointerId = input.PointerId,
                Buttons = Buttons(input.Buttons),
                ChangedButton = changed,
                Modifiers = Modifiers(input.Modifiers),
                HasPressure = input.HasPressure,
                Samples = samples,
            };
            // Like the Mac's text view above the canvas, the text being edited takes the presses, drags and clicks inside it.
            if (_text.Editor is { } editor)
            {
                _batch++;
                try
                {
                    if (editor.OnPointer(pointer))
                    {
                        return;
                    }
                }
                finally
                {
                    EndBatch();
                }
            }
            _controller.OnPointer(pointer);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<CanvasSample>.Shared.Return(rented);
            }
        }
    }

    public bool OnKey(in KeyInput input)
    {
        if (_disposed)
        {
            return false;
        }
        if (_text.Editor is { } editor)
        {
            // Every key but the system's Alt keys stays inside the text being edited, as in the Mac's text view.
            if (input.Kind == KeyInputKind.KeyUp || (input.Kind == KeyInputKind.Character && input.IsSystemKey))
            {
                return false;
            }
            _batch++;
            try
            {
                return input.Kind == KeyInputKind.Character
                    ? editor.OnCharacter(input.Character)
                    : editor.OnKey(new KeyEvent(input.VirtualKey, Modifiers(input.Modifiers), KeyText(input), input.IsRepeat));
            }
            finally
            {
                EndBatch();
            }
        }
        if (input.Kind != KeyInputKind.KeyDown)
        {
            return false;
        }
        if (IsContextMenuKey(in input))
        {
            // WIN-A11Y-02: Shift+F10 and the Menu key open the canvas menu for the selection, with Focus drawing tools.
            _openingFromKeyboard = true;
            try
            {
                return _controller.OpenKeyboardContextMenu(KeyboardMenuPoint());
            }
            finally
            {
                _openingFromKeyboard = false;
            }
        }
        if (_controller.OnKey(new KeyEvent(input.VirtualKey, Modifiers(input.Modifiers), KeyText(input), input.IsRepeat)))
        {
            return true;
        }
        if (input.VirtualKey == VirtualKeys.Escape && !input.IsRepeat && _drawing)
        {
            // Escape always releases input (WIN-A11Y-04), even while this canvas has no document to edit.
            _context.Channel.Send(new CanvasCommand(CanvasCommandKind.Escape, _display));
            return true;
        }
        return false;
    }

    public void OnFocusChanged(bool hasFocus)
    {
        if (_disposed)
        {
            return;
        }
        _hasFocus = hasFocus;
        _text.SetFocus(hasFocus);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        ReleasePresses();
        _controller.Changed -= OnChanged;
        _controller.Requested -= OnRequested;
        _controller.ContextMenuRequested -= OnContextMenuRequested;
        _factory.Removed(this);
        _zoom.Dispose();
        _controller.Dispose();
        _text.Dispose();
        _presenter.StateChanged -= OnPresenterStateChanged;
        _presenter.Dispose();
        if (_effects is not null)
        {
            _effects.PixelsAvailableChanged -= OnPixelsAvailableChanged;
            _effects.StateChanged -= OnEffectsStateChanged;
            _effects.Dispose();
            if (_effectsReported != default)
            {
                // The display's next canvas reports for itself; until then it shows no regions.
                _factory.Report(new OverlayEvent.Effects(_display, false, false, null, null));
            }
        }
        if (_adornments is not null)
        {
            _adornments.StateChanged -= OnPresenterStateChanged;
            _adornments.Dispose();
        }
        _hud.Dispose();
        _factory.PointerEffects.Detach(_pointerEffects);
        _pointerEffects.Dispose();
        _hintTimer?.Stop();
        _hint?.Dispose();
        _context.Overlay.Accessibility = null;
        _content.Children.Remove(_board);
        _board.Dispose();
        _context.Overlay.Root?.Children.Remove(_content);
        _content.Dispose();
        _white?.Dispose();
        _black?.Dispose();
        _context.Overlay.Cursor = 0;
        CursorHandle = 0;
    }

    private void OnChanged(CanvasChange change)
    {
        _factory.NoteArrow(this, _controller.ConstructingArrow);
        RequestRender();
    }

    /// <summary>
    /// Draws what changed now, or once at the end of the input the text editor is handling, or after an input method's edit
    /// (the editor and the controller each report a change of one keystroke). Present draws once after the controller took
    /// its frame.
    /// </summary>
    private void RequestRender()
    {
        if (_updating || _disposed)
        {
            return;
        }
        if (_batch > 0)
        {
            _renderPending = true;
            return;
        }
        if (_text.InputMethodIsEditing)
        {
            _renderPending = true;
            if (!_flushQueued)
            {
                _flushQueued = _factory.Queue.TryEnqueue(Windows.System.DispatcherQueuePriority.High, () =>
                {
                    _flushQueued = false;
                    FlushRender();
                });
            }
            return;
        }
        ShowChange();
    }

    private void EndBatch()
    {
        if (--_batch == 0)
        {
            FlushRender();
        }
    }

    private void FlushRender()
    {
        if (!_renderPending || _disposed)
        {
            return;
        }
        _renderPending = false;
        ShowChange();
    }

    /// <summary>A canvas change: draw it, update the summary (Mac refreshAccessibilitySummary) and the cursor and transitions.</summary>
    private void ShowChange()
    {
        Render();
        UpdateAccessibility();
        // Text editing and edits in progress change the tip's Escape line.
        UpdateHint(_hintWorkspace, _hintDisplay, animate: true);
        AfterChange();
    }

    /// <summary>Builds the layers from the controller's state and hands what changed to the presenters.</summary>
    private void Render()
    {
        try
        {
            CanvasLayers changed = _scene.Update(_controller.State, _bounds, pixelsAvailable: _pixelsAvailable, editor: _text.Decorations);
            if ((changed & CanvasLayers.Scene) != 0)
            {
                Split(_scene.Scene);
                _presenter.Show(_sceneBody, _scene.Live);
                _adornments?.ShowScene(_sceneSelection);
            }
            else if ((changed & CanvasLayers.Live) != 0)
            {
                _presenter.ShowLive(_scene.Live);
            }
        }
        catch (Exception error) when (error is NativeCallException or RenderFailure or CanvasError or InvalidOperationException)
        {
            _factory.Report(new OverlayEvent.Failure("Draw the annotations", error));
        }
        _renderPending = false;
        _zoom.Update(_controller.State.Elements, _controller.State.Text is not null);
        _text.UpdateCaret();
        UpdateEditorAccessibility();
        UpdateEffects();
        _factory.CanvasRendered(this);
    }

    /// <summary>
    /// Screen zoom's viewport changed (Mac DrawingCanvas under its screen view): the drawings, the selection, the effect layer
    /// and the caret show through it in the same composition frame as the zoomed screen.
    /// </summary>
    private void OnZoomViewChanged()
    {
        Viewport? view = _zoom.View;
        double zoom = view is { } shown ? shown.Output.Width / shown.Source.Width : 1;
        double x = view?.Source.X ?? 0, y = view?.Source.Y ?? 0;
        try
        {
            _presenter.SetView(zoom, x, y);
            _adornments?.SetView(zoom, x, y);
            _effects?.SetViewport(view is { } crop
                ? new Rect(crop.Source.X * _scale, crop.Source.Y * _scale, crop.Source.Width * _scale, crop.Source.Height * _scale)
                : null);
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            _factory.Report(new OverlayEvent.Failure("Show the drawings through live zoom", error));
        }
        _text.View = view;
        _text.UpdateCaret();
        // The shared region shows the display through the same viewport (Mac sourceBounds(through: screenViewport)).
        _factory.CanvasRendered(this);
    }

    /// <summary>
    /// The selection draws above the effect layer (WIN-OVR-04, Mac SelectionOverlayView): the canvas's presenter shows the
    /// scene without it and the adornment presenter shows it alone. Unchanged parts keep their snapshots.
    /// </summary>
    private void Split(SceneSnapshot scene)
    {
        if (ReferenceEquals(scene, _splitScene))
        {
            return;
        }
        _splitScene = scene;
        if (_adornments is null || scene.Selection.IsEmpty)
        {
            _sceneBody = scene;
        }
        else if (!SameLists(_sceneBody.Elements, scene.Elements))
        {
            _sceneBody = SceneSnapshot.Create(scene.Elements);
        }
        if (_adornments is not null && !ReferenceEquals(_sceneSelection.Selection, scene.Selection))
        {
            _sceneSelection = scene.Selection.IsEmpty ? SceneSnapshot.Empty : SceneSnapshot.Create([], scene.Selection);
        }
    }

    private static bool SameLists(ReadOnlySpan<DisplayList> shown, ReadOnlySpan<DisplayList> next)
    {
        if (shown.Length != next.Length)
        {
            return false;
        }
        for (int index = 0; index < shown.Length; index++)
        {
            if (!ReferenceEquals(shown[index], next[index]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Mac LivePresentation.refresh: while the workspace shows, the effect layer shows the document's Blur, Pixelate and
    /// Distort regions and a region being drawn, so the region follows the drag live. Without regions it holds no capture
    /// (WIN-CAP-01). An unchanged document does nothing.
    /// </summary>
    private void UpdateEffects()
    {
        if (_effects is null)
        {
            return;
        }
        CanvasLiveState state = _controller.State;
        ElementList elements = _workspaceShown ? state.Elements : ElementList.Empty;
        Element? draft = _workspaceShown && state.Draft is { } drawn && drawn.Tool.IsPixelEffect ? drawn : null;
        if (elements == _effectElements && SameDraft(draft, _effectDraft))
        {
            return;
        }
        _effectElements = elements;
        _effectDraft = draft;
        try
        {
            if (draft is null && !HasPixelEffects(elements))
            {
                _effects.Show(EffectList.Empty);
            }
            else
            {
                _effects.Show(draft is null ? elements : state.ComposeElements());
            }
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            _factory.Report(new OverlayEvent.Failure("Show the effect regions", error));
        }
        ReportEffects();
    }

    private static bool SameDraft(Element? next, Element? shown) =>
        next is { } a ? shown is { } b && a.Equals(in b) : shown is null;

    private static bool HasPixelEffects(ElementList elements)
    {
        foreach (ref readonly Element element in elements)
        {
            if (element.Tool.IsPixelEffect)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Tells the app thread what the effect layer shows, once per change (the toolbar's capture warning).</summary>
    private void ReportEffects()
    {
        if (_effects is null || _disposed)
        {
            return;
        }
        EffectsState state = _effects.State;
        (bool Regions, bool Available, string? Failure) report = (_effects.Effects.NeedsPixels, _effects.PixelsAvailable, EffectsMessages.Failure(state));
        if (report == _effectsReported)
        {
            return;
        }
        _effectsReported = report;
        _factory.Report(new OverlayEvent.Effects(_display, report.Regions, report.Available, report.Failure,
            report.Failure is null ? null : EffectsMessages.Detail(state)));
    }

    /// <summary>
    /// Mac effectsReady: the scene leaves effect regions to the effect layer while it shows live pixels, and draws them
    /// opaque again as soon as it cannot (WIN-CAP-03).
    /// </summary>
    private void OnPixelsAvailableChanged(object? sender, EventArgs args)
    {
        if (_disposed || _effects is null || _effects.PixelsAvailable == _pixelsAvailable)
        {
            return;
        }
        _pixelsAvailable = _effects.PixelsAvailable;
        Render();
        ReportEffects();
    }

    private void OnEffectsStateChanged(object? sender, EffectsStateChangedEventArgs change) => ReportEffects();

    private EffectsPresenter? CreateEffects(OverlayCanvasContext context, ContainerVisual root, DrawingCanvasFactory factory)
    {
        if (factory.Capture is not { } capture)
        {
            return null;
        }
        try
        {
            EffectsPresenter effects = EffectsPresenter.Create(new EffectsPresenterOptions
            {
                Compositor = context.Compositor,
                Root = root,
                Capture = capture,
                Display = context.Overlay.Display,
                Scale = _scale,
                PixelWidth = context.Overlay.Display.Bounds.Width,
                PixelHeight = context.Overlay.Display.Bounds.Height,
            });
            effects.PixelsAvailableChanged += OnPixelsAvailableChanged;
            effects.StateChanged += OnEffectsStateChanged;
            return effects;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NativeCallException)
        {
            // Without the effect layer the scene keeps every effect region opaque (fail closed).
            factory.Report(new OverlayEvent.Failure("Show live effects", error));
            return null;
        }
    }

    private OverlayPresenter? CreateAdornments(OverlayCanvasContext context, ContainerVisual root, DrawingCanvasFactory factory)
    {
        try
        {
            OverlayPresenter adornments = OverlayPresenter.Create(new OverlayPresenterOptions
            {
                Compositor = context.Compositor,
                Root = root,
                Devices = context.Devices,
                Scale = _scale,
                PixelWidth = context.Overlay.Display.Bounds.Width,
                PixelHeight = context.Overlay.Display.Bounds.Height,
                Colors = factory.Colors,
            });
            adornments.StateChanged += OnPresenterStateChanged;
            return adornments;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NativeCallException)
        {
            // The selection then stays in the canvas's scene, below the effect layer.
            factory.Report(new OverlayEvent.Failure("Show the selection above live effects", error));
            return null;
        }
    }

    private void AfterChange()
    {
        UpdateCursor();
        if (_controller.State.HasTransitions)
        {
            _factory.Animate(this);
        }
    }

    private void ShowBoard(Board board)
    {
        if (board == _shownBoard)
        {
            return;
        }
        _shownBoard = board;
        if (board == Board.Desktop)
        {
            _board.IsVisible = false;
            return;
        }
        _board.Brush = board == Board.Whiteboard
            ? _white ??= _context.Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
            : _black ??= _context.Compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, BlackboardLevel, BlackboardLevel, BlackboardLevel));
        _board.IsVisible = true;
    }

    private void UpdateCursor()
    {
        CanvasCursor cursor = _controller.State.Cursor;
        if (cursor == _shownCursor)
        {
            return;
        }
        nint handle = 0;
        try
        {
            handle = _factory.Cursors.Get(cursor, _scale);
        }
        catch (NativeCallException error)
        {
            _factory.Report(new OverlayEvent.Failure("Show the drawing cursor", error));
        }
        _shownCursor = cursor;
        CursorHandle = handle;
        ApplyCursor();
    }

    /// <summary>
    /// Windows asks for the cursor only when the pointer moves; a tool change while it rests shows at once. The Use apps
    /// button shows the arrow, as the Mac's hoverCursor does over a subview.
    /// </summary>
    private void ApplyCursor()
    {
        nint shown = _overHud ? 0 : CursorHandle;
        _context.Overlay.Cursor = shown;
        if (_pointerInside && _drawing)
        {
            PInvoke.SetCursor(shown != 0 ? (HCURSOR)shown : s_arrow);
        }
    }

    /// <summary>
    /// Mac HUDButton and HintCloseButton: a primary press inside the shown Use apps button or the tip's ✕ belongs to it, and
    /// releasing it there uses apps or turns tips off. While they fade out they take no clicks. Returns true when a button
    /// took the input.
    /// </summary>
    private bool HandleHud(in PointerInput input)
    {
        if (input.Samples.IsEmpty)
        {
            return false;
        }
        Vector2 position = input.Current.Position;
        double x = position.X / _scale, y = position.Y / _scale;
        bool useApps = _hud.IsVisible && Contains(CanvasHud.UseAppsFrame, x, y);
        bool close = _hint is { IsVisible: true, Closable: true } hint && _hintShown && Contains(hint.CloseFrame, x, y);
        if (input.Kind is PointerInputKind.Move or PointerInputKind.Enter or PointerInputKind.Leave)
        {
            bool over = (useApps || close) && input.Kind != PointerInputKind.Leave;
            if (over != _overHud)
            {
                _overHud = over;
                ApplyCursor();
            }
        }
        if (_hudPress is { } pressed && pressed == input.PointerId)
        {
            if (input.Kind == PointerInputKind.Up && Buttons(input.ChangedButton) == CanvasButtons.Primary)
            {
                _hudPress = null;
                if (_hudPressCloses ? close : useApps)
                {
                    if (_hudPressCloses)
                    {
                        TurnOffTips();
                    }
                    else
                    {
                        ReleaseInput();
                    }
                }
            }
            else if (input.Kind == PointerInputKind.Canceled)
            {
                _hudPress = null;
            }
            return true;
        }
        if (input.Kind == PointerInputKind.Down && (useApps || close) && Buttons(input.ChangedButton) == CanvasButtons.Primary)
        {
            _hudPress = input.PointerId;
            _hudPressCloses = !useApps;
            return true;
        }
        return false;

        static bool Contains(Rect frame, double x, double y) => x >= frame.X && y >= frame.Y && x < frame.MaxX && y < frame.MaxY;
    }

    /// <summary>The Use apps button: release drawing input like Use apps. Safe on any thread (UI Automation invokes it).</summary>
    private void ReleaseInput() => _context.Channel.Send(new CanvasCommand(CanvasCommandKind.ReleaseInput, _display));

    /// <summary>The tip's ✕: turn tips off (Mac setShortcutHints(false)). Safe on any thread (UI Automation invokes it).</summary>
    private void TurnOffTips() => _context.Channel.Send(new CanvasCommand(CanvasCommandKind.TurnOffTips, _display));

    /// <summary>
    /// Mac refreshHint: the tip shows on the editing target's canvas unless the person turned tips off, with the mode, what
    /// Escape does now and zoom. It wakes when its <see cref="HintWake"/> changes and fades out
    /// <see cref="Annotator.Core.Motion.MotionTiming.HintDwellDuration"/> later (Mac HintSchedule); strokes and zoom values
    /// update the text without waking it. While drawing it has the ✕ that turns tips off. Its text is built again only when
    /// something it says changed, so a stroke's samples cost a few comparisons.
    /// </summary>
    private void UpdateHint(WorkspaceSnapshot? workspace, DisplaySnapshot? display, bool animate)
    {
        if (_hint is null || workspace is null)
        {
            return;
        }
        bool textEditor = _text.Editor is { IsEnded: false };
        bool activeEdit = _controller.HasActiveEdit;
        double now = _hintClock.Elapsed.TotalSeconds;
        bool shown = _hintSchedule.Shows(WorkspaceHint.Wake(workspace, display, textEditor), now);
        bool closable = CanvasHud.ShowsUseApps(workspace);
        var hinted = (textEditor, activeEdit, shown, closable);
        if (ReferenceEquals(workspace, _hintWorkspace) && ReferenceEquals(display, _hintDisplay) && hinted == _hinted)
        {
            return;
        }
        _hintWorkspace = workspace;
        _hintDisplay = display;
        _hinted = hinted;
        string text = WorkspaceHint.Text(workspace, textEditor, activeEdit, workspace.ZoomHint);
        Rect view = HintBox.ViewFrame(_bounds.Width, closable);
        string? before = _hint.Text;
        _hint.Update(text, view, closable, shown, animate, ReducesMotion());
        if (_hint.Failure is { } failure && shown && !_hintShown)
        {
            _factory.Report(new OverlayEvent.Failure("Show the on-screen tips", failure));
        }
        ScheduleHintFade(shown, now);
        if (shown != _hintShown || closable != _hintClosable || (shown && text != before))
        {
            _hintShown = shown;
            _hintClosable = closable;
            _accessibility.Tree = AccessibleTree();
        }
    }

    /// <summary>A shown tip looks again when its dwell ends, so it fades out without other input.</summary>
    private void ScheduleHintFade(bool shown, double now)
    {
        if (!shown)
        {
            _hintTimer?.Stop();
            return;
        }
        if (_hintTimer is null)
        {
            _hintTimer = _factory.Queue.CreateTimer();
            _hintTimer.IsRepeating = false;
            _hintTimer.Tick += (_, _) =>
            {
                if (!_disposed)
                {
                    UpdateHint(_hintWorkspace, _hintDisplay, animate: true);
                }
            };
        }
        _hintTimer.Stop();
        _hintTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, _hintSchedule.HidesAt - now) + 0.001);
        _hintTimer.Start();
    }

    /// <summary>
    /// Mac refreshAccessibilitySummary on each published frame and canvas change: the canvas group says how many objects
    /// there are, counting one still being drawn and the text being typed as the Mac does, and how many are selected
    /// (<see cref="CanvasAccessibility.Counts"/>); the Use apps button and the text being edited are its children while
    /// they show.
    /// </summary>
    private void UpdateAccessibility(CanvasFrame frame)
    {
        _summaryDrawing = frame.Workspace.Mode == WorkspaceMode.Draw;
        UpdateAccessibility();
    }

    private void UpdateAccessibility()
    {
        (int objects, int selected) = CanvasAccessibility.Counts(_controller);
        var current = (objects, selected, _summaryDrawing, _hudShown);
        if (current == _summarized)
        {
            return;
        }
        _summarized = current;
        _summary = CanvasAccessibility.Summary(current.Item1, current.Item2, current.Item3);
        _accessibility.Tree = AccessibleTree();
    }

    private OverlayAccessibleTree AccessibleTree()
    {
        PixelRect bounds = _context.Overlay.Display.Bounds;
        var root = new OverlayAccessibleNode(OverlayControlType.Group, CanvasAccessibility.Label, new PixelRect(0, 0, bounds.Width, bounds.Height))
        {
            Value = _summary.Length == 0 ? null : _summary,
            HelpText = CanvasAccessibility.Help,
        };
        var children = ImmutableArray.CreateBuilder<OverlayAccessibleNode>(4);
        if (_hudShown)
        {
            Rect frame = CanvasHud.UseAppsAccessibleFrame;
            children.Add(new OverlayAccessibleNode(OverlayControlType.Button, CanvasHud.UseAppsLabel, new PixelRect(
                (int)Math.Round(frame.X * _scale), (int)Math.Round(frame.Y * _scale), (int)Math.Round(frame.MaxX * _scale), (int)Math.Round(frame.MaxY * _scale)))
            {
                Invoke = ReleaseInput,
            });
        }
        if (_hintShown && _hint?.Text is { } tip)
        {
            // Mac WorkspaceHintView: static text whose value is the tip, over the hint view's frame.
            children.Add(new OverlayAccessibleNode(OverlayControlType.Text, tip, Pixels(_hint.View)) { Value = tip });
            if (_hintClosable && _hint.Closable)
            {
                // Mac HintCloseButton: the ✕ that turns tips off, over its click area.
                children.Add(new OverlayAccessibleNode(OverlayControlType.Button, WorkspaceHint.CloseLabel, Pixels(HintBox.CloseAccessibleFrame(_hint.Box)))
                {
                    HelpText = WorkspaceHint.CloseHelp,
                    Invoke = TurnOffTips,
                });
            }
        }
        if (_editorNode is { } editor)
        {
            // Mac: the text view is in the accessibility tree while it edits, with its text as the value.
            children.Add(new OverlayAccessibleNode(OverlayControlType.Edit, editor.Label, editor.Frame)
            {
                Value = editor.Value,
                IsFocused = editor.Focused,
                SetValue = _text.SetValueFromAutomation,
            });
        }
        return new OverlayAccessibleTree(root, children.ToImmutable());
    }

    /// <summary>
    /// The text editor's element follows its text, place and focus. Screen readers hear when it opens with the keyboard
    /// focus and when its text changes.
    /// </summary>
    private void UpdateEditorAccessibility()
    {
        EditorNode? node = _text.Editor is { IsEnded: false } editor
            ? new EditorNode(editor.Session.ElementId, editor.Session.AccessibilityLabel, editor.Text, Pixels(editor.FrameBounds), _hasFocus)
            : null;
        if (node == _editorNode)
        {
            return;
        }
        EditorNode? previous = _editorNode;
        _editorNode = node;
        _accessibility.Tree = AccessibleTree();
        if (node is not { } shown)
        {
            return;
        }
        if (shown.Focused && (previous is not { } before || !before.Focused || before.Element != shown.Element))
        {
            _accessibility.RaiseFocusChanged();
        }
        else if (previous is { } earlier && earlier.Element == shown.Element && earlier.Value != shown.Value)
        {
            _accessibility.RaiseValueChanged(earlier.Value);
        }
    }

    /// <summary>A canvas rectangle in overlay pixels, covering every pixel it touches.</summary>
    private PixelRect Pixels(Rect rect) => new((int)Math.Floor(rect.X * _scale), (int)Math.Floor(rect.Y * _scale),
                                               (int)Math.Ceiling(rect.MaxX * _scale), (int)Math.Ceiling(rect.MaxY * _scale));

    /// <summary>What UI Automation reads about the text being edited.</summary>
    private readonly record struct EditorNode(Guid Element, string Label, string Value, PixelRect Frame, bool Focused);

    private void OnRequested(CanvasRequest request) => _factory.Request(_display, request);

    /// <summary>
    /// The press chose what the menu acts on, as on the Mac; the menu itself opens when the button is released, as Windows
    /// context menus do. A menu opened from the keyboard shows at once.
    /// </summary>
    private void OnContextMenuRequested(CanvasMenu menu)
    {
        if (_openingFromKeyboard)
        {
            _factory.Queue.TryEnqueue(() => ShowContextMenu(menu));
            return;
        }
        _pendingMenu = menu;
    }

    /// <summary>Shift+F10 or the Menu key, the Windows keys for a context menu.</summary>
    private static bool IsContextMenuKey(in KeyInput input)
    {
        const int Apps = 0x5D, F10 = 0x79;
        ModifierKeys held = input.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows);
        return !input.IsRepeat && (input.VirtualKey == Apps && held == ModifierKeys.None || input.VirtualKey == F10 && held == ModifierKeys.Shift);
    }

    /// <summary>
    /// Where a menu opened from the keyboard shows when nothing is selected: at the pointer when it is on this display,
    /// else in the middle of the display (canvas points).
    /// </summary>
    private Point KeyboardMenuPoint()
    {
        Rect frame = _context.Display.DesktopFrame;
        if (PInvoke.GetCursorPos(out System.Drawing.Point cursor) && cursor.X >= frame.X && cursor.X < frame.MaxX
            && cursor.Y >= frame.Y && cursor.Y < frame.MaxY)
        {
            return new Point((cursor.X - frame.X) / _scale, (cursor.Y - frame.Y) / _scale);
        }
        return new Point(_bounds.Width / 2, _bounds.Height / 2);
    }

    private void ShowContextMenu(CanvasMenu menu)
    {
        if (_disposed)
        {
            return;
        }
        Point anchor = _context.Display.ToDesktop(menu.Position);
        CanvasMenuCommand? chosen;
        try
        {
            chosen = CanvasContextMenu.Show(new HWND(_context.Overlay.Handle),
                new System.Drawing.Point((int)Math.Round(anchor.X), (int)Math.Round(anchor.Y)), menu);
        }
        catch (NativeCallException error)
        {
            _factory.Report(new OverlayEvent.Failure("Show the canvas menu", error));
            return;
        }
        if (_disposed)
        {
            return;
        }
        if (chosen is { } command)
        {
            _controller.PerformMenuItem(command);
        }
        else
        {
            _menuClosedAt = Environment.TickCount64;
        }
    }

    private void OnPresenterStateChanged(object? sender, PresenterStateChangedEventArgs change)
    {
        // Lost or missing devices are reported by the overlay host with the device state; covers stay opaque meanwhile.
        if (change.Current is PresenterState.Failed failed)
        {
            _factory.Report(new OverlayEvent.Failure("Draw the annotations", failed.Error));
        }
    }

    /// <summary>Message times are 32-bit milliseconds that wrap after 49.7 days; canvas timestamps keep counting.</summary>
    private long Timestamp(uint time)
    {
        if (time < _lastTime && _lastTime - time > uint.MaxValue / 2)
        {
            _timeEpoch += 1L << 32;
        }
        _lastTime = time;
        return _timeEpoch + time;
    }

    /// <summary>The Mac's Reduce Motion is Windows' Animation effects setting (WIN-MOT-03).</summary>
    private static unsafe bool ReducesMotion()
    {
        BOOL animations = true;
        return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) && !animations;
    }

    /// <summary>
    /// The character the active keyboard layout gives the key with Shift alone, like the Mac's
    /// charactersIgnoringModifiers (WIN-INP-03), or null. Settings records tool keys the same way, so on AZERTY Shift and
    /// the "&amp;" key give "1" in both places, and on US Shift+1 gives "!", which is no tool key, as on the Mac.
    /// </summary>
    private static unsafe string? KeyText(in KeyInput input)
    {
        Span<byte> state = stackalloc byte[256];
        state.Clear();
        if ((input.Modifiers & ModifierKeys.Shift) != 0)
        {
            state[0x10] = 0x80;
        }
        Span<char> text = stackalloc char[8];
        fixed (byte* keys = state)
        fixed (char* output = text)
        {
            // Flag 4 leaves the keyboard state (dead keys) untouched; a dead key gives its spacing character.
            int produced = PInvoke.ToUnicodeEx((uint)input.VirtualKey, (uint)input.ScanCode, keys, output, text.Length, 4, PInvoke.GetKeyboardLayout(0));
            return produced < 0 ? text[..1].ToString() : produced > 0 ? text[..produced].ToString() : null;
        }
    }

    private static CanvasButtons Buttons(PlatformButtons buttons)
    {
        var mapped = CanvasButtons.None;
        if ((buttons & PlatformButtons.Left) != 0)
        {
            mapped |= CanvasButtons.Primary;
        }
        if ((buttons & PlatformButtons.Right) != 0)
        {
            mapped |= CanvasButtons.Secondary;
        }
        if ((buttons & PlatformButtons.Middle) != 0)
        {
            mapped |= CanvasButtons.Middle;
        }
        return mapped;
    }

    private static KeyModifiers Modifiers(ModifierKeys modifiers)
    {
        var mapped = KeyModifiers.None;
        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            mapped |= KeyModifiers.Shift;
        }
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            mapped |= KeyModifiers.Ctrl;
        }
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            mapped |= KeyModifiers.Alt;
        }
        return mapped;
    }
}

/// <summary>Development only (UX reference capture): a canvas's picture and the objects it shows, in points.</summary>
internal sealed record CanvasReference(Annotator.Rendering.Imaging.RasterImage Image, ElementList Elements,
                                       ImmutableHashSet<Guid> Selection, Guid? Draft, Size Size);