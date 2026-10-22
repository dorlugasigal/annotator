using System.Numerics;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Color = Windows.UI.Color;

namespace Annotator.Platform.Sharing;

/// <summary>A rectangle in points from a display's top-left corner.</summary>
public readonly record struct SelectionRect(double X, double Y, double Width, double Height);

/// <summary>
/// One display's panel for drawing a custom sharing region (specification 012, the Mac's RegionSelectionPanel and
/// RegionSelectionView): the display shaded with black at 28% except the rectangle being dragged, which has a 2-point
/// accent border, with a crosshair pointer. A drag in any direction chooses a rectangle, clamped to this display; Escape
/// and Alt+F4 cancel. The panel is a topmost window above every other Annotator window (the Mac's screen-saver level) that
/// takes input and can take the keyboard; it registers in the guide layer before it first shows, so Annotator's own
/// capture leaves it out. The caller adds the instruction text to <see cref="Overlay"/>. Use it on the thread that created
/// it; that thread runs a message loop and owns the compositor.
/// </summary>
public sealed class RegionSelectionPanel : IDisposable
{
    /// <summary>The Mac's shade: black at 28%.</summary>
    public const double ShadeOpacity = 0.28;

    /// <summary>The selection's border width in points, centered on its edge.</summary>
    public const double BorderWidth = 2;

    internal const WINDOW_EX_STYLE ExStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOPMOST |
        WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED;

