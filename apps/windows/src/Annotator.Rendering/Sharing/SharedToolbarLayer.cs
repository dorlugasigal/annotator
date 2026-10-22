using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Sharing;
using Annotator.Rendering.Interop;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.Rendering.Sharing;

/// <summary>
/// The toolbar in the shared region (Mac SharedToolbarCapture with CaptureCompositor's screen overlay): Annotator leaves
/// its own windows out of screen capture, so the output captures the toolbar's window on its own and shows it above the
/// region's picture, where the toolbar is on the display, scaled and clipped like the region. It runs only while the
/// toolbar shows over the region with visible drawings. Use it on the thread of its compositor; frames are copied to a
/// composition swap chain by the capture worker.
/// </summary>
public sealed unsafe class SharedToolbarLayer : IWindowFrameSink, IDisposable
{
    private readonly Compositor _compositor;
    private readonly SpriteVisual _visual;
    private readonly SystemDispatcherQueue _queue;
    private readonly Lock _gate = new();
    private WindowCapture? _capture;
    private nint _window;
    private GraphicsDevices? _devices;
    private ICompositionSurface? _surface;
    private CompositionSurfaceBrush? _brush;
    private bool _disposed;

    // Capture worker, under _gate.
    private IDXGISwapChain2* _chain;
    private nint _backBuffer;
    private HANDLE _waitable;
    private int _width;
    private int _height;
    private long _generation;
    private bool _stopped;

    private SharedToolbarLayer(Compositor compositor, SystemDispatcherQueue queue)
    {
        _compositor = compositor;
        _queue = queue;
        _visual = compositor.CreateSpriteVisual();
        _visual.IsVisible = false;
        _visual.Clip = compositor.CreateInsetClip();
    }

