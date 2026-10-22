using System.Windows.Automation;

namespace Annotator.App.Tests;

/// <summary>What the checks need to know about an element beyond its identifier.</summary>
internal enum UiaElementKind
{
    Other,

    /// <summary>A tile of Settings > Toolbar & Shortcuts, which carries the Mac's VoiceOver actions.</summary>
    ArrangerTile,
}

/// <summary>One element of a window's UI Automation control view, as Narrator reads it.</summary>
internal sealed record UiaElement(string Type, string Name, string Id, string Help, bool Enabled, bool Focusable, IReadOnlyList<string> Patterns)
{
    /// <summary>A person can use it: it has Invoke, Toggle, ExpandCollapse, SelectionItem or a writable Value or RangeValue.</summary>
    public bool Usable => Patterns.Any(pattern => pattern is "Invoke" or "Toggle" or "ExpandCollapse" or "SelectionItem" or "Value" or "RangeValue");

    public override string ToString() => $"{Type} \"{Name}\"{(Id.Length > 0 ? $" ({Id})" : string.Empty)}";
}

/// <summary>Reads the control view of one of the app's windows through the UI Automation client, as Narrator does.</summary>
internal static class Uia
{
    public static List<UiaElement> Read(nint window)
    {
        var elements = new List<UiaElement>();
        AutomationElement root = AutomationElement.FromHandle(window);
        Walk(root, elements, 0);
        return elements;
    }

    private static void Walk(AutomationElement parent, List<UiaElement> into, int depth)
    {
        if (depth > 40)
        {
            throw new InvalidOperationException("The UI Automation tree is deeper than 40 levels.");
        }
        TreeWalker walker = TreeWalker.ControlViewWalker;
        for (AutomationElement? child = walker.GetFirstChild(parent); child is not null; child = walker.GetNextSibling(child))
        {
            AutomationElement.AutomationElementInformation current = child.Current;
            var patterns = new List<string>();
            if (child.TryGetCurrentPattern(InvokePattern.Pattern, out _))
            {
                patterns.Add("Invoke");
            }
            if (child.TryGetCurrentPattern(TogglePattern.Pattern, out _))
            {
                patterns.Add("Toggle");
            }
            if (child.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _))
            {
                patterns.Add("ExpandCollapse");
            }
            if (child.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _))
            {
                patterns.Add("SelectionItem");
            }
            if (child.TryGetCurrentPattern(ValuePattern.Pattern, out object? value) && !((ValuePattern)value).Current.IsReadOnly)
            {
                patterns.Add("Value");
            }
            if (child.TryGetCurrentPattern(RangeValuePattern.Pattern, out object? range) && !((RangeValuePattern)range).Current.IsReadOnly)
            {
                patterns.Add("RangeValue");
            }
            string type = current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal);
            into.Add(new UiaElement(type, current.Name ?? string.Empty, current.AutomationId ?? string.Empty, current.HelpText ?? string.Empty,
                current.IsEnabled, current.IsKeyboardFocusable, patterns));
            Walk(child, into, depth + 1);
        }
    }
}
