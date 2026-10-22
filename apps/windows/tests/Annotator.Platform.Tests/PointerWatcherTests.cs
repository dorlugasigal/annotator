using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;

namespace Annotator.Platform.Tests;

/// <summary>
/// The raw-input pointer watcher behind Spotlight and Cursor halo (WIN-INP-06, WIN-SYS-07): it registers raw mouse input
/// for a message-only window on its own thread only while it exists, reports moves and left or right presses once until
/// they are taken, and never takes over a registration it did not make.
/// </summary>
public sealed class PointerWatcherTests
{
    private const ushort LeftDown = 0x0001;
    private const ushort LeftUp = 0x0002;
    private const ushort RightDown = 0x0004;
    private const ushort Wheel = 0x0400;
    private const ushort Absolute = 0x0001;
    private const uint InputSink = 0x00000100;

    [Fact]
    public async Task RegistersRawMouseInputOnlyWhileItExists()
    {
        Assert.Empty(MouseRegistrations());
        PointerWatcher watcher = PointerWatcher.Start(static () => { });
        try
        {
            Assert.True(await Until(() => watcher.State == PointerWatcherState.Watching), $"State {watcher.State}: {watcher.Failure}");
            nint window = watcher.Window;
            Assert.NotEqual(0, window);
            var registration = Assert.Single(MouseRegistrations());
            Assert.Equal(InputSink, registration.Flags & InputSink);
            Assert.Equal(window, registration.Target);
        }
        finally
        {
            Assert.True(await watcher.StopAsync(TestTimeouts.Work), "The watcher thread did not end.");
        }
        Assert.Equal(PointerWatcherState.Stopped, watcher.State);
        Assert.Null(watcher.Failure);
        Assert.Equal(0, watcher.Window);
        Assert.Empty(MouseRegistrations());
    }

    [Fact]
    public void ReportsMovesAndPressesOnceUntilTaken()
    {
        // A detached watcher hears only these reports, never the real mouse.
        int signals = 0;
        PointerWatcher watcher = PointerWatcher.StartDetached(() => signals++);
        // A wheel turn, a release and a report without movement are not pointer activity.
        watcher.NoteForTest(Wheel, 0, 0, 0);
        watcher.NoteForTest(LeftUp, 0, 0, 0);
        Assert.Equal(0, signals);
        Assert.True(watcher.Take().IsEmpty);

        watcher.NoteForTest(0, 0, 3, -1);
        watcher.NoteForTest(0, 0, 1, 0);
        Assert.Equal(1, signals);
        PointerActivity moves = watcher.Take();
        Assert.True(moves.Moved);
        Assert.Equal(0, moves.Clicks);
        Assert.Null(moves.Click);

        watcher.NoteForTest(LeftDown, 0, 0, 0);
        watcher.NoteForTest(RightDown, 0, 2, 2);
        Assert.Equal(2, signals);
        PointerActivity clicks = watcher.Take();
        Assert.True(clicks.Moved);
        Assert.Equal(2, clicks.Clicks);
        Assert.Equal(PointerWatcher.CursorPosition(), clicks.Click);
        Assert.True(clicks.ClickTimestamp > 0);
        Assert.True(watcher.Take().IsEmpty);

        // Absolute reports (tablets, remote sessions) move only when the position changes.
        watcher.NoteForTest(0, Absolute, 1000, 2000);
        Assert.True(watcher.Take().Moved);
        watcher.NoteForTest(0, Absolute, 1000, 2000);
        Assert.False(watcher.Take().Moved);
        watcher.NoteForTest(0, Absolute, 1001, 2000);
        Assert.True(watcher.Take().Moved);

        // After Dispose nothing signals.
        watcher.Dispose();
        watcher.NoteForTest(0, 0, 5, 5);
        Assert.Equal(PointerWatcherState.Stopped, watcher.State);
        Assert.Equal(4, signals);
    }
    [Fact]
    public async Task NeverTakesOverAnotherRegistration()
    {
        PointerWatcher first = PointerWatcher.Start(static () => { });
        PointerWatcher? second = null;
        try
        {
            Assert.True(await Until(() => first.State == PointerWatcherState.Watching), $"State {first.State}: {first.Failure}");
            Exception? reported = null;
            second = PointerWatcher.Start(static () => { }, error => Volatile.Write(ref reported, error));
            // The state turns Failed just before the failure is reported on the watcher's thread, so wait for both.
            Assert.True(await Until(() => second.State == PointerWatcherState.Failed && Volatile.Read(ref reported) is not null), $"State {second.State}");
            Assert.IsType<InvalidOperationException>(second.Failure);
            Assert.Same(second.Failure, reported);
            // The first one still holds its registration.
            Assert.Equal(first.Window, Assert.Single(MouseRegistrations()).Target);
        }
        finally
        {
            Assert.True(await first.StopAsync(TestTimeouts.Work), "The first watcher did not end.");
            if (second is not null)
            {
                Assert.True(await second.StopAsync(TestTimeouts.Work), "The second watcher did not end.");
            }
        }
        // A watcher that starts right after another stops waits for its registration to go, then takes it.
        PointerWatcher again = PointerWatcher.Start(static () => { });
        PointerWatcher next = PointerWatcher.Start(static () => { });
        try
        {
            Assert.True(await Until(() => again.State == PointerWatcherState.Watching || next.State != PointerWatcherState.Starting));
            again.Dispose();
            Assert.True(await Until(() => next.State != PointerWatcherState.Starting), $"State {next.State}");
            Assert.Equal(PointerWatcherState.Watching, next.State);
            Assert.Equal(next.Window, Assert.Single(MouseRegistrations()).Target);
        }
        finally
        {
            Assert.True(await again.StopAsync(TestTimeouts.Work));
            Assert.True(await next.StopAsync(TestTimeouts.Work));
        }
        Assert.Empty(MouseRegistrations());
    }

