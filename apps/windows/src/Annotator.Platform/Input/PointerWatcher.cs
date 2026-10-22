using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Input;

/// <summary>What the pointer did since the last <see cref="PointerWatcher.Take"/>.</summary>
/// <param name="Moved">The pointer moved at least once.</param>
/// <param name="Clicks">How many left or right presses arrived.</param>
/// <param name="Click">Where the last press happened, in physical desktop pixels, or null without one.</param>
/// <param name="ClickTimestamp">The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the last press, or 0.</param>
public readonly record struct PointerActivity(bool Moved, int Clicks, System.Drawing.Point? Click, long ClickTimestamp)
{
    public bool IsEmpty => !Moved && Clicks == 0;
}

/// <summary>The watcher's state. Only <see cref="Watching"/> reports activity.</summary>
public enum PointerWatcherState
{
    Starting,
    Watching,

    /// <summary>The watcher could not start or stopped working; <see cref="PointerWatcher.Failure"/> says why.</summary>
    Failed,

    Stopped,
}

/// <summary>
/// Tells the app when the mouse moves or a left or right button goes down anywhere on the desktop, without a low-level
/// hook (WIN-INP-06, WIN-SYS-07): Spotlight and Cursor halo follow the pointer in Draw and in Use apps. While it exists, a
/// message-only window on its own thread receives raw mouse input with RIDEV_INPUTSINK, which never delays or changes
/// anyone's input; disposing it unregisters the input and ends the thread, so nothing runs while no pointer effect shows.
/// The app hears of activity once until it calls <see cref="Take"/>. Until then the thread sleeps through raw input,
/// which waits in its queue, and <see cref="Take"/> wakes it to read the queue at once: a 1000 Hz mouse costs one wake
/// per take, not one per report (macOS coalesces mouse events the same way).
/// </summary>
/// <remarks>
/// Windows keeps one raw input target per device type for a whole process. Another feature that needs pointer movement
/// must share this watcher rather than register raw mouse input itself; <see cref="Start"/> refuses to take over a
/// registration it did not make.
/// </remarks>
public sealed class PointerWatcher : IDisposable
{
    private const ushort GenericDesktopPage = 0x01;
    private const ushort MouseUsage = 0x02;
    private const ushort LeftDown = 0x0001;
    private const ushort RightDown = 0x0004;
    private const ushort MoveAbsolute = 0x0001;
    private const uint CloseMessage = PInvoke.WM_APP + 1;
    private const uint TakenMessage = PInvoke.WM_APP + 2;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The watcher that holds the process's raw mouse registration, or null.</summary>
    private static readonly Lock s_registration = new();
    private static PointerWatcher? s_registered;

    private readonly Thread? _thread;
    private readonly Action _signal;
    private readonly Action<Exception>? _failed;
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _closed = new(false);
    private nint _window;
    private bool _moved;
    private int _clicks;
    private System.Drawing.Point? _click;
    private long _clickTimestamp;
    private bool _signalled;
    private int _lastAbsoluteX = int.MinValue;
    private int _lastAbsoluteY = int.MinValue;
    private int _state = (int)PointerWatcherState.Starting;
    private int _disposed;

    /// <summary>1 while the thread sleeps through raw input; <see cref="Take"/> then wakes it.</summary>
    private int _sleepsThroughInput;
    private long _wakes;

