using Annotator.Core;

namespace Annotator.Workspace.Breaks;

/// <summary>Mac BreakTimerController.LockState: whether input locks the computer.</summary>
public enum BreakLockState
{
    Off,
    /// <summary>Counting down <see cref="BreakFace.ArmingSeconds"/> before input locks.</summary>
    Arming,
    Armed,
    /// <summary>A lock was requested; Windows has not confirmed it yet.</summary>
    Locking,
}

public enum BreakEndReason
{
    Ended,
    Cancelled,
}

public enum BreakPhase
{
    Running,
    LastMinute,
    Paused,
    TimeUp,
}

/// <summary>The keys the break screen reacts to; the host maps virtual keys to these.</summary>
public enum BreakKey
{
    Escape,
    Enter,
    Space,
    Up,
    Down,
    Plus,
    Minus,
    Tab,
    Other,
}

/// <summary>The "+1 min" or "-1 min" that floats up when the length changes.</summary>
public sealed record BreakBadge(int Id, string Text);

/// <summary>Mac BreakFaceState: everything one display's break screen shows.</summary>
public sealed record BreakFace
{
    public required BreakTimerSettings Settings { get; init; }
    public required string Label { get; init; }
    public required double Fraction { get; init; }
    /// <summary>Seconds until the ring is empty; null while it stands still.</summary>
    public double? SecondsToEmpty { get; init; }
    public int RingGeneration { get; init; }
    public BreakPhase Phase { get; init; }
    public string? EndTime { get; init; }
    public BreakLockState Lock { get; init; }
    public int ArmingSeconds { get; init; }
    public bool ControlsVisible { get; init; }
    public int RippleGeneration { get; init; }
    public BreakBadge? Badge { get; init; }
    public bool Closing { get; init; }
    public string? Notice { get; init; }
    public required string SpokenValue { get; init; }

    public string Title => Phase == BreakPhase.TimeUp ? BreakCopy.TimeUp : Settings.Title;

    /// <summary>A still copy for the Settings preview and the UX reference.</summary>
    public static BreakFace Preview(BreakTimerSettings settings, string? endTime) => new()
    {
        Settings = settings,
        Label = BreakCountdown.Clock(settings.Minutes * 60),
        Fraction = 0.72,
        EndTime = settings.ShowEndTime ? endTime : null,
        Lock = settings.LockWhenTouched ? BreakLockState.Armed : BreakLockState.Off,
        SpokenValue = $"{BreakCountdown.Spoken(settings.Minutes * 60)} left",
    };
}

/// <summary>What the break timer asks of the app and of Windows. The tests use a fake, so they never lock, beep or keep a computer awake.</summary>
public interface IBreakTimerHost
{
    DateTimeOffset Now { get; }

    /// <summary>The pointer in physical desktop pixels, or null when it is unknown.</summary>
    Point? Pointer { get; }

    /// <summary>Physical pixels per point on the display with the pointer, for the 12-point jitter rule.</summary>
    double PointerScale { get; }

    bool CanLock { get; }

    /// <summary>Asks Windows to lock the computer; throws <see cref="BreakLockException"/> when it refuses.</summary>
    void Lock();

    /// <summary>Whether Windows reported the session as locked since the request.</summary>
    bool IsLocked { get; }

    bool ScreenReaderRunning { get; }

    void Chime();

    /// <summary>Keeps the computer awake; <paramref name="display"/> also keeps the display on. Replaces an earlier request.</summary>
    void KeepAwake(bool display);

    void AllowSleep();

    void Announce(string text);

    /// <summary>Shows the break screen on the displays its settings choose, taking the keyboard.</summary>
    void Present(BreakTimerSettings settings, bool animated);

    /// <summary>Hides the break screen, animated or at once, then calls <paramref name="closed"/>.</summary>
    void Dismiss(bool animated, Action closed);

    void ReturnFocus();

    void HideCursorUntilMoved();

