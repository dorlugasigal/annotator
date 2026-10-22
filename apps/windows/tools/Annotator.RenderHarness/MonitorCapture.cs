using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using WinRT;

namespace Annotator.RenderHarness;

/// <summary>
/// Measurement only: captures the whole monitor with Windows.Graphics.Capture, as a full-screen share sees it
/// (the S-1 method), at the display's rate, with no border. Reads pixels from composed frames. <see cref="ForWindow"/>
/// captures one window instead, as a meeting app's window share sees it.
/// </summary>
internal sealed unsafe partial class MonitorCapture : IDisposable
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    private readonly GraphicsDevices gpu = GraphicsDevices.Create();
    private readonly IDirect3DDevice device;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession session;
    private readonly Lock gate = new();
    private Action<nint, TimeSpan>? onFrame;
    private long frames;

    /// <param name="display">The monitor to capture.</param>
    /// <param name="start">False to set a frame handler first and then call <see cref="Start"/>, so the first frame of a screen that does not change is not missed.</param>
    public MonitorCapture(DisplayInfo display, bool start = true)
        : this(monitor: display.Handle, window: 0, start)
    {
    }

    private MonitorCapture(nint monitor, nint window, bool start)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");
        }
        device = Direct3DDevice(gpu.DxgiDevice);
        var item = window != 0 ? Item(window, slot: 3) : Item(monitor, slot: 4);
        Size = (item.Size.Width, item.Size.Height);
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        pool.FrameArrived += OnFrameArrived;
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.IsBorderRequired = false;
        session.MinUpdateInterval = TimeSpan.FromMilliseconds(1);
        if (start)
        {
            session.StartCapture();
        }
    }

    /// <summary>
    /// Captures the window <paramref name="window"/> alone, with its title bar and without the pointer (the Mac recorder's
    /// desktopIndependentWindow filter). Frames come only when the window's picture changes.
    /// </summary>
    public static MonitorCapture ForWindow(nint window, bool start = true) => new(monitor: 0, window, start);

    /// <summary>The captured item's size in pixels when the capture started.</summary>
    public (int Width, int Height) Size { get; }

    /// <summary>Starts a capture created with <c>start: false</c>.</summary>
    public void Start() => session.StartCapture();

    /// <summary>The devices that own the captured textures. Their immediate context is the capture thread's during a handler.</summary>
    public GraphicsDevices Devices => gpu;

    /// <summary>Frames delivered since the capture started. Windows.Graphics.Capture sends frames only when the screen changes.</summary>
    public long Frames => Interlocked.Read(ref frames);

    /// <summary>Called on a capture thread with each frame's ID3D11Texture2D (borrowed) and composition time.</summary>
    public void OnFrame(Action<nint, TimeSpan>? handler)
    {
        lock (gate)
        {
            onFrame = handler;
        }
    }

    /// <summary>
    /// Arms a check for <paramref name="color"/> at the pixel <paramref name="probe"/>, runs <paramref name="submit"/>
    /// (which changes the screen and returns its own start and end timestamps), then waits up to
    /// <paramref name="timeout"/> for a composed frame that shows the color. Returns that frame's composition time
    /// (QPC based, like <see cref="System.Diagnostics.Stopwatch"/>), or null when no such frame arrived in time.
    /// </summary>
    public (TimeSpan? Seen, long Start, long End) Expect(PixelRect probe, (byte Red, byte Green, byte Blue) color, TimeSpan timeout,
                                                         Func<(long Start, long End)> submit) =>
        Expect(probe, pixel => pixel == color, timeout, submit);

    /// <summary>
    /// Like <see cref="Expect(PixelRect, ValueTuple{byte, byte, byte}, TimeSpan, Func{ValueTuple{long, long}})"/>, for the
    /// first composed frame whose probe pixel <paramref name="matches"/>.
    /// </summary>
    public (TimeSpan? Seen, long Start, long End) Expect(PixelRect probe, Func<(byte Red, byte Green, byte Blue), bool> matches, TimeSpan timeout,
                                                         Func<(long Start, long End)> submit)
    {
        var seen = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Armed before the submit: the color is new, so no frame composed before the submit can show it.
        OnFrame((texture, time) =>
        {
            if (!seen.Task.IsCompleted)
            {
                var pixel = Read(texture, probe)[0, 0];
                if (matches((pixel.Red, pixel.Green, pixel.Blue)))
                {
                    seen.TrySetResult(time);
                }
            }
        });
        try
        {
            var (start, end) = submit();
            return (seen.Task.Wait(timeout) ? seen.Task.Result : null, start, end);
        }
        finally
        {
            OnFrame(null);
        }
    }

    /// <summary>Copies a rectangle of a captured texture into a CPU image (premultiplied BGRA).</summary>
    public RasterImage Read(nint texture, PixelRect area)
    {
        var context = (ID3D11DeviceContext*)gpu.D3D11DeviceContext;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)area.Width,
            Height = (uint)area.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* staging;
        Native.Check(((ID3D11Device*)gpu.D3D11Device)->CreateTexture2D(&desc, null, &staging), "ID3D11Device::CreateTexture2D(staging)");
        try
        {
            var box = new D3D11_BOX { left = (uint)area.Left, top = (uint)area.Top, front = 0, right = (uint)area.Right, bottom = (uint)area.Bottom, back = 1 };
            context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)texture, 0, &box);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Native.Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext::Map");
            try
            {
                var stride = area.Width * 4;
                var pixels = new byte[stride * area.Height];
                for (var row = 0; row < area.Height; row++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + (long)row * mapped.RowPitch, stride).CopyTo(pixels.AsSpan(row * stride));
                }
                return new RasterImage(area.Width, area.Height, stride, pixels);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            Native.Release(ref staging);
        }
    }

    /// <summary>The next composed frame's pixels in <paramref name="area"/>, waiting up to two seconds.</summary>
    public RasterImage Snapshot(PixelRect area)
    {
        RasterImage? image = null;
        using var done = new ManualResetEventSlim();
        var skipped = 0;
        OnFrame((texture, _) =>
        {
            // Skip a few frames so the newest overlay content has been composed.
            if (image is null && ++skipped > 4)
            {
                image = Read(texture, area);
                done.Set();
            }
        });
        if (!done.Wait(TimeSpan.FromSeconds(2)))
        {
            OnFrame(null);
            throw new NoFramesException("Windows.Graphics.Capture delivered no frame within two seconds.");
        }
        OnFrame(null);
        return image!;
    }

    public void Dispose()
    {
        OnFrame(null);
        session.Dispose();
        pool.Dispose();
        device.Dispose();
        gpu.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }
        Interlocked.Increment(ref frames);
        Action<nint, TimeSpan>? handler;
        lock (gate)
        {
            handler = onFrame;
        }
        if (handler is null)
        {
            return;
        }
        var surface = frame.Surface;
        var texture = Texture(surface);
        try
        {
            handler(texture, frame.SystemRelativeTime);
        }
        finally
        {
            Marshal.Release(texture);
            // Closing the wrapper now lets the pool reuse and free its buffer without waiting for the collector.
            surface.Dispose();
        }
    }

    /// <summary>
    /// A capture item from IGraphicsCaptureItemInterop: CreateForWindow is vtable slot 3 and CreateForMonitor slot 4.
    /// </summary>
    private static GraphicsCaptureItem Item(nint handle, int slot)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        nint factory;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, IID_IGraphicsCaptureItemInterop, out factory));
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
        try
        {
            Guid iid = IID_IGraphicsCaptureItem;
            nint item;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)factory)[slot];
            Marshal.ThrowExceptionForHR(create(factory, handle, &iid, &item));
            var result = GraphicsCaptureItem.FromAbi(item);
            Marshal.Release(item);
            return result;
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static IDirect3DDevice Direct3DDevice(nint dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
        var result = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        Marshal.Release(inspectable);
        return result;
    }

    private static nint Texture(IDirect3DSurface surface)
    {
        var unknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in IID_IDirect3DDxgiInterfaceAccess, out var access));
        try
        {
            Guid iid = ID3D11Texture2D.IID_Guid;
            nint texture;
            // IDirect3DDxgiInterfaceAccess::GetInterface is vtable slot 3.
            var get = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3];
            Marshal.ThrowExceptionForHR(get(access, &iid, &texture));
            return texture;
        }
        finally
        {
            Marshal.Release(access);
        }
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

/// <summary>
/// Composition frames stopped arriving: the display is probably off or the session locked. Runs report it and stop
/// instead of waiting.
/// </summary>
internal sealed class NoFramesException(string message) : Exception(message);
