using System.Numerics;
using Annotator.Platform.Displays;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Interop;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.Rendering.Presentation;

/// <summary>How a presenter layer reaches the screen.</summary>
public enum LayerKind
{
    /// <summary>
    /// A <c>CompositionVirtualDrawingSurface</c>: BeginDraw with an update rectangle, the compositor keeps the rest;
    /// memory is allocated only for tiles that were drawn, and updates commit with the other composition changes of
    /// the same overlay-thread work item.
    /// </summary>
    VirtualSurface,

    /// <summary>A <c>CompositionDrawingSurface</c> of the whole display, allocated at once.</summary>
    DrawingSurface,

    /// <summary>
    /// A composition swap chain (flip model, two buffers, a frame-latency waitable object with maximum latency 1,
    /// dirty rectangles). It presents on its own, outside composition commits; Windows 11's delegated ink trail needs one.
    /// </summary>
    SwapChain,
}

/// <summary>What a layer draws: called once per damaged rectangle, with the target clipped to it and cleared.</summary>
internal interface ILayerContent
{
    void Render(nint deviceContext, in DrawArea area);
}

/// <summary>One composition visual of the overlay and the surface behind it, sized in device pixels (no stretching).</summary>
internal abstract class PresentationLayer : IDisposable
{
    protected PresentationLayer(Compositor compositor, int width, int height)
    {
        Width = width;
        Height = height;
        Visual = compositor.CreateSpriteVisual();
        Visual.Size = new Vector2(width, height);
    }

    public SpriteVisual Visual { get; }

    public int Width { get; }

    public int Height { get; }

    public abstract LayerKind Kind { get; }

    /// <summary>Takes a frame if one can be drawn now without waiting for the display. Surfaces always can.</summary>
    public virtual bool TryTakeFrame() => true;

    /// <summary>
    /// Called when <see cref="TryTakeFrame"/> failed: asks to be told on the overlay thread when a frame frees. Returns
    /// true when one freed meanwhile and was taken, so the caller draws now.
    /// </summary>
    public virtual bool DeferFrame() => true;

    /// <summary>Draws the damaged rectangles. Returns 0 or the failing HRESULT (a lost device, for example).</summary>
    public abstract int Draw(DamageRegion damage, double scale, bool live, ILayerContent content);

    public virtual void Dispose() => Visual.Dispose();

    protected static CompositionSurfaceBrush Brush(Compositor compositor, ICompositionSurface surface)
    {
        var brush = compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;
        brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.NearestNeighbor;
        return brush;
    }

    protected static unsafe void Clear(ID2D1DeviceContext* context, in PixelRect clip)
    {
        var identity = D2D.Identity;
        context->SetTransform(&identity);
        var rect = D2D.Rect(clip.Left, clip.Top, clip.Right, clip.Bottom);
        context->PushAxisAlignedClip(&rect, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        var transparent = default(D2D1_COLOR_F);
        context->Clear(&transparent);
        context->PopAxisAlignedClip();
    }
}

/// <summary>A layer on a composition drawing surface: each damaged rectangle is one BeginDraw and EndDraw.</summary>
internal sealed unsafe class DrawingSurfaceLayer : PresentationLayer
{
    private readonly CompositionDrawingSurface surface;
    private readonly CompositionSurfaceBrush brush;
    private readonly SurfaceInterop interop;
    private readonly RectInt32[] whole;

    public DrawingSurfaceLayer(Compositor compositor, CompositionGraphicsDevice graphics, int width, int height, bool isVirtual)
        : base(compositor, width, height)
    {
        Kind = isVirtual ? LayerKind.VirtualSurface : LayerKind.DrawingSurface;
        whole = [new RectInt32 { X = 0, Y = 0, Width = width, Height = height }];
        try
        {
            surface = isVirtual
                ? graphics.CreateVirtualDrawingSurface(new SizeInt32 { Width = width, Height = height }, DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied)
                : graphics.CreateDrawingSurface(new Windows.Foundation.Size(width, height), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            interop = new SurfaceInterop(surface);
            brush = Brush(compositor, surface);
            Visual.Brush = brush;
        }
        catch
        {
            interop?.Dispose();
            surface?.Dispose();
            base.Dispose();
            throw;
        }
    }

    public override LayerKind Kind { get; }

    public override int Draw(DamageRegion damage, double scale, bool live, ILayerContent content)
    {
        foreach (var rect in damage.Rects)
        {
            var result = interop.BeginDraw(rect, out var context, out var offsetX, out var offsetY);
            if (result < 0)
            {
                return result;
            }
            try
            {
                var clip = new PixelRect(offsetX, offsetY, offsetX + rect.Width, offsetY + rect.Height);
                Clear((ID2D1DeviceContext*)context, clip);
                content.Render(context, new DrawArea(scale, offsetX - rect.Left, offsetY - rect.Top, clip, live));
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.Release(context);
                result = interop.EndDraw();
            }
            if (result < 0)
            {
                return result;
            }
        }
        return 0;
    }

    /// <summary>
    /// Clears a virtual surface and gives back the memory of every tile, in the next composition commit; it reads as
    /// transparent afterwards. WinRT's <c>Trim</c> clears the rectangles it is given (DirectComposition's keeps them).
    /// Returns false for other surfaces, which must draw the clear.
    /// </summary>
    public bool TrimAll()
    {
        if (surface is not CompositionVirtualDrawingSurface virtualSurface)
        {
            return false;
        }
        virtualSurface.Trim(whole);
        return true;
    }

    public override void Dispose()
    {
        interop.Dispose();
        brush.Dispose();
        surface.Dispose();
        base.Dispose();
    }
}
