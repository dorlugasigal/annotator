using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Interop;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Capture;

/// <summary>What one pick from the screen gave.</summary>
public abstract record ScreenColorSample
{
    private ScreenColorSample()
    {
    }

    /// <summary>The pixel under the pointer when the person clicked, as 8-bit sRGB from the composed screen.</summary>
    public sealed record Picked(byte Red, byte Green, byte Blue) : ScreenColorSample;

    /// <summary>The pick ended without a color (<see cref="ScreenColorSampler.Cancel"/>, or the displays changed).</summary>
    public sealed record Cancelled : ScreenColorSample;

    /// <summary>The screen could not be read; <paramref name="Operation"/> names the step that failed.</summary>
    public sealed record Failed(string Operation, Exception? Error) : ScreenColorSample;
}

/// <summary>Options of a <see cref="ScreenColorSampler"/>; tests change them.</summary>
public sealed record ScreenColorSamplerOptions
{
    /// <summary>
    /// False makes the loupe window click-through, so nothing ends the pick but <see cref="ScreenColorSampler.Cancel"/>
    /// and <see cref="ScreenColorSampler.PickAt"/> (tests on a desktop that someone may be using).
    /// </summary>
    public bool CatchesClicks { get; init; } = true;

    /// <summary>How long a click waits for the first captured frame before the pick fails.</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long reading the clicked pixel back from the GPU may take before the pick fails.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Pick from screen (WIN-UI-02), the Windows replacement for the Mac's system color sampler: a magnifier loupe follows
/// the pointer over every display, a click reads only that pixel of the composed screen, and the owner ends the pick
/// with <see cref="Cancel"/> on Escape. It needs no permission.
/// </summary>
/// <remarks>
/// The loupe is a borderless, topmost, never-activated window over the whole virtual screen, so it receives the clicks
/// and the keyboard stays with the window that had it. It is left out of every capture (WDA_EXCLUDEFROMCAPTURE), and
/// Windows.Graphics.Capture reads the monitor under the pointer without a border; Annotator's drawings, toolbar and
/// popovers are part of the composed screen, as they are for the Mac's sampler. Frames stay on the GPU: the loupe copies
/// 15 by 15 pixels around the pointer into a composition surface that it shows enlarged, and the click copies one pixel
/// to the CPU. Capture runs only while the loupe shows. Everything runs on the sampler's own thread; waits are bounded.
/// </remarks>
public sealed class ScreenColorSampler : IDisposable
{
    private readonly DispatcherThread _thread;
    private readonly Session _session;
    private int _disposed;

    private ScreenColorSampler(DispatcherThread thread, Session session)
    {
        _thread = thread;
        _session = session;
    }

    /// <summary>
    /// Shows the loupe and starts reading the screen on a new thread. <paramref name="completed"/> runs once on that
    /// thread with the result, unless the sampler is disposed first.
    /// </summary>
    public static ScreenColorSampler Start(WindowRegistry registry, Action<ScreenColorSample> completed, ScreenColorSamplerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(completed);
        DispatcherThread thread = DispatcherThread.Start("Annotator screen color");
        var session = new Session(thread.Queue, registry, completed, options ?? new ScreenColorSamplerOptions());
        if (!thread.Queue.TryEnqueue(session.Open))
        {
            _ = thread.ShutdownAsync();
            throw new InvalidOperationException("The screen color thread did not start.");
        }
        return new ScreenColorSampler(thread, session);
    }

    /// <summary>Ends the pick without a color; <c>completed</c> receives <see cref="ScreenColorSample.Cancelled"/>.</summary>
    public void Cancel() => _thread.Queue.TryEnqueue(() => _session.Finish(new ScreenColorSample.Cancelled()));

    /// <summary>For tests: moves the loupe to <paramref name="x"/>, <paramref name="y"/> (physical desktop pixels) as the pointer would.</summary>
    internal void MoveTo(int x, int y) => _thread.Queue.TryEnqueue(() => _session.MoveTo(x, y));

    /// <summary>For tests: picks the pixel at <paramref name="x"/>, <paramref name="y"/> as a click there would, without moving the loupe.</summary>
    internal void PickAt(int x, int y) => _thread.Queue.TryEnqueue(() => _session.PickAt(x, y));

    /// <summary>For tests: the loupe window, once it exists.</summary>
    internal Task<nint> WindowAsync() => _thread.RunAsync(() => (nint)_session.Handle);

