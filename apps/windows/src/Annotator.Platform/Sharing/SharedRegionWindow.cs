using System.Numerics;
using Annotator.Platform.Accessibility;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Sharing;

/// <summary>
/// The window people share in their meeting app (WIN-CAP-08, the Mac's SharedRegionWindow): an ordinary, opaque top-level
/// window with a title, a close button and resizable borders, which opens behind other apps without taking the keyboard
/// and keeps its identity until it closes. Its client area is drawn by Windows.UI.Composition (<see cref="Root"/>, in
/// physical pixels, on a black background) and its content child is the UI Automation image (<see cref="Accessibility"/>).
/// It registers before it first shows, so Annotator's capture leaves it out and it never appears inside its own region,
/// but as an ordinary window that the stacking order never lifts. Use it on the thread that created it; that thread runs a
/// message loop and owns the compositor.
/// </summary>
public sealed class SharedRegionWindow : IDisposable
{
    /// <summary>The Mac's contentMinSize, in points (SharingRegion.OutputMinimumWidth and OutputMinimumHeight).</summary>
    public const double MinimumWidth = 320;

    public const double MinimumHeight = 200;

    // No minimize button, as on the Mac: meeting apps cannot capture a minimized window.
    private const WINDOW_STYLE Style = WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU | WINDOW_STYLE.WS_THICKFRAME |
        WINDOW_STYLE.WS_MAXIMIZEBOX | WINDOW_STYLE.WS_CLIPCHILDREN;

    private const WINDOW_EX_STYLE ExStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_APPWINDOW;

    private const uint WindowMessageGetObject = 0x003D;

    private readonly Frame _frame;
    private readonly Content _content;
    private readonly WindowRegistration? _registration;
    private readonly DesktopWindowTarget? _target;
    private readonly ContainerVisual? _root;
    private readonly SpriteVisual? _background;
    private OverlayAccessibility? _accessibility;
    private bool _shown;
    private bool _disposed;

