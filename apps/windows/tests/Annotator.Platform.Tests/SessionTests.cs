using Annotator.Platform.Sessions;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.Win32;

namespace Annotator.Platform.Tests;

/// <summary>Session and power transitions (WIN-OVR-09) and virtual desktop switches (WIN-OVR-06).</summary>
public sealed class SessionTests
{
    [Theory]
    [InlineData(PInvoke.WTS_SESSION_LOCK, SessionTransition.Locked)]
    [InlineData(PInvoke.WTS_SESSION_UNLOCK, SessionTransition.Unlocked)]
    [InlineData(PInvoke.WTS_SESSION_LOGOFF, SessionTransition.SigningOut)]
    [InlineData(PInvoke.WTS_CONSOLE_CONNECT, SessionTransition.ConnectionChanged)]
    [InlineData(PInvoke.WTS_CONSOLE_DISCONNECT, SessionTransition.ConnectionChanged)]
    [InlineData(PInvoke.WTS_REMOTE_CONNECT, SessionTransition.ConnectionChanged)]
    [InlineData(PInvoke.WTS_REMOTE_DISCONNECT, SessionTransition.ConnectionChanged)]
    public void SessionChangesAreClassified(uint change, SessionTransition expected) =>
        Assert.Equal(expected, SessionWatcher.Classify(PInvoke.WM_WTSSESSION_CHANGE, change));

    [Fact]
    public void OnlySleepAmongPowerChangesPauses()
    {
        Assert.Equal(SessionTransition.Sleeping, SessionWatcher.Classify(PInvoke.WM_POWERBROADCAST, PInvoke.PBT_APMSUSPEND));
        Assert.Null(SessionWatcher.Classify(PInvoke.WM_POWERBROADCAST, 0x12));
        Assert.Null(SessionWatcher.Classify(PInvoke.WM_WTSSESSION_CHANGE, 0x9));
        Assert.Null(SessionWatcher.Classify(0x001A, PInvoke.WTS_SESSION_LOCK));
    }

    [Fact]
    public async Task TheWatcherReportsOnItsOwnThread()
    {
        DispatcherThread thread = DispatcherThread.Start("Annotator test sessions");
        try
        {
            var seen = new List<(SessionTransition Transition, int Thread)>();
            int owner = await thread.Run(() =>
            {
                using SessionWatcher watcher = SessionWatcher.Start(transition => seen.Add((transition, Environment.CurrentManagedThreadId)));
                watcher.Deliver(PInvoke.WM_WTSSESSION_CHANGE, PInvoke.WTS_SESSION_LOCK);
                watcher.Deliver(PInvoke.WM_POWERBROADCAST, 0x12);
                watcher.Deliver(PInvoke.WM_POWERBROADCAST, PInvoke.PBT_APMSUSPEND);
                watcher.Deliver(PInvoke.WM_WTSSESSION_CHANGE, PInvoke.WTS_SESSION_UNLOCK);
                return Environment.CurrentManagedThreadId;
            });
            Assert.Equal([SessionTransition.Locked, SessionTransition.Sleeping, SessionTransition.Unlocked], seen.Select(entry => entry.Transition));
            Assert.All(seen, entry => Assert.Equal(owner, entry.Thread));
        }
        finally
        {
            await thread.Stop();
        }
    }

    [Fact]
    public void ADesktopSwitchIsNoticedWhenTheLastAppWindowIsLeftBehind()
    {
        const nint Mail = 1, Browser = 2, Editor = 3, Taskbar = 4;
        var desktops = new Dictionary<nint, int> { [Mail] = 1, [Browser] = 1, [Editor] = 2 };
        int current = 1;
        var closed = new HashSet<nint>();
        var tracker = new VirtualDesktopTracker(
            window => closed.Contains(window) ? null : desktops[window] == current,
            window => desktops.ContainsKey(window));

        Assert.False(tracker.Switched(Mail));
        Assert.False(tracker.Switched(Browser));
        Assert.False(tracker.Switched(Taskbar));

        current = 2;
        Assert.True(tracker.Switched(Editor));
        Assert.False(tracker.Switched(Taskbar));
        Assert.False(tracker.Switched(Editor));

        // An empty desktop shows only the shell: one switch, then nothing until an app window is active there.
        current = 3;
        Assert.True(tracker.Switched(Taskbar));
        Assert.False(tracker.Switched(Taskbar));

        // A closed reference window tells nothing.
        current = 1;
        tracker.Reset(Browser);
        closed.Add(Browser);
        Assert.False(tracker.Switched(Mail));
        current = 2;
        Assert.True(tracker.Switched(Editor));
    }

    [Fact]
    public async Task TheSystemDesktopManagerKeepsTheForegroundWindowOnTheCurrentDesktop()
    {
        DispatcherThread thread = DispatcherThread.Start("Annotator test desktops");
        try
        {
            bool? switched = await thread.Run(() =>
            {
                nint foreground = (nint)PInvoke.GetForegroundWindow();
                if (foreground == 0)
                {
                    return (bool?)null;
                }
                using VirtualDesktopTracker tracker = VirtualDesktopTracker.Create();
                tracker.Reset(foreground);
                return tracker.Switched(foreground);
            });
            Assert.SkipWhen(switched is null, "No window is in the foreground (the session may be locked).");
            Assert.False(switched);
        }
        finally
        {
            await thread.Stop();
        }
    }
}
