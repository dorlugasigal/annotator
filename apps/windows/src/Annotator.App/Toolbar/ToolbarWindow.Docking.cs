using System.Runtime.InteropServices;
using Annotator.App.Overlay;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using CorePoint = Annotator.Core.Point;
using CoreRect = Annotator.Core.Rect;
using CoreSize = Annotator.Core.Geometry.Size;

namespace Annotator.App.Toolbar;

/// <summary>
/// The toolbar's drag and docking (WIN-OVR-10, spec 009). A drag from anywhere on the toolbar, or a press and hold
/// (ToolbarWindow.Hold.cs), moves the toolbar inside the work area of the display under the pointer, with
/// Windows' move cursor (the Mac's closed hand). With the pointer at the outermost point of a display edge a mouse-transparent
/// ghost shows exactly where it will dock; releasing there docks it flush against that edge of the work area, centered,
/// vertical at the left and right edges and horizontal at the top and bottom, and saves the edge. The drag is the app's
/// own, not a window manager move, so Snap layouts never compete with it. The rules are <see cref="ToolbarDocking"/>'s;
/// as on the Mac, the toolbar takes its docked frame at once and the ghost appears and goes at once.
/// </summary>
internal sealed partial class ToolbarWindow
{
    private readonly ToolbarDocking _docking = new();
    /// <summary>The toolbar's size in points for each layout measured for the ghost during this drag.</summary>
    private readonly Dictionary<string, Size> _dockingSizes = [];
    private IReadOnlyList<DisplayDescriptor> _dragDisplays = [];
    /// <summary>The available size when the drag started, kept until it ends.</summary>
    private Size? _dragAvailable;
    private UIElement? _dragSurface;
    private DockPreviewWindow? _ghost;
    private ToolbarDockPreview? _ghostShown;
    private bool _ghostFailed;

