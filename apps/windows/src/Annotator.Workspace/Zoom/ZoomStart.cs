namespace Annotator.Workspace.Zoom;

/// <summary>The phases of a live zoom request (Mac <c>ZoomStartState</c>).</summary>
public enum ZoomStartPhase
{
    Idle,

    /// <summary>
    /// Waiting for screen access: a packaged build whose explanation asks Windows (WIN-CAP-04). A development build needs none,
    /// so this never happens there.
    /// </summary>
    ScreenPermission,

    /// <summary>Waiting for gesture access. Windows hooks need no permission (WIN-INP-08), so this never happens there.</summary>
    GesturePermission,

    /// <summary>Zoom was requested and waits for its first zoomed frame.</summary>
    Starting,

    /// <summary>The first zoomed frame was shown.</summary>
    Active,

    /// <summary>The request failed; <see cref="ZoomStartState.Message"/> says why and what to do.</summary>
    Failed,
}

/// <summary>Mac <c>ZoomStartState</c>: the phase, and the message of <see cref="ZoomStartPhase.Failed"/>.</summary>
public readonly record struct ZoomStartState(ZoomStartPhase Phase, string? Message = null)
{
    public static ZoomStartState Idle => default;

    public static ZoomStartState Starting => new(ZoomStartPhase.Starting);

    public static ZoomStartState Active => new(ZoomStartPhase.Active);

    public static ZoomStartState Failed(string message) => new(ZoomStartPhase.Failed, message);

    /// <summary>Waiting for a permission (Mac isWaiting).</summary>
    public bool IsWaiting => Phase is ZoomStartPhase.ScreenPermission or ZoomStartPhase.GesturePermission;
}

/// <summary>Runs <paramref name="callback"/> on the workspace's thread after <paramref name="delay"/>; disposing cancels it.</summary>
public delegate IDisposable WorkspaceTimer(TimeSpan delay, Action callback);

/// <summary>
/// Port of the Mac <c>ZoomStartController</c>: a zoom request waits for its permissions, starts, and becomes active when the
/// first zoomed frame is shown. Waiting and starting are bounded: 120 checks 750 ms apart while a permission is missing, and
/// 10 seconds for the first frame. Cancellation prevents a later start. On Windows only a packaged build waits, for screen
/// access (WIN-CAP-04); the hooks need no permission (WIN-INP-08).
/// </summary>
public sealed class ZoomStartController
{
    /// <summary>Mac: the message after 120 permission checks.</summary>
    public const string PermissionTimeoutMessage =
        "Zoom is waiting for permission. Open Settings > Permissions to check access for this copy of Annotator. " +
        "Then try your Live zoom shortcut from Settings > Toolbar & Shortcuts.";

    /// <summary>Mac: the message when no zoomed frame appears within 10 seconds.</summary>
    public const string StartupTimeoutMessage =
        "Screen zoom did not appear. Stop zoom and try again. If you just allowed screen access, you may need to reopen " +
        "Annotator. Stop screen sharing and save your board first: quitting removes covers.";

    private static readonly TimeSpan s_checkInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan s_startupDeadline = TimeSpan.FromSeconds(10);
    private const int Checks = 120;

    private readonly Func<bool> _screenGranted;
    private readonly Func<bool> _gesturesGranted;
    private readonly Action _requestScreen;
    private readonly Action _requestGestures;
    private readonly Action _start;
    private readonly Action<ZoomStartState> _changed;
    private readonly WorkspaceTimer? _timer;
    private readonly bool _requiresGesturesForStart;
    private IDisposable? _waiting;
    private IDisposable? _startupDeadline;
    private int _checks;

    /// <param name="timer">Schedules the bounded checks and the startup deadline; null turns automatic checks off (Mac automaticChecks false).</param>
    public ZoomStartController(Func<bool> screenGranted, Func<bool> gesturesGranted, Action requestScreen, Action requestGestures,
                               Action start, Action<ZoomStartState> changed, WorkspaceTimer? timer, bool requiresGesturesForStart = true)
    {
        _screenGranted = screenGranted;
        _gesturesGranted = gesturesGranted;
        _requestScreen = requestScreen;
        _requestGestures = requestGestures;
        _start = start;
        _changed = changed;
        _timer = timer;
        _requiresGesturesForStart = requiresGesturesForStart;
    }

    public ZoomStartState State { get; private set; }

    public bool IsWaiting => State.IsWaiting;

    /// <summary>Mac request(): asks for what is missing, or starts; a request while starting or active does nothing.</summary>
    public void Request()
    {
        if (State.Phase is ZoomStartPhase.Starting or ZoomStartPhase.Active)
        {
            return;
        }
        Advance();
        if (_timer is null || !IsWaiting || _waiting is not null)
        {
            return;
        }
        _checks = 0;
        _waiting = _timer(s_checkInterval, Check);
    }

    /// <summary>Mac refresh(): checks the permissions again while waiting for them.</summary>
    public void Refresh()
    {
        if (IsWaiting)
        {
            Advance();
        }
    }

    /// <summary>Mac presented(): the first zoomed frame is on screen.</summary>
    public void Presented()
    {
        if (State.Phase == ZoomStartPhase.Starting)
        {
            CancelStartupDeadline();
            Set(ZoomStartState.Active);
        }
    }

    public void Fail(string message)
    {
        CancelWaiting();
        CancelStartupDeadline();
        Set(ZoomStartState.Failed(message));
    }

    public void Cancel()
    {
        CancelWaiting();
        CancelStartupDeadline();
        Set(ZoomStartState.Idle);
    }

    private void Check()
    {
        _waiting = null;
        if (!IsWaiting)
        {
            return;
        }
        Advance();
        if (!IsWaiting)
        {
            return;
        }
        if (++_checks < Checks && _timer is not null)
        {
            _waiting = _timer(s_checkInterval, Check);
            return;
        }
        Fail(PermissionTimeoutMessage);
    }

    private void Advance()
    {
        if (!_screenGranted())
        {
            if (State.Phase != ZoomStartPhase.ScreenPermission)
            {
                Set(new ZoomStartState(ZoomStartPhase.ScreenPermission));
                _requestScreen();
            }
        }
        else if (_requiresGesturesForStart && !_gesturesGranted())
        {
            if (State.Phase != ZoomStartPhase.GesturePermission)
            {
                Set(new ZoomStartState(ZoomStartPhase.GesturePermission));
                _requestGestures();
            }
        }
        else
        {
            CancelWaiting();
            Set(ZoomStartState.Starting);
            if (_timer is not null)
            {
                CancelStartupDeadline();
                _startupDeadline = _timer(s_startupDeadline, () =>
                {
                    _startupDeadline = null;
                    if (State.Phase == ZoomStartPhase.Starting)
                    {
                        Fail(StartupTimeoutMessage);
                    }
                });
            }
            _start();
        }
    }

    private void CancelWaiting()
    {
        _waiting?.Dispose();
        _waiting = null;
    }

    private void CancelStartupDeadline()
    {
        _startupDeadline?.Dispose();
        _startupDeadline = null;
    }

    private void Set(ZoomStartState state)
    {
        State = state;
        _changed(state);
    }
}
