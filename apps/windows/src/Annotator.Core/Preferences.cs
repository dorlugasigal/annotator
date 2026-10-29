// Port of apps/macos/Sources/AnnotationCore/Preferences.swift.
using System.Collections.Immutable;
using AnimationStyle = Annotator.Core.VisibilityAnimationStyle;
using Shortcut = Annotator.Core.ActivationShortcut;

namespace Annotator.Core;

public enum ToolbarOrientation { Horizontal, Vertical }

public enum ToolbarDockEdge { Automatic, Top, Bottom, Left, Right }

public enum VisibilityAnimationStyle
{
    Slide, Fluid, Quick, Fade, Reveal, Curtain, Fold, Rise, Dissolve, Magic, Corners, Pop, Drop, Focus, None,
}

public enum AppTheme { System, Light, Dark }

public enum ToolCursorStyle { ToolPreview, Crosshair, System }

public static class ToolbarOrientationExtensions
{
    internal static readonly RawValueMap<ToolbarOrientation> RawValues = new("horizontal", "vertical");

    extension(ToolbarOrientation orientation)
    {
        public string RawValue => RawValues.RawValue(orientation);
    }

    extension(ToolbarOrientation)
    {
        public static ValueList<ToolbarOrientation> AllCases => RawValues.AllCases;