    private const uint FirstButton = 0x0010;
    private static readonly HCURSOR s_crosshair = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_CROSS);

    private readonly Host _host;
    private readonly WindowRegistration? _registration;
    private readonly DesktopWindowTarget? _target;
    private readonly ContainerVisual? _root;
    private readonly SpriteVisual[] _shade = new SpriteVisual[4];
    private readonly ShapeVisual? _shape;
    private readonly CompositionRectangleGeometry? _border;
    private readonly CompositionSpriteShape? _borderShape;
    private (double X, double Y)? _start;
    private SelectionRect? _selection;
    private bool _released;
    private bool _disposed;

    /// <summary>Creates the hidden panel over <paramref name="display"/>, with the selection border in <paramref name="accent"/>.</summary>
    public RegionSelectionPanel(WindowRegistry registry, Compositor compositor, DisplayInfo display, Color accent)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(display);
        if (display.Bounds.IsEmpty)
        {
            throw new ArgumentException($"Display {display.DeviceName} has empty bounds.", nameof(display));
        }
        Display = display;
        _host = new Host(this);
        try
        {
            _host.Create(display.Bounds);
            HWND hwnd = _host.Handle;
            if (!PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw NativeCallException.FromLastError("SetLayeredWindowAttributes (region selection)");
            }
            WindowPriming.ShowOnceCloaked(hwnd);
            _registration = registry.Register((nint)hwnd, WindowLayer.Guide);
            _target = CompositionInterop.CreateDesktopWindowTarget(compositor, (nint)hwnd, isTopmost: true);
            _root = compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            CompositionColorBrush shade = compositor.CreateColorBrush(Color.FromArgb((byte)Math.Round(ShadeOpacity * 255), 0, 0, 0));
            for (int index = 0; index < _shade.Length; index++)
            {
                _shade[index] = compositor.CreateSpriteVisual();
                _shade[index].Brush = shade;
                _root.Children.InsertAtTop(_shade[index]);
            }
            _shape = compositor.CreateShapeVisual();
            _shape.RelativeSizeAdjustment = Vector2.One;
            _border = compositor.CreateRectangleGeometry();
            _borderShape = compositor.CreateSpriteShape(_border);
            _borderShape.StrokeBrush = compositor.CreateColorBrush(accent);
            _borderShape.StrokeThickness = (float)(BorderWidth * display.Scale);
            _shape.Shapes.Add(_borderShape);
            _shape.IsVisible = false;
            _root.Children.InsertAtTop(_shape);
            Overlay = compositor.CreateContainerVisual();
            Overlay.RelativeSizeAdjustment = Vector2.One;
            _root.Children.InsertAtTop(Overlay);
            _target.Root = _root;
            Layout();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The display this panel covers.</summary>
    public DisplayInfo Display { get; }

    /// <summary>Visuals above the shade and the border, such as the instruction text, in physical pixels.</summary>
    public ContainerVisual Overlay { get; } = null!;

    /// <summary>The window handle, or 0 once disposed.</summary>
    public nint Handle => (nint)_host.Handle;

    public bool IsVisible => !_host.Handle.IsNull && PInvoke.IsWindowVisible(_host.Handle);

    /// <summary>True while the panel takes input: from <see cref="Show"/> until <see cref="Release"/>.</summary>
    public bool TakesInput => !_released && IsVisible;

    /// <summary>Whether this panel is the foreground window (it has the keyboard).</summary>
    public bool IsActive => !_host.Handle.IsNull && PInvoke.GetForegroundWindow() == _host.Handle;

    /// <summary>The rectangle dragged so far, in points, or null.</summary>
    public SelectionRect? Selection => _selection;

    /// <summary>A press was released: the rectangle from the press to the release, clamped to this display, or null without a press.</summary>
    public event Action<RegionSelectionPanel, SelectionRect?>? Completed;

    /// <summary>Escape or Alt+F4 cancelled the selection.</summary>
    public event Action<RegionSelectionPanel>? Cancelled;

    /// <summary>The panel lost the activation; the keyboard may have gone to another panel or outside them.</summary>
    public event Action<RegionSelectionPanel>? Deactivated;

    /// <summary>Shows the panel over its display, topmost and without activating it.</summary>
    public void Show()
    {
        Verify();
        _released = false;
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
            SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        PixelRect bounds = Display.Bounds;
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        if (!PInvoke.SetWindowPos(_host.Handle, HWND.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags))
        {
            throw NativeCallException.FromLastError("SetWindowPos (region selection)");
        }
    }

    /// <summary>Gives the panel the keyboard (the Mac's makeKey). Returns false when Windows refused the foreground.</summary>
    public bool Activate()
    {
        Verify();
        return PInvoke.SetForegroundWindow(_host.Handle) || IsActive;
    }

    /// <summary>
    /// Ends the panel's input and hides it (the Mac's ignoresMouseEvents and orderOut): a press in progress is dropped and
    /// the mouse capture released.
    /// </summary>
    public void Release()
    {
        Verify();
        _released = true;
        _start = null;
        if (PInvoke.GetCapture() == _host.Handle)
        {
            PInvoke.ReleaseCapture();
        }
        if (PInvoke.IsWindowVisible(_host.Handle))
        {
            PInvoke.ShowWindow(_host.Handle, SHOW_WINDOW_CMD.SW_HIDE);
        }
    }

    /// <summary>Releases composition and destroys the window. Call it on the thread that created the panel.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _released = true;
        if (_target is not null)
        {
            _target.Root = null;
            _target.Dispose();
        }
        foreach (SpriteVisual? band in _shade)
        {
            band?.Dispose();
        }
        _borderShape?.Dispose();
        _border?.Dispose();
        _shape?.Dispose();
        Overlay?.Dispose();
        _root?.Dispose();
        _host.Destroy();
        _registration?.Dispose();
    }

    /// <summary>Test access: a press, moves and a release at points on this display, as the pointer would deliver them.</summary>
    internal void Drag(Vector2 from, Vector2 to)
    {
        Press((from.X, from.Y));
        Move((to.X, to.Y));
        Finish((to.X, to.Y));
    }

    private void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _host.Verify();
    }

    /// <summary>The Mac's draw: the shade around the selection (an even-odd hole), and its border.</summary>
    private void Layout()
    {
        float width = Display.Bounds.Width, height = Display.Bounds.Height;
        if (_selection is not { } selection)
        {
            Band(0, 0, 0, width, height);
            for (int index = 1; index < _shade.Length; index++)
            {
                _shade[index].IsVisible = false;
            }
            _shape!.IsVisible = false;
            return;
        }
        double scale = Display.Scale;
        float left = (float)(selection.X * scale), top = (float)(selection.Y * scale);
        float right = (float)((selection.X + selection.Width) * scale), bottom = (float)((selection.Y + selection.Height) * scale);
        Band(0, 0, 0, width, top);
        Band(1, 0, bottom, width, height - bottom);
        Band(2, 0, top, left, bottom - top);
        Band(3, right, top, width - right, bottom - top);
        _border!.Offset = new Vector2(left, top);
        _border.Size = new Vector2(right - left, bottom - top);
        _shape!.IsVisible = true;
    }

    private void Band(int index, float x, float y, float width, float height)
    {
        SpriteVisual band = _shade[index];
        band.Offset = new Vector3(x, y, 0);
        band.Size = new Vector2(Math.Max(0, width), Math.Max(0, height));
        band.IsVisible = width > 0 && height > 0;
    }

    private (double X, double Y) ToPoints(int screenX, int screenY) =>
        ((screenX - Display.Bounds.Left) / Display.Scale, (screenY - Display.Bounds.Top) / Display.Scale);

    private void Press((double X, double Y) point)
    {
        if (_released)
        {
            return;
        }
        _start = point;
        _selection = null;
        Layout();
    }

    /// <summary>The Mac's mouseDragged: the rectangle from the press to the pointer, clamped to the display's bounds.</summary>
    private void Move((double X, double Y) point)
    {
        if (_released || _start is not { } start)
        {
            return;
        }
        double width = Display.Bounds.Width / Display.Scale, height = Display.Bounds.Height / Display.Scale;
        double x = Math.Clamp(point.X, 0, width), y = Math.Clamp(point.Y, 0, height);
        _selection = new SelectionRect(Math.Min(start.X, x), Math.Min(start.Y, y), Math.Abs(x - start.X), Math.Abs(y - start.Y));
        Layout();
    }

    /// <summary>The Mac's mouseUp: the last drag, then the result (null when the press never reached this panel).</summary>
    private void Finish((double X, double Y) point)
    {
        if (_released)
        {
            return;
        }
        Move(point);
        SelectionRect? result = _start is null ? null : _selection;
        _start = null;
        Completed?.Invoke(this, result);
    }

    private LRESULT OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_POINTERDOWN:
                if (((wParam.Value >> 16) & FirstButton) != 0)
                {
                    PInvoke.SetCapture(_host.Handle);
                    Press(ToPoints(Low(lParam), High(lParam)));
                }
                return default;
            case PInvoke.WM_POINTERUPDATE:
                Move(ToPoints(Low(lParam), High(lParam)));
                return default;
            case PInvoke.WM_POINTERUP:
                if (PInvoke.GetCapture() == _host.Handle)
                {
                    PInvoke.ReleaseCapture();
                }
                if (_start is not null)
                {
                    Finish(ToPoints(Low(lParam), High(lParam)));
                }
                return default;
            case PInvoke.WM_LBUTTONDOWN:
                PInvoke.SetCapture(_host.Handle);
                Press(ClientToPoints(lParam));
                return default;
            case PInvoke.WM_MOUSEMOVE:
                Move(ClientToPoints(lParam));
                return default;
            case PInvoke.WM_LBUTTONUP:
                if (PInvoke.GetCapture() == _host.Handle)
                {
                    PInvoke.ReleaseCapture();
                }
                if (_start is not null)
                {
                    Finish(ClientToPoints(lParam));
                }
                return default;
            case PInvoke.WM_KEYDOWN when (VIRTUAL_KEY)wParam.Value == VIRTUAL_KEY.VK_ESCAPE:
                if (!_released)
                {
                    Cancelled?.Invoke(this);
                }
                return default;
            case PInvoke.WM_CLOSE:
                if (!_released)
                {
                    Cancelled?.Invoke(this);
                }
                return default;
            case PInvoke.WM_ACTIVATE when (wParam.Value & 0xFFFF) == PInvoke.WA_INACTIVE:
                if (!_released)
                {
                    Deactivated?.Invoke(this);
                }
                break;
            case PInvoke.WM_MOUSEACTIVATE:
                return (LRESULT)(nint)PInvoke.MA_ACTIVATE;
            case PInvoke.WM_POINTERACTIVATE:
                return (LRESULT)(nint)PInvoke.PA_ACTIVATE;
            case PInvoke.WM_SETCURSOR when (uint)(lParam.Value & 0xFFFF) == PInvoke.HTCLIENT:
                PInvoke.SetCursor(s_crosshair);
                return (LRESULT)1;
            case PInvoke.WM_ERASEBKGND:
                return (LRESULT)1;
        }
        return PInvoke.DefWindowProc(_host.Handle, message, wParam, lParam);
    }

    private (double X, double Y) ClientToPoints(LPARAM lParam) =>
        (Low(lParam) / Display.Scale, High(lParam) / Display.Scale);

    private static int Low(LPARAM value) => (short)(value.Value & 0xFFFF);

    private static int High(LPARAM value) => (short)((value.Value >> 16) & 0xFFFF);

    /// <summary>The Win32 window behind the panel.</summary>
    private sealed class Host(RegionSelectionPanel owner) : NativeWindow
    {
        public void Create(PixelRect bounds)
        {
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle("Annotator.Platform.RegionSelection", ExStyle, WINDOW_STYLE.WS_POPUP, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                         HWND.Null, "CreateWindowEx (region selection)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (region selection)");

        public void Verify() => VerifyOwnerThread("The region selection panel");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam) => owner.OnMessage(message, wParam, lParam);
    }
}
