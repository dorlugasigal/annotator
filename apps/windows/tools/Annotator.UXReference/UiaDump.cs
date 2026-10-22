using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace Annotator.UXReference;

/// <summary>
/// Reads a window's UI Automation tree (the control view, as Narrator reads it) into the reference JSON shape:
/// the control type becomes the Mac role, Name the label (or the value of a static text), AutomationId the
/// identifier, the Value, Toggle or RangeValue pattern the value, HelpText the help, and BoundingRectangle the frame
/// in DIPs from the window's top-left corner. Panes and other containers without a name or identifier only
/// structure the tree, so their children move up, as SwiftUI's stacks do.
/// </summary>
internal static class UiaDump
{
    /// <param name="includeRoot">Starts with the window's own element, as for a canvas whose window is the canvas.</param>
    public static List<AxNode> Read(nint window, double scale, bool includeRoot = false)
    {
        AutomationElement root = AutomationElement.FromHandle(window);
        System.Windows.Rect origin = root.Current.BoundingRectangle;
        var nodes = new List<AxNode>();
        if (includeRoot && Describe(root, origin, scale) is { } self)
        {
            nodes.Add(self);
            Walk(root, self.Children, origin, scale, depth: 0);
            return nodes;
        }
        Walk(root, nodes, origin, scale, depth: 0);
        return nodes;
    }

    private static void Walk(AutomationElement parent, List<AxNode> into, System.Windows.Rect origin, double scale, int depth)
    {
        if (depth > 40)
        {
            throw new InvalidOperationException("The UI Automation tree is deeper than 40 levels.");
        }
        TreeWalker walker = TreeWalker.ControlViewWalker;
        for (AutomationElement? child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
        {
            AxNode? node = Describe(child, origin, scale);
            if (node is null)
            {
                Walk(child, into, origin, scale, depth + 1);
                continue;
            }
            into.Add(node);
            Walk(child, node.Children, origin, scale, depth + 1);
        }
    }

    private static AxNode? Describe(AutomationElement element, System.Windows.Rect origin, double scale)
    {
        AutomationElement.AutomationElementInformation current = element.Current;
        ControlType type = current.ControlType;
        string? name = Text(current.Name);
        string? id = Text(current.AutomationId);
        bool scrolls = element.TryGetCurrentPattern(ScrollPattern.Pattern, out _);
        if ((type == ControlType.Pane || type == ControlType.Custom || type == ControlType.Window) && name is null && id is null && !scrolls)
        {
            return null;
        }
        string role = Role(element, type, scrolls);
        System.Windows.Rect bounds = current.BoundingRectangle;
        double[]? frame = bounds.IsEmpty ? null :
        [
            (bounds.X - origin.X) / scale, (bounds.Y - origin.Y) / scale, bounds.Width / scale, bounds.Height / scale,
        ];
        if (role == "AXStaticText")
        {
            return new AxNode { Role = role, Value = name is null ? null : JsonValue.Create(name), Identifier = id, Help = Text(current.HelpText), Frame = frame, Enabled = current.IsEnabled };
        }
        if (role is "AXHeading" or "AXTab")
        {
            // The Mac records a heading's and a tab's words as their label, without a value.
            return new AxNode { Role = role, Label = name, Identifier = id, Help = Text(current.HelpText), Frame = frame, Enabled = current.IsEnabled };
        }
        return new AxNode
        {
            Role = role,
            Label = name,
            Identifier = id,
            Value = Value(element, role),
            Help = Text(current.HelpText),
            Frame = frame,
            Enabled = current.IsEnabled,
        };
    }

    private static string Role(AutomationElement element, ControlType type, bool scrolls)
    {
        if (type == ControlType.Button)
        {
            return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _) &&
                !element.TryGetCurrentPattern(TogglePattern.Pattern, out _) ? "AXMenuButton" : "AXButton";
        }
        if (type == ControlType.RadioButton)
        {
            return "AXRadioButton";
        }
        if (type == ControlType.Group)
        {
            // Groups that carry a pattern stand for the Mac's segmented picker (Selection), LazyVGrid (Grid) and
            // DisclosureGroup (ExpandCollapse, the Windows expander).
            return element.TryGetCurrentPattern(SelectionPattern.Pattern, out _) ? "AXRadioGroup"
                : element.TryGetCurrentPattern(GridPattern.Pattern, out _) ? "AXOpaqueProviderGroup"
                : element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _) ? "AXDisclosureTriangle"
                : "AXGroup";
        }
        if (type == ControlType.SplitButton || type == ControlType.MenuItem && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
        {
            return "AXMenuButton";
        }
        if (type == ControlType.Text)
        {
            // A heading (Settings' section titles) has a heading level or reads as text whose localized type is "heading",
            // the Mac's AXHeading.
            return Headings.IsHeading(element) || string.Equals(element.Current.LocalizedControlType, "heading", StringComparison.OrdinalIgnoreCase)
                ? "AXHeading" : "AXStaticText";
        }
        if (type == ControlType.Tab)
        {
            // Settings' tab view: the Mac's AXTabGroup.
            return "AXTabGroup";
        }
        if (type == ControlType.TabItem)
        {
            // The Mac's recorder keeps a tab's title and identifier but no role or frame; the comparison calls those AXTab.
            return "AXTab";
        }
        if (type == ControlType.Edit)
        {
            return "AXTextField";
        }
        if (type == ControlType.Slider)
        {
            return "AXSlider";
        }
        if (type == ControlType.Thumb)
        {
            return "AXValueIndicator";
        }
        if (type == ControlType.ComboBox)
        {
            return "AXPopUpButton";
        }
        if (type == ControlType.CheckBox)
        {
            return "AXCheckBox";
        }
        if (type == ControlType.Image)
        {
            return "AXImage";
        }
        if (type == ControlType.ScrollBar)
        {
            return "AXScrollBar";
        }
        if (type == ControlType.Hyperlink)
        {
            return "AXLink";
        }
        if (type == ControlType.MenuItem)
        {
            return "AXMenuItem";
        }
        if (scrolls)
        {
            return "AXScrollArea";
        }
        // A spinner, such as the break length's stepper, is AppKit's AXIncrementor.
        if (type == ControlType.Spinner)
        {
            return "AXIncrementor";
        }
        // A named custom control, such as the shortcut keycaps, is what AppKit reports as AXUnknown.
        if (type == ControlType.Custom)
        {
            return "AXUnknown";
        }
        return "UIA." + type.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
    }

