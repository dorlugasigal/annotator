using System.Globalization;
using System.Runtime.InteropServices;
using Annotator.App.Motion;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Breaks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.Win32;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Power;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Breaks;

/// <summary>
/// The break timer on Windows (specification 014): runs the shared <see cref="BreakSession"/> with one
/// <see cref="BreakWindow"/> per display, locks with LockWorkStation, keeps the display awake with
/// SetThreadExecutionState, plays the system notification sound and speaks through Narrator. The app host passes it
/// session locks and unlocks, display changes and the break shortcut. Everything runs on the app thread.
/// </summary>
internal sealed partial class BreakTimerHost : IBreakTimerHost
{
    private static readonly TimeSpan CloseDuration = TimeSpan.FromSeconds(0.34);
    private static readonly TimeSpan DeactivationCheck = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherQueue _queue;
    private readonly WindowRegistry _registry;
    private readonly WorkspaceModel _model;
    private readonly List<BreakWindow> _windows = [];
    private HWND _previous;
    private bool _cursorHidden;
    private BreakLockState _lastLock;
    private IDisposable? _deactivation;
    private IDisposable? _closing;

    public BreakTimerHost(DispatcherQueue queue, WindowRegistry registry, WorkspaceModel model)
    {
        _queue = queue;
        _registry = registry;
        _model = model;
        Session = new BreakSession(this, model.PrepareForBreak, model.Report);
        Session.Changed += OnChanged;
    }

    public BreakSession Session { get; }

    public bool IsRunning => Session.IsRunning;

    /// <summary>Starts a break with the saved settings, or with <paramref name="minutes"/> for a quick start; a refusal is reported.</summary>
    public bool Start(long? minutes = null)
    {
        try
        {
            Session.Start(_model.BreakTimerSettings, minutes);
            return true;
        }
        catch (CanvasError error)
        {
            _model.Report(error.ErrorDescription);
        }
        catch (BreakLockException error)
        {
            _model.Report(error.Message);
        }
        catch (Exception error) when (error is COMException or NativeCallException)
        {
            Session.Close();
            _model.Report($"The break timer could not open: {error.Message}");
        }
        return false;
    }

    /// <summary>The break's global shortcut: starts a break, or ends, cancels or locks a running one.</summary>
    public void ShortcutPressed()
    {
        if (Session.IsRunning)
        {
            Session.ShortcutPressed();
        }
        else
        {
            Start();
        }
    }

    /// <summary>Windows locked the session, whoever locked it.</summary>
    public void SessionLocked()
    {
        IsLocked = true;
        Session.SystemLocked();
    }

    /// <summary>Someone signed in again.</summary>
    public void SessionUnlocked()
    {
        IsLocked = false;
        Session.SystemUnlocked();
    }

    public void DisplaysChanged() => Session.DisplaysChanged();

    /// <summary>Quit: the break screen closes at once and the display may sleep again.</summary>
    public void Shutdown()
    {
        Session.Close();
        AllowSleep();
        ShowCursor();
    }

    // IBreakTimerHost

    public DateTimeOffset Now => DateTimeOffset.Now;

    public Point? Pointer => WindowChrome.TryCursor() is { } cursor ? new Point(cursor.X, cursor.Y) : null;

    public double PointerScale => WindowChrome.MonitorAtCursor().Scale;

    /// <summary>False when a policy (DisableLockWorkstation) takes Lock away from this account.</summary>
    public bool CanLock => !PolicyDisablesLock(Registry.CurrentUser) && !PolicyDisablesLock(Registry.LocalMachine);

    public bool IsLocked { get; private set; }

