using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Capture;

/// <summary>
/// Calls back, on the window's own thread and inside the call that shows it, when a registered window that capture
/// has never seen visible is shown for the first time. The Magnification API leaves out only windows that have been
/// shown at least once, so the exclude list must be applied again at that moment, before the window's thread returns to
/// its message loop to paint. A thread-specific <c>WH_CALLWNDPROCRET</c> hook exists only for threads that own such
/// windows, only while capture runs.
/// </summary>
internal sealed unsafe class ShowWatcher : IDisposable
{
    private static readonly Lock s_gate = new();
    private static ShowWatcher[] s_watchers = [];

    private readonly Action<nint> shown;
    private readonly Dictionary<uint, HHOOK> hooks = [];
    private nint[] pending = [];

    public ShowWatcher(Action<nint> shown)
    {
        this.shown = shown;
        lock (s_gate)
        {
            s_watchers = [.. s_watchers, this];
        }
    }

    /// <summary>Threads hooked now, for tests.</summary>
    public int HookCount => hooks.Count;

    /// <summary>
    /// Watches exactly <paramref name="windows"/> (capture thread). Throws <see cref="NativeCallException"/> when a
    /// thread cannot be hooked; the caller then fails closed.
    /// </summary>
    public void Update(IReadOnlyList<RegisteredWindow> windows)
    {
        var handles = new nint[windows.Count];
        var threads = new HashSet<uint>();
        for (var index = 0; index < handles.Length; index++)
        {
            handles[index] = windows[index].Handle;
            threads.Add(windows[index].ThreadId);
        }
        Volatile.Write(ref pending, handles);
        foreach (var thread in threads)
        {
            if (hooks.ContainsKey(thread))
            {
                continue;
            }
            HHOOK hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_CALLWNDPROCRET, &Procedure, HINSTANCE.Null, thread);
            if (hook.IsNull)
            {
                throw NativeCallException.FromLastError($"SetWindowsHookEx(WH_CALLWNDPROCRET) for thread {thread}");
            }
            hooks[thread] = hook;
        }
        foreach (var (thread, hook) in hooks.ToArray())
        {
            if (!threads.Contains(thread))
            {
                PInvoke.UnhookWindowsHookEx(hook);
                hooks.Remove(thread);
            }
        }
    }

    public void Dispose()
    {
        Update([]);
        lock (s_gate)
        {
            s_watchers = Array.FindAll(s_watchers, watcher => !ReferenceEquals(watcher, this));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Procedure(int code, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (code >= 0)
            {
                var message = (CWPRETSTRUCT*)lParam.Value;
                if (message->message == PInvoke.WM_WINDOWPOSCHANGED
                    && (((WINDOWPOS*)message->lParam.Value)->flags & SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW) != 0)
                {
                    var window = (nint)message->hwnd;
                    foreach (var watcher in Volatile.Read(ref s_watchers))
                    {
                        if (Array.IndexOf(Volatile.Read(ref watcher.pending), window) >= 0)
                        {
                            watcher.shown(window);
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            NativeCallbackErrors.Report(error);
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }
}
