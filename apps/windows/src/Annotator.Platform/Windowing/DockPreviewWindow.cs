using System.Numerics;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Color = Windows.UI.Color;

namespace Annotator.Platform.Windowing;

/// <summary>
/// The ghost that shows where a dragged toolbar will dock (WIN-OVR-10, spec 009), a port of the Mac's DockPreviewPanel:
/// a rounded rectangle with 12-point corners, filled with the accent at 18% and edged inside its frame with a 2-point
/// accent border at 80%. It is a topmost tool window that never activates and that the pointer passes through, drawn only
/// by Windows.UI.Composition. It registers in the popover layer, one above the toolbar as the Mac's panel level is, before
/// it first shows, so capture leaves it out like every Annotator window. Use it on the thread that created it; that thread
/// runs a message loop and owns the compositor.
/// </summary>
public sealed class DockPreviewWindow : IDisposable
{
    /// <summary>The Mac panel layer's corner radius in points.</summary>
    public const double CornerRadius = 12;

    /// <summary>The Mac panel layer's border width in points.</summary>
    public const double BorderWidth = 2;

    /// <summary>The accent's opacity in the Mac panel layer's background.</summary>
    public const double FillOpacity = 0.18;

    /// <summary>The accent's opacity in the Mac panel layer's border.</summary>
    public const double BorderOpacity = 0.8;

    internal const WINDOW_EX_STYLE ExStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOPMOST |
        WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT |
        WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    private readonly Host _host = new();
    private readonly WindowRegistration? _registration;
    private readonly DesktopWindowTarget? _target;
    private readonly ShapeVisual? _visual;
    private readonly CompositionRoundedRectangleGeometry? _fill;
    private readonly CompositionRoundedRectangleGeometry? _border;
    private readonly CompositionSpriteShape? _fillShape;
    private readonly CompositionSpriteShape? _borderShape;
    private PixelRect _laidOut;
    private double _laidOutScale;
    private bool _disposed;

    /// <summary>Creates the hidden ghost in the <paramref name="accent"/> color.</summary>
    public DockPreviewWindow(WindowRegistry registry, Compositor compositor, Color accent)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(compositor);
        try
        {
            _host.Create();
            HWND hwnd = _host.Handle;
            if (!PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw NativeCallException.FromLastError("SetLayeredWindowAttributes (dock preview)");
            }
            // The magnifier leaves out only windows that were shown once, so it is shown and hidden while cloaked first.
            WindowPriming.ShowOnceCloaked(hwnd);
            _registration = registry.Register((nint)hwnd, WindowLayer.Popover);
            _target = CompositionInterop.CreateDesktopWindowTarget(compositor, (nint)hwnd, isTopmost: true);
            _visual = compositor.CreateShapeVisual();
            _fill = compositor.CreateRoundedRectangleGeometry();
            _fillShape = compositor.CreateSpriteShape(_fill);
            _fillShape.FillBrush = compositor.CreateColorBrush(WithOpacity(accent, FillOpacity));
            _border = compositor.CreateRoundedRectangleGeometry();
            _borderShape = compositor.CreateSpriteShape(_border);
            _borderShape.StrokeBrush = compositor.CreateColorBrush(WithOpacity(accent, BorderOpacity));
            _visual.Shapes.Add(_fillShape);
            _visual.Shapes.Add(_borderShape);
            _target.Root = _visual;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The window handle, or 0 once disposed.</summary>
    public nint Handle => (nint)_host.Handle;

    public bool IsVisible => !_host.Handle.IsNull && PInvoke.IsWindowVisible(_host.Handle);

    /// <summary>
    /// Shows the ghost over <paramref name="frame"/>, in physical pixels, for a display of <paramref name="scale"/> pixels
    /// per point, on top of every window and without activating it.
    /// </summary>
    public void Show(PixelRect frame, double scale)
    {
        Verify();
        if (frame.IsEmpty)
        {
            throw new ArgumentException("The dock preview needs a frame with an area.", nameof(frame));
        }
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The display scale must be positive.");
        }
        if (frame.Width != _laidOut.Width || frame.Height != _laidOut.Height || scale != _laidOutScale)
        {
            Layout(frame.Width, frame.Height, scale);
            _laidOut = frame;
            _laidOutScale = scale;
        }
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        // Physical pixels whatever the calling thread's DPI awareness.
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        if (!PInvoke.SetWindowPos(_host.Handle, HWND.HWND_TOPMOST, frame.Left, frame.Top, frame.Width, frame.Height, flags))
        {
            throw NativeCallException.FromLastError("SetWindowPos (dock preview)");
        }
    }

    /// <summary>Hides the ghost.</summary>
    public void Hide()
    {
        Verify();
        if (PInvoke.IsWindowVisible(_host.Handle))
        {
            PInvoke.ShowWindow(_host.Handle, SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    /// <summary>Releases composition and destroys the window. Call it on the thread that created the ghost.</summary>
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
        _visual?.Dispose();
        _fillShape?.Dispose();
        _borderShape?.Dispose();
        _fill?.Dispose();
        _border?.Dispose();
        _host.Destroy();
        // WM_NCDESTROY leaves the registration to this call, which also covers a window that was never created.
        _registration?.Dispose();
    }

    /// <summary>
    /// The fill covers the whole frame with its rounded corners; the border's center line runs half its width inside the
    /// frame, so it is drawn inside, as a Core Animation border is.
    /// </summary>
    private void Layout(int width, int height, double scale)
    {
        float radius = (float)(CornerRadius * scale);
        float border = (float)(BorderWidth * scale);
        _visual!.Size = new Vector2(width, height);
        _fill!.Size = new Vector2(width, height);
        _fill.CornerRadius = new Vector2(radius);
        _border!.Offset = new Vector2(border / 2);
        _border.Size = new Vector2(Math.Max(0, width - border), Math.Max(0, height - border));
        _border.CornerRadius = new Vector2(Math.Max(0, radius - border / 2));
        _borderShape!.StrokeThickness = border;
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.Verify();
    }

    private static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * color.A, MidpointRounding.AwayFromZero), color.R, color.G, color.B);

    /// <summary>The Win32 window behind the ghost: it never activates and lets the pointer through.</summary>
    private sealed class Host : NativeWindow
    {
        public void Create()
        {
            // Per-Monitor v2 so the ghost's frame is in physical pixels and stays sharp (WIN-REND-06).
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle("Annotator.Platform.DockPreview", ExStyle, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, "CreateWindowEx (dock preview)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (dock preview)");

        public void Verify() => VerifyOwnerThread("The dock preview");

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
