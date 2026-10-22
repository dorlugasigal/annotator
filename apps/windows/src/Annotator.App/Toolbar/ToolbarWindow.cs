using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using CoreRect = Annotator.Core.Rect;
using CoreSize = Annotator.Core.Geometry.Size;

namespace Annotator.App.Toolbar;

/// <summary>
/// The drawing toolbar (WIN-UI-02, ADR 0012), a port of the Mac ToolbarView: the tool rail from the profile's
/// toolbar items, Tool library, Board background, Draw or Use apps, Undo, Redo, Clear, Properties, Settings and Hide,
/// the quick properties of the current tool, and the active board. It is a borderless window that never activates
/// (WS_EX_NOACTIVATE), so clicking it leaves the person's app active (WIN-OVR-03); a value field activates it only
/// while the person types. Drag the background to move it inside the work area, or to a display edge to dock it there
/// (WIN-OVR-10, ToolbarWindow.Docking.cs).
/// </summary>
internal sealed partial class ToolbarWindow : Window, IPropertiesHost
{
    private readonly WorkspaceModel _model;
    private readonly WindowRegistry _registry;
    private readonly Action _interacted;
    private readonly Action<ColorPickerRequest, RectInt32> _openColorPicker;
    private readonly ToolbarReferencePlacement? _reference;
    private readonly WindowRegistration _registration;
    private readonly WindowRoot _root = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    /// <summary>The toolbar at a larger text size (WIN-A11Y-03): its content, zoomed; null at 100%.</summary>
    private ZoomPanel? _zoom;
    private List<Action<WorkspaceSnapshot>> _updates = [];
    private readonly SolidColorBrush _ink = new(Microsoft.UI.Colors.Black);
    private SelectionMarkers _markers;
    private static readonly MotionSamples s_selectionMotion = ToolbarControlMotion.Selection(reduceMotion: false);
    private static readonly MotionSamples s_reducedSelectionMotion = ToolbarControlMotion.Selection(reduceMotion: true);
    /// <summary>
    /// This update's control motion (Mac selectionAnimation): the snappy spring, or Motion.reduced with Reduce Motion;
    /// null while the controls change at once.
    /// </summary>
    private MotionSamples? _controlMotion;
    private bool _reducesMotion;
    private string _structure = string.Empty;
    private bool? _vertical;
    /// <summary>The preferences that place the toolbar, as last shown; null before the first show.</summary>
    private string? _layout;
    /// <summary>The snapshot the toolbar showed last.</summary>
    private WorkspaceSnapshot? _view;
    private double _rasterization;
    /// <summary>The properties layout the person last saw (Mac settledPropertiesMode); null while the toolbar is hidden.</summary>
    private ToolbarPropertiesMode? _settledProperties;
    private bool _shown;
    private bool _placed;
    private bool _firstFrame;
    private bool _closing;
    private HWND _typingReturn;