    private static JsonValue? Value(AutomationElement element, string role)
    {
        if (role is "AXSlider" or "AXScrollBar" or "AXIncrementor" && element.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? range))
        {
            return JsonValue.Create(((RangeValuePattern)range).Current.Value);
        }
        // The Mac reports a segment as 1 or 0 and a disclosure triangle as true or false.
        if (role == "AXRadioButton" && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? item))
        {
            return JsonValue.Create(((SelectionItemPattern)item).Current.IsSelected ? 1.0 : 0.0);
        }
        if (role == "AXDisclosureTriangle" && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expander))
        {
            return JsonValue.Create(((ExpandCollapsePattern)expander).Current.ExpandCollapseState == ExpandCollapseState.Expanded);
        }
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object? value) && Text(((ValuePattern)value).Current.Value) is { } text)
        {
            return JsonValue.Create(text);
        }
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
        {
            ToggleState state = ((TogglePattern)toggle).Current.ToggleState;
            if (role == "AXCheckBox")
            {
                return JsonValue.Create(state == ToggleState.On ? 1.0 : 0.0);
            }
            return JsonValue.Create(state == ToggleState.On ? "On" : state == ToggleState.Off ? "Off" : "Mixed");
        }
        if (role == "AXPopUpButton" && element.TryGetCurrentPattern(SelectionPattern.Pattern, out object? selection) &&
            ((SelectionPattern)selection).Current.GetSelection() is [{ } selected, ..])
        {
            return JsonValue.Create(selected.Current.Name);
        }
        return null;
    }

    private static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static class Headings
    {
        /// <summary>UIA's HeadingLevel1 to HeadingLevel9 (80051 to 80059), or 1 to 9 when the client maps them.</summary>
        public static bool IsHeading(AutomationElement element)
        {
            object level = element.GetCurrentPropertyValue(AutomationElement.HeadingLevelProperty, ignoreDefaultValue: true);
            if (level == AutomationElement.NotSupported || level is null)
            {
                return false;
            }
            int value = Convert.ToInt32(level, CultureInfo.InvariantCulture);
            return value is (>= 80051 and <= 80059) or (>= 1 and <= 9);
        }
    }
}
