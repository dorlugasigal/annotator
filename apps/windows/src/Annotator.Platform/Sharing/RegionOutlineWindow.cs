using System.Numerics;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Color = Windows.UI.Color;

namespace Annotator.Platform.Sharing;

/// <summary>
/// The shared region's outline on the desktop (specification 012, the Mac's RegionOutlinePanel and RegionOutlineView): a
/// 2-point accent border at 95% inside the region's frame, and a faint accent fill (8%) only while the person chooses a
/// region. It is a topmost tool window above every other Annotator window (the Mac's screen-saver level) that never
/// activates and lets every click through, drawn only by Windows.UI.Composition. It registers in the guide layer before
/// it first shows, so Annotator's own capture leaves it out; other apps' captures can show it. Use it on the thread that
/// created it; that thread runs a message loop and owns the compositor.
/// </summary>
public sealed class RegionOutlineWindow : IDisposable
{
    /// <summary>The Mac's RegionOutlineView.fillOpacity.</summary>
    public const double FillOpacity = 0.08;

    /// <summary>The border's opacity (the Mac's controlAccentColor at 0.95).</summary>
    public const double BorderOpacity = 0.95;

    /// <summary>The border's width in points, inside the frame.</summary>
    public const double BorderWidth = 2;

    internal const WINDOW_EX_STYLE ExStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOPMOST |
        WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT |
        WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    private readonly Host _host = new();
    private readonly WindowRegistration? _registration;
    private readonly DesktopWindowTarget? _target;
    private readonly ContainerVisual? _root;
    private readonly SpriteVisual? _fill;
    private readonly ShapeVisual? _shape;
    private readonly CompositionRectangleGeometry? _border;
    private readonly CompositionSpriteShape? _borderShape;
    private PixelRect _laidOut;
    private double _laidOutScale;
    private bool _disposed;

    /// <summary>Creates the hidden outline in the <paramref name="accent"/> color.</summary>
    public RegionOutlineWindow(WindowRegistry registry, Compositor compositor, Color accent)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(compositor);
        try
        {
            _host.Create();
            HWND hwnd = _host.Handle;
            if (!PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw NativeCallException.FromLastError("SetLayeredWindowAttributes (region outline)");
            }
            // The magnifier leaves out only windows that were shown once, so it is shown and hidden while cloaked first.
            WindowPriming.ShowOnceCloaked(hwnd);
            _registration = registry.Register((nint)hwnd, WindowLayer.Guide);
            _target = CompositionInterop.CreateDesktopWindowTarget(compositor, (nint)hwnd, isTopmost: true);
            _root = compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            _fill = compositor.CreateSpriteVisual();
            _fill.RelativeSizeAdjustment = Vector2.One;
            _fill.Brush = compositor.CreateColorBrush(WithOpacity(accent, FillOpacity));
            _shape = compositor.CreateShapeVisual();
            _shape.RelativeSizeAdjustment = Vector2.One;
            _border = compositor.CreateRectangleGeometry();
            _borderShape = compositor.CreateSpriteShape(_border);
            _borderShape.StrokeBrush = compositor.CreateColorBrush(WithOpacity(accent, BorderOpacity));
            _shape.Shapes.Add(_borderShape);
            _root.Children.InsertAtTop(_fill);
            _root.Children.InsertAtTop(_shape);
            _target.Root = _root;
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

    /// <summary>Whether the fill shows (Mac outlineFillOpacity is 0.08 while choosing, 0 while sharing).</summary>
    public bool ShowsFill => _fill?.IsVisible == true;

    /// <summary>The frame shown last, in physical pixels.</summary>
    public PixelRect Frame => _laidOut;

    /// <summary>
    /// Shows the outline over <paramref name="frame"/> (physical pixels) on a display of <paramref name="scale"/> pixels
    /// per point, with the selection fill when <paramref name="selectionFill"/> is set, on top of every window and without
    /// activating it.
    /// </summary>
    public void Show(PixelRect frame, double scale, bool selectionFill)
    {
        Verify();
        if (frame.IsEmpty)
        {
            throw new ArgumentException("The region outline needs a frame with an area.", nameof(frame));
        }
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The display scale must be positive.");
        }
        if (frame.Width != _laidOut.Width || frame.Height != _laidOut.Height || scale != _laidOutScale)
        {
            Layout(frame.Width, frame.Height, scale);
        }
        _laidOut = frame;
        _laidOutScale = scale;
        _fill!.IsVisible = selectionFill;
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        // Physical pixels whatever the calling thread's DPI awareness.
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        if (!PInvoke.SetWindowPos(_host.Handle, HWND.HWND_TOPMOST, frame.Left, frame.Top, frame.Width, frame.Height, flags))
        {
            throw NativeCallException.FromLastError("SetWindowPos (region outline)");
        }
    }

    /// <summary>Hides the outline.</summary>
    public void Hide()
    {
        Verify();
        if (PInvoke.IsWindowVisible(_host.Handle))
        {
            PInvoke.ShowWindow(_host.Handle, SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    /// <summary>Releases composition and destroys the window. Call it on the thread that created the outline.</summary>
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
        _fill?.Dispose();
        _shape?.Dispose();
        _borderShape?.Dispose();
        _border?.Dispose();
        _host.Destroy();
        // WM_NCDESTROY leaves the registration to this call, which also covers a window that was never created.
        _registration?.Dispose();
    }

    /// <summary>
    /// The Mac strokes a 2-point path around the frame inset by one point, so the border covers the frame's outer two
    /// points. A composition stroke is centered on its geometry the same way.
    /// </summary>
    private void Layout(int width, int height, double scale)
    {
        float border = (float)(BorderWidth * scale);
        _border!.Offset = new Vector2(border / 2);
        _border.Size = new Vector2(Math.Max(0, width - border), Math.Max(0, height - border));
        _borderShape!.StrokeThickness = border;
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.Verify();
    }

    internal static Color WithOpacity(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * color.A, MidpointRounding.AwayFromZero), color.R, color.G, color.B);

    /// <summary>The Win32 window behind the outline: it never activates and lets the pointer through.</summary>
    private sealed class Host : NativeWindow
    {
        public void Create()
        {
            // Per-Monitor v2 so the frame is in physical pixels and stays sharp (WIN-REND-06).
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle("Annotator.Platform.RegionOutline", ExStyle, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, "CreateWindowEx (region outline)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (region outline)");

        public void Verify() => VerifyOwnerThread("The region outline");

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
