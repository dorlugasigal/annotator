using Annotator.Core;
using Size = Annotator.Core.Geometry.Size;

namespace Annotator.Workspace.Toolbar;

/// <summary>
/// Where a dragged toolbar docks if it is released now (Mac dockPreviewFrame, previewEdge and previewScreen). The ghost
/// shows exactly <paramref name="Frame"/>, in desktop units.
/// </summary>
public sealed record ToolbarDockPreview(ToolbarDockEdge Edge, DisplayDescriptor Display, Rect Frame);

/// <summary>What releasing a toolbar drag does.</summary>
public enum ToolbarDropKind
{
    /// <summary>The toolbar did not move: the drag is cancelled (Mac onUserDragCancelled).</summary>
    Unmoved,

    /// <summary>The pointer was at a display edge: the toolbar docks at <see cref="ToolbarDrop.Dock"/>.</summary>
    Docked,

    /// <summary>The toolbar stays where it was dropped.</summary>
    Free,

    /// <summary>The tools were hidden or moving when the drag ended, so the placement stays as it is (Mac finishToolbarDrag's guard).</summary>
    Ignored,
}

/// <summary>
/// The release of a toolbar drag. <paramref name="Frame"/> is the toolbar's frame at the release point (the drag's last
/// step), <paramref name="Dock"/> the docked frame of a <see cref="ToolbarDropKind.Docked"/> drop, and
/// <paramref name="Display"/> the display it was dropped on.
/// </summary>
public readonly record struct ToolbarDrop(ToolbarDropKind Kind, Rect Frame, ToolbarDockPreview? Dock = null, DisplayDescriptor? Display = null);

/// <summary>One step of a toolbar drag: the toolbar's frame and where it would dock now (null away from the edges).</summary>
public readonly record struct ToolbarDragStep(Rect Frame, ToolbarDockPreview? Preview);

/// <summary>
/// The toolbar's placement, drag and docking (WIN-OVR-10, spec 009): a port of the Mac AppDelegate's toolbar placement
/// (showToolbar, dockToolbar, resizeToolbar, recoverToolbarPlacement, updateDockPreview and finishToolbarDrag) and of
/// InspectorPanel's app-owned drag. Frames, pointers and displays use desktop units (physical pixels on Windows) with a
/// top-left origin, and work areas stand for the Mac's visible frames (WIN-OVR-05); the Mac's distances in points are
/// scaled by each display's scale. It owns no window: the host moves the toolbar to the frames it returns, shows the
/// ghost at <see cref="Preview"/> and measures the toolbar for an edge when a preview needs it. Use it on one thread.
/// </summary>
public sealed class ToolbarDocking
{
    /// <summary>Mac DragGesture(minimumDistance: 3): a drag on the toolbar's background starts after 3 points.</summary>
    public const double DragThreshold = 3;

    /// <summary>ToolbarPlacement.frame's default inset: the startup placement sits 12 points in from the edges.</summary>
    public const double StartupInset = 12;

    private Point? _press;
    private Rect _pressFrame;
    private double _pressScale = 1;
    private Point? _dragOrigin;
    private Rect _dragFrame;
    private Rect _dragCurrent;

    /// <summary>Mac dockedFrame: where the toolbar was docked, while it has not been dragged away.</summary>
    public Rect? DockedFrame { get; private set; }

    /// <summary>Mac toolbarDockDisplayID: the display the toolbar is docked on.</summary>
    public DisplayId? DockDisplay { get; private set; }

    /// <summary>Mac edgeDocked: dropped at an edge, so it sits flush against the work area (the startup placement keeps 12 points).</summary>
    public bool EdgeDocked { get; private set; }

    /// <summary>Mac pendingToolbarSize: a content size that arrived during a drag, applied when it ends.</summary>
    public Size? PendingSize { get; private set; }

    /// <summary>Where the toolbar docks if the drag ends now; null while the pointer is away from the edges.</summary>
    public ToolbarDockPreview? Preview { get; private set; }

    /// <summary>A drag moves the toolbar (Mac isTrackingToolbarDrag).</summary>
    public bool IsDragging => _dragOrigin is not null;

    /// <summary>A press on the background waits to become a drag, or a drag runs.</summary>
    public bool IsTracking => _press is not null || _dragOrigin is not null;

