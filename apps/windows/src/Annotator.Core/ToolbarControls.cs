namespace Annotator.Core;

/// <summary>
/// Swift `ToolbarControl` (ToolbarControls.swift): the toolbar's own buttons, next to the tools. People can turn each
/// one off in Settings; the tools and added buttons such as Spotlight and the Zoom menu are arranged separately
/// (<see cref="WorkspacePreferences.ToolbarItems"/>).
/// </summary>
public enum ToolbarControl { Library, Board, Mode, Undo, Redo, ClearAll, Properties, Settings, Hide }

public static class ToolbarControlExtensions
{
    internal static readonly RawValueMap<ToolbarControl> RawValues = new(
        "library", "board", "mode", "undo", "redo", "clearAll", "properties", "settings", "hide");

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
            ToolbarControl.Properties => "Show or hide the properties below the tools.",
            ToolbarControl.Settings => "Open Settings.",
            ToolbarControl.Hide => "Hide drawings and tools.",
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

/// <summary>
/// Swift `PropertyChoice`: properties with a few choices. Each shows as one button that steps through its choices, which
/// is the default, or as all its choices side by side.
/// </summary>
public enum PropertyChoice { Pattern, Roughness, FillStyle, Edges, ArrowType, Pressure, Font, Alignment }

public static class PropertyChoiceExtensions
{
    internal static readonly RawValueMap<PropertyChoice> RawValues = new(
        "pattern", "roughness", "fillStyle", "edges", "arrowType", "pressure", "font", "alignment");

    extension(PropertyChoice)
    {
        public static ValueList<PropertyChoice> AllCases => RawValues.AllCases;

        public static PropertyChoice? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }

    extension(PropertyChoice choice)
    {
        public string RawValue => RawValues.RawValue(choice);

        public string Label => choice switch
        {
            PropertyChoice.Pattern => "Pattern",
            PropertyChoice.Roughness => "Roughness",
            PropertyChoice.FillStyle => "Fill style",
            PropertyChoice.Edges => "Edges",
            PropertyChoice.ArrowType => "Arrow type",
            PropertyChoice.Pressure => "Pressure",
            PropertyChoice.Font => "Font",
            PropertyChoice.Alignment => "Alignment",
            _ => throw new ArgumentOutOfRangeException(nameof(choice)),
        };

        /// <summary>The property's choices, in order. One button starts at the first and steps through them.</summary>
        public ValueList<string> Choices => choice switch
        {
            PropertyChoice.Pattern => ["Solid", "Dashed", "Dotted"],
            PropertyChoice.Roughness => ["Clean", "Sketchy", "Wild"],
            PropertyChoice.FillStyle => ["Solid", "Hatch", "Crosshatch"],
            PropertyChoice.Edges => ["Sharp corners", "Rounded corners"],
            PropertyChoice.ArrowType => ["Straight arrow", "Curved arrow"],
            PropertyChoice.Pressure => ["Constant pressure", "Ink pressure"],
            PropertyChoice.Font => ["System", "Mono"],
            PropertyChoice.Alignment => ["Left", "Center", "Right"],
            _ => throw new ArgumentOutOfRangeException(nameof(choice)),
        };

        public int ChoiceCount => choice.Choices.Count;
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

    /// <summary>Whether a property shows all its choices side by side instead of one button.</summary>
    public readonly bool ShowsAllChoices(PropertyChoice choice) =>
        ExpandedPropertyChoices is { } expanded && expanded.AsSpan().Contains(choice.RawValue);

    public void SetShowsAllChoices(PropertyChoice choice, bool shown)
    {
        var expanded = new HashSet<string>(ExpandedPropertyChoices?.AsSpan().ToArray() ?? [], StringComparer.Ordinal);
        if (shown)
        {
            expanded.Add(choice.RawValue);
        }
        else
        {
            expanded.Remove(choice.RawValue);
        }
        var ordered = new List<string>();
        foreach (var candidate in PropertyChoice.AllCases)
        {
            if (expanded.Contains(candidate.RawValue))
            {
                ordered.Add(candidate.RawValue);
            }
        }
        ExpandedPropertyChoices = ordered.Count == 0 ? null : ordered.ToValueList();
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
        if (ExpandedPropertyChoices is { } expanded)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in expanded)
            {
                if (PropertyChoice.FromRawValue(item) is null || !unique.Add(item))
                {
                    throw CanvasError.InvalidDocument(
                        "Properties that show all their choices must be known properties, each listed once.");
                }
            }
            if (expanded.Count > PropertyChoice.AllCases.Count)
            {
                throw CanvasError.InvalidDocument(
                    "Properties that show all their choices must be known properties, each listed once.");
            }
        }
    }

    /// <summary>Buttons and properties from a newer version are dropped, so a profile with them still opens.</summary>
    internal void NormalizeToolbarControls()
    {
        if (HiddenToolbarControls is { } hidden)
        {
            HiddenToolbarControls = Known(hidden, item => ToolbarControl.FromRawValue(item) is not null);
        }
        if (ExpandedPropertyChoices is { } expanded)
        {
            ExpandedPropertyChoices = Known(expanded, item => PropertyChoice.FromRawValue(item) is not null);
        }
    }

    private static ValueList<string>? Known(ValueList<string> items, Func<string, bool> isKnown)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var known = new List<string>();
        foreach (var item in items)
        {
            if (isKnown(item) && seen.Add(item))
            {
                known.Add(item);
            }
        }
        return known.Count == 0 ? null : known.ToValueList();
    }
}
