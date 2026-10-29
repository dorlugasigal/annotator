// Port of apps/macos/Sources/AnnotationCore/Shortcuts.swift.
using System.Collections.Immutable;

namespace Annotator.Core;

/// <summary>Toolbar density. Expanded is the original layout; Minimal uses smaller buttons without key labels.</summary>
public enum ToolbarSize { Expanded, Minimal }

/// <summary>A key pressed while a drawing canvas has keyboard focus: one letter or number, optionally with Shift for letters.</summary>
public readonly record struct LocalShortcut
{
    internal const string ShiftPrefix = "⇧";

    private LocalShortcut(string key, bool shift)
    {
        Key = key;
        Shift = shift;
    }

    public string Key { get; }

    public bool Shift { get; }

    public string StorageKey => Shift ? ShiftPrefix + Key : Key;

    public string Label => (Shift ? "⇧" : "") + SwiftText.Uppercased(Key);

    public string SpokenLabel => Shift ? $"Shift + {SwiftText.Uppercased(Key)}" : SwiftText.Uppercased(Key);

    public bool IsInkColor => InkShortcut.Choices.ContainsKey(Key);

    public LocalShortcut Unshifted => Shift ? Create(Key)!.Value : this;

    /// <summary>Swift equality: the keys compare by canonical equivalence.</summary>
    public bool Equals(LocalShortcut other) => Shift == other.Shift && SwiftText.Equals(Key, other.Key);

    public override int GetHashCode() => HashCode.Combine(SwiftText.GetHashCode(Key ?? string.Empty), Shift);

    /// <summary>Swift `LocalShortcut(key:shift:)`. Returns null unless the key is one letter or number.</summary>
    public static LocalShortcut? Create(string key, bool shift = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        var normalized = SwiftText.Lowercased(key);
        if (SwiftText.CharacterCount(normalized) != 1 || SwiftText.ScalarCount(normalized) != 1)
        {
            return null;
        }
        var scalar = normalized.EnumerateRunes().First();
        if (!SwiftText.IsAlphanumeric(scalar))
        {
            return null;
        }
        // Shift changes the character of digits and symbols on most layouts, so only letters take Shift.
        if (shift && !(SwiftText.IsLetter(scalar) && !SwiftText.Equals(SwiftText.Uppercased(normalized), normalized)))
        {
            return null;
        }
        return new LocalShortcut(normalized, shift);
    }

    /// <summary>Swift `LocalShortcut(storageKey:)`: a plain key, or "⇧" followed by a letter.</summary>
    public static LocalShortcut? FromStorageKey(string storageKey)
    {
        ArgumentNullException.ThrowIfNull(storageKey);
        if (storageKey.StartsWith(ShiftPrefix, StringComparison.Ordinal))
        {
            return Create(storageKey[ShiftPrefix.Length..], shift: true);
        }
        return SwiftText.Equals(storageKey, SwiftText.Lowercased(storageKey)) ? Create(storageKey) : null;
    }
}

/// <summary>Toolbar buttons that are not drawing tools but can have a single-key shortcut.</summary>
public enum ShortcutAction { Properties, Board, Spotlight, CursorHalo, PointerLens, SmartDraw, ShareRegion, ClearAll, FocusMenu, ZoomMenu }

public static class ToolbarSizeExtensions
{
    internal static readonly RawValueMap<ToolbarSize> RawValues = new("expanded", "minimal");

    extension(ToolbarSize size)
    {
        public string RawValue => RawValues.RawValue(size);
    }

    extension(ToolbarSize)
    {
        public static ValueList<ToolbarSize> AllCases => RawValues.AllCases;