    public bool ScreenReaderRunning
    {
        get
        {
            unsafe
            {
                BOOL running = false;
                return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETSCREENREADER, 0, &running, 0) && running;
            }
        }
    }

    public void Lock()
    {
        if (!PInvoke.LockWorkStation())
        {
            throw new BreakLockException(BreakCopy.LockRefused(Marshal.GetLastPInvokeError()));
        }
    }

    /// <summary>The Windows notification sound, at the computer's volume; the default beep when the sound is missing.</summary>
    public void Chime()
    {
        if (!PInvoke.PlaySound("Notification.Default", HMODULE.Null, SND_FLAGS.SND_ALIAS | SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_NODEFAULT))
        {
            PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);
        }
    }

    public void KeepAwake(bool display) =>
        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                                        (display ? EXECUTION_STATE.ES_DISPLAY_REQUIRED : 0));

    public void AllowSleep() => PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);

    public void Announce(string text)
    {
        if (_windows.FirstOrDefault() is not { } window || !window.View.Countdown.IsLoaded)
        {
            return;
        }
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(window.View.Countdown)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(window.View.Countdown);
        try
        {
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, "break-timer");
        }
        catch (Exception error) when (error is ArgumentException or COMException)
        {
            // An announcement is a courtesy to Narrator; it must never end the break.
        }
    }

    public void Present(BreakTimerSettings settings, bool animated)
    {
        _closing?.Dispose();
        _closing = null;
        if (_windows.Count == 0)
        {
            HWND foreground = PInvoke.GetForegroundWindow();
            _previous = IsOurs(foreground) ? HWND.Null : foreground;
        }
        CloseWindows();
        IReadOnlyList<DisplayInfo> displays = DisplayEnumerator.GetDisplays();
        System.Drawing.Point? cursor = WindowChrome.TryCursor();
        DisplayInfo? pointed = cursor is { } at ? displays.FirstOrDefault(display => display.Bounds.Contains(at.X, at.Y)) : null;
        IEnumerable<DisplayInfo> chosen = settings.AllDisplays ? displays : [pointed ?? displays[0]];
        bool reduce = SystemMotion.ReducesMotion();
        BreakFace face = Session.Face;
        foreach (DisplayInfo display in chosen)
        {
            var view = new BreakFaceView(preview: false, Actions(), reduce);
            var window = new BreakWindow(_registry, display, settings, view, Input());
            view.Apply(face);
            _windows.Add(window);
        }
        BreakWindow key = _windows.FirstOrDefault(window => window.Display == pointed) ?? _windows[0];
        foreach (BreakWindow window in _windows.Where(window => window != key))
        {
            window.Present(activate: false);
            window.View.Enter(animated);
        }
        key.Present(activate: true);
        key.View.Enter(animated);
    }

    public void Dismiss(bool animated, Action closed)
    {
        _deactivation?.Dispose();
        _deactivation = null;
        ShowCursor();
        if (!animated || _windows.Count == 0)
        {
            CloseWindows();
            closed();
            return;
        }
        // The faces already play their exit (BreakFace.Closing); the windows close once it ends.
        _closing?.Dispose();
        _closing = Schedule(CloseDuration, () =>
        {
            _closing = null;
            CloseWindows();
            closed();
        });
    }

    public void ReturnFocus()
    {
        HWND previous = _previous;
        _previous = HWND.Null;
        if (!previous.IsNull && PInvoke.IsWindow(previous))
        {
            PInvoke.SetForegroundWindow(previous);
        }
    }

    public void HideCursorUntilMoved()
    {
        if (!_cursorHidden && _windows.Count > 0)
        {
            _cursorHidden = true;
            PInvoke.ShowCursor(false);
        }
    }

    /// <summary>The time as the account's short time format writes it (Region settings), such as 12:45 or 12:45 PM.</summary>
    public string FormatTime(DateTimeOffset time)
    {
        DateTime local = time.ToLocalTime().DateTime;
        string pattern = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International", "sShortTime", null) as string ?? "HH:mm";
        string designator = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International", local.Hour < 12 ? "s1159" : "s2359", null) as string
            ?? (local.Hour < 12 ? "AM" : "PM");
        string quoted = "'" + designator.Replace("'", string.Empty, StringComparison.Ordinal) + "'";
        pattern = pattern.Replace("tt", quoted, StringComparison.Ordinal);
        if (!pattern.Contains(quoted, StringComparison.Ordinal))
        {
            pattern = pattern.Replace("t", designator.Length > 0 ? "'" + designator[0] + "'" : string.Empty, StringComparison.Ordinal);
        }
        try
        {
            return local.ToString(pattern, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        DispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            action();
        };
        timer.Start();
        return new Timer(timer);
    }

    // Windows and input

    private BreakFaceActions Actions() => new()
    {
        RemoveMinute = () => Session.Adjust(-1),
        TogglePause = Session.TogglePause,
        AddMinute = () => Session.Adjust(1),
        End = () => Session.End(),
        HoverControls = hovered => Session.ControlsHovered = hovered,
    };

    private BreakWindowInput Input() => new()
    {
        KeyDown = OnKeyDown,
        PointerMoved = () =>
        {
            ShowCursor();
            Session.PointerMoved();
        },
        PointerPressed = Session.PointerPressed,
        Activation = OnActivation,
        CloseRequested = () =>
        {
            if (Session.LockState == BreakLockState.Off)
            {
                Session.End();
            }
        },
    };

    private bool OnKeyDown(VirtualKey key, bool command)
    {
        if (key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftMenu or VirtualKey.RightMenu)
        {
            Session.ModifierDown();
            return Session.LockState != BreakLockState.Off;
        }
        BreakKey mapped = key switch
        {
            VirtualKey.Escape => BreakKey.Escape,
            VirtualKey.Enter => BreakKey.Enter,
            VirtualKey.Space => BreakKey.Space,
            VirtualKey.Up => BreakKey.Up,
            VirtualKey.Down => BreakKey.Down,
            VirtualKey.Add or (VirtualKey)187 => BreakKey.Plus,
            VirtualKey.Subtract or (VirtualKey)189 => BreakKey.Minus,
            VirtualKey.Tab => BreakKey.Tab,
            _ => BreakKey.Other,
        };
        return Session.KeyDown(mapped, command);
    }

    /// <summary>
    /// A break window lost activation: once the change settles, another app in front (Alt+Tab, Start, a notification) counts
    /// as touching the computer, as the Mac's didResignActive does. Moving between Annotator's own windows does not.
    /// </summary>
    private void OnActivation(bool active)
    {
        if (active)
        {
            _deactivation?.Dispose();
            _deactivation = null;
            return;
        }
        _deactivation?.Dispose();
        _deactivation = Schedule(DeactivationCheck, () =>
        {
            _deactivation = null;
            if (!IsOurs(PInvoke.GetForegroundWindow()))
            {
                Session.AppDeactivated();
            }
        });
    }

    private void OnChanged()
    {
        BreakFace face = Session.Face;
        foreach (BreakWindow window in _windows)
        {
            window.View.Apply(face);
        }
        BreakLockState now = Session.LockState;
        if (now == BreakLockState.Armed && _lastLock != BreakLockState.Armed && _windows.Count > 0 && !IsOurs(PInvoke.GetForegroundWindow()))
        {
            // Mac: armed while another app is in front locks at once.
            _lastLock = now;
            Session.AppDeactivated();
            return;
        }
        _lastLock = now;
        if (!Session.IsRunning && _windows.Count == 0)
        {
            _lastLock = BreakLockState.Off;
        }
    }

    private void CloseWindows()
    {
        foreach (BreakWindow window in _windows)
        {
            window.Shutdown();
        }
        _windows.Clear();
    }

    private void ShowCursor()
    {
        if (_cursorHidden)
        {
            _cursorHidden = false;
            PInvoke.ShowCursor(true);
        }
    }

    private static bool IsOurs(HWND window)
    {
        if (window.IsNull)
        {
            return false;
        }
        PInvoke.GetWindowThreadProcessId(window, out uint process);
        return process == (uint)Environment.ProcessId;
    }

    private static bool PolicyDisablesLock(RegistryKey root)
    {
        using RegistryKey? key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
        return key?.GetValue("DisableLockWorkstation") is int value && value != 0;
    }

    private sealed class Timer(DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
