using System.Diagnostics;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Benchmarks;

/// <summary>
/// The Windows counterpart of the Mac suite's CPU bitmap: a Direct2D target bitmap on the device, cleared and drawn
/// between BeginDraw and EndDraw, then followed by a Direct3D event query so a sample also includes the GPU finishing the
/// work. Pixels are read back outside timing for the checksum. Every GPU wait is bounded.
/// </summary>
internal sealed unsafe class GpuCanvas : IDisposable
{
    private readonly int width;
    private readonly int height;
    private ID2D1DeviceContext1* context;
    private ID2D1Bitmap1* target;
    private ID2D1Bitmap1* readback;
    private GpuFence? fence;

    public GpuCanvas(GraphicsDevices devices, int width, int height)
    {
        this.width = width;
        this.height = height;
        try
        {
            var device = (ID2D1Device*)devices.D2DDevice;
            ID2D1DeviceContext* created;
            Native.Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &created), "ID2D1Device::CreateDeviceContext");
            try
            {
                context = Native.Query<ID2D1DeviceContext1>(created, ID2D1DeviceContext1.IID_Guid, "QueryInterface(ID2D1DeviceContext1)");
            }
            finally
            {
                Native.Release(ref created);
            }
            var size = new D2D_SIZE_U { width = (uint)width, height = (uint)height };
            var targetProperties = Properties(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET);
            ID2D1Bitmap1* bitmap;
            Native.Check(context->CreateBitmap(size, null, 0, &targetProperties, &bitmap), "ID2D1DeviceContext::CreateBitmap(target)");
            target = bitmap;
            var readProperties = Properties(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
            Native.Check(context->CreateBitmap(size, null, 0, &readProperties, &bitmap), "ID2D1DeviceContext::CreateBitmap(readback)");
            readback = bitmap;
            fence = new GpuFence(devices);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The CPU part of the last <see cref="Draw"/>: from its start until EndDraw returned and the work was flushed.</summary>
    public double LastSubmitMs { get; private set; }

    /// <summary>The whole last <see cref="Draw"/>, until the GPU signalled the event query.</summary>
    public double LastCompletionMs { get; private set; }

    /// <summary>Clears the target, runs <paramref name="draw"/> with the device context (as an ID2D1DeviceContext*), and waits for the GPU.</summary>
    public void Draw(Action<nint> draw)
    {
        var start = Stopwatch.GetTimestamp();
        context->SetTarget((ID2D1Image*)target);
        context->BeginDraw();
        var clear = default(D2D1_COLOR_F);
        context->Clear(&clear);
        try
        {
            draw((nint)context);
        }
        finally
        {
            var ended = context->EndDraw(null, null);
            context->SetTarget(null);
            Native.Check(ended, "ID2D1DeviceContext::EndDraw");
        }
        fence!.Signal();
        LastSubmitMs = Measurement.Milliseconds(start, Stopwatch.GetTimestamp());
        fence.Wait();
        LastCompletionMs = Measurement.Milliseconds(start, Stopwatch.GetTimestamp());
    }

    /// <summary>FNV-1a over the target's BGRA rows (premultiplied), read back from the GPU.</summary>
    public ulong Checksum() => Fixtures.Checksum(Pixels());

    /// <summary>The target's BGRA rows (premultiplied, without padding), read back from the GPU.</summary>
    public byte[] Pixels()
    {
        Native.Check(readback->CopyFromBitmap(null, (ID2D1Bitmap*)target, null), "ID2D1Bitmap::CopyFromBitmap");
        D2D1_MAPPED_RECT mapped;
        Native.Check(readback->Map(D2D1_MAP_OPTIONS.D2D1_MAP_OPTIONS_READ, &mapped), "ID2D1Bitmap1::Map");
        try
        {
            var stride = width * 4;
            var pixels = new byte[stride * height];
            for (var row = 0; row < height; row++)
            {
                new ReadOnlySpan<byte>(mapped.bits + (long)row * mapped.pitch, stride).CopyTo(pixels.AsSpan(row * stride, stride));
            }
            return pixels;
        }
        finally
        {
            readback->Unmap();
        }
    }

    /// <summary>Where two readbacks differ: the number of pixels, the largest channel difference and their bounds.</summary>
    public PixelDifference Difference(byte[] expected, byte[] actual)
    {
        int count = 0, largest = 0, left = width, top = height, right = -1, bottom = -1;
        for (var index = 0; index < expected.Length; index += 4)
        {
            var differs = false;
            for (var channel = 0; channel < 4; channel++)
            {
                var delta = Math.Abs(expected[index + channel] - actual[index + channel]);
                if (delta > 0)
                {
                    differs = true;
                    largest = Math.Max(largest, delta);
                }
            }
            if (differs)
            {
                count++;
                int x = index / 4 % width, y = index / 4 / width;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }
        return new PixelDifference(count, largest, left, top, right, bottom);
    }
    public void Dispose()
    {
        fence?.Dispose();
        fence = null;
        Native.Release(ref readback);
        Native.Release(ref target);
        Native.Release(ref context);
    }

    private static D2D1_BITMAP_PROPERTIES1 Properties(D2D1_BITMAP_OPTIONS options) => new()
    {
        pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
        dpiX = 96,
        dpiY = 96,
        bitmapOptions = options,
    };
}

/// <summary>How two readbacks differ (<see cref="GpuCanvas.Difference"/>).</summary>
internal readonly record struct PixelDifference(int Count, int Largest, int Left, int Top, int Right, int Bottom)
{
    public override string ToString() =>
        Count == 0 ? "no pixel differs" : $"{Count} pixels differ by up to {Largest}/255 within ({Left}, {Top}) to ({Right}, {Bottom})";
}
