using Annotator.Workspace.Zoom;

namespace Annotator.Workspace.Toolbar;

/// <summary>
/// The modes the toolbar's strip shows with a one-click Stop button (Mac <c>ActiveToolbarMode</c>, ToolbarView.swift), in
/// the Mac's order.
/// </summary>
public enum ActiveToolbarMode
{
    ScreenZoom,
    Magnifier,
    Spotlight,
    CursorHalo,
    Whiteboard,
    Blackboard,
}

public static class ActiveToolbarModes
{
    public static IReadOnlyList<ActiveToolbarMode> All { get; } = Enum.GetValues<ActiveToolbarMode>();

    extension(ActiveToolbarMode mode)
    {
        /// <summary>The Mac's raw value, used in the Stop button's identifier (stop-active-screenZoom).</summary>
        public string RawValue => mode switch
        {
            ActiveToolbarMode.ScreenZoom => "screenZoom",
            ActiveToolbarMode.Magnifier => "magnifier",
            ActiveToolbarMode.Spotlight => "spotlight",
            ActiveToolbarMode.CursorHalo => "cursorHalo",
            ActiveToolbarMode.Whiteboard => "whiteboard",
            ActiveToolbarMode.Blackboard => "blackboard",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        public string Title => mode switch
        {
            ActiveToolbarMode.ScreenZoom => "Screen zoom",
            ActiveToolbarMode.Magnifier => "Pointer lens",
            ActiveToolbarMode.Spotlight => "Spotlight",
            ActiveToolbarMode.CursorHalo => "Cursor halo",
            ActiveToolbarMode.Whiteboard => "Whiteboard",
            ActiveToolbarMode.Blackboard => "Blackboard",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        /// <summary>The compact strip's caption before " on".</summary>
        public string ShortTitle => mode switch
        {
            ActiveToolbarMode.ScreenZoom => "Zoom",
            ActiveToolbarMode.Magnifier => "Lens",
            ActiveToolbarMode.Spotlight => "Spot",
            ActiveToolbarMode.CursorHalo => "Halo",
            ActiveToolbarMode.Whiteboard => "White",
            ActiveToolbarMode.Blackboard => "Black",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        /// <summary>The Mac SF Symbol name.</summary>
        public string Symbol => mode switch
        {
            ActiveToolbarMode.ScreenZoom => "arrow.up.left.and.arrow.down.right",
            ActiveToolbarMode.Magnifier => "plus.magnifyingglass",
            ActiveToolbarMode.Spotlight => "flashlight.on.fill",
            ActiveToolbarMode.CursorHalo => "circle.dotted",
            ActiveToolbarMode.Whiteboard => "rectangle.fill",
            ActiveToolbarMode.Blackboard => "rectangle",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

        /// <summary>Mac isActive(in:). Screen zoom counts once its start is no longer pending.</summary>
        public bool IsActive(WorkspaceSnapshot snapshot) => mode switch
        {
            ActiveToolbarMode.ScreenZoom => snapshot.ScreenZoomEnabled && PendingZoomPresentation.For(snapshot.ZoomStart) is null,
            ActiveToolbarMode.Magnifier => snapshot.MagnifierEnabled,
            ActiveToolbarMode.Spotlight => snapshot.Spotlight,
            ActiveToolbarMode.CursorHalo => snapshot.CursorHalo,
            ActiveToolbarMode.Whiteboard => snapshot.Board == Board.Whiteboard,
            ActiveToolbarMode.Blackboard => snapshot.Board == Board.Blackboard,
            _ => false,
        };

        /// <summary>The Stop button's help: what clicking it ends.</summary>
        public string StopHint => mode is ActiveToolbarMode.Whiteboard or ActiveToolbarMode.Blackboard
            ? "Return to the desktop background" : $"Turn off {mode.Title.ToLowerInvariant()}";

        /// <summary>Mac stop(in:): zoom and the lens stop, Spotlight and Cursor halo turn off, a board returns to the desktop.</summary>
        public void Stop(WorkspaceModel model)
        {
            ArgumentNullException.ThrowIfNull(model);
            switch (mode)
            {
                case ActiveToolbarMode.ScreenZoom or ActiveToolbarMode.Magnifier:
                    model.StopZoom();
                    break;
                case ActiveToolbarMode.Spotlight:
                    model.SetSpotlight(false);
                    break;
                case ActiveToolbarMode.CursorHalo:
                    model.SetCursorHalo(false);
                    break;
                default:
                    model.SetBoard(Board.Desktop);
                    break;
            }
        }
    }

    /// <summary>Mac activeModes: the modes that are on, in the Mac's order.</summary>
    public static IReadOnlyList<ActiveToolbarMode> Active(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var active = new List<ActiveToolbarMode>();
        foreach (ActiveToolbarMode mode in All)
        {
            if (mode.IsActive(snapshot))
            {
                active.Add(mode);
            }
        }
        return active;
    }

    /// <summary>Mac showsModeStrip: a pending zoom or any active mode shows the strip.</summary>
    public static bool ShowsStrip(WorkspaceSnapshot snapshot) =>
        PendingZoomPresentation.For(snapshot.ZoomStart) is not null || Active(snapshot).Count > 0;

    /// <summary>Mac: the live zoom controls show below the modes while screen zoom is on and not pending.</summary>
    public static bool ShowsZoomControls(WorkspaceSnapshot snapshot) =>
        snapshot.ScreenZoomEnabled && PendingZoomPresentation.For(snapshot.ZoomStart) is null;
}

/// <summary>
/// Mac <c>PendingZoomPresentation</c>: the orange Cancel control while a zoom request waits or starts. A packaged build waits
/// for screen access while Windows has not asked yet (WIN-CAP-04); hooks need no permission (WIN-INP-08), so the gesture
/// phase keeps the Mac's words but never occurs.
/// </summary>
public sealed record PendingZoomPresentation(string Phase, string Detail)
{
    public string Title => $"Zoom {Phase.ToLowerInvariant()}";

    public static PendingZoomPresentation? For(ZoomStartState state) => state.Phase switch
    {
        ZoomStartPhase.ScreenPermission => new("Waiting", Capture.ScreenAccessText.ZoomWaitingDetail),
        ZoomStartPhase.GesturePermission => new("Waiting", "Waiting for Accessibility permission"),
        ZoomStartPhase.Starting => new("Starting", "Starting live screen zoom"),
        _ => null,
    };
}