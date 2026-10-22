using Annotator.Core;

namespace Annotator.Workspace.Settings;

/// <summary>The groups of the "More tools" tray (Mac ToolbarArranger.TrayGroup), in the Mac's order.</summary>
public enum TrayGroup
{
    Drawing,
    Effects,
    Presenting,
    Shapes,
}

public static class TrayGroupExtensions
{
    extension(TrayGroup)
    {
        public static IReadOnlyList<TrayGroup> AllCases => [TrayGroup.Drawing, TrayGroup.Effects, TrayGroup.Presenting, TrayGroup.Shapes];
    }

    extension(TrayGroup group)
    {
        /// <summary>The Mac's raw value, also the heading people see.</summary>
        public string RawValue => group switch
        {
            TrayGroup.Drawing => "Drawing",
            TrayGroup.Effects => "Effects",
            TrayGroup.Presenting => "Presenting",
            TrayGroup.Shapes => "Shapes",
            _ => throw new ArgumentOutOfRangeException(nameof(group)),
        };

        /// <summary>Buttons are Presenting; effects are Effects; shapes are Shapes; other tools are Drawing.</summary>
        public bool Contains(ShortcutCommand item) => item.Tool is not { } tool ? group == TrayGroup.Presenting
            : tool.IsProtection ? group == TrayGroup.Effects
            : tool.IsShape ? group == TrayGroup.Shapes
            : group == TrayGroup.Drawing;
    }
}

/// <summary>
/// The arranger's rules from ToolbarArranger in ToolbarShortcutSettings.swift: what "More tools" offers, the identifiers and
/// help of the tiles, where a dragged item lands, and the toolbar changes of the add, remove and move commands. Changes go
/// through <see cref="WorkspaceModel.ConfigureToolbar(IEnumerable{ShortcutCommand})"/>; the returned words are what the page
/// says (Mac editor.say).
/// </summary>
public static class ToolbarArranger
{
    /// <summary>Select always stays in the toolbar.</summary>
    public static ShortcutCommand Select { get; } = ShortcutCommand.ForTool(Tool.Select);

    /// <summary>How far outside the toolbar strip a drag still counts as over it, in points.</summary>
    public const double StripReach = 18;

    /// <summary>How far a press must move before it becomes a drag, in points (Mac DragGesture minimumDistance).</summary>
    public const double DragStart = 4;

    public const string SelectStays = "Select always stays in the toolbar.";

    public const string DefaultTools = "Your toolbar has the default tools again.";

    public const string DefaultKeys = "Tool and button keys are back to their defaults.";

    /// <summary>The tools and buttons that are not in the toolbar, in the order of <see cref="WorkspaceModel.ToolbarCandidates"/>.</summary>
    public static IReadOnlyList<ShortcutCommand> Tray(IReadOnlyList<ShortcutCommand> order) =>
        [.. WorkspaceModel.ToolbarCandidates.Where(item => !order.Contains(item))];

    /// <summary>The tray's groups that have members, each with its members in order.</summary>
    public static IReadOnlyList<(TrayGroup Group, IReadOnlyList<ShortcutCommand> Members)> TrayGroups(IReadOnlyList<ShortcutCommand> order)
    {
        IReadOnlyList<ShortcutCommand> tray = Tray(order);
        var groups = new List<(TrayGroup, IReadOnlyList<ShortcutCommand>)>();
        foreach (TrayGroup group in TrayGroup.AllCases)
        {
            ShortcutCommand[] members = [.. tray.Where(item => group.Contains(item))];
            if (members.Length > 0)
            {
                groups.Add((group, members));
            }
        }
        return groups;
    }

    /// <summary>The tray's words next to "More tools": the idle hint, or that everything is in the toolbar.</summary>
    public static string TrayHint(IReadOnlyList<ShortcutCommand> order) =>
        Tray(order).Count == 0 ? "Everything is in your toolbar." : "Drag one up to add it, or click its plus button.";

    /// <summary>The accessibility identifier of a toolbar item or a tray tile, such as "toolbar-tool-select" or "more-action-spotlight".</summary>
    public static string Identifier(ShortcutCommand item, bool inToolbar) =>
        (inToolbar ? "toolbar-" : "more-") + item.Id.Replace(".", "-", StringComparison.Ordinal);

