using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using Annotator.Platform.Diagnostics;
using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Platform.Interop;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Input.Pointer;
using Windows.Win32.UI.WindowsAndMessaging;
using DrawingPoint = System.Drawing.Point;

namespace Annotator.Platform.Overlays;

/// <summary>
/// The overlay of one display: a borderless, topmost tool window covering the whole monitor,
/// taskbar area included, with no taskbar button, no Alt+Tab entry and no shadow (WIN-OVR-01).
/// Its only content is the Windows.UI.Composition tree under <see cref="Root"/>, composited with
/// per-pixel alpha by the GPU (WS_EX_NOREDIRECTIONBITMAP). Created and owned by
/// <see cref="OverlayManager"/>; use it only on the overlay thread.
/// </summary>
public sealed unsafe class OverlayWindow
{
    /// <summary>Styles every overlay keeps.</summary>
    internal const WINDOW_EX_STYLE BaseExStyle =
        WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOPMOST |
        WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED;

    /// <summary>
    /// Added outside Draw. WS_EX_TRANSPARENT on a layered window makes it click-through.
    /// WS_EX_NOACTIVATE is removed in Draw because Windows does not activate such a window on a
    /// click, and Draw must activate the clicked canvas (WIN-OVR-03).
    /// </summary>
    internal const WINDOW_EX_STYLE PassThroughExStyle = WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    private const int MaxStackSamples = 32;

    /// <summary>The most positions Windows keeps in its mouse history (GetMouseMovePointsEx).</summary>
    private const int MouseMoveHistory = 64;
    private const int MousePointerId = 1;
    private const uint PointerModShift = 0x0004;
    private const uint PointerModCtrl = 0x0008;
    private const nint ContextAltDown = 1 << 29;
    private const nint PreviousKeyDown = 1 << 30;
    private const nint ExtendedKey = 1 << 24;
    private const uint WindowMessageGetObject = 0x003D;

    /// <summary>Posted to itself to put the canvas back below the toolbar after a change of the stacking order.</summary>
    private const uint RestackMessage = PInvoke.WM_APP + 0x30;

