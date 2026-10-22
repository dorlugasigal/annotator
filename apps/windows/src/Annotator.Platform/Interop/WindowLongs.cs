using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Interop;

/// <summary>
/// GetWindowLongPtrW and SetWindowLongPtrW. CsWin32 does not generate them for an AnyCPU
/// library because 32-bit user32 lacks them; Annotator ships for x64 and ARM64 only.
/// </summary>
internal static partial class WindowLongs
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int GWLP_USERDATA = -21;

    public static WINDOW_STYLE GetStyle(HWND window) => (WINDOW_STYLE)(uint)Get(window, GWL_STYLE, "GetWindowLongPtr(GWL_STYLE)");

    public static WINDOW_EX_STYLE GetExStyle(HWND window) => (WINDOW_EX_STYLE)(uint)Get(window, GWL_EXSTYLE, "GetWindowLongPtr(GWL_EXSTYLE)");

    public static void SetExStyle(HWND window, WINDOW_EX_STYLE style) => Set(window, GWL_EXSTYLE, (nint)(uint)style, "SetWindowLongPtr(GWL_EXSTYLE)");

    public static nint GetUserData(HWND window) => GetWindowLongPtr(window, GWLP_USERDATA);

    public static void SetUserData(HWND window, nint value) => Set(window, GWLP_USERDATA, value, "SetWindowLongPtr(GWLP_USERDATA)");

    private static nint Get(HWND window, int index, string operation)
    {
        nint value = GetWindowLongPtr(window, index);
        if (value == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw NativeCallException.FromLastError(operation);
        }
        return value;
    }

    private static void Set(HWND window, int index, nint value, string operation)
    {
        // A previous value of 0 is valid, so only a non-zero last error means failure.
        if (SetWindowLongPtr(window, index, value) == 0 && Marshal.GetLastPInvokeError() != 0)
        {
            throw NativeCallException.FromLastError(operation);
        }
    }

    private static nint GetWindowLongPtr(HWND window, int index) => GetWindowLongPtrW(window, index);

    private static nint SetWindowLongPtr(HWND window, int index, nint value) => SetWindowLongPtrW(window, index, value);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint window, int index, nint value);
}
