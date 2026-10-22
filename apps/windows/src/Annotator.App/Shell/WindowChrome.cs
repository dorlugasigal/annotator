using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Annotator.App.Native;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using CompositionCompositor = Windows.UI.Composition.Compositor;
using CompositionTarget = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;

namespace Annotator.App.Shell;

/// <summary>A monitor's work area in physical pixels and its scale (desktop pixels per point).</summary>
internal readonly record struct MonitorArea(RectInt32 WorkArea, RectInt32 Bounds, double Scale);

/// <summary>Window setup shared by the toolbar and the popovers (tray flyout and color palette).</summary>
internal static partial class WindowChrome
{
    /// <summary>The window procedures that <see cref="FramelessProcedure"/> replaced; used on the app thread only.</summary>
    private static readonly Dictionary<nint, nint> s_originalProcedures = [];

    /// <summary>Frameless windows that may be larger than the screen (<see cref="AllowAnySize"/>).</summary>
    private static readonly HashSet<nint> s_anySize = [];
    private static CompositionCompositor? s_compositor;

    /// <summary>
    /// Development only (UX reference capture): lets a frameless window be taller or wider than the screen, as the Mac
    /// capture's 1500-point Settings states are.
    /// </summary>
    public static void AllowAnySize(HWND hwnd)
    {
        if (!s_originalProcedures.ContainsKey((nint)hwnd))
        {
            throw new InvalidOperationException("Only a window set up by WindowChrome.Configure can be larger than the screen.");
        }
        s_anySize.Add((nint)hwnd);
    }

    /// <summary>
    /// A borderless, always-on-top tool window with no taskbar button or Alt+Tab entry. Only its XAML content is
    /// visible, so the content draws its own rounded surface over the desktop. <paramref name="noActivate"/> adds
    /// WS_EX_NOACTIVATE.
    /// </summary>
    public static HWND Configure(Window window, bool noActivate)
    {
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;

        var hwnd = new HWND(Win32Interop.GetWindowFromWindowId(window.AppWindow.Id));
        nint style = PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        style |= (nint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        if (noActivate)
        {
            style |= (nint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        }
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style);
        ApplySurfaceAlpha(hwnd);
        RemoveFrame(hwnd);
        window.SystemBackdrop = new TransparentBackdrop();
        return hwnd;
    }

    /// <summary>
    /// Puts the window in the topmost band without activating it. OverlappedPresenter.IsAlwaysOnTop alone did not
    /// set WS_EX_TOPMOST on a window shown without activation, so Annotator sets it explicitly.
    /// </summary>
    public static void KeepOnTop(HWND hwnd)
    {
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        if (!PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags))
        {
            throw CallbackErrors.LastError("SetWindowPos(HWND_TOPMOST)");
        }
    }

