using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Annotator.Platform.Threading;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Input;

/// <summary>A turn of the vertical mouse wheel, as the low-level mouse hook sees it before any window does.</summary>
/// <param name="Delta">Wheel units, 120 per notch; positive turns the wheel away from the person.</param>
/// <param name="X">The pointer, in physical desktop pixels.</param>
/// <param name="Y">The pointer, in physical desktop pixels.</param>
/// <param name="Extra">The event's extra information, which marks input that a program sent.</param>
public readonly record struct LowLevelWheel(int Delta, int X, int Y, bool Alt, bool Control, bool Shift, bool Windows, nuint Extra);

/// <summary>Why <see cref="LowLevelInputHooks"/> stopped on its own.</summary>
/// <param name="Error">What Windows reported when it refused a hook.</param>
public sealed record LowLevelHooksStopped(NativeCallException Error);

/// <summary>What <see cref="LowLevelInputHooks"/> does with the input it sees. Every callback runs on the hook thread.</summary>
public sealed record LowLevelInputOptions
{
    /// <summary>
    /// Decides whether a wheel turn is consumed, so no window receives it. It runs inside the hook: it must only read state
    /// that is ready and hand the turn on (for example post it to the app thread), and never block or throw.
    /// </summary>
    public required Func<LowLevelWheel, bool> Wheel { get; init; }

    /// <summary>The pointer moved to (x, y), in physical desktop pixels; it runs inside the hook, as <see cref="Wheel"/> does.</summary>
    public Action<int, int>? Moved { get; init; }

    /// <summary>The keyboard hook consumed an unmodified Escape press; it runs inside the hook, as <see cref="Wheel"/> does.</summary>
    public Action? Escape { get; init; }

    /// <summary>Windows refused a hook, so both hooks are off until the next <see cref="LowLevelInputHooks.Update"/> that turns one on.</summary>
    public Action<LowLevelHooksStopped>? Stopped { get; init; }

    /// <summary>
    /// After a consumed wheel turn with Alt held, sends one press of an unassigned key (0xE8) once per Alt press, so releasing
    /// Alt does not open the front app's menu or ribbon key tips (WIN-INP-06). Tests turn it off.
    /// </summary>
    public bool MasksAlt { get; init; } = true;

    /// <summary>
    /// An event that reaches the hook this much later than it happened shows that the hook thread stalled long enough for
    /// Windows to give up on a call, and Windows silently removes a low-level hook that times out: the hooks are installed
    /// again at once.
    /// </summary>
    public TimeSpan StallLimit { get; init; } = TimeSpan.FromMilliseconds(200);
}

/// <summary>How long the hooks take, for the WIN-INP-06 budget (p99 under 1 ms). Read on any thread.</summary>
public sealed class LowLevelHookStatistics
{
    private const int Capacity = 1 << 14;
    private readonly long[] ticks = new long[Capacity];
    private long recorded;
    private long baseline;
    private long consumed;
    private long masks;
    private long reinstalls;
    private long maxLateness;

    /// <summary>Hook calls since <see cref="Reset"/>.</summary>
    public long Calls => Interlocked.Read(ref recorded) - Interlocked.Read(ref baseline);

    /// <summary>Events the hooks consumed.</summary>
    public long Consumed => Interlocked.Read(ref consumed);

    /// <summary>Unassigned key presses sent so that releasing Alt opens no menu.</summary>
    public long AltMasks => Interlocked.Read(ref masks);

    /// <summary>Times the hooks were installed again after a stall.</summary>
    public long Reinstalls => Interlocked.Read(ref reinstalls);

    /// <summary>The longest time an event waited before the hook ran, in milliseconds (Windows' event clock).</summary>
    public long MaxLatenessMilliseconds => Interlocked.Read(ref maxLateness);

