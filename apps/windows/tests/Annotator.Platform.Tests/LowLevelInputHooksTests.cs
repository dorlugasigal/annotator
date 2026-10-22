using System.Diagnostics;
using Annotator.Platform.Input;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Annotator.Platform.Tests;

/// <summary>
/// Live zoom's low-level hooks (WIN-INP-06, WIN-INP-07). The default tests run the hook thread and its decisions without
/// putting hooks into Windows, so nobody's input is touched; the explicit test installs the real mouse hook and measures it
/// with input that only this test's hook consumes.
/// </summary>
public sealed class LowLevelInputHooksTests
{
    private const nuint TestMarker = 0x7E57;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Mac ZoomGestureMonitor.handle: an unmodified Escape leaves zoom; the tap exists only while zoom does.
    [Fact]
    public async Task EscapeIsConsumedOnlyWhileZoomListensForIt()
    {
        int escapes = 0;
        var hooks = LowLevelInputHooks.StartWithoutHooks(new LowLevelInputOptions { Wheel = _ => false, Escape = () => escapes++, MasksAlt = false });
        try
        {
            hooks.Update(mouseHook: true, keyboardHook: false);
            Assert.False(await hooks.RunAsync(() => hooks.Key(PInvoke.WM_KEYDOWN, VIRTUAL_KEY.VK_ESCAPE)));
            Assert.True(hooks.MouseInstalled);
            Assert.False(hooks.KeyboardInstalled);

            hooks.Update(mouseHook: true, keyboardHook: true);
            Assert.True(await hooks.RunAsync(() => hooks.Key(PInvoke.WM_KEYDOWN, VIRTUAL_KEY.VK_ESCAPE)));
            Assert.True(hooks.KeyboardInstalled);
            // Key repeat and the release of a consumed press are consumed too, and leave zoom only once.
            Assert.True(await hooks.RunAsync(() => hooks.Key(PInvoke.WM_KEYDOWN, VIRTUAL_KEY.VK_ESCAPE)));
            Assert.True(await hooks.RunAsync(() => hooks.Key(PInvoke.WM_KEYUP, VIRTUAL_KEY.VK_ESCAPE)));
            Assert.Equal(1, escapes);
            // Other keys and a lone release reach the apps.
            Assert.False(await hooks.RunAsync(() => hooks.Key(PInvoke.WM_KEYDOWN, VIRTUAL_KEY.VK_A)));
            Assert.False(await hooks.RunAsync(() => hooks.Key(PInvoke.WM_KEYUP, VIRTUAL_KEY.VK_ESCAPE)));

            hooks.Update(mouseHook: true, keyboardHook: false);
            await hooks.RunAsync(() => { });
            Assert.False(hooks.KeyboardInstalled);
            Assert.False(await hooks.RunAsync(() => hooks.Key(PInvoke.WM_KEYDOWN, VIRTUAL_KEY.VK_ESCAPE)));
            Assert.Equal(1, escapes);
        }
        finally
        {
            Assert.True(await hooks.StopAsync(Timeout));
        }
    }

    // WIN-INP-06: the hook consumes only the scroll events the app uses, and only while it listens.
    [Fact]
    public async Task WheelTurnsAreConsumedOnlyWhenTheAppUsesThem()
    {
        var seen = new List<LowLevelWheel>();
        var hooks = LowLevelInputHooks.StartWithoutHooks(new LowLevelInputOptions
        {
            Wheel = wheel =>
            {
                seen.Add(wheel);
                return wheel.Extra == TestMarker && wheel.Delta > 0;
            },
            MasksAlt = false,
        });
        try
        {
            Assert.False(await hooks.RunAsync(() => hooks.Turn(120, 10, 20, TestMarker)));
            hooks.Update(mouseHook: true, keyboardHook: false);
            Assert.True(await hooks.RunAsync(() => hooks.Turn(120, 10, 20, TestMarker)));
            Assert.False(await hooks.RunAsync(() => hooks.Turn(-240, 10, 20, TestMarker)));
            Assert.False(await hooks.RunAsync(() => hooks.Turn(120, 10, 20, 0)));
            Assert.Equal(3, seen.Count);
            Assert.Equal((120, 10, 20), (seen[0].Delta, seen[0].X, seen[0].Y));
            Assert.Equal(-240, seen[1].Delta);
            Assert.Equal(1, hooks.Statistics.Consumed);
            hooks.Update(mouseHook: false, keyboardHook: false);
            Assert.False(await hooks.RunAsync(() => hooks.Turn(120, 10, 20, TestMarker)));
            Assert.False(hooks.MouseInstalled);
        }
        finally
        {
            Assert.True(await hooks.StopAsync(Timeout));
        }
    }

    [Fact]
    public async Task OnlyOneInstanceListensAtATime()
    {
        var first = LowLevelInputHooks.StartWithoutHooks(new LowLevelInputOptions { Wheel = _ => false });
        try
        {
            Assert.Throws<InvalidOperationException>(() => LowLevelInputHooks.StartWithoutHooks(new LowLevelInputOptions { Wheel = _ => false }));
        }
        finally
        {
            Assert.True(await first.StopAsync(Timeout));
        }
        var second = LowLevelInputHooks.StartWithoutHooks(new LowLevelInputOptions { Wheel = _ => false });
        Assert.True(await second.StopAsync(Timeout));
    }