    /// <summary>The local time as Windows writes it, for "Back at".</summary>
    string FormatTime(DateTimeOffset time);

    IDisposable Schedule(TimeSpan delay, Action action);
}

/// <summary>Windows refused to lock the computer; the message says so in plain words.</summary>
public sealed class BreakLockException : Exception
{
    public BreakLockException() : base(BreakCopy.LockNotConfirmed)
    {
    }

    public BreakLockException(string message) : base(message)
    {
    }

    public BreakLockException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// The full-screen break countdown of specification 014, a port of the Mac's BreakTimerController without the windows: the
/// countdown, locking when touched, keys and controls, time up, the computer locking and unlocking, and keeping the display
/// awake. <see cref="Changed"/> tells the host to redraw from <see cref="Face"/>.
/// </summary>
public sealed class BreakSession
{
    public const int ArmingSeconds = 3;
    public const double JitterPoints = 12;
    public static readonly TimeSpan ControlsDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan LockConfirmation = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AwakeAfterTimeUp = TimeSpan.FromMinutes(30);
    /// <summary>A time up noticed later than this, after sleep, is shown without the sound.</summary>
    public const double LateChimeSeconds = 10;

    private readonly IBreakTimerHost _host;
    private readonly Action _prepare;
    private readonly Action<string> _report;
    private BreakCountdown? _countdown;
    private BreakTimerSettings _settings = new();
    private DateTimeOffset _now;
    private BreakLockState _lock;
    private int _arming;
    private bool _controlsVisible;
    private bool _controlsHovered;
    private int _ripples;
    private int _rings;
    private BreakBadge? _badge;
    private int _badges;
    private bool _closing;
    private string? _notice;
    private DateTimeOffset? _timeUpAt;
    private bool _announcedLastMinute;
    private Point? _armedPointer;
    private bool _awake;
    private bool _awakeDisplay;
    private IDisposable? _tick;
    private IDisposable? _armTimer;
    private IDisposable? _controlsTimer;
    private IDisposable? _lockTimer;
    private IDisposable? _badgeTimer;

    /// <param name="host">The app and Windows.</param>
    /// <param name="prepare">Mac model.prepareForBreak: stops zoom and region sharing and releases drawing input.</param>
    /// <param name="report">Shows an error to the person, as the model's Report does.</param>
    public BreakSession(IBreakTimerHost host, Action prepare, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _prepare = prepare;
        _report = report;
    }

    public event Action? Changed;

    public bool IsRunning => _countdown is not null && !_closing;

    public bool IsAway { get; private set; }

    public BreakLockState LockState => _lock;

    public bool ControlsVisible => _controlsVisible;

    /// <summary>Keyboard focus is on the controls, so they stay until the pointer moves.</summary>
    public bool ControlsPinned { get; private set; }

    public BreakEndReason? LastEndReason { get; private set; }

    public bool KeepsDisplayAwake => _awake && _awakeDisplay;

    public bool KeepsComputerAwake => _awake;

    public BreakTimerSettings Settings => _settings;

    public bool ControlsHovered
    {
        get => _controlsHovered;
        set
        {
            _controlsHovered = value;
            if (!value && _controlsVisible)
            {
                ScheduleControlsHide();
            }
        }
    }

    // Start and end

