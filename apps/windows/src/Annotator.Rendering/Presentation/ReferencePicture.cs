using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Presentation;

/// <summary>
/// Development only (live motion recordings, <c>tools/Annotator.RenderHarness motion-live</c>): a picture under an
/// overlay's canvas, such as the desktop of a Mac recording, scaled once into a composition drawing surface at
/// <c>target</c> (pixels from the overlay's top-left corner). Create and dispose it on the overlay thread.
/// </summary>
public sealed unsafe class ReferencePicture : IDisposable
{
    private readonly CompositionGraphicsDevice _graphics;
    private readonly CompositionDrawingSurface _surface;
    private readonly CompositionSurfaceBrush _brush;

    /// <exception cref="NativeCallException">Drawing failed, for example because the device was lost.</exception>
    public ReferencePicture(Compositor compositor, GraphicsDevices devices, RasterImage picture, PixelRect target)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(picture);
        if (target.Width <= 0 || target.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "The picture needs at least one pixel.");
        }
        _graphics = Annotator.Platform.Interop.CompositionInterop.CreateGraphicsDevice(compositor, devices.D2DDevice);
        _surface = _graphics.CreateDrawingSurface(new Windows.Foundation.Size(target.Width, target.Height),
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        _brush = compositor.CreateSurfaceBrush(_surface);
        _brush.Stretch = CompositionStretch.Fill;
        Visual = compositor.CreateSpriteVisual();
        Visual.Size = new Vector2(target.Width, target.Height);
        Visual.Offset = new Vector3(target.Left, target.Top, 0);
        Visual.Brush = _brush;
        try
        {
            Draw(picture, target);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The picture's visual; add it to the overlay's root below the canvas.</summary>
    public SpriteVisual Visual { get; }

    public void Dispose()
    {
        Visual.Brush = null;
        Visual.Dispose();
        _brush.Dispose();
        _surface.Dispose();
        _graphics.Dispose();
    }

    private void Draw(RasterImage picture, PixelRect target)
    {
        using var interop = new SurfaceInterop(_surface);
        Native.Check(interop.BeginDraw(new PixelRect(0, 0, target.Width, target.Height), out var updateObject, out var offsetX, out var offsetY),
                     "ICompositionDrawingSurfaceInterop::BeginDraw (reference picture)");
        var context = (ID2D1DeviceContext*)updateObject;
        ID2D1Bitmap* bitmap = null;
        int ended;
        try
        {
            var identity = D2D.Identity;
            context->SetTransform(&identity);
            var properties = new D2D1_BITMAP_PROPERTIES
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
            };
            fixed (byte* bytes = picture.Pixels)
            {
                Native.Check(context->CreateBitmap(new D2D_SIZE_U { width = (uint)picture.Width, height = (uint)picture.Height }, bytes,
                                                   (uint)picture.Stride, &properties, &bitmap), "ID2D1DeviceContext::CreateBitmap (reference picture)");
            }
            var destination = D2D.Rect(offsetX, offsetY, offsetX + target.Width, offsetY + target.Height);
            context->DrawBitmap(bitmap, &destination, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC, null, null);
        }
        finally
        {
            Native.Release(ref bitmap);
            Marshal.Release(updateObject);
            ended = interop.EndDraw();
        }
        Native.Check(ended, "ICompositionDrawingSurfaceInterop::EndDraw (reference picture)");
    }
}
