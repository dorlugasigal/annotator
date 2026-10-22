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

/// <summary>How a <see cref="LensWindow"/> looks; the defaults are the Mac lens panel's.</summary>
public sealed record LensWindowOptions
{
    /// <summary>The border color (the Mac's controlAccentColor).</summary>
    public required Color Accent { get; init; }

    /// <summary>Corner radius in points (Mac view.layer.cornerRadius).</summary>
    public double CornerRadius { get; init; } = 18;

    /// <summary>Border width in points, drawn inside the frame (Mac view.layer.borderWidth).</summary>
    public double BorderWidth { get; init; } = 2;

    /// <summary>Half the length of each crosshair arm in points (Mac LensGuideView).</summary>
    public double CrosshairArm { get; init; } = 6;

    /// <summary>The fade when the lens first shows (Mac NSAnimationContext 0.12 s with Motion.fade).</summary>
    public TimeSpan FadeDuration { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>The fade's cubic Bezier control points (MotionCurves.Fade).</summary>
    public Vector2 FadeControl1 { get; init; } = new(0.2f, 0);

    public Vector2 FadeControl2 { get; init; } = new(0.4f, 1);

    /// <summary>
    /// The lens's own shadow (Mac panel.hasShadow), in points: the blur radius, the downward offset and the opacity of black.
    /// </summary>
    public double ShadowBlur { get; init; } = 16;

    public double ShadowOffset { get; init; } = 4;

    public double ShadowOpacity { get; init; } = 0.35;
}

/// <summary>
/// The pointer lens's window (WIN-CAP-06), a port of the Mac lens panel: a rounded square with an accent border, its own
/// shadow and a crosshair at the source point, above the canvas and below the toolbar. The magnified screen goes into
/// <see cref="Content"/>, clipped to the rounded square. It is a topmost tool window that never activates and that the
/// pointer passes through, drawn only by Windows.UI.Composition, registered in the adornment layer before it first shows
/// so capture leaves it out like every Annotator window. Use it on the thread that created it; that thread runs a message
/// loop and owns the compositor.
/// </summary>
public sealed class LensWindow : IDisposable
{
    private readonly Host _host;
    private readonly WindowRegistration? _registration;
    private readonly Compositor _compositor;
    private readonly LensWindowOptions _options;
    private readonly DesktopWindowTarget? _target;
    private readonly ContainerVisual? _root;
    private readonly LayerVisual? _shadowLayer;
    private readonly ShapeVisual? _shadowShape;
    private readonly CompositionRoundedRectangleGeometry? _shadowGeometry;
    private readonly DropShadow? _shadow;
    private readonly ContainerVisual? _lens;
    private readonly CompositionRoundedRectangleGeometry? _clipGeometry;
    private readonly ContainerVisual? _content;
    private readonly ShapeVisual? _crosshair;
    private readonly CompositionLineGeometry[] _lines = new CompositionLineGeometry[4];
    private readonly CompositionSpriteShape[] _lineShapes = new CompositionSpriteShape[4];
    private readonly ShapeVisual? _border;
    private readonly CompositionRoundedRectangleGeometry? _borderGeometry;
    private readonly CompositionSpriteShape? _borderShape;
    private int _laidOutSize;
    private double _laidOutScale;
    private int _margin;
    private bool _faded;
    private bool _disposed;