    public ToolbarWindow(WorkspaceModel model, WindowRegistry registry, Action interacted, Action<ColorPickerRequest, RectInt32> openColorPicker,
                         ToolbarReferencePlacement? reference = null)
    {
        _model = model;
        _registry = registry;
        _interacted = interacted;
        _openColorPicker = openColorPicker;
        _reference = reference;
        _markers = new SelectionMarkers(_root);
        Title = "Annotator Drawing Tools";
        Hwnd = WindowChrome.Configure(this, noActivate: true);
        // Registered before the first show so capture can leave it out (S-1); the toolbar layer orders it above canvases.
        _registration = registry.Register((nint)Hwnd, WindowLayer.Toolbar);
        _root.Loaded += (_, _) =>
        {
            WatchScale();
            FitToContent();
        };
        _root.ActualThemeChanged += (_, _) =>
        {
            _ink.Color = PropertiesInk.Color();
            // Values drawn from theme colors outside XAML resources, such as the color wells' squares, follow the theme too.
            if (_view is { } view)
            {
                foreach (Action<WorkspaceSnapshot> update in _updates)
                {
                    update(view);
                }
            }
            // Controls whose colors were read in code when they were built are built again (WIN-UI-07).
            DispatcherQueue.TryEnqueue(RebuildForTheme);
        };
        AppResources.FollowTheme(_root);
        _ink.Color = PropertiesInk.Color();
        AutomationFrames.Nudge(_root);
        Content = _root;
        ApplyZoom();
        WireKeyboard();
        AppResources.SystemLookChanged += OnSystemLookChanged;

        // Alt+F4 releases drawing input like Use apps and never closes the toolbar (WIN-OVR-12).
        AppWindow.Closing += (_, args) =>
        {
            if (!_closing)
            {
                args.Cancel = true;
                _model.UseApps();
            }
        };
        Closed += (_, _) =>
        {
            AppResources.SystemLookChanged -= OnSystemLookChanged;
            _registration.Dispose();
        };
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size: the toolbar is built again with the new colors
    /// and zoom, in the same place (WIN-UI-07, WIN-A11Y-03).
    /// </summary>
    private void OnSystemLookChanged()
    {
        ApplyZoom();
        RebuildForTheme();
    }

    /// <summary>Raised once when the toolbar draws its first frame (startup measurement).</summary>
    public event Action? FirstFrame;

    /// <summary>
    /// Opens the arrowhead choices of the full properties panel for the start or end, below the anchor rectangle in
    /// physical pixels; the action runs when they close. Without it the arrowhead buttons do nothing.
    /// </summary>
    public Action<bool, RectInt32, Action>? ArrowHeads { get; init; }

    /// <summary>Opens the Tools window (the Tool library button, Mac openToolLibrary). Without it the button is unavailable.</summary>
    public Action? ToolLibrary { get; init; }

    /// <summary>Opens Settings on General (the gear, Mac openPreferences()). Without it the gear is unavailable.</summary>
    public Action? Settings { get; init; }

    public HWND Hwnd { get; }

    /// <summary>The toolbar's rectangle in physical pixels.</summary>
    public RectInt32 Frame => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    /// <summary>
    /// Hides the window for good (Quit); Application.Exit destroys it. Closing it here stopped the app thread's dispatcher
    /// from running queued work when it was the app's last window and Annotator was not in front (a quiet start at sign-in,
    /// or a board opened from another app), so Quit never got past its next await to Exit.
    /// </summary>
    public void Shutdown()
    {
        _closing = true;
        CancelDrag();
        _ghost?.Dispose();
        _ghost = null;
        if (_shown)
        {
            AppWindow.Hide();
            _shown = false;
        }
    }

    /// <summary>Shows the workspace state. Called on the app thread for every published snapshot.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (!snapshot.ToolbarVisible)
        {
            _settledProperties = null;
            if (_shown)
            {
                // Mac hideTools and orderOut end a drag.
                CancelDrag();
                ReturnTypingFocus();
                ReturnKeyboard();
                AppWindow.Hide();
                _shown = false;
            }
            return;
        }
        if (!Presented())
        {
            // A show or hide, or hidden tools, end a drag (Mac hideTools and onSuspendPresentation).
            CancelDrag();
        }
        _view = snapshot;
        // Mac selectionAnimation: controls move only on a toolbar that rests on screen, so a snapshot for the show and
        // hide motion shows what lands. Reference captures keep them still unless they record the control motion.
        bool moves = ToolbarControlMotion.Animates(snapshot);
        bool animates = _shown && moves && (_reference is null || _reference.ControlMotion);
        _reducesMotion = animates && ToolbarMotion.ReducesMotion();
        _controlMotion = !animates ? null : _reducesMotion ? s_reducedSelectionMotion : s_selectionMotion;
        ToolbarLook look = ToolbarLook.Of(snapshot, Available());
        if (_vertical is { } vertical && vertical != look.Vertical)
        {
            // Mac `.id(vertical)`: the other orientation is a new toolbar view, so nothing slides over from the old one.
            _controlMotion = null;
        }
        _vertical = look.Vertical;
        bool rebuilt = false;
        if (look.Structure != _structure)
        {
            _structure = look.Structure;
            // A different layout from the one the person saw means they just changed it: the new properties fade in
            // (Mac revealingProperties, never while hidden or during a show or hide).
            ToolbarPropertiesMode? revealedFrom = _shown && moves && _settledProperties != look.Properties ? _settledProperties : null;
            Build(look, snapshot, revealedFrom);
            rebuilt = true;
        }
        // Mac onToolbarLayoutChanged: another orientation, dock edge, toolbar size or set of items docks the toolbar again.
        string layout = Layout(snapshot);
        bool relayout = _layout is not null && _layout != layout;
        _layout = layout;
        if ((rebuilt || relayout) && _root.IsLoaded)
        {
            FitToContent(redock: relayout);
        }
        _settledProperties = look.Properties;
        foreach (Action<WorkspaceSnapshot> update in _updates)
        {
            update(snapshot);
        }
        _markers.Settle(_controlMotion);
        if (!_shown)
        {
            bool recovered = false;
            if (!_placed)
            {
                Place();
            }
            else
            {
                recovered = Recover();
            }
            AppWindow.Show(activateWindow: false);
            WindowChrome.KeepOnTop(Hwnd);
            _shown = true;
            if (!_firstFrame)
            {
                CompositionTarget.Rendering += OnRendering;
            }
            if (recovered)
            {
                Relayout();
            }
        }
    }