    private static readonly HCURSOR s_arrow = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW);

    private readonly OverlayManager _manager;
    private readonly IOverlayInputSink _sink;
    private readonly OverlayHost _host;
    private WindowRegistry? _registry;
    private WindowRegistration? _registration;
    private DesktopWindowTarget? _target;
    private PointerButtons _mouseButtons;
    private Vector2 _lastMousePosition;

    /// <summary>The last mouse position delivered while a button is down (screen pixels and Windows' event time), or null.</summary>
    private (DrawingPoint Point, uint Time)? _mouseTrail;
    private uint _capturedPointerId;
    private bool _trackingMouseLeave;
    private bool _releasingCapture;
    private bool _restackPosted;
    private Accessibility.OverlayAccessibility? _accessibility;

    private OverlayWindow(OverlayManager manager, DisplayInfo display, IOverlayInputSink sink)
    {
        _manager = manager;
        _sink = sink;
        Display = display;
        _host = new OverlayHost(this);
    }

    /// <summary>The window handle (HWND), or 0 after the overlay is destroyed.</summary>
    public nint Handle => (nint)_host.Handle;

    /// <summary>The display this overlay covers.</summary>
    public DisplayInfo Display { get; }

    /// <summary>
    /// The root visual, sized to the whole monitor in pixels. Add drawing content as children.
    /// When displays change, the manager detaches the children, destroys this overlay and
    /// raises <see cref="OverlayManager.OverlaysRebuilt"/> with new overlays.
    /// </summary>
    public ContainerVisual Root { get; private set; } = null!;

    public OverlayInputMode InputMode { get; private set; }

    public bool IsVisible => !_host.Handle.IsNull && PInvoke.IsWindowVisible(_host.Handle);

    /// <summary>The HCURSOR shown over the overlay; 0 shows the standard arrow.</summary>
    public nint Cursor { get; set; }

    /// <summary>
    /// What UI Automation reads in this overlay (the canvas and its controls), or null for the default window element.
    /// Set it on the overlay thread; the overlay disposes it before the window goes.
    /// </summary>
    public Accessibility.OverlayAccessibility? Accessibility
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

    internal HWND Hwnd => _host.Handle;

    internal static OverlayWindow Create(OverlayManager manager, Compositor compositor, DisplayInfo display, OverlayInputMode mode, WindowRegistry registry, IOverlayInputSink sink)
    {
        if (display.Bounds.IsEmpty)
        {
            throw new ArgumentException($"Display {display.DeviceName} has empty bounds.", nameof(display));
        }

        var overlay = new OverlayWindow(manager, display, sink);
        try
        {
            overlay.InputMode = mode;
            overlay._host.Create(display.Bounds, ExStyleFor(mode));
            HWND hwnd = overlay._host.Handle;
            if (!PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw NativeCallException.FromLastError("SetLayeredWindowAttributes (overlay)");
            }

            // The magnifier leaves out only windows that have been shown at least once (measured, see the README's
            // capture notes), so the empty window is shown and hidden once while cloaked, before it has content.
            WindowPriming.ShowOnceCloaked(hwnd);

            // Registered before the first show so capture can exclude it before it paints (S-1).
            overlay._registration = registry.Register((nint)hwnd, WindowLayer.Canvas);
            overlay._registry = registry;

            overlay._target = CompositionInterop.CreateDesktopWindowTarget(compositor, (nint)hwnd, isTopmost: true);
            ContainerVisual root = compositor.CreateContainerVisual();
            root.RelativeSizeAdjustment = Vector2.One;
            overlay._target.Root = root;
            overlay.Root = root;

            overlay.UpdateVisibility(mode);
            return overlay;
        }
        catch
        {
            overlay.Destroy();
            throw;
        }
    }

    internal static WINDOW_EX_STYLE ExStyleFor(OverlayInputMode mode) =>
        mode == OverlayInputMode.Draw ? BaseExStyle : BaseExStyle | PassThroughExStyle;

    /// <summary>Switches click-through and visibility. The manager calls it for every overlay in one pass.</summary>
    internal void ApplyMode(OverlayInputMode mode)
    {
        HWND hwnd = _host.Handle;
        WINDOW_EX_STYLE current = WindowLongs.GetExStyle(hwnd);
        WINDOW_EX_STYLE desired = mode == OverlayInputMode.Draw ? current & ~PassThroughExStyle : current | PassThroughExStyle;
        if (desired != current)
        {
            WindowLongs.SetExStyle(hwnd, desired);
            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED;
            if (!PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0, flags))
            {
                throw NativeCallException.FromLastError("SetWindowPos(SWP_FRAMECHANGED) (overlay)");
            }
        }
        InputMode = mode;
        UpdateVisibility(mode);
    }

    internal bool TryApplyMode(OverlayInputMode mode)
    {
        try
        {
            ApplyMode(mode);
            return true;
        }
        catch (NativeCallException)
        {
            return false;
        }
    }

    /// <summary>Detaches the app's visuals, releases composition and destroys the window.</summary>
    internal void Destroy()
    {
        Accessibility = null;
        if (Root is not null)
        {
            Root.Children.RemoveAll();
            Root.Dispose();
            Root = null!;
        }
        if (_target is not null)
        {
            _target.Dispose();
            _target = null;
        }
        _host.Destroy();
        // WM_NCDESTROY unregisters; this covers a window that was never created.
        _registration?.Dispose();
    }

    private void UpdateVisibility(OverlayInputMode mode)
    {
        bool visible = PInvoke.IsWindowVisible(_host.Handle);
        if (mode == OverlayInputMode.Hidden && visible)
        {
            PInvoke.ShowWindow(_host.Handle, SHOW_WINDOW_CMD.SW_HIDE);
        }
        else if (mode != OverlayInputMode.Hidden && !visible)
        {
            PInvoke.ShowWindow(_host.Handle, SHOW_WINDOW_CMD.SW_SHOWNA);
        }
    }

    private LRESULT OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_POINTERDOWN:
            case PInvoke.WM_POINTERUPDATE:
            case PInvoke.WM_POINTERUP:
            case PInvoke.WM_POINTERENTER:
            case PInvoke.WM_POINTERLEAVE:
            case PInvoke.WM_POINTERCAPTURECHANGED:
            case PInvoke.WM_POINTERWHEEL:
            case PInvoke.WM_POINTERHWHEEL:
                return OnPointerMessage(message, wParam, lParam);

            case PInvoke.WM_MOUSEMOVE:
            case PInvoke.WM_LBUTTONDOWN:
            case PInvoke.WM_LBUTTONUP:
            case PInvoke.WM_RBUTTONDOWN:
            case PInvoke.WM_RBUTTONUP:
            case PInvoke.WM_MBUTTONDOWN:
            case PInvoke.WM_MBUTTONUP:
            case PInvoke.WM_XBUTTONDOWN:
            case PInvoke.WM_XBUTTONUP:
            case PInvoke.WM_MOUSEWHEEL:
            case PInvoke.WM_MOUSEHWHEEL:
                return OnMouseMessage(message, wParam, lParam);

            case PInvoke.WM_MOUSELEAVE:
                _trackingMouseLeave = false;
                Deliver(PointerInputKind.Leave, PointerDeviceKind.Mouse, MousePointerId, _mouseButtons, PointerButtons.None, 0, _lastMousePosition);
                return default;

            case PInvoke.WM_CAPTURECHANGED:
                _mouseTrail = null;
                if (!_releasingCapture && _mouseButtons != PointerButtons.None)
                {
                    _mouseButtons = PointerButtons.None;
                    Deliver(PointerInputKind.Canceled, PointerDeviceKind.Mouse, MousePointerId, PointerButtons.None, PointerButtons.None, 0, _lastMousePosition);
                }
                if (!_releasingCapture && _capturedPointerId != 0)
                {
                    // A mouse stroke in pointer mode lost its capture to another window.
                    uint pointerId = _capturedPointerId;
                    _capturedPointerId = 0;
                    Deliver(PointerInputKind.Canceled, PointerDeviceKind.Mouse, pointerId, PointerButtons.None, PointerButtons.None, 0, _lastMousePosition);
                }
                return default;

            case PInvoke.WM_KEYDOWN:
            case PInvoke.WM_KEYUP:
            case PInvoke.WM_SYSKEYDOWN:
            case PInvoke.WM_SYSKEYUP:
            case PInvoke.WM_CHAR:
            case PInvoke.WM_SYSCHAR:
                return OnKeyMessage(message, wParam, lParam);

            case PInvoke.WM_SYSCOMMAND:
                switch ((uint)(wParam.Value & 0xFFF0))
                {
                    case PInvoke.SC_CLOSE:
                        _sink.OnInputReleaseRequested(this, InputReleaseReason.CloseRequested);
                        return default;
                    case PInvoke.SC_KEYMENU:
                        // Overlays have no menus; this keeps Alt and F10 from entering menu mode.
                        return default;
                    default:
                        return _host.Default(message, wParam, lParam);
                }

            case PInvoke.WM_CLOSE:
                // Overlays never close; only Quit in the tray menu ends the app (WIN-OVR-12).
                _sink.OnInputReleaseRequested(this, InputReleaseReason.CloseRequested);
                return default;

            case PInvoke.WM_MOUSEACTIVATE:
                return (LRESULT)(nint)(InputMode == OverlayInputMode.Draw ? PInvoke.MA_ACTIVATE : PInvoke.MA_NOACTIVATE);

            case PInvoke.WM_POINTERACTIVATE:
                return (LRESULT)(nint)(InputMode == OverlayInputMode.Draw ? PInvoke.PA_ACTIVATE : PInvoke.PA_NOACTIVATE);

            case PInvoke.WM_WINDOWPOSCHANGING:
                KeepBelowHigherLayers((WINDOWPOS*)lParam.Value);
                return _host.Default(message, wParam, lParam);

            case PInvoke.WM_WINDOWPOSCHANGED:
                CheckBelowHigherLayers((WINDOWPOS*)lParam.Value);
                return _host.Default(message, wParam, lParam);

            case RestackMessage:
                Restack();
                return default;

            case PInvoke.WM_SETFOCUS:
                _sink.OnFocusChanged(this, true);
                return default;

            case PInvoke.WM_KILLFOCUS:
                _sink.OnFocusChanged(this, false);
                return default;

            case PInvoke.WM_SETCURSOR when (uint)(lParam.Value & 0xFFFF) == PInvoke.HTCLIENT:
                PInvoke.SetCursor(Cursor != 0 ? (HCURSOR)Cursor : s_arrow);
                return (LRESULT)1;

            case PInvoke.WM_ERASEBKGND:
                return (LRESULT)1;

            case WindowMessageGetObject when _accessibility is { } accessibility && accessibility.TryAnswer(wParam.Value, lParam.Value, out nint answer):
                return (LRESULT)answer;

            case PInvoke.WM_DPICHANGED:
                // Keep covering the monitor exactly; a scale change rebuilds the overlays.
                _manager.OnOverlayDpiChanged();
                return default;

            default:
                return _host.Default(message, wParam, lParam);
        }
    }

    private LRESULT OnPointerMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        uint pointerId = (uint)(wParam.Value & 0xFFFF);
        if (!PInvoke.GetPointerType(pointerId, out POINTER_INPUT_TYPE type))
        {
            // Not a live pointer, so there is nothing to read.
            return _host.Default(message, wParam, lParam);
        }

        PointerInputKind kind = message switch
        {
            PInvoke.WM_POINTERDOWN => PointerInputKind.Down,
            PInvoke.WM_POINTERUP => PointerInputKind.Up,
            PInvoke.WM_POINTERENTER => PointerInputKind.Enter,
            PInvoke.WM_POINTERLEAVE => PointerInputKind.Leave,
            PInvoke.WM_POINTERCAPTURECHANGED => PointerInputKind.Canceled,
            PInvoke.WM_POINTERWHEEL => PointerInputKind.Wheel,
            PInvoke.WM_POINTERHWHEEL => PointerInputKind.HorizontalWheel,
            _ => PointerInputKind.Move,
        };
        int wheelDelta = kind is PointerInputKind.Wheel or PointerInputKind.HorizontalWheel ? (short)((wParam.Value >> 16) & 0xFFFF) : 0;
        Vector2 origin = ClientOrigin();

        if (type == POINTER_INPUT_TYPE.PT_PEN)
        {
            DeliverPen(pointerId, kind, wheelDelta, origin);
        }
        else
        {
            DeliverPointer(pointerId, type, kind, wheelDelta, origin);
        }

        if (type == POINTER_INPUT_TYPE.PT_MOUSE)
        {
            if (kind == PointerInputKind.Down)
            {
                PInvoke.SetCapture(_host.Handle);
                _capturedPointerId = pointerId;
            }
            else if (kind == PointerInputKind.Canceled && pointerId == _capturedPointerId)
            {
                _capturedPointerId = 0;
            }
            else if (kind == PointerInputKind.Up && PInvoke.GetCapture() == _host.Handle && !AnyButtonDown(pointerId))
            {
                _capturedPointerId = 0;
                ReleaseMouseCapture();
            }
        }
        return default;
    }

    private void DeliverPointer(uint pointerId, POINTER_INPUT_TYPE type, PointerInputKind kind, int wheelDelta, Vector2 origin)
    {
        long dispatch = InputLatencyProbe.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        if (!PInvoke.GetPointerInfo(pointerId, out POINTER_INFO info))
        {
            _manager.ReportFailure(NativeCallException.FromLastError("GetPointerInfo (overlay)"));
            return;
        }

        int count = kind == PointerInputKind.Move ? (int)Math.Max(1u, info.historyCount) : 1;
        POINTER_INFO[]? rented = null;
        Span<POINTER_INFO> history = count <= MaxStackSamples
            ? stackalloc POINTER_INFO[count]
            : (rented = ArrayPool<POINTER_INFO>.Shared.Rent(count)).AsSpan(0, count);
        Span<PointerSample> samples = count <= MaxStackSamples ? stackalloc PointerSample[count] : new PointerSample[count];
        Span<PointerSample> filled = type == POINTER_INPUT_TYPE.PT_MOUSE && kind == PointerInputKind.Move
            ? stackalloc PointerSample[MouseMoveHistory]
            : default;
        try
        {
            history[0] = info;
            if (count > 1)
            {
                uint entries = (uint)count;
                fixed (POINTER_INFO* buffer = history)
                {
                    if (PInvoke.GetPointerInfoHistory(pointerId, &entries, buffer))
                    {
                        count = (int)Math.Min(entries, (uint)count);
                    }
                    else
                    {
                        _manager.ReportFailure(NativeCallException.FromLastError("GetPointerInfoHistory (overlay)"));
                        history[0] = info;
                        count = 1;
                    }
                }
            }

            // History runs from the newest entry to the oldest; samples go oldest first.
            for (int i = 0; i < count; i++)
            {
                POINTER_INFO entry = history[count - 1 - i];
                samples[i] = new PointerSample(ToClient(entry.ptPixelLocation, origin), 0f, entry.dwTime);
            }
            ReadOnlySpan<PointerSample> delivered = samples[..count];
            if (type == POINTER_INPUT_TYPE.PT_MOUSE)
            {
                bool held = ButtonsFrom(info.pointerFlags) != PointerButtons.None;
                if (kind == PointerInputKind.Move && held && _mouseTrail is { } end)
                {
                    int older = MouseMovesSince(end, info.ptPixelLocation, info.dwTime, origin, filled);
                    if (older > 0)
                    {
                        filled[older] = samples[count - 1];
                        delivered = filled[..(older + 1)];
                    }
                }
                _mouseTrail = held && kind is PointerInputKind.Down or PointerInputKind.Move ? (info.ptPixelLocation, info.dwTime) : null;
                _lastMousePosition = samples[count - 1].Position;
            }

            var input = new PointerInput
            {
                Kind = kind,
                Device = DeviceKind(type),
                PointerId = pointerId,
                Buttons = ButtonsFrom(info.pointerFlags),
                ChangedButton = ChangedButtonFrom(info.ButtonChangeType),
                Modifiers = ModifiersFrom(info.dwKeyStates),
                WheelDelta = wheelDelta,
                Samples = delivered,
            };
            _sink.OnPointer(this, in input);
            if (InputLatencyProbe.IsEnabled)
            {
                InputLatencyProbe.Add(new InputLatencyProbe.Record((long)info.PerformanceCount, dispatch, Stopwatch.GetTimestamp(), info.ptPixelLocation.X,
                                                                   info.ptPixelLocation.Y, (int)kind, delivered.Length));
                if (kind is PointerInputKind.Up or PointerInputKind.Canceled)
                {
                    InputLatencyProbe.Flush();
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<POINTER_INFO>.Shared.Return(rented);
            }
        }
    }

    private void DeliverPen(uint pointerId, PointerInputKind kind, int wheelDelta, Vector2 origin)
    {
        if (!PInvoke.GetPointerPenInfo(pointerId, out POINTER_PEN_INFO pen))
        {
            _manager.ReportFailure(NativeCallException.FromLastError("GetPointerPenInfo (overlay)"));
            return;
        }

        int count = kind == PointerInputKind.Move ? (int)Math.Max(1u, pen.pointerInfo.historyCount) : 1;
        POINTER_PEN_INFO[]? rented = null;
        Span<POINTER_PEN_INFO> history = count <= MaxStackSamples
            ? stackalloc POINTER_PEN_INFO[count]
            : (rented = ArrayPool<POINTER_PEN_INFO>.Shared.Rent(count)).AsSpan(0, count);
        Span<PointerSample> samples = count <= MaxStackSamples ? stackalloc PointerSample[count] : new PointerSample[count];
        try
        {
            history[0] = pen;
            if (count > 1)
            {
                uint entries = (uint)count;
                fixed (POINTER_PEN_INFO* buffer = history)
                {
                    if (PInvoke.GetPointerPenInfoHistory(pointerId, &entries, buffer))
                    {
                        count = (int)Math.Min(entries, (uint)count);
                    }
                    else
                    {
                        _manager.ReportFailure(NativeCallException.FromLastError("GetPointerPenInfoHistory (overlay)"));
                        history[0] = pen;
                        count = 1;
                    }
                }
            }

            bool hasPressure = (pen.penMask & PInvoke.PEN_MASK_PRESSURE) != 0;
            for (int i = 0; i < count; i++)
            {
                POINTER_PEN_INFO entry = history[count - 1 - i];
                float pressure = hasPressure ? Math.Clamp(entry.pressure / 1024f, 0f, 1f) : 0f;
                samples[i] = new PointerSample(ToClient(entry.pointerInfo.ptPixelLocation, origin), pressure, entry.pointerInfo.dwTime);
            }

            PenState penState = PenState.None;
            if ((pen.penFlags & PInvoke.PEN_FLAG_BARREL) != 0)
            {
                penState |= PenState.Barrel;
            }
            if ((pen.penFlags & PInvoke.PEN_FLAG_INVERTED) != 0)
            {
                penState |= PenState.Inverted;
            }
            if ((pen.penFlags & PInvoke.PEN_FLAG_ERASER) != 0)
            {
                penState |= PenState.Eraser;
            }

            var input = new PointerInput
            {
                Kind = kind,
                Device = PointerDeviceKind.Pen,
                PointerId = pointerId,
                Buttons = ButtonsFrom(pen.pointerInfo.pointerFlags),
                ChangedButton = ChangedButtonFrom(pen.pointerInfo.ButtonChangeType),
                Modifiers = ModifiersFrom(pen.pointerInfo.dwKeyStates),
                Pen = penState,
                HasPressure = hasPressure,
                WheelDelta = wheelDelta,
                Samples = samples[..count],
            };
            _sink.OnPointer(this, in input);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<POINTER_PEN_INFO>.Shared.Return(rented);
            }
        }
    }

    private LRESULT OnMouseMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        PointerButtons held = MouseButtonsFrom(wParam);
        PointerButtons changed = PointerButtons.None;
        PointerInputKind kind;
        int wheelDelta = 0;
        Vector2 position;
        switch (message)
        {
            case PInvoke.WM_MOUSEWHEEL:
            case PInvoke.WM_MOUSEHWHEEL:
                kind = message == PInvoke.WM_MOUSEWHEEL ? PointerInputKind.Wheel : PointerInputKind.HorizontalWheel;
                wheelDelta = (short)((wParam.Value >> 16) & 0xFFFF);
                // Wheel messages carry screen coordinates.
                position = new Vector2(LowSigned(lParam), HighSigned(lParam)) - ClientOrigin();
                break;
            default:
                position = new Vector2(LowSigned(lParam), HighSigned(lParam));
                (kind, changed) = message switch
                {
                    PInvoke.WM_LBUTTONDOWN => (PointerInputKind.Down, PointerButtons.Left),
                    PInvoke.WM_LBUTTONUP => (PointerInputKind.Up, PointerButtons.Left),
                    PInvoke.WM_RBUTTONDOWN => (PointerInputKind.Down, PointerButtons.Right),
                    PInvoke.WM_RBUTTONUP => (PointerInputKind.Up, PointerButtons.Right),
                    PInvoke.WM_MBUTTONDOWN => (PointerInputKind.Down, PointerButtons.Middle),
                    PInvoke.WM_MBUTTONUP => (PointerInputKind.Up, PointerButtons.Middle),
                    PInvoke.WM_XBUTTONDOWN => (PointerInputKind.Down, XButtonFrom(wParam)),
                    PInvoke.WM_XBUTTONUP => (PointerInputKind.Up, XButtonFrom(wParam)),
                    _ => (PointerInputKind.Move, PointerButtons.None),
                };
                break;
        }

        if (!_trackingMouseLeave && kind != PointerInputKind.Wheel && kind != PointerInputKind.HorizontalWheel)
        {
            var track = new TRACKMOUSEEVENT
            {
                cbSize = (uint)sizeof(TRACKMOUSEEVENT),
                dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE,
                hwndTrack = _host.Handle,
            };
            if (!PInvoke.TrackMouseEvent(ref track))
            {
                _manager.ReportFailure(NativeCallException.FromLastError("TrackMouseEvent(TME_LEAVE) (overlay)"));
            }
            else
            {
                _trackingMouseLeave = true;
                Deliver(PointerInputKind.Enter, PointerDeviceKind.Mouse, MousePointerId, held, PointerButtons.None, 0, position);
            }
        }

        if (kind == PointerInputKind.Down && _mouseButtons == PointerButtons.None)
        {
            PInvoke.SetCapture(_host.Handle);
        }
        _mouseButtons = held;
        _lastMousePosition = position;
        Deliver(kind, PointerDeviceKind.Mouse, MousePointerId, held, changed, wheelDelta, position);
        if (kind == PointerInputKind.Up && held == PointerButtons.None && PInvoke.GetCapture() == _host.Handle)
        {
            ReleaseMouseCapture();
        }

        // WM_XBUTTONDOWN and WM_XBUTTONUP report handling by returning TRUE.
        return message is PInvoke.WM_XBUTTONDOWN or PInvoke.WM_XBUTTONUP ? (LRESULT)1 : default;
    }

    private LRESULT OnKeyMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        nint flags = lParam.Value;
        bool isAltF4 = message is PInvoke.WM_SYSKEYDOWN or PInvoke.WM_SYSKEYUP &&
            wParam.Value == (nuint)VIRTUAL_KEY.VK_F4 && (flags & ContextAltDown) != 0;
        if (isAltF4)
        {
            // Alt+F4 releases drawing input and never closes the overlay (WIN-OVR-12).
            if (message == PInvoke.WM_SYSKEYDOWN && (flags & PreviousKeyDown) == 0)
            {
                _sink.OnInputReleaseRequested(this, InputReleaseReason.AltF4);
            }
            return default;
        }

        KeyInputKind kind = message switch
        {
            PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN => KeyInputKind.KeyDown,
            PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP => KeyInputKind.KeyUp,
            _ => KeyInputKind.Character,
        };
        var input = new KeyInput(
            kind,
            kind == KeyInputKind.Character ? 0 : (int)wParam.Value,
            (int)((flags >> 16) & 0xFF),
            (flags & ExtendedKey) != 0,
            kind == KeyInputKind.KeyDown && (flags & PreviousKeyDown) != 0,
            message is PInvoke.WM_SYSKEYDOWN or PInvoke.WM_SYSKEYUP or PInvoke.WM_SYSCHAR,
            CurrentModifiers(),
            kind == KeyInputKind.Character ? (char)wParam.Value : '\0');
        return _sink.OnKey(this, input) ? default : _host.Default(message, wParam, lParam);
    }

    private void Deliver(PointerInputKind kind, PointerDeviceKind device, uint pointerId, PointerButtons buttons, PointerButtons changed, int wheelDelta, Vector2 position)
    {
        Span<PointerSample> sample = [new PointerSample(position, 0f, (uint)PInvoke.GetMessageTime())];
        var input = new PointerInput
        {
            Kind = kind,
            Device = device,
            PointerId = pointerId,
            Buttons = buttons,
            ChangedButton = changed,
            Modifiers = CurrentModifiers(),
            WheelDelta = wheelDelta,
            Samples = sample,
        };
        _sink.OnPointer(this, in input);
    }

    private void ReleaseMouseCapture()
    {
        _releasingCapture = true;
        try
        {
            PInvoke.ReleaseCapture();
        }
        finally
        {
            _releasingCapture = false;
        }
    }

    /// <summary>
    /// A click in Draw, or focus given back after a popover, activates the canvas, and activation moves a window to the
    /// top of its band. The canvas then stops below the toolbar and popovers instead, so their buttons keep taking clicks
    /// (WIN-OVR-04).
    /// </summary>
    private void KeepBelowHigherLayers(WINDOWPOS* position)
    {
        if (_registry is null || position is null || (position->flags & SET_WINDOW_POS_FLAGS.SWP_NOZORDER) != 0)
        {
            return;
        }
        if (StackOrder.BelowHigherLayers(_registry, _host.Handle, WindowLayer.Canvas, position->hwndInsertAfter) is { } below)
        {
            position->hwndInsertAfter = below;
        }
    }

    /// <summary>
    /// After a change of the stacking order, puts the canvas back below the toolbar and popovers if it ended above them
    /// anyway, once the change is complete.
    /// </summary>
    private void CheckBelowHigherLayers(WINDOWPOS* position)
    {
        if (_registry is null || _restackPosted || position is null || (position->flags & SET_WINDOW_POS_FLAGS.SWP_NOZORDER) != 0 ||
            StackOrder.MisplacedBelow(_registry, _host.Handle, WindowLayer.Canvas) is null)
        {
            return;
        }
        _restackPosted = PInvoke.PostMessage(_host.Handle, RestackMessage, default, default);
        if (!_restackPosted)
        {
            _manager.ReportFailure(NativeCallException.FromLastError("PostMessage (restack the canvas)"));
        }
    }

    private void Restack()
    {
        _restackPosted = false;
        if (_registry is null || StackOrder.MisplacedBelow(_registry, _host.Handle, WindowLayer.Canvas) is not { } below)
        {
            return;
        }
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        if (!PInvoke.SetWindowPos(_host.Handle, below, 0, 0, 0, 0, flags))
        {
            _manager.ReportFailure(NativeCallException.FromLastError("SetWindowPos (keep the canvas below the toolbar)"));
        }
    }

    private void OnDestroyed()
    {
        _registration?.Dispose();
        _registration = null;
    }

    private static bool AnyButtonDown(uint pointerId) =>
        PInvoke.GetPointerInfo(pointerId, out POINTER_INFO info) && ButtonsFrom(info.pointerFlags) != PointerButtons.None;

    private Vector2 ClientOrigin()
    {
        var origin = new DrawingPoint(0, 0);
        if (!PInvoke.ClientToScreen(_host.Handle, ref origin))
        {
            throw NativeCallException.FromLastError("ClientToScreen (overlay)");
        }
        return new Vector2(origin.X, origin.Y);
    }

    private static Vector2 ToClient(DrawingPoint screen, Vector2 origin) => new Vector2(screen.X, screen.Y) - origin;

    /// <summary>
    /// The mouse positions Windows recorded after <paramref name="end"/> and before <paramref name="newest"/>, oldest first, in
    /// <paramref name="output"/>; returns how many. Windows gives a window one move per message, and while the overlay thread
    /// animates, messages can come a frame or more apart: a fast drag then moves tens of pixels between them, and a laser
    /// pointer trail or a stroke made of those moves turns into long straight lines. Windows keeps the last 64 positions, so
    /// the drag keeps its real path. Returns 0 when Windows no longer has <paramref name="newest"/>.
    /// </summary>
    private static unsafe int MouseMovesSince((DrawingPoint Point, uint Time) end, DrawingPoint newest, uint time, Vector2 origin,
                                              Span<PointerSample> output)
    {
        Span<MOUSEMOVEPOINT> points = stackalloc MOUSEMOVEPOINT[MouseMoveHistory];
        // Display points take and give 16-bit coordinates; positions left of or above the main display wrap around.
        var search = new MOUSEMOVEPOINT { x = newest.X & 0xFFFF, y = newest.Y & 0xFFFF, time = time };
        int found;
        fixed (MOUSEMOVEPOINT* buffer = points)
        {
            found = PInvoke.GetMouseMovePointsEx((uint)sizeof(MOUSEMOVEPOINT), &search, buffer, MouseMoveHistory,
                                                 GET_MOUSE_MOVE_POINTS_EX_RESOLUTION.GMMP_USE_DISPLAY_POINTS);
            if (found <= 0)
            {
                // The message's time can differ from the move's; the newest match of the position is the move.
                search.time = 0;
                found = PInvoke.GetMouseMovePointsEx((uint)sizeof(MOUSEMOVEPOINT), &search, buffer, MouseMoveHistory,
                                                     GET_MOUSE_MOVE_POINTS_EX_RESOLUTION.GMMP_USE_DISPLAY_POINTS);
            }
        }
        // The newest entry is the position searched for; an older match than the last delivered move is not this move.
        if (found <= 1 || unchecked((int)(points[0].time - end.Time)) < 0)
        {
            return 0;
        }
        int older = 0;
        for (int index = 1; index < found && older < output.Length - 1; index++)
        {
            MOUSEMOVEPOINT point = points[index];
            int age = unchecked((int)(point.time - end.Time));
            if (age < 0 || age == 0 && Unwrap(point.x) == end.Point.X && Unwrap(point.y) == end.Point.Y)
            {
                break;
            }
            older++;
        }
        for (int index = 0; index < older; index++)
        {
            MOUSEMOVEPOINT point = points[older - index];
            output[index] = new PointerSample(ToClient(new DrawingPoint(Unwrap(point.x), Unwrap(point.y)), origin), 0f, point.time);
        }
        return older;

        static int Unwrap(int coordinate) => coordinate > short.MaxValue ? coordinate - 0x10000 : coordinate;
    }

    private static int LowSigned(LPARAM value) => (short)(value.Value & 0xFFFF);

    private static int HighSigned(LPARAM value) => (short)((value.Value >> 16) & 0xFFFF);

    private static PointerDeviceKind DeviceKind(POINTER_INPUT_TYPE type) => type switch
    {
        POINTER_INPUT_TYPE.PT_TOUCH => PointerDeviceKind.Touch,
        POINTER_INPUT_TYPE.PT_PEN => PointerDeviceKind.Pen,
        POINTER_INPUT_TYPE.PT_TOUCHPAD => PointerDeviceKind.Touchpad,
        _ => PointerDeviceKind.Mouse,
    };

    private static PointerButtons ButtonsFrom(POINTER_FLAGS flags)
    {
        PointerButtons buttons = PointerButtons.None;
        if ((flags & POINTER_FLAGS.POINTER_FLAG_FIRSTBUTTON) != 0)
        {
            buttons |= PointerButtons.Left;
        }
        if ((flags & POINTER_FLAGS.POINTER_FLAG_SECONDBUTTON) != 0)
        {
            buttons |= PointerButtons.Right;
        }
        if ((flags & POINTER_FLAGS.POINTER_FLAG_THIRDBUTTON) != 0)
        {
            buttons |= PointerButtons.Middle;
        }
        if ((flags & POINTER_FLAGS.POINTER_FLAG_FOURTHBUTTON) != 0)
        {
            buttons |= PointerButtons.X1;
        }
        if ((flags & POINTER_FLAGS.POINTER_FLAG_FIFTHBUTTON) != 0)
        {
            buttons |= PointerButtons.X2;
        }
        return buttons;
    }

    private static PointerButtons ChangedButtonFrom(POINTER_BUTTON_CHANGE_TYPE change) => change switch
    {
        POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_FIRSTBUTTON_DOWN or POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_FIRSTBUTTON_UP => PointerButtons.Left,
        POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_SECONDBUTTON_DOWN or POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_SECONDBUTTON_UP => PointerButtons.Right,
        POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_THIRDBUTTON_DOWN or POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_THIRDBUTTON_UP => PointerButtons.Middle,
        POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_FOURTHBUTTON_DOWN or POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_FOURTHBUTTON_UP => PointerButtons.X1,
        POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_FIFTHBUTTON_DOWN or POINTER_BUTTON_CHANGE_TYPE.POINTER_CHANGE_FIFTHBUTTON_UP => PointerButtons.X2,
        _ => PointerButtons.None,
    };

    private static PointerButtons MouseButtonsFrom(WPARAM wParam)
    {
        var keys = (MODIFIERKEYS_FLAGS)(uint)(wParam.Value & 0xFFFF);
        PointerButtons buttons = PointerButtons.None;
        if ((keys & MODIFIERKEYS_FLAGS.MK_LBUTTON) != 0)
        {
            buttons |= PointerButtons.Left;
        }
        if ((keys & MODIFIERKEYS_FLAGS.MK_RBUTTON) != 0)
        {
            buttons |= PointerButtons.Right;
        }
        if ((keys & MODIFIERKEYS_FLAGS.MK_MBUTTON) != 0)
        {
            buttons |= PointerButtons.Middle;
        }
        if ((keys & MODIFIERKEYS_FLAGS.MK_XBUTTON1) != 0)
        {
            buttons |= PointerButtons.X1;
        }
        if ((keys & MODIFIERKEYS_FLAGS.MK_XBUTTON2) != 0)
        {
            buttons |= PointerButtons.X2;
        }
        return buttons;
    }

    private static PointerButtons XButtonFrom(WPARAM wParam) => ((wParam.Value >> 16) & 0xFFFF) == 1 ? PointerButtons.X1 : PointerButtons.X2;

    private static ModifierKeys ModifiersFrom(uint pointerKeyStates)
    {
        ModifierKeys modifiers = CurrentModifiers() & ~(ModifierKeys.Shift | ModifierKeys.Control);
        if ((pointerKeyStates & PointerModShift) != 0)
        {
            modifiers |= ModifierKeys.Shift;
        }
        if ((pointerKeyStates & PointerModCtrl) != 0)
        {
            modifiers |= ModifierKeys.Control;
        }
        return modifiers;
    }

    private static ModifierKeys CurrentModifiers()
    {
        ModifierKeys modifiers = ModifierKeys.None;
        if (IsDown(VIRTUAL_KEY.VK_SHIFT))
        {
            modifiers |= ModifierKeys.Shift;
        }
        if (IsDown(VIRTUAL_KEY.VK_CONTROL))
        {
            modifiers |= ModifierKeys.Control;
        }
        if (IsDown(VIRTUAL_KEY.VK_MENU))
        {
            modifiers |= ModifierKeys.Alt;
        }
        if (IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN))
        {
            modifiers |= ModifierKeys.Windows;
        }
        return modifiers;
    }

    private static bool IsDown(VIRTUAL_KEY key) => PInvoke.GetKeyState((int)key) < 0;

    /// <summary>The Win32 window behind an overlay; it forwards every message to the overlay.</summary>
    private sealed class OverlayHost(OverlayWindow owner) : NativeWindow
    {
        public void Create(PixelRect bounds, WINDOW_EX_STYLE exStyle)
        {
            // Per-Monitor v2 so the window covers the monitor in physical pixels and stays sharp.
            using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
            CreateHandle("Annotator.Platform.Overlay", exStyle, WINDOW_STYLE.WS_POPUP, bounds.Left, bounds.Top, bounds.Width, bounds.Height, HWND.Null, "CreateWindowEx (overlay)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (overlay)");

        public LRESULT Default(uint message, WPARAM wParam, LPARAM lParam) => base.WndProc(message, wParam, lParam);

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam) => owner.OnMessage(message, wParam, lParam);

        protected override void OnDestroyed(HWND destroyed) => owner.OnDestroyed();
    }
}
