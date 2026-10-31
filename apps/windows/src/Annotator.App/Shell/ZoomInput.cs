using Annotator.App.Overlay;
using Annotator.Core;
using Annotator.Platform;
using Annotator.Platform.Input;
using Annotator.Workspace;
using Annotator.Workspace.Zoom;
using Microsoft.UI.Dispatching;
using DisplayId = Annotator.Workspace.DisplayId;

namespace Annotator.App.Shell;

/// <summary>
/// Live zoom's global input on the app thread (WIN-INP-06, WIN-INP-07, WIN-INP-08), the Windows form of the Mac's
/// ToolbarZoomActivation and per-zoom ZoomGestureMonitor. It keeps the low-level mouse hook while the toolbar shows, zoom
/// shows or the lens shows, and the keyboard hook only while screen zoom shows; the hooks hand Alt+scroll and Escape to
/// this thread and pointer moves to the overlay thread. A hook that Windows refuses leaves the toolbar's zoom controls,
/// with the Mac's interrupted state.
/// </summary>
internal sealed class ZoomInput : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly WorkspaceModel _model;
    private readonly DisplayedZoomBoard _board;
    private readonly DrawingCanvasFactory _canvases;
    private readonly Func<bool> _dismissShown;
    private readonly bool _installs;
    private LowLevelInputHooks? _hooks;
    private ZoomScrollState _state = ZoomScrollState.Off;
    private bool _refused;
    private bool _wanted;
    private bool _screenShown;
    private bool _disposed;

    /// <param name="dismissShown">Closes the error window or the tray flyout if one shows and says whether it did (Mac onDismissMenuBar).</param>
    /// <param name="hooks">False keeps every hook out (the live check): Alt+scroll then works only on a canvas in Draw.</param>
    public ZoomInput(DispatcherQueue queue, WorkspaceModel model, DisplayedZoomBoard board, DrawingCanvasFactory canvases, Func<bool> dismissShown,
                     bool hooks)
    {
        _queue = queue;
        _model = model;
        _board = board;
        _canvases = canvases;
        _dismissShown = dismissShown;
        _installs = hooks;
    }

    /// <summary>Development only (measurements): the hooks' timings, or null while no hook ever ran.</summary>
    public LowLevelHookStatistics? Statistics => _hooks?.Statistics;

    /// <summary>
    /// The hooks follow the published workspace and what the displays show (app thread). It runs while a snapshot is
    /// published, so it changes the workspace only later, in its own turn.
    /// </summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }
        DisplaySnapshot? active = snapshot.ActiveDisplay is { } id ? snapshot.Displays.FirstOrDefault(display => display.Id == id) : null;
        DisplayedZoom? shown = active is null ? null : _board.Get(active.Id);
        bool screen = !snapshot.WorkspaceHidden && (snapshot.ScreenZoomEnabled || shown?.Mode == PresentationMode.Screen);
        bool lens = !snapshot.WorkspaceHidden && (snapshot.MagnifierEnabled || shown?.Mode == PresentationMode.Lens);
        bool activation = ZoomScrollState.ActivationMonitors(snapshot.ToolbarVisible, snapshot.WorkspaceHidden, snapshot.Visibility is not null,
                                                            snapshot.ScreenZoomEnabled);
        ZoomScrollState state = screen && active is not null
            ? new ZoomScrollState(ZoomScrollRole.Zoom, (int)Math.Round(active.Display.DesktopFrame.X), (int)Math.Round(active.Display.DesktopFrame.Y),
                                  (int)Math.Round(active.Display.DesktopFrame.MaxX), (int)Math.Round(active.Display.DesktopFrame.MaxY))
            : activation ? new ZoomScrollState(ZoomScrollRole.Activation) : ZoomScrollState.Off;
        Volatile.Write(ref _state, state);
        bool wanted = state.Role != ZoomScrollRole.None || lens;
        if (!wanted)
        {
            // A refused hook stays off until its lifetime ends, as the Mac's monitor starts again only with a new lifetime.
            _refused = false;
        }
        _wanted = wanted;
        if (screen != _screenShown)
        {
            _screenShown = screen;
            // Mac noteZoomGestures: Alt+scroll and Escape reach zoom while its hooks run; clearScreenZoom turns this off.
            bool available = screen && !_refused && _installs;
            string? message = _refused ? WorkspaceModel.ZoomScrollInterrupted : null;
            _queue.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    _model.NoteZoomGestures(available, message);
                }
            });
        }
        if (_refused || !_installs)
        {
            return;
        }
        if (wanted && _hooks is null)
        {
            try
            {
                _hooks = LowLevelInputHooks.Start(new LowLevelInputOptions
                {
                    Wheel = Wheel,
                    Moved = _canvases.PointerMoved,
                    Escape = () => _queue.TryEnqueue(OnEscape),
                    Stopped = stopped => _queue.TryEnqueue(() => OnStopped(stopped)),
                });
            }
            catch (InvalidOperationException error)
            {
                StartupLog.Note("zoom-hooks", error.ToString().ReplaceLineEndings(" | "));
                NativeCallException refused = new("Start the zoom input thread", unchecked((int)0x80004005));
                _refused = true;
                _queue.TryEnqueue(() => OnStopped(new LowLevelHooksStopped(refused)));
                return;
            }
        }
        _hooks?.Update(mouseHook: wanted, keyboardHook: screen);
    }

    /// <summary>
    /// Mac handleLocalZoomScroll: a canvas received Alt+scroll. That means the hook did not take it: if it should have, the
    /// hook is installed again.
    /// </summary>
    public void LocalScroll(DisplayId display, double delta, int x, int y)
    {
        if (_disposed)
        {
            return;
        }
        if (_wanted && !_refused && Volatile.Read(ref _state).Consumes(delta, x, y))
        {
            _hooks?.Revive();
        }
        Scroll(delta);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _hooks?.Dispose();
        _hooks = null;
    }

    /// <summary>The hook's decision (hook thread): only Alt+scroll that zoom uses now is consumed and handed to the app thread.</summary>
    private bool Wheel(LowLevelWheel wheel)
    {
        if (ZoomScroll.Delta(wheel.Delta, wheel.Alt, wheel.Control, wheel.Shift, wheel.Windows) is not { } delta
            || !Volatile.Read(ref _state).Consumes(delta, wheel.X, wheel.Y))
        {
            return false;
        }
        _queue.TryEnqueue(() => Scroll(delta));
        return true;
    }

    /// <summary>
    /// Mac activateScreenZoomFromOptionScroll and scrollScreenZoom: scrolling up starts zoom from 1x while the toolbar shows;
    /// once zoom shows, every scroll changes it (scrolls queued before the start count too).
    /// </summary>
    private void Scroll(double delta)
    {
        if (_disposed)
        {
            return;
        }
        WorkspaceSnapshot snapshot = _model.Snapshot;
        DisplayedZoom? shown = snapshot.ActiveDisplay is { } active ? _board.Get(active) : null;
        if (snapshot.ScreenZoomEnabled || shown is { Mode: PresentationMode.Screen, Exiting: true })
        {
            _model.ScrollScreenZoom(delta);
        }
        else if (ZoomScrollState.ActivationMonitors(snapshot.ToolbarVisible, snapshot.WorkspaceHidden, snapshot.Visibility is not null, false))
        {
            _model.StartScreenZoomFromAltScroll(delta);
        }
    }

    /// <summary>Mac handleZoomEscape: Escape closes the error window or the tray flyout first, then leaves zoom.</summary>
    private void OnEscape()
    {
        if (_disposed || _dismissShown())
        {
            return;
        }
        _model.HandleZoomEscape();
    }

    private void OnStopped(LowLevelHooksStopped stopped)
    {
        if (!_disposed && _wanted)
        {
            Refuse(stopped.Error);
        }
    }

    /// <summary>
    /// Windows refused a hook (WIN-INP-08): screen zoom stops with the Mac's interrupted state, and the toolbar's zoom
    /// controls stay; the hooks start again with their next lifetime.
    /// </summary>
    private void Refuse(NativeCallException error)
    {
        StartupLog.Note("zoom-hooks", error.ToString().ReplaceLineEndings(" | "));
        _refused = true;
        _hooks?.Update(mouseHook: false, keyboardHook: false);
        WorkspaceSnapshot snapshot = _model.Snapshot;
        if (snapshot.ScreenZoomEnabled && snapshot.ActiveDisplay is { } active)
        {
            _model.ReportZoomFailure(active, PresentationMode.Screen, WorkspaceModel.ZoomScrollInterrupted);
        }
        _model.NoteZoomGestures(false, snapshot.ScreenZoomEnabled ? WorkspaceModel.ZoomScrollInterrupted : WorkspaceModel.ZoomScrollUnavailable);
    }
}
