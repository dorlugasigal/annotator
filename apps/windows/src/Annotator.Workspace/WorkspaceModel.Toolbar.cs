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

    /// <summary>Mac setToolbarControl: shows or hides one of the toolbar's own buttons or menus. Its key keeps working.</summary>
    public void SetToolbarControl(ToolbarControl control, bool shown)
    {
        using ChangeScope scope = Change();
        WorkspacePreferences updated = _preferences;
        updated.SetToolbarControl(control, shown);
        try
        {
            updated.Validate();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
            return;
        }
        _preferences = updated;
        PersistPreferences();
    }

    /// <summary>Mac showAllToolbarControls: Reset > Show every toolbar button.</summary>
    public void ShowAllToolbarControls()
    {
        using ChangeScope scope = Change();
        if (_preferences.HiddenToolbarControls is null)
        {
            return;
        }
        _preferences.HiddenToolbarControls = null;
        PersistPreferences();
    }

    /// <summary>
    /// Mac setShowsAllChoices: a property with a few choices shows all of them side by side, or one button that steps
    /// through them (Settings > Toolbar > Your properties).
    /// </summary>
    public void SetShowsAllChoices(PropertyChoice choice, bool shown)
    {
        using ChangeScope scope = Change();
        WorkspacePreferences updated = _preferences;
        updated.SetShowsAllChoices(choice, shown);
        try
        {
            updated.Validate();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
            return;
        }
        _preferences = updated;
        PersistPreferences();
    }

    /// <summary>Mac resetPropertyChoices: "Use one button everywhere".</summary>
    public void ResetPropertyChoices()
    {
        using ChangeScope scope = Change();
        if (_preferences.ExpandedPropertyChoices is null)
        {
            return;
        }
        _preferences.ExpandedPropertyChoices = null;
        PersistPreferences();
    }

    /// <summary>
    /// Mac setToolbarCollapseDelay: how many seconds the pointer can stay off the toolbar before it shrinks to one button;
    /// null keeps it open. Turning it off opens a shrunk toolbar.
    /// </summary>
    public void SetToolbarCollapseDelay(double? delay)
    {
        using ChangeScope scope = Change();
        WorkspacePreferences updated = _preferences;
        updated.ToolbarCollapseDelay = delay;
        try
        {
            updated.Validate();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
            return;
        }
        _preferences = updated;
        PersistPreferences();
        if (delay is null)
        {
            _toolbarCollapsed = false;
        }
    }

    /// <summary>
    /// Mac setToolbarCollapsed: the toolbar shows as one button, or as every tool again. The host animates the change
    /// (Core <c>ToolbarMorph</c>) and decides when with <see cref="Toolbar.ToolbarCollapseTimer"/>. Hiding the toolbar
    /// opens it again for the next time it shows.
    /// </summary>
    public void SetToolbarCollapsed(bool collapsed)
    {
        using ChangeScope scope = Change();
        _toolbarCollapsed = collapsed;
    }
}