    /// <summary>Creates the hidden window titled <paramref name="title"/>.</summary>
    public SharedRegionWindow(WindowRegistry registry, Compositor compositor, string title)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        _frame = new Frame(this);
        _content = new Content(this);
        try
        {
            _frame.Create(title);
            HWND hwnd = _frame.Handle;
            _content.Create(hwnd);
            WindowPriming.ShowOnceCloaked(hwnd);
            _registration = registry.Register((nint)hwnd, WindowLayer.Ordinary);
            _target = CompositionInterop.CreateDesktopWindowTarget(compositor, (nint)hwnd, isTopmost: true);
            _root = compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            _background = compositor.CreateSpriteVisual();
            _background.RelativeSizeAdjustment = Vector2.One;
            _background.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            _root.Children.InsertAtBottom(_background);
            Root = compositor.CreateContainerVisual();
            Root.RelativeSizeAdjustment = Vector2.One;
            _root.Children.InsertAtTop(Root);
            _target.Root = _root;
            UpdateSize();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The top-level window, or 0 once disposed: the window meeting apps list.</summary>
    public nint Handle => (nint)_frame.Handle;

    /// <summary>The content child that UI Automation reads as the shared image, or 0 once disposed.</summary>
    public nint ContentHandle => (nint)_content.Handle;

    /// <summary>The client area's content, in physical pixels, above a black background.</summary>
    public ContainerVisual Root { get; } = null!;

    /// <summary>The client area's size in physical pixels.</summary>
    public (int Width, int Height) ClientSize { get; private set; }

    /// <summary>Physical pixels per point on the window's display.</summary>
    public double Scale { get; private set; } = 1;

    public bool IsVisible => !_frame.Handle.IsNull && PInvoke.IsWindowVisible(_frame.Handle);

    /// <summary>The person closed the window (its close button, Alt+F4 or the taskbar). The owner disposes it.</summary>
    public event Action? CloseRequested;

    /// <summary>The client area's size or scale changed.</summary>
    public event Action? Resized;

    /// <summary>What UI Automation reads in the client area, or null. Set it on the window's thread.</summary>
    public OverlayAccessibility? Accessibility
    {
        get => _accessibility;
        set
        {
            if (!ReferenceEquals(_accessibility, value))
            {
                _accessibility?.Dispose();
                _accessibility = value;
            }
        }
    }

    /// <summary>The client area's rectangle on the screen, in physical pixels.</summary>
    public unsafe PixelRect ClientScreenRect()
    {
        Verify();
        if (!PInvoke.GetClientRect(_frame.Handle, out RECT client))
        {
            throw NativeCallException.FromLastError("GetClientRect (shared region)");
        }
        var origin = new System.Drawing.Point(0, 0);
        PInvoke.ClientToScreen(_frame.Handle, ref origin);
        return PixelRect.FromSize(origin.X, origin.Y, client.right - client.left, client.bottom - client.top);
    }

    /// <summary>
    /// The window's visible frame around its client area on a display with <paramref name="dpi"/>, in physical pixels: the
    /// caption and the borders people see, without the invisible resize borders Windows draws outside them.
    /// </summary>
    public static unsafe (int Top, int Left, int Bottom, int Right) VisibleFrame(uint dpi)
    {
        var rect = default(RECT);
        if (!PInvoke.AdjustWindowRectExForDpi(ref rect, Style, false, ExStyle, dpi))
        {
            throw NativeCallException.FromLastError("AdjustWindowRectExForDpi (shared region frame)");
        }
        int invisible = Math.Max(0, -rect.left - (int)Math.Round(dpi / 96.0));
        return (-rect.top, -rect.left - invisible, rect.bottom - invisible, rect.right - invisible);
    }

    /// <summary>
    /// Moves and sizes the window so its client area is <paramref name="client"/>, in physical pixels on
    /// <paramref name="display"/> (Mac SharedRegionWindow.fit), without activating it or changing its stacking.
    /// </summary>
    public unsafe void Place(DisplayInfo display, PixelRect client)
    {
        Verify();
        ArgumentNullException.ThrowIfNull(display);
        var rect = new RECT { left = client.Left, top = client.Top, right = client.Right, bottom = client.Bottom };
        if (!PInvoke.AdjustWindowRectExForDpi(ref rect, Style, false, ExStyle, display.Dpi))
        {
            throw NativeCallException.FromLastError("AdjustWindowRectExForDpi (shared region)");
        }
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        // Moving to a display with another DPI applies Windows' suggested size first; the second call sets the exact size.
        for (int pass = 0; pass < 2; pass++)
        {
            if (!PInvoke.SetWindowPos(_frame.Handle, HWND.Null, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top, flags))
            {
                throw NativeCallException.FromLastError("SetWindowPos (shared region placement)");
            }
        }
        UpdateSize();
    }

    /// <summary>
    /// Shows the window behind every other window without activating it (the Mac's orderBack): it stays an ordinary
    /// visible window that meeting apps list. Later calls only show it again if it was hidden.
    /// </summary>
    public void ShowBehind()
    {
        Verify();
        if (_shown && PInvoke.IsWindowVisible(_frame.Handle))
        {
            return;
        }
        _shown = true;
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        if (!PInvoke.SetWindowPos(_frame.Handle, HWND.HWND_BOTTOM, 0, 0, 0, 0, flags))
        {
            throw NativeCallException.FromLastError("SetWindowPos(HWND_BOTTOM) (shared region)");
        }
    }

    /// <summary>Releases composition and destroys the window. Call it on the thread that created it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Accessibility = null;
        if (_target is not null)
        {
            _target.Root = null;
            _target.Dispose();
        }
        Root?.Children.RemoveAll();
        Root?.Dispose();
        _background?.Dispose();
        _root?.Dispose();
        _content.Destroy();
        _frame.Destroy();
        _registration?.Dispose();
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frame.Verify();
    }

