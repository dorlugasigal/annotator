using System.Runtime.InteropServices;
using Annotator.App.Native;
using Annotator.Core;
using Annotator.Workspace.Artwork;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Registry;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Shell;

/// <summary>How the person opened the tray icon (NOTIFYICON_VERSION_4 notifications).</summary>
internal enum TrayInvocation
{
    /// <summary>A left click: open or close the flyout.</summary>
    Select,

    /// <summary>Space or Enter on the focused icon: open the flyout with keyboard focus on its first row.</summary>
    KeyboardSelect,

    /// <summary>Right click, Shift+F10 or the menu key: the same flyout as a left click.</summary>
    ContextMenu,
}

/// <summary>The theme variant of the tray glyph.</summary>
internal enum TrayTheme
{
    /// <summary>A light taskbar: a dark glyph.</summary>
    Light,

    /// <summary>A dark taskbar: a white glyph.</summary>
    Dark,

    /// <summary>A contrast theme: the theme's text color.</summary>
    Contrast,
}

/// <summary>
/// The notification area icon (WIN-UI-01): the A mark rendered at run time at the taskbar's icon size, in a
/// light, dark or contrast variant that follows the taskbar theme. It re-adds itself when Explorer restarts
/// (TaskbarCreated). It lives on a hidden top-level window of the app thread, which receives those broadcasts.
/// </summary>
internal sealed unsafe class TrayIcon : HiddenWindow
{
    private const uint CallbackMessage = PInvoke.WM_APP + 1;
    private const uint IconId = 1;
    private const uint ColorWindowText = 8;
    private const uint NotifySelect = PInvoke.WM_USER;
    private const uint NotifyKeySelect = PInvoke.WM_USER | 1;
    private readonly uint _taskbarCreated;
    private readonly Action<TrayInvocation, System.Drawing.Point> _invoked;
    private HICON _icon;
    private bool _added;
    private bool _hidden;
    private bool _sharing;

    public TrayIcon(Action<TrayInvocation, System.Drawing.Point> invoked)
        : base("Annotator.App.Tray", messageOnly: false)
    {
        _invoked = invoked;
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        Add();
    }

    /// <summary>Why the icon is missing, or null when it is shown. Windows may still keep it in the overflow (DV-4).</summary>
    public string? Problem { get; private set; }

    public event Action? ProblemChanged;

