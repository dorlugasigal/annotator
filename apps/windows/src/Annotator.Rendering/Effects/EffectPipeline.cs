using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Effects;

/// <summary>
/// Draws an <see cref="EffectList"/> from a captured frame with Direct3D 11, in the order of the Mac's
/// <c>CaptureCompositor</c>: image layers are composited into the scene, the scene outside a privacy crop turns black
/// and covered pixels turn black, all before any filter samples it; each visible region is filtered from that scene,
/// which is clamped at the display's edges, and cropped to its rectangle, later regions on top; covers are drawn black
/// over every region last. <see cref="Render"/> draws the effect layer alone (<c>regionsOnly</c>);
/// <see cref="RenderScene"/> draws the whole frame.
/// </summary>
/// <remarks>
/// <para>The filters match Core Image in the Mac conformance corpus (S-13). Blur is a Gaussian with a standard
/// deviation of <c>amount x scale</c> pixels. Large blurs average 2 x 2 blocks into smaller levels until the remaining
/// deviation is at most six texels, blur there separably and scale back up with bilinear filtering; the deviation added
/// by the reductions and the final bilinear step is taken off the remaining blur. The levels cover the region's whole
/// reach, beyond the display edge too, where they repeat the edge pixels at full resolution like Core Image's clamped
/// extent. Pixelate and Distort read the scene once per pixel: they interpolate the capture's encoded values when they
/// read it directly and linear values when they read a composited scene, as Core Image does.</para>
/// <para>Filtering happens in linear light like Core Image's working space: 8-bit sRGB values are decoded when read,
/// intermediate levels are 16-bit floats, and the final pass encodes sRGB.</para>
/// <para>Use an instance on one thread at a time. Several pipelines may share a device bundle: each render holds the
/// device's context lock, so their draws never interleave on the immediate context. Scratch textures grow to the
/// largest area asked for and are released with the pipeline; image layer textures are kept only while a list uses
/// them.</para>
/// </remarks>
internal sealed unsafe class EffectPipeline : IDisposable
{
    internal const int MaxTaps = 24;
    internal const int MaxLevels = 8;
    internal const double LevelDeviation = 6;

    /// <summary>Taps on each side of a Gaussian, in standard deviations.</summary>
    internal const double TapReach = 3.5;

    private const int ViewCacheLimit = 8;
    private const int ScratchGranularity = 64;

