namespace Annotator.UXReference;

/// <summary>
/// Expected words of the Toolbar &amp; Shortcuts states (settings/tab-toolbar*, dialogs/keycaps-*) that the recorded Mac tree
/// does not have. The tiles of "Your toolbar" and "More tools" set an accessibility value and hint in
/// ToolbarShortcutSettings.swift (ToolbarArranger's toolbarItem and trayItem), but the recorder does not record them for
/// SwiftUI containers, so they come from the Mac source. Hide only's Windows default is Ctrl+Alt+Shift+2 instead of the Mac's
/// Control-Option-2 (decision D-5 of specification 013).
/// </summary>
internal static class ShortcutExpectations
{
    private const string HideOnlyMac = "Control + Option + 2";
    private const string HideOnlyWindows = "Control + Option + Shift + 2";

    public static bool Applies(string surface, string state) =>
        surface == "settings" && state.StartsWith("tab-toolbar", StringComparison.Ordinal) ||
        surface == "dialogs" && state.StartsWith("keycaps-", StringComparison.Ordinal);

    public static ReferenceState Apply(ReferenceState reference)
    {
        if (reference.Accessibility is not { } nodes)
        {
            return reference;
        }
        var notes = new List<string>();
        List<AxNode> expected = [.. nodes.Select(node => Rewrite(node, notes))];
        return reference with
        {
            Accessibility = expected,
            SourceNote = notes.Count == 0 ? null : "Expected words from the Mac source and specification 013: " + string.Join("; ", notes.Distinct()) + ".",
            SourceSummary = notes.Count == 0 ? null : "words from the Mac source and D-5",
        };
    }

    private static AxNode Rewrite(AxNode node, List<string> notes)
    {
        string? label = node.Label;
        string? help = node.Help;
        System.Text.Json.Nodes.JsonValue? value = node.Value?.DeepClone().AsValue();
        if (node.Role == "AXUnknown" && label == HideOnlyMac)
        {
            label = HideOnlyWindows;
            help = help == HideOnlyMac ? HideOnlyWindows : help;
            notes.Add("Hide only's keycaps show Ctrl+Alt+Shift+2 (D-5)");
        }
        if (node.Identifier == "global-shortcut-hideOnly" && node.ValueText == HideOnlyMac)
        {
            value = System.Text.Json.Nodes.JsonValue.Create(HideOnlyWindows);
            notes.Add("Hide only's value is Ctrl+Alt+Shift+2 (D-5)");
        }
        var rewritten = new AxNode
        {
            Role = node.Role,
            Label = label,
            Identifier = node.Identifier,
            Value = value,
            Help = help,
            Title = node.Title,
            Frame = node.Frame,
            Enabled = node.Enabled,
        };
        foreach (AxNode child in node.Children)
        {
            rewritten.Children.Add(Rewrite(child, notes));
        }
        if (node.Identifier == "toolbar-arrangement")
        {
            // ToolbarArranger.toolbarItem: .accessibilityValue("Position \(index + 1) of \(order.count)") and its hint.
            List<AxNode> tiles = [.. rewritten.Children.Where(child => child.Role == "AXGroup" && child.Identifier?.StartsWith("toolbar-", StringComparison.Ordinal) == true)];
            for (int index = 0; index < tiles.Count; index++)
            {
                int at = rewritten.Children.IndexOf(tiles[index]);
                rewritten.Children[at] = With(tiles[index], $"Position {index + 1} of {tiles.Count}",
                    $"In your toolbar, position {index + 1} of {tiles.Count}. Use the actions to move or remove it.");
            }
            notes.Add("tile values and hints of Your toolbar and More tools (ToolbarArranger)");
        }
        if (node.Role == "AXGroup" && node.Label == "More tools" && node.Identifier is null)
        {
            // ToolbarArranger.trayItem: .accessibilityValue("Not in the toolbar") and its hint.
            for (int at = 0; at < rewritten.Children.Count; at++)
            {
                if (rewritten.Children[at] is { Role: "AXGroup", Identifier: { } id } tile && id.StartsWith("more-", StringComparison.Ordinal))
                {
                    rewritten.Children[at] = With(tile, "Not in the toolbar", "Not in your toolbar. Use the Add to toolbar action to add it.");
                }
            }
        }
        return rewritten;
    }

    private static AxNode With(AxNode node, string value, string help)
    {
        var copy = new AxNode
        {
            Role = node.Role,
            Label = node.Label,
            Identifier = node.Identifier,
            Value = System.Text.Json.Nodes.JsonValue.Create(value),
            Help = help,
            Title = node.Title,
            Frame = node.Frame,
            Enabled = node.Enabled,
        };
        copy.Children.AddRange(node.Children);
        return copy;
    }
}