        public static ToolbarOrientation? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class ToolbarDockEdgeExtensions
{
    internal static readonly RawValueMap<ToolbarDockEdge> RawValues = new("automatic", "top", "bottom", "left", "right");

    extension(ToolbarDockEdge edge)
    {
        public string RawValue => RawValues.RawValue(edge);
    }

    extension(ToolbarDockEdge)
    {
        public static ValueList<ToolbarDockEdge> AllCases => RawValues.AllCases;

        public static ToolbarDockEdge? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class VisibilityAnimationStyleExtensions
{
    internal static readonly RawValueMap<VisibilityAnimationStyle> RawValues = new(
        "slide", "fluid", "quick", "fade", "reveal", "curtain", "fold", "rise", "dissolve", "magic", "corners", "pop", "drop", "focus", "none");

    private static readonly ValueList<VisibilityAnimationStyle> AvailableStyles =
    [
        AnimationStyle.Dissolve, AnimationStyle.Slide, AnimationStyle.Magic, AnimationStyle.Corners, AnimationStyle.Pop,
        AnimationStyle.Drop, AnimationStyle.Focus, AnimationStyle.Fade, AnimationStyle.None,
    ];

    extension(VisibilityAnimationStyle style)
    {
        public string RawValue => RawValues.RawValue(style);

        /// <summary>Retired styles map to the closest style that is still offered.</summary>
        public VisibilityAnimationStyle Resolved => style switch
        {
            AnimationStyle.Fluid or AnimationStyle.Quick => AnimationStyle.Slide,
            AnimationStyle.Reveal or AnimationStyle.Curtain or AnimationStyle.Fold or AnimationStyle.Rise => AnimationStyle.Fade,
            _ => style,
        };
    }

    extension(VisibilityAnimationStyle)
    {
        public static ValueList<VisibilityAnimationStyle> AllCases => RawValues.AllCases;

        /// <summary>The styles offered in the picker, in order.</summary>
        public static ValueList<VisibilityAnimationStyle> Available => AvailableStyles;

        public static VisibilityAnimationStyle? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class AppThemeExtensions
{
    internal static readonly RawValueMap<AppTheme> RawValues = new("system", "light", "dark");

    extension(AppTheme theme)
    {
        public string RawValue => RawValues.RawValue(theme);
    }

    extension(AppTheme)
    {
        public static ValueList<AppTheme> AllCases => RawValues.AllCases;

        public static AppTheme? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class ToolCursorStyleExtensions
{
    internal static readonly RawValueMap<ToolCursorStyle> RawValues = new("toolPreview", "crosshair", "system");

    extension(ToolCursorStyle style)
    {
        public string RawValue => RawValues.RawValue(style);
    }

    extension(ToolCursorStyle)
    {
        public static ValueList<ToolCursorStyle> AllCases => RawValues.AllCases;

        public static ToolCursorStyle? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

/// <summary>A global shortcut. Key codes are the macOS virtual key codes stored by the Mac. Stored properties are public fields (see Element.cs).</summary>
public struct ActivationShortcut : IEquatable<ActivationShortcut>
{
    public uint KeyCode;
    public bool Control;
    public bool Option;
    public bool Command;
    public bool Shift;
    public string Character;

    public ActivationShortcut(uint keyCode, string character, bool control = false, bool option = false,
                              bool command = false, bool shift = false)
    {
        KeyCode = keyCode;
        Character = character;
        Control = control;
        Option = option;
        Command = command;
        Shift = shift;
    }

    public static ActivationShortcut Standard => new(19, "2", control: true);

    public static ActivationShortcut HideTools => new(19, "2", control: true, option: true);

    public static ActivationShortcut LiveZoom => new(18, "1", control: true);

    public static ActivationShortcut ResumeDrawing => new(19, "2", control: true, shift: true);

    public static ActivationShortcut BreakTimer => new(20, "3", control: true);

    public readonly string Label =>
        (Control ? "⌃" : "") + (Option ? "⌥" : "") + (Shift ? "⇧" : "") + (Command ? "⌘" : "") + SwiftText.Uppercased(Character);

    public readonly bool UsesSameKeys(ActivationShortcut other) =>
        KeyCode == other.KeyCode && Control == other.Control && Option == other.Option
        && Command == other.Command && Shift == other.Shift;

    public readonly void Validate()
    {
        var alphanumeric = true;
        foreach (var rune in Character.EnumerateRunes())
        {
            alphanumeric &= SwiftText.IsAlphanumeric(rune);
        }
        if (KeyCode > 127 || SwiftText.CharacterCount(Character) != 1 || !alphanumeric || !(Control || Option || Command))
        {
            throw CanvasError.InvalidDocument("Choose a letter or number with Control, Option, or Command for the global shortcut.");
        }
    }

    public readonly bool Equals(ActivationShortcut other) =>
        UsesSameKeys(other) && SwiftText.Equals(Character, other.Character);

    public override readonly bool Equals(object? obj) => obj is ActivationShortcut other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(KeyCode, Control, Option, Command, Shift, SwiftText.GetHashCode(Character));

    public static bool operator ==(ActivationShortcut left, ActivationShortcut right) => left.Equals(right);

    public static bool operator !=(ActivationShortcut left, ActivationShortcut right) => !left.Equals(right);
}

/// <summary>
/// Swift `WorkspacePreferences`: the preference profile. Stored properties are public fields (see Element.cs).
/// Swift dictionaries are immutable sorted dictionaries with Swift string keys (<see cref="SwiftText.Comparer"/>); Swift arrays are <see cref="ValueList{T}"/>.
/// </summary>
public partial struct WorkspacePreferences : IEquatable<WorkspacePreferences>
{
    public long SchemaVersion = 1;
    public ImmutableSortedDictionary<string, Style> ToolStyles = EmptyStyles;
    public ImmutableSortedDictionary<string, string> ToolShortcuts = DefaultToolShortcuts;
    public long? ShortcutVersion = 2;
    public ActivationShortcut ActivationShortcut = Shortcut.Standard;
    public bool SmartDraw;
    public double SpotlightRadius = 130;
    public ActivationShortcut? HideShortcut;
    public ActivationShortcut? ZoomShortcut;
    public ActivationShortcut? ResumeShortcut;
    public ToolbarOrientation? ToolbarOrientation;
    public ToolbarDockEdge? ToolbarDockEdge;
    public Tool? LastCoverMode;
    public ValueList<string>? ToolbarTools;
    public bool? ShowMenuBarIcon;
    public ToolCursorStyle? CursorStyle;
    public bool? QuickPreferencesExpanded;
    public bool? InspectorPinned;
    public bool? AnimateVisibility;
    public VisibilityAnimationStyle? VisibilityAnimationStyle;
    public AppTheme? Theme;
    public bool? ShowShortcutHints;
    public CursorHighlightSettings? CursorHighlight;
    public ImmutableSortedDictionary<string, string>? CommandShortcuts;
    public ToolbarSize? ToolbarSize;
    public bool? ShareToolStyles;

    /// <summary>Toolbar order including buttons such as Spotlight. <see cref="ToolbarTools"/> keeps the tools for older versions.</summary>
    public ValueList<string>? ToolbarItems;

    /// <summary>Break screen choices (specification 014). <c>null</c> means the defaults.</summary>
    public BreakTimerSettings? BreakTimer;
    public ActivationShortcut? BreakShortcut;

    /// <summary>The toolbar's own buttons and menus that are turned off (<see cref="ToolbarControl"/> raw values).</summary>
    public ValueList<string>? HiddenToolbarControls;

    /// <summary>Seconds the pointer can stay off the toolbar before it shrinks to one button. <c>null</c> keeps it open.</summary>
    public double? ToolbarCollapseDelay;

    /// <summary>Properties that show all their choices side by side (<see cref="PropertyChoice"/> raw values). <c>null</c>
    /// means every property is one button.</summary>
    public ValueList<string>? ExpandedPropertyChoices;

    private const int MaximumBytes = 256 * 1_024;

    public WorkspacePreferences()
    {
    }

    public static ImmutableSortedDictionary<string, string> LegacyToolShortcuts { get; } = SortedStrings(
        ("v", "select"), ("p", "pen"), ("h", "highlighter"), ("r", "rectangle"), ("o", "ellipse"),
        ("d", "diamond"), ("a", "arrow"), ("l", "line"), ("t", "text"), ("e", "eraser"));

    public static ImmutableSortedDictionary<string, string> DefaultToolShortcuts { get; } = SortedStrings(
        ("1", "select"), ("2", "rectangle"), ("3", "diamond"), ("4", "ellipse"), ("5", "arrow"),
        ("6", "line"), ("7", "pen"), ("8", "text"), ("9", "highlighter"), ("0", "eraser"),
        ("u", "blur"), ("s", "cover"), ("x", "pixelate"), ("t", "distort"), ("m", "ruler"),
        ("n", "counter"), ("i", "image"), ("d", "database"), ("q", "queue"), ("h", "person"),
        ("f", "grid"), ("a", "axes"));

    public static ValueList<Tool> DefaultToolbar { get; } =
    [
        Tool.Select, Tool.Rectangle, Tool.Diamond, Tool.Ellipse, Tool.Arrow, Tool.Line, Tool.Pen, Tool.Text,
        Tool.Highlighter, Tool.Eraser, Tool.Cover,
    ];

    internal static ImmutableSortedDictionary<string, Style> EmptyStyles { get; } =
        ImmutableSortedDictionary.Create<string, Style>(SwiftText.Comparer);

    internal static ImmutableSortedDictionary<string, string> EmptyStrings { get; } =
        ImmutableSortedDictionary.Create<string, string>(SwiftText.Comparer);

    public readonly void Validate()
    {
        CursorHighlight?.Validate();
        if (LastCoverMode is { } cover && !cover.IsProtection)
        {
            throw CanvasError.InvalidDocument("Unsupported cover mode.");
        }
        if (!(SchemaVersion >= 1 && SchemaVersion <= 2))
        {
            throw CanvasError.UnsupportedSchema(SchemaVersion);
        }
        Shortcut[] shortcuts =
        [
            ActivationShortcut, HideShortcut ?? Shortcut.HideTools, ZoomShortcut ?? Shortcut.LiveZoom,
            ResumeShortcut ?? Shortcut.ResumeDrawing,
        ];
        for (var index = 0; index < shortcuts.Length; index++)
        {
            shortcuts[index].Validate();
            for (var prior = 0; prior < index; prior++)
            {
                if (shortcuts[prior].UsesSameKeys(shortcuts[index]))
                {
                    throw CanvasError.InvalidDocument("Show / hide, Hide only, Live zoom and Resume drawing need different shortcuts.");
                }
            }
        }
        var breakKeys = BreakShortcut ?? Shortcut.BreakTimer;
        breakKeys.Validate();
        if (shortcuts.Any(shortcut => shortcut.UsesSameKeys(breakKeys)))
        {
            throw CanvasError.InvalidDocument("Break timer needs its own shortcut. Choose keys that no other global shortcut uses.");
        }
        BreakTimer?.Validate();
        if (ToolbarTools is { } tools)
        {
            var distinct = new HashSet<string>(tools, SwiftText.Comparer);
            var supported = true;
            foreach (var tool in tools)
            {
                supported &= Tool.FromRawValue(tool) is not null;
            }
            if (tools.IsEmpty || !tools.Contains(Tool.Select.RawValue) || tools.Count > Tool.AllCases.Count
                || distinct.Count != tools.Count || !supported)
            {
                throw CanvasError.InvalidDocument("The toolbar must contain unique supported tools, including Select.");
            }
        }
        if (!double.IsFinite(SpotlightRadius) || !(SpotlightRadius >= 50 && SpotlightRadius <= 350)
            || ToolStyles.Count > Tool.AllCases.Count || ToolShortcuts.Count > Tool.AllCases.Count)
        {
            throw CanvasError.InvalidDocument("The preferences contain invalid limits.");
        }
        foreach (var (tool, style) in ToolStyles)
        {
            if (Tool.FromRawValue(tool) is null)
            {
                throw CanvasError.InvalidDocument("Unknown tool in preferences.");
            }
            style.Validate();
        }
        var assignedTools = new HashSet<string>(SwiftText.Comparer);
        foreach (var (key, tool) in ToolShortcuts)
        {
            var alphanumeric = true;
            foreach (var rune in key.EnumerateRunes())
            {
                alphanumeric &= SwiftText.IsAlphanumeric(rune);
            }
            if (SwiftText.CharacterCount(key) != 1 || !SwiftText.Equals(key, SwiftText.Lowercased(key)) || !alphanumeric
                || Tool.FromRawValue(tool) is null || !assignedTools.Add(tool))
            {
                throw CanvasError.InvalidDocument("Tool shortcuts must use unique letters or numbers.");
            }
            if (InkShortcut.Choices.ContainsKey(key))
            {
                throw CanvasError.InvalidDocument(
                    $"{SwiftText.Uppercased(key)} changes ink color. Choose a number or another letter for a tool.");
            }
        }
        ValidateCommandShortcuts();
        ValidateToolbarItems();
        ValidateToolbarControls();
    }

    public void AssignShortcut(string key, Tool tool)
    {
        ArgumentNullException.ThrowIfNull(key);
        var normalized = SwiftText.Lowercased(key);
        var updated = this;
        updated.ToolShortcuts = RemoveValues(updated.ToolShortcuts, tool.RawValue);
        if (normalized.Length != 0)
        {
            if (updated.ToolShortcuts.ContainsKey(normalized) || updated.CommandShortcuts?.ContainsKey(normalized) == true)
            {
                throw CanvasError.InvalidDocument("That key is already assigned to another tool.");
            }
            updated.ToolShortcuts = updated.ToolShortcuts.SetItem(normalized, tool.RawValue);
        }
        updated.Validate();
        this = updated;
    }

    /// <summary>The profile as JSON. Gradient styles raise the format to 2.</summary>
    public readonly byte[] Data()
    {
        var saved = this;
        if (UsesGradients(ToolStyles))
        {
            saved.SchemaVersion = 2;
        }
        saved.Validate();
        return AnnotatorJson.Encode(saved);
    }

    public static WorkspacePreferences Read(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes)
        {
            throw CanvasError.InvalidDocument("The preferences file is too large.");
        }
        var preferences = AnnotatorJson.Decode<WorkspacePreferences>(data);
        if (!(preferences.SchemaVersion >= 2 || !UsesGradients(preferences.ToolStyles)))
        {
            throw CanvasError.InvalidDocument("Gradient colors need a newer profile format.");
        }
        if (preferences.VisibilityAnimationStyle == AnimationStyle.Fluid)
        {
            preferences.VisibilityAnimationStyle = AnimationStyle.Slide;
        }
        preferences.NormalizeToolbarControls();
        if (preferences.ShortcutVersion is null or 1)
        {
            var custom = new List<KeyValuePair<string, string>>();
            foreach (var entry in preferences.ToolShortcuts)
            {
                var legacy = LegacyToolShortcuts.TryGetValue(entry.Key, out var legacyTool) ? legacyTool : null;
                if (!SwiftText.Equals(legacy, entry.Value) && !InkShortcut.Choices.ContainsKey(entry.Key))
                {
                    custom.Add(entry);
                }
            }
            custom.Sort((left, right) => SwiftText.Compare(left.Key, right.Key));
            preferences.ToolShortcuts = DefaultToolShortcuts;
            foreach (var (key, tool) in custom)
            {
                preferences.ToolShortcuts = RemoveValues(preferences.ToolShortcuts, tool).SetItem(key, tool);
            }
            preferences.ShortcutVersion = 2;
        }
        if (preferences.ShortcutVersion != 2)
        {
            throw CanvasError.InvalidDocument("Unsupported shortcut profile version.");
        }
        if (preferences.ZoomShortcut is null
            && (preferences.ActivationShortcut.UsesSameKeys(Shortcut.LiveZoom)
                || (preferences.HideShortcut ?? Shortcut.HideTools).UsesSameKeys(Shortcut.LiveZoom)))
        {
            Shortcut[] taken = [preferences.ActivationShortcut, preferences.HideShortcut ?? Shortcut.HideTools];
            Shortcut[] candidates =
            [
                new(18, "1", control: true, option: true),
                new(18, "1", control: true, shift: true),
                new(18, "1", control: true, command: true),
            ];
            preferences.ZoomShortcut = FirstFree(candidates, taken);
        }
        Shortcut[] existing =
        [
            preferences.ActivationShortcut, preferences.HideShortcut ?? Shortcut.HideTools,
            preferences.ZoomShortcut ?? Shortcut.LiveZoom,
        ];
        if (preferences.ResumeShortcut is null && existing.Any(shortcut => shortcut.UsesSameKeys(Shortcut.ResumeDrawing)))
        {
            Shortcut[] candidates =
            [
                Shortcut.ResumeDrawing,
                new(19, "2", control: true, option: true, shift: true),
                new(19, "2", control: true, command: true, shift: true),
                new(19, "2", control: true, option: true, command: true, shift: true),
            ];
            preferences.ResumeShortcut = FirstFree(candidates, existing);
        }
        Shortcut[] globals = [.. existing, preferences.ResumeShortcut ?? Shortcut.ResumeDrawing];
        if (preferences.BreakShortcut is null && globals.Any(shortcut => shortcut.UsesSameKeys(Shortcut.BreakTimer)))
        {
            Shortcut[] candidates =
            [
                new(20, "3", control: true, option: true),
                new(20, "3", control: true, shift: true),
                new(20, "3", control: true, command: true),
                new(20, "3", control: true, option: true, shift: true),
            ];
            preferences.BreakShortcut = FirstFree(candidates, globals);
        }
        preferences.Validate();
        return preferences;
    }

    public readonly bool Equals(WorkspacePreferences other) =>
        SchemaVersion == other.SchemaVersion && DictionaryEquals(ToolStyles, other.ToolStyles)
        && DictionaryEquals(ToolShortcuts, other.ToolShortcuts) && ShortcutVersion == other.ShortcutVersion
        && ActivationShortcut == other.ActivationShortcut && SmartDraw == other.SmartDraw
        && SpotlightRadius == other.SpotlightRadius && HideShortcut == other.HideShortcut
        && ZoomShortcut == other.ZoomShortcut && ResumeShortcut == other.ResumeShortcut
        && ToolbarOrientation == other.ToolbarOrientation && ToolbarDockEdge == other.ToolbarDockEdge
        && LastCoverMode == other.LastCoverMode && ToolbarTools == other.ToolbarTools
        && ShowMenuBarIcon == other.ShowMenuBarIcon && CursorStyle == other.CursorStyle
        && QuickPreferencesExpanded == other.QuickPreferencesExpanded && InspectorPinned == other.InspectorPinned
        && AnimateVisibility == other.AnimateVisibility && VisibilityAnimationStyle == other.VisibilityAnimationStyle
        && Theme == other.Theme && ShowShortcutHints == other.ShowShortcutHints && CursorHighlight == other.CursorHighlight
        && OptionalDictionaryEquals(CommandShortcuts, other.CommandShortcuts) && ToolbarSize == other.ToolbarSize
        && ShareToolStyles == other.ShareToolStyles && ToolbarItems == other.ToolbarItems
        && BreakTimer == other.BreakTimer && BreakShortcut == other.BreakShortcut
        && HiddenToolbarControls == other.HiddenToolbarControls && ToolbarCollapseDelay == other.ToolbarCollapseDelay
        && ExpandedPropertyChoices == other.ExpandedPropertyChoices;

    public override readonly bool Equals(object? obj) => obj is WorkspacePreferences other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(SchemaVersion, ToolStyles.Count, ToolShortcuts.Count, ActivationShortcut, SpotlightRadius);

    public static bool operator ==(WorkspacePreferences left, WorkspacePreferences right) => left.Equals(right);

    public static bool operator !=(WorkspacePreferences left, WorkspacePreferences right) => !left.Equals(right);

    internal static ImmutableSortedDictionary<string, string> RemoveValues(ImmutableSortedDictionary<string, string> dictionary, string value)
    {
        var result = dictionary;
        foreach (var entry in dictionary)
        {
            if (SwiftText.Equals(entry.Value, value))
            {
                result = result.Remove(entry.Key);
            }
        }
        return result;
    }

    private static bool UsesGradients(ImmutableSortedDictionary<string, Style> styles)
    {
        foreach (var style in styles.Values)
        {
            if (style.UsesGradient)
            {
                return true;
            }
        }
        return false;
    }

    private static Shortcut? FirstFree(Shortcut[] candidates, Shortcut[] taken)
    {
        foreach (var candidate in candidates)
        {
            if (!taken.Any(shortcut => shortcut.UsesSameKeys(candidate)))
            {
                return candidate;
            }
        }
        return null;
    }

    private static ImmutableSortedDictionary<string, string> SortedStrings(params (string Key, string Value)[] entries)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(SwiftText.Comparer);
        foreach (var (key, value) in entries)
        {
            builder.Add(key, value);
        }
        return builder.ToImmutable();
    }

    private static bool DictionaryEquals<TValue>(ImmutableSortedDictionary<string, TValue> left, ImmutableSortedDictionary<string, TValue> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }
        if (left.Count != right.Count)
        {
            return false;
        }
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !EqualityComparer<TValue>.Default.Equals(value, other))
            {
                return false;
            }
        }
        return true;
    }

    private static bool OptionalDictionaryEquals(ImmutableSortedDictionary<string, string>? left, ImmutableSortedDictionary<string, string>? right) =>
        left is null ? right is null : right is not null && DictionaryEquals(left, right);
}
