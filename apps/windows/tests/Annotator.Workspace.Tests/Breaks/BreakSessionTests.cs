// Port of the logic in apps/macos/Tests/NativeAppTests/BreakTimerTests.swift (specification 014). The host is fake: no window shows,
// nothing locks, beeps or keeps a computer awake.
using Annotator.Core;
using Annotator.Workspace.Breaks;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.Tests.Breaks;

public sealed class BreakSessionTests
{
    private sealed class FakeHost : IBreakTimerHost
    {
        public DateTimeOffset Clock = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public Point? PointerAt = new Point(1700, 400);
        public bool LockAvailable = true;
        public string? LockError;
        public bool Locked;
        public bool Reader;
        public int Locks;
        public int Chimes;
        public readonly List<bool> AwakeRequests = [];
        public int AwakeReleases;
        public readonly List<bool> Presented = [];
        public int FocusReturns;
        public readonly List<string> Announcements = [];
        public readonly List<(TimeSpan Delay, Timer Handle)> Timers = [];
        public bool Showing;

        public DateTimeOffset Now => Clock;
        public Point? Pointer => PointerAt;
        public double PointerScale => 1;
        public bool CanLock => LockAvailable;
        public bool IsLocked => Locked;
        public bool ScreenReaderRunning => Reader;

        public void Lock()
        {
            Locks++;
            if (LockError is { } message)
            {
                throw new BreakLockException(message);
            }
        }

        public void Chime() => Chimes++;

        public void KeepAwake(bool display) => AwakeRequests.Add(display);

        public void AllowSleep() => AwakeReleases++;

        public void Announce(string text) => Announcements.Add(text);

        public void Present(BreakTimerSettings settings, bool animated)
        {
            Presented.Add(animated);
            Showing = true;
        }

        public void Dismiss(bool animated, Action closed)
        {
            Showing = false;
            closed();
        }

        public void ReturnFocus() => FocusReturns++;

        public void HideCursorUntilMoved()
        {
        }

        public string FormatTime(DateTimeOffset time) => time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var handle = new Timer();
            Timers.Add((delay, handle));
            return handle;
        }

        public sealed class Timer : IDisposable
        {
            public bool Cancelled { get; private set; }