    /// <summary>Creates the layer on its compositor's thread; its visual goes right above <paramref name="below"/>.</summary>
    public static SharedToolbarLayer Create(Compositor compositor, ContainerVisual parent, Visual below)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(below);
        var queue = SystemDispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Create the shared toolbar on a thread with a Windows.System dispatcher queue.");
        var layer = new SharedToolbarLayer(compositor, queue);
        parent.Children.InsertAbove(layer._visual, below);
        return layer;
    }

    /// <summary>Whether the toolbar's window is being captured.</summary>
    public bool IsRunning => _capture is not null;

    /// <summary>The capture ended or failed; the layer shows nothing until <see cref="Show"/> starts it again.</summary>
    public event Action? Stopped;

    /// <summary>The toolbar's first captured frame is on its visual (after <see cref="Show"/> started a capture).</summary>
    public event Action? Shown;

    /// <summary>Whether the layer shows a captured frame of the toolbar now.</summary>
    public bool IsShown => _visual.IsVisible;

    /// <summary>
    /// Shows <paramref name="window"/> (the toolbar) at <paramref name="frame"/>, its rectangle on the desktop, in the output:
    /// <paramref name="privacy"/> is the shared region on the desktop and <paramref name="fit"/> where the output shows it.
    /// </summary>
    public void Show(nint window, GraphicsDevices devices, PixelRect frame, PixelRect privacy, PixelRect fit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is null || window != _window || !ReferenceEquals(devices, _devices))
        {
            Hide();
            _window = window;
            _devices = devices;
            lock (_gate)
            {
                _stopped = false;
            }
            _capture = WindowCapture.Start(window, devices, this);
        }
        Place(frame, privacy, fit);
    }

    /// <summary>Stops capturing the toolbar and hides it.</summary>
    public void Hide()
    {
        _capture?.Dispose();
        _capture = null;
        _visual.IsVisible = false;
        _visual.Brush = null;
        _brush?.Dispose();
        (_surface as IDisposable)?.Dispose();
        _brush = null;
        _surface = null;
        lock (_gate)
        {
            ReleaseChain();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Hide();
        _visual.Parent?.Children.Remove(_visual);
        _visual.Dispose();
        _disposed = true;
    }

    /// <summary>Places the toolbar's picture: the region's scale, cut to the region's frame in the output.</summary>
    private void Place(PixelRect frame, PixelRect privacy, PixelRect fit)
    {
        if (privacy.IsEmpty || fit.IsEmpty)
        {
            _visual.IsVisible = false;
            return;
        }
        float scale = fit.Width / (float)privacy.Width;
        var offset = new Vector2(fit.Left + (frame.Left - privacy.Left) * scale, fit.Top + (frame.Top - privacy.Top) * scale);
        var size = new Vector2(frame.Width * scale, frame.Height * scale);
        _visual.Offset = new Vector3(offset, 0);
        _visual.Size = size;
        var clip = (InsetClip)_visual.Clip;
        clip.LeftInset = Math.Max(0, fit.Left - offset.X);
        clip.TopInset = Math.Max(0, fit.Top - offset.Y);
        clip.RightInset = Math.Max(0, offset.X + size.X - fit.Right);
        clip.BottomInset = Math.Max(0, offset.Y + size.Y - fit.Bottom);
    }

    void IWindowFrameSink.OnFrame(nint texture, int width, int height)
    {
        lock (_gate)
        {
            if (_stopped || _devices is not { } devices)
            {
                return;
            }
            try
            {
                if (_chain == null || width != _width || height != _height)
                {
                    CreateChain(devices, width, height);
                }
                if (PInvoke.WaitForSingleObject(_waitable, 0) != (WAIT_EVENT)0)
                {
                    return;
                }
                var context = (ID3D11DeviceContext*)devices.D3D11DeviceContext;
                var box = new D3D11_BOX { left = 0, top = 0, right = (uint)width, bottom = (uint)height, front = 0, back = 1 };
                context->CopySubresourceRegion((ID3D11Resource*)_backBuffer, 0, 0, 0, 0, (ID3D11Resource*)texture, 0, &box);
                Native.Check(_chain->Present(0, 0), "IDXGISwapChain::Present(shared toolbar)");
            }
            catch (NativeCallException)
            {
                _stopped = true;
                ReleaseChain();
                _queue.TryEnqueue(OnStopped);
            }
        }
    }

    void IWindowFrameSink.OnStopped()
    {
        lock (_gate)
        {
            _stopped = true;
        }
        _queue.TryEnqueue(OnStopped);
    }

    private void OnStopped()
    {
        if (!_disposed && _capture is not null)
        {
            Hide();
            Stopped?.Invoke();
        }
    }

    private void CreateChain(GraphicsDevices devices, int width, int height)
    {
        ReleaseChain();
        _chain = (IDXGISwapChain2*)SwapChains.CreateForComposition(devices, width, height, "shared toolbar", premultiplied: true);
        Native.Check(_chain->SetMaximumFrameLatency(1), "IDXGISwapChain2::SetMaximumFrameLatency(shared toolbar)");
        _waitable = _chain->GetFrameLatencyWaitableObject();
        Guid textureIid = ID3D11Texture2D.IID_Guid;
        void* buffer;
        Native.Check(_chain->GetBuffer(0, &textureIid, &buffer), "IDXGISwapChain::GetBuffer(shared toolbar)");
        _backBuffer = (nint)buffer;
        _width = width;
        _height = height;
        long generation = ++_generation;
        var chain = (nint)_chain;
        Marshal.AddRef(chain);
        if (!_queue.TryEnqueue(() => Attach(chain, generation)))
        {
            Marshal.Release(chain);
        }
    }

    private void Attach(nint chain, long generation)
    {
        try
        {
            if (_disposed || _capture is null || generation != Interlocked.Read(ref _generation))
            {
                return;
            }
            ICompositionSurface surface = Platform.Interop.CompositionInterop.CreateCompositionSurfaceForSwapChain(_compositor, chain);
            CompositionSurfaceBrush brush = _compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Fill;
            _visual.Brush = null;
            _brush?.Dispose();
            (_surface as IDisposable)?.Dispose();
            _surface = surface;
            _brush = brush;
            _visual.Brush = brush;
            _visual.IsVisible = true;
            Shown?.Invoke();
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or COMException)
        {
            Hide();
            Stopped?.Invoke();
        }
        finally
        {
            Marshal.Release(chain);
        }
    }

    private void ReleaseChain()
    {
        if (_backBuffer != 0)
        {
            Marshal.Release(_backBuffer);
            _backBuffer = 0;
        }
        if (_waitable != HANDLE.Null)
        {
            PInvoke.CloseHandle(_waitable);
            _waitable = HANDLE.Null;
        }
        Native.Release(ref _chain);
        _width = _height = 0;
        Interlocked.Increment(ref _generation);
    }
}
