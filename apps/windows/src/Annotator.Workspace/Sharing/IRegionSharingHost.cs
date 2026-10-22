using Annotator.Core;

namespace Annotator.Workspace.Sharing;

/// <summary>
/// The native side of region sharing on the app thread (Mac RegionSharingController's windows): the Share a region
/// window, the guide outline, the custom selection panels and a timer. The output window is drawn from
/// <see cref="WorkspaceSnapshot.RegionSharing"/> on the thread that renders it.
/// </summary>
public interface IRegionSharingHost
{
    /// <summary>The name people see for a display (Mac NSScreen.localizedName), or null to use "Display N".</summary>
    string? DisplayName(DisplayId display);

    /// <summary>True while the Windows session is locked (Mac DisplayCapture.desktopIsLocked).</summary>
    bool DesktopIsLocked { get; }

    /// <summary>Whether the Share a region window is on screen (Mac controls?.isVisible).</summary>
    bool ControlsVisible { get; }

    /// <summary>Creates the Share a region window if needed and brings it to the front with the keyboard (makeKeyAndOrderFront).</summary>
    void ShowControls();

    /// <summary>Hides the Share a region window during a custom selection (orderOut).</summary>
    void HideControls();

    /// <summary>Shows the window again after a selection, with the keyboard or behind the window that took it (orderFront).</summary>
    void RestoreControls(bool activate);

    /// <summary>Closes the Share a region window for good (quit).</summary>
    void CloseControls();

    /// <summary>Shows the click-through outline of <paramref name="region"/> on <paramref name="display"/>; the fill only while choosing.</summary>
    void ShowOutline(SharingRegion region, DisplayDescriptor display, bool selectionFill);

    void HideOutline();

    /// <summary>True while the selection panels take input.</summary>
    bool IsSelecting { get; }

    /// <summary>
    /// Prepares a selection panel with a crosshair on every display, gives the keyboard to <paramref name="preferred"/>'s
    /// panel and calls <paramref name="completed"/> once, after releasing every panel.
    /// </summary>
    void ChooseRegion(IReadOnlyList<RegionSharingDisplay> displays, DisplayId preferred, Action<RegionSelectionResult> completed);

    /// <summary>Releases every selection panel without calling the pending completion.</summary>
    void CancelSelection();

    /// <summary>Calls <paramref name="action"/> once on the app thread after <paramref name="delay"/>, unless disposed first.</summary>
    IDisposable Schedule(TimeSpan delay, Action action);
}