    /// <summary>For tests: the loupe's diameter in pixels on the display under it, once it shows.</summary>
    internal Task<int> DiameterAsync() => _thread.RunAsync(() => _session.Diameter);

    /// <summary>Removes the loupe and stops capture without calling <c>completed</c>, then ends the thread. Never blocks.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _thread.Queue.TryEnqueue(_session.Close);
        _ = _thread.ShutdownAsync();
    }

    /// <summary>The loupe window and the capture behind it; used only on the sampler thread.</summary>
    private sealed unsafe class Session : NativeWindow
    {
        private const int Span = 15;
        private const int Half = Span / 2;
        private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int)0x887A000A);
        private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

        private readonly Windows.System.DispatcherQueue _queue;
        private readonly WindowRegistry _registry;
        private readonly Action<ScreenColorSample> _completed;
        private readonly ScreenColorSamplerOptions _options;
        private readonly Lock _gate = new();
        private readonly ManualResetEventSlim _firstFrame = new(false);
        private readonly byte[] _clear = new byte[Span * Span * 4];
        private IReadOnlyList<DisplayInfo> _displays = [];
        private PixelRect _screen;
        private GraphicsDevices? _devices;
        private IDirect3DDevice? _captureDevice;
        private Compositor? _compositor;
        private CompositionGraphicsDevice? _graphics;
        private DesktopWindowTarget? _target;
        private CompositionDrawingSurface? _surface;
        private ContainerVisual? _loupe;
        private SpriteVisual? _pixels;
        private ShapeVisual? _chrome;
        private WindowRegistration? _registration;
        private ID3D11Texture2D* _scratch;
        private ID3D11Texture2D* _staging;
        private DisplayInfo? _display;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _capture;
        private TypedEventHandler<Direct3D11CaptureFramePool, object>? _frameHandler;
        private nint _poolPointer;
        private nint _frameClosable;
        private nint _frameTexture;
        private int _frameWidth;
        private int _frameHeight;
        private int _refreshQueued;
        private int _cell;
        private int _pointerX;
        private int _pointerY;
        private bool _finished;
        private bool _closed;

        public Session(Windows.System.DispatcherQueue queue, WindowRegistry registry, Action<ScreenColorSample> completed, ScreenColorSamplerOptions options)
        {
            _queue = queue;
            _registry = registry;
            _completed = completed;
            _options = options;
            _refresh = Refresh;
        }

        private readonly Windows.System.DispatcherQueueHandler _refresh;

        /// <summary>Creates the loupe over the virtual screen and starts capturing the monitor under the pointer.</summary>
        public void Open()
        {
            if (_finished)
            {
                return;
            }
            string step = "Prepare the screen color loupe";
            try
            {
                // This thread only serves the loupe: physical pixels everywhere (WIN-REND-06).
                PInvoke.SetThreadDpiAwarenessContext(Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                step = "Check Windows.Graphics.Capture";
                if (!GraphicsCaptureSession.IsSupported())
                {
                    throw new NotSupportedException("Windows.Graphics.Capture is not available.");
                }
                // A packaged app captures only with the person's consent, which the app asked for before the pick started;
                // without it Windows would draw the yellow border (WIN-CAP-04). An unpackaged one needs none.
                step = "Check Windows' screen capture consent";
                if (CaptureAccess.Check().Failure is { } refused)
                {
                    throw new UnauthorizedAccessException($"Windows did not allow screen capture without a border ({refused.Operation}).");
                }
                step = "List the displays";
                _displays = DisplayEnumerator.GetDisplays();
                _screen = Union(_displays);
                step = "Create the graphics device";
                _devices = GraphicsDevices.Create();
                _captureDevice = CaptureInterop.CreateDirect3DDevice(_devices.DxgiDevice);
                CreateTextures();
                step = "Create the loupe window";
                CreateWindow();
                step = "Create the loupe";
                CreateLoupe();
                step = "Show the loupe";
                PInvoke.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_SHOWNA);
                const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                    SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
                if (!PInvoke.SetWindowPos(Handle, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags))
                {
                    throw NativeCallException.FromLastError("SetWindowPos(HWND_TOPMOST) (screen color loupe)");
                }
                step = "Follow the pointer";
                if (!PInvoke.GetCursorPos(out System.Drawing.Point cursor))
                {
                    throw NativeCallException.FromLastError("GetCursorPos");
                }
                Track(cursor.X, cursor.Y);
            }
            catch (Exception error) when (error is NativeCallException or COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                Finish(new ScreenColorSample.Failed(step, error));
            }
        }

        /// <summary>Hides the loupe, stops capture and reports <paramref name="sample"/> once.</summary>
        public void Finish(ScreenColorSample sample)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            if (!Handle.IsNull)
            {
                PInvoke.ShowWindow(Handle, SHOW_WINDOW_CMD.SW_HIDE);
            }
            StopCapture();
            if (_closed)
            {
                return;
            }
            try
            {
                _completed(sample);
            }
            catch (Exception error)
            {
                NativeCallbackErrors.Report(error);
            }
        }

        /// <summary>Releases everything without reporting a result (the owner disposed the sampler).</summary>
        public void Close()
        {
            _closed = true;
            Finish(new ScreenColorSample.Cancelled());
            _target?.Dispose();
            _target = null;
            _pixels?.Dispose();
            _chrome?.Dispose();
            _loupe?.Dispose();
            _surface?.Dispose();
            _graphics?.Dispose();
            _compositor?.Dispose();
            _pixels = null;
            _chrome = null;
            _loupe = null;
            _surface = null;
            _graphics = null;
            _compositor = null;
            _registration?.Dispose();
            _registration = null;
            if (!Handle.IsNull)
            {
                DestroyHandle("DestroyWindow (screen color loupe)");
            }
            ComPtr.Release(ref _scratch);
            ComPtr.Release(ref _staging);
            _captureDevice?.Dispose();
            _captureDevice = null;
            _devices?.Dispose();
            _devices = null;
            _firstFrame.Dispose();
        }

        public void MoveTo(int x, int y)
        {
            if (!_finished)
            {
                Track(x, y);
            }
        }

        public int Diameter => _cell * Span;

        public void PickAt(int x, int y)
        {
            if (!_finished)
            {
                Pick(x, y);
            }
        }

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
                case PInvoke.WM_MOUSEMOVE:
                    if (!_finished)
                    {
                        (int x, int y) = ClientPoint(lParam);
                        Track(x, y);
                    }
                    return default;
                case PInvoke.WM_LBUTTONDOWN:
                    if (!_finished)
                    {
                        (int x, int y) = ClientPoint(lParam);
                        Track(x, y);
                        Pick(x, y);
                    }
                    return default;
                case PInvoke.WM_SETCURSOR:
                    // The loupe's center square shows where the click reads, as the Mac's sampler does.
                    PInvoke.SetCursor(HCURSOR.Null);
                    return (LRESULT)1;
                case PInvoke.WM_MOUSEACTIVATE:
                    return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                case PInvoke.WM_ERASEBKGND:
                    return (LRESULT)1;
                case PInvoke.WM_DISPLAYCHANGE:
                case PInvoke.WM_DPICHANGED:
                    // The loupe covers the old screen; the next pick starts on the new one.
                    Finish(new ScreenColorSample.Cancelled());
                    return default;
                case PInvoke.WM_CLOSE:
                    return default;
                default:
                    return base.WndProc(message, wParam, lParam);
            }
        }

        protected override void OnDestroyed(HWND destroyed)
        {
            _registration?.Dispose();
            _registration = null;
        }

        private (int X, int Y) ClientPoint(LPARAM lParam) =>
            (_screen.Left + (short)(lParam.Value & 0xFFFF), _screen.Top + (short)((lParam.Value >> 16) & 0xFFFF));

        private static PixelRect Union(IReadOnlyList<DisplayInfo> displays)
        {
            if (displays.Count == 0)
            {
                throw new InvalidOperationException("No display is connected.");
            }
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            foreach (DisplayInfo display in displays)
            {
                left = Math.Min(left, display.Bounds.Left);
                top = Math.Min(top, display.Bounds.Top);
                right = Math.Max(right, display.Bounds.Right);
                bottom = Math.Max(bottom, display.Bounds.Bottom);
            }
            return new PixelRect(left, top, right, bottom);
        }

        private void CreateTextures()
        {
            var device = (ID3D11Device*)_devices!.D3D11Device;
            var description = new D3D11_TEXTURE2D_DESC
            {
                Width = Span,
                Height = Span,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            };
            ID3D11Texture2D* scratch;
            NativeCallException.ThrowIfFailed(device->CreateTexture2D(&description, null, &scratch), "ID3D11Device::CreateTexture2D(loupe pixels)");
            _scratch = scratch;
            description.Width = 1;
            description.Height = 1;
            description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            description.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            ID3D11Texture2D* staging;
            NativeCallException.ThrowIfFailed(device->CreateTexture2D(&description, null, &staging), "ID3D11Device::CreateTexture2D(picked pixel)");
            _staging = staging;
        }

        /// <summary>A topmost tool window over every display that never activates and is left out of every capture.</summary>
        private void CreateWindow()
        {
            WINDOW_EX_STYLE exStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOPMOST |
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            if (!_options.CatchesClicks)
            {
                exStyle |= WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
            }
            using (PerMonitorDpiScope.Enter())
            {
                CreateHandle("Annotator.Platform.ScreenColor", exStyle, WINDOW_STYLE.WS_POPUP, _screen.Left, _screen.Top, _screen.Width, _screen.Height,
                    HWND.Null, "CreateWindowEx (screen color loupe)");
            }
            if (!_options.CatchesClicks && !PInvoke.SetLayeredWindowAttributes(Handle, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw NativeCallException.FromLastError("SetLayeredWindowAttributes (screen color loupe)");
            }
            // Capture must read what is under the loupe, never the loupe itself.
            if (!PInvoke.SetWindowDisplayAffinity(Handle, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
            {
                throw NativeCallException.FromLastError("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)");
            }
            // Registered before the first show, like every Annotator window that can appear on screen.
            _registration = _registry.Register((nint)Handle, WindowLayer.Popover);
        }

        /// <summary>The enlarged pixels in a circle, a white ring with a dark edge and a square around the pixel a click reads.</summary>
        private void CreateLoupe()
        {
            _compositor = new Compositor();
            _graphics = CompositionInterop.CreateGraphicsDevice(_compositor, _devices!.D3D11Device);
            _surface = _graphics.CreateDrawingSurface(new Size(Span, Span), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            _target = CompositionInterop.CreateDesktopWindowTarget(_compositor, (nint)Handle, isTopmost: true);
            ContainerVisual root = _compositor.CreateContainerVisual();
            _target.Root = root;
            _loupe = _compositor.CreateContainerVisual();
            root.Children.InsertAtTop(_loupe);
            CompositionSurfaceBrush brush = _compositor.CreateSurfaceBrush(_surface);
            brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
            brush.Stretch = CompositionStretch.Fill;
            _pixels = _compositor.CreateSpriteVisual();
            _pixels.Brush = brush;
            _loupe.Children.InsertAtTop(_pixels);
            _chrome = _compositor.CreateShapeVisual();
            _loupe.Children.InsertAtTop(_chrome);
            Draw(null);
        }

        /// <summary>Sizes the loupe for the display's scale: 15 enlarged pixels of about 7.5 points each.</summary>
        private void Layout(DisplayInfo display)
        {
            int cell = Math.Max(4, (int)Math.Round(112 * display.Scale / Span));
            if (cell == _cell)
            {
                return;
            }
            _cell = cell;
            Compositor compositor = _compositor!;
            float size = cell * Span;
            _loupe!.Size = new Vector2(size);
            _pixels!.Size = new Vector2(size);
            CompositionEllipseGeometry circle = compositor.CreateEllipseGeometry();
            circle.Center = new Vector2(size / 2);
            circle.Radius = new Vector2(size / 2);
            _pixels.Clip = compositor.CreateGeometricClip(circle);
            _chrome!.Size = new Vector2(size);
            _chrome.Shapes.Clear();
            float ring = Math.Max(2, (float)Math.Round(2 * display.Scale));
            _chrome.Shapes.Add(Ring(compositor, size, size / 2 - ring / 2 - 1, ring, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
            _chrome.Shapes.Add(Ring(compositor, size, size / 2 - 0.5f, 1, Windows.UI.Color.FromArgb(140, 0, 0, 0)));
            float center = Half * cell;
            _chrome.Shapes.Add(Square(compositor, center - 0.5f, cell + 1, Windows.UI.Color.FromArgb(255, 0, 0, 0)));
            _chrome.Shapes.Add(Square(compositor, center + 0.5f, cell - 1, Windows.UI.Color.FromArgb(255, 255, 255, 255)));
        }

        private static CompositionSpriteShape Ring(Compositor compositor, float size, float radius, float thickness, Windows.UI.Color color)
        {
            CompositionEllipseGeometry geometry = compositor.CreateEllipseGeometry();
            geometry.Center = new Vector2(size / 2);
            geometry.Radius = new Vector2(radius);
            CompositionSpriteShape shape = compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = compositor.CreateColorBrush(color);
            shape.StrokeThickness = thickness;
            return shape;
        }

        private static CompositionSpriteShape Square(Compositor compositor, float offset, float size, Windows.UI.Color color)
        {
            CompositionRectangleGeometry geometry = compositor.CreateRectangleGeometry();
            geometry.Offset = new Vector2(offset);
            geometry.Size = new Vector2(size);
            CompositionSpriteShape shape = compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = compositor.CreateColorBrush(color);
            shape.StrokeThickness = 1;
            return shape;
        }

        /// <summary>Moves the loupe to the pointer, follows it to another display, and shows the pixels around it.</summary>
        private void Track(int x, int y)
        {
            _pointerX = x;
            _pointerY = y;
            DisplayInfo? display = null;
            foreach (DisplayInfo candidate in _displays)
            {
                if (candidate.Bounds.Contains(x, y))
                {
                    display = candidate;
                    break;
                }
            }
            if (display is null)
            {
                return;
            }
            if (!ReferenceEquals(display, _display))
            {
                Layout(display);
                StartCapture(display);
            }
            float size = _cell * Span;
            _loupe!.Offset = new Vector3(x - _screen.Left - size / 2, y - _screen.Top - size / 2, 0);
            Draw(null);
        }

        private void StartCapture(DisplayInfo display)
        {
            StopCapture();
            _display = display;
            try
            {
                _item = CaptureInterop.CreateItemForMonitor(display.Handle);
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_captureDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
                _frameHandler = OnFrameArrived;
                lock (_gate)
                {
                    _poolPointer = CaptureInterop.FramePoolPointer(_pool);
                }
                _pool.FrameArrived += _frameHandler;
                _capture = _pool.CreateCaptureSession(_item);
                _capture.IsCursorCaptureEnabled = false;
                // The yellow capture border must never appear (WIN-CAP-04).
                _capture.IsBorderRequired = false;
                if (_capture.IsBorderRequired)
                {
                    throw new InvalidOperationException("Windows requires a capture border.");
                }
                _capture.StartCapture();
            }
            catch (Exception error) when (error is NativeCallException or COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Finish(new ScreenColorSample.Failed("Start Windows.Graphics.Capture for the display under the pointer", error));
            }
        }

        private void StopCapture()
        {
            nint closable, texture;
            lock (_gate)
            {
                _poolPointer = 0;
                closable = _frameClosable;
                texture = _frameTexture;
                _frameClosable = 0;
                _frameTexture = 0;
            }
            FrameReferences.Release(closable, texture);
            if (_pool is not null && _frameHandler is not null)
            {
                _pool.FrameArrived -= _frameHandler;
            }
            _capture?.Dispose();
            _pool?.Dispose();
            _capture = null;
            _pool = null;
            _item = null;
            _frameHandler = null;
            _display = null;
            _firstFrame.Reset();
        }

        /// <summary>A capture thread: keeps the newest frame on the GPU and asks the sampler thread to show it.</summary>
        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                nint pool = Volatile.Read(ref _poolPointer);
                while (pool != 0 && CaptureInterop.TryGetNextFrame(pool, out nint closable, out nint texture, out int width, out int height, out _))
                {
                    nint oldClosable = closable, oldTexture = texture;
                    lock (_gate)
                    {
                        if (_poolPointer == pool)
                        {
                            (oldClosable, oldTexture) = (_frameClosable, _frameTexture);
                            (_frameClosable, _frameTexture, _frameWidth, _frameHeight) = (closable, texture, width, height);
                        }
                    }
                    FrameReferences.Release(oldClosable, oldTexture);
                    _firstFrame.Set();
                }
                if (Interlocked.Exchange(ref _refreshQueued, 1) == 0 && !_queue.TryEnqueue(_refresh))
                {
                    Volatile.Write(ref _refreshQueued, 0);
                }
            }
            catch (Exception error) when (error is ObjectDisposedException)
            {
                // The pick ended while a frame arrived.
            }
        }

        private void Refresh()
        {
            Volatile.Write(ref _refreshQueued, 0);
            if (!_finished)
            {
                Draw(null);
            }
        }

        /// <summary>
        /// Copies the 15 by 15 pixels around the pointer from the newest frame into the loupe, transparent where they fall
        /// outside the display, and, when <paramref name="pixel"/> is given, the pixel at <paramref name="pickX"/>,
        /// <paramref name="pickY"/> into it. Returns whether that pixel was copied.
        /// </summary>
        private bool Draw(ID3D11Texture2D* pixel, int pickX = 0, int pickY = 0)
        {
            if (_devices is null || _surface is null || _scratch is null)
            {
                return false;
            }
            var context = (ID3D11DeviceContext*)_devices.D3D11DeviceContext;
            bool copied = false;
            lock (_gate)
            {
                fixed (byte* zeros = _clear)
                {
                    context->UpdateSubresource((ID3D11Resource*)_scratch, 0, null, zeros, Span * 4, 0);
                }
                if (_frameTexture != 0 && _display is { } display)
                {
                    int x = _pointerX - display.Bounds.Left, y = _pointerY - display.Bounds.Top;
                    int left = Math.Max(0, x - Half), top = Math.Max(0, y - Half);
                    int right = Math.Min(_frameWidth, x + Half + 1), bottom = Math.Min(_frameHeight, y + Half + 1);
                    if (right > left && bottom > top)
                    {
                        var box = new D3D11_BOX { left = (uint)left, top = (uint)top, right = (uint)right, bottom = (uint)bottom, front = 0, back = 1 };
                        context->CopySubresourceRegion((ID3D11Resource*)_scratch, 0, (uint)(left - (x - Half)), (uint)(top - (y - Half)), 0,
                            (ID3D11Resource*)_frameTexture, 0, &box);
                    }
                    int pickLeft = pickX - display.Bounds.Left, pickTop = pickY - display.Bounds.Top;
                    if (pixel is not null && pickLeft >= 0 && pickTop >= 0 && pickLeft < _frameWidth && pickTop < _frameHeight)
                    {
                        var one = new D3D11_BOX { left = (uint)pickLeft, top = (uint)pickTop, right = (uint)pickLeft + 1, bottom = (uint)pickTop + 1, front = 0, back = 1 };
                        context->CopySubresourceRegion((ID3D11Resource*)pixel, 0, 0, 0, 0, (ID3D11Resource*)_frameTexture, 0, &one);
                        copied = true;
                    }
                }
            }
            try
            {
                using CompositionDrawingSession draw = CompositionInterop.BeginDraw(_surface, IID_ID3D11Texture2D);
                context->CopySubresourceRegion((ID3D11Resource*)draw.UpdateObject, 0, (uint)draw.Offset.X, (uint)draw.Offset.Y, 0,
                    (ID3D11Resource*)_scratch, 0, null);
                draw.EndDraw();
            }
            catch (NativeCallException error)
            {
                Finish(new ScreenColorSample.Failed("Show the screen in the loupe", error));
            }
            return copied;
        }

        /// <summary>Reads the clicked pixel of the newest frame back to the CPU, waiting a bounded time for the frame and the GPU.</summary>
        private void Pick(int x, int y)
        {
            if (!_firstFrame.Wait(_options.FirstFrameTimeout))
            {
                Finish(new ScreenColorSample.Failed("Wait for the first captured frame", null));
                return;
            }
            if (!Draw(_staging, x, y))
            {
                Finish(new ScreenColorSample.Failed("Copy the clicked pixel", null));
                return;
            }
            var context = (ID3D11DeviceContext*)_devices!.D3D11DeviceContext;
            D3D11_MAPPED_SUBRESOURCE mapped;
            var clock = Stopwatch.StartNew();
            int hr;
            while ((hr = context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, (uint)D3D11_MAP_FLAG.D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped).Value) ==
                DXGI_ERROR_WAS_STILL_DRAWING)
            {
                if (clock.Elapsed > _options.ReadTimeout)
                {
                    Finish(new ScreenColorSample.Failed("Read the clicked pixel", new TimeoutException("The graphics device did not finish the copy in time.")));
                    return;
                }
                Thread.Sleep(1);
            }
            if (hr < 0)
            {
                Finish(new ScreenColorSample.Failed("Read the clicked pixel", new NativeCallException("ID3D11DeviceContext::Map(picked pixel)", hr)));
                return;
            }
            byte* bgra = (byte*)mapped.pData;
            var sample = new ScreenColorSample.Picked(bgra[2], bgra[1], bgra[0]);
            context->Unmap((ID3D11Resource*)_staging, 0);
            Finish(sample);
        }
    }
}
