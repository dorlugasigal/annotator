using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Annotator.Platform.Capture;

/// <summary>
/// Asks every running source for a frame once per compositor clock tick: the magnifier renders only when invalidated
/// (S-12), and the tick follows the display's refresh rate, also on a still desktop (measured 240 per second on the
/// reference machine). With a frame comparer, a source whose frames change nothing in its leases' areas while no input
/// arrives is asked at most <see cref="CaptureServiceOptions.StillFramesPerSecond"/> times a second, until a frame changes,
/// the pointer moves or input arrives (one GetCursorPos and one GetLastInputInfo per tick), and the thread also wakes when
/// the GPU has finished a comparison, to read it. The thread exists only while a source runs. It waits in <c>DCompositionWaitForCompositorClock</c> with a 100 ms limit;
/// when that returns at once without a tick (the display is off) it waits 250 ms instead, so it never spins. Each pass also
/// checks the sources for stalls and first-frame timeouts, which allocates nothing unless a state changes.
/// </summary>
internal sealed unsafe partial class CompositorPacer : IDisposable
{
    private const uint Stopped = 0;
    private const uint Compared = 1;
    private const uint Tick = 2;
    private const uint WaitTimeout = 0x102;
    private const uint ClockTimeoutMs = 100;
    private const int OffBackoffMs = 250;
    private const int FallbackPeriodMs = 16;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly Thread thread;
    private readonly ManualResetEvent stop = new(false);
    private readonly AutoResetEvent completion;
    private readonly WaitHandle[] waits;
    private readonly long minimumTicks;
    private readonly long stallTicks;
    private readonly long firstFrameTicks;
    private readonly bool comparing;
    private readonly int stillFrames;
    private readonly long stillPeriod;
    private System.Drawing.Point pointer = new(int.MinValue, int.MinValue);
    private uint lastInput;
    private CaptureSource[] sources = [];
    private long ticks;
    private long requests;
    private long completions;

    /// <param name="completed">The event Windows sets when the GPU has finished a frame comparison; the service owns it.</param>
    public CompositorPacer(CaptureServiceOptions options, AutoResetEvent completed)
    {
        completion = completed;
        waits = [stop, completed];
        minimumTicks = options.MaximumFramesPerSecond is { } rate
            ? Math.Max(0, (long)(Stopwatch.Frequency / rate) - Stopwatch.Frequency / 1000)
            : 0;
        stallTicks = (long)(options.StallThreshold.TotalSeconds * Stopwatch.Frequency);
        firstFrameTicks = (long)(options.FirstFrameTimeout.TotalSeconds * Stopwatch.Frequency);
        comparing = options.FrameComparer is not null;
        stillFrames = options.StillFrames;
        // A little under the period, so a tick that comes early by the clock's jitter still asks.
        stillPeriod = Math.Max(0, (long)(Stopwatch.Frequency / options.StillFramesPerSecond) - Stopwatch.Frequency / 1000);
        thread = new Thread(Run) { IsBackground = true, Name = "Annotator capture pacer" };
        thread.Start();
    }

    /// <summary>Compositor clock ticks seen, frame requests made and comparison wake-ups, for measurements.</summary>
    public (long Ticks, long Requests) Counters => (Interlocked.Read(ref ticks), Interlocked.Read(ref requests));

    /// <summary>Times the GPU woke the pacer to read finished comparisons, for measurements.</summary>
    public long Completions => Interlocked.Read(ref completions);

    /// <summary>Replaces the sources to pace (capture thread).</summary>
    public void Update(CaptureSource[] running) => Volatile.Write(ref sources, running);

    public void Dispose()
    {
        stop.Set();
        // The wait inside the thread ends within 250 ms; the limit only guards against a wait that ignores the event.
        if (thread.Join(JoinTimeout))
        {
            stop.Dispose();
        }
    }

    private void Run()
    {
        SafeHandle[] held = [stop.SafeWaitHandle, completion.SafeWaitHandle];
        var added = new bool[held.Length];
        try
        {
            for (var index = 0; index < held.Length; index++)
            {
                held[index].DangerousAddRef(ref added[index]);
            }
            var raw = stackalloc nint[2] { held[0].DangerousGetHandle(), held[1].DangerousGetHandle() };
            var clock = true;
            long lastRequest = 0;
            while (true)
            {
                uint result;
                if (clock)
                {
                    try
                    {
                        result = DCompositionWaitForCompositorClock(2, raw, ClockTimeoutMs);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        clock = false;
                        continue;
                    }
                }
                else
                {
                    var signaled = WaitHandle.WaitAny(waits, FallbackPeriodMs);
                    result = signaled == WaitHandle.WaitTimeout ? Tick : (uint)signaled;
                }
                if (result == Stopped)
                {
                    return;
                }
                var current = Volatile.Read(ref sources);
                if (result == Compared)
                {
                    Interlocked.Increment(ref completions);
                    Resolve(current);
                    continue;
                }
                var now = Stopwatch.GetTimestamp();
                if (result == Tick)
                {
                    Interlocked.Increment(ref ticks);
                    if (minimumTicks == 0 || now - lastRequest >= minimumTicks)
                    {
                        lastRequest = now;
                        var input = comparing && InputSeen();
                        foreach (var source in current)
                        {
                            if (!comparing || source.WantsFrame(now, input, stillFrames, stillPeriod))
                            {
                                Interlocked.Increment(ref requests);
                                source.Invalidate();
                            }
                        }
                    }
                }
                else if (result != WaitTimeout)
                {
                    // The display is off or the clock failed: wait instead of spinning, still reading finished comparisons.
                    var signaled = WaitHandle.WaitAny(waits, OffBackoffMs);
                    if (signaled == (int)Stopped)
                    {
                        return;
                    }
                }
                // A backstop: a comparison whose event came before it could be read is read with the next pass.
                Resolve(current);
                foreach (var source in current)
                {
                    source.CheckHealth(now, stallTicks, firstFrameTicks);
                }
            }
        }
        catch (Exception error)
        {
            NativeCallbackErrors.Report(error);
        }
        finally
        {
            for (var index = 0; index < held.Length; index++)
            {
                if (added[index])
                {
                    held[index].DangerousRelease();
                }
            }
        }
    }

    private static void Resolve(CaptureSource[] current)
    {
        foreach (var source in current)
        {
            if (source.HasPendingResults)
            {
                source.ResolvePending();
            }
        }
    }

    /// <summary>
    /// Whether the pointer moved or input (keys, buttons, wheel, touch or pen) arrived since the last tick, since such input
    /// usually changes what the screen shows; a failed read counts as input.
    /// </summary>
    private bool InputSeen()
    {
        var info = new Windows.Win32.UI.Input.KeyboardAndMouse.LASTINPUTINFO
        {
            cbSize = (uint)sizeof(Windows.Win32.UI.Input.KeyboardAndMouse.LASTINPUTINFO),
        };
        var seen = !Windows.Win32.PInvoke.GetLastInputInfo(&info) || info.dwTime != lastInput;
        lastInput = info.dwTime;
        if (!Windows.Win32.PInvoke.GetCursorPos(out var now))
        {
            return true;
        }
        seen |= now != pointer;
        pointer = now;
        return seen;
    }

    [LibraryImport("dcomp.dll")]
    private static partial uint DCompositionWaitForCompositorClock(uint count, nint* handles, uint timeoutInMs);
}