    [Fact]
    public void StatisticsReportPercentilesOfRecentCalls()
    {
        var statistics = new LowLevelHookStatistics();
        Assert.Equal(TimeSpan.Zero, statistics.Percentile(0.99));
        long microsecond = Stopwatch.Frequency / 1_000_000;
        for (int index = 1; index <= 100; index++)
        {
            statistics.Record(index * microsecond, index == 100 ? 40 : 0);
        }
        Assert.Equal(100, statistics.Calls);
        Assert.Equal(40, statistics.MaxLatenessMilliseconds);
        Assert.InRange(statistics.Percentile(0.99).TotalMicroseconds, 98.5, 99.5);
        Assert.InRange(statistics.Percentile(0.5).TotalMicroseconds, 49.5, 50.5);
        statistics.Reset();
        Assert.Equal(0, statistics.Calls);
        Assert.Equal(TimeSpan.Zero, statistics.Percentile(0.99));
    }

    /// <summary>
    /// The real mouse hook, for the WIN-INP-06 budget. Once the hook is shown to receive input (a one-pixel move there and
    /// back), it sends pointer moves that cancel out and marked wheel turns that only this test's hook consumes, so no window
    /// scrolls. Run it explicitly on a quiet desktop; with ANNOTATOR_EVIDENCE_DIR set it writes its timings there.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task RealMouseHookStaysWithinItsBudget()
    {
        long reached = 0;
        var hooks = LowLevelInputHooks.Start(new LowLevelInputOptions
        {
            Wheel = wheel =>
            {
                if (wheel.Extra != TestMarker)
                {
                    return false;
                }
                Volatile.Write(ref reached, Stopwatch.GetTimestamp());
                return true;
            },
            MasksAlt = false,
        });
        try
        {
            hooks.Update(mouseHook: true, keyboardHook: false);
            await hooks.RunAsync(() => { });
            Assert.True(hooks.MouseInstalled);
            // Windows drops input that changes nothing (a move of zero, a turn of zero), so the probe moves one pixel and back.
            Move(1);
            Move(-1);
            Assert.True(await Until(() => hooks.Statistics.Calls >= 2), "The mouse hook received no input, so no wheel turn is sent.");

            hooks.Statistics.Reset();
            const int Events = 2000;
            for (int index = 0; index < Events; index++)
            {
                Move(1);
                Move(-1);
                Turn(120);
            }
            Assert.True(await Until(() => hooks.Statistics.Consumed >= Events), $"Only {hooks.Statistics.Consumed} of {Events} wheel turns reached the hook.");
            TimeSpan p50 = hooks.Statistics.Percentile(0.5), p99 = hooks.Statistics.Percentile(0.99), max = hooks.Statistics.Percentile(1);
            long calls = hooks.Statistics.Calls;

            // From SendInput to the hook's decision, one turn at a time.
            var waits = new List<double>();
            for (int index = 0; index < 300; index++)
            {
                long consumed = hooks.Statistics.Consumed;
                long sent = Stopwatch.GetTimestamp();
                Turn(120);
                Assert.True(await Until(() => hooks.Statistics.Consumed > consumed), "A wheel turn did not reach the hook.");
                waits.Add(Stopwatch.GetElapsedTime(sent, Volatile.Read(ref reached)).TotalMilliseconds);
            }
            waits.Sort();
            string report =
                $"Real WH_MOUSE_LL hook, {DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.MachineName}: {calls} calls ({Events} wheel turns, {2 * Events} moves); " +
                $"time in Annotator's hook p50 {p50.TotalMicroseconds:F1} us, p99 {p99.TotalMicroseconds:F1} us, max {max.TotalMicroseconds:F1} us; " +
                $"SendInput to hook p50 {waits[waits.Count / 2]:F3} ms, p99 {waits[(int)Math.Ceiling(waits.Count * 0.99) - 1]:F3} ms; " +
                $"largest event age at the hook {hooks.Statistics.MaxLatenessMilliseconds} ms; repairs {hooks.Statistics.Reinstalls}.";
            TestContext.Current.TestOutputHelper?.WriteLine(report);
            if (Environment.GetEnvironmentVariable("ANNOTATOR_EVIDENCE_DIR") is { Length: > 0 } evidence)
            {
                File.AppendAllText(Path.Combine(evidence, "zoom-hook-timing.txt"), report + Environment.NewLine);
            }
            Assert.True(p99 < TimeSpan.FromMilliseconds(1), $"The hook's p99 is {p99.TotalMilliseconds:F3} ms.");
        }
        finally
        {
            Assert.True(await hooks.StopAsync(Timeout));
        }
    }
    private static async Task<bool> Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        return true;
    }

    private static void Move(int dx) => Send(new MOUSEINPUT { dx = dx, dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE, dwExtraInfo = TestMarker });

    private static void Turn(int delta) =>
        Send(new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, dwExtraInfo = TestMarker });

    private static unsafe void Send(MOUSEINPUT mouse)
    {
        Span<INPUT> inputs = [new INPUT { type = INPUT_TYPE.INPUT_MOUSE, Anonymous = { mi = mouse } }];
        if (PInvoke.SendInput(inputs, sizeof(INPUT)) != 1)
        {
            throw NativeCallException.FromLastError("SendInput");
        }
    }
}
