namespace Annotator.UXReference;

/// <summary>
/// What the on-screen tip (dialogs/canvas-hint) expects beyond its JSON. The Mac recorder writes the ✕ (HintCloseButton in
/// WorkspaceHint.swift) with its title and no label, although the source labels it "Turn off tips"
/// (setAccessibilityLabel), so the ✕ takes that label. The ✕ sits at the right end of the first line, so its left edge
/// follows the text's width, which comes from the fonts (D-4); its top, size, label and help are still compared.
/// The Mac reports the ✕ as a child of the tip's static text; the comparison aligns nodes in tree order, not by depth.
/// </summary>
internal static class HintExpectations
{
    private const string CloseTitle = "Turn off tips";

    private const string CloseLabel = "Turn off tips";

    private const string Note =
        "The ✕'s label is \"Turn off tips\" from WorkspaceHint.swift (setAccessibilityLabel); the Mac recorder wrote only its title. " +
        "Its left edge follows the tip's text width (D-4).";

    public static bool Applies(string surface, string state) => surface == "dialogs" && state == "canvas-hint";

    public static ReferenceState Apply(ReferenceState reference)
    {
        bool changed = false;
        List<AxNode> nodes = [.. (reference.Accessibility ?? []).Select(node => Relabel(node, ref changed))];
        return changed ? reference with { Accessibility = nodes, SourceNote = Note, SourceSummary = "✕ label from the Mac source; ✕ follows the text width" } : reference;
    }

    private static AxNode Relabel(AxNode node, ref bool changed)
    {
        bool close = node is { Role: "AXButton", Label: null, Title: CloseTitle };
        changed |= close;
        var copy = new AxNode
        {
            Role = node.Role,
            Label = close ? CloseLabel : node.Label,
            Identifier = node.Identifier,
            Value = node.Value,
            Help = node.Help,
            Title = node.Title,
            Frame = node.Frame,
            Enabled = node.Enabled,
            FollowsTextWidth = close || node.FollowsTextWidth,
        };
        foreach (AxNode child in node.Children)
        {
            copy.Children.Add(Relabel(child, ref changed));
        }
        return copy;
    }
}