    /// <summary>
    /// Lets DWM blend the window's surface by its per-pixel alpha, which it does only for windows with blur-behind;
    /// an empty blur region keeps the desktop behind the window sharp. The surface is transparent black wherever
    /// the XAML content does not draw. The system's own corner rounding stays off: it would clip the XAML surface
    /// with a smaller radius.
    /// </summary>
    private static unsafe void ApplySurfaceAlpha(HWND hwnd)
    {
        HRGN nowhere = PInvoke.CreateRectRgn(-2, -2, -1, -1);
        if (nowhere.IsNull)
        {
            throw CallbackErrors.LastError("CreateRectRgn");
        }
        try
        {
            var blur = new DWM_BLURBEHIND { dwFlags = PInvoke.DWM_BB_ENABLE | PInvoke.DWM_BB_BLURREGION, fEnable = true, hRgnBlur = nowhere };
            PInvoke.DwmEnableBlurBehindWindow(hwnd, blur).ThrowOnFailure();
        }
        finally
        {
            PInvoke.DeleteObject(nowhere);
        }
        DWM_WINDOW_CORNER_PREFERENCE preference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &preference,
            sizeof(DWM_WINDOW_CORNER_PREFERENCE)).ThrowOnFailure();
    }

    /// <summary>
    /// The presenter keeps a 3-pixel dialog frame (WS_DLGFRAME) even without border and title bar, and removing the
    /// style does not stick. Instead, the window procedure gives the client area the whole window, so the frame has
    /// no pixels to draw.
    /// </summary>
    internal static unsafe void RemoveFrame(HWND hwnd)
    {
        nint original = PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_WNDPROC,
            (nint)(delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)&FramelessProcedure);
        if (original == 0)
        {
            throw CallbackErrors.LastError("SetWindowLongPtr(GWLP_WNDPROC)");
        }
        s_originalProcedures[(nint)hwnd] = original;
        const SET_WINDOW_POS_FLAGS frameChanged = SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        if (!PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0, frameChanged))
        {
            throw CallbackErrors.LastError("SetWindowPos(SWP_FRAMECHANGED)");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe LRESULT FramelessProcedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (!s_originalProcedures.TryGetValue((nint)hwnd, out nint original))
        {
            return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }
        try
        {
            switch (message)
            {
                case PInvoke.WM_NCCALCSIZE:
                    return default;
                case PInvoke.WM_ERASEBKGND when PInvoke.GetClientRect(hwnd, out RECT client):
                    // WinUI erases with the theme's window color, white in the light theme. GDI writes zero alpha,
                    // so only black is fully transparent once DWM blends by the surface alpha.
                    PInvoke.FillRect(new HDC((nint)wParam.Value), client, new HBRUSH(PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.BLACK_BRUSH).Value));
                    return new LRESULT(1);
                case PInvoke.WM_DWMCOMPOSITIONCHANGED:
                    // Composition changed, for example because DWM restarted: apply the blur-behind and the corner
                    // preference again.
                    ApplySurfaceAlpha(hwnd);
                    break;
                case PInvoke.WM_GETMINMAXINFO when s_anySize.Contains((nint)hwnd):
                {
                    // Windows keeps a window within the screen's size unless it says it may be larger.
                    LRESULT result = PInvoke.CallWindowProc((delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)original, hwnd, message, wParam, lParam);
                    var limits = (MINMAXINFO*)lParam.Value;
                    limits->ptMaxTrackSize = new System.Drawing.Point(32_000, 32_000);
                    return result;
                }
                case PInvoke.WM_NCDESTROY:
                    s_originalProcedures.Remove((nint)hwnd);
                    s_anySize.Remove((nint)hwnd);
                    PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWLP_WNDPROC, original);
                    break;
            }
        }
        catch (Exception error)
        {
            CallbackErrors.Report(error);
        }
        return PInvoke.CallWindowProc((delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)original, hwnd, message, wParam, lParam);
    }

    /// <summary>The work area and scale of the monitor nearest a desktop point.</summary>
    public static MonitorArea MonitorAt(int x, int y)
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(x, y), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return Describe(monitor);
    }

    /// <summary>The work area and scale of the monitor that a rectangle overlaps most.</summary>
    public static MonitorArea MonitorFor(RectInt32 rect)
    {
        var area = new RECT { left = rect.X, top = rect.Y, right = rect.X + rect.Width, bottom = rect.Y + rect.Height };
        HMONITOR monitor = PInvoke.MonitorFromRect(area, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return Describe(monitor);
    }

    /// <summary>The pointer in physical pixels, or null when Windows cannot tell (for example on the secure desktop).</summary>
    public static System.Drawing.Point? TryCursor() => PInvoke.GetCursorPos(out System.Drawing.Point point) ? point : null;

    /// <summary>The monitor under the pointer, or the primary monitor when the pointer position is unknown.</summary>
    public static MonitorArea MonitorAtCursor() => TryCursor() is { } cursor ? MonitorAt(cursor.X, cursor.Y) : MonitorAt(0, 0);

    private static unsafe MonitorArea Describe(HMONITOR monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            throw CallbackErrors.LastError("GetMonitorInfo");
        }
        double scale = 1;
        if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded)
        {
            scale = dpiX / 96.0;
        }
        RECT work = info.rcWork, bounds = info.rcMonitor;
        return new MonitorArea(
            new RectInt32(work.left, work.top, work.right - work.left, work.bottom - work.top),
            new RectInt32(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top),
            scale);
    }

    /// <summary>
    /// The app thread's Windows.UI.Composition compositor, for windows that the system compositor draws (the transparent
    /// backdrop and the toolbar's dock ghost). The WinUI thread needs a Windows.System dispatcher queue first (S-12).
    /// </summary>
    public static CompositionCompositor SystemCompositor()
    {
        if (s_compositor is null)
        {
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
            s_compositor = new CompositionCompositor();
        }
        return s_compositor;
    }

    /// <summary>
    /// A system backdrop of fully transparent color. Without a backdrop, WinUI fills the whole window with the
    /// theme's background color, corners included.
    /// </summary>
    private sealed partial class TransparentBackdrop : SystemBackdrop
    {
        protected override void OnTargetConnected(CompositionTarget connectedTarget, XamlRoot xamlRoot)
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            connectedTarget.SystemBackdrop = SystemCompositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        protected override void OnTargetDisconnected(CompositionTarget disconnectedTarget)
        {
            disconnectedTarget.SystemBackdrop = null;
            base.OnTargetDisconnected(disconnectedTarget);
        }
    }
}