    private void UpdateSize()
    {
        if (_frame.Handle.IsNull || !PInvoke.GetClientRect(_frame.Handle, out RECT client))
        {
            return;
        }
        int width = Math.Max(0, client.right - client.left), height = Math.Max(0, client.bottom - client.top);
        uint dpi = PInvoke.GetDpiForWindow(_frame.Handle);
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        if (!_content.Handle.IsNull)
        {
            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
            PInvoke.SetWindowPos(_content.Handle, HWND.Null, 0, 0, width, height, flags);
        }
        if ((width, height) == ClientSize && scale == Scale)
        {
            return;
        }
        ClientSize = (width, height);
        Scale = scale;
        Resized?.Invoke();
    }

    private unsafe LRESULT OnFrameMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_CLOSE:
                CloseRequested?.Invoke();
                return default;
            case PInvoke.WM_SIZE:
                UpdateSize();
                return default;
            case PInvoke.WM_GETMINMAXINFO:
            {
                uint dpi = PInvoke.GetDpiForWindow(_frame.Handle);
                double scale = dpi == 0 ? 1 : dpi / 96.0;
                var rect = new RECT { left = 0, top = 0, right = (int)Math.Round(MinimumWidth * scale), bottom = (int)Math.Round(MinimumHeight * scale) };
                if (PInvoke.AdjustWindowRectExForDpi(ref rect, Style, false, ExStyle, dpi == 0 ? 96 : dpi))
                {
                    var info = (MINMAXINFO*)lParam.Value;
                    info->ptMinTrackSize = new System.Drawing.Point(rect.right - rect.left, rect.bottom - rect.top);
                }
                return default;
            }
            case PInvoke.WM_DPICHANGED:
            {
                var suggested = (RECT*)lParam.Value;
                const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                    SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
                PInvoke.SetWindowPos(_frame.Handle, HWND.Null, suggested->left, suggested->top,
                                     suggested->right - suggested->left, suggested->bottom - suggested->top, flags);
                UpdateSize();
                return default;
            }
            case PInvoke.WM_ERASEBKGND:
                return (LRESULT)1;
        }
        return PInvoke.DefWindowProc(_frame.Handle, message, wParam, lParam);
    }

    private LRESULT OnContentMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WindowMessageGetObject when _accessibility is { } accessibility && accessibility.TryAnswer(wParam.Value, lParam.Value, out nint answer):
                return (LRESULT)answer;
            case PInvoke.WM_ERASEBKGND:
                return (LRESULT)1;
            case PInvoke.WM_NCHITTEST:
                // View only (the Mac's shared window takes no input): clicks go to the frame, which only moves and sizes.
                return (LRESULT)PInvoke.HTTRANSPARENT;
        }
        return PInvoke.DefWindowProc(_content.Handle, message, wParam, lParam);
    }

    private sealed class Frame(SharedRegionWindow owner) : NativeWindow
    {
        public void Create(string title)
        {
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle("Annotator.Platform.SharedRegion", ExStyle, Style, 0, 0, 640, 400, HWND.Null, "CreateWindowEx (shared region)");
            if (!PInvoke.SetWindowText(Handle, title))
            {
                throw NativeCallException.FromLastError("SetWindowText (shared region)");
            }
        }

        public void Destroy() => DestroyHandle("DestroyWindow (shared region)");

        public void Verify() => VerifyOwnerThread("The shared region window");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam) => owner.OnFrameMessage(message, wParam, lParam);
    }

    private sealed class Content(SharedRegionWindow owner) : NativeWindow
    {
        public void Create(HWND parent) =>
            CreateHandle("Annotator.Platform.SharedRegionContent", 0, WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE, 0, 0, 1, 1, parent,
                         "CreateWindowEx (shared region content)");

        public void Destroy() => DestroyHandle("DestroyWindow (shared region content)");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam) => owner.OnContentMessage(message, wParam, lParam);
    }
}