    /// <summary>Left and right edges hold a vertical toolbar; top and bottom a horizontal one.</summary>
    public static ToolbarOrientation OrientationFor(ToolbarDockEdge edge) =>
        edge is ToolbarDockEdge.Left or ToolbarDockEdge.Right ? ToolbarOrientation.Vertical : ToolbarOrientation.Horizontal;

    /// <summary>
    /// Mac showToolbar's first placement: docked at the saved edge in the saved orientation, 12 points in from the edges of
    /// the work area of <paramref name="display"/>, the display under the pointer.
    /// </summary>
    public Rect Place(Size size, DisplayDescriptor display, ToolbarOrientation orientation, ToolbarDockEdge edge)
    {
        ArgumentNullException.ThrowIfNull(display);
        Rect frame = ToolbarPlacement.Frame(size.Width, size.Height, display.DesktopWorkArea, orientation, edge, StartupInset * display.Scale);
        DockDisplay = display.Id;
        DockedFrame = frame;
        EdgeDocked = false;
        PendingSize = null;
        return frame;
    }

    /// <summary>
    /// A press on the toolbar's empty background at <paramref name="pointer"/>, with the toolbar at <paramref name="frame"/>
    /// on a display of <paramref name="scale"/>. It becomes a drag once the pointer moves 3 points.
    /// </summary>
    public void Press(Point pointer, Rect frame, double scale)
    {
        if (IsTracking)
        {
            return;
        }
        _press = pointer;
        _pressFrame = frame;
        _pressScale = double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    /// <summary>
    /// Mac updateToolbarDrag and updateDockPreview: the pointer moved to <paramref name="pointer"/>. Returns the toolbar's
    /// frame, kept inside the work area of the display under the pointer (the pointer itself is never clamped, so it
    /// still reaches the edges), and where the toolbar would dock now; null before the drag starts. <paramref name="size"/>
    /// is the toolbar's size now. <paramref name="presented"/> is false while the tools are hidden or a show or hide
    /// plays. <paramref name="dockingSize"/> measures the toolbar for an edge of a display.
    /// </summary>
    public ToolbarDragStep? Move(Point pointer, Size size, IReadOnlyList<DisplayDescriptor> displays, bool presented,
                                 Func<ToolbarDockEdge, DisplayDescriptor, Size> dockingSize)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(dockingSize);
        if (_dragOrigin is null)
        {
            if (_press is not { } press || press.Distance(pointer) < DragThreshold * _pressScale)
            {
                return null;
            }
            // Mac beginToolbarDrag and onUserDragStarted: the drag starts at the press, and the toolbar is no longer docked.
            _dragOrigin = press;
            _dragFrame = _pressFrame;
            _dragCurrent = _pressFrame;
            _press = null;
            DockedFrame = null;
            DockDisplay = null;
            EdgeDocked = false;
        }
        Point origin = _dragOrigin.Value;
        var proposed = new Rect(_dragFrame.X + pointer.X - origin.X, _dragFrame.Y + pointer.Y - origin.Y, size.Width, size.Height);
        if ((DisplayContaining(pointer, displays) ?? DisplayOf(_dragCurrent, displays)) is { } destination)
        {
            proposed = ToolbarPlacement.ConstrainedDragFrame(proposed, destination.DesktopWorkArea);
        }
        _dragCurrent = proposed;
        return new ToolbarDragStep(proposed, UpdatePreview(pointer, displays, presented, dockingSize));
    }