    private readonly GraphicsDevices devices;
    private readonly ID3D11Device* device;
    // Sized for the limit, so a new frame or composition target texture never grows them while drawing (no allocation).
    private readonly Dictionary<nint, nint> sourceViews = new(ViewCacheLimit);
    private readonly Dictionary<nint, nint> targetViews = new(ViewCacheLimit);
    private readonly Dictionary<ValueList<byte>, LayerTexture> layerTextures = [];
    private readonly Scratch scene = new(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
    private readonly Scratch spare = new(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM);
    private readonly Scratch[] levels = new Scratch[MaxLevels + 1];
    private readonly Scratch horizontal = new(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT);
    private readonly Scratch vertical = new(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT);

    /// <summary>The frame as a view draws it before its projection: exactly the display's size, so it can take a copy of the frame.</summary>
    private readonly Scratch viewScene = new(DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, exact: true);
    private ID3D11DeviceContext1* context;
    private ID3D11Multithread* multithread;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* downShader;
    private ID3D11PixelShader* blurShader;
    private ID3D11PixelShader* upShader;
    private ID3D11PixelShader* pixelateShader;
    private ID3D11PixelShader* twirlShader;
    private ID3D11PixelShader* imageShader;
    private ID3D11PixelShader* zoomShader;
    private ID3D11PixelShader* projectShader;
    private ID3D11SamplerState* sampler;
    private ID3D11Buffer* constants;
    private WicImaging? wic;
    private RECT[] rects = new RECT[16];
    private bool disposed;

    public EffectPipeline(GraphicsDevices devices)
    {
        this.devices = devices;
        device = (ID3D11Device*)devices.D3D11Device;
        for (var level = 1; level <= MaxLevels; level++)
        {
            levels[level] = new Scratch(DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT);
        }
        try
        {
            context = Native.Query<ID3D11DeviceContext1>((void*)devices.D3D11DeviceContext, ID3D11DeviceContext1.IID_Guid,
                                                        "QueryInterface(ID3D11DeviceContext1)");
            multithread = Native.Query<ID3D11Multithread>(device, ID3D11Multithread.IID_Guid, "QueryInterface(ID3D11Multithread)(effects)");
            vertexShader = CreateVertexShader("vs_main");
            downShader = CreatePixelShader("ps_down");
            blurShader = CreatePixelShader("ps_blur");
            upShader = CreatePixelShader("ps_up");
            pixelateShader = CreatePixelShader("ps_pixelate");
            twirlShader = CreatePixelShader("ps_twirl");
            imageShader = CreatePixelShader("ps_image");
            zoomShader = CreatePixelShader("ps_zoom");
            projectShader = CreatePixelShader("ps_project");
            var samplerDescription = new D3D11_SAMPLER_DESC
            {
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
                MaxLOD = float.MaxValue,
            };
            ID3D11SamplerState* createdSampler;
            Native.Check(device->CreateSamplerState(&samplerDescription, &createdSampler), "ID3D11Device::CreateSamplerState(effects)");
            sampler = createdSampler;
            var bufferDescription = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)sizeof(PassConstants),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            ID3D11Buffer* buffer;
            Native.Check(device->CreateBuffer(&bufferDescription, null, &buffer), "ID3D11Device::CreateBuffer(effect constants)");
            constants = buffer;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The bundle whose device the pipeline uses.</summary>
    public GraphicsDevices Devices => devices;

    /// <summary>Draw calls made so far, for measurements.</summary>
    public long Passes { get; private set; }

    /// <summary>
    /// Holds the device's context lock (ID3D11Multithread) until the result is disposed, so draws and presents of other
    /// threads on the same device wait. The lock is re-entrant; <see cref="Render"/> takes it too.
    /// </summary>
    public ContextLock LockContext()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        multithread->Enter();
        return new ContextLock(multithread);
    }

    /// <summary>
    /// The effect layer (<c>regionsOnly</c>): clears <paramref name="clear"/> to transparent in <paramref name="target"/>,
    /// then draws the visible regions of <paramref name="list"/> from <paramref name="source"/>, and covers inside them.
    /// Both are ID3D11Texture2D pointers of the list's display size in B8G8R8A8_UNORM; the source needs shader-resource
    /// binding and the target render-target binding. Pixels outside the regions are not touched.
    /// </summary>
    /// <exception cref="Annotator.Platform.NativeCallException">A Direct3D call failed, for example because the device was lost.</exception>
    /// <exception cref="RenderFailure">An image layer could not be decoded.</exception>
    public void Render(nint source, nint target, EffectList list, ReadOnlySpan<PixelRect> clear)
    {
        using var held = LockContext();
        var targetView = TargetView(target);
        if (!clear.IsEmpty)
        {
            ClearRects(targetView, clear, transparent: true);
        }
        if (!list.HasVisibleRegions)
        {
            ReleaseUnusedLayers(list);
            return;
        }
        var capture = new SourceRef(SourceView(source), list.Width, list.Height, 0, 0, list.Width, list.Height, composited: false);
        Begin();
        try
        {
            foreach (var region in list.Regions)
            {
                if (!region.IsVisible)
                {
                    continue;
                }
                var footprint = Footprint(region, list);
                var input = Composites(footprint, list) ? Scene(source, footprint, list) : capture;
                Filter(region, input, list, targetView);
            }
            DrawCovers(targetView, list, inRegionsOnly: true);
        }
        finally
        {
            End();
            ReleaseUnusedLayers(list);
        }
    }

    /// <summary>
    /// The whole frame, like the Mac's <c>CaptureCompositor.image</c> without <c>regionsOnly</c>: the scene (the
    /// source with image layers, the drawing layer, the privacy crop and covers) with every visible region filtered over
    /// it and covers on top. <paramref name="target"/> is a B8G8R8A8_UNORM texture of the list's display size with
    /// render-target binding; every pixel of it is written.
    /// </summary>
    /// <param name="drawing">The Mac's <c>drawingLayer</c>: drawings composited over the image layers before the privacy
    /// crop, the covers and any filter, as the shared region shows them; null for none.</param>
    /// <exception cref="Annotator.Platform.NativeCallException">A Direct3D call failed, for example because the device was lost.</exception>
    /// <exception cref="RenderFailure">An image layer could not be decoded.</exception>
    public void RenderScene(nint source, nint target, EffectList list, SceneLayer? drawing = null)
    {
        using var held = LockContext();
        var targetView = TargetView(target);
        var whole = new PixelRect(0, 0, list.Width, list.Height);
        Begin();
        try
        {
            SourceRef? composite = null;
            if (Composites(whole, list, drawing))
            {
                composite = Scene(source, whole, list, drawing);
                var box = new D3D11_BOX { left = 0, top = 0, right = (uint)list.Width, bottom = (uint)list.Height, front = 0, back = 1 };
                context->CopySubresourceRegion((ID3D11Resource*)target, 0, 0, 0, 0, (ID3D11Resource*)composite.Value.Texture, 0, &box);
            }
            else
            {
                context->CopyResource((ID3D11Resource*)target, (ID3D11Resource*)source);
            }
            var capture = new SourceRef(SourceView(source), list.Width, list.Height, 0, 0, list.Width, list.Height, composited: false);
            foreach (var region in list.Regions)
            {
                if (!region.IsVisible)
                {
                    continue;
                }
                var footprint = Footprint(region, list);
                Filter(region, composite is { } scene && Composites(footprint, list, drawing) ? scene : capture, list, targetView);
            }
            DrawCovers(targetView, list, inRegionsOnly: false);
        }
        finally
        {
            End();
            ReleaseUnusedLayers(list);
        }
    }

    /// <summary>
    /// Screen zoom, the pointer lens and the effect layer under zoom (Mac PixelEffectsView.present with a viewport): the frame
    /// as <see cref="RenderScene"/> draws it, or the effect layer alone as <see cref="Render"/> draws it with
    /// <paramref name="regionsOnly"/>, then the part inside <paramref name="crop"/> (frame pixels, which may be fractional) drawn
    /// into <paramref name="area"/> of <paramref name="target"/> with nearest sampling, as ViewportProjection.imageForMetal
    /// does with Core Image's samplingNearest(). A view shows the desktop opaque; the effect layer keeps its transparency.
    /// Without regions, covers, image layers or a privacy crop, the frame itself is projected.
    /// </summary>
    /// <exception cref="Annotator.Platform.NativeCallException">A Direct3D call failed, for example because the device was lost.</exception>
    /// <exception cref="RenderFailure">An image layer could not be decoded.</exception>
    public void RenderView(nint source, nint target, EffectList list, PixelRect area, Rect crop, bool regionsOnly)
    {
        if (area.IsEmpty || !(crop.Width > 0) || !(crop.Height > 0) || !crop.IsValid)
        {
            return;
        }
        using var held = LockContext();
        ID3D11ShaderResourceView* input;
        if (regionsOnly || list.Regions.Length > 0 || list.Covers.Length > 0 || list.ImageLayers.Length > 0 || list.Privacy is not null)
        {
            viewScene.Ensure(device, list.Width, list.Height);
            if (regionsOnly)
            {
                Render(source, (nint)viewScene.Texture, list, [new PixelRect(0, 0, list.Width, list.Height)]);
            }
            else
            {
                RenderScene(source, (nint)viewScene.Texture, list);
            }
            input = viewScene.View;
        }
        else
        {
            input = SourceView(source);
        }
        var targetView = TargetView(target);
        double scaleX = crop.Width / area.Width, scaleY = crop.Height / area.Height;
        var values = new PassConstants
        {
            SourceScaleX = (float)scaleX,
            SourceScaleY = (float)scaleY,
            SourceOffsetX = (float)(crop.X - area.Left * scaleX),
            SourceOffsetY = (float)(crop.Y - area.Top * scaleY),
            SourceWidth = list.Width,
            SourceHeight = list.Height,
            ClampMinX = 0,
            ClampMinY = 0,
            ClampMaxX = list.Width - 1,
            ClampMaxY = list.Height - 1,
        };
        Begin();
        try
        {
            Pass(regionsOnly ? projectShader : zoomShader, input, targetView, Viewport(area.Left, area.Top, area.Width, area.Height), values);
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// The level count and the deviation left for the separable pass at that level, in its texels, for a Gaussian of
    /// <paramref name="deviation"/> pixels. Each 2 x 2 average adds a variance of 4^j / 4 pixels squared at level j, and
    /// bilinear upscaling from level k adds 4^k / 6; both are taken off the requested variance.
    /// </summary>
    internal static (int Levels, double Deviation) BlurPlan(double deviation)
    {
        var levelsNeeded = 0;
        while (levelsNeeded < MaxLevels && deviation / (1 << levelsNeeded) > LevelDeviation)
        {
            levelsNeeded++;
        }
        if (levelsNeeded == 0)
        {
            return (0, deviation);
        }
        var scale = (double)(1 << levelsNeeded);
        var added = (scale * scale - 1) / 12 + scale * scale / 6;
        var remaining = Math.Sqrt(Math.Max(deviation * deviation - added, scale * scale / 16));
        return (levelsNeeded, remaining / scale);
    }

    /// <summary>Normalized weights of a Gaussian for offsets 0 to the returned tap count (at most <see cref="MaxTaps"/>).</summary>
    internal static int BlurWeights(double deviation, Span<float> weights)
    {
        var taps = Math.Clamp((int)Math.Ceiling(TapReach * deviation), 1, MaxTaps);
        double total = 0;
        for (var index = 0; index <= taps; index++)
        {
            var weight = deviation > 0 ? Math.Exp(-(index * index) / (2 * deviation * deviation)) : index == 0 ? 1 : 0;
            weights[index] = (float)weight;
            total += index == 0 ? weight : 2 * weight;
        }
        for (var index = 0; index <= taps; index++)
        {
            weights[index] = (float)(weights[index] / total);
        }
        return taps;
    }

    /// <summary>
    /// The display pixels a region's filter reads, clipped to the display: everything outside them has no weight in
    /// its result (beyond the display edge a blur repeats the edge pixels).
    /// </summary>
    internal static PixelRect Footprint(EffectRegion region, EffectList list) => Clip(Reach(region, list), list);

    /// <summary>The pixels a region's filter reads before clipping: for a blur, beyond the display edge too.</summary>
    internal static PixelRect Reach(EffectRegion region, EffectList list)
    {
        var rect = region.Rect;
        int margin;
        switch (region.Kind)
        {
            case EffectKind.Blur:
                var deviation = region.Amount * list.Scale;
                margin = (int)Math.Ceiling(TapReach * deviation) + (1 << BlurPlan(deviation).Levels) + 1;
                break;
            case EffectKind.Pixelate:
                margin = (int)Math.Ceiling(region.Amount * list.Scale) + 1;
                break;
            default:
                // The twirl turns sample points around the center within max(width, height).
                var radius = Math.Max(rect.Width, rect.Height) + 2;
                var centerX = (rect.Left + rect.Right) / 2;
                var centerY = (rect.Top + rect.Bottom) / 2;
                return new PixelRect(centerX - radius, centerY - radius, centerX + radius + 1, centerY + radius + 1);
        }
        return new PixelRect(rect.Left - margin, rect.Top - margin, rect.Right + margin, rect.Bottom + margin);
    }

    /// <summary>
    /// Whether the scene differs from the capture inside <paramref name="area"/>: a cover, an image layer, the drawing
    /// layer or the outside of the privacy crop reaches into it.
    /// </summary>
    internal static bool Composites(PixelRect area, EffectList list, SceneLayer? drawing = null)
    {
        if (drawing is { } drawn && !Intersection(area, drawn.Bounds).IsEmpty)
        {
            return true;
        }
        foreach (var cover in list.Covers)
        {
            if (!Intersection(area, cover).IsEmpty)
            {
                return true;
            }
        }
        foreach (var layer in list.ImageLayers)
        {
            if (!Intersection(area, layer.Bounds).IsEmpty)
            {
                return true;
            }
        }
        return list.Privacy is { } privacy && !privacy.Contains(area);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        if (multithread != null)
        {
            multithread->Enter();
        }
        try
        {
            if (context != null)
            {
                // Bound shaders and buffers stay alive until they are unbound.
                End();
                context->VSSetShader(null, null, 0);
                context->PSSetShader(null, null, 0);
                ID3D11SamplerState* noSampler = null;
                context->PSSetSamplers(0, 1, &noSampler);
                ID3D11Buffer* noBuffer = null;
                context->PSSetConstantBuffers(0, 1, &noBuffer);
            }
            ReleaseResources();
            // Direct3D destroys released resources at the context's next flush. Capture can keep the device alive
            // without one, so flush now and free the scratch textures (and a swap chain released before) at once.
            if (context != null)
            {
                context->Flush();
            }
        }
        finally
        {
            if (multithread != null)
            {
                multithread->Leave();
            }
        }
        Native.Release(ref multithread);
        Native.Release(ref context);
        wic?.Dispose();
        wic = null;
    }

    private void ReleaseResources()
    {
        ReleaseViews(sourceViews);
        ReleaseViews(targetViews);
        foreach (var texture in layerTextures.Values)
        {
            texture.Release();
        }
        layerTextures.Clear();
        scene.Release();
        spare.Release();
        viewScene.Release();
        foreach (var level in levels)
        {
            level?.Release();
        }
        horizontal.Release();
        vertical.Release();
        Native.Release(ref constants);
        Native.Release(ref sampler);
        Native.Release(ref imageShader);
        Native.Release(ref zoomShader);
        Native.Release(ref projectShader);
        Native.Release(ref twirlShader);
        Native.Release(ref pixelateShader);
        Native.Release(ref upShader);
        Native.Release(ref blurShader);
        Native.Release(ref downShader);
        Native.Release(ref vertexShader);
    }

    /// <summary>A held device context lock; dispose it once.</summary>
    internal readonly ref struct ContextLock
    {
        private readonly ID3D11Multithread* held;

        public ContextLock(ID3D11Multithread* multithread) => held = multithread;

        public void Dispose() => held->Leave();
    }

    private static PixelRect Clip(PixelRect rect, EffectList list) => new(
        Math.Max(0, rect.Left), Math.Max(0, rect.Top), Math.Min(list.Width, rect.Right), Math.Min(list.Height, rect.Bottom));

    private static PixelRect Intersection(PixelRect a, PixelRect b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));

    private void Filter(EffectRegion region, in SourceRef input, EffectList list, ID3D11RenderTargetView* target)
    {
        switch (region.Kind)
        {
            case EffectKind.Blur:
                Blur(input, region.Rect, Reach(region, list), region.Amount * list.Scale, target);
                break;
            case EffectKind.Pixelate:
                Point(pixelateShader, input, region.Rect, (float)(region.Amount * list.Scale), 0, target);
                break;
            default:
                Point(twirlShader, input, region.Rect, Math.Max(region.Rect.Width, region.Rect.Height),
                      (float)(region.Amount / 100 * 2 * Math.PI), target);
                break;
        }
    }

    /// <summary>
    /// The scene inside <paramref name="area"/>, in the Mac's order: the capture, image layers and then the drawing layer
    /// composited in linear light, black outside the privacy crop, black under covers. No filter can read what a cover or
    /// the crop hides.
    /// </summary>
    private SourceRef Scene(nint source, PixelRect area, EffectList list, SceneLayer? drawing = null)
    {
        scene.Ensure(device, area.Width, area.Height);
        var box = new D3D11_BOX
        {
            left = (uint)area.Left,
            top = (uint)area.Top,
            right = (uint)area.Right,
            bottom = (uint)area.Bottom,
            front = 0,
            back = 1,
        };
        context->CopySubresourceRegion((ID3D11Resource*)scene.Texture, 0, 0, 0, 0, (ID3D11Resource*)source, 0, &box);
        var current = scene;
        foreach (var layer in list.ImageLayers)
        {
            if (Intersection(area, layer.Bounds).IsEmpty)
            {
                continue;
            }
            var other = ReferenceEquals(current, scene) ? spare : scene;
            other.Ensure(device, area.Width, area.Height);
            var texture = LayerTextureFor(layer);
            var values = new PassConstants
            {
                DisplayOffsetX = area.Left,
                DisplayOffsetY = area.Top,
                LayerCenterX = (float)(layer.X + layer.Width / 2),
                LayerCenterY = (float)(layer.Y + layer.Height / 2),
                LayerCos = (float)Math.Cos(layer.Rotation),
                LayerSin = (float)Math.Sin(layer.Rotation),
                LayerLeft = (float)layer.X,
                LayerTop = (float)layer.Y,
                LayerTexelsX = (float)(texture.Width / layer.Width),
                LayerTexelsY = (float)(texture.Height / layer.Height),
                LayerWidth = texture.Width,
                LayerHeight = texture.Height,
                LayerOpacity = (float)layer.Opacity,
            };
            Pass(imageShader, current.View, other.Target, Viewport(0, 0, area.Width, area.Height), values, texture.View);
            current = other;
        }
        if (drawing is { } drawn && !Intersection(area, drawn.Bounds).IsEmpty)
        {
            var other = ReferenceEquals(current, scene) ? spare : scene;
            other.Ensure(device, area.Width, area.Height);
            var values = new PassConstants
            {
                DisplayOffsetX = area.Left,
                DisplayOffsetY = area.Top,
                LayerCenterX = (float)(drawn.Left + drawn.Width / 2.0),
                LayerCenterY = (float)(drawn.Top + drawn.Height / 2.0),
                LayerCos = 1,
                LayerSin = 0,
                LayerLeft = drawn.Left,
                LayerTop = drawn.Top,
                LayerTexelsX = 1,
                LayerTexelsY = 1,
                LayerWidth = drawn.Width,
                LayerHeight = drawn.Height,
                LayerOpacity = 1,
            };
            Pass(imageShader, current.View, other.Target, Viewport(0, 0, area.Width, area.Height), values, SourceView(drawn.Texture));
            current = other;
        }
        var count = 0;
        if (list.Privacy is { } privacy)
        {
            // Up to four bands around the crop, inside the area.
            Span<PixelRect> outside =
            [
                new(area.Left, area.Top, area.Right, privacy.Top),
                new(area.Left, privacy.Bottom, area.Right, area.Bottom),
                new(area.Left, Math.Max(area.Top, privacy.Top), privacy.Left, Math.Min(area.Bottom, privacy.Bottom)),
                new(privacy.Right, Math.Max(area.Top, privacy.Top), area.Right, Math.Min(area.Bottom, privacy.Bottom)),
            ];
            foreach (var band in outside)
            {
                AddLocalRect(Intersection(band, area), area, ref count);
            }
        }
        foreach (var cover in list.Covers)
        {
            AddLocalRect(Intersection(cover, area), area, ref count);
        }
        Clear(current.Target, count, transparent: false);
        return new SourceRef(current.View, current.Width, current.Height, area.Left, area.Top, area.Width, area.Height, composited: true)
        {
            Texture = current.Texture,
        };
    }

    private void AddLocalRect(PixelRect rect, PixelRect area, ref int count)
    {
        if (!rect.IsEmpty)
        {
            Rects(count + 1)[count++] = new RECT
            {
                left = rect.Left - area.Left,
                top = rect.Top - area.Top,
                right = rect.Right - area.Left,
                bottom = rect.Bottom - area.Top,
            };
        }
    }

    /// <summary>
    /// A Gaussian of <paramref name="deviation"/> pixels over <paramref name="reach"/> (the region plus the blur's
    /// reach, beyond the display edge too), cropped to <paramref name="region"/> in the target.
    /// </summary>
    private void Blur(in SourceRef source, PixelRect region, PixelRect reach, double deviation, ID3D11RenderTargetView* target)
    {
        var (levelCount, levelDeviation) = BlurPlan(deviation);
        var values = default(PassConstants);
        var taps = BlurWeights(levelDeviation, new Span<float>(values.Weights, WeightCount));
        var current = source;
        float offsetX = reach.Left - source.OriginX, offsetY = reach.Top - source.OriginY;
        int width = reach.Width, height = reach.Height;
        for (var level = 1; level <= levelCount; level++)
        {
            int reducedWidth = (width + 1) / 2, reducedHeight = (height + 1) / 2;
            var scratch = levels[level];
            scratch.Ensure(device, reducedWidth, reducedHeight);
            var down = Constants(current, 2, offsetX, offsetY);
            Pass(downShader, current.View, scratch.Target, Viewport(0, 0, reducedWidth, reducedHeight), down);
            current = new SourceRef(scratch.View, scratch.Width, scratch.Height, reach.Left, reach.Top, reducedWidth, reducedHeight, composited: false)
            {
                Linear = true,
            };
            offsetX = offsetY = 0;
            width = reducedWidth;
            height = reducedHeight;
        }

        horizontal.Ensure(device, width, height);
        var across = Constants(current, 1, offsetX, offsetY);
        across.DirectionX = 1;
        across.Taps = taps;
        CopyWeights(ref across, ref values, taps);
        Pass(blurShader, current.View, horizontal.Target, Viewport(0, 0, width, height), across);
        var blurred = new SourceRef(horizontal.View, horizontal.Width, horizontal.Height, reach.Left, reach.Top, width, height, composited: false)
        {
            Linear = true,
        };

        if (levelCount == 0)
        {
            // Straight into the target: a target pixel p reads texel p - reach origin.
            var down = Constants(blurred, 1, -reach.Left, -reach.Top);
            down.DirectionY = 1;
            down.Taps = taps;
            down.TargetSrgb = 1;
            CopyWeights(ref down, ref values, taps);
            Pass(blurShader, blurred.View, target, Viewport(region.Left, region.Top, region.Width, region.Height), down);
            return;
        }

        vertical.Ensure(device, width, height);
        var downward = Constants(blurred, 1, 0, 0);
        downward.DirectionY = 1;
        downward.Taps = taps;
        CopyWeights(ref downward, ref values, taps);
        Pass(blurShader, blurred.View, vertical.Target, Viewport(0, 0, width, height), downward);
        var finished = new SourceRef(vertical.View, vertical.Width, vertical.Height, reach.Left, reach.Top, width, height, composited: false)
        {
            Linear = true,
        };
        var inverse = 1f / (1 << levelCount);
        var up = Constants(finished, inverse, -reach.Left * inverse, -reach.Top * inverse);
        up.TargetSrgb = 1;
        Pass(upShader, finished.View, target, Viewport(region.Left, region.Top, region.Width, region.Height), up);
    }

    /// <summary>Pixelate or Distort: one pass that reads the scene at a computed point for every region pixel.</summary>
    private void Point(ID3D11PixelShader* shader, in SourceRef source, PixelRect region, float z, float w, ID3D11RenderTargetView* target)
    {
        var values = Constants(source, 1, 0, 0);
        values.EffectX = (region.Left + region.Right) / 2f;
        values.EffectY = (region.Top + region.Bottom) / 2f;
        values.EffectZ = z;
        values.EffectW = w;
        values.EffectOriginX = source.OriginX;
        values.EffectOriginY = source.OriginY;
        values.TargetSrgb = 1;
        // Core Image interpolates the encoded capture when it reads it directly, and its linear intermediate otherwise.
        values.SampleEncoded = source.Composited ? 0 : 1;
        Pass(shader, source.View, target, Viewport(region.Left, region.Top, region.Width, region.Height), values);
    }

    private void DrawCovers(ID3D11RenderTargetView* target, EffectList list, bool inRegionsOnly)
    {
        var count = 0;
        if (inRegionsOnly)
        {
            foreach (var region in list.Regions)
            {
                if (!region.IsVisible)
                {
                    continue;
                }
                foreach (var cover in list.Covers)
                {
                    var inside = Intersection(cover, region.Rect);
                    if (!inside.IsEmpty)
                    {
                        Rects(count + 1)[count++] = new RECT { left = inside.Left, top = inside.Top, right = inside.Right, bottom = inside.Bottom };
                    }
                }
            }
        }
        else
        {
            foreach (var cover in list.Covers)
            {
                Rects(count + 1)[count++] = new RECT { left = cover.Left, top = cover.Top, right = cover.Right, bottom = cover.Bottom };
            }
        }
        Clear(target, count, transparent: false);
    }

    private void ClearRects(ID3D11RenderTargetView* target, ReadOnlySpan<PixelRect> clear, bool transparent)
    {
        var count = 0;
        foreach (var rect in clear)
        {
            if (!rect.IsEmpty)
            {
                Rects(count + 1)[count++] = new RECT { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
            }
        }
        Clear(target, count, transparent);
    }

    private void Clear(ID3D11RenderTargetView* target, int count, bool transparent)
    {
        if (count == 0)
        {
            return;
        }
        var color = stackalloc float[4] { 0, 0, 0, transparent ? 0 : 1 };
        fixed (RECT* list = rects)
        {
            context->ClearView((ID3D11View*)target, color, list, (uint)count);
        }
    }

    private RECT[] Rects(int needed)
    {
        if (rects.Length < needed)
        {
            Array.Resize(ref rects, Math.Max(needed, rects.Length * 2));
        }
        return rects;
    }

    private static PassConstants Constants(in SourceRef source, float scale, float offsetX, float offsetY) => new()
    {
        SourceScaleX = scale,
        SourceScaleY = scale,
        SourceOffsetX = offsetX,
        SourceOffsetY = offsetY,
        SourceWidth = source.TextureWidth,
        SourceHeight = source.TextureHeight,
        ClampMinX = 0.5f,
        ClampMinY = 0.5f,
        ClampMaxX = source.ValidWidth - 0.5f,
        ClampMaxY = source.ValidHeight - 0.5f,
        SourceSrgb = source.Linear ? 0 : 1,
    };

    private static void CopyWeights(ref PassConstants target, ref PassConstants weights, int taps)
    {
        for (var index = 0; index <= taps; index++)
        {
            target.Weights[index] = weights.Weights[index];
        }
    }

    private static D3D11_VIEWPORT Viewport(int left, int top, int width, int height) => new()
    {
        TopLeftX = left,
        TopLeftY = top,
        Width = width,
        Height = height,
        MinDepth = 0,
        MaxDepth = 1,
    };

    private void Begin()
    {
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->IASetInputLayout(null);
        context->VSSetShader(vertexShader, null, 0);
        context->RSSetState(null);
        var blendFactor = stackalloc float[4] { 1, 1, 1, 1 };
        context->OMSetBlendState(null, blendFactor, 0xFFFFFFFF);
        context->OMSetDepthStencilState(null, 0);
        var samplerState = sampler;
        context->PSSetSamplers(0, 1, &samplerState);
        var buffer = constants;
        context->PSSetConstantBuffers(0, 1, &buffer);
    }

    private void End()
    {
        var noViews = stackalloc ID3D11ShaderResourceView*[2] { null, null };
        context->PSSetShaderResources(0, 2, noViews);
        context->OMSetRenderTargets(0, null, null);
    }

    private void Pass(ID3D11PixelShader* shader, ID3D11ShaderResourceView* source, ID3D11RenderTargetView* target, D3D11_VIEWPORT viewport,
                      in PassConstants values, ID3D11ShaderResourceView* layer = null)
    {
        D3D11_MAPPED_SUBRESOURCE mapped;
        Native.Check(context->Map((ID3D11Resource*)constants, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "ID3D11DeviceContext::Map(effect constants)");
        *(PassConstants*)mapped.pData = values;
        context->Unmap((ID3D11Resource*)constants, 0);
        // The target replaces the previous one before its texture is read, so no texture is bound both ways.
        context->OMSetRenderTargets(1, &target, null);
        context->RSSetViewports(1, &viewport);
        context->PSSetShader(shader, null, 0);
        var views = stackalloc ID3D11ShaderResourceView*[2] { source, layer };
        context->PSSetShaderResources(0, 2, views);
        context->Draw(3, 0);
        views[0] = null;
        views[1] = null;
        context->PSSetShaderResources(0, 2, views);
        Passes++;
    }

    private ID3D11ShaderResourceView* SourceView(nint texture)
    {
        if (sourceViews.TryGetValue(texture, out var cached))
        {
            return (ID3D11ShaderResourceView*)cached;
        }
        if (sourceViews.Count >= ViewCacheLimit)
        {
            ReleaseViews(sourceViews);
        }
        ID3D11ShaderResourceView* view;
        Native.Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "ID3D11Device::CreateShaderResourceView(captured frame)");
        Marshal.AddRef(texture);
        sourceViews[texture] = (nint)view;
        return view;
    }

    private ID3D11RenderTargetView* TargetView(nint texture)
    {
        if (targetViews.TryGetValue(texture, out var cached))
        {
            return (ID3D11RenderTargetView*)cached;
        }
        if (targetViews.Count >= ViewCacheLimit)
        {
            ReleaseViews(targetViews);
        }
        ID3D11RenderTargetView* view;
        Native.Check(device->CreateRenderTargetView((ID3D11Resource*)texture, null, &view), "ID3D11Device::CreateRenderTargetView(effect target)");
        Marshal.AddRef(texture);
        targetViews[texture] = (nint)view;
        return view;
    }

    private static void ReleaseViews(Dictionary<nint, nint> views)
    {
        foreach (var (texture, view) in views)
        {
            Marshal.Release(view);
            Marshal.Release(texture);
        }
        views.Clear();
    }

    /// <summary>The decoded image of a layer as a texture, decoded once while lists use it.</summary>
    private LayerTexture LayerTextureFor(EffectImageLayer layer)
    {
        if (layerTextures.TryGetValue(layer.Png, out var cached))
        {
            return cached;
        }
        wic ??= WicImaging.Create();
        var image = wic.DecodeImage(layer.Png.AsSpan());
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)image.Width,
            Height = (uint)image.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        fixed (byte* pixels = image.Buffer)
        {
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = pixels, SysMemPitch = (uint)image.Stride };
            Native.Check(device->CreateTexture2D(&description, &data, &texture), "ID3D11Device::CreateTexture2D(image layer)");
        }
        ID3D11ShaderResourceView* view;
        var result = device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view);
        if (result.Failed)
        {
            texture->Release();
            Native.Check(result, "ID3D11Device::CreateShaderResourceView(image layer)");
        }
        var entry = new LayerTexture { Texture = texture, View = view, Width = image.Width, Height = image.Height };
        layerTextures[layer.Png] = entry;
        return entry;
    }

