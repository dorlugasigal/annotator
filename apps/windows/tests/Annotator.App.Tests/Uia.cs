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

    /// <summary>Its bounding rectangle from the window's top-left corner, as UI Automation reports it.</summary>
    public System.Windows.Rect Bounds { get; init; }

    public override string ToString() => $"{Type} \"{Name}\"{(Id.Length > 0 ? $" ({Id})" : string.Empty)}";
}

/// <summary>Reads the control view of one of the app's windows through the UI Automation client, as Narrator does.</summary>
internal static class Uia
{
    public static List<UiaElement> Read(nint window)
    {
        var elements = new List<UiaElement>();
        AutomationElement root = AutomationElement.FromHandle(window);
        Walk(root, root.Current.BoundingRectangle.TopLeft, elements, 0);
        return elements;
    }

    /// <summary>
    /// Toggles the element named <paramref name="name"/>, as Narrator's Toggle command does, and waits a bounded time for
    /// the element named <paramref name="then"/> to appear, if any.
    /// </summary>
    public static void Toggle(nint window, string name, string? then = null)
    {
        AutomationElement root = AutomationElement.FromHandle(window);
        AutomationElement element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            ?? throw new InvalidOperationException($"The window has no element named {name}.");
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        if (then is null)
        {
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, then)) is null)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"{then} did not appear within 10 s after toggling {name}.");
            }
            Thread.Sleep(100);
        }
    }

    private static void Walk(AutomationElement parent, System.Windows.Point origin, List<UiaElement> into, int depth)
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
            System.Windows.Rect bounds = current.BoundingRectangle;
            if (!bounds.IsEmpty)
            {
                bounds.Offset(-origin.X, -origin.Y);
            }
            into.Add(new UiaElement(type, current.Name ?? string.Empty, current.AutomationId ?? string.Empty, current.HelpText ?? string.Empty,
                current.IsEnabled, current.IsKeyboardFocusable, patterns) { Bounds = bounds });
            Walk(child, origin, into, depth + 1);
        }
    }
}
