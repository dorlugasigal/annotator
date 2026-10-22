using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Windowing;

/// <summary>Prepares new Annotator windows for capture exclusion.</summary>
internal static unsafe class WindowPriming
{
    /// <summary>
    /// Shows and hides a hidden window once while DWM cloaks it, so nothing appears on screen. The Magnification API
    /// leaves out only windows that have been shown at least once (measured); afterwards an exclusion holds across
    /// hiding, showing and moving.
    /// </summary>
    public static void ShowOnceCloaked(HWND window)
    {
        if (PInvoke.IsWindowVisible(window))
        {
            return;
        }
        SetCloak(window, true);
        try
        {
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_SHOWNA);
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_HIDE);
        }
        finally
        {
            SetCloak(window, false);
        }
    }

    private static void SetCloak(HWND window, bool cloaked)
    {
        BOOL value = cloaked;
        NativeCallException.ThrowIfFailed(PInvoke.DwmSetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL)),
                                          cloaked ? "DwmSetWindowAttribute(DWMWA_CLOAK)" : "DwmSetWindowAttribute(DWMWA_CLOAK off)");
    }
}