    /// <summary>
    /// Mac onDisplaysChanged: a drag ends, and a toolbar outside every work area returns to a connected display's work area
    /// (recoverToolbarPlacement).
    /// </summary>
    public void RecoverPlacement()
    {
        CancelDrag();
        if (Recover() && _shown)
        {
            Relayout();
        }
    }

    /// <summary>Mac recoverToolbarPlacement; true when the toolbar moved.</summary>
    private bool Recover()
    {
        if (_reference is not null || _docking.Recover(ToCore(Frame), Displays()) is not { } recovered)
        {
            return false;
        }
        MoveTo(recovered);
        return true;
    }

    /// <summary>Lays the toolbar out again for the display it is on now (the Mac's new ToolbarContentView with its available size).</summary>
    private void Relayout()
    {
        if (_view is { } view && _shown)
        {
            Apply(view);
        }
    }

    /// <summary>The app's theme changed: the toolbar is built again with the new colors, in the same place.</summary>
    private void RebuildForTheme()
    {
        if (_closing)
        {
            return;
        }
        _structure = string.Empty;
        Relayout();
    }

    /// <summary>The preferences that place the toolbar (Mac onToolbarLayoutChanged): orientation, dock edge, size and items.</summary>
    private static string Layout(WorkspaceSnapshot snapshot)
    {
        WorkspacePreferences preferences = snapshot.Preferences;
        var layout = new System.Text.StringBuilder();
        layout.Append(preferences.ToolbarOrientation).Append('|').Append(preferences.ToolbarDockEdge).Append('|').Append(preferences.ToolbarSize);
        foreach (ShortcutCommand item in snapshot.ToolbarItems)
        {
            layout.Append('|').Append(item.Id);
        }
        return layout.ToString();
    }

    /// <summary>
    /// The Mac's available size: the work area in points less 32 by 64 (AnnotatorApp toolbar placement). A drag keeps the
    /// size it started with, so the toolbar is not rebuilt under the pointer.
    /// </summary>
    private Size Available()
    {
        if (_reference is { } reference)
        {
            return reference.AvailableSize;
        }
        if (_dragAvailable is { } dragging)
        {
            return dragging;
        }
        MonitorArea monitor = _placed ? WindowChrome.MonitorFor(Frame) : WindowChrome.MonitorAtCursor();
        double zoom = Zoom;
        return new Size((monitor.WorkArea.Width / monitor.Scale - 32) / zoom, (monitor.WorkArea.Height / monitor.Scale - 64) / zoom);
    }