        public static ToolbarSize? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class ShortcutActionExtensions
{
    internal static readonly RawValueMap<ShortcutAction> RawValues = new(
        "properties", "board", "spotlight", "cursorHalo", "pointerLens", "smartDraw", "shareRegion", "clearAll", "focusMenu",
        "zoomMenu");

    private static readonly ValueList<ShortcutAction> Choices =
    [
        ShortcutAction.Spotlight, ShortcutAction.CursorHalo, ShortcutAction.PointerLens, ShortcutAction.SmartDraw,
        ShortcutAction.ShareRegion, ShortcutAction.FocusMenu, ShortcutAction.ZoomMenu,
    ];

    extension(ShortcutAction)
    {
        public static ValueList<ShortcutAction> AllCases => RawValues.AllCases;

        /// <summary>Buttons people can add to the toolbar. Properties, Board and Clear all have fixed places (<see cref="ToolbarControl"/>).</summary>
        public static ValueList<ShortcutAction> ToolbarChoices => Choices;

        public static ShortcutAction? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }

    extension(ShortcutAction action)
    {
        public string RawValue => RawValues.RawValue(action);

        public bool CanBeInToolbar => Choices.Contains(action);

        /// <summary>The Spotlight and Zoom menus open with a click; they have no key.</summary>
        public bool IsMenu => action is ShortcutAction.FocusMenu or ShortcutAction.ZoomMenu;

        public string Label => action switch
        {
            ShortcutAction.Properties => "Properties",
            ShortcutAction.Board => "Board",
            ShortcutAction.Spotlight => "Spotlight",
            ShortcutAction.CursorHalo => "Cursor halo",
            ShortcutAction.PointerLens => "Pointer lens",
            ShortcutAction.SmartDraw => "Smart draw",
            ShortcutAction.ShareRegion => "Share region",
            ShortcutAction.ClearAll => "Clear all",
            ShortcutAction.FocusMenu => "Spotlight menu",
            ShortcutAction.ZoomMenu => "Zoom menu",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        public string Detail => action switch
        {
            ShortcutAction.Properties => "Show or hide the properties below the tools.",
            ShortcutAction.Board => "Switch between desktop, whiteboard and blackboard.",
            ShortcutAction.Spotlight => "Turn the spotlight on or off.",
            ShortcutAction.CursorHalo => "Turn the ring around your pointer on or off.",
            ShortcutAction.PointerLens => "Turn the zoom lens under your pointer on or off.",
            ShortcutAction.SmartDraw => "Turn shape recognition for the pen on or off.",
            ShortcutAction.ShareRegion => "Choose part of a display and open a window to share in your call.",
            ShortcutAction.ClearAll => "Remove all drawings from every display. You can undo it.",
            ShortcutAction.FocusMenu => "Spotlight, cursor halo and cleanup, in one menu.",
            ShortcutAction.ZoomMenu => "Pointer lens, screen zoom and zoom levels, in one menu.",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        /// <summary>The SF Symbol name on the Mac; Windows maps it through the icon table (plan D-6).</summary>
        public string Symbol => action switch
        {
            ShortcutAction.Properties => "slider.horizontal.3",
            ShortcutAction.Board => "rectangle.inset.filled",
            ShortcutAction.Spotlight => "flashlight.on.fill",
            ShortcutAction.CursorHalo => "circle.dotted",
            ShortcutAction.PointerLens => "plus.magnifyingglass",
            ShortcutAction.SmartDraw => "wand.and.stars",
            ShortcutAction.ShareRegion => "rectangle.on.rectangle",
            ShortcutAction.ClearAll => "trash",
            ShortcutAction.FocusMenu => "light.beacon.max",
            ShortcutAction.ZoomMenu => "arrow.up.left.and.down.right.magnifyingglass",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    extension(Tool tool)
    {
        /// <summary>Tools that take part when "Share style between tools" is on.</summary>
        public bool SharesStyle => !tool.IsProtection && tool is not (Tool.Select or Tool.Eraser or Tool.Image);
    }
}

/// <summary>Something a local shortcut can do: pick a tool (`.tool`) or press a toolbar button (`.action`).</summary>
public readonly record struct ShortcutCommand
{
    private ShortcutCommand(Tool? tool, ShortcutAction? action)
    {
        Tool = tool;
        Action = action;
    }

    /// <summary>The tool of a `.tool` command, otherwise null.</summary>
    public Tool? Tool { get; }

    /// <summary>The button of an `.action` command, otherwise null.</summary>
    public ShortcutAction? Action { get; }

    public string Id => Tool is { } tool ? $"tool.{tool.RawValue}" : $"action.{Action.GetValueOrDefault().RawValue}";

    public string Label => Tool is { } tool
        ? tool == Core.Tool.Cover ? "Effects" : tool.Label
        : Action.GetValueOrDefault().Label;

    public static ShortcutCommand ForTool(Tool tool) => new(tool, null);

    public static ShortcutCommand ForAction(ShortcutAction action) => new(null, action);

    /// <summary>Swift `ShortcutCommand(id:)`: "tool.&lt;raw value&gt;" or "action.&lt;raw value&gt;".</summary>
    public static ShortcutCommand? FromId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.StartsWith("tool.", StringComparison.Ordinal) && Core.Tool.FromRawValue(id[5..]) is { } tool)
        {
            return ForTool(tool);
        }
        if (id.StartsWith("action.", StringComparison.Ordinal) && ShortcutAction.FromRawValue(id[7..]) is { } action)
        {
            return ForAction(action);
        }
        return null;
    }
}

public partial struct WorkspacePreferences
{
    /// <summary>
    /// Every local key binding. Plain tool keys stay in <see cref="ToolShortcuts"/> so older versions can still read them;
    /// Shift tool keys and toolbar button keys live in <see cref="CommandShortcuts"/>, which older versions ignore.
    /// </summary>
    public readonly IReadOnlyDictionary<LocalShortcut, ShortcutCommand> LocalShortcuts
    {
        get
        {
            var result = new Dictionary<LocalShortcut, ShortcutCommand>();
            foreach (var (key, name) in ToolShortcuts)
            {
                if (LocalShortcut.Create(key) is { } shortcut && Tool.FromRawValue(name) is { } tool)
                {
                    result[shortcut] = ShortcutCommand.ForTool(tool);
                }
            }
            foreach (var (key, id) in CommandShortcuts ?? EmptyStrings)
            {
                if (LocalShortcut.FromStorageKey(key) is { } shortcut && ShortcutCommand.FromId(id) is { } command)
                {
                    result[shortcut] = command;
                }
            }
            return result;
        }
    }

    /// <summary>Swift `shortcut(for:)`.</summary>
    public readonly LocalShortcut? ShortcutFor(ShortcutCommand command)
    {
        if (command.Tool is { } tool)
        {
            foreach (var (key, value) in ToolShortcuts)
            {
                if (SwiftText.Equals(value, tool.RawValue))
                {
                    return LocalShortcut.Create(key);
                }
            }
        }
        foreach (var (key, value) in CommandShortcuts ?? EmptyStrings)
        {
            if (SwiftText.Equals(value, command.Id))
            {
                return LocalShortcut.FromStorageKey(key);
            }
        }
        return null;
    }

    /// <summary>Swift `command(forPressed:)`. Shift with a letter that has no Shift binding keeps its plain meaning.</summary>
    public readonly ShortcutCommand? CommandForPressed(LocalShortcut shortcut)
    {
        var bindings = LocalShortcuts;
        if (bindings.TryGetValue(shortcut, out var command))
        {
            return command;
        }
        return shortcut.Shift && bindings.TryGetValue(shortcut.Unshifted, out var plain) ? plain : null;
    }

    /// <summary>
    /// Gives <paramref name="shortcut"/> to <paramref name="command"/> and removes the command's previous key. A key that
    /// belonged to another command moves; that command is returned so the caller can say what changed. Null clears the key.
    /// </summary>
    public ShortcutCommand? Bind(LocalShortcut? shortcut, ShortcutCommand command)
    {
        var updated = this;
        updated.RemoveBinding(command);
        ShortcutCommand? displaced = null;
        if (shortcut is { } menuKey && command.Action is { IsMenu: true } menu)
        {
            throw CanvasError.InvalidDocument($"{menu.Label} opens with a click, so it has no key. {menuKey.Label} stays free.");
        }
        if (shortcut is { } key)
        {
            if (key.IsInkColor)
            {
                throw CanvasError.InvalidDocument($"{SwiftText.Uppercased(key.Key)} changes the ink color. Choose another key.");
            }
            if (updated.LocalShortcuts.TryGetValue(key, out var owner))
            {
                displaced = owner;
                updated.RemoveBinding(owner);
            }
            if (command.Tool is { } tool && !key.Shift)
            {
                updated.ToolShortcuts = updated.ToolShortcuts.SetItem(key.Key, tool.RawValue);
            }
            else
            {
                updated.CommandShortcuts = (updated.CommandShortcuts ?? EmptyStrings).SetItem(key.StorageKey, command.Id);
            }
        }
        updated.Validate();
        this = updated;
        return displaced;
    }

    public void ResetLocalShortcuts()
    {
        ToolShortcuts = DefaultToolShortcuts;
        CommandShortcuts = null;
    }

    private void RemoveBinding(ShortcutCommand command)
    {
        if (command.Tool is { } tool)
        {
            ToolShortcuts = RemoveValues(ToolShortcuts, tool.RawValue);
        }
        var remaining = RemoveValues(CommandShortcuts ?? EmptyStrings, command.Id);
        CommandShortcuts = remaining.IsEmpty ? null : remaining;
    }

    internal readonly void ValidateToolbarItems()
    {
        if (ToolbarItems is not { } items)
        {
            return;
        }
        var commands = new List<ShortcutCommand>(items.Count);
        foreach (var item in items)
        {
            if (ShortcutCommand.FromId(item) is { } command)
            {
                commands.Add(command);
            }
        }
        var allowed = commands.TrueForAll(command => command.Action is not { } action || action.CanBeInToolbar);
        if (items.IsEmpty || commands.Count != items.Count
            || items.Count > Tool.AllCases.Count + ShortcutAction.ToolbarChoices.Count
            || new HashSet<string>(items, SwiftText.Comparer).Count != items.Count
            || !commands.Contains(ShortcutCommand.ForTool(Tool.Select)) || !allowed)
        {
            throw CanvasError.InvalidDocument("The toolbar must contain unique supported tools and buttons, including Select.");
        }
    }

    internal readonly void ValidateCommandShortcuts()
    {
        var commands = new HashSet<string>(SwiftText.Comparer);
        foreach (var tool in ToolShortcuts.Values)
        {
            commands.Add($"tool.{tool}");
        }
        if (CommandShortcuts is not { } shortcuts)
        {
            return;
        }
        if (shortcuts.Count > Tool.AllCases.Count + ShortcutAction.AllCases.Count)
        {
            throw CanvasError.InvalidDocument("The preferences contain invalid limits.");
        }
        foreach (var (key, id) in shortcuts)
        {
            if (LocalShortcut.FromStorageKey(key) is not { } shortcut || ShortcutCommand.FromId(id) is not { } command)
            {
                throw CanvasError.InvalidDocument("Keyboard shortcuts must use one letter or number. Shift works with letters.");
            }
            if (shortcut.IsInkColor)
            {
                throw CanvasError.InvalidDocument($"{SwiftText.Uppercased(shortcut.Key)} changes the ink color. Choose another key.");
            }
            if (command.Tool is not null && !shortcut.Shift)
            {
                throw CanvasError.InvalidDocument("Plain tool keys belong with the other tool keys.");
            }
            if (command.Action is { IsMenu: true } menu)
            {
                throw CanvasError.InvalidDocument($"{menu.Label} opens with a click, so it has no key.");
            }
            if (!(shortcut.Shift || !ToolShortcuts.ContainsKey(shortcut.Key)) || !commands.Add(command.Id))
            {
                throw CanvasError.InvalidDocument("Each key and each tool or button can have only one shortcut.");
            }
        }
    }
}

public partial struct Style
{
    /// <summary>
    /// Copies the parts of the look that changed from <paramref name="oldStyle"/> to <paramref name="newStyle"/>: colors,
    /// fill, opacity, width, line pattern and sloppiness. Tool-specific settings such as text size and arrowheads stay separate.
    /// </summary>
    public void AdoptSharedChanges(Style oldStyle, Style newStyle, bool supportsFill)
    {
        if (oldStyle.Color != newStyle.Color || oldStyle.StrokeGradient != newStyle.StrokeGradient)
        {
            Color = newStyle.Color;
            StrokeGradient = newStyle.StrokeGradient;
        }
        if (supportsFill && (oldStyle.Fill != newStyle.Fill || oldStyle.FillGradient != newStyle.FillGradient))
        {
            Fill = newStyle.Fill;
            FillGradient = newStyle.FillGradient;
        }
        if (supportsFill && oldStyle.FillPattern != newStyle.FillPattern)
        {
            FillPattern = newStyle.FillPattern;
        }
        if (oldStyle.Opacity != newStyle.Opacity)
        {
            Opacity = newStyle.Opacity;
        }
        if (oldStyle.Width != newStyle.Width)
        {
            Width = newStyle.Width;
        }
        if (oldStyle.Pattern != newStyle.Pattern)
        {
            Pattern = newStyle.Pattern;
        }
        if (oldStyle.Roughness != newStyle.Roughness)
        {
            Roughness = newStyle.Roughness;
        }
    }
}