    private PointerWatcher(Action signal, Action<Exception>? failed, bool registers)
    {
        _signal = signal;
        _failed = failed;
        if (registers)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Annotator pointer watcher" };
        }
        else
        {
            _state = (int)PointerWatcherState.Watching;
            _closed.Set();
        }
    }

    /// <summary>
    /// Starts watching on a new thread. <paramref name="signal"/> runs on that thread when activity arrives after the last
    /// <see cref="Take"/>; it must only hand the news on (for example post to a dispatcher queue). <paramref name="failed"/>
    /// runs on that thread if raw input cannot be registered.
    /// </summary>
    public static PointerWatcher Start(Action signal, Action<Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        var watcher = new PointerWatcher(signal, failed, registers: true);
        watcher._thread!.Start();
        return watcher;
    }

    /// <summary>For tests: a watcher without a thread or raw input, which hears only <see cref="NoteForTest"/>.</summary>
    internal static PointerWatcher StartDetached(Action signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return new PointerWatcher(signal, null, registers: false);
    }

    public PointerWatcherState State => (PointerWatcherState)Volatile.Read(ref _state);

    /// <summary>Why the watcher failed, or null.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// The activity since the last call, which it clears; the next activity signals again. Safe on any thread.
    /// </summary>
    public PointerActivity Take()
    {
        PointerActivity activity;
        lock (_gate)
        {
            activity = new PointerActivity(_moved, _clicks, _click, _clickTimestamp);
            _moved = false;
            _clicks = 0;
            _click = null;
            _clickTimestamp = 0;
            _signalled = false;
        }
        // The thread reads the raw input that waited in its queue and signals again if there was any.
        if (Interlocked.Exchange(ref _sleepsThroughInput, 0) == 1 && Window is var window and not 0)
        {
            PInvoke.PostMessage((HWND)window, TakenMessage, default, default);
        }
        return activity;
    }

    /// <summary>For tests and measurements: how often the watcher thread woke up.</summary>
    internal long Wakes => Interlocked.Read(ref _wakes);

    /// <summary>The pointer's position in physical desktop pixels, or null when Windows cannot say (a secure desktop).</summary>
    public static System.Drawing.Point? CursorPosition() =>
        PInvoke.GetCursorPos(out System.Drawing.Point point) ? point : null;

    /// <summary>Unregisters raw input and ends the thread without waiting. Safe on any thread, more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Volatile.Write(ref _state, (int)PointerWatcherState.Stopped);
        // Open checks the flag after it publishes the window, so one of the two closes it.
        nint window = Interlocked.CompareExchange(ref _window, 0, 0);
        if (window != 0 && !PInvoke.PostMessage((HWND)window, CloseMessage, default, default))
        {
            Failure ??= NativeCallException.FromLastError("PostMessage (stop the pointer watcher)");
        }
    }

    /// <summary>For tests: disposes and waits, at most <paramref name="timeout"/>, for the thread to end.</summary>
    internal Task<bool> StopAsync(TimeSpan timeout)
    {
        Dispose();
        return _thread is { } thread ? Task.Run(() => thread.Join(timeout)) : Task.FromResult(true);
    }

    /// <summary>For tests: the process's raw input registrations, as (usage page, usage, flags, target). Any thread.</summary>
    internal static (ushort Page, ushort Usage, uint Flags, nint Target)[] ProcessRegistrations() => Registrations();

    /// <summary>For tests: the message-only window that receives raw input, or 0.</summary>
    internal nint Window => Interlocked.CompareExchange(ref _window, 0, 0);

    /// <summary>For tests: one raw mouse report as if Windows had delivered it, to a watcher from <see cref="StartDetached"/>.</summary>
    internal void NoteForTest(ushort buttonFlags, ushort moveFlags, int x, int y)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Only a detached pointer watcher takes test reports.");
        }
        Note(buttonFlags, moveFlags, x, y);
    }

    private unsafe void Run()
    {
        if (Open() is not { } sink)
        {
            return;
        }
        MSG message;
        while (true)
        {
            while (PInvoke.PeekMessage(&message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                if (message.message == PInvoke.WM_QUIT)
                {
                    Close(sink);
                    return;
                }
                PInvoke.DispatchMessage(&message);
            }
            QUEUE_STATUS_FLAGS wake = QUEUE_STATUS_FLAGS.QS_POSTMESSAGE | QUEUE_STATUS_FLAGS.QS_SENDMESSAGE;
            if (!SleepsThroughInput())
            {
                wake |= QUEUE_STATUS_FLAGS.QS_RAWINPUT;
            }
            WAIT_EVENT result = PInvoke.MsgWaitForMultipleObjectsEx(0, null, PInvoke.INFINITE, wake, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
            Interlocked.Exchange(ref _sleepsThroughInput, 0);
            Interlocked.Increment(ref _wakes);
            if (result == WAIT_EVENT.WAIT_FAILED)
            {
                Failure ??= NativeCallException.FromLastError("MsgWaitForMultipleObjectsEx (pointer watcher)");
                Volatile.Write(ref _state, (int)PointerWatcherState.Failed);
                Close(sink);
                return;
            }
        }
    }

    /// <summary>
    /// Whether the thread may sleep through raw input: activity waits to be taken, and <see cref="Take"/> will wake it. The
    /// flag goes up before the check, so a take between the two still wakes the thread.
    /// </summary>
    private bool SleepsThroughInput()
    {
        Interlocked.Exchange(ref _sleepsThroughInput, 1);
        lock (_gate)
        {
            if (_signalled)
            {
                return true;
            }
        }
        Interlocked.Exchange(ref _sleepsThroughInput, 0);
        return false;
    }

    /// <summary>On the watcher thread: registers raw input for a new message-only window, or reports why it cannot.</summary>
    private Sink? Open()
    {
        Sink? sink = null;
        try
        {
            // Physical pixels for click positions on every display (WIN-REND-06).
            PInvoke.SetThreadDpiAwarenessContext(Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            bool claimed = Claim();
            if (Volatile.Read(ref _disposed) != 0)
            {
                Release();
                _closed.Set();
                return null;
            }
            if (!claimed)
            {
                throw new InvalidOperationException("The previous pointer watcher did not stop within a second.");
            }
            foreach ((ushort page, ushort usage, uint _, nint _) in Registrations())
            {
                // One registration per process: another one, even one that follows the keyboard focus, belongs to another
                // part, which would stop hearing the mouse once the watcher took the registration over.
                if (page == GenericDesktopPage && usage == MouseUsage)
                {
                    throw new InvalidOperationException("Another part of Annotator already reads raw mouse input, so the pointer effects cannot follow the pointer.");
                }
            }
            sink = new Sink(this);
            Register(sink.Handle, RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK, "RegisterRawInputDevices(mouse, input sink)");
            Interlocked.Exchange(ref _window, (nint)sink.Handle);
            Interlocked.CompareExchange(ref _state, (int)PointerWatcherState.Watching, (int)PointerWatcherState.Starting);
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
            {
                Close(sink);
            }
            return sink;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException)
        {
            if (sink is not null)
            {
                Destroy(sink);
            }
            Release();
            Failure = error;
            Volatile.Write(ref _state, (int)PointerWatcherState.Failed);
            _closed.Set();
            _failed?.Invoke(error);
            return null;
        }
    }

    /// <summary>
    /// Claims the process's raw mouse registration for this watcher. A watcher that is still closing on its own thread gives
    /// it back first; false when none came back within <see cref="CloseTimeout"/>.
    /// </summary>
    private bool Claim()
    {
        long deadline = Environment.TickCount64 + (long)CloseTimeout.TotalMilliseconds;
        while (true)
        {
            PointerWatcher? previous;
            lock (s_registration)
            {
                previous = s_registered;
                if (previous is null)
                {
                    s_registered = this;
                    return true;
                }
            }
            long left = deadline - Environment.TickCount64;
            // A watcher releases its claim before it sets _closed, so each wake finds a new holder or none.
            if (left <= 0 || !previous._closed.Wait(TimeSpan.FromMilliseconds(left)))
            {
                return false;
            }
        }
    }

    private void Release()
    {
        lock (s_registration)
        {
            if (s_registered == this)
            {
                s_registered = null;
            }
        }
    }

    /// <summary>On the watcher thread: unregisters raw input, destroys the window and ends the message loop. Once.</summary>
    private void Close(Sink sink)
    {
        if (Interlocked.Exchange(ref _window, 0) == 0)
        {
            return;
        }
        try
        {
            Register(HWND.Null, RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE, "RegisterRawInputDevices(mouse, remove)");
        }
        catch (NativeCallException error)
        {
            Failure ??= error;
        }
        Release();
        Destroy(sink);
        _closed.Set();
        PInvoke.PostQuitMessage(0);
    }

    private void Destroy(Sink sink)
    {
        try
        {
            sink.Destroy();
        }
        catch (NativeCallException error)
        {
            Failure ??= error;
        }
    }

    private static unsafe void Register(HWND target, RAWINPUTDEVICE_FLAGS flags, string operation)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = GenericDesktopPage,
            usUsage = MouseUsage,
            dwFlags = flags,
            hwndTarget = target,
        };
        if (!PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)))
        {
            throw NativeCallException.FromLastError(operation);
        }
    }

    private static unsafe (ushort Page, ushort Usage, uint Flags, nint Target)[] Registrations()
    {
        uint count = 0;
        uint size = (uint)sizeof(RAWINPUTDEVICE);
        PInvoke.GetRegisteredRawInputDevices(null, &count, size);
        if (count == 0)
        {
            return [];
        }
        var devices = new RAWINPUTDEVICE[count];
        fixed (RAWINPUTDEVICE* first = devices)
        {
            uint read = PInvoke.GetRegisteredRawInputDevices(first, &count, size);
            if (read == uint.MaxValue)
            {
                throw NativeCallException.FromLastError("GetRegisteredRawInputDevices");
            }
            return [.. devices.Take((int)read).Select(device => (device.usUsagePage, device.usUsage, (uint)device.dwFlags, (nint)device.hwndTarget.Value))];
        }
    }

    /// <summary>One raw mouse report, on the watcher thread; <paramref name="at"/> is the pointer position then, if known.</summary>
    private void Note(ushort buttonFlags, ushort moveFlags, int x, int y, System.Drawing.Point? at = null)
    {
        bool clicked = (buttonFlags & (LeftDown | RightDown)) != 0;
        // Relative reports carry how far the mouse moved; absolute ones (tablets, remote sessions) where it is.
        bool moved = (moveFlags & MoveAbsolute) != 0 ? x != _lastAbsoluteX || y != _lastAbsoluteY : x != 0 || y != 0;
        if ((moveFlags & MoveAbsolute) != 0)
        {
            _lastAbsoluteX = x;
            _lastAbsoluteY = y;
        }
        if (!clicked && !moved)
        {
            return;
        }
        System.Drawing.Point? position = clicked ? at ?? CursorPosition() : null;
        long now = clicked ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        bool signal;
        lock (_gate)
        {
            _moved |= moved;
            if (clicked)
            {
                _clicks++;
                _click = position;
                _clickTimestamp = now;
            }
            signal = !_signalled;
            _signalled = true;
        }
        if (signal && State == PointerWatcherState.Watching)
        {
            _signal();
        }
    }

    /// <summary>The message-only window that raw input reaches.</summary>
    private sealed unsafe class Sink : NativeWindow
    {
        private readonly PointerWatcher _owner;

        public Sink(PointerWatcher owner)
        {
            _owner = owner;
            CreateHandle("AnnotatorPointerWatcher", 0, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, "CreateWindowEx (pointer watcher)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (pointer watcher)");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            if (message == PInvoke.WM_INPUT)
            {
                RAWINPUT input;
                uint size = (uint)sizeof(RAWINPUT);
                uint read = PInvoke.GetRawInputData(new HRAWINPUT(lParam.Value), RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &input, &size, (uint)sizeof(RAWINPUTHEADER));
                if (read != uint.MaxValue && read >= sizeof(RAWINPUTHEADER) && input.header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE)
                {
                    RAWMOUSE mouse = input.data.mouse;
                    // Where the pointer was when the report arrived, which may be a frame before this thread reads it.
                    uint at = PInvoke.GetMessagePos();
                    var position = new System.Drawing.Point((short)(at & 0xFFFF), (short)(at >> 16));
                    _owner.Note(mouse.Anonymous.Anonymous.usButtonFlags, (ushort)mouse.usFlags, mouse.lLastX, mouse.lLastY, position);
                }
            }
            else if (message == CloseMessage)
            {
                _owner.Close(this);
                return default;
            }
            else if (message == TakenMessage)
            {
                // Only wakes the thread, which then reads the raw input that waited.
                return default;
            }
            // WM_INPUT also needs DefWindowProc, so Windows can clean up the input.
            return base.WndProc(message, wParam, lParam);
        }
    }
}