            public void Dispose() => Cancelled = true;
        }
    }

    private sealed class Counter
    {
        public int Value;
    }

    private static (BreakSession Session, FakeHost Host, List<string> Reports, Counter Prepared) Make()
    {
        var host = new FakeHost();
        var reports = new List<string>();
        var prepared = new Counter();
        var session = new BreakSession(host, () => prepared.Value++, reports.Add);
        return (session, host, reports, prepared);
    }

    private static BreakTimerSettings Unlocked(long minutes = 10) => new() { LockWhenTouched = false, Minutes = minutes };

    private static void Arm(BreakSession session)
    {
        for (var step = 0; step < BreakSession.ArmingSeconds; step++)
        {
            session.ArmStep();
        }
    }

    [Fact]
    public void StartingShowsTheScreenKeepsTheDisplayAwakeAndArms()
    {
        var (session, host, _, prepared) = Make();
        session.Start(new BreakTimerSettings());
        Assert.True(session.IsRunning);
        Assert.Equal(1, prepared.Value);
        Assert.Equal([true], host.Presented);
        Assert.Equal([true], host.AwakeRequests);
        Assert.Equal("10:00", session.Face.Label);
        Assert.Equal("Coffee break", session.Face.Title);
        Assert.Equal("Back at 12:10", session.Face.EndTime);
        Assert.Equal(BreakLockState.Arming, session.LockState);
        Assert.Equal(3, session.Face.ArmingSeconds);
        session.End();
        Assert.False(session.IsRunning);
        Assert.Equal(1, host.AwakeReleases);
        Assert.Equal(1, host.FocusReturns);
        Assert.Equal(BreakEndReason.Ended, session.LastEndReason);
    }

    [Fact]
    public void ArmedBreakLocksOnAKeyAndKeepsCountingBehindTheLockScreen()
    {
        var (session, host, _, _) = Make();
        session.Start(new BreakTimerSettings());
        Assert.True(session.KeyDown(BreakKey.Other, false), "Input during arming is swallowed.");
        Assert.Equal(0, host.Locks);
        session.ArmStep();
        Assert.Equal(2, session.Face.ArmingSeconds);
        session.ArmStep();
        session.ArmStep();
        Assert.Equal(BreakLockState.Armed, session.LockState);
        Assert.True(session.KeyDown(BreakKey.Other, false));
        Assert.Equal(1, host.Locks);
        Assert.Equal(BreakLockState.Locking, session.LockState);
        Assert.True(session.KeyDown(BreakKey.Other, false));
        Assert.Equal(1, host.Locks);
        host.Locked = true;
        session.ConfirmLock();
        Assert.True(session.IsRunning, "The break keeps counting behind the lock screen.");
        Assert.True(session.IsAway);
        Assert.Null(session.LastEndReason);
        Assert.False(host.Showing);
        Assert.Equal([true, false], host.AwakeRequests);
        Assert.False(session.KeyDown(BreakKey.Other, false), "Nothing is handled while the computer is locked.");
        host.Clock = host.Clock.AddSeconds(120);
        session.SystemUnlocked();
        Assert.False(session.IsAway);
        Assert.True(host.Showing, "The break screen comes back after sign-in.");
        Assert.Equal(BreakLockState.Off, session.LockState);
        Assert.True(session.ControlsVisible);
        Assert.Equal("8:00", session.Face.Label);
        Assert.Equal([true, false, true], host.AwakeRequests);
        Assert.True(session.KeyDown(BreakKey.Escape, false));
        Assert.False(session.IsRunning);
    }

    [Theory]
    [InlineData("modifier")]
    [InlineData("press")]
    [InlineData("deactivated")]
    [InlineData("move")]
    [InlineData("shortcut")]
    public void EveryKindOfTouchLocksButJitterDoesNot(string kind)
    {
        var (session, host, _, _) = Make();
        session.Start(new BreakTimerSettings());
        Arm(session);
        switch (kind)
        {
            case "modifier":
                session.ModifierDown();
                break;
            case "press":
                session.PointerPressed();
                break;
            case "deactivated":
                session.AppDeactivated();
                break;
            case "shortcut":
                session.ShortcutPressed();
                break;
            default:
                host.PointerAt = new Point(1708, 400);
                session.PointerMoved();
                Assert.Equal(0, host.Locks);
                host.PointerAt = new Point(1718, 400);
                session.PointerMoved();
                break;
        }
        Assert.Equal(1, host.Locks);
    }

    [Fact]
    public void ShortcutAndEscapeCancelWhileArming()
    {
        var (session, host, _, _) = Make();
        session.Start(new BreakTimerSettings());
        session.ShortcutPressed();
        Assert.False(session.IsRunning);
        Assert.Equal(BreakEndReason.Cancelled, session.LastEndReason);
        session.Start(new BreakTimerSettings());
        Assert.True(session.KeyDown(BreakKey.Escape, false));
        Assert.Equal(BreakEndReason.Cancelled, session.LastEndReason);
        Assert.Equal(0, host.Locks);
    }

    [Fact]
    public void AFailedLockKeepsTheTimerAndSaysNothingIsLocked()
    {
        var (session, host, reports, _) = Make();
        host.LockError = BreakCopy.LockRefused(5);
        session.Start(new BreakTimerSettings());
        Arm(session);
        session.PointerPressed();
        Assert.True(session.IsRunning);
        Assert.Equal(BreakLockState.Off, session.LockState);
        Assert.Equal(BreakCopy.LockRefused(5), session.Face.Notice);
        Assert.Contains("so nothing is locked", session.Face.Notice, StringComparison.Ordinal);
        Assert.True(session.ControlsVisible);
        Assert.Equal([BreakCopy.LockRefused(5)], reports);
        Assert.False(session.KeyDown(BreakKey.Other, false), "Without a lock, other keys pass on.");
    }

    [Fact]
    public void ALockWindowsDoesNotConfirmIsNotShownAsALock()
    {
        var (session, _, reports, _) = Make();
        session.Start(new BreakTimerSettings());
        Arm(session);
        session.PointerPressed();
        session.ConfirmLock();
        Assert.True(session.IsRunning);
        Assert.Equal(BreakLockState.Off, session.LockState);
        Assert.Equal(BreakCopy.LockNotConfirmed, session.Face.Notice);
        Assert.Equal([BreakCopy.LockNotConfirmed], reports);
    }

    [Fact]
    public void ABreakThatCannotLockDoesNotStart()
    {
        var (session, host, _, prepared) = Make();
        host.LockAvailable = false;
        var error = Assert.Throws<BreakLockException>(() => session.Start(new BreakTimerSettings()));
        Assert.Equal(BreakCopy.LockUnavailable, error.Message);
        Assert.False(session.IsRunning);
        Assert.Empty(host.Presented);
        Assert.Empty(host.AwakeRequests);
        Assert.Equal(0, prepared.Value);
        session.Start(Unlocked());
        Assert.True(session.IsRunning);
    }

    [Fact]
    public void TheBreakCountsAndChimesWhileLockedAndReturnsAfterSignIn()
    {
        var (session, host, _, _) = Make();
        session.Start(Unlocked(1));
        session.SystemLocked();
        Assert.True(session.IsRunning);
        Assert.False(host.Showing);
        Assert.True(session.KeepsComputerAwake);
        Assert.False(session.KeepsDisplayAwake, "The display may sleep behind the lock screen.");
        session.SystemLocked();
        Assert.Equal([true, false], host.AwakeRequests);
        host.Clock = host.Clock.AddSeconds(60.5);
        session.Refresh();
        Assert.Equal(1, host.Chimes);
        Assert.False(session.KeepsComputerAwake, "After the sound nothing needs the computer awake.");
        host.Clock = host.Clock.AddSeconds(90);
        session.SystemUnlocked();
        Assert.Equal(BreakPhase.TimeUp, session.Face.Phase);
        Assert.Equal("+1:30", session.Face.Label);
        Assert.True(host.Showing);
        Assert.True(session.KeepsDisplayAwake);
        Assert.Equal(1, host.Chimes);
        int presented = host.Presented.Count;
        session.SystemUnlocked();
        Assert.Equal(presented, host.Presented.Count);
    }

    [Fact]
    public void ATimeUpMissedDuringSleepIsShownWithoutTheSound()
    {
        var (session, host, _, _) = Make();
        session.Start(Unlocked(1));
        session.SystemLocked();
        host.Clock = host.Clock.Add(BreakSession.AwakeAfterTimeUp).AddSeconds(120);
        session.SystemUnlocked();
        Assert.Equal(0, host.Chimes);
        Assert.Equal(BreakPhase.TimeUp, session.Face.Phase);
        Assert.False(session.KeepsComputerAwake);
        Assert.True(host.Showing);
    }

    [Fact]
    public void LockingDuringArmingStopsArmingAndTheBreakReturnsWithoutLocking()
    {
        var (session, host, _, _) = Make();
        session.Start(new BreakTimerSettings());
        session.SystemLocked();
        Arm(session);
        Assert.Equal(BreakLockState.Off, session.LockState);
        session.SystemUnlocked();
        session.PointerPressed();
        Assert.Equal(0, host.Locks);
    }

    [Fact]
    public void KeysPauseChangeTimeAndEndWithoutLocking()
    {
        var (session, host, _, _) = Make();
        session.Start(Unlocked());
        Assert.Equal(BreakLockState.Off, session.LockState);
        Assert.True(session.ControlsVisible);
        host.Clock = host.Clock.AddSeconds(30);
        Assert.True(session.KeyDown(BreakKey.Space, false));
        Assert.Equal(BreakPhase.Paused, session.Face.Phase);
        Assert.Null(session.Face.SecondsToEmpty);
        host.Clock = host.Clock.AddSeconds(120);
        session.Refresh();
        Assert.Equal("9:30", session.Face.Label);
        Assert.True(session.KeyDown(BreakKey.Space, false));
        Assert.Equal(BreakPhase.Running, session.Face.Phase);
        Assert.True(session.KeyDown(BreakKey.Up, false));
        Assert.Equal("10:30", session.Face.Label);
        Assert.Equal("+1 min", session.Face.Badge?.Text);
        Assert.True(session.KeyDown(BreakKey.Minus, false));
        Assert.True(session.KeyDown(BreakKey.Down, false));
        Assert.Equal("8:30", session.Face.Label);
        Assert.False(session.KeyDown(BreakKey.Other, true), "Ctrl, Alt and Win keys stay with the app.");
        session.HideControls();
        Assert.False(session.KeyDown(BreakKey.Tab, false), "Tab moves between the controls.");
        Assert.True(session.ControlsVisible, "Tab shows the controls.");
        session.HideControls();
        Assert.True(session.ControlsVisible, "Controls with keyboard focus stay.");
        Assert.False(session.KeyDown(BreakKey.Space, false), "Space presses the focused control.");
        Assert.False(session.KeyDown(BreakKey.Enter, false), "Enter presses the focused control.");
        Assert.True(session.IsRunning);
        Assert.Equal(BreakPhase.Running, session.Face.Phase);
        session.PointerMoved();
        session.HideControls();
        Assert.False(session.ControlsVisible);
        Assert.True(session.KeyDown(BreakKey.Escape, false));
        Assert.False(session.IsRunning);
        Assert.Equal(0, host.Locks);
    }

    [Fact]
    public void ControlsHideUnlessThePointerRestsOnThemOrNarratorRuns()
    {
        var (session, host, _, _) = Make();
        session.Start(Unlocked());
        session.HideControls();
        Assert.False(session.ControlsVisible);
        session.PointerMoved();
        Assert.True(session.ControlsVisible);
        session.ControlsHovered = true;
        session.HideControls();
        Assert.True(session.ControlsVisible);
        session.ControlsHovered = false;
        host.Reader = true;
        session.HideControls();
        Assert.True(session.ControlsVisible, "Narrator users keep the controls.");
        host.Reader = false;
        session.HideControls();
        Assert.False(session.ControlsVisible);
    }

    [Fact]
    public void TimeUpChimesOnceCountsOverAndLetsTheDisplaySleepLater()
    {
        var (session, host, _, _) = Make();
        session.Start(Unlocked(1));
        int ripples = session.Face.RippleGeneration;
        host.Clock = host.Clock.AddSeconds(30);
        session.Refresh();
        Assert.Equal(BreakPhase.LastMinute, session.Face.Phase);
        host.Clock = host.Clock.AddSeconds(35);
        session.Refresh();
        session.Refresh();
        Assert.Equal(1, host.Chimes);
        Assert.Equal(ripples + 1, session.Face.RippleGeneration);
        Assert.Equal(BreakPhase.TimeUp, session.Face.Phase);
        Assert.Equal("Time's up", session.Face.Title);
        Assert.Equal("+0:05", session.Face.Label);
        Assert.Equal(1, session.Face.Fraction);
        Assert.Null(session.Face.EndTime);
        Assert.StartsWith("Time's up.", session.Face.SpokenValue, StringComparison.Ordinal);
        Assert.True(session.KeyDown(BreakKey.Space, false));
        Assert.Equal(BreakPhase.TimeUp, session.Face.Phase);
        Assert.True(session.KeyDown(BreakKey.Down, false));
        Assert.Equal("+0:05", session.Face.Label);
        Assert.Null(session.Face.Badge);
        Assert.True(session.KeepsDisplayAwake);
        host.Clock = host.Clock.Add(BreakSession.AwakeAfterTimeUp);
        session.Refresh();
        Assert.False(session.KeepsDisplayAwake);
        Assert.Equal(1, host.AwakeReleases);
        Assert.True(session.IsRunning);
        session.KeyDown(BreakKey.Up, false);
        Assert.Equal("1:00", session.Face.Label);
        Assert.Equal(BreakPhase.LastMinute, session.Face.Phase);
        Assert.True(session.KeepsDisplayAwake);
    }

    [Fact]
    public void SilentBreakWithoutCountingOver()
    {
        var (session, host, _, _) = Make();
        session.Start(new BreakTimerSettings
        {
            LockWhenTouched = false, Chime = false, CountOver = false, ShowEndTime = false, Title = "", Minutes = 2,
        });
        Assert.Null(session.Face.EndTime);
        Assert.Equal("", session.Face.Title);
        host.Clock = host.Clock.AddSeconds(200);
        session.Refresh();
        Assert.Equal(0, host.Chimes);
        Assert.Equal("0:00", session.Face.Label);
    }

    [Fact]
    public void DisplayChangesRebuildTheScreenAndKeepTheTime()
    {
        var (session, host, _, _) = Make();
        session.Start(Unlocked());
        host.Clock = host.Clock.AddSeconds(90);
        session.DisplaysChanged();
        Assert.Equal([true, false], host.Presented);
        session.Refresh();
        Assert.Equal("8:30", session.Face.Label);
    }

    [Fact]
    public void ClosingCancelsEveryTimer()
    {
        var (session, host, _, _) = Make();
        session.Start(new BreakTimerSettings());
        session.ArmStep();
        session.Close();
        Assert.False(session.IsRunning);
        Assert.All(host.Timers, timer => Assert.True(timer.Handle.Cancelled));
        Assert.Equal(1, host.AwakeReleases);
    }

    [Fact]
    public void BreakShortcutIsAGlobalKeyWithItsOwnKeys()
    {
        Assert.Contains(GlobalShortcutKind.BreakTimer, GlobalShortcutKind.AllCases);
        Assert.Equal(GlobalShortcutKind.BreakTimer, GlobalShortcutKind.AllCases[^1]);
        Assert.Equal("Break timer", GlobalShortcutKind.BreakTimer.Title);
        Assert.Equal("breakTimer", GlobalShortcutKind.BreakTimer.RawValue);
        var preferences = new WorkspacePreferences();
        Assert.Equal("Ctrl+3", WindowsShortcuts.Label(GlobalShortcutAction.BreakTimer, preferences));
        Assert.Equal("⌃3", GlobalShortcutKind.BreakTimer.Value(preferences).Label);
        Assert.True(WindowsShortcuts.ToolLibraryKeyIsFree(preferences));
        preferences.BreakShortcut = new ActivationShortcut(0x25, "l", command: true);
        Assert.False(WindowsShortcuts.ToolLibraryKeyIsFree(preferences), "Ctrl+L leaves the Tools window when the break timer uses it.");
        Assert.Contains(SettingsTab.BreakTimer, SettingsTab.AllCases);
        Assert.Equal("Break timer", SettingsTab.BreakTimer.Title);
        Assert.Equal(SettingsTab.BreakTimer, SettingsTab.FromRawValue("breakTimer"));
    }
}