    /// <summary>Starts a break, replacing a running one. Throws a <see cref="CanvasError"/> or <see cref="BreakLockException"/>.</summary>
    public void Start(BreakTimerSettings requested, long? minutes = null)
    {
        var settings = requested;
        if (minutes is { } length)
        {
            settings.Minutes = length;
        }
        settings.Validate();
        if (settings.LockWhenTouched && !_host.CanLock)
        {
            throw new BreakLockException(BreakCopy.LockUnavailable);
        }
        if (_countdown is not null)
        {
            TearDown();
        }
        _prepare();
        DateTimeOffset start = _host.Now;
        _countdown = new BreakCountdown(settings.Minutes, start);
        _settings = settings;
        _now = start;
        _closing = false;
        _notice = null;
        _badge = null;
        _timeUpAt = null;
        IsAway = false;
        LastEndReason = null;
        _announcedLastMinute = settings.Minutes <= 1;
        _rings++;
        HoldAwake(display: true);
        _host.Present(settings, animated: true);
        if (settings.LockWhenTouched)
        {
            _lock = BreakLockState.Arming;
            _arming = ArmingSeconds;
            ScheduleArming();
        }
        else
        {
            _lock = BreakLockState.Off;
            RevealControls();
        }
        ScheduleTick();
        _host.HideCursorUntilMoved();
        _host.Announce($"Break timer started. {BreakCountdown.Spoken(settings.Minutes * 60)} left.");
        Changed?.Invoke();
    }

    /// <summary>Ends the break.</summary>
    public void End(BreakEndReason reason = BreakEndReason.Ended)
    {
        if (_countdown is null || _closing)
        {
            return;
        }
        LastEndReason = reason;
        StopTimers();
        _closing = true;
        Changed?.Invoke();
        _host.Dismiss(animated: !IsAway, () =>
        {
            TearDown();
            _host.ReturnFocus();
            Changed?.Invoke();
        });
    }

    /// <summary>Closes at once without animation or focus changes, as on quit.</summary>
    public void Close()
    {
        if (_countdown is null)
        {
            return;
        }
        LastEndReason = BreakEndReason.Ended;
        StopTimers();
        _host.Dismiss(animated: false, () => { });
        TearDown();
        Changed?.Invoke();
    }

    /// <summary>The break's global shortcut: ends a break that doesn't lock, cancels one that is arming and locks one that is armed.</summary>
    public void ShortcutPressed()
    {
        if (IsAway)
        {
            return;
        }
        switch (_lock)
        {
            case BreakLockState.Off:
                End();
                break;
            case BreakLockState.Arming:
                End(BreakEndReason.Cancelled);
                break;
            case BreakLockState.Armed:
                LockNow();
                break;
        }
    }

    // Changing a running break

    public void Adjust(int minutes)
    {
        if (_countdown is not { } countdown || _lock != BreakLockState.Off || _closing || minutes == 0)
        {
            return;
        }
        DateTimeOffset current = _host.Now;
        if (minutes < 0 && countdown.IsTimeUp(current))
        {
            return;
        }
        countdown.Adjust(minutes, current);
        _countdown = countdown;
        _rings++;
        if (countdown.Remaining(current) > 60)
        {
            _announcedLastMinute = false;
        }
        int id = ++_badges;
        _badge = new BreakBadge(id, minutes > 0 ? $"+{minutes} min" : $"\u2212{-minutes} min");
        _badgeTimer?.Dispose();
        _badgeTimer = _host.Schedule(TimeSpan.FromSeconds(0.9), () =>
        {
            if (_badge?.Id == id)
            {
                _badge = null;
                Changed?.Invoke();
            }
        });
        Refresh();
        long left = Math.Max((long)Math.Ceiling(countdown.Remaining(current)), 0);
        _host.Announce(left > 0 ? $"{BreakCountdown.Spoken(left)} left." : "Time's up.");
        if (_controlsVisible)
        {
            ScheduleControlsHide();
        }
    }

    public void TogglePause()
    {
        if (_countdown is not { } countdown || _lock != BreakLockState.Off || _closing)
        {
            return;
        }
        DateTimeOffset current = _host.Now;
        if (countdown.IsTimeUp(current))
        {
            return;
        }
        countdown.TogglePause(current);
        _countdown = countdown;
        _rings++;
        Refresh();
        _host.Announce(countdown.IsPaused ? "Paused." : "Resumed.");
    }

    // Input