    /// <summary>Releases the image textures the list does not use.</summary>
    private void ReleaseUnusedLayers(EffectList list)
    {
        if (layerTextures.Count == 0)
        {
            return;
        }
        List<ValueList<byte>>? unused = null;
        foreach (var key in layerTextures.Keys)
        {
            var used = false;
            foreach (var layer in list.ImageLayers)
            {
                if (layer.Png == key)
                {
                    used = true;
                    break;
                }
            }
            if (!used)
            {
                (unused ??= []).Add(key);
            }
        }
        if (unused is null)
        {
            return;
        }
        foreach (var key in unused)
        {
            layerTextures[key].Release();
            layerTextures.Remove(key);
        }
    }

    private ID3D11VertexShader* CreateVertexShader(string entry)
    {
        var code = Bytecode(entry);
        ID3D11VertexShader* shader;
        fixed (byte* bytes = code)
        {
            Native.Check(device->CreateVertexShader(bytes, (nuint)code.Length, null, &shader), $"ID3D11Device::CreateVertexShader({entry})");
        }
        return shader;
    }

    private ID3D11PixelShader* CreatePixelShader(string entry)
    {
        var code = Bytecode(entry);
        ID3D11PixelShader* shader;
        fixed (byte* bytes = code)
        {
            Native.Check(device->CreatePixelShader(bytes, (nuint)code.Length, null, &shader), $"ID3D11Device::CreatePixelShader({entry})");
        }
        return shader;
    }