    /// <summary>Creates the hidden lens window.</summary>
    public LensWindow(WindowRegistry registry, Compositor compositor, LensWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(options);
        _compositor = compositor;
        _options = options;
        _host = new Host(registry);
        try
        {
            _host.Create();
            HWND hwnd = _host.Handle;
            if (!PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw NativeCallException.FromLastError("SetLayeredWindowAttributes (pointer lens)");
            }
            // The magnifier leaves out only windows that were shown once, so it is shown and hidden while cloaked first.
            WindowPriming.ShowOnceCloaked(hwnd);
            _registration = registry.Register((nint)hwnd, WindowLayer.Adornments);
            _target = CompositionInterop.CreateDesktopWindowTarget(compositor, (nint)hwnd, isTopmost: true);
            _root = compositor.CreateContainerVisual();
            _shadowGeometry = compositor.CreateRoundedRectangleGeometry();
            CompositionSpriteShape shadowFill = compositor.CreateSpriteShape(_shadowGeometry);
            shadowFill.FillBrush = compositor.CreateColorBrush(Color.FromArgb(255, 0, 0, 0));
            _shadowShape = compositor.CreateShapeVisual();
            _shadowShape.Shapes.Add(shadowFill);
            _shadow = compositor.CreateDropShadow();
            _shadow.Color = Color.FromArgb(255, 0, 0, 0);
            _shadow.Opacity = (float)options.ShadowOpacity;
            _shadowLayer = compositor.CreateLayerVisual();
            _shadowLayer.Shadow = _shadow;
            _shadowLayer.Children.InsertAtTop(_shadowShape);
            _root.Children.InsertAtTop(_shadowLayer);
            _clipGeometry = compositor.CreateRoundedRectangleGeometry();
            _lens = compositor.CreateContainerVisual();
            _lens.Clip = compositor.CreateGeometricClip(_clipGeometry);
            _content = compositor.CreateContainerVisual();
            _lens.Children.InsertAtTop(_content);
            _crosshair = compositor.CreateShapeVisual();
            CompositionColorBrush dark = compositor.CreateColorBrush(Color.FromArgb((byte)Math.Round(0.7 * 255), 0, 0, 0));
            CompositionColorBrush light = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
            for (int index = 0; index < 4; index++)
            {
                _lines[index] = compositor.CreateLineGeometry();
                _lineShapes[index] = compositor.CreateSpriteShape(_lines[index]);
                _lineShapes[index].StrokeBrush = index < 2 ? dark : light;
                _crosshair.Shapes.Add(_lineShapes[index]);
            }
            _lens.Children.InsertAtTop(_crosshair);
            _root.Children.InsertAtTop(_lens);
            _borderGeometry = compositor.CreateRoundedRectangleGeometry();
            _borderShape = compositor.CreateSpriteShape(_borderGeometry);
            _borderShape.StrokeBrush = compositor.CreateColorBrush(options.Accent);
            _border = compositor.CreateShapeVisual();
            _border.Shapes.Add(_borderShape);
            _root.Children.InsertAtTop(_border);
            _root.Opacity = 0;
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

    /// <summary>Where the magnified screen goes: a lens-sized container, clipped to the rounded square, under the crosshair.</summary>
    public ContainerVisual Content => _content ?? throw new ObjectDisposedException(nameof(LensWindow));

    /// <summary>The lens's size in device pixels as last shown, or 0.</summary>
    public int PixelSize => _laidOutSize;

    /// <summary>
    /// Shows the lens over <paramref name="lens"/> (a square in physical pixels) on a display of <paramref name="scale"/>
    /// pixels per point, with the crosshair at <paramref name="crosshair"/> (pixels from the lens's top-left corner), on top
    /// of the canvas and without activating anything. The first show fades in; later ones, after the pointer left the
    /// display, appear at once (Mac renderLens).
    /// </summary>
    public void Show(PixelRect lens, double scale, Vector2 crosshair)
    {
        Verify();
        if (lens.IsEmpty || lens.Width != lens.Height)
        {
            throw new ArgumentException("The lens needs a square frame with an area.", nameof(lens));
        }
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The display scale must be positive.");
        }
        if (lens.Width != _laidOutSize || scale != _laidOutScale)
        {
            Layout(lens.Width, scale);
        }
        MoveCrosshair(crosshair);
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        // Physical pixels whatever the calling thread's DPI awareness.
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        if (!PInvoke.SetWindowPos(_host.Handle, HWND.HWND_TOPMOST, lens.Left - _margin, lens.Top - _margin, lens.Width + 2 * _margin,
                                  lens.Height + 2 * _margin, flags))
        {
            throw NativeCallException.FromLastError("SetWindowPos (pointer lens)");
        }
        if (!_faded)
        {
            _faded = true;
            ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, _compositor.CreateCubicBezierEasingFunction(_options.FadeControl1, _options.FadeControl2));
            fade.Duration = _options.FadeDuration;
            _root!.StartAnimation("Opacity", fade);
        }
    }

