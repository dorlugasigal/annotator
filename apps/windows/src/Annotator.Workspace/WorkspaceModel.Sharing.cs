using Annotator.Core;
using Annotator.Workspace.Sharing;

namespace Annotator.Workspace;

/// <summary>
/// Region sharing (specification 012, WIN-CAP-08): the workspace keeps the <see cref="Sharing.RegionSharing"/> the host
/// attaches, publishes its state in every snapshot, prepares the shared display's canvas, and stops sharing for safety on
/// system and display changes, as the Mac WorkspaceModel does.
/// </summary>
public sealed partial class WorkspaceModel
{
    private RegionSharing? _sharing;

    /// <summary>The host's region sharing, or null when this workspace has none (tests and reference states).</summary>
    public RegionSharing? RegionSharing => _sharing;

    /// <summary>Mac openRegionSharing: the Share region button, its key and the Tools tile open the Share a region window.</summary>
    /// <returns>False when no region sharing is attached.</returns>
    public bool OpenRegionSharing()
    {
        VerifyAccess();
        if (_sharing is not { } sharing)
        {
            return false;
        }
        sharing.ShowControls();
        return true;
    }

    internal void Attach(RegionSharing sharing)
    {
        VerifyAccess();
        if (_sharing is not null)
        {
            throw new InvalidOperationException("The workspace already has region sharing.");
        }
        _sharing = sharing;
    }

    /// <summary>A change of region sharing, published with the workspace's next snapshot.</summary>
    internal ChangeScope SharingChange() => Change();

    /// <summary>Mac visibilityAnimating: drawings and tools show or hide with motion now.</summary>
    internal bool VisibilityAnimating => _transition is not null;

    /// <summary>The pointer in desktop units, or null when it is unknown or cannot be read.</summary>
    internal Point? PointerPosition()
    {
        try
        {
            return _pointer();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mac displaysForRegionSharing: the connected displays, refused as a whole when two share an identity or one is
    /// smaller than 64 points, larger than 16,384 pixels on a side or 40 million pixels.
    /// </summary>
    internal IReadOnlyList<DisplayDescriptor> DisplaysForRegionSharing()
    {
        IReadOnlyList<DisplayDescriptor> displays = _displaySource();
        bool usable = displays.Select(display => display.Id).Distinct().Count() == displays.Count && displays.All(display =>
            display.Bounds.IsValid && display.Bounds.Width >= 64 && display.Bounds.Height >= 64
            && display.DesktopFrame.Width <= 16_384 && display.DesktopFrame.Height <= 16_384
            && display.DesktopFrame.Width * display.DesktopFrame.Height <= 40_000_000);
        return usable ? displays : throw new RegionSharingFailure(RegionSharingText.CaptureDisplayUnavailable, capture: true);
    }

    /// <summary>
    /// Mac prepareRegionSharingSession: the shared display gets its canvas, which becomes the editing target when there is
    /// none, and Off becomes Use apps so its drawings show.
    /// </summary>
    internal void PrepareRegionSharingSession(DisplayId id)
    {
        using ChangeScope scope = Change();
        DisplayDescriptor display = DisplaysForRegionSharing().FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new RegionSharingFailure(RegionSharingText.CaptureDisplayUnavailable, capture: true);
        if (!_canvases.TryGetValue(id, out Canvas? canvas))
        {
            canvas = new Canvas(display, CanvasDocument.Empty(), FreshVersion());
            _canvases[id] = canvas;
        }
        if (_active is null)
        {
            _active = canvas;
            canvas.Board = Board.Desktop;
        }
        if (_mode == WorkspaceMode.Off)
        {
            _mode = WorkspaceMode.UseApps;
        }
        if (!_hidden)
        {
            SetDrawing(canvas, _mode == WorkspaceMode.Draw, focus: false);
        }
        Synchronize();
    }
}
