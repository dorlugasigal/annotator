using Annotator.Core;
using Annotator.Workspace.Toolbar;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    /// <summary>Mac setQuickPreferencesExpanded: shows or collapses the toolbar's properties.</summary>
    public void SetQuickPreferencesExpanded(bool expanded)
    {
        using ChangeScope scope = Change();
        _preferences.QuickPreferencesExpanded = expanded;
        PersistPreferences();
    }

    /// <summary>Mac setInspectorPinned: full properties; pinning also expands the properties.</summary>
    public void SetInspectorPinned(bool pinned)
    {
        using ChangeScope scope = Change();
        _preferences.InspectorPinned = pinned;
        if (pinned)
        {
            _preferences.QuickPreferencesExpanded = true;
        }
        PersistPreferences();
    }

    /// <summary>Mac ToolbarPropertiesMode.apply(to:): what the Properties button and its key choose.</summary>
    public void ApplyPropertiesMode(ToolbarPropertiesMode mode)
    {
        using ChangeScope scope = Change();
        switch (mode)
        {
            case ToolbarPropertiesMode.ToolsOnly:
                SetQuickPreferencesExpanded(false);
                break;
            case ToolbarPropertiesMode.Quick:
                SetInspectorPinned(false);
                SetQuickPreferencesExpanded(true);
                break;
            case ToolbarPropertiesMode.Full:
                SetInspectorPinned(true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>
    /// The Properties key (Mac performShortcutAction(.properties)): the next of quick controls, full properties and
    /// tools only, with a status line naming it.
    /// </summary>
    public void CyclePropertiesMode()
    {
        using ChangeScope scope = Change();
        ToolbarPropertiesMode next = ToolbarPropertiesMode.From(_preferences).Next;
        ApplyPropertiesMode(next);
        _status = $"Properties: {next.RawValue.ToLowerInvariant()}.";
    }

    /// <summary>The canvas menu's Properties (Mac propertiesFromMenu): full properties in the shown toolbar.</summary>
    public void ShowFullProperties()
    {
        using ChangeScope scope = Change();
        SetInspectorPinned(true);
        SetQuickPreferencesExpanded(true);
        ShowToolbar();
    }

    /// <summary>
    /// Mac dockToolbar(to:): a toolbar dropped at a display edge docks there. Left and right make it vertical, top and
    /// bottom horizontal, and the edge and orientation are saved. Automatic changes nothing. Docking never moves drawings
    /// or changes the input mode.
    /// </summary>
    public void DockToolbar(ToolbarDockEdge edge)
    {
        if (edge == ToolbarDockEdge.Automatic)
        {
            return;
        }
        using ChangeScope scope = Change();
        _preferences.ToolbarOrientation = ToolbarDocking.OrientationFor(edge);
        _preferences.ToolbarDockEdge = edge;
        PersistPreferences();
    }

    /// <summary>Mac setToolbarDockEdge: saves the edge the toolbar docks to (Settings' Dock toolbar choice).</summary>
    public void SetToolbarDockEdge(ToolbarDockEdge edge)
    {
        using ChangeScope scope = Change();
        _preferences.ToolbarDockEdge = edge;
        PersistPreferences();
    }

    /// <summary>
    /// Mac setToolbarOrientation: saves the orientation; a saved edge that does not suit it (top or bottom for a vertical
    /// toolbar, left or right for a horizontal one) becomes Automatic.
    /// </summary>
    public void SetToolbarOrientation(ToolbarOrientation orientation)
    {
        using ChangeScope scope = Change();
        _preferences.ToolbarOrientation = orientation;
        if (orientation == ToolbarOrientation.Vertical && _preferences.ToolbarDockEdge is ToolbarDockEdge.Top or ToolbarDockEdge.Bottom)
        {
            _preferences.ToolbarDockEdge = ToolbarDockEdge.Automatic;
        }
        else if (orientation == ToolbarOrientation.Horizontal && _preferences.ToolbarDockEdge is ToolbarDockEdge.Left or ToolbarDockEdge.Right)
        {
            _preferences.ToolbarDockEdge = ToolbarDockEdge.Automatic;
        }
        PersistPreferences();
    }
}