    private static byte[] Bytecode(string entry)
    {
        using var stream = typeof(EffectPipeline).Assembly.GetManifestResourceStream(entry + ".cso")
            ?? throw new InvalidOperationException($"The effect shader {entry} is missing from the build.");
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        return code;
    }

    private const int WeightCount = 28;

    /// <summary>
    /// A texture to read: its view and size, the display position of its first texel and its valid area, and whether
    /// it holds a composited scene (not the bare capture) or linear 16-bit values (a blur level).
    /// </summary>
    private readonly struct SourceRef(ID3D11ShaderResourceView* view, int textureWidth, int textureHeight, int originX, int originY,
                                      int validWidth, int validHeight, bool composited)
    {
        public ID3D11ShaderResourceView* View { get; } = view;

        public ID3D11Texture2D* Texture { get; init; }

        public int TextureWidth { get; } = textureWidth;

        public int TextureHeight { get; } = textureHeight;

        public int OriginX { get; } = originX;

        public int OriginY { get; } = originY;

        public int ValidWidth { get; } = validWidth;

        public int ValidHeight { get; } = validHeight;

        public bool Composited { get; } = composited;

        public bool Linear { get; init; }
    }

    /// <summary>The shader's constant buffer (effects.hlsl, EffectPass): 256 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PassConstants
    {
        public float SourceScaleX;
        public float SourceScaleY;
        public float SourceOffsetX;
        public float SourceOffsetY;
        public float SourceWidth;
        public float SourceHeight;
        public float DirectionX;
        public float DirectionY;
        public float EffectX;
        public float EffectY;
        public float EffectZ;
        public float EffectW;
        public float DisplayOffsetX;
        public float DisplayOffsetY;
        public float EffectOriginX;
        public float EffectOriginY;
        public float ClampMinX;
        public float ClampMinY;
        public float ClampMaxX;
        public float ClampMaxY;
        public int Taps;
        public int SourceSrgb;
        public int TargetSrgb;
        public int SampleEncoded;
        public float LayerCenterX;
        public float LayerCenterY;
        public float LayerCos;
        public float LayerSin;
        public float LayerLeft;
        public float LayerTop;
        public float LayerTexelsX;
        public float LayerTexelsY;
        public float LayerWidth;
        public float LayerHeight;
        public float LayerOpacity;
        public float LayerUnused;
        public fixed float Weights[WeightCount];
    }

