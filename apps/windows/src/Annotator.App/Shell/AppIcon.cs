using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Shell;

/// <summary>
/// The app icon in the title bar and taskbar button of Annotator's normal windows. WinUI shows a blank icon unless the
/// window is given one; this is the executable's own icon (ApplicationIcon in Annotator.App.csproj).
/// </summary>
internal static class AppIcon
{
    /// <summary>The icon group's resource ID that the C# compiler gives ApplicationIcon (IDI_APPLICATION).</summary>
    private const int ResourceId = 32512;

    private static HANDLE s_icon;

    /// <summary>Shows the app icon on <paramref name="window"/>; a build without the icon keeps the default.</summary>
    public static void Apply(AppWindow window)
    {
        if (Load() is { } icon)
        {
            window.SetIcon(Win32Interop.GetIconIdFromIcon(icon));
        }
    }

    private static unsafe nint? Load()
    {
        if (s_icon.IsNull)
        {
            HMODULE module = PInvoke.GetModuleHandle((char*)null);
            int size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXICON);
            // Kept for the life of the app: every window shares it.
            s_icon = PInvoke.LoadImage((HINSTANCE)(nint)module, (char*)ResourceId, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_DEFAULTCOLOR);
        }
        return s_icon.IsNull ? null : (nint)s_icon;
    }
}
