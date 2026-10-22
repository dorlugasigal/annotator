using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Sessions;

/// <summary>A session or power transition that pauses live presentation (WIN-OVR-09).</summary>
public enum SessionTransition
{
    /// <summary>The session was locked.</summary>
    Locked,

    /// <summary>The session was unlocked.</summary>
    Unlocked,

    /// <summary>The person is signing out.</summary>
    SigningOut,

    /// <summary>A console or Remote Desktop connection changed (fast user switching or Remote Desktop).</summary>
    ConnectionChanged,

    /// <summary>The computer is going to sleep.</summary>
    Sleeping,
}

/// <summary>
/// Hears session and power transitions for the app (WIN-OVR-09): lock and unlock, signing out, console and Remote Desktop
/// connections, and sleep. A hidden top-level window of the creating thread receives them, so that thread must run a
/// message loop. Between notifications it does nothing: no timers, polling or hooks (WIN-SYS-07).
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    private readonly Host _host;
    private bool _registered;

    private SessionWatcher(Action<SessionTransition> changed)
    {
        _host = new Host(changed);
        try
        {
            if (!PInvoke.WTSRegisterSessionNotification(_host.Handle, PInvoke.NOTIFY_FOR_THIS_SESSION))
            {
                throw NativeCallException.FromLastError("WTSRegisterSessionNotification");
            }
            _registered = true;
        }
        catch
        {
            _host.Destroy();
            throw;
        }
    }

    /// <summary>Starts watching on the calling thread; <paramref name="changed"/> runs there, inside the window's message.</summary>
    /// <exception cref="NativeCallException">The window or the session notifications could not be created.</exception>
    public static SessionWatcher Start(Action<SessionTransition> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return new SessionWatcher(changed);
    }

    /// <summary>The transition a window message announces, or null.</summary>
    internal static SessionTransition? Classify(uint message, nuint wParam) => message switch
    {
        PInvoke.WM_WTSSESSION_CHANGE => (uint)wParam switch
        {
            PInvoke.WTS_SESSION_LOCK => SessionTransition.Locked,
            PInvoke.WTS_SESSION_UNLOCK => SessionTransition.Unlocked,
            PInvoke.WTS_SESSION_LOGOFF => SessionTransition.SigningOut,
            PInvoke.WTS_CONSOLE_CONNECT or PInvoke.WTS_CONSOLE_DISCONNECT or PInvoke.WTS_REMOTE_CONNECT or PInvoke.WTS_REMOTE_DISCONNECT =>
                SessionTransition.ConnectionChanged,
            _ => null,
        },
        PInvoke.WM_POWERBROADCAST when wParam == PInvoke.PBT_APMSUSPEND => SessionTransition.Sleeping,
        _ => null,
    };

    /// <summary>For tests: delivers a window message as Windows would.</summary>
    internal void Deliver(uint message, nuint wParam) => PInvoke.SendMessage(_host.Handle, message, new WPARAM(wParam), default);

    public void Dispose()
    {
        if (_registered)
        {
            _registered = false;
            PInvoke.WTSUnRegisterSessionNotification(_host.Handle);
        }
        _host.Destroy();
    }

    /// <summary>The hidden window; top-level, so it also receives the power broadcast. It is never shown.</summary>
    private sealed class Host : NativeWindow
    {
        private readonly Action<SessionTransition> _changed;

        public Host(Action<SessionTransition> changed)
        {
            _changed = changed;
            CreateHandle("Annotator.Platform.SessionWatcher", WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                         WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, HWND.Null, "CreateWindowEx (session watcher)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (session watcher)");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            if (Classify(message, wParam.Value) is { } transition)
            {
                _changed(transition);
                return message == PInvoke.WM_POWERBROADCAST ? (LRESULT)1 : default;
            }
            return base.WndProc(message, wParam, lParam);
        }
    }
}