    /// <summary>The time a hook call took inside Annotator at <paramref name="fraction"/> (0.99 for p99) of the recent calls.</summary>
    public TimeSpan Percentile(double fraction)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1);
        long[] samples = Samples();
        if (samples.Length == 0)
        {
            return TimeSpan.Zero;
        }
        Array.Sort(samples);
        int index = Math.Clamp((int)Math.Ceiling(fraction * samples.Length) - 1, 0, samples.Length - 1);
        return Stopwatch.GetElapsedTime(0, samples[index]);
    }

    /// <summary>Starts the counts and timings again.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref baseline, Interlocked.Read(ref recorded));
        Interlocked.Exchange(ref consumed, 0);
        Interlocked.Exchange(ref masks, 0);
        Interlocked.Exchange(ref maxLateness, 0);
    }

    // Written only on the hook thread.
    internal void Record(long elapsed, long lateness)
    {
        long index = Interlocked.Read(ref recorded);
        ticks[index & (Capacity - 1)] = elapsed;
        Interlocked.Exchange(ref recorded, index + 1);
        if (lateness > Interlocked.Read(ref maxLateness))
        {
            Interlocked.Exchange(ref maxLateness, lateness);
        }
    }

    internal void Consume() => Interlocked.Increment(ref consumed);

    internal void Mask() => Interlocked.Increment(ref masks);

    internal void Reinstall() => Interlocked.Increment(ref reinstalls);

    private long[] Samples()
    {
        long end = Interlocked.Read(ref recorded);
        long start = Math.Max(Interlocked.Read(ref baseline), end - Capacity);
        var samples = new long[Math.Max(0, end - start)];
        for (long index = start; index < end; index++)
        {
            samples[index - start] = Volatile.Read(ref ticks[index & (Capacity - 1)]);
        }
        return samples;
    }
}

/// <summary>
/// The low-level mouse and keyboard hooks of live zoom (WIN-INP-06, WIN-INP-07), the Windows form of the Mac's event taps:
/// Alt+scroll changes zoom over any app, the pointer is followed for the zoom viewport and the lens, and an unmodified
/// Escape leaves zoom. Both hooks run on this object's own thread with a message loop at high priority; each call only
/// reads the state it is given, hands the event on and returns. The app turns each hook on and off with
/// <see cref="Update"/> for exactly the lifetimes in WIN-INP-06 and WIN-INP-07. Windows needs no permission for them
/// (WIN-INP-08); it gives no notice when it removes a hook that took too long, so a stall makes the hooks start again.
/// </summary>
/// <remarks>
/// One instance at a time per process: the hook procedures are static. Hooks of a process that is not elevated see no input
/// meant for elevated windows (DV-3).
/// </remarks>
public sealed class LowLevelInputHooks : IDisposable
{
    /// <summary>An unassigned virtual key (AutoHotkey's menu mask key): apps ignore it, and Alt is no longer pressed alone.</summary>
    private const ushort MaskKey = 0xE8;

    /// <summary>Marks the mask key presses this class sends.</summary>
    internal const nuint Marker = 0x416E6E5A;

    private static readonly TimeSpan AltPoll = TimeSpan.FromMilliseconds(25);
    private static LowLevelInputHooks? s_current;

    private readonly LowLevelInputOptions options;
    private readonly DispatcherThread thread;
    private readonly bool installs;
    private readonly DispatcherQueueHandler maskAlt;
    private readonly DispatcherQueueHandler apply;
    private readonly DispatcherQueueHandler refresh;
    private readonly DispatcherQueueHandler reinstall;

    // Hook thread.
    private HHOOK mouse;
    private HHOOK keyboard;
    private DispatcherQueueTimer? altWatch;
    private DispatcherQueueTimer? escapeWatch;
    private bool altMasked;
    private bool escapeHeld;
    private bool reinstallQueued;
    private bool closed;

    // Any thread.
    private volatile bool wantMouse;
    private volatile bool wantKeyboard;
    private volatile bool mouseInstalled;
    private volatile bool keyboardInstalled;
    private int disposed;

    private LowLevelInputHooks(LowLevelInputOptions options, DispatcherThread thread, bool installs)
    {
        this.options = options;
        this.thread = thread;
        this.installs = installs;
        maskAlt = MaskAlt;
        apply = Apply;
        refresh = () => Reinstall(stalled: false);
        reinstall = () => Reinstall(stalled: true);
    }

    /// <summary>Starts the hook thread with both hooks off.</summary>
    /// <exception cref="InvalidOperationException">Another instance is running in this process.</exception>
    public static LowLevelInputHooks Start(LowLevelInputOptions options) => Start(options, installs: true);

    /// <summary>For tests: the hook thread and its decisions without hooks in Windows; <see cref="Key"/> and <see cref="Turn"/> feed events.</summary>
    internal static LowLevelInputHooks StartWithoutHooks(LowLevelInputOptions options) => Start(options, installs: false);