    /// <summary>Mac detail(_:): a button's purpose, Effects' explanation, or a tool's name, for the tray's help.</summary>
    public static string Detail(ShortcutCommand item) => item.Action is { } action ? action.Detail
        : item.Tool == Tool.Cover ? "Uses the last effect you chose: Blur, Pixelate or Distort."
        : item.Tool!.Value.Label;

    /// <summary>The help of a toolbar item (Mac stripItem .help).</summary>
    public static string ToolbarHelp(ShortcutCommand item) => item == Select
        ? "Select always stays in the toolbar. Drag it to change its place."
        : $"Drag to move {item.Label}. Drag it down to remove it.";

    /// <summary>The accessibility hint of a toolbar item at <paramref name="index"/> of <paramref name="count"/>.</summary>
    public static string ToolbarHint(int index, int count) =>
        $"In your toolbar, position {index + 1} of {count}. Use the actions to move or remove it.";

    /// <summary>The accessibility value of a toolbar item at <paramref name="index"/> of <paramref name="count"/>.</summary>
    public static string Position(int index, int count) => $"Position {index + 1} of {count}";

    public const string TrayValue = "Not in the toolbar";

    public const string TrayHintText = "Not in your toolbar. Use the Add to toolbar action to add it.";

    /// <summary>The help of a tray tile (Mac trayTile .help).</summary>
    public static string TrayHelp(ShortcutCommand item) => $"{Detail(item)} Drag it into your toolbar, or click the plus button.";

    /// <summary>
    /// Mac insertionIndex(for:among:): before the first item whose row starts below the point, or whose row holds the point
    /// and whose middle is right of it; otherwise at the end. Frames are the toolbar items' frames as laid out now.
    /// </summary>
    public static int InsertionIndex(Point location, IReadOnlyList<ShortcutCommand> others, Func<ShortcutCommand, Rect?> frame)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(frame);
        for (int index = 0; index < others.Count; index++)
        {
            if (frame(others[index]) is not { } item)
            {
                continue;
            }
            if (location.Y < item.Y - 3)
            {
                return index;
            }
            if (location.Y <= item.MaxY + 3 && location.X < item.X + item.Width / 2)
            {
                return index;
            }
        }
        return others.Count;
    }

    /// <summary>Mac add(_:): puts a tray item at the end of the toolbar; the words to say, or null when it is already there.</summary>
    public static string? Add(WorkspaceModel model, ShortcutCommand item)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValueList<ShortcutCommand> items = model.Snapshot.ToolbarItems;
        if (items.Contains(item))
        {
            return null;
        }
        model.ConfigureToolbar([.. items, item]);
        return $"{item.Label} is in your toolbar now.";
    }

    /// <summary>Mac remove(_:): moves a toolbar item to More tools; Select always stays.</summary>
    public static string Remove(WorkspaceModel model, ShortcutCommand item)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (item == Select)
        {
            return SelectStays;
        }
        model.ConfigureToolbar(model.Snapshot.ToolbarItems.Where(other => other != item));
        return $"{item.Label} moved to More tools.";
    }

    /// <summary>Mac move(_:by:): swaps a toolbar item with its neighbor; false at either end.</summary>
    public static bool Move(WorkspaceModel model, ShortcutCommand item, int delta)
    {
        ArgumentNullException.ThrowIfNull(model);
        ShortcutCommand[] items = [.. model.Snapshot.ToolbarItems];
        int index = Array.IndexOf(items, item);
        if (index < 0 || index + delta < 0 || index + delta >= items.Length)
        {
            return false;
        }
        (items[index], items[index + delta]) = (items[index + delta], items[index]);
        model.ConfigureToolbar(items);
        return true;
    }
}

/// <summary>
/// A drag in the arranger (Mac ArrangerDrag): the item, where it came from, where the pointer is, and the toolbar order it
/// would leave now. Over the toolbar strip the item takes the slot under the pointer; away from it a toolbar item leaves the
/// order, except Select.
/// </summary>
public sealed class ArrangerDrag
{
    private ArrangerDrag(ShortcutCommand item, bool fromToolbar, Point location, Point grabOffset, IReadOnlyList<ShortcutCommand> order)
    {
        Item = item;
        FromToolbar = fromToolbar;
        Location = location;
        GrabOffset = grabOffset;
        Order = order;
        OverStrip = fromToolbar;
    }

