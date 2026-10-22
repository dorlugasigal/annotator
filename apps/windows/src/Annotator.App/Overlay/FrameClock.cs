using System.Diagnostics;
using Windows.Win32;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.App.Overlay;

/// <summary>
/// Calls <see cref="Tick"/> on the overlay thread once per composition frame while something animates, paced to the
/// display by the compositor clock (WIN-MOT-04), and not at all otherwise (WIN-SYS-07): the helper thread that waits for
/// frames exists only between <see cref="Start"/> and <see cref="Stop"/>. Each wait is bounded, so a display that is off
/// or a locked session (no composition frames) cannot hold anything up: the animation then moves on by time.
/// </summary>
internal sealed class FrameClock : IDisposable
{
    /// <summary>The longest wait for one compositor clock tick.</summary>
    private const uint FrameTimeoutMilliseconds = 100;
    private const uint WaitFailed = 0xFFFFFFFF;

    private readonly SystemDispatcherQueue _queue;
    private readonly Action<double> _tick;
    private readonly Windows.System.DispatcherQueueHandler _onTick;
    private readonly Lock _gate = new();
    private Thread? _thread;
    private bool _running;
    private bool _disposed;
    private int _posted;

    /// <param name="queue">The overlay thread's dispatcher queue; ticks run there.</param>
    /// <param name="tick">Receives the time in seconds on a steady clock.</param>
    public FrameClock(SystemDispatcherQueue queue, Action<double> tick)
    {
        _queue = queue;
        _tick = tick;
        _onTick = OnTick;
    }

    /// <summary>Starts ticking, or keeps ticking. Call it on the overlay thread.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _running = true;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Annotator frame clock" };
                _thread.Start();
            }
        }
    }

    /// <summary>Stops ticking; the helper thread ends after its current wait.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _running = false;
        }
    }

    private unsafe void Run()
    {
        while (true)
        {
            lock (_gate)
            {
                if (!_running)
                {
                    _thread = null;
                    return;
                }
            }
            uint result = PInvoke.DCompositionWaitForCompositorClock(0, null, FrameTimeoutMilliseconds);
            if (result == WaitFailed)
            {
                // No compositor clock: keep moving at about 120 frames per second while the animation lasts.
                Thread.Sleep(8);
            }
            if (Interlocked.Exchange(ref _posted, 1) == 0 && !_queue.TryEnqueue(_onTick))
            {
                // The overlay thread is shutting down.
                lock (_gate)
                {
                    _running = false;
                    _thread = null;
                }
                return;
            }
        }
    }

    private void OnTick()
    {
        Volatile.Write(ref _posted, 0);
        bool running;
        lock (_gate)
        {
            running = _running;
        }
        if (running)
        {
            _tick(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }
    }
}
