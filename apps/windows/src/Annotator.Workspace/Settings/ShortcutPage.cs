using Annotator.Core;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.Settings;

/// <summary>One entry of "Keys that stay the same": the keys as keycap text and what they do.</summary>
public readonly record struct FixedKey(string Keys, string Meaning)
{
    /// <summary>What UI Automation reads for the entry, the Mac's combined element: "Ctrl+W, whiteboard".</summary>
    public string Spoken => $"{Keys}, {Meaning}";
}

/// <summary>The fixed parts of the Toolbar &amp; Shortcuts page (ToolbarShortcutSettings.swift) in Windows terms.</summary>
public static class ShortcutPage
{
    /// <summary>The buttons that are always in the toolbar, in the Mac's order: Properties, Board and Clear all.</summary>
    public static IReadOnlyList<ShortcutCommand> AlwaysThere { get; } =
        [.. ShortcutAction.AllCases.Where(action => !action.CanBeInToolbar).Select(ShortcutCommand.ForAction)];

    /// <summary>
    /// Mac fixedKeysSection with the keyboard mapping of specification 013: Ctrl for Control and Command, Alt for Option,
    /// Shift for ⇧ and Delete for ⌫. Ctrl+L is listed only while no global shortcut takes it, and Alt + scroll only once
    /// zoom exists.
    /// </summary>
    public static IReadOnlyList<FixedKey> FixedKeys(in WorkspacePreferences preferences, bool zoomAvailable)
    {
        var keys = new List<FixedKey>
        {
            new("R G B Y O P C W K", "ink colors"),
            new("Ctrl+W", "whiteboard"),
            new("Ctrl+B", "blackboard"),
            new("Ctrl+Z", "undo"),
            new("Ctrl+Shift+Z", "redo"),
        };
        if (WindowsShortcuts.ToolLibraryKeyIsFree(preferences))
        {
            keys.Add(new FixedKey("Ctrl+L", "more tools"));
        }
        if (zoomAvailable)
        {
            keys.Add(new FixedKey("Alt + scroll", "zoom"));
        }
        keys.Add(new FixedKey("Esc", "use apps"));
        keys.Add(new FixedKey("Delete", "delete selection"));
        keys.Add(new FixedKey("← ↑ → ↓", "nudge; add Shift for 10 px"));
        return keys;
    }

    /// <summary>The key chip's accessibility value: the key, "Shift + S" for a Shift key, or "None".</summary>
    public static string ChipValue(LocalShortcut? shortcut) => shortcut is { } key ? key.SpokenLabel : "None";

    /// <summary>The key chip's help (Mac KeyChip .help).</summary>
    public static string ChipHelp(string name, LocalShortcut? shortcut) => shortcut is { } key
        ? $"{name}: {WindowsShortcuts.Label(key)}. Click, then press a new key."
        : $"Click, then press a key for {name}.";

    public const string ChipHint = "Press, then type a letter or number.";

    public const string GlobalHint = "Press, then hold Ctrl, Alt or Win and type a letter or number.";

    /// <summary>The global shortcut button's help (Mac GlobalKeyButton .help).</summary>
    public static string GlobalHelp(GlobalShortcutKind kind) => $"Click, then press a new combination for {kind.Title}.";
}