    /// <summary>How much larger the toolbar shows than the Mac's points: 1, or the text size factor (<see cref="ZoomPanel"/>).</summary>
    private double Zoom => _zoom?.Zoom ?? 1;

    /// <summary>
    /// Follows Windows' text size (WIN-A11Y-03): above 100% the toolbar's content shows in a <see cref="ZoomPanel"/>, so it
    /// grows as a whole and its labels never outgrow its buttons.
    /// </summary>
    private void ApplyZoom()
    {
        _zoom = ZoomPanel.Host(this, _root, _zoom, Size.Empty);
    }

    private double Scale() => _root.XamlRoot?.RasterizationScale ?? WindowChrome.MonitorFor(Frame).Scale;

    private void OnRendering(object? sender, object args)
    {
        // Unsubscribe at once: a Rendering handler keeps the XAML frame loop running (no idle work).
        CompositionTarget.Rendering -= OnRendering;
        if (!_firstFrame)
        {
            _firstFrame = true;
            FirstFrame?.Invoke();
        }
    }

    /// <summary>
    /// Mac showToolbar's placement: docked at the saved edge in the saved orientation on the display under the pointer,
    /// 12 points in from the work area's edges (horizontal toolbars at the top unless docked at the bottom, vertical ones
    /// at the right unless docked at the left).
    /// </summary>
    private void Place()
    {
        MonitorArea monitor = _reference?.DockArea is { } dock ? WindowChrome.MonitorFor(dock) : WindowChrome.MonitorAtCursor();
        Size size = Measure();
        int width = (int)Math.Ceiling(size.Width * monitor.Scale);
        int height = (int)Math.Ceiling(size.Height * monitor.Scale);
        _placed = true;
        if (_reference is { DockArea: null } reference)
        {
            AppWindow.MoveAndResize(new RectInt32(reference.Origin.X, reference.Origin.Y, width, height));
            return;
        }
        WorkspacePreferences preferences = _model.Snapshot.Preferences;
        MoveTo(_docking.Place(new CoreSize(width, height), DisplayFor(monitor),
            preferences.ToolbarOrientation ?? ToolbarOrientation.Horizontal, preferences.ToolbarDockEdge ?? ToolbarDockEdge.Automatic));
    }

    /// <summary>
    /// Mac resizeToolbar: sizes the window to its content. A docked toolbar docks again with its new size (flush against
    /// the work area after a drop at an edge, else 12 points in); a free one keeps its top-left corner inside the work
    /// area. <paramref name="redock"/> docks it at the saved edge first, as the Mac's updateToolbarLayout does when the
    /// orientation, edge, toolbar size or items change. During a drag the size waits for the drop.
    /// </summary>
    private void FitToContent(bool redock = false)
    {
        double scale = Scale();
        Size size = Measure();
        int width = (int)Math.Ceiling(size.Width * scale);
        int height = (int)Math.Ceiling(size.Height * scale);
        RectInt32 frame = Frame;
        if (_reference is { DockArea: null } reference)
        {
            if (frame.Width != width || frame.Height != height)
            {
                AppWindow.MoveAndResize(new RectInt32(reference.Origin.X, reference.Origin.Y, width, height));
            }
            return;
        }
        WorkspacePreferences preferences = (_view ?? _model.Snapshot).Preferences;
        ToolbarOrientation orientation = preferences.ToolbarOrientation ?? ToolbarOrientation.Horizontal;
        ToolbarDockEdge edge = preferences.ToolbarDockEdge ?? ToolbarDockEdge.Automatic;
        var content = new CoreSize(width, height);
        CoreRect current = ToCore(frame);
        CoreRect? next = redock && !_docking.IsDragging
            ? _docking.Dock(content, current, Displays(), orientation, edge)
            : _docking.Resize(current, content, Displays(), orientation, edge);
        if (next is { } placed && placed != current)
        {
            MoveTo(placed);
        }
    }