    /// <summary>A drag of the toolbar's background starts on <paramref name="surface"/>; controls on it keep their own clicks.</summary>
    private void Watch(FrameworkElement surface)
    {
        surface.PointerPressed += OnPointerPressed;
        surface.PointerMoved += OnPointerMoved;
        surface.PointerReleased += OnPointerReleased;
        surface.PointerCaptureLost += (_, _) =>
        {
            // A press and hold moves the pointer to the toolbar's root; only the surface's own drag ends here.
            if (_docking.IsTracking && ReferenceEquals(_dragSurface, surface))
            {
                CancelDrag();
            }
        };
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var surface = (UIElement)sender;
        if (!args.GetCurrentPoint(surface).Properties.IsLeftButtonPressed || _docking.IsTracking || !surface.CapturePointer(args.Pointer))
        {
            return;
        }
        _interacted();
        _dragSurface = surface;
        _dragAvailable = Available();
        _dragDisplays = Displays();
        _dockingSizes.Clear();
        _ghostFailed = false;
        _docking.Press(EventPoint(args), ToCore(Frame), Scale());
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_docking.IsTracking)
        {
            return;
        }
        if (_docking.Move(DragPoint(args), WindowSize(), _dragDisplays, CanDock(), DockingSize) is { } step)
        {
            if (ToCore(Frame) != step.Frame)
            {
                MoveTo(step.Frame);
            }
            ShowGhost(step.Preview);
            _root.ShowMoveCursor(true);
            TakeEscape();
            args.Handled = true;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_docking.IsTracking)
        {
            return;
        }
        ToolbarDrop? drop = _docking.Release(DragPoint(args), WindowSize(), _dragDisplays, CanDock(), DockingSize);
        _docking.Cancel();
        EndDrag();
        ((UIElement)sender).ReleasePointerCapture(args.Pointer);
        args.Handled = true;
        if (drop is { } released)
        {
            Drop(released);
        }
    }

    /// <summary>
    /// Where the pointer was when this event happened, in desktop pixels (Mac pointerEventLocation): the press, while the
    /// window has not moved yet.
    /// </summary>
    private CorePoint EventPoint(PointerRoutedEventArgs args)
    {
        Windows.Foundation.Point position = args.GetCurrentPoint(null).Position;
        double scale = Scale();
        Windows.Graphics.PointInt32 window = AppWindow.Position;
        return new CorePoint(Math.Round(window.X + position.X * scale), Math.Round(window.Y + position.Y * scale));
    }

    /// <summary>
    /// The pointer during a drag, in desktop pixels. An event's position is relative to the window as it was when the event
    /// was queued; once the drag moves the window, a queued move read against the new position overshoots by the last step,
    /// and each step grew the next until the toolbar flew across the screen. The cursor's own position has no such loop.
    /// </summary>
    private CorePoint DragPoint(PointerRoutedEventArgs args) =>
        WindowChrome.TryCursor() is { } cursor ? new CorePoint(cursor.X, cursor.Y) : EventPoint(args);

    /// <summary>Mac finishToolbarDrag: the toolbar takes the dropped frame, and a drop at an edge docks it there.</summary>
    private void Drop(ToolbarDrop drop)
    {
        if (drop.Kind == ToolbarDropKind.Docked)
        {
            // The ghost's frame first, then the edge's layout: the toolbar laid out for the new orientation docks again with
            // its measured size, which is the ghost's.
            MoveTo(drop.Dock!.Frame);
            _model.DockToolbar(drop.Dock.Edge);
        }
        else
        {
            if (ToCore(Frame) != drop.Frame)
            {
                MoveTo(drop.Frame);
            }
            if (drop.Kind == ToolbarDropKind.Free)
            {
                // Another display has another available size.
                Relayout();
            }
        }
        if (_docking.TakePendingSize() is not null && _root.IsLoaded)
        {
            FitToContent();
        }
    }

    /// <summary>
    /// Mac cancelToolbarDrag: the drag ends where the toolbar is now, and the ghost goes. Hiding or showing the toolbar,
    /// a display change and a suspension (Mac onSuspendPresentation) end a drag this way.
    /// </summary>
    public void CancelDrag()
    {
        ClearHold();
        bool tracking = _docking.IsTracking;
        _docking.Cancel();
        UIElement? surface = _dragSurface;
        EndDrag();
        if (tracking)
        {
            surface?.ReleasePointerCaptures();
            if (_docking.TakePendingSize() is not null && _root.IsLoaded && _shown)
            {
                FitToContent();
            }
        }
    }

    private void EndDrag()
    {
        ReleaseEscape();
        _root.ShowMoveCursor(false);
        _dragSurface = null;
        _dragAvailable = null;
        _dragDisplays = [];
        _dockingSizes.Clear();
        ShowGhost(null);
    }

    /// <summary>Shows the ghost at <paramref name="preview"/>'s frame, or hides it. A ghost that fails is reported once per drag.</summary>
    private void ShowGhost(ToolbarDockPreview? preview)
    {
        if (preview == _ghostShown)
        {
            return;
        }
        _ghostShown = preview;
        if (_ghostFailed)
        {
            return;
        }
        try
        {
            if (preview is null)
            {
                _ghost?.Hide();
                return;
            }
            _ghost ??= new DockPreviewWindow(_registry, WindowChrome.SystemCompositor(), ToolbarParts.Color(DrawingCanvasFactory.Accent));
            CoreRect frame = preview.Frame;
            _ghost.Show(PixelRect.FromSize((int)Math.Round(frame.X), (int)Math.Round(frame.Y), (int)Math.Round(frame.Width), (int)Math.Round(frame.Height)),
                preview.Display.Scale);
        }
        catch (Exception error) when (error is NativeCallException or COMException or InvalidOperationException or ArgumentException)
        {
            _ghostFailed = true;
            StartupLog.Note("dock-preview", error.ToString().ReplaceLineEndings(" | "));
            _model.Report($"The docking preview could not appear. You can still drop the toolbar at a screen edge to dock it. Details: {error.Message}");
        }
    }

    /// <summary>
    /// Mac dockingSize(on:edge:): the toolbar's size in <paramref name="display"/>'s pixels when it docks at
    /// <paramref name="edge"/>: laid out in that edge's orientation for that display's available size. The toolbar's own
    /// layout gives it when it is the same; other layouts are measured once per drag.
    /// </summary>
    private CoreSize DockingSize(ToolbarDockEdge edge, DisplayDescriptor display)
    {
        WorkspaceSnapshot snapshot = _view ?? _model.Snapshot;
        double zoom = Zoom;
        var available = new Size((display.WorkArea.Width - 32) / zoom, (display.WorkArea.Height - 64) / zoom);
        ToolbarLook look = ToolbarLook.Of(snapshot, available, ToolbarDocking.OrientationFor(edge), edge);
        Size size;
        if (look.Structure == _structure)
        {
            size = Measure();
        }
        else if (!_dockingSizes.TryGetValue(look.Structure, out size))
        {
            Size laidOut = MeasureLayout(look, snapshot);
            size = new Size(laidOut.Width * zoom, laidOut.Height * zoom);
            _dockingSizes[look.Structure] = size;
        }
        return new CoreSize(Math.Ceiling(size.Width * display.Scale), Math.Ceiling(size.Height * display.Scale));
    }

    /// <summary>
    /// Measures the toolbar as <paramref name="look"/> lays it out, without changing the toolbar: the layout is built with
    /// its own updates and selection marker and measured inside the window, as the toolbar is, so templates and text
    /// measure as they will show. It leaves the window before the next layout pass.
    /// </summary>
    private Size MeasureLayout(ToolbarLook look, WorkspaceSnapshot snapshot)
    {
        List<Action<WorkspaceSnapshot>> updates = _updates;
        SelectionMarkers markers = _markers;
        MotionSamples? motion = _controlMotion;
        _updates = [];
        _markers = new SelectionMarkers(_root);
        _controlMotion = null;
        FrameworkElement? layout = null;
        try
        {
            layout = look.Collapsed ? Collapsed(look) : look.Vertical ? Vertical(look, snapshot, null) : Horizontal(look, snapshot, null);
            foreach (Action<WorkspaceSnapshot> update in _updates)
            {
                update(snapshot);
            }
            _root.Children.Add(layout);
            var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
            layout.Measure(unlimited);
            if (_zoom is not null && ZoomPanel.FreezeText(layout))
            {
                layout.Measure(unlimited);
            }
            return layout.DesiredSize;
        }
        finally
        {
            if (layout is not null)
            {
                _root.Children.Remove(layout);
            }
            _updates = updates;
            _markers = markers;
            _controlMotion = motion;
        }
    }

    /// <summary>The connected displays with their work areas, primary first (the live motion recordings dock in their own area).</summary>
    private IReadOnlyList<DisplayDescriptor> Displays()
    {
        if (_reference?.DockArea is { } area)
        {
            var rect = new CoreRect(area.X, area.Y, area.Width, area.Height);
            return [new DisplayDescriptor(new DisplayId("reference-dock"), rect, rect, WindowChrome.MonitorFor(area).Scale)];
        }
        try
        {
            return [.. DisplayEnumerator.GetDisplays().Select(OverlayHost.Describe)];
        }
        catch (NativeCallException error)
        {
            // Placement still works on the display the toolbar is on.
            StartupLog.Note("toolbar-displays", error.Message);
            return [Describe(WindowChrome.MonitorFor(Frame))];
        }
    }

    /// <summary>The display <paramref name="monitor"/> describes, from the connected displays.</summary>
    private DisplayDescriptor DisplayFor(MonitorArea monitor)
    {
        var bounds = new CoreRect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height);
        foreach (DisplayDescriptor display in Displays())
        {
            if (display.DesktopFrame == bounds || _reference?.DockArea is not null)
            {
                return display;
            }
        }
        return Describe(monitor);
    }

    private static DisplayDescriptor Describe(MonitorArea monitor) => new(new DisplayId("toolbar-display"),
        new CoreRect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.Width, monitor.Bounds.Height),
        new CoreRect(monitor.WorkArea.X, monitor.WorkArea.Y, monitor.WorkArea.Width, monitor.WorkArea.Height), monitor.Scale);

    private CoreSize WindowSize() => new(AppWindow.Size.Width, AppWindow.Size.Height);

    /// <summary>Mac: no ghost and no dock while the tools are hidden or a show or hide plays.</summary>
    private bool Presented() => _model.Snapshot is { ToolbarVisible: true, Visibility: null, WorkspaceHidden: false };

    /// <summary>The toolbar can dock now; reference captures with a fixed origin never dock.</summary>
    private bool CanDock() => Presented() && _reference is not { DockArea: null };
}
