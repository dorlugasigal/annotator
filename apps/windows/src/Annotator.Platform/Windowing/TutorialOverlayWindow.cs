using System.Numerics;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Windowing;

/// <summary>
/// The tutorial's layer over one display (the Mac's TutorialOverlay panel): the ghost cursor, its clicks and ink, the ring
/// around the control to use and the confetti at the end. It is a topmost tool window above every other Annotator window
/// (the guide layer) that never activates and lets every click through, drawn only by Windows.UI.Composition through
/// <see cref="Root"/>, in physical pixels from the window's top-left corner. It registers before it first shows, so
/// Annotator's own capture leaves it out. Use it on the thread that created it; that thread runs a message loop and owns
/// the compositor.
/// </summary>
public sealed class TutorialOverlayWindow : IDisposable
{
    internal const WINDOW_EX_STYLE ExStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOPMOST |
        WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT |
        WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    private readonly Host _host = new();
    private readonly WindowRegistration? _registration;
    private readonly DesktopWindowTarget? _target;
    private readonly ContainerVisual? _root;
    private bool _disposed;

    public TutorialOverlayWindow(WindowRegistry registry, Compositor compositor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(compositor);
        try
        {
            _host.Create();
            HWND hwnd = _host.Handle;
            if (!PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw NativeCallException.FromLastError("SetLayeredWindowAttributes (tutorial overlay)");
            }
            // The magnifier leaves out only windows that were shown once, so it is shown and hidden while cloaked first.
            WindowPriming.ShowOnceCloaked(hwnd);
            _registration = registry.Register((nint)hwnd, WindowLayer.Guide);
            _target = CompositionInterop.CreateDesktopWindowTarget(compositor, (nint)hwnd, isTopmost: true);
            _root = compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            _target.Root = _root;
            Compositor = compositor;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public Compositor? Compositor { get; }

    /// <summary>The visuals' parent, in physical pixels from the window's top-left corner.</summary>
    public ContainerVisual Root => _root ?? throw new ObjectDisposedException(nameof(TutorialOverlayWindow));

    /// <summary>The window handle, or 0 once disposed.</summary>
    public nint Handle => (nint)_host.Handle;

    public bool IsVisible => !_host.Handle.IsNull && PInvoke.IsWindowVisible(_host.Handle);

    /// <summary>The frame shown last, in physical pixels.</summary>
    public PixelRect Frame { get; private set; }

    /// <summary>Shows the layer over <paramref name="frame"/> (physical pixels), on top of every window and without activating it.</summary>
    public void Show(PixelRect frame)
    {
        Verify();
        if (frame.IsEmpty)
        {
            throw new ArgumentException("The tutorial overlay needs a frame with an area.", nameof(frame));
        }
        Frame = frame;
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        // Physical pixels whatever the calling thread's DPI awareness.
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        if (!PInvoke.SetWindowPos(_host.Handle, HWND.HWND_TOPMOST, frame.Left, frame.Top, frame.Width, frame.Height, flags))
        {
            throw NativeCallException.FromLastError("SetWindowPos (tutorial overlay)");
        }
    }

    /// <summary>Hides the layer; its visuals stay until the caller removes them.</summary>
    public void Hide()
    {
        Verify();
        if (PInvoke.IsWindowVisible(_host.Handle))
        {
            PInvoke.ShowWindow(_host.Handle, SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    /// <summary>Releases composition and destroys the window. Call it on the thread that created the layer.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_target is not null)
        {
            _target.Root = null;
            _target.Dispose();
        }
        _root?.Dispose();
        _host.Destroy();
        // WM_NCDESTROY leaves the registration to this call, which also covers a window that was never created.
        _registration?.Dispose();
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.Verify();
    }

    /// <summary>The Win32 window behind the layer: it never activates and lets the pointer through.</summary>
    private sealed class Host : NativeWindow
    {
        public void Create()
        {
            // Per-Monitor v2 so the frame is in physical pixels and stays sharp (WIN-REND-06).
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle("Annotator.Platform.TutorialOverlay", ExStyle, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, "CreateWindowEx (tutorial overlay)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (tutorial overlay)");

        public void Verify() => VerifyOwnerThread("The tutorial overlay");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
                case PInvoke.WM_MOUSEACTIVATE:
                    return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                case PInvoke.WM_NCHITTEST:
                    return (LRESULT)PInvoke.HTTRANSPARENT;
                case PInvoke.WM_ERASEBKGND:
                    return (LRESULT)1;
                case PInvoke.WM_CLOSE:
                    return default;
                default:
                    return base.WndProc(message, wParam, lParam);
            }
        }
    }
}