    /// <summary>Moves the crosshair to <paramref name="crosshair"/>, pixels from the lens's top-left corner.</summary>
    public void MoveCrosshair(Vector2 crosshair)
    {
        Verify();
        float arm = (float)(_options.CrosshairArm * _laidOutScale);
        for (int index = 0; index < 4; index++)
        {
            bool horizontal = index % 2 == 0;
            _lines[index].Start = crosshair + (horizontal ? new Vector2(-arm, 0) : new Vector2(0, -arm));
            _lines[index].End = crosshair + (horizontal ? new Vector2(arm, 0) : new Vector2(0, arm));
        }
    }

    /// <summary>Hides the lens at once (the pointer left its display).</summary>
    public void Hide()
    {
        Verify();
        if (PInvoke.IsWindowVisible(_host.Handle))
        {
            PInvoke.ShowWindow(_host.Handle, SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    /// <summary>Releases composition and destroys the window. Call it on the thread that created the lens.</summary>
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

    /// <summary>
    /// The window is the lens plus room for its shadow. The border's center line runs half its width inside the frame, so it
    /// is drawn inside, as a Core Animation border is.
    /// </summary>
    private void Layout(int size, double scale)
    {
        _laidOutSize = size;
        _laidOutScale = scale;
        float radius = (float)(_options.CornerRadius * scale);
        float border = (float)(_options.BorderWidth * scale);
        float blur = (float)(_options.ShadowBlur * scale);
        _margin = (int)Math.Ceiling(blur + _options.ShadowOffset * scale);
        var lensSize = new Vector2(size);
        var offset = new Vector3(_margin, _margin, 0);
        _root!.Size = new Vector2(size + 2 * _margin);
        _shadowLayer!.Offset = offset;
        _shadowLayer.Size = lensSize;
        _shadowShape!.Size = lensSize;
        _shadowGeometry!.Size = lensSize;
        _shadowGeometry.CornerRadius = new Vector2(radius);
        _shadow!.BlurRadius = blur;
        _shadow.Offset = new Vector3(0, (float)(_options.ShadowOffset * scale), 0);
        _lens!.Offset = offset;
        _lens.Size = lensSize;
        _clipGeometry!.Size = lensSize;
        _clipGeometry.CornerRadius = new Vector2(radius);
        _content!.Size = lensSize;
        _crosshair!.Size = lensSize;
        for (int index = 0; index < 4; index++)
        {
            _lineShapes[index].StrokeThickness = (float)((index < 2 ? 3 : 1) * scale);
        }
        _border!.Offset = offset;
        _border.Size = lensSize;
        _borderGeometry!.Offset = new Vector2(border / 2);
        _borderGeometry.Size = new Vector2(Math.Max(0, size - border));
        _borderGeometry.CornerRadius = new Vector2(Math.Max(0, radius - border / 2));
        _borderShape!.StrokeThickness = border;
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.Verify();
    }

    /// <summary>The Win32 window behind the lens: it never activates, lets the pointer through and stays below higher layers.</summary>
    private sealed unsafe class Host(WindowRegistry registry) : NativeWindow
    {
        public void Create()
        {
            // Per-Monitor v2 so the lens's frame is in physical pixels and stays sharp (WIN-REND-06).
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle("Annotator.Platform.PointerLens", DockPreviewWindow.ExStyle, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null,
                         "CreateWindowEx (pointer lens)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (pointer lens)");

        public void Verify() => VerifyOwnerThread("The pointer lens");

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
                case PInvoke.WM_WINDOWPOSCHANGING:
                    // Shown topmost, the lens stops below the toolbar and popovers, as the Mac's floating+1 panel does.
                    var position = (WINDOWPOS*)lParam.Value;
                    if (position is not null && (position->flags & SET_WINDOW_POS_FLAGS.SWP_NOZORDER) == 0
                        && StackOrder.BelowHigherLayers(registry, Handle, WindowLayer.Adornments, position->hwndInsertAfter) is { } below)
                    {
                        position->hwndInsertAfter = below;
                    }
                    return base.WndProc(message, wParam, lParam);
                default:
                    return base.WndProc(message, wParam, lParam);
            }
        }
    }
}
