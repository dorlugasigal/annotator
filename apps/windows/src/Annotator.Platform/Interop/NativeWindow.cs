using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Interop;

/// <summary>
/// A Win32 window owned by managed code. One static window procedure routes each message to the
/// instance stored in GWLP_USERDATA. The window belongs to the thread that created it, and that
/// thread must run a message loop (a dispatcher queue thread or the WinUI thread).
/// </summary>
internal abstract unsafe class NativeWindow
{
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private static readonly Lock s_classLock = new();
    private static readonly HashSet<string> s_registeredClasses = [];

    private GCHandle _self;

    public HWND Handle { get; private set; }

    /// <summary>The managed thread that created the window; every call must come from it.</summary>
    public int OwnerThreadId { get; } = Environment.CurrentManagedThreadId;

    protected void CreateHandle(string className, WINDOW_EX_STYLE exStyle, WINDOW_STYLE style, int x, int y, int width, int height, HWND parent, string operation)
    {
        if (!Handle.IsNull)
        {
            throw new InvalidOperationException("The window already exists.");
        }

        HINSTANCE module = RegisterClass(className);
        _self = GCHandle.Alloc(this);
        HWND hwnd = PInvoke.CreateWindowEx(exStyle, className, null, style, x, y, width, height, parent, HMENU.Null, module, (void*)GCHandle.ToIntPtr(_self));
        if (hwnd.IsNull)
        {
            NativeCallException failure = NativeCallException.FromLastError(operation);
            // When creation fails after WM_NCCREATE, WM_NCDESTROY has already released the handle.
            if (_self.IsAllocated)
            {
                _self.Free();
            }
            throw failure;
        }
    }

    /// <summary>Destroys the window. WM_NCDESTROY then calls <see cref="OnDestroyed"/>.</summary>
    protected void DestroyHandle(string operation)
    {
        if (Handle.IsNull)
        {
            return;
        }
        if (!PInvoke.DestroyWindow(Handle))
        {
            throw NativeCallException.FromLastError(operation);
        }
    }

    protected void VerifyOwnerThread(string component)
    {
        if (Environment.CurrentManagedThreadId != OwnerThreadId)
        {
            throw new InvalidOperationException($"{component} must be used on the thread that created it.");
        }
    }

    protected virtual LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(Handle, message, wParam, lParam);

    /// <summary>Runs during WM_NCDESTROY, after <see cref="Handle"/> is cleared.</summary>
    protected virtual void OnDestroyed(HWND destroyed)
    {
    }

    private static HINSTANCE RegisterClass(string className)
    {
        HINSTANCE module = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        lock (s_classLock)
        {
            if (s_registeredClasses.Contains(className))
            {
                return module;
            }

            fixed (char* name = className)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProcedure,
                    hInstance = module,
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(&windowClass) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw NativeCallException.FromLastError($"RegisterClassEx({className})");
                }
            }
            s_registeredClasses.Add(className);
        }
        return module;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProcedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        NativeWindow? window = null;
        try
        {
            if (message == PInvoke.WM_NCCREATE)
            {
                nint self = (nint)((CREATESTRUCTW*)lParam.Value)->lpCreateParams;
                window = (NativeWindow)GCHandle.FromIntPtr(self).Target!;
                WindowLongs.SetUserData(hwnd, self);
                window.Handle = hwnd;
            }
            else
            {
                nint self = WindowLongs.GetUserData(hwnd);
                window = self == 0 ? null : GCHandle.FromIntPtr(self).Target as NativeWindow;
            }

            if (window is null)
            {
                return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
            }
            if (message == PInvoke.WM_NCDESTROY)
            {
                LRESULT result = PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
                window.Detach(hwnd);
                return result;
            }
            return window.WndProc(message, wParam, lParam);
        }
        catch (Exception exception)
        {
            NativeCallbackErrors.Report(exception);
            return message == PInvoke.WM_NCDESTROY ? default : PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void Detach(HWND hwnd)
    {
        WindowLongs.SetUserData(hwnd, 0);
        Handle = HWND.Null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
        OnDestroyed(hwnd);
    }
}
