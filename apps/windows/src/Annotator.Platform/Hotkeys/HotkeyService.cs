using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Hotkeys;

/// <summary>Windows modifier keys of a global shortcut; the values match RegisterHotKey's MOD_ flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A global shortcut: Windows modifiers plus a virtual-key code. The macOS profile mapping
/// (control to Ctrl, option to Alt, command to Win, WIN-INP-04) builds these later.
/// </summary>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, int VirtualKey);

/// <summary>Identifies one registration of a <see cref="HotkeyService"/>.</summary>
public readonly record struct HotkeyId(int Value);

/// <summary>Why a chord was refused.</summary>
public enum HotkeyRefusal
{
    /// <summary>The virtual-key code is outside 1-254 or is a mouse button.</summary>
    InvalidKey,

    /// <summary>The key is itself a modifier, such as Shift or the Windows key.</summary>
    ModifierAsKey,

    /// <summary>The modifiers include unknown flags.</summary>
    InvalidModifiers,

    /// <summary>Windows rejected the registration; see <see cref="HotkeyRegistrationResult.Refused.Win32Error"/>.</summary>
    RejectedByWindows,
}

/// <summary>The typed outcome of <see cref="HotkeyService.Register"/>.</summary>
public abstract record HotkeyRegistrationResult
{
    private HotkeyRegistrationResult()
    {
    }

    /// <summary>The shortcut is active and raises <see cref="HotkeyService.Pressed"/>.</summary>
    public sealed record Registered(HotkeyChord Chord, HotkeyId Id) : HotkeyRegistrationResult;

    /// <summary>
    /// Another registration owns the chord: usually another app (for example ZoomIt), or
    /// another registration in this process. The action stays without this shortcut (WIN-INP-05).
    /// </summary>
    public sealed record AlreadyUsed(HotkeyChord Chord) : HotkeyRegistrationResult;

    /// <summary>The chord cannot be a shortcut. <paramref name="Win32Error"/> is 0 unless Windows refused it.</summary>
    public sealed record Refused(HotkeyChord Chord, HotkeyRefusal Reason, int Win32Error) : HotkeyRegistrationResult;
}

public sealed class HotkeyPressedEventArgs(HotkeyId id, HotkeyChord chord) : EventArgs
{
    public HotkeyId Id { get; } = id;

    public HotkeyChord Chord { get; } = chord;
}