    private static LowLevelInputHooks Start(LowLevelInputOptions options, bool installs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Wheel);
        DispatcherThread thread = DispatcherThread.Start("Annotator zoom input");
        var hooks = new LowLevelInputHooks(options, thread, installs);
        if (Interlocked.CompareExchange(ref s_current, hooks, null) is not null)
        {
            _ = thread.ShutdownAsync();
            throw new InvalidOperationException("Live zoom input is already running in this process.");
        }
        if (!thread.Queue.TryEnqueue(hooks.Open))
        {
            Interlocked.CompareExchange(ref s_current, null, hooks);
            _ = thread.ShutdownAsync();
            throw new InvalidOperationException("The zoom input thread did not start.");
        }
        return hooks;
    }

    public LowLevelHookStatistics Statistics { get; } = new();

    /// <summary>Whether the mouse hook is installed now.</summary>
    public bool MouseInstalled => mouseInstalled;

    /// <summary>Whether the keyboard hook is installed now.</summary>
    public bool KeyboardInstalled => keyboardInstalled;

    /// <summary>
    /// Turns the mouse hook (Alt+scroll and the pointer) and the keyboard hook (Escape) on or off. Safe on any thread;
    /// the change happens on the hook thread. Turning the keyboard hook on also installs the mouse hook again, so a hook
    /// that Windows removed unnoticed works again when zoom starts.
    /// </summary>
    public void Update(bool mouseHook, bool keyboardHook)
    {
        if (Volatile.Read(ref disposed) != 0 || (mouseHook == wantMouse && keyboardHook == wantKeyboard))
        {
            return;
        }
        bool fresh = keyboardHook && !wantKeyboard;
        wantMouse = mouseHook;
        wantKeyboard = keyboardHook;
        thread.Queue.TryEnqueue(fresh ? refresh : apply);
    }

    /// <summary>The hooks were not called when they should have been (input reached a window instead): install them again.</summary>
    public void Revive()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            thread.Queue.TryEnqueue(reinstall);
        }
    }

    /// <summary>For tests: runs <paramref name="work"/> on the hook thread after the changes queued before it.</summary>
    internal Task<T> RunAsync<T>(Func<T> work) => thread.RunAsync(work);

    internal Task RunAsync(Action work) => thread.RunAsync(work);

    /// <summary>For tests, on the hook thread: a key event as the keyboard hook would see it; true when it is consumed.</summary>
    internal unsafe bool Key(uint message, VIRTUAL_KEY key)
    {
        var info = new KBDLLHOOKSTRUCT { vkCode = (uint)key, time = PInvoke.GetTickCount() };
        return !keyboard.IsNull && OnKey(message, &info);
    }

    /// <summary>For tests, on the hook thread: a wheel turn as the mouse hook would see it; true when it is consumed.</summary>
    internal unsafe bool Turn(int delta, int x, int y, nuint extra)
    {
        var info = new MSLLHOOKSTRUCT
        {
            pt = new System.Drawing.Point(x, y),
            mouseData = unchecked((uint)(delta << 16)),
            dwExtraInfo = extra,
            time = PInvoke.GetTickCount(),
        };
        return !mouse.IsNull && OnMouse(PInvoke.WM_MOUSEWHEEL, &info);
    }

    /// <summary>Removes both hooks and ends the thread without waiting. Safe on any thread, more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        wantMouse = false;
        wantKeyboard = false;
        if (!thread.Queue.TryEnqueue(Close))
        {
            Interlocked.CompareExchange(ref s_current, null, this);
        }
        _ = thread.ShutdownAsync();
    }

    /// <summary>For tests: disposes and waits, at most <paramref name="timeout"/>, for the hooks to go.</summary>
    internal async Task<bool> StopAsync(TimeSpan timeout)
    {
        Dispose();
        Task shutdown = thread.ShutdownAsync();
        return await Task.WhenAny(shutdown, Task.Delay(timeout)).ConfigureAwait(false) == shutdown;
    }

    private void Open()
    {
        // The hooks hold up every mouse and key event while they run.
        PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_PRIORITY_HIGHEST);
        Apply();
    }

    private void Close()
    {
        closed = true;
        altWatch?.Stop();
        altWatch = null;
        escapeWatch?.Stop();
        escapeWatch = null;
        Remove(ref mouse);
        Remove(ref keyboard);
        mouseInstalled = false;
        keyboardInstalled = false;
        Interlocked.CompareExchange(ref s_current, null, this);
    }

    /// <summary>Installs and removes hooks to match what the app wants (hook thread).</summary>
    private void Apply()
    {
        if (closed)
        {
            return;
        }
        try
        {
            Set(ref mouse, wantMouse, WINDOWS_HOOK_ID.WH_MOUSE_LL);
            mouseInstalled = !mouse.IsNull;
            // An Escape press that left zoom keeps the hook until its release, so the front app never gets a lone key-up.
            bool waitsForRelease = !wantKeyboard && escapeHeld && Down(VIRTUAL_KEY.VK_ESCAPE);
            if (!wantKeyboard && !waitsForRelease)
            {
                escapeHeld = false;
            }
            Set(ref keyboard, wantKeyboard || waitsForRelease, WINDOWS_HOOK_ID.WH_KEYBOARD_LL);
            keyboardInstalled = !keyboard.IsNull;
            WatchEscape(waitsForRelease);
        }
        catch (NativeCallException error)
        {
            Remove(ref mouse);
            Remove(ref keyboard);
            mouseInstalled = false;
            keyboardInstalled = false;
            wantMouse = false;
            wantKeyboard = false;
            options.Stopped?.Invoke(new LowLevelHooksStopped(error));
        }
    }

    /// <summary>
    /// Removes the hooks and installs them again, which also moves them to the front of the hook chain.
    /// <paramref name="stalled"/> counts it as a repair.
    /// </summary>
    private void Reinstall(bool stalled)
    {
        reinstallQueued = false;
        if (closed)
        {
            return;
        }
        if (stalled && (!mouse.IsNull || !keyboard.IsNull))
        {
            Statistics.Reinstall();
        }
        Remove(ref mouse);
        Remove(ref keyboard);
        Apply();
    }

    private unsafe void Set(ref HHOOK hook, bool wanted, WINDOWS_HOOK_ID kind)
    {
        if (wanted == !hook.IsNull)
        {
            return;
        }
        if (!wanted)
        {
            Remove(ref hook);
            return;
        }
        if (!installs)
        {
            hook = (HHOOK)(nint)(kind == WINDOWS_HOOK_ID.WH_MOUSE_LL ? 1 : 2);
            return;
        }
        var module = (HINSTANCE)PInvoke.GetModuleHandle((Windows.Win32.Foundation.PCWSTR)null).Value;
        hook = kind == WINDOWS_HOOK_ID.WH_MOUSE_LL
            ? PInvoke.SetWindowsHookEx(kind, &MouseProcedure, module, 0)
            : PInvoke.SetWindowsHookEx(kind, &KeyboardProcedure, module, 0);
        if (hook.IsNull)
        {
            throw NativeCallException.FromLastError($"SetWindowsHookEx({kind})");
        }
    }

    private void Remove(ref HHOOK hook)
    {
        if (!hook.IsNull)
        {
            if (installs)
            {
                // False only for a hook Windows already removed; nothing is left to undo either way.
                PInvoke.UnhookWindowsHookEx(hook);
            }
            hook = HHOOK.Null;
        }
    }

    private static bool Down(VIRTUAL_KEY key) => (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT MouseProcedure(int code, WPARAM wParam, LPARAM lParam)
    {
        bool consume = false;
        if (code >= 0 && Volatile.Read(ref s_current) is { } hooks)
        {
            long start = Stopwatch.GetTimestamp();
            var info = (MSLLHOOKSTRUCT*)lParam.Value;
            try
            {
                consume = hooks.OnMouse((uint)wParam.Value, info);
            }
            catch (Exception error)
            {
                NativeCallbackErrors.Report(error);
            }
            hooks.Finish(start, info->time);
        }
        return consume ? (LRESULT)1 : PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT KeyboardProcedure(int code, WPARAM wParam, LPARAM lParam)
    {
        bool consume = false;
        if (code >= 0 && Volatile.Read(ref s_current) is { } hooks)
        {
            long start = Stopwatch.GetTimestamp();
            var info = (KBDLLHOOKSTRUCT*)lParam.Value;
            try
            {
                consume = hooks.OnKey((uint)wParam.Value, info);
            }
            catch (Exception error)
            {
                NativeCallbackErrors.Report(error);
            }
            hooks.Finish(start, info->time);
        }
        return consume ? (LRESULT)1 : PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    private unsafe bool OnMouse(uint message, MSLLHOOKSTRUCT* info)
    {
        switch (message)
        {
            case PInvoke.WM_MOUSEMOVE:
                options.Moved?.Invoke(info->pt.X, info->pt.Y);
                return false;
            case PInvoke.WM_MOUSEWHEEL when wantMouse:
                bool alt = Down(VIRTUAL_KEY.VK_MENU);
                var wheel = new LowLevelWheel((short)(info->mouseData >> 16), info->pt.X, info->pt.Y, alt, Down(VIRTUAL_KEY.VK_CONTROL),
                                              Down(VIRTUAL_KEY.VK_SHIFT), Down(VIRTUAL_KEY.VK_LWIN) || Down(VIRTUAL_KEY.VK_RWIN), info->dwExtraInfo);
                if (!options.Wheel(wheel))
                {
                    return false;
                }
                Statistics.Consume();
                if (alt && options.MasksAlt && !altMasked)
                {
                    altMasked = true;
                    thread.Queue.TryEnqueue(maskAlt);
                }
                return true;
            default:
                return false;
        }
    }

    private unsafe bool OnKey(uint message, KBDLLHOOKSTRUCT* info)
    {
        if (info->vkCode != (uint)VIRTUAL_KEY.VK_ESCAPE)
        {
            return false;
        }
        bool down = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
        if (!down)
        {
            if (!escapeHeld)
            {
                return false;
            }
            escapeHeld = false;
            if (!wantKeyboard)
            {
                thread.Queue.TryEnqueue(apply);
            }
            return true;
        }
        if (escapeHeld)
        {
            // Key repeat of the press that was consumed.
            return true;
        }
        if (!wantKeyboard || Down(VIRTUAL_KEY.VK_CONTROL) || Down(VIRTUAL_KEY.VK_MENU) || Down(VIRTUAL_KEY.VK_SHIFT)
            || Down(VIRTUAL_KEY.VK_LWIN) || Down(VIRTUAL_KEY.VK_RWIN))
        {
            return false;
        }
        escapeHeld = true;
        options.Escape?.Invoke();
        return true;
    }

    /// <summary>Records the call's time; an event that waited past the stall limit makes the hooks start again.</summary>
    private void Finish(long start, uint time)
    {
        long lateness = unchecked((int)(PInvoke.GetTickCount() - time));
        Statistics.Record(Stopwatch.GetTimestamp() - start, Math.Max(0, lateness));
        if (lateness > options.StallLimit.TotalMilliseconds && !reinstallQueued)
        {
            reinstallQueued = true;
            thread.Queue.TryEnqueue(reinstall);
        }
    }

    /// <summary>Presses and releases the mask key once while Alt is held, then waits for Alt's release (hook thread).</summary>
    private unsafe void MaskAlt()
    {
        if (closed || !Down(VIRTUAL_KEY.VK_MENU))
        {
            altMasked = false;
            return;
        }
        Span<INPUT> inputs =
        [
            new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = { ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)MaskKey, dwExtraInfo = Marker } } },
            new INPUT
            {
                type = INPUT_TYPE.INPUT_KEYBOARD,
                Anonymous = { ki = new KEYBDINPUT { wVk = (VIRTUAL_KEY)MaskKey, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP, dwExtraInfo = Marker } },
            },
        ];
        if (PInvoke.SendInput(inputs, sizeof(INPUT)) == 2)
        {
            Statistics.Mask();
        }
        if (altWatch is null)
        {
            altWatch = thread.Queue.CreateTimer();
            altWatch.Interval = AltPoll;
            altWatch.IsRepeating = true;
            altWatch.Tick += (_, _) => WatchAlt();
        }
        altWatch.Start();
    }

    /// <summary>Checks for the release of an Escape press that the hook still waits for, in case the hook misses it.</summary>
    private void WatchEscape(bool waiting)
    {
        if (!waiting)
        {
            escapeWatch?.Stop();
            return;
        }
        if (escapeWatch is null)
        {
            escapeWatch = thread.Queue.CreateTimer();
            escapeWatch.Interval = TimeSpan.FromMilliseconds(50);
            escapeWatch.IsRepeating = true;
            escapeWatch.Tick += (_, _) => Apply();
        }
        escapeWatch.Start();
    }

    /// <summary>Alt was released: the next Alt+scroll masks again.</summary>
    private void WatchAlt()
    {
        if (closed || !Down(VIRTUAL_KEY.VK_MENU))
        {
            altMasked = false;
            altWatch?.Stop();
        }
    }
}
