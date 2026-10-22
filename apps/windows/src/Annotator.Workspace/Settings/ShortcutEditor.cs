using Annotator.Core;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.Settings;

/// <summary>What a key chip or global shortcut button records (Mac ShortcutRecordingTarget).</summary>
public readonly record struct RecordingTarget
{
    private RecordingTarget(ShortcutCommand? command, GlobalShortcutKind? kind)
    {
        Command = command;
        Kind = kind;
    }

    /// <summary>The tool or button of a local key, or null.</summary>
    public ShortcutCommand? Command { get; }

    /// <summary>The global shortcut, or null.</summary>
    public GlobalShortcutKind? Kind { get; }

    public bool IsGlobal => Kind is not null;

    public string Title => Command is { } command ? command.Label : Kind!.Value.Title;

    public static RecordingTarget Local(ShortcutCommand command) => new(command, null);

    public static RecordingTarget Global(GlobalShortcutKind kind) => new(null, kind);
}

/// <summary>The toast's symbol color (Mac SettingsToast.tint).</summary>
public enum ToastTint
{
    /// <summary>Color.green: a change worked.</summary>
    Success,

    /// <summary>The accent: a prompt or a note.</summary>
    Accent,

    /// <summary>Color.secondary: a key was removed.</summary>
    Secondary,

    /// <summary>Color.orange: a key was refused.</summary>
    Warning,
}

/// <summary>
/// Port of SettingsToast: the message at the bottom of the Toolbar &amp; Shortcuts page, with an optional Undo that puts
/// back the keys of <see cref="Undo"/>. A persistent toast stays while a key is recorded; others go after
/// <see cref="Lifetime"/>.
/// </summary>
public sealed record SettingsToast(string Message, string Symbol = "checkmark.circle.fill", ToastTint Tint = ToastTint.Success)
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>The profile whose tool and button keys Undo puts back, or null for no Undo.</summary>
    public WorkspacePreferences? Undo { get; init; }

    /// <summary>Says exactly what Undo puts back, such as "Give 2 back to Rectangle".</summary>
    public string UndoTitle { get; init; } = "Undo";

    /// <summary>Stays, with a Cancel link, while a key is recorded.</summary>
    public bool Persistent { get; init; }

    /// <summary>How long the toast shows: 6 seconds with Undo, 3.2 seconds otherwise, or until it is replaced.</summary>
    public TimeSpan? Lifetime => Persistent ? null : TimeSpan.FromSeconds(Undo is null ? 3.2 : 6);
}

/// <summary>
/// A key press the page records: the virtual key, the PC set-1 scan code of the physical key (0xE0xx for extended keys),
/// the modifiers held, and the character the keyboard layout gives with Shift alone, like the Mac's
/// charactersIgnoringModifiers (null when the key types nothing).
/// </summary>
public readonly record struct RecordedKey(int VirtualKey, uint ScanCode, ChordModifiers Modifiers, string? Character);

/// <summary>Asks the installed keyboard layouts whether a chord types a character (Ctrl+Alt is AltGr on many layouts).</summary>
public interface IKeyboardLayouts
{
    /// <summary>The character <paramref name="chord"/> types in an installed layout, or null when it types nothing.</summary>
    string? TypedCharacter(ShortcutChord chord);
}

/// <summary>
/// The recording logic of ToolbarShortcutSettings.swift: one key chip or global shortcut button listens at a time, the next
/// key press becomes its shortcut, and refusals explain themselves in place. It keeps the toast and the shake state the
/// page draws, and changes keys only through the workspace model. Use it on the model's thread.
/// </summary>
public sealed class ShortcutEditor
{
    private const int Escape = 0x1B;
    private const int Back = 0x08;
    private const int Delete = 0x2E;
    private readonly WorkspaceModel _model;
    private readonly IKeyboardLayouts _layouts;