    /// <summary>Moves and sizes the window to <paramref name="frame"/> in physical pixels.</summary>
    private void MoveTo(CoreRect frame) =>
        AppWindow.MoveAndResize(new RectInt32((int)Math.Round(frame.X), (int)Math.Round(frame.Y), (int)Math.Round(frame.Width), (int)Math.Round(frame.Height)));

    private static CoreRect ToCore(RectInt32 rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>
    /// Follows the window's scale: after it moves to a display with another scale, it is laid out for that display and
    /// sized to its content in that display's pixels. A drag waits for the drop.
    /// </summary>
    private void WatchScale()
    {
        if (_root.XamlRoot is not { } root || _rasterization != 0)
        {
            return;
        }
        _rasterization = root.RasterizationScale;
        root.Changed += (sender, _) =>
        {
            if (sender.RasterizationScale == _rasterization)
            {
                return;
            }
            _rasterization = sender.RasterizationScale;
            if (_shown && !_docking.IsTracking)
            {
                Relayout();
                FitToContent();
            }
        };
    }

    /// <summary>The content size in points, zoomed at a larger text size.</summary>
    private Size Measure()
    {
        var host = (UIElement)Content;
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return host.DesiredSize;
    }

    /// <summary>
    /// A value field needs keystrokes, so the toolbar becomes the active window while the person types in it
    /// (WIN-OVR-03). <see cref="ReturnTypingFocus"/> gives focus back to the window that had it.
    /// </summary>
    private void BeginTyping(TextBox field)
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground != Hwnd)
        {
            _typingReturn = foreground;
            PInvoke.SetForegroundWindow(Hwnd);
        }
        field.Focus(FocusState.Pointer);
    }

    private void ReturnTypingFocus()
    {
        HWND target = _typingReturn;
        _typingReturn = HWND.Null;
        if (!target.IsNull && PInvoke.GetForegroundWindow() == Hwnd && PInvoke.IsWindow(target))
        {
            PInvoke.SetForegroundWindow(target);
        }
    }

    /// <summary>The rectangle of <paramref name="anchor"/> in physical screen pixels, zoomed with the toolbar.</summary>
    private RectInt32 Anchor(FrameworkElement anchor)
    {
        double scale = Scale();
        Windows.Foundation.Rect bounds = anchor.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight));
        PointInt32 window = AppWindow.Position;
        return new RectInt32(window.X + (int)(bounds.X * scale), window.Y + (int)(bounds.Y * scale),
            (int)(bounds.Width * scale), (int)(bounds.Height * scale));
    }

    WorkspaceModel IPropertiesHost.Model => _model;

    Brush IPropertiesHost.Ink => _ink;

    void IPropertiesHost.Interacted() => _interacted();

    void IPropertiesHost.OpenArrowHeads(bool start, FrameworkElement anchor, Action closed)
    {
        if (ArrowHeads is { } open)
        {
            open(start, Anchor(anchor), closed);
        }
        else
        {
            closed();
        }
    }

    void IPropertiesHost.BeginTyping(TextBox field) => BeginTyping(field);

    void IPropertiesHost.EndTyping() => ReturnTypingFocus();
}

/// <summary>
/// Development only (UX reference capture): the available size in points that the Mac capture passed to
/// ToolbarView, and a fixed window origin in physical pixels instead of the docked placement.
/// </summary>
internal sealed record ToolbarReferencePlacement(Size AvailableSize, PointInt32 Origin)
{
    /// <summary>
    /// Development only (live motion recordings): docks in this work area in physical pixels, as the app docks in a display's
    /// work area, instead of placing the window at <see cref="Origin"/>.
    /// </summary>
    public RectInt32? DockArea { get; init; }

    /// <summary>Development only (live motion recordings): the controls move as in the app (Mac selectionAnimation).</summary>
    public bool ControlMotion { get; init; }
}
