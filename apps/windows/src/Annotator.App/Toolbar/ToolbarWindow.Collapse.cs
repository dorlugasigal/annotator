using Annotator.Workspace;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Annotator.App.Toolbar;

/// <summary>
/// The toolbar that shrinks to one button (Mac ToolbarCollapseController and the app delegate's collapse wiring): once
/// the pointer has been off the toolbar for the delay chosen in Settings, it becomes the Annotator capsule; pointing at
/// the capsule or clicking it opens the toolbar again at once. Open menus and flyouts, a drag and typing keep it open.
/// The change fades the new face in; the Mac's frame morph (Core ToolbarMorph) is not ported yet.
/// </summary>
internal sealed partial class ToolbarWindow
{
    private ToolbarCollapseTimer? _collapseTimer;
    /// <summary>The collapsed state and delay the timer last followed; null while the toolbar is hidden.</summary>
    private bool? _collapseShown;
    private double? _collapseDelayShown;

    private ToolbarCollapseTimer CollapseTimer
    {
        get
        {
            if (_collapseTimer is { } existing)
            {
                return existing;
            }
            var timer = new ToolbarCollapseTimer(
                () => (_view ?? _model.Snapshot).Preferences.ToolbarCollapseDelay,
                () => (_view ?? _model.Snapshot).ToolbarCollapsed)
            {
                Collapse = CollapseToolbar,
                Expand = ExpandToolbar,
                IsHeldOpen = ToolbarMustStayOpen,
            };
            _root.PointerEntered += (_, _) => timer.PointerChanged(true);
            _root.PointerExited += (_, _) => timer.PointerChanged(false);
            _collapseTimer = timer;
            return timer;
        }
    }

    /// <summary>
    /// Follows a shown snapshot: the wait starts again when the toolbar appears, opens or gets another delay. True when
    /// the toolbar just changed between its full face and the capsule.
    /// </summary>
    private bool FollowCollapse(WorkspaceSnapshot snapshot, bool appearing)
    {
        ToolbarCollapseTimer timer = CollapseTimer;
        double? delay = snapshot.Preferences.ToolbarCollapseDelay;
        bool collapsed = snapshot.ToolbarCollapsed;
        bool changed = _collapseShown is { } was && was != collapsed;
        if (appearing || delay != _collapseDelayShown || (changed && !collapsed))
        {
            timer.Restart();
        }
        _collapseShown = collapsed;
        _collapseDelayShown = delay;
        return changed;
    }

    /// <summary>The toolbar hid: nothing waits to shrink it.</summary>
    private void StopCollapse()
    {
        _collapseTimer?.Cancel();
        _collapseShown = null;
    }

    private void CollapseToolbar()
    {
        if (_closing || !_shown)
        {
            return;
        }
        if (ToolbarMustStayOpen())
        {
            CollapseTimer.Restart();
            return;
        }
        _model.SetToolbarCollapsed(true);
    }

    /// <summary>Opens the shrunk toolbar: a click on the capsule, its context menu or the pointer reaching it.</summary>
    private void ExpandToolbar()
    {
        if ((_view ?? _model.Snapshot).ToolbarCollapsed)
        {
            _model.SetToolbarCollapsed(false);
        }
    }

    /// <summary>Mac toolbarMustStayOpen: an open menu, flyout or popover, a drag of the toolbar, or typing in a field.</summary>
    private bool ToolbarMustStayOpen()
    {
        if (_docking.IsTracking || !_typingReturn.IsNull)
        {
            return true;
        }
        return _root.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;
    }

    /// <summary>The new face fades in after the toolbar shrinks or opens (Reduce Motion keeps the short fade).</summary>
    private void RevealMorph()
    {
        if (_root.Children.Count > 0 && _root.Children[0] is FrameworkElement face)
        {
            PropertiesReveal.Start(face);
        }
    }
}