    /// <summary>A decoded image layer on the device.</summary>
    private sealed class LayerTexture
    {
        public ID3D11Texture2D* Texture;
        public ID3D11ShaderResourceView* View;

        public int Width { get; init; }

        public int Height { get; init; }

        public void Release()
        {
            Native.Release(ref View);
            Native.Release(ref Texture);
        }
    }

    /// <summary>
    /// A render target that can also be read, grown to the largest size asked for; with <paramref name="exact"/>, made at exactly
    /// the size asked for.
    /// </summary>
    private sealed class Scratch(DXGI_FORMAT format, bool exact = false)
    {
        public ID3D11Texture2D* Texture;
        public ID3D11ShaderResourceView* View;
        public ID3D11RenderTargetView* Target;

        public int Width { get; private set; }

        public int Height { get; private set; }

        public void Ensure(ID3D11Device* device, int width, int height)
        {
            if (Texture != null && (exact ? width == Width && height == Height : width <= Width && height <= Height))
            {
                return;
            }
            var newWidth = exact ? width : RoundUp(Math.Max(width, Width));
            var newHeight = exact ? height : RoundUp(Math.Max(height, Height));
            Release();
            var description = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)newWidth,
                Height = (uint)newHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
            };
            ID3D11Texture2D* texture;
            Native.Check(device->CreateTexture2D(&description, null, &texture), "ID3D11Device::CreateTexture2D(effect scratch)");
            Texture = texture;
            ID3D11ShaderResourceView* view;
            Native.Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "ID3D11Device::CreateShaderResourceView(effect scratch)");
            View = view;
            ID3D11RenderTargetView* target;
            Native.Check(device->CreateRenderTargetView((ID3D11Resource*)texture, null, &target), "ID3D11Device::CreateRenderTargetView(effect scratch)");
            Target = target;
            Width = newWidth;
            Height = newHeight;
        }

        public void Release()
        {
            Native.Release(ref Target);
            Native.Release(ref View);
            Native.Release(ref Texture);
            Width = 0;
            Height = 0;
        }

        private static int RoundUp(int value) => (value + ScratchGranularity - 1) / ScratchGranularity * ScratchGranularity;
    }
}

/// <summary>
/// A drawn layer for <see cref="EffectPipeline.RenderScene"/>: a premultiplied B8G8R8A8_UNORM texture with shader-resource
/// binding, <paramref name="Width"/> by <paramref name="Height"/> texels placed one texel per pixel with its top-left corner at
/// (<paramref name="Left"/>, <paramref name="Top"/>) in the list's pixels.
/// </summary>
internal readonly record struct SceneLayer(nint Texture, int Width, int Height, int Left, int Top)
{
    /// <summary>The pixels the layer can change.</summary>
    public PixelRect Bounds => new(Left, Top, Left + Width, Top + Height);
}