    /// <summary>
    /// Mac updateDockPreview: with the pointer at the outermost point of a display edge (within 1 point of the display's
    /// bounds), the frame the toolbar would dock to: flush against that edge of the work area, centered along it, in the
    /// edge's orientation. Otherwise null: moving even slightly inward leaves the toolbar free, with no near-edge attraction.
    /// The measured size is kept while the pointer stays on the same edge of the same display.
    /// </summary>
    public ToolbarDockPreview? UpdatePreview(Point pointer, IReadOnlyList<DisplayDescriptor> displays, bool presented,
                                             Func<ToolbarDockEdge, DisplayDescriptor, Size> dockingSize)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(dockingSize);
        if (!presented || DisplayTouching(pointer, displays) is not { } display ||
            ToolbarPlacement.DockEdge(pointer, display.DesktopFrame, display.Scale) is not { } edge)
        {
            Preview = null;
            return null;
        }
        Size size = Preview is { } shown && shown.Edge == edge && shown.Display == display
            ? new Size(shown.Frame.Width, shown.Frame.Height)
            : dockingSize(edge, display);
        Rect frame = ToolbarPlacement.Frame(size.Width, size.Height, display.DesktopWorkArea, OrientationFor(edge), edge, inset: 0);
        if (Preview is not { } current || current.Edge != edge || current.Display != display || current.Frame != frame)
        {
            Preview = new ToolbarDockPreview(edge, display, frame);
        }
        return Preview;
    }

    /// <summary>
    /// Mac endToolbarDrag and finishToolbarDrag: the button was released at <paramref name="pointer"/>. With the pointer at an
    /// edge the toolbar docks there, at exactly the ghost's frame; anywhere else it stays where it was dropped. A drag that
    /// did not move the toolbar is cancelled. Returns null for a press that never became a drag (a click on the background).
    /// </summary>
    public ToolbarDrop? Release(Point pointer, Size size, IReadOnlyList<DisplayDescriptor> displays, bool presented,
                                Func<ToolbarDockEdge, DisplayDescriptor, Size> dockingSize)
    {
        if (_dragOrigin is null)
        {
            _press = null;
            return null;
        }
        Rect original = _dragFrame;
        ToolbarDragStep step = Move(pointer, size, displays, presented, dockingSize)!.Value;
        _dragOrigin = null;
        _press = null;
        if (step.Frame == original)
        {
            Preview = null;
            return new ToolbarDrop(ToolbarDropKind.Unmoved, step.Frame);
        }
        ToolbarDockPreview? target = Preview;
        Preview = null;
        DisplayDescriptor? display = target?.Display ?? DisplayContaining(pointer, displays) ?? DisplayOf(step.Frame, displays);
        if (!presented || display is null)
        {
            return new ToolbarDrop(ToolbarDropKind.Ignored, step.Frame);
        }
        if (target is not null)
        {
            DockDisplay = display.Id;
            EdgeDocked = true;
            PendingSize = null;
            DockedFrame = target.Frame;
            return new ToolbarDrop(ToolbarDropKind.Docked, step.Frame, target, display);
        }
        EdgeDocked = false;
        DockDisplay = null;
        DockedFrame = null;
        return new ToolbarDrop(ToolbarDropKind.Free, step.Frame, null, display);
    }

    /// <summary>Mac cancelToolbarDrag: the drag ends where the toolbar is now, and the ghost goes.</summary>
    public void Cancel()
    {
        _press = null;
        _dragOrigin = null;
        Preview = null;
    }

    /// <summary>
    /// Mac resizeToolbar: the toolbar's content now measures <paramref name="size"/>. During a drag, or while the host
    /// <paramref name="deferred"/> it, the size waits in <see cref="PendingSize"/>. A docked toolbar docks again with the
    /// new size; a free one keeps its top-left corner, inside the work area. Returns the new frame, or null without a change.
    /// </summary>
    public Rect? Resize(Rect frame, Size size, IReadOnlyList<DisplayDescriptor> displays, ToolbarOrientation orientation,
                        ToolbarDockEdge edge, bool deferred = false)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (!(size.Width > 0 && size.Height > 0 && double.IsFinite(size.Width) && double.IsFinite(size.Height)))
        {
            return null;
        }
        if (deferred || IsDragging)
        {
            PendingSize = size;
            return null;
        }
        if (EdgeDocked)
        {
            return Changed(frame, Dock(size, frame, displays, orientation, edge));
        }
        DisplayDescriptor? screen = DockScreen(frame, displays);
        // Mac: a window that still sits where it was docked is docked, even when its size already matches.
        if (DockedFrame is { } docked && screen is not null && Near(frame.X, docked.X, screen.Scale) &&
            (Near(frame.Y, docked.Y, screen.Scale) || Near(frame.MaxY, docked.MaxY, screen.Scale)))
        {
            return Changed(frame, Dock(size, frame, displays, orientation, edge));
        }
        var resized = new Rect(frame.X, frame.Y, size.Width, size.Height);
        if (screen is not null)
        {
            resized = ToolbarPlacement.ConstrainedResizedFrame(resized, screen.DesktopWorkArea);
        }
        return Changed(frame, resized);
    }

    /// <summary>
    /// Mac dockToolbar(_:size:): docks at <paramref name="edge"/> in <paramref name="orientation"/> on the docking display, or
    /// the display the toolbar is on: flush against its work area after a drop at an edge, else 12 points in.
    /// </summary>
    public Rect Dock(Size size, Rect frame, IReadOnlyList<DisplayDescriptor> displays, ToolbarOrientation orientation, ToolbarDockEdge edge)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (DockScreen(frame, displays) is not { } screen)
        {
            return frame;
        }
        Rect docked = ToolbarPlacement.Frame(size.Width, size.Height, screen.DesktopWorkArea, orientation, edge,
            EdgeDocked ? 0 : StartupInset * screen.Scale);
        DockedFrame = docked;
        return docked;
    }

    /// <summary>Mac applyPendingToolbarSize: the size that waited for the drag to end, once.</summary>
    public Size? TakePendingSize()
    {
        Size? size = PendingSize;
        PendingSize = null;
        return size;
    }

    /// <summary>
    /// Mac recoverToolbarPlacement: a toolbar outside every work area (its display went away or shrank) moves into the first
    /// work area it overlaps, or the first display's (the primary display comes first), at most that area's size. It is no
    /// longer docked. Returns null while it is inside a work area.
    /// </summary>
    public Rect? Recover(Rect frame, IReadOnlyList<DisplayDescriptor> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
        {
            return null;
        }
        DisplayDescriptor? target = null;
        foreach (DisplayDescriptor display in displays)
        {
            if (display.DesktopWorkArea.Contains(frame))
            {
                return null;
            }
            if (target is null && Intersects(display.DesktopWorkArea, frame))
            {
                target = display;
            }
        }
        Rect area = (target ?? displays[0]).DesktopWorkArea;
        Rect recovered = ToolbarPlacement.ConstrainedDragFrame(
            new Rect(frame.X, frame.Y, Math.Min(frame.Width, area.Width), Math.Min(frame.Height, area.Height)), area);
        DockDisplay = null;
        DockedFrame = null;
        EdgeDocked = false;
        PendingSize = null;
        return recovered;
    }

    /// <summary>Mac toolbarDockScreen(for:): the docking display, else the first display the frame overlaps, else the first.</summary>
    public DisplayDescriptor? DockScreen(Rect frame, IReadOnlyList<DisplayDescriptor> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (DockDisplay is { } id)
        {
            foreach (DisplayDescriptor display in displays)
            {
                if (display.Id == id)
                {
                    return display;
                }
            }
        }
        foreach (DisplayDescriptor display in displays)
        {
            if (Intersects(display.DesktopFrame, frame))
            {
                return display;
            }
        }
        return displays.Count > 0 ? displays[0] : null;
    }

    /// <summary>The display whose pixels hold <paramref name="pointer"/>.</summary>
    public static DisplayDescriptor? DisplayContaining(Point pointer, IReadOnlyList<DisplayDescriptor> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        foreach (DisplayDescriptor display in displays)
        {
            if (display.ContainsDesktopPoint(pointer))
            {
                return display;
            }
        }
        return null;
    }

    /// <summary>
    /// Mac updateDockPreview's screen: the display that holds the pointer, else the first whose bounds grown by one point
    /// hold it. On an edge two displays share, each display's own pixels dock to its own edge.
    /// </summary>
    private static DisplayDescriptor? DisplayTouching(Point pointer, IReadOnlyList<DisplayDescriptor> displays)
    {
        if (DisplayContaining(pointer, displays) is { } display)
        {
            return display;
        }
        foreach (DisplayDescriptor candidate in displays)
        {
            if (candidate.DesktopFrame.Contains(pointer, candidate.Scale))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>NSWindow.screen: the display the frame overlaps most.</summary>
    private static DisplayDescriptor? DisplayOf(Rect frame, IReadOnlyList<DisplayDescriptor> displays)
    {
        DisplayDescriptor? best = null;
        double largest = 0;
        foreach (DisplayDescriptor display in displays)
        {
            double area = Overlap(display.DesktopFrame, frame);
            if (area > largest)
            {
                largest = area;
                best = display;
            }
        }
        return best;
    }

    private static double Overlap(Rect a, Rect b)
    {
        double width = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.X, b.X);
        double height = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.Y, b.Y);
        return width > 0 && height > 0 ? width * height : 0;
    }

    private static bool Intersects(Rect a, Rect b) => Overlap(a, b) > 0;

    /// <summary>The Mac's "within 1 point" for frame edges.</summary>
    private static bool Near(double a, double b, double scale) => Math.Abs(a - b) < scale;

    private static Rect? Changed(Rect before, Rect after) => after == before ? null : after;
}
