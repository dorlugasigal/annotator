using Annotator.Core;
using Annotator.Platform.Hotkeys;
using Annotator.Workspace;
using Annotator.Workspace.Shortcuts;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Annotator.App.Shell;

/// <summary>
/// Registers the global shortcuts through the Platform <see cref="HotkeyService"/> (WIN-INP-04, WIN-INP-05):
/// Show / hide, Resume drawing, Live zoom, Hide only and Break timer. A refused or already used chord (for example Ctrl+1 taken by
/// ZoomIt) leaves the action without a shortcut and a typed, visible state in the workspace. As the workspace's
/// <see cref="IGlobalShortcutRegistrar"/>, it replaces a shortcut that Settings changes, keeping the previous one when
/// Windows refuses the new one.
/// </summary>
internal sealed class GlobalShortcuts : IDisposable, IGlobalShortcutRegistrar
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private const uint MapScanCodeToVirtualKey = 1;

    /// <summary>The actions whose feature exists on Windows, in the Mac's order.</summary>
    private static readonly GlobalShortcutAction[] s_built =
        [GlobalShortcutAction.ShowHide, GlobalShortcutAction.ResumeDrawing, GlobalShortcutAction.LiveZoom, GlobalShortcutAction.HideOnly,
         GlobalShortcutAction.BreakTimer];

    private readonly HotkeyService _service = new();
    private readonly Dictionary<HotkeyId, GlobalShortcutAction> _actions = [];
    private readonly Dictionary<GlobalShortcutAction, HotkeyId> _ids = [];
    private readonly Action<GlobalShortcutAction> _pressed;

    public GlobalShortcuts(Action<GlobalShortcutAction> pressed)
    {
        _pressed = pressed;
        _service.Pressed += (_, args) =>
        {
            // While Settings records a key, a pressed global shortcut is that key, not its action.
            if (_actions.TryGetValue(args.Id, out GlobalShortcutAction action) && !Settings.ShortcutRecordingGate.Offer(action))
            {
                _pressed(action);
            }
        };
    }

    /// <summary>Registers the actions that exist now and returns each one's state for the workspace.</summary>
    public IReadOnlyList<GlobalShortcutState> Register(in WorkspacePreferences preferences)
    {
        var states = new List<GlobalShortcutState>();
        foreach (GlobalShortcutAction action in s_built)
        {
            states.Add(Register(action, WindowsShortcuts.Shortcut(action, preferences)));
        }
        return states;
    }

    public bool Supports(GlobalShortcutAction action) => s_built.Contains(action);

    /// <summary>
    /// Registers <paramref name="shortcut"/> for <paramref name="action"/> in place of its current shortcut. A refused
    /// shortcut leaves the current one registered; an action whose feature is not built registers nothing.
    /// </summary>
    public GlobalShortcutState Replace(GlobalShortcutAction action, ActivationShortcut shortcut) => Supports(action)
        ? Register(action, shortcut)
        : new GlobalShortcutState(action, WindowsShortcuts.Label(shortcut), GlobalShortcutStatus.NotRegistered, null);

    public void Dispose() => _service.Dispose();

    /// <summary>
    /// True when a chord with Ctrl and Alt types a character in an installed keyboard layout, where Ctrl+Alt
    /// is AltGr; Windows would then lose that character while Annotator runs (WIN-INP-04).
    /// </summary>
    public static unsafe string? TypesCharacter(ShortcutChord chord)
    {
        if (!chord.Modifiers.HasFlag(ChordModifiers.Ctrl) || !chord.Modifiers.HasFlag(ChordModifiers.Alt) || chord.Modifiers.HasFlag(ChordModifiers.Win))
        {
            return null;
        }
        int count = PInvoke.GetKeyboardLayoutList(0, null);
        Span<HKL> layouts = count <= 32 ? stackalloc HKL[count] : new HKL[count];
        fixed (HKL* buffer = layouts)
        {
            count = PInvoke.GetKeyboardLayoutList(count, buffer);
        }
        Span<byte> keys = stackalloc byte[256];
        keys[(int)VIRTUAL_KEY.VK_CONTROL] = 0x80;
        keys[(int)VIRTUAL_KEY.VK_MENU] = 0x80;
        if (chord.Modifiers.HasFlag(ChordModifiers.Shift))
        {
            keys[(int)VIRTUAL_KEY.VK_SHIFT] = 0x80;
        }
        Span<char> text = stackalloc char[8];
        foreach (HKL layout in layouts[..count])
        {
            uint virtualKey = chord.VirtualKey ?? PInvoke.MapVirtualKeyEx(chord.ScanCode, (MAP_VIRTUAL_KEY_TYPE)MapScanCodeToVirtualKey, layout);
            if (virtualKey == 0)
            {
                continue;
            }
            fixed (byte* state = keys)
            fixed (char* output = text)
            {
                // Flag 4 leaves the keyboard state (dead keys) untouched.
                int produced = PInvoke.ToUnicodeEx(virtualKey, chord.ScanCode, state, output, text.Length, 4, layout);
                if (produced > 0 && !char.IsControl(text[0]))
                {
                    return text[..produced].ToString();
                }
            }
        }
        return null;
    }

    private GlobalShortcutState Register(GlobalShortcutAction action, ActivationShortcut shortcut)
    {
        string label = WindowsShortcuts.Label(shortcut);
        if (WindowsShortcuts.Chord(shortcut) is not { } chord)
        {
            return new GlobalShortcutState(action, label, GlobalShortcutStatus.Refused, WindowsShortcuts.RegistrationFailure(label, 0));
        }
        if (TypesCharacter(chord) is not null)
        {
            return new GlobalShortcutState(action, label, GlobalShortcutStatus.Refused,
                $"The {label} shortcut types a character with a keyboard layout on this computer, so Annotator does not use it. " +
                "Change it in Settings > Toolbar & Shortcuts, or use the tray menu command.");
        }
        // Keypad and special keys have fixed virtual keys; their scan codes would name navigation keys.
        uint virtualKey = chord.VirtualKey ?? PInvoke.MapVirtualKeyEx(chord.ScanCode, (MAP_VIRTUAL_KEY_TYPE)MapScanCodeToVirtualKey, PInvoke.GetKeyboardLayout(0));
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (chord.Modifiers.HasFlag(ChordModifiers.Ctrl))
        {
            modifiers |= HotkeyModifiers.Control;
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }
        if (chord.Modifiers.HasFlag(ChordModifiers.Win))
        {
            modifiers |= HotkeyModifiers.Windows;
        }
        HotkeyId? current = _ids.TryGetValue(action, out HotkeyId id) ? id : null;
        return _service.Replace(current, new HotkeyChord(modifiers, (int)virtualKey)) switch
        {
            HotkeyRegistrationResult.Registered registered => Track(registered.Id, current, action, label),
            HotkeyRegistrationResult.AlreadyUsed => new GlobalShortcutState(action, label, GlobalShortcutStatus.AlreadyUsed,
                WindowsShortcuts.RegistrationFailure(label, ErrorHotkeyAlreadyRegistered)),
            HotkeyRegistrationResult.Refused refused => new GlobalShortcutState(action, label, GlobalShortcutStatus.Refused,
                WindowsShortcuts.RegistrationFailure(label, refused.Win32Error)),
            _ => throw new InvalidOperationException("Unknown hotkey registration result."),
        };
    }

    private GlobalShortcutState Track(HotkeyId id, HotkeyId? previous, GlobalShortcutAction action, string label)
    {
        if (previous is { } replaced && replaced != id)
        {
            _actions.Remove(replaced);
        }
        _actions[id] = action;
        _ids[action] = id;
        return new GlobalShortcutState(action, label, GlobalShortcutStatus.Registered, null);
    }
}