    public ShortcutCommand Item { get; }

    public bool FromToolbar { get; }

    /// <summary>The pointer, in the arranger's coordinates.</summary>
    public Point Location { get; private set; }

    /// <summary>Where the item was taken, from its center.</summary>
    public Point GrabOffset { get; }

    /// <summary>The toolbar order the drag would leave now.</summary>
    public IReadOnlyList<ShortcutCommand> Order { get; private set; }

    public bool OverStrip { get; private set; }

    /// <summary>The drop happened and the item settles into its slot.</summary>
    public bool Settling { get; private set; }

    /// <summary>Letting go here removes the item from the toolbar (the tray turns red).</summary>
    public bool Removing => !OverStrip && FromToolbar && Item != ToolbarArranger.Select;

    /// <summary>Letting go here adds the item to the toolbar (the strip's outline turns to the accent).</summary>
    public bool Adding => OverStrip && !FromToolbar;

    /// <summary>
    /// The words next to "More tools" during the drag, in red while letting go removes the item and in orange when Select
    /// is pulled away; null for the idle hint.
    /// </summary>
    public (string Text, string Symbol, bool Red)? TrayMessage => Removing
        ? ("Let go to remove it from your toolbar", "minus.circle.fill", true)
        : Item == ToolbarArranger.Select && !OverStrip ? ("Select always stays in the toolbar", "lock.fill", false) : null;

    /// <summary>Mac dragChanged's first event: the item under the press starts moving with the toolbar order as it is.</summary>
    public static ArrangerDrag Start(ShortcutCommand item, bool fromToolbar, Point location, Point grabOffset, IReadOnlyList<ShortcutCommand> order) =>
        new(item, fromToolbar, location, grabOffset, [.. order]);

    /// <summary>
    /// Mac dragChanged: follows the pointer. Returns true when the order changed, so the other items slide to their new
    /// places.
    /// </summary>
    public bool Update(Point location, bool overStrip, Func<ShortcutCommand, Rect?> stripFrame)
    {
        ArgumentNullException.ThrowIfNull(stripFrame);
        if (Settling)
        {
            return false;
        }
        Location = location;
        OverStrip = overStrip;
        List<ShortcutCommand> next = [.. Order];
        if (overStrip)
        {
            List<ShortcutCommand> others = [.. next.Where(other => other != Item)];
            others.Insert(ToolbarArranger.InsertionIndex(location, others, stripFrame), Item);
            next = others;
        }
        else if (Item != ToolbarArranger.Select)
        {
            next.Remove(Item);
        }
        if (next.SequenceEqual(Order))
        {
            return false;
        }
        Order = next;
        return true;
    }

    /// <summary>
    /// Mac dragEnded: saves the new order unless it is unchanged or lacks Select, and returns the words to say, or null.
    /// The item then settles into its slot in the toolbar, or in the tray when it left the toolbar.
    /// </summary>
    public string? End(WorkspaceModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        IReadOnlyList<ShortcutCommand> before = [.. model.Snapshot.ToolbarItems];
        if (!Order.SequenceEqual(before) && Order.Contains(ToolbarArranger.Select))
        {
            model.ConfigureToolbar(Order);
        }
        bool added = !before.Contains(Item) && Order.Contains(Item);
        bool removed = before.Contains(Item) && !Order.Contains(Item);
        string? message = Item == ToolbarArranger.Select && !OverStrip ? ToolbarArranger.SelectStays
            : added ? $"{Item.Label} is in your toolbar now."
            : removed ? $"{Item.Label} moved to More tools."
            : null;
        Settling = true;
        OverStrip = Order.Contains(Item);
        return message;
    }

    /// <summary>Where the settling item goes: the center of its slot, shifted by where it was taken.</summary>
    public void SettleAt(Rect slot) => Location = new Point(slot.X + slot.Width / 2 + GrabOffset.X, slot.Y + slot.Height / 2 + GrabOffset.Y);
}
