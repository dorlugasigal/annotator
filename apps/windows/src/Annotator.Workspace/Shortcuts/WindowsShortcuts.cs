using System.Collections.Frozen;
using Annotator.Core;

namespace Annotator.Workspace.Shortcuts;

/// <summary>Windows modifier keys of a global shortcut.</summary>
[Flags]
public enum ChordModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A global shortcut as Windows registers it: modifiers plus the physical key as a PC set-1 scan code (0xE0xx for
/// extended keys). The app turns the scan code into the virtual key of the active keyboard layout, so the same physical
/// key is used as on the Mac (WIN-INP-04); keypad and special keys carry their fixed <see cref="VirtualKey"/> instead,
/// because their scan codes also name other keys. <see cref="Key"/> is the character shown on keycaps.
/// </summary>
public readonly record struct ShortcutChord(ChordModifiers Modifiers, uint ScanCode, string Key, uint? VirtualKey = null)
{
    /// <summary>Text keycaps in Windows order, for example "Ctrl+Alt+Shift+2".</summary>
    public string Label
    {
        get
        {
            string label = string.Empty;
            if (Modifiers.HasFlag(ChordModifiers.Ctrl))
            {
                label += "Ctrl+";
            }
            if (Modifiers.HasFlag(ChordModifiers.Alt))
            {
                label += "Alt+";
            }
            if (Modifiers.HasFlag(ChordModifiers.Shift))
            {
                label += "Shift+";
            }
            if (Modifiers.HasFlag(ChordModifiers.Win))
            {
                label += "Win+";
            }
            return label + Key;
        }
    }

    public override string ToString() => Label;
}

/// <summary>
/// One key of the macOS key table: the Carbon key code, the character (or the key's name for special keys), the PC
/// set-1 scan code of the same physical key (0xE0xx for extended keys) and, for keypad and special keys, the fixed
/// Windows virtual key.
/// </summary>
public readonly record struct MacKey(uint KeyCode, string Name, uint ScanCode, uint? VirtualKey = null);

/// <summary>
/// The Windows forms of the Mac's global shortcuts (spec 013, keyboard mapping and D-5): control becomes Ctrl,
/// option Alt, command Win and shift Shift. Profiles keep the macOS key code of the physical key.
/// </summary>
public static class WindowsShortcuts
{
    /// <summary>Show / hide: Ctrl+2.</summary>
    public static ShortcutChord ShowHideDefault { get; } = new(ChordModifiers.Ctrl, 0x03, "2");

    /// <summary>Resume drawing: Ctrl+Shift+2.</summary>
    public static ShortcutChord ResumeDrawingDefault { get; } = new(ChordModifiers.Ctrl | ChordModifiers.Shift, 0x03, "2");

    /// <summary>Hide only: Ctrl+Alt+Shift+2. The Mac's Control-Option-2 would be AltGr+2 on many layouts (D-5).</summary>
    public static ShortcutChord HideOnlyDefault { get; } = new(ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Shift, 0x03, "2");

    /// <summary>Live zoom: Ctrl+1 (WIN-CAP-05).</summary>
    public static ShortcutChord LiveZoomDefault { get; } = new(ChordModifiers.Ctrl, 0x02, "1");

    /// <summary>Break timer: Ctrl+3 (specification 014).</summary>
    public static ShortcutChord BreakTimerDefault { get; } = new(ChordModifiers.Ctrl, 0x04, "3");

    /// <summary>
    /// The macOS key table of WIN-INP-04 (Carbon kVK_* codes for the US ANSI layout, corpus
    /// <c>shortcuts/macos-key-codes.json</c>) with the PC set-1 scan code of the key in the same physical position.
    /// Keypad and special keys also carry their fixed virtual key. Profiles store the macOS code, so they move between
    /// platforms; recording a shortcut on Windows maps the pressed key back through <see cref="MacKeyCode"/>.
    /// </summary>
    public static IReadOnlyList<MacKey> KeyTable { get; } =
    [
        new(0x00, "a", 0x1E), new(0x0B, "b", 0x30), new(0x08, "c", 0x2E), new(0x02, "d", 0x20), new(0x0E, "e", 0x12),
        new(0x03, "f", 0x21), new(0x05, "g", 0x22), new(0x04, "h", 0x23), new(0x22, "i", 0x17), new(0x26, "j", 0x24),
        new(0x28, "k", 0x25), new(0x25, "l", 0x26), new(0x2E, "m", 0x32), new(0x2D, "n", 0x31), new(0x1F, "o", 0x18),
        new(0x23, "p", 0x19), new(0x0C, "q", 0x10), new(0x0F, "r", 0x13), new(0x01, "s", 0x1F), new(0x11, "t", 0x14),
        new(0x20, "u", 0x16), new(0x09, "v", 0x2F), new(0x0D, "w", 0x11), new(0x07, "x", 0x2D), new(0x10, "y", 0x15),
        new(0x06, "z", 0x2C), new(0x1D, "0", 0x0B), new(0x12, "1", 0x02), new(0x13, "2", 0x03), new(0x14, "3", 0x04),
        new(0x15, "4", 0x05), new(0x17, "5", 0x06), new(0x16, "6", 0x07), new(0x1A, "7", 0x08), new(0x1C, "8", 0x09),
        new(0x19, "9", 0x0A),
        new(0x52, "0", 0x52, 0x60), new(0x53, "1", 0x4F, 0x61), new(0x54, "2", 0x50, 0x62), new(0x55, "3", 0x51, 0x63),
        new(0x56, "4", 0x4B, 0x64), new(0x57, "5", 0x4C, 0x65), new(0x58, "6", 0x4D, 0x66), new(0x59, "7", 0x47, 0x67),
        new(0x5B, "8", 0x48, 0x68), new(0x5C, "9", 0x49, 0x69),
        new(0x24, "Return", 0x1C, 0x0D), new(0x31, "Space", 0x39, 0x20), new(0x33, "Delete", 0x0E, 0x08),
        new(0x75, "ForwardDelete", 0xE053, 0x2E), new(0x35, "Escape", 0x01, 0x1B), new(0x7B, "LeftArrow", 0xE04B, 0x25),
        new(0x7C, "RightArrow", 0xE04D, 0x27), new(0x7D, "DownArrow", 0xE050, 0x28), new(0x7E, "UpArrow", 0xE048, 0x26),
    ];

    /// <summary>
    /// The Mac's tool library key, Command-L. Windows uses Ctrl+L for it (WIN-INP-02), so a global Control-L
    /// chord, the same keys on Windows, also takes it.
    /// </summary>
    public static ActivationShortcut ToolLibraryShortcut => new(0x25, "l", command: true);

    private static readonly FrozenDictionary<uint, MacKey> s_byMacCode = KeyTable.ToFrozenDictionary(key => key.KeyCode);

    private static readonly FrozenDictionary<uint, MacKey> s_byScanCode = KeyTable.ToFrozenDictionary(key => key.ScanCode);

    /// <summary>The PC scan code of the key at a macOS key code's position, or null for keys outside the table.</summary>
    public static uint? ScanCode(uint macKeyCode) => s_byMacCode.TryGetValue(macKeyCode, out MacKey key) ? key.ScanCode : null;

    /// <summary>The macOS key code to store for the key with this PC scan code, or null for keys outside the table.</summary>
    public static uint? MacKeyCode(uint scanCode) => s_byScanCode.TryGetValue(scanCode, out MacKey key) ? key.KeyCode : null;

    /// <summary>The fixed virtual key of a keypad or special key, or null for keys that depend on the keyboard layout.</summary>
    public static uint? VirtualKey(uint macKeyCode) => s_byMacCode.TryGetValue(macKeyCode, out MacKey key) ? key.VirtualKey : null;

    /// <summary>
    /// The character Settings stores for a number-row key while recording a global shortcut, whatever Shift types
    /// (the Mac's digit map); null for other keys, which store their unmodified character in lowercase.
    /// </summary>
    public static string? SettingsDigit(uint macKeyCode) =>
        macKeyCode is 0x12 or 0x13 or 0x14 or 0x15 or 0x16 or 0x17 or 0x19 or 0x1A or 0x1C or 0x1D ? s_byMacCode[macKeyCode].Name : null;

    /// <summary>Whether Ctrl+L can open the tool library: no global shortcut uses Command-L or Control-L.</summary>
    public static bool ToolLibraryKeyIsFree(in WorkspacePreferences preferences)
    {
        ActivationShortcut command = ToolLibraryShortcut;
        var control = new ActivationShortcut(command.KeyCode, command.Character, control: true);
        ActivationShortcut[] global =
        [
            preferences.ActivationShortcut, preferences.HideShortcut ?? ActivationShortcut.HideTools,
            preferences.ZoomShortcut ?? ActivationShortcut.LiveZoom, preferences.ResumeShortcut ?? ActivationShortcut.ResumeDrawing,
            preferences.BreakShortcut ?? ActivationShortcut.BreakTimer,
        ];
        return !global.Any(shortcut => shortcut.UsesSameKeys(command) || shortcut.UsesSameKeys(control));
    }

    /// <summary>Maps a stored shortcut; null when its key has no Windows equivalent in the table.</summary>
    public static ShortcutChord? Chord(ActivationShortcut shortcut)
    {
        if (!s_byMacCode.TryGetValue(shortcut.KeyCode, out MacKey key))
        {
            return null;
        }
        ChordModifiers modifiers = ChordModifiers.None;
        if (shortcut.Control)
        {
            modifiers |= ChordModifiers.Ctrl;
        }
        if (shortcut.Option)
        {
            modifiers |= ChordModifiers.Alt;
        }
        if (shortcut.Shift)
        {
            modifiers |= ChordModifiers.Shift;
        }
        if (shortcut.Command)
        {
            modifiers |= ChordModifiers.Win;
        }
        return new ShortcutChord(modifiers, key.ScanCode, (shortcut.Character ?? string.Empty).ToUpperInvariant(), key.VirtualKey);
    }

    /// <summary>The chord for a global shortcut action from the profile, using the Windows default when the profile has none.</summary>
    public static ShortcutChord? Chord(GlobalShortcutAction action, in WorkspacePreferences preferences) => action switch
    {
        GlobalShortcutAction.ShowHide => Chord(preferences.ActivationShortcut),
        GlobalShortcutAction.ResumeDrawing => preferences.ResumeShortcut is { } resume ? Chord(resume) : ResumeDrawingDefault,
        GlobalShortcutAction.HideOnly => preferences.HideShortcut is { } hide ? Chord(hide) : HideOnlyDefault,
        GlobalShortcutAction.LiveZoom => preferences.ZoomShortcut is { } zoom ? Chord(zoom) : LiveZoomDefault,
        GlobalShortcutAction.BreakTimer => preferences.BreakShortcut is { } breakKeys ? Chord(breakKeys) : BreakTimerDefault,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Hide only's Windows default as a profile stores it: Control-Option-Shift-2, which Windows shows as Ctrl+Alt+Shift+2 (D-5).</summary>
    public static ActivationShortcut HideOnlyDefaultShortcut => new(0x13, "2", control: true, option: true, shift: true);

    /// <summary>
    /// The shortcut an action uses on Windows: the profile's, else the Windows default (D-5). A profile without its own Hide
    /// only shortcut keeps the Mac's Control-Option-2 on the Mac and uses Ctrl+Alt+Shift+2 on Windows.
    /// </summary>
    public static ActivationShortcut Shortcut(GlobalShortcutAction action, in WorkspacePreferences preferences) => action switch
    {
        GlobalShortcutAction.ShowHide => preferences.ActivationShortcut,
        GlobalShortcutAction.ResumeDrawing => preferences.ResumeShortcut ?? ActivationShortcut.ResumeDrawing,
        GlobalShortcutAction.HideOnly => preferences.HideShortcut ?? HideOnlyDefaultShortcut,
        GlobalShortcutAction.LiveZoom => preferences.ZoomShortcut ?? ActivationShortcut.LiveZoom,
        GlobalShortcutAction.BreakTimer => preferences.BreakShortcut ?? ActivationShortcut.BreakTimer,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The label of a stored shortcut in Windows keycap text, such as "Ctrl+Shift+2".</summary>
    public static string Label(ActivationShortcut shortcut) => Chord(shortcut)?.Label ?? Words(shortcut, "+");

    /// <summary>
    /// A stored shortcut's keys as Windows names them, one by one, for the keycaps and for screen readers: "Ctrl", "Alt",
    /// "Shift", "Win" and the key, in the order of <see cref="ShortcutChord.Label"/>.
    /// </summary>
    public static IReadOnlyList<string> Keys(ActivationShortcut shortcut)
    {
        var keys = new List<string>(5);
        if (shortcut.Control)
        {
            keys.Add("Ctrl");
        }
        if (shortcut.Option)
        {
            keys.Add("Alt");
        }
        if (shortcut.Shift)
        {
            keys.Add("Shift");
        }
        if (shortcut.Command)
        {
            keys.Add("Win");
        }
        keys.Add((shortcut.Character ?? string.Empty).ToUpperInvariant());
        return keys;
    }

    /// <summary>The spoken form of a shortcut, the Mac's "Control + Shift + 2" with Windows names: "Ctrl + Shift + 2".</summary>
    public static string Spoken(ActivationShortcut shortcut) => Words(shortcut, " + ");

    private static string Words(ActivationShortcut shortcut, string separator) => string.Join(separator, Keys(shortcut));

    /// <summary>The keycap label of an action's shortcut, as the Mac shows its label.</summary>
    public static string Label(GlobalShortcutAction action, in WorkspacePreferences preferences) =>
        Chord(action, preferences)?.Label ?? Fallback(action, preferences);

    /// <summary>A tool or button key: "7", or "Shift+7" (the Mac shows "⇧7").</summary>
    public static string Label(LocalShortcut shortcut) => (shortcut.Shift ? "Shift+" : string.Empty) + shortcut.Key.ToUpperInvariant();

    /// <summary>
    /// The Mac's registration failure message (HotkeyError) with Windows terms. <paramref name="error"/> is the
    /// Windows error code, as the Mac shows its OSStatus.
    /// </summary>
    public static string RegistrationFailure(string label, int error) =>
        $"The {label} shortcut could not be registered (error {error}). Another app or Windows may be using it. " +
        "Change it in Settings > Toolbar & Shortcuts, or use the tray menu command.";

    private static string Fallback(GlobalShortcutAction action, in WorkspacePreferences preferences)
    {
        ActivationShortcut shortcut = action switch
        {
            GlobalShortcutAction.ShowHide => preferences.ActivationShortcut,
            GlobalShortcutAction.ResumeDrawing => preferences.ResumeShortcut ?? ActivationShortcut.ResumeDrawing,
            GlobalShortcutAction.HideOnly => preferences.HideShortcut ?? ActivationShortcut.HideTools,
            GlobalShortcutAction.BreakTimer => preferences.BreakShortcut ?? ActivationShortcut.BreakTimer,
            _ => preferences.ZoomShortcut ?? ActivationShortcut.LiveZoom,
        };
        string label = string.Empty;
        if (shortcut.Control)
        {
            label += "Ctrl+";
        }
        if (shortcut.Option)
        {
            label += "Alt+";
        }
        if (shortcut.Shift)
        {
            label += "Shift+";
        }
        if (shortcut.Command)
        {
            label += "Win+";
        }
        return label + (shortcut.Character ?? string.Empty).ToUpperInvariant();
    }
}