    /// <summary>The icon's rectangle on screen in physical pixels, or null when Windows cannot tell.</summary>
    public RECT? Bounds()
    {
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER),
            hWnd = Handle,
            uID = IconId,
        };
        return PInvoke.Shell_NotifyIconGetRect(identifier, out RECT rect).Succeeded ? rect : null;
    }

    public new void Dispose()
    {
        if (_added)
        {
            NOTIFYICONDATAW data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE);
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &data);
            _added = false;
        }
        DestroyIcon();
        base.Dispose();
    }

    /// <summary>The taskbar's theme: contrast, then SystemUsesLightTheme.</summary>
    public static TrayTheme CurrentTheme()
    {
        if (ContrastTheme.IsOn)
        {
            return TrayTheme.Contrast;
        }
        Span<byte> value = stackalloc byte[sizeof(uint)];
        uint size = sizeof(uint);
        WIN32_ERROR result = PInvoke.RegGetValue(HKEY.HKEY_CURRENT_USER, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", REG_ROUTINE_FLAGS.RRF_RT_REG_DWORD, out _, value, ref size);
        return result == WIN32_ERROR.ERROR_SUCCESS && BitConverter.ToUInt32(value) != 0 ? TrayTheme.Light : TrayTheme.Dark;
    }

    /// <summary>
    /// Shows or hides the icon (Settings > General > Show tray icon, the Mac's status item isVisible). A hidden icon stays
    /// added, so it returns at once and after Explorer restarts in the same state.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_hidden == !visible)
        {
            return;
        }
        _hidden = !visible;
        if (!_added)
        {
            return;
        }
        NOTIFYICONDATAW data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_STATE);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data))
        {
            SetProblem(visible ? "The tray icon could not be shown. Try again from Settings." : "The tray icon could not be hidden. Try again from Settings.");
        }
    }

    /// <summary>The glyph color for a theme. Contrast themes use the theme's text color.</summary>
    public static RGBA GlyphColor(TrayTheme theme)
    {
        if (theme == TrayTheme.Contrast)
        {
            uint color = PInvoke.GetSysColor((SYS_COLOR_INDEX)ColorWindowText);
            return new RGBA((color & 0xFF) / 255.0, ((color >> 8) & 0xFF) / 255.0, ((color >> 16) & 0xFF) / 255.0);
        }
        return theme == TrayTheme.Light ? new RGBA(0, 0, 0) : new RGBA(1, 1, 1);
    }

    /// <summary>The small-icon size at the taskbar's DPI (16 at 100%, 20 at 125%).</summary>
    public static int IconSize()
    {
        HWND taskbar = PInvoke.FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar.IsNull ? 96 : PInvoke.GetDpiForWindow(taskbar);
        int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, dpi == 0 ? 96 : dpi);
        return size > 0 ? size : 16;
    }

    /// <summary>The icon's name for UI Automation and its tooltip (the Mac status item's label and sharing tooltip).</summary>
    public static string Tip(bool sharing) => sharing ? "Annotator menu. Region sharing is on." : "Annotator menu";

    /// <summary>Shows whether region sharing is on: a red dot in the icon's top-right corner and the words in its tooltip.</summary>
    public void ShowSharing(bool sharing)
    {
        if (sharing == _sharing)
        {
            return;
        }
        _sharing = sharing;
        if (!_added)
        {
            return;
        }
        ReplaceIcon(CreateIcon(IconSize(), GlyphColor(CurrentTheme()), _sharing));
        NOTIFYICONDATAW data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data))
        {
            SetProblem("The tray icon could not show whether region sharing is on.");
        }
    }

    /// <summary>
    /// Renders the mark as an icon with straight alpha, as icon bitmaps expect. While region sharing is on, a red dot sits in
    /// the top-right corner, as on the Mac's status item (systemRed, a 5-point dot in a 6-point box).
    /// </summary>
    public static HICON CreateIcon(int size, RGBA color, bool sharing = false)
    {
        byte[] pixels = MarkGlyph.RenderBgra(size, color, premultiplied: false);
        if (sharing)
        {
            AddDot(pixels, size);
        }
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = size,
            bV5Height = -size,
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BI_COMPRESSION.BI_BITFIELDS,
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };
        void* bits;
        HBITMAP colorBitmap = PInvoke.CreateDIBSection(HDC.Null, (BITMAPINFO*)&header, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (colorBitmap.IsNull)
        {
            throw CallbackErrors.LastError("CreateDIBSection (tray icon)");
        }
        HBITMAP mask = HBITMAP.Null;
        try
        {
            Marshal.Copy(pixels, 0, (nint)bits, pixels.Length);
            mask = PInvoke.CreateBitmap(size, size, 1, 1, null);
            if (mask.IsNull)
            {
                throw CallbackErrors.LastError("CreateBitmap (tray icon mask)");
            }
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = colorBitmap };
            HICON icon = PInvoke.CreateIconIndirect(&info);
            if (icon.IsNull)
            {
                throw CallbackErrors.LastError("CreateIconIndirect (tray icon)");
            }
            return icon;
        }
        finally
        {
            PInvoke.DeleteObject(colorBitmap);
            if (!mask.IsNull)
            {
                PInvoke.DeleteObject(mask);
            }
        }
    }

    /// <summary>Draws the sharing dot over straight-alpha BGRA pixels, antialiased by its coverage of each pixel.</summary>
    internal static void AddDot(byte[] pixels, int size)
    {
        double radius = Math.Max(2, size * 5.0 / 32);
        double centerX = size - radius - size / 32.0, centerY = radius + size / 32.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double coverage = Math.Clamp(radius + 0.5 - Math.Sqrt(Math.Pow(x + 0.5 - centerX, 2) + Math.Pow(y + 0.5 - centerY, 2)), 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }
                int offset = (y * size + x) * 4;
                double below = pixels[offset + 3] / 255.0;
                double alpha = coverage + below * (1 - coverage);
                // systemRed (#FF3B30) over the mark.
                (double blue, double green, double red) = (0x30 / 255.0, 0x3B / 255.0, 1.0);
                pixels[offset] = (byte)Math.Round((blue * coverage + pixels[offset] / 255.0 * below * (1 - coverage)) / alpha * 255);
                pixels[offset + 1] = (byte)Math.Round((green * coverage + pixels[offset + 1] / 255.0 * below * (1 - coverage)) / alpha * 255);
                pixels[offset + 2] = (byte)Math.Round((red * coverage + pixels[offset + 2] / 255.0 * below * (1 - coverage)) / alpha * 255);
                pixels[offset + 3] = (byte)Math.Round(alpha * 255);
            }
        }
    }

    protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == CallbackMessage)
        {
            uint notification = (uint)(lParam.Value & 0xFFFF);
            var anchor = new System.Drawing.Point((short)(wParam.Value & 0xFFFF), (short)((wParam.Value >> 16) & 0xFFFF));
            switch (notification)
            {
                case NotifySelect:
                    _invoked(TrayInvocation.Select, anchor);
                    break;
                case NotifyKeySelect:
                    _invoked(TrayInvocation.KeyboardSelect, anchor);
                    break;
                case PInvoke.WM_CONTEXTMENU:
                    _invoked(TrayInvocation.ContextMenu, anchor);
                    break;
            }
            return default;
        }
        if (message == _taskbarCreated)
        {
            // Explorer restarted: every notification icon must be added again.
            _added = false;
            Add();
            return default;
        }
        switch (message)
        {
            case PInvoke.WM_CLOSE:
                // The menu leaves this window active; Alt+F4 must not destroy it and orphan the icon (WIN-OVR-12).
                return default;
            case PInvoke.WM_SETTINGCHANGE:
                if (wParam.Value == (nuint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETHIGHCONTRAST ||
                    (lParam.Value != 0 && new string((char*)lParam.Value) == "ImmersiveColorSet"))
                {
                    Refresh();
                    // App windows that stay open follow a contrast theme turned on or off (WIN-UI-07).
                    AppResources.CheckSystemLook();
                }
                break;
            case PInvoke.WM_SYSCOLORCHANGE:
            case PInvoke.WM_THEMECHANGED:
            case PInvoke.WM_DISPLAYCHANGE:
            case PInvoke.WM_DPICHANGED:
                Refresh();
                break;
        }
        return base.WndProc(message, wParam, lParam);
    }

    private void Add()
    {
        HICON icon = CreateIcon(IconSize(), GlyphColor(CurrentTheme()), _sharing);
        ReplaceIcon(icon);
        NOTIFYICONDATAW data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON |
            NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP | NOTIFY_ICON_DATA_FLAGS.NIF_STATE);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &data))
        {
            // Explorer may not be running yet; TaskbarCreated arrives when it is.
            SetProblem("The tray icon could not be added. Windows will add it when the taskbar is ready.");
            return;
        }
        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &data))
        {
            SetProblem("The tray icon could not use the current Windows notification behavior.");
        }
        else
        {
            SetProblem(null);
        }
        _added = true;
    }

    private void Refresh()
    {
        if (!_added)
        {
            return;
        }
        ReplaceIcon(CreateIcon(IconSize(), GlyphColor(CurrentTheme()), _sharing));
        NOTIFYICONDATAW data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_ICON);
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data))
        {
            SetProblem("The tray icon could not change to the new Windows theme.");
        }
    }

    private NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            dwState = _hidden ? NOTIFY_ICON_STATE.NIS_HIDDEN : 0,
            dwStateMask = NOTIFY_ICON_STATE.NIS_HIDDEN,
        };
        // Windows reads the tooltip as the icon's UI Automation name, so it carries the Mac status item's
        // accessibility label (WIN-A11Y-01).
        Tip(_sharing).AsSpan().CopyTo(data.szTip.AsSpan());
        return data;
    }

    private void ReplaceIcon(HICON icon)
    {
        DestroyIcon();
        _icon = icon;
    }

    private void DestroyIcon()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = HICON.Null;
        }
    }

    private void SetProblem(string? problem)
    {
        if (Problem != problem)
        {
            Problem = problem;
            ProblemChanged?.Invoke();
        }
    }
}