    /// <summary>A key went down on the break screen. Returns true when the break screen used or swallowed it.</summary>
    public bool KeyDown(BreakKey key, bool commandModifiers)
    {
        if (_countdown is null || _closing || IsAway)
        {
            return false;
        }
        switch (_lock)
        {
            case BreakLockState.Locking:
                return true;
            case BreakLockState.Armed:
                LockNow();
                return true;
            case BreakLockState.Arming:
                if (key == BreakKey.Escape && !commandModifiers)
                {
                    End(BreakEndReason.Cancelled);
                }
                return true;
        }
        if (key == BreakKey.Tab)
        {
            ControlsPinned = true;
            RevealControls();
            return false;
        }
        if (commandModifiers)
        {
            return false;
        }
        switch (key)
        {
            case BreakKey.Enter or BreakKey.Space when ControlsPinned:
                return false;
            case BreakKey.Escape or BreakKey.Enter:
                End();
                return true;
            case BreakKey.Space:
                TogglePause();
                return true;
            case BreakKey.Up or BreakKey.Plus:
                Adjust(1);
                return true;
            case BreakKey.Down or BreakKey.Minus:
                Adjust(-1);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A modifier key alone (Ctrl, Alt, Shift or Win) went down.</summary>
    public void ModifierDown()
    {
        if (IsRunning && !IsAway && _lock == BreakLockState.Armed)
        {
            LockNow();
        }
    }

    /// <summary>The pointer moved on the break screen.</summary>
    public void PointerMoved()
    {
        if (!IsRunning || IsAway)
        {
            return;
        }
        if (_lock == BreakLockState.Armed)
        {
            if (_armedPointer is { } origin && _host.Pointer is { } point)
            {
                double limit = JitterPoints * Math.Max(_host.PointerScale, 1);
                double dx = point.X - origin.X, dy = point.Y - origin.Y;
                if (Math.Sqrt(dx * dx + dy * dy) > limit)
                {
                    LockNow();
                }
            }
            return;
        }
        if (_lock == BreakLockState.Off)
        {
            ControlsPinned = false;
            RevealControls();
        }
    }

    /// <summary>A click, press or scroll on the break screen.</summary>
    public void PointerPressed()
    {
        if (!IsRunning || IsAway)
        {
            return;
        }
        if (_lock == BreakLockState.Armed)
        {
            LockNow();
        }
        else if (_lock == BreakLockState.Off)
        {
            RevealControls();
        }
    }

    /// <summary>Another app became active: an armed break locks, as Alt+Tab, Win and Start count as touching it.</summary>
    public void AppDeactivated()
    {
        if (IsRunning && !IsAway && _lock == BreakLockState.Armed)
        {
            LockNow();
        }
    }

    // What the screens show

    public BreakFace Face
    {
        get
        {
            DateTimeOffset reference = _now;
            if (_countdown is not { } countdown)
            {
                return BreakFace.Preview(_settings, null);
            }
            double left = countdown.Remaining(reference);
            bool timeUp = left <= 0;
            BreakPhase phase = timeUp ? BreakPhase.TimeUp
                : countdown.IsPaused ? BreakPhase.Paused
                : left <= 60 ? BreakPhase.LastMinute
                : BreakPhase.Running;
            string? endTime = _settings.ShowEndTime && !timeUp ? EndTimeText(countdown.EndDate(reference)) : null;
            string spoken;
            if (timeUp)
            {
                long over = (long)Math.Floor(-left);
                spoken = _settings.CountOver && over > 0 ? $"Time's up. {BreakCountdown.Spoken(over)} over." : "Time's up.";
            }
            else
            {
                string words = $"{BreakCountdown.Spoken((long)Math.Ceiling(left))} left";
                spoken = (countdown.IsPaused ? "Paused. " : "") + words + (endTime is null ? "." : $". {endTime}.");
            }
            return new BreakFace
            {
                Settings = _settings,
                Label = countdown.Label(reference, _settings.CountOver),
                Fraction = timeUp ? 1 : countdown.Fraction(reference),
                SecondsToEmpty = timeUp || countdown.IsPaused ? null : left,
                RingGeneration = _rings,
                Phase = phase,
                EndTime = endTime,
                Lock = _lock,
                ArmingSeconds = _arming,
                ControlsVisible = _controlsVisible,
                RippleGeneration = _ripples,
                Badge = _badge,
                Closing = _closing,
                Notice = _notice,
                SpokenValue = spoken,
            };
        }
    }

    public string EndTimeText(DateTimeOffset time) => "Back at " + _host.FormatTime(time);

    // Ticking

    private void ScheduleTick()
    {
        _tick?.Dispose();
        _tick = null;
        // Once time is up behind the lock screen nothing changes until someone signs in.
        if (_countdown is not { } countdown || (IsAway && _timeUpAt is not null))
        {
            return;
        }
        double delay = countdown.SecondsUntilLabelChanges(_host.Now) + 0.01;
        _tick = _host.Schedule(TimeSpan.FromSeconds(delay), Refresh);
    }

    /// <summary>Reads the clock and moves the break on. The timer calls it each second; the tests call it directly.</summary>
    public void Refresh()
    {
        if (_countdown is not { } countdown || _closing)
        {
            return;
        }
        DateTimeOffset current = _host.Now;
        _now = current;
        double left = countdown.Remaining(current);
        if (left > 0)
        {
            if (_timeUpAt is not null)
            {
                _timeUpAt = null;
                HoldAwake(display: !IsAway);
            }
            if (left <= 60 && !_announcedLastMinute)
            {
                _announcedLastMinute = true;
                _host.Announce("One minute left.");
            }
        }
        else if (_timeUpAt is null)
        {
            // The real end, not when it was noticed: after sleep these can be far apart.
            _timeUpAt = countdown.EndDate(current);
            _ripples++;
            _rings++;
            if (_settings.Chime && -left < LateChimeSeconds)
            {
                _host.Chime();
            }
            _host.Announce("Time's up.");
            if (IsAway)
            {
                ReleaseAwake();
            }
        }
        if (_timeUpAt is { } up && current - up >= AwakeAfterTimeUp)
        {
            ReleaseAwake();
        }
        ScheduleTick();
        Changed?.Invoke();
    }

    // Locking

    private void ScheduleArming()
    {
        _armTimer?.Dispose();
        _armTimer = _host.Schedule(TimeSpan.FromSeconds(1), ArmStep);
    }

    /// <summary>One second of arming. The timer calls it; the tests call it directly.</summary>
    public void ArmStep()
    {
        _armTimer?.Dispose();
        _armTimer = null;
        if (_lock != BreakLockState.Arming)
        {
            return;
        }
        if (_arming > 1)
        {
            _arming--;
            ScheduleArming();
            Changed?.Invoke();
            return;
        }
        _armedPointer = _host.Pointer;
        _lock = BreakLockState.Armed;
        _host.Announce(BreakCopy.NowLocks);
        Changed?.Invoke();
    }

    private void LockNow()
    {
        if (_lock != BreakLockState.Armed)
        {
            return;
        }
        _lock = BreakLockState.Locking;
        Changed?.Invoke();
        try
        {
            _host.Lock();
        }
        catch (BreakLockException error)
        {
            LockFailed(error.Message);
            return;
        }
        if (_lock == BreakLockState.Locking)
        {
            _lockTimer = _host.Schedule(LockConfirmation, ConfirmLock);
        }
    }

    /// <summary>Called after the waiting time when Windows did not report the lock. The tests call it directly.</summary>
    public void ConfirmLock()
    {
        if (_lock != BreakLockState.Locking)
        {
            return;
        }
        if (_host.IsLocked)
        {
            SystemLocked();
        }
        else
        {
            LockFailed(BreakCopy.LockNotConfirmed);
        }
    }

    private void LockFailed(string message)
    {
        _lockTimer?.Dispose();
        _lockTimer = null;
        _lock = BreakLockState.Off;
        _settings.LockWhenTouched = false;
        _notice = message;
        RevealControls();
        _report(message);
        Changed?.Invoke();
    }

    /// <summary>
    /// The computer locked, whoever locked it, or another user came to the front. The lock screen covers the break screen,
    /// so its windows close while the countdown goes on; the sound still plays at zero (spec 014, BRK-LOCK-05).
    /// </summary>
    public void SystemLocked()
    {
        if (_countdown is null || _closing || IsAway)
        {
            return;
        }
        IsAway = true;
        foreach (IDisposable? timer in new[] { _armTimer, _controlsTimer, _lockTimer, _badgeTimer })
        {
            timer?.Dispose();
        }
        _armTimer = _controlsTimer = _lockTimer = _badgeTimer = null;
        _lock = BreakLockState.Off;
        _armedPointer = null;
        _notice = null;
        _badge = null;
        _controlsVisible = false;
        _controlsHovered = false;
        ControlsPinned = false;
        _host.Dismiss(animated: false, () => { });
        if (_timeUpAt is null)
        {
            HoldAwake(display: false);
        }
        else
        {
            ReleaseAwake();
        }
        Refresh();
    }

    /// <summary>Someone signed in again. Shows the break as it is now, without locking: whoever signed in is at the computer.</summary>
    public void SystemUnlocked()
    {
        if (_countdown is null || _closing || !IsAway)
        {
            return;
        }
        IsAway = false;
        _rings++;
        Refresh();
        if (_timeUpAt is { } up && _host.Now - up >= AwakeAfterTimeUp)
        {
            ReleaseAwake();
        }
        else
        {
            HoldAwake(display: true);
        }
        _host.Present(_settings, animated: true);
        RevealControls();
        _host.Announce("The break timer is back. " + Face.SpokenValue);
        Changed?.Invoke();
    }

    /// <summary>Displays were added, removed or changed: the windows are made again and the countdown goes on.</summary>
    public void DisplaysChanged()
    {
        if (!IsRunning || IsAway)
        {
            return;
        }
        _rings++;
        _host.Present(_settings, animated: false);
        Changed?.Invoke();
    }

    // Controls

    private void RevealControls()
    {
        if (_lock != BreakLockState.Off || _countdown is null || _closing)
        {
            return;
        }
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            Changed?.Invoke();
        }
        ScheduleControlsHide();
    }

    private void ScheduleControlsHide()
    {
        _controlsTimer?.Dispose();
        _controlsTimer = _host.Schedule(ControlsDuration, HideControls);
    }

    /// <summary>Hides the controls unless the pointer rests on them, a control has the keyboard or Narrator runs. The tests call it directly.</summary>
    public void HideControls()
    {
        if (_controlsHovered || ControlsPinned || _host.ScreenReaderRunning)
        {
            ScheduleControlsHide();
            return;
        }
        if (_controlsVisible)
        {
            _controlsVisible = false;
            _host.HideCursorUntilMoved();
            Changed?.Invoke();
        }
    }

    // Cleanup

    private void HoldAwake(bool display)
    {
        if (_awake && _awakeDisplay == display)
        {
            return;
        }
        _host.KeepAwake(display);
        _awake = true;
        _awakeDisplay = display;
    }

    private void ReleaseAwake()
    {
        if (_awake)
        {
            _host.AllowSleep();
        }
        _awake = false;
        _awakeDisplay = false;
    }

    private void StopTimers()
    {
        foreach (IDisposable? timer in new[] { _tick, _armTimer, _controlsTimer, _lockTimer, _badgeTimer })
        {
            timer?.Dispose();
        }
        _tick = _armTimer = _controlsTimer = _lockTimer = _badgeTimer = null;
        _controlsVisible = false;
        _controlsHovered = false;
        ControlsPinned = false;
    }

    private void TearDown()
    {
        StopTimers();
        ReleaseAwake();
        _countdown = null;
        _closing = false;
        _lock = BreakLockState.Off;
        _badge = null;
        _notice = null;
        _armedPointer = null;
        _timeUpAt = null;
        IsAway = false;
    }
}
