using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Annotator.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Native;

/// <summary>Native failures inside window procedures; the host shows them to the person.</summary>
internal static class CallbackErrors
{
    public static event Action<Exception>? Raised;

    public static void Report(Exception error)
    {
        Action<Exception>? handler = Raised;
        if (handler is null)
        {
            Environment.FailFast("An Annotator window procedure failed before error reporting started.", error);
        }
        handler(error);
    }

    public static NativeCallException LastError(string operation)
    {
        int hresult = Marshal.GetHRForLastWin32Error();
        return new NativeCallException(operation, hresult != 0 ? hresult : unchecked((int)0x80004005));
    }
}

/// <summary>
/// A hidden Win32 window owned by managed code on the thread that creates it. Message-only windows receive
/// sent and posted messages; a hidden top-level window also receives broadcasts such as TaskbarCreated and
/// WM_SETTINGCHANGE.
/// </summary>
internal abstract unsafe class HiddenWindow : IDisposable
{
    private static readonly Lock s_classLock = new();
    private static readonly HashSet<string> s_classes = [];
    private GCHandle _self;

    protected HiddenWindow(string className, bool messageOnly)
    {
        HINSTANCE module = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        lock (s_classLock)
        {
            if (!s_classes.Contains(className))
            {
                fixed (char* name = className)
                {
                    var windowClass = new WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(WNDCLASSEXW),
                        lpfnWndProc = &WindowProcedure,
                        hInstance = module,
                        lpszClassName = name,
                    };
                    if (PInvoke.RegisterClassEx(&windowClass) == 0 && Marshal.GetLastPInvokeError() != 1410)
                    {
                        throw CallbackErrors.LastError($"RegisterClassEx({className})");
                    }
                }
                s_classes.Add(className);
            }
        }
        _self = GCHandle.Alloc(this);
        HWND hwnd = PInvoke.CreateWindowEx(
            messageOnly ? 0 : WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE,
            className, null, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0,
            messageOnly ? HWND.HWND_MESSAGE : HWND.Null, HMENU.Null, module, (void*)GCHandle.ToIntPtr(_self));
        if (hwnd.IsNull)
        {
            NativeCallException failure = CallbackErrors.LastError($"CreateWindowEx({className})");
            if (_self.IsAllocated)
            {
                _self.Free();
            }
            throw failure;
        }
    }

    public HWND Handle { get; private set; }

    public void Dispose()
    {
        if (!Handle.IsNull && !PInvoke.DestroyWindow(Handle))
        {
            throw CallbackErrors.LastError("DestroyWindow");
        }
    }

    protected virtual LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(Handle, message, wParam, lParam);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProcedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            HiddenWindow? window;
            if (message == PInvoke.WM_NCCREATE)
            {
                nint self = (nint)((CREATESTRUCTW*)lParam.Value)->lpCreateParams;
                window = (HiddenWindow)GCHandle.FromIntPtr(self).Target!;
                PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, self);
                window.Handle = hwnd;
            }
            else
            {
                nint self = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA);
                window = self == 0 ? null : GCHandle.FromIntPtr(self).Target as HiddenWindow;
            }
            if (window is null)
            {
                return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
            }
            if (message == PInvoke.WM_NCDESTROY)
            {
                LRESULT result = PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
                PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_USERDATA, 0);
                window.Handle = HWND.Null;
                if (window._self.IsAllocated)
                {
                    window._self.Free();
                }
                return result;
            }
            return window.WndProc(message, wParam, lParam);
        }
        catch (Exception error)
        {
            CallbackErrors.Report(error);
            return message == PInvoke.WM_NCDESTROY ? default : PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }
    }
}