/// <summary>
/// Global shortcuts through RegisterHotKey on a message-only window (WIN-INP-04). Create it on
/// a thread that runs a message loop and use it only there; <see cref="Pressed"/> is raised on
/// that thread. Holding a chord fires once (MOD_NOREPEAT). Disposing unregisters every chord.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private const int MaxId = 0xBFFF;

    private readonly Dictionary<int, HotkeyChord> _registered = [];
    private readonly HotkeyWindow _window;
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService()
    {
        _window = new HotkeyWindow(this);
    }

    /// <summary>Raised on the owning thread when a registered chord is pressed.</summary>
    public event EventHandler<HotkeyPressedEventArgs>? Pressed;

    /// <summary>The active registrations.</summary>
    public IReadOnlyDictionary<HotkeyId, HotkeyChord> Registrations
    {
        get
        {
            VerifyAccess();
            return _registered.ToDictionary(pair => new HotkeyId(pair.Key), pair => pair.Value);
        }
    }

    internal HWND Handle => _window.Handle;

    /// <summary>Registers a global shortcut. Failures are typed results, never exceptions.</summary>
    public HotkeyRegistrationResult Register(HotkeyChord chord)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Validate(chord) is HotkeyRefusal refusal)
        {
            return new HotkeyRegistrationResult.Refused(chord, refusal, 0);
        }
        if (_nextId > MaxId)
        {
            throw new InvalidOperationException("This hotkey service has used every registration id.");
        }

        int id = _nextId;
        var modifiers = (HOT_KEY_MODIFIERS)(uint)chord.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (!PInvoke.RegisterHotKey(_window.Handle, id, modifiers, (uint)chord.VirtualKey))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            return error == ERROR_HOTKEY_ALREADY_REGISTERED
                ? new HotkeyRegistrationResult.AlreadyUsed(chord)
                : new HotkeyRegistrationResult.Refused(chord, HotkeyRefusal.RejectedByWindows, error);
        }
        _nextId++;
        _registered[id] = chord;
        return new HotkeyRegistrationResult.Registered(chord, new HotkeyId(id));
    }

    /// <summary>
    /// Registers <paramref name="chord"/> in place of <paramref name="current"/> (WIN-INP-04). The new chord is registered
    /// first and <paramref name="current"/> is released only when that worked, so a refused or taken chord leaves the
    /// current shortcut working. A chord equal to the current one keeps that registration.
    /// </summary>
    public HotkeyRegistrationResult Replace(HotkeyId? current, HotkeyChord chord)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (current is { } id && _registered.TryGetValue(id.Value, out HotkeyChord existing) && existing == chord)
        {
            return new HotkeyRegistrationResult.Registered(chord, id);
        }
        HotkeyRegistrationResult result = Register(chord);
        if (result is HotkeyRegistrationResult.Registered && current is { } previous)
        {
            Unregister(previous);
        }
        return result;
    }

    /// <summary>Removes a registration. Returns false if this service does not hold <paramref name="id"/>.</summary>
    public bool Unregister(HotkeyId id)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_registered.Remove(id.Value))
        {
            return false;
        }
        if (!PInvoke.UnregisterHotKey(_window.Handle, id.Value))
        {
            throw NativeCallException.FromLastError($"UnregisterHotKey({id.Value})");
        }
        return true;
    }

    /// <summary>Unregisters every chord and destroys the window.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        VerifyAccess();
        _disposed = true;
        List<Exception>? failures = null;
        foreach (int id in _registered.Keys)
        {
            if (!PInvoke.UnregisterHotKey(_window.Handle, id))
            {
                (failures ??= []).Add(NativeCallException.FromLastError($"UnregisterHotKey({id})"));
            }
        }
        _registered.Clear();
        try
        {
            _window.Destroy();
        }
        catch (NativeCallException failure)
        {
            (failures ??= []).Add(failure);
        }
        if (failures is not null)
        {
            throw new AggregateException("The hotkey service did not shut down cleanly.", failures);
        }
    }

    internal static HotkeyRefusal? Validate(HotkeyChord chord)
    {
        const HotkeyModifiers known = HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Windows;
        if ((chord.Modifiers & ~known) != 0)
        {
            return HotkeyRefusal.InvalidModifiers;
        }
        if (chord.VirtualKey is < 0x01 or > 0xFE)
        {
            return HotkeyRefusal.InvalidKey;
        }
        return (VIRTUAL_KEY)chord.VirtualKey switch
        {
            VIRTUAL_KEY.VK_LBUTTON or VIRTUAL_KEY.VK_RBUTTON or VIRTUAL_KEY.VK_MBUTTON or
            VIRTUAL_KEY.VK_XBUTTON1 or VIRTUAL_KEY.VK_XBUTTON2 => HotkeyRefusal.InvalidKey,
            VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT or
            VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL or
            VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU or
            VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN => HotkeyRefusal.ModifierAsKey,
            _ => null,
        };
    }

    private void OnHotkey(int id)
    {
        // Negative ids are system hotkeys (IDHOT_SNAPWINDOW, IDHOT_SNAPDESKTOP); ignore them.
        if (!_disposed && _registered.TryGetValue(id, out HotkeyChord chord))
        {
            Pressed?.Invoke(this, new HotkeyPressedEventArgs(new HotkeyId(id), chord));
        }
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _window.OwnerThreadId)
        {
            throw new InvalidOperationException("HotkeyService must be used on the thread that created it.");
        }
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        private readonly HotkeyService _owner;

        public HotkeyWindow(HotkeyService owner)
        {
            _owner = owner;
            CreateHandle("Annotator.Platform.Hotkeys", 0, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, "CreateWindowEx (hotkey message window)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (hotkey message window)");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            if (message == PInvoke.WM_HOTKEY)
            {
                _owner.OnHotkey((int)(nint)wParam.Value);
                return default;
            }
            return base.WndProc(message, wParam, lParam);
        }
    }
}