    public ShortcutEditor(WorkspaceModel model, IKeyboardLayouts layouts)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(layouts);
        _model = model;
        _layouts = layouts;
    }

    /// <summary>Raised after the recording target, the live modifiers, the toast or the shake changed.</summary>
    public event Action? Changed;

    /// <summary>Raised when a key was refused, so the page shakes the target that is still listening.</summary>
    public event Action<RecordingTarget>? Refused;

    /// <summary>The chip or button that listens for the next key, or null.</summary>
    public RecordingTarget? Recording { get; private set; }

    /// <summary>The modifiers held while a global shortcut is recorded.</summary>
    public ChordModifiers LiveModifiers { get; private set; }

    /// <summary>The target of the last refusal (Mac shakeTarget).</summary>
    public RecordingTarget? ShakeTarget { get; private set; }

    /// <summary>Counts refusals; each one plays the shake once (Mac shakes).</summary>
    public int Shakes { get; private set; }

    public SettingsToast? Toast { get; private set; }

    /// <summary>The global shortcut button's words while it listens: "Press keys", or the held modifiers and "+ key".</summary>
    public string LiveModifierText => LiveModifiers == ChordModifiers.None ? "Press keys" : $"{ModifierNames(LiveModifiers)} + key";

    /// <summary>Mac toggleRecording: starts listening for <paramref name="target"/>, or stops when it already listens.</summary>
    public void Toggle(RecordingTarget target)
    {
        if (Recording == target)
        {
            Stop();
            return;
        }
        LiveModifiers = ChordModifiers.None;
        Recording = target;
        Show(target.Command is { } command
            ? new SettingsToast($"Press a key for {command.Label}. Shift with a letter works too. Delete removes the key.", "keyboard", ToastTint.Accent)
            {
                Persistent = true,
            }
            : new SettingsToast($"Press Ctrl, Alt or Win with a letter or number for {target.Kind!.Value.Title}.", "keyboard", ToastTint.Accent)
            {
                Persistent = true,
            });
    }

    /// <summary>Mac stopRecording: nothing listens any more, and the recording prompt goes.</summary>
    public void Stop()
    {
        bool changed = Recording is not null || LiveModifiers != ChordModifiers.None || Toast?.Persistent == true;
        Recording = null;
        LiveModifiers = ChordModifiers.None;
        if (Toast?.Persistent == true)
        {
            Toast = null;
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The page went away (Mac onDisappear): recording stops and the toast goes.</summary>
    public void Reset()
    {
        Recording = null;
        LiveModifiers = ChordModifiers.None;
        Toast = null;
        Changed?.Invoke();
    }

    /// <summary>Mac editor.say: a note with the info symbol.</summary>
    public void Say(string message) => Show(new SettingsToast(message, "info.circle.fill", ToastTint.Accent));

    /// <summary>Removes the toast when its time is up, unless another one replaced it.</summary>
    public void Expire(Guid id)
    {
        if (Toast?.Id == id)
        {
            Toast = null;
            Changed?.Invoke();
        }
    }

    /// <summary>The toast's Undo: puts the keys back as they were.</summary>
    public void Undo()
    {
        if (Toast?.Undo is not { } snapshot)
        {
            return;
        }
        _model.RestoreLocalShortcuts(snapshot);
        Show(new SettingsToast("Done. The keys are back as they were."));
    }

    /// <summary>Mac flagsChanged while recording: the modifiers held now.</summary>
    public void UpdateModifiers(ChordModifiers modifiers)
    {
        if (Recording is null || modifiers == LiveModifiers)
        {
            return;
        }
        LiveModifiers = modifiers;
        Changed?.Invoke();
    }

    /// <summary>
    /// Mac handleKey: the key press while a chip or button listens. Returns false when nothing listens. Modifier keys alone
    /// only change <see cref="LiveModifiers"/>.
    /// </summary>
    public bool HandleKey(RecordedKey key)
    {
        if (Recording is not { } target)
        {
            return false;
        }
        if (key.VirtualKey == Escape)
        {
            Stop();
            return true;
        }
        if (IsModifier(key.VirtualKey))
        {
            UpdateModifiers(key.Modifiers);
            return true;
        }
        if (target.Command is { } command)
        {
            RecordLocal(key, command, target);
        }
        else
        {
            RecordGlobal(key, target.Kind!.Value, target);
        }
        return true;
    }

    /// <summary>A global shortcut pressed while a chip or button listens: Windows delivers it as the chord, not as keys.</summary>
    public bool HandleHotkey(GlobalShortcutAction action)
    {
        if (Recording is null || WindowsShortcuts.Chord(action, _model.Snapshot.Preferences) is not { } chord)
        {
            return false;
        }
        ActivationShortcut shortcut = WindowsShortcuts.Shortcut(action, _model.Snapshot.Preferences);
        return HandleKey(new RecordedKey((int)(chord.VirtualKey ?? 0), chord.ScanCode, chord.Modifiers, shortcut.Character));
    }

    private void RecordLocal(RecordedKey key, ShortcutCommand command, RecordingTarget target)
    {
        WorkspacePreferences snapshot = _model.Snapshot.Preferences;
        if (key.VirtualKey is Back or Delete)
        {
            Stop();
            if (_model.BindShortcut(null, command) is ShortcutBindingResult.Failed failed)
            {
                Fail(failed.Message, target);
                return;
            }
            LocalShortcut? previous = snapshot.ShortcutFor(command);
            Show(new SettingsToast($"{command.Label} has no key now.", "minus.circle.fill", ToastTint.Secondary)
            {
                Undo = previous is null ? null : snapshot,
                UndoTitle = previous is { } old ? $"Give {WindowsShortcuts.Label(old)} back to {command.Label}" : "Undo",
            });
            return;
        }
        if ((key.Modifiers & (ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Win)) != 0)
        {
            Fail("Keys for tools and buttons work without Ctrl or Alt. Press a letter or number.", target);
            return;
        }
        string character = (key.Character ?? string.Empty).ToLowerInvariant();
        // Some layouts (AZERTY, Czech) need Shift to type a digit; the layout decides, like canvas routing does.
        bool shift = key.Modifiers.HasFlag(ChordModifiers.Shift);
        if ((LocalShortcut.Create(character, shift) ?? (shift ? LocalShortcut.Create(character) : null)) is not { } shortcut)
        {
            Fail("Press a letter or a number. Shift works with letters only.", target);
            return;
        }
        if (shortcut.IsInkColor)
        {
            Fail($"{shortcut.Key.ToUpperInvariant()} changes the ink color. Choose another key.", target);
            return;
        }
        Stop();
        string label = WindowsShortcuts.Label(shortcut);
        switch (_model.BindShortcut(shortcut, command))
        {
            case ShortcutBindingResult.Assigned { Displaced: { } displaced }:
                Show(new SettingsToast($"{label} is now the key for {command.Label}. {displaced.Label} has no key now.")
                {
                    Undo = snapshot,
                    UndoTitle = $"Give {label} back to {displaced.Label}",
                });
                break;
            case ShortcutBindingResult.Assigned:
                Show(new SettingsToast($"{label} is now the key for {command.Label}."));
                break;
            case ShortcutBindingResult.Failed failed:
                Fail(failed.Message, target);
                break;
        }
    }

    private void RecordGlobal(RecordedKey key, GlobalShortcutKind kind, RecordingTarget target)
    {
        WorkspacePreferences preferences = _model.Snapshot.Preferences;
        uint? code = MacKeyCode(key);
        string character = (code is { } mac ? WindowsShortcuts.SettingsDigit(mac) : null) ?? (key.Character ?? string.Empty).ToLowerInvariant();
        var shortcut = new ActivationShortcut(code ?? 0, character, control: key.Modifiers.HasFlag(ChordModifiers.Ctrl),
            option: key.Modifiers.HasFlag(ChordModifiers.Alt), command: key.Modifiers.HasFlag(ChordModifiers.Win),
            shift: key.Modifiers.HasFlag(ChordModifiers.Shift));
        try
        {
            shortcut.Validate();
        }
        catch (CanvasError)
        {
            Fail("Hold Ctrl, Alt or Win and press a letter or number.", target);
            return;
        }
        // Profiles store the Mac key code of the physical key, so a key outside the Mac's key table cannot be stored.
        if (code is null || WindowsShortcuts.Chord(shortcut) is not { } chord)
        {
            Fail("Hold Ctrl, Alt or Win and press a letter from A to Z or a number.", target);
            return;
        }
        string label = chord.Label;
        foreach (GlobalShortcutKind other in GlobalShortcutKind.AllCases)
        {
            if (other != kind && other.Value(preferences).UsesSameKeys(shortcut))
            {
                Fail($"{label} already does {other.Title}. Choose another combination.", target);
                return;
            }
        }
        if (ReservedShortcuts.IsReserved(chord))
        {
            Fail($"Windows uses {label} for its own shortcut. Choose another combination.", target);
            return;
        }
        if (_layouts.TypedCharacter(chord) is not null)
        {
            Fail($"{label} types a character with a keyboard layout on this computer. Choose another combination.", target);
            return;
        }
        string before = WindowsShortcuts.Label(kind.Value(preferences));
        Stop();
        switch (_model.SetGlobalShortcut(kind.Action, shortcut))
        {
            case GlobalShortcutChange.Changed:
                Show(new SettingsToast($"{label} now does {kind.Title} in any app."));
                break;
            case GlobalShortcutChange.Invalid invalid:
                Fail(invalid.Message, target);
                break;
            case GlobalShortcutChange.NotRegistered:
                Fail($"{label} didn't work. Another app may use it. {kind.Title} still uses {before}.", target);
                break;
            case GlobalShortcutChange.Unsupported:
                Fail($"{kind.Title} is not ready on Windows yet, so its shortcut can't change.", target);
                break;
        }
    }

    /// <summary>Mac fail: shakes the target and shows the reason; the toast stays while the target still listens.</summary>
    private void Fail(string message, RecordingTarget target)
    {
        ShakeTarget = target;
        Shakes++;
        Refused?.Invoke(target);
        Show(new SettingsToast(message, "exclamationmark.triangle.fill", ToastTint.Warning) { Persistent = Recording is not null });
    }

    /// <summary>Mac show: replaces the toast; the page announces it to screen readers and hides it after its lifetime.</summary>
    private void Show(SettingsToast toast)
    {
        Toast = toast;
        Changed?.Invoke();
    }

    /// <summary>The Mac key code of the pressed key: keypad digits by their virtual key, other keys by their physical position.</summary>
    public static uint? MacKeyCode(RecordedKey key)
    {
        foreach (MacKey entry in WindowsShortcuts.KeyTable)
        {
            if (entry.VirtualKey is { } virtualKey && virtualKey == key.VirtualKey && virtualKey is >= 0x60 and <= 0x69)
            {
                return entry.KeyCode;
            }
        }
        return key.VirtualKey is >= 0x60 and <= 0x69 ? null : WindowsShortcuts.MacKeyCode(key.ScanCode);
    }

    /// <summary>
    /// Shift, Ctrl, Alt and the Windows keys (left, right or either), and the lock keys: the Mac sees these as modifier
    /// changes, never as key presses.
    /// </summary>
    public static bool IsModifier(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or 0x90 or 0x91 or (>= 0xA0 and <= 0xA5);

    /// <summary>Held modifiers as Windows names them, in keycap order: "Ctrl + Alt + Shift + Win".</summary>
    public static string ModifierNames(ChordModifiers modifiers)
    {
        var names = new List<string>(4);
        if (modifiers.HasFlag(ChordModifiers.Ctrl))
        {
            names.Add("Ctrl");
        }
        if (modifiers.HasFlag(ChordModifiers.Alt))
        {
            names.Add("Alt");
        }
        if (modifiers.HasFlag(ChordModifiers.Shift))
        {
            names.Add("Shift");
        }
        if (modifiers.HasFlag(ChordModifiers.Win))
        {
            names.Add("Win");
        }
        return string.Join(" + ", names);
    }
}
