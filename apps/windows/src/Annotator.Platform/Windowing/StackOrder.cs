using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Windowing;

/// <summary>Puts visible registered windows back on top of the topmost band in layer order.</summary>
internal static class StackOrder
{
    private const int MaxWindowsWalked = 100_000;

    /// <summary>
    /// Moves each visible window to the top of the topmost band, from the lowest layer to the
    /// highest, so the final order matches <see cref="WindowLayer"/>. Windows of other threads
    /// are moved asynchronously so this thread never waits on them. <see cref="WindowLayer.Ordinary"/>
    /// windows stay where they are. Returns the failures.
    /// </summary>
    public static IReadOnlyList<NativeCallException> Restore(WindowRegistry registry)
    {
        List<NativeCallException>? failures = null;
        uint thisThread = PInvoke.GetCurrentThreadId();
        foreach (RegisteredWindow window in registry.Windows.OrderBy(window => window.Layer))
        {
            var hwnd = (HWND)window.Handle;
            if (window.Layer == WindowLayer.Ordinary || !PInvoke.IsWindowVisible(hwnd))
            {
                continue;
            }
            SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
            if (window.ThreadId != thisThread)
            {
                flags |= SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;
            }
            if (!PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags))
            {
                (failures ??= []).Add(NativeCallException.FromLastError($"SetWindowPos(HWND_TOPMOST) for window 0x{window.Handle:X}"));
            }
        }
        return failures ?? [];
    }

    /// <summary>
    /// Where a window of <paramref name="layer"/> may go when something moves it to <paramref name="insertAfter"/>:
    /// directly below the lowest visible, topmost registered window of a higher layer, when the requested place is above
    /// that window. Activation moves a window to the top of its band (a click, or focus given back after a popover), which
    /// would put a canvas above the toolbar: the toolbar would still show through the canvas, but every click on it
    /// would reach the canvas. Returns null when the requested place keeps the order.
    /// </summary>
    public static HWND? BelowHigherLayers(WindowRegistry registry, HWND window, WindowLayer layer, HWND insertAfter)
    {
        if (insertAfter == HWND.HWND_BOTTOM || insertAfter == HWND.HWND_NOTOPMOST)
        {
            return null;
        }
        // Activation first lifts the window's own helper windows (its thread's hidden IME windows) to the top and then
        // puts the window right after them, so such a place is the top, wherever that helper is before the move.
        bool toTop = insertAfter.IsNull || insertAfter == HWND.HWND_TOPMOST || MovesWith(insertAfter, window);
        Walk(registry, window, layer, toTop ? HWND.Null : insertAfter, out HWND lowest, out bool requestedAbove, out _);
        return lowest.IsNull || lowest == insertAfter || !(toTop || requestedAbove) ? null : lowest;
    }

    /// <summary>
    /// The window that <paramref name="window"/> of <paramref name="layer"/> must go directly below because it is above
    /// it now: the lowest visible, topmost registered window of a higher layer. Null when the order is right.
    /// </summary>
    public static HWND? MisplacedBelow(WindowRegistry registry, HWND window, WindowLayer layer)
    {
        Walk(registry, window, layer, HWND.Null, out HWND lowest, out _, out bool windowAbove);
        return !lowest.IsNull && windowAbove ? lowest : null;
    }

    /// <summary>
    /// Finds the lowest visible, topmost registered window of a layer above <paramref name="layer"/> in the z-order, and
    /// whether <paramref name="insertAfter"/> and <paramref name="window"/> are above it.
    /// </summary>
    private static void Walk(WindowRegistry registry, HWND window, WindowLayer layer, HWND insertAfter,
                             out HWND lowest, out bool requestedAbove, out bool windowAbove)
    {
        lowest = HWND.Null;
        requestedAbove = false;
        windowAbove = false;
        IReadOnlyList<RegisteredWindow> registered = registry.Windows;
        Span<nint> higher = registered.Count <= 16 ? stackalloc nint[registered.Count] : new nint[registered.Count];
        int count = 0;
        foreach (RegisteredWindow candidate in registered)
        {
            var hwnd = (HWND)candidate.Handle;
            // Only topmost windows: placing a topmost window below a window that is not topmost would end its topmost state.
            if (candidate.Layer > layer && hwnd != window && PInvoke.IsWindowVisible(hwnd) &&
                (WindowLongs.GetExStyle(hwnd) & WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0)
            {
                higher[count++] = candidate.Handle;
            }
        }
        if (count == 0)
        {
            return;
        }
        higher = higher[..count];
        bool requestedSeen = false;
        bool windowSeen = false;
        int walked = 0;
        for (HWND current = PInvoke.GetTopWindow(HWND.Null); !current.IsNull && walked < MaxWindowsWalked;
             current = PInvoke.GetWindow(current, GET_WINDOW_CMD.GW_HWNDNEXT), walked++)
        {
            requestedSeen |= current == insertAfter;
            windowSeen |= current == window;
            if (higher.Contains((nint)current))
            {
                lowest = current;
                // Relative to the lowest one, which is the last found: a place right after it is below it.
                requestedAbove = requestedSeen && current != insertAfter;
                windowAbove = windowSeen;
                if (--count == 0)
                {
                    break;
                }
            }
        }
    }

    /// <summary>True for a window that <paramref name="window"/> owns, or a hidden helper window of its thread.</summary>
    private static bool MovesWith(HWND candidate, HWND window)
    {
        HWND owner = candidate;
        for (int depth = 0; depth < 8; depth++)
        {
            owner = PInvoke.GetWindow(owner, GET_WINDOW_CMD.GW_OWNER);
            if (owner.IsNull)
            {
                break;
            }
            if (owner == window)
            {
                return true;
            }
        }
        return !PInvoke.IsWindowVisible(candidate) &&
            PInvoke.GetWindowThreadProcessId(candidate, out _) == PInvoke.GetWindowThreadProcessId(window, out _);
    }
}

