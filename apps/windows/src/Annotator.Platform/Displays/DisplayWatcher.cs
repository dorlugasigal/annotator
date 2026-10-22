using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Displays;

/// <summary>What made a <see cref="DisplayWatcher"/> look at the displays again.</summary>
[Flags]
public enum DisplayChangeReasons
{
    None = 0,

    /// <summary>WM_DISPLAYCHANGE: a monitor was added or removed, or a resolution changed.</summary>
    DisplaySettings = 1,

    /// <summary>WM_DPICHANGED: a scale factor changed.</summary>
    Dpi = 2,

    /// <summary>WM_SETTINGCHANGE with SPI_SETWORKAREA: the taskbar or an app bar moved or resized.</summary>
    WorkArea = 4,

    /// <summary>The app asked with <see cref="DisplayWatcher.Refresh"/>.</summary>
    Requested = 8,
}

public sealed class DisplaysChangedEventArgs(DisplayChangeReasons reasons, IReadOnlyList<DisplayInfo> previous, IReadOnlyList<DisplayInfo> current) : EventArgs
{
    public DisplayChangeReasons Reasons { get; } = reasons;

    public IReadOnlyList<DisplayInfo> Previous { get; } = previous;

    public IReadOnlyList<DisplayInfo> Current { get; } = current;
}

public sealed class DisplayRefreshFailedEventArgs(DisplayChangeReasons reasons, NativeCallException error) : EventArgs
{
    public DisplayChangeReasons Reasons { get; } = reasons;

    public NativeCallException Error { get; } = error;
}

/// <summary>
/// Watches for display, scale and work-area changes on the thread that creates it, which must
/// run a message loop. It listens on a hidden top-level window that is never shown: Windows
/// broadcasts WM_DISPLAYCHANGE and WM_SETTINGCHANGE only to top-level windows, so a
/// message-only window would miss them. Messages of one burst are coalesced into one check, and
/// <see cref="Changed"/> fires only when the display list really differs. No timers or polling.
/// </summary>
public sealed class DisplayWatcher : IDisposable
{
    private readonly Func<IReadOnlyList<DisplayInfo>> _source;
    private readonly ListenerWindow _window;
    private IReadOnlyList<DisplayInfo> _displays;
    private DisplayChangeReasons _pending;
    private bool _checkPosted;
    private bool _disposed;

    public DisplayWatcher()
        : this(DisplayEnumerator.GetDisplays)
    {
    }

    /// <summary>Uses <paramref name="source"/> instead of <see cref="DisplayEnumerator.GetDisplays"/>, for tests.</summary>
    public DisplayWatcher(Func<IReadOnlyList<DisplayInfo>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _displays = source();
        _window = new ListenerWindow(this);
    }

    /// <summary>Raised on the owning thread after the display list changed.</summary>
    public event EventHandler<DisplaysChangedEventArgs>? Changed;

    /// <summary>
    /// Raised on the owning thread when the displays could not be read, for example while a
    /// monitor disconnects. The reasons stay pending and the next change message checks again.
    /// </summary>
    public event EventHandler<DisplayRefreshFailedEventArgs>? RefreshFailed;

    public IReadOnlyList<DisplayInfo> Displays
    {
        get
        {
            VerifyAccess();
            return _displays;
        }
    }

    internal HWND Handle => _window.Handle;

    /// <summary>Reads the displays now, for example after resuming from sleep. Returns true if they changed.</summary>
    public bool Refresh()
    {
        VerifyAccess();
        DisplayChangeReasons reasons = _pending | DisplayChangeReasons.Requested;
        _pending = DisplayChangeReasons.None;
        return Check(reasons);
    }

    /// <summary>Queues one check for this burst of change messages.</summary>
    internal void Schedule(DisplayChangeReasons reason)
    {
        if (_disposed)
        {
            return;
        }
        _pending |= reason;
        if (_checkPosted)
        {
            return;
        }
        if (PInvoke.PostMessage(_window.Handle, ListenerWindow.CheckMessage, default, default))
        {
            _checkPosted = true;
            return;
        }
        // The queue is full; check now rather than lose the change.
        RunPendingCheck();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        VerifyAccess();
        _disposed = true;
        _window.Destroy();
    }

    private void RunPendingCheck()
    {
        _checkPosted = false;
        DisplayChangeReasons reasons = _pending;
        _pending = DisplayChangeReasons.None;
        if (reasons != DisplayChangeReasons.None && !_disposed)
        {
            Check(reasons);
        }
    }

    private bool Check(DisplayChangeReasons reasons)
    {
        IReadOnlyList<DisplayInfo> current;
        try
        {
            current = _source();
        }
        catch (NativeCallException error)
        {
            _pending |= reasons & ~DisplayChangeReasons.Requested;
            RefreshFailed?.Invoke(this, new DisplayRefreshFailedEventArgs(reasons, error));
            return false;
        }

        if (current.SequenceEqual(_displays))
        {
            return false;
        }
        IReadOnlyList<DisplayInfo> previous = _displays;
        _displays = current;
        Changed?.Invoke(this, new DisplaysChangedEventArgs(reasons, previous, current));
        return true;
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _window.OwnerThreadId)
        {
            throw new InvalidOperationException("DisplayWatcher must be used on the thread that created it.");
        }
    }

    private sealed class ListenerWindow : NativeWindow
    {
        public const uint CheckMessage = PInvoke.WM_APP + 1;
        private readonly DisplayWatcher _owner;

        public ListenerWindow(DisplayWatcher owner)
        {
            _owner = owner;
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle(
                "Annotator.Platform.DisplayWatcher",
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
                WINDOW_STYLE.WS_POPUP,
                0, 0, 0, 0,
                HWND.Null,
                "CreateWindowEx (display watcher)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (display watcher)");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
                case PInvoke.WM_DISPLAYCHANGE:
                    _owner.Schedule(DisplayChangeReasons.DisplaySettings);
                    return default;
                case PInvoke.WM_DPICHANGED:
                    // Never resize to the suggested rectangle: this window is never shown.
                    _owner.Schedule(DisplayChangeReasons.Dpi);
                    return default;
                case PInvoke.WM_SETTINGCHANGE when wParam.Value == (nuint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWORKAREA:
                    _owner.Schedule(DisplayChangeReasons.WorkArea);
                    return default;
                case CheckMessage:
                    _owner.RunPendingCheck();
                    return default;
                default:
                    return base.WndProc(message, wParam, lParam);
            }
        }
    }
}
