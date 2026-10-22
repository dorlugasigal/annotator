using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Export;

/// <summary>
/// Draws into a GPU bitmap and reads the pixels back: the Windows counterpart of drawing into a
/// <c>CGBitmapContext</c> and calling <c>makeImage()</c>. Owns its own device context; use it on one thread.
/// </summary>
internal sealed unsafe class OffscreenCanvas : IDisposable
{
    private readonly GraphicsDevices devices;
    private ID2D1DeviceContext1* context;

    public OffscreenCanvas(GraphicsDevices devices)
    {
        this.devices = devices;
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
    }

    public ID2D1DeviceContext1* Context => context;

    /// <summary>
    /// Creates a cleared <paramref name="width"/> x <paramref name="height"/> target, runs <paramref name="draw"/>
    /// between BeginDraw and EndDraw, and returns the pixels. A lost device is reported to the bundle and thrown.
    /// </summary>
    public RasterImage Render(int width, int height, DrawCallback draw)
    {
        ObjectDisposedException.ThrowIf(context == null, this);
        ID2D1Bitmap1* target = null;
        ID2D1Bitmap1* readable = null;
        try
        {
            var properties = Properties(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET);
            var size = new D2D_SIZE_U { width = (uint)width, height = (uint)height };
            Check(context->CreateBitmap(size, null, 0, &properties, &target), "ID2D1DeviceContext::CreateBitmap(target)");
            context->SetTarget((ID2D1Image*)target);
            context->BeginDraw();
            var clear = default(D2D1_COLOR_F);
            context->Clear(&clear);
            try
            {
                draw(context);
            }
            finally
            {
                var ended = context->EndDraw(null, null);
                context->SetTarget(null);
                Check(ended.Value, "ID2D1DeviceContext::EndDraw");
            }
            var readback = Properties(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW);
            Check(context->CreateBitmap(size, null, 0, &readback, &readable), "ID2D1DeviceContext::CreateBitmap(readback)");
            Check(readable->CopyFromBitmap(null, (ID2D1Bitmap*)target, null), "ID2D1Bitmap::CopyFromBitmap");
            D2D1_MAPPED_RECT mapped;
            Check(readable->Map(D2D1_MAP_OPTIONS.D2D1_MAP_OPTIONS_READ, &mapped), "ID2D1Bitmap1::Map");
            try
            {
                var stride = width * 4;
                var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * height));
                for (var row = 0; row < height; row++)
                {
                    new ReadOnlySpan<byte>(mapped.bits + (long)row * mapped.pitch, stride).CopyTo(pixels.AsSpan(row * stride, stride));
                }
                return new RasterImage(width, height, stride, pixels);
            }
            finally
            {
                readable->Unmap();
            }
        }
        finally
        {
            Native.Release(ref readable);
            Native.Release(ref target);
        }
    }

    /// <summary>Uploads a CPU image as a drawable bitmap. The caller owns the result.</summary>
    public ID2D1Bitmap1* Upload(RasterImage image)
    {
        var properties = Properties(D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_NONE);
        var size = new D2D_SIZE_U { width = (uint)image.Width, height = (uint)image.Height };
        ID2D1Bitmap1* bitmap;
        fixed (byte* pixels = image.Buffer)
        {
            Check(context->CreateBitmap(size, pixels, (uint)image.Stride, &properties, &bitmap), "ID2D1DeviceContext::CreateBitmap(source)");
        }
        return bitmap;
    }

    public void Dispose() => Native.Release(ref context);

    /// <summary>The drawing area for a whole <paramref name="width"/> x <paramref name="height"/> target.</summary>
    public static DrawArea Area(int width, int height, double scale) => new(scale, 0, 0, new PixelRect(0, 0, width, height));

    private void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            if (DeviceLoss.IsDeviceLoss(hresult))
            {
                devices.ReportFailure(hresult);
            }
            throw new NativeCallException(operation, hresult);
        }
    }

    private void Check(Windows.Win32.Foundation.HRESULT result, string operation) => Check(result.Value, operation);

    private static D2D1_BITMAP_PROPERTIES1 Properties(D2D1_BITMAP_OPTIONS options) => new()
    {
        pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
        dpiX = 96,
        dpiY = 96,
        bitmapOptions = options,
    };

    public delegate void DrawCallback(ID2D1DeviceContext1* context);
}