/// <summary>
/// An out-of-context EVENT_SYSTEM_FOREGROUND hook. Windows calls it through the installing
/// thread's message loop when another process's window comes to the foreground. It exists only
/// while installed; skipping this process's own events avoids reacting to Annotator itself.
/// </summary>
internal sealed unsafe class ForegroundHook : IDisposable
{
    private static readonly Lock s_gate = new();
    private static readonly Dictionary<nint, ForegroundHook> s_hooks = [];

    private readonly Action<HWND> _onForegroundChanged;
    private HWINEVENTHOOK _hook;

    private ForegroundHook(Action<HWND> onForegroundChanged) => _onForegroundChanged = onForegroundChanged;

    /// <summary>Installs the hook; <paramref name="onForegroundChanged"/> receives the new foreground window.</summary>
    public static ForegroundHook Install(Action<HWND> onForegroundChanged)
    {
        var hook = new ForegroundHook(onForegroundChanged);
        lock (s_gate)
        {
            hook._hook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_SYSTEM_FOREGROUND,
                PInvoke.EVENT_SYSTEM_FOREGROUND,
                HMODULE.Null,
                &OnEvent,
                0,
                0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
            if (hook._hook.IsNull)
            {
                throw new NativeCallException("SetWinEventHook(EVENT_SYSTEM_FOREGROUND)", unchecked((int)0x80004005));
            }
            s_hooks[hook._hook] = hook;
        }
        return hook;
    }

    public void Dispose()
    {
        lock (s_gate)
        {
            if (_hook.IsNull)
            {
                return;
            }
            s_hooks.Remove(_hook);
            HWINEVENTHOOK hook = _hook;
            _hook = default;
            if (!PInvoke.UnhookWinEvent(hook))
            {
                throw new NativeCallException("UnhookWinEvent(EVENT_SYSTEM_FOREGROUND)", unchecked((int)0x80004005));
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(HWINEVENTHOOK hook, uint winEvent, HWND window, int objectId, int childId, uint eventThread, uint eventTime)
    {
        ForegroundHook? target;
        lock (s_gate)
        {
            s_hooks.TryGetValue(hook, out target);
        }
        if (target is null)
        {
            return;
        }
        try
        {
            target._onForegroundChanged(window);
        }
        catch (Exception exception)
        {
            NativeCallbackErrors.Report(exception);
        }
    }
}
