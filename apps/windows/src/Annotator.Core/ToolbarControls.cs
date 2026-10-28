namespace Annotator.Core;

/// <summary>
/// Swift `ToolbarControl` (ToolbarControls.swift): the toolbar's own buttons and menus, next to the tools. People can
/// turn each one off in Settings; the tools and added buttons such as Spotlight are arranged separately
/// (<see cref="WorkspacePreferences.ToolbarItems"/>).
/// </summary>
public enum ToolbarControl { Library, Board, Mode, Undo, Redo, ClearAll, Properties, Settings, Hide, FocusMenu, ZoomMenu }

public static class ToolbarControlExtensions
{
    internal static readonly RawValueMap<ToolbarControl> RawValues = new(
        "library", "board", "mode", "undo", "redo", "clearAll", "properties", "settings", "hide", "focusMenu", "zoomMenu");

    extension(ToolbarControl)
    {
        public static ValueList<ToolbarControl> AllCases => RawValues.AllCases;

        public static ToolbarControl? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }

    extension(ToolbarControl control)
    {
        public string RawValue => RawValues.RawValue(control);

        public string Label => control switch
        {
            ToolbarControl.Library => "More tools",
            ToolbarControl.Board => "Board",
            ToolbarControl.Mode => "Draw and Use apps",
            ToolbarControl.Undo => "Undo",
            ToolbarControl.Redo => "Redo",
            ToolbarControl.ClearAll => "Clear all",
            ToolbarControl.Properties => "Properties",
            ToolbarControl.Settings => "Settings",
            ToolbarControl.Hide => "Hide",
            ToolbarControl.FocusMenu => "Spotlight and cleanup menu",
            ToolbarControl.ZoomMenu => "Zoom menu",
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

        public string Detail => control switch
        {
            ToolbarControl.Library => "Every shape and tool in one place.",
            ToolbarControl.Board => "Desktop, whiteboard or blackboard.",
            ToolbarControl.Mode => "Switch between drawing and clicking your apps.",
            ToolbarControl.Undo => "Take back your last change.",
            ToolbarControl.Redo => "Bring back what Undo took.",
            ToolbarControl.ClearAll => "Remove all drawings. You can undo it.",
            ToolbarControl.Properties => "Quick controls, full properties or tools only.",
            ToolbarControl.Settings => "Open Settings.",
            ToolbarControl.Hide => "Hide drawings and tools.",
            ToolbarControl.FocusMenu => "Spotlight, cursor halo and cleanup.",
            ToolbarControl.ZoomMenu => "Pointer lens and screen zoom.",
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

        /// <summary>The SF Symbol name on the Mac; Windows maps it through the icon table (plan D-6).</summary>
        public string Symbol => control switch
        {
            ToolbarControl.Library => "square.grid.2x2",
            ToolbarControl.Board => "desktopcomputer",
            ToolbarControl.Mode => "pencil.tip",
            ToolbarControl.Undo => "arrow.uturn.backward",
            ToolbarControl.Redo => "arrow.uturn.forward",
            ToolbarControl.ClearAll => "trash",
            ToolbarControl.Properties => "slider.horizontal.3",
            ToolbarControl.Settings => "gearshape",
            ToolbarControl.Hide => "xmark",
            ToolbarControl.FocusMenu => "light.beacon.max",
            ToolbarControl.ZoomMenu => "plus.magnifyingglass",
            _ => throw new ArgumentOutOfRangeException(nameof(control)),
        };

        /// <summary>The toolbar button key this control shares, for controls that have one.</summary>
        public ShortcutAction? Action => control switch
        {
            ToolbarControl.Board => ShortcutAction.Board,
            ToolbarControl.ClearAll => ShortcutAction.ClearAll,
            ToolbarControl.Properties => ShortcutAction.Properties,
            _ => null,
        };
    }
}

public partial struct WorkspacePreferences
{
    /// <summary>How long the pointer can stay off the toolbar before it shrinks to one button, in seconds.</summary>
    public static ValueList<double> ToolbarCollapseDelays { get; } = [3, 5, 10, 30];

    internal const double MinimumToolbarCollapseDelay = 1;
    internal const double MaximumToolbarCollapseDelay = 600;

    public readonly bool ShowsToolbarControl(ToolbarControl control) =>
        HiddenToolbarControls is not { } hidden || !hidden.AsSpan().Contains(control.RawValue);

    /// <summary>Shows or hides one toolbar control. The hidden list keeps the controls' own order, so equal toolbars
    /// compare equal.</summary>
    public void SetToolbarControl(ToolbarControl control, bool shown)
    {
        var hidden = new HashSet<string>(HiddenToolbarControls?.AsSpan().ToArray() ?? [], StringComparer.Ordinal);
        if (shown)
        {
            hidden.Remove(control.RawValue);
        }
        else
        {
            hidden.Add(control.RawValue);
        }
        var ordered = new List<string>();
        foreach (var candidate in ToolbarControl.AllCases)
        {
            if (hidden.Contains(candidate.RawValue))
            {
                ordered.Add(candidate.RawValue);
            }
        }
        HiddenToolbarControls = ordered.Count == 0 ? null : ordered.ToValueList();
    }

    internal readonly void ValidateToolbarControls()
    {
        if (HiddenToolbarControls is { } hidden)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in hidden)
            {
                if (ToolbarControl.FromRawValue(item) is null || !unique.Add(item))
                {
                    throw CanvasError.InvalidDocument("Hidden toolbar buttons must be known buttons, each listed once.");
                }
            }
            if (hidden.Count > ToolbarControl.AllCases.Count)
            {
                throw CanvasError.InvalidDocument("Hidden toolbar buttons must be known buttons, each listed once.");
            }
        }
        if (ToolbarCollapseDelay is { } delay
            && !(double.IsFinite(delay) && delay >= MinimumToolbarCollapseDelay && delay <= MaximumToolbarCollapseDelay))
        {
            throw CanvasError.InvalidDocument("The toolbar can shrink after 1 to 600 seconds.");
        }
    }

    /// <summary>Buttons from a newer version are dropped, so a profile with them still opens.</summary>
    internal void NormalizeToolbarControls()
    {
        if (HiddenToolbarControls is not { } hidden)
        {
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var known = new List<string>();
        foreach (var item in hidden)
        {
            if (ToolbarControl.FromRawValue(item) is not null && seen.Add(item))
            {
                known.Add(item);
            }
        }
        HiddenToolbarControls = known.Count == 0 ? null : known.ToValueList();
    }
}
