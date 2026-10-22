using Annotator.Platform.Capture;
using Annotator.Platform.Graphics;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Annotator.Platform.Sharing;

/// <summary>Receives a <see cref="WindowCapture"/>'s frames on a capture worker.</summary>
public interface IWindowFrameSink
{
    /// <summary>A frame of <paramref name="width"/> by <paramref name="height"/> pixels at the top-left of <paramref name="texture"/>
    /// (an ID3D11Texture2D that is valid only during the call).</summary>
    void OnFrame(nint texture, int width, int height);

    /// <summary>The window closed or capture ended.</summary>
    void OnStopped();
}

/// <summary>
/// Windows.Graphics.Capture of one window, without the pointer and without the yellow border (WIN-CAP-04): the shared
/// region's toolbar (Mac SharedToolbarCapture). Frames arrive on a capture worker; the frame pool follows the window's size.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private const int Buffers = 2;

    private readonly Lock _gate = new();
    private readonly IWindowFrameSink _sink;
    private IDirect3DDevice? _device;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private TypedEventHandler<Direct3D11CaptureFramePool, object>? _frameHandler;
    private TypedEventHandler<GraphicsCaptureItem, object>? _closedHandler;
    private nint _poolPointer;
    private SizeInt32 _poolSize;
    private int _disposed;

    private WindowCapture(IWindowFrameSink sink) => _sink = sink;

    /// <summary>Starts capturing <paramref name="window"/> on <paramref name="devices"/>' Direct3D device.</summary>
    /// <exception cref="NativeCallException">Windows refused to capture the window, or would show the yellow border.</exception>
    public static WindowCapture Start(nint window, GraphicsDevices devices, IWindowFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(sink);
        var capture = new WindowCapture(sink);
        try
        {
            capture.Begin(window, devices);
            return capture;
        }
        catch (Exception error) when (error is not NativeCallException)
        {
            capture.Dispose();
            throw new NativeCallException("Windows.Graphics.Capture (window)", error.HResult);
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private void Begin(nint window, GraphicsDevices devices)
    {
        // A packaged app captures only with the person's consent, which the app asked for before sharing started; without it
        // Windows would draw the yellow border (WIN-CAP-04).
        if (CaptureAccess.Check().Failure is { } refused)
        {
            throw new NativeCallException(refused.Operation, unchecked((int)0x80070005));
        }
        _device = CaptureInterop.CreateDirect3DDevice(devices.DxgiDevice);
        _item = CaptureInterop.CreateItemForWindow(window);
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, _poolSize);
        _poolPointer = CaptureInterop.FramePoolPointer(_pool);
        _frameHandler = OnFrameArrived;
        _pool.FrameArrived += _frameHandler;
        _closedHandler = OnClosed;
        _item.Closed += _closedHandler;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        _session.IsBorderRequired = false;
        if (_session.IsBorderRequired)
        {
            throw new NativeCallException("GraphicsCaptureSession.IsBorderRequired stayed true (window)", unchecked((int)0x80070005));
        }
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _poolPointer == 0
                || !CaptureInterop.TryGetNextFrame(_poolPointer, out nint closable, out nint texture, out int width, out int height, out _))
            {
                return;
            }
            bool resized = width != _poolSize.Width || height != _poolSize.Height;
            try
            {
                if (width > 0 && height > 0)
                {
                    _sink.OnFrame(texture, Math.Min(width, _poolSize.Width), Math.Min(height, _poolSize.Height));
                }
            }
            finally
            {
                FrameReferences.Release(closable, texture);
            }
            if (resized && width > 0 && height > 0)
            {
                // The window changed size: the next frames come in buffers of the new size.
                _poolSize = new SizeInt32(width, height);
                _pool!.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, Buffers, _poolSize);
            }
        }
    }

    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _sink.OnStopped();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        lock (_gate)
        {
            _poolPointer = 0;
        }
        if (_pool is not null && _frameHandler is not null)
        {
            _pool.FrameArrived -= _frameHandler;
        }
        if (_item is not null && _closedHandler is not null)
        {
            _item.Closed -= _closedHandler;
        }
        _session?.Dispose();
        _pool?.Dispose();
        (_device as IDisposable)?.Dispose();
    }
}