    // Only the watcher reads raw mouse input (one registration per process): a registration of another part, even one that
    // follows the keyboard focus, stays, and the watcher reports that it cannot follow the pointer.
    [Fact]
    public async Task NeverTakesOverARegistrationThatFollowsTheFocus()
    {
        RegisterMouse(0);
        try
        {
            Exception? reported = null;
            PointerWatcher watcher = PointerWatcher.Start(static () => { }, error => reported = error);
            try
            {
                Assert.True(await Until(() => watcher.State == PointerWatcherState.Failed), $"State {watcher.State}");
                Assert.IsType<InvalidOperationException>(watcher.Failure);
                Assert.Same(watcher.Failure, reported);
                Assert.Equal(0, Assert.Single(MouseRegistrations()).Target);
            }
            finally
            {
                Assert.True(await watcher.StopAsync(TestTimeouts.Work), "The watcher thread did not end.");
            }
        }
        finally
        {
            RegisterMouse(RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE);
        }
        Assert.Empty(MouseRegistrations());
    }

    /// <summary>
    /// Explicit: real input, as <see cref="RealMouseInputReachesTheWatcher"/>. Live zoom's low-level mouse hook and the watcher
    /// run at once, as when Spotlight or Cursor halo shows during zoom: the hook registers no raw input, the watcher keeps the
    /// process's only registration, and a click on the test's own window reaches both.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ZoomMouseHookAndWatcherHearTheSameInput()
    {
        PixelRect tile = TestDisplays.Tiles(1, size: 120)[0].Bounds;
        await using StandInWindow target = await StandInWindow.StartAsync(registry: null, WindowLayer.Toolbar, tile);
        Assert.True(PInvoke.GetCursorPos(out System.Drawing.Point pointer));
        var hooks = LowLevelInputHooks.Start(new LowLevelInputOptions { Wheel = _ => false, MasksAlt = false });
        PointerWatcher? watcher = null;
        try
        {
            hooks.Update(mouseHook: true, keyboardHook: false);
            await hooks.RunAsync(() => { });
            Assert.True(hooks.MouseInstalled);
            Assert.Empty(MouseRegistrations());
            watcher = PointerWatcher.Start(static () => { });
            PointerWatcher started = watcher;
            Assert.True(await Until(() => started.State == PointerWatcherState.Watching), $"State {started.State}: {started.Failure}");
            Assert.Equal(started.Window, Assert.Single(MouseRegistrations()).Target);
            var point = new System.Drawing.Point(tile.Left + 60, tile.Top + 60);
            started.Take();
            long calls = hooks.Statistics.Calls;
            RealInput.Click(point.X, point.Y, target.Window);
            PointerActivity? click = null;
            for (int round = 0; round < 10 && click is null; round++)
            {
                PointerActivity activity = started.Take();
                click = activity.Clicks > 0 ? activity : null;
                if (click is null)
                {
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
            }
            Assert.NotNull(click);
            Assert.Equal(point, click.Value.Click);
            Assert.Equal(1, target.Presses);
            Assert.True(await Until(() => hooks.Statistics.Calls > calls), "The zoom hook heard none of the input.");
            Assert.Equal(PointerWatcherState.Watching, started.State);
            Assert.True(hooks.MouseInstalled);
        }
        finally
        {
            if (watcher is not null)
            {
                Assert.True(await watcher.StopAsync(TestTimeouts.Work), "The watcher thread did not end.");
            }
            Assert.True(await hooks.StopAsync(TestTimeouts.Work), "The hook thread did not end.");
            PInvoke.SetCursorPos(pointer.X, pointer.Y);
        }
        Assert.Empty(MouseRegistrations());
    }

    private static unsafe void RegisterMouse(RAWINPUTDEVICE_FLAGS flags)
    {
        var device = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = flags, hwndTarget = HWND.Null };
        Assert.True(PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)), $"RegisterRawInputDevices failed: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
    }

    /// <summary>
    /// Explicit: real input. Moves the pointer onto the test's own 120-pixel window in the primary display's top-left corner
    /// and clicks it there (WindowFromPoint first), then moves it 40 times inside that window, for about a second. The
    /// watcher, which sees all mouse input as an input sink, reports the move and the press at the pointer's position;
    /// while activity waits to be taken it sleeps through the 40 reports and reads them in one go after the take.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task RealMouseInputReachesTheWatcher()
    {
        PixelRect tile = TestDisplays.Tiles(1, size: 120)[0].Bounds;
        await using StandInWindow target = await StandInWindow.StartAsync(registry: null, WindowLayer.Toolbar, tile);
        Assert.True(PInvoke.GetCursorPos(out System.Drawing.Point pointer));
        int signals = 0;
        PointerWatcher watcher = PointerWatcher.Start(() => Interlocked.Increment(ref signals));
        try
        {
            Assert.True(await Until(() => watcher.State == PointerWatcherState.Watching), $"State {watcher.State}: {watcher.Failure}");
            var point = new System.Drawing.Point(tile.Left + 60, tile.Top + 60);
            watcher.Take();
            RealInput.Click(point.X, point.Y, target.Window);
            // The press may wait in the queue until the first take, so collect until it shows.
            bool moved = false;
            PointerActivity? click = null;
            for (int round = 0; round < 10 && click is null; round++)
            {
                int before = Volatile.Read(ref signals);
                PointerActivity activity = watcher.Take();
                moved |= activity.Moved;
                click = activity.Clicks > 0 ? activity : null;
                if (click is null)
                {
                    await Until(() => Volatile.Read(ref signals) > before, 1000);
                }
            }
            Assert.NotNull(click);
            Assert.True(moved || click.Value.Moved || pointer == point);
            Assert.Equal(1, click.Value.Clicks);
            Assert.Equal(point, click.Value.Click);
            Assert.Equal(1, target.Presses);

            // One move signals; then the thread sleeps through the next 40 until the take.
            watcher.Take();
            int signalled = Volatile.Read(ref signals);
            RealInput.DragTo(point.X - 30, point.Y, target.Window);
            Assert.True(await Until(() => Volatile.Read(ref signals) > signalled), "The first move did not signal.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
            long wakes = watcher.Wakes;
            for (int step = 1; step <= 40; step++)
            {
                RealInput.DragTo(point.X - 30 + step, point.Y + (step % 2), target.Window);
                Thread.Sleep(2);
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
            long asleep = watcher.Wakes - wakes;
            Assert.True(watcher.Take().Moved);
            Assert.True(await Until(() => Volatile.Read(ref signals) > signalled + 1), "The waiting moves did not signal after the take.");
            Assert.True(watcher.Take().Moved);
            Assert.True(asleep <= 2, $"The watcher woke {asleep} times for 40 moves while activity waited.");
        }
        finally
        {
            Assert.True(await watcher.StopAsync(TestTimeouts.Work), "The watcher thread did not end.");
            PInvoke.SetCursorPos(pointer.X, pointer.Y);
        }
    }
    private static (ushort Page, ushort Usage, uint Flags, nint Target)[] MouseRegistrations() =>
        [.. PointerWatcher.ProcessRegistrations().Where(device => device.Page == 1 && device.Usage == 2)];

    private static async Task<bool> Until(Func<bool> condition, int milliseconds = 5000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds)
            {
                return false;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return true;
    }
}
