using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
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
    private const int MaximumInputPerFrame = 64;

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
            DispatchWaitingInput();
            _tick(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// Handles the pointer and key messages that wait on the overlay thread before the frame. Windows hands out posted
    /// messages, and so every tick, before input: while an animation ran, the overlays got a pointer move only now and then,
    /// up to 400 ms apart, though the thread was idle most of the time, so the laser pointer's trail jumped in long straight
    /// lines. A bounded number per frame, so a flood of input cannot hold the frame up.
    /// </summary>
    private static unsafe void DispatchWaitingInput()
    {
        // QS_TOUCH and QS_POINTER (0x0800, 0x1000) are missing from the generated flags.
        const uint Input = (uint)QUEUE_STATUS_FLAGS.QS_INPUT | 0x0800 | 0x1000;
        var remove = (PEEK_MESSAGE_REMOVE_TYPE)((uint)PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE | Input << 16);
        MSG message;
        for (int count = 0; count < MaximumInputPerFrame && PInvoke.PeekMessage(&message, HWND.Null, 0, 0, remove); count++)
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }
}
