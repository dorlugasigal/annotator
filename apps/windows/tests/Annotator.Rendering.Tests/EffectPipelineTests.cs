using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.Rendering.Tests;

/// <summary>Synthetic frames and targets on a device, for effect tests.</summary>
internal static unsafe class EffectTextures
{
    /// <summary>An opaque image whose pixels come from <paramref name="color"/> (RGB bytes).</summary>
    public static RasterImage Image(int width, int height, Func<int, int, (byte Red, byte Green, byte Blue)> color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (red, green, blue) = color(x, y);
                var offset = (y * width + x) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 255;
            }
        }
        return new RasterImage(width, height, width * 4, pixels);
    }

    public static nint Upload(GraphicsDevices devices, RasterImage image)
    {
        var description = Description(image.Width, image.Height, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        ID3D11Texture2D* texture;
        fixed (byte* pixels = image.Pixels)
        {
            var data = new D3D11_SUBRESOURCE_DATA { pSysMem = pixels, SysMemPitch = (uint)image.Stride };
            Native.Check(((ID3D11Device*)devices.D3D11Device)->CreateTexture2D(&description, &data, &texture), "CreateTexture2D(source)");
        }
        return (nint)texture;
    }

    public static nint Target(GraphicsDevices devices, int width, int height)
    {
        var description = Description(width, height, D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE);
        ID3D11Texture2D* texture;
        Native.Check(((ID3D11Device*)devices.D3D11Device)->CreateTexture2D(&description, null, &texture), "CreateTexture2D(target)");
        return (nint)texture;
    }

    public static RasterImage Read(GraphicsDevices devices, nint texture, int width, int height)
    {
        var description = Description(width, height, 0);
        description.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        description.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Native.Check(((ID3D11Device*)devices.D3D11Device)->CreateTexture2D(&description, null, &staging), "CreateTexture2D(staging)");
        try
        {
            var context = (ID3D11DeviceContext*)devices.D3D11DeviceContext;
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Native.Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(staging)");
            try
            {
                var pixels = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + (long)row * mapped.RowPitch, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
                }
                return new RasterImage(width, height, width * 4, pixels);
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            staging->Release();
        }
    }

    private static D3D11_TEXTURE2D_DESC Description(int width, int height, D3D11_BIND_FLAG bind) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
        Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
        BindFlags = bind,
    };
}

/// <summary>
/// The effect pipeline on WARP with synthetic frames. Ports the Mac CaptureTests assertions that apply to the live
/// effect layer (BlurLevelTests, CensorSamplingTests, CaptureUtilityTests) and checks the Windows formulas.
/// </summary>
public sealed class EffectPipelineTests
{
    private static Element Effect(Tool tool, Rect frame, double? amount = null)
    {
        var element = new Element(tool, frame);
        element.Style.EffectAmount = amount;
        return element;
    }

    private static EffectList List(int width, int height, double scale, params Element[] elements) =>
        EffectList.From(ElementList.Create(elements), scale, width, height);

    /// <summary>The effect layer alone: transparent outside the regions.</summary>
    private static RasterImage Layer(RasterImage source, EffectList list)
    {
        var devices = Warp.Devices;
        using var pipeline = new EffectPipeline(devices);
        var input = EffectTextures.Upload(devices, source);
        var target = EffectTextures.Target(devices, source.Width, source.Height);
        try
        {
            pipeline.Render(input, target, list, [new PixelRect(0, 0, source.Width, source.Height)]);
            return EffectTextures.Read(devices, target, source.Width, source.Height);
        }
        finally
        {
            Marshal.Release(target);
            Marshal.Release(input);
        }
    }

    /// <summary>What the screen shows: the layer over the source (the layer is opaque wherever it draws).</summary>
    private static RasterImage Flattened(RasterImage source, EffectList list)
    {
        var layer = Layer(source, list);
        var pixels = source.Pixels.ToArray();
        var drawn = layer.Pixels;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if (drawn[offset + 3] != 0)
            {
                drawn.Slice(offset, 4).CopyTo(pixels.AsSpan(offset, 4));
            }
        }
        return new RasterImage(source.Width, source.Height, source.Width * 4, pixels);
    }

    private static double Linear(byte value)
    {
        var c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static RasterImage Checker() => EffectTextures.Image(64, 64, (x, y) => (x / 2 + y / 2) % 2 == 0 ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0));

    /// <summary>The whole frame (<see cref="EffectPipeline.RenderScene"/>).</summary>
    private static RasterImage Scene(RasterImage source, EffectList list)
    {
        var devices = Warp.Devices;
        using var pipeline = new EffectPipeline(devices);
        var input = EffectTextures.Upload(devices, source);
        var target = EffectTextures.Target(devices, source.Width, source.Height);
        try
        {
            pipeline.RenderScene(input, target, list);
            return EffectTextures.Read(devices, target, source.Width, source.Height);
        }
        finally
        {
            Marshal.Release(target);
            Marshal.Release(input);
        }
    }

    private static Element Picture(Rect frame, RasterImage art, double rotation = 0, double opacity = 1)
    {
        var element = new Element(Tool.Image, frame) { ImagePng = ValueList.Create<byte>(RasterAsset.EncodePng(art)), Rotation = rotation };
        element.Style.Opacity = opacity;
        return element;
    }

    // Mac CaptureCompositor with includeImageLayers: a pasted image is part of the scene the effects read.
    [Fact]
    public void ImageLayersAreFilteredUnderARegion()
    {
        var source = EffectTextures.Image(96, 64, (_, _) => ((byte)128, (byte)128, (byte)128));
        var red = EffectTextures.Image(8, 8, (_, _) => ((byte)255, (byte)0, (byte)0));
        var elements = ElementList.Create([Picture(new Rect(16, 16, 32, 32), red), Effect(Tool.Blur, new Rect(8, 8, 48, 48), 4)]);
        var without = Layer(source, EffectList.From(elements, 1, 96, 64));
        var with = EffectList.From(elements, 1, 96, 64, includeImageLayers: true);
        var layer = Assert.Single(with.ImageLayers.ToArray());
        Assert.Equal((16.0, 16.0, 32.0, 32.0), (layer.X, layer.Y, layer.Width, layer.Height));
        var blurred = Layer(source, with);
        Assert.InRange(without[32, 32].Red, 127, 129);
        Assert.InRange(without[32, 32].Green, 127, 129);
        Assert.True(blurred[32, 32].Red > 200 && blurred[32, 32].Green < 80, $"The blurred image shows: {blurred[32, 32]}.");
        // The layer is drawn only where a region is; elsewhere the effect layer stays transparent.
        Assert.Equal(0, blurred[80, 32].Alpha);
        // Half opacity in linear light: the middle of the image is a linear mix of red and gray.
        var half = Scene(source, EffectList.From(ElementList.Create([Picture(new Rect(16, 16, 32, 32), red, opacity: 0.5)]), 1, 96, 64, includeImageLayers: true));
        var mixed = Linear(half[32, 32].Red);
        Assert.InRange(mixed, 0.5 * 1 + 0.5 * Linear(128) - 0.01, 0.5 * 1 + 0.5 * Linear(128) + 0.01);
        // Turned a quarter turn, a 32 x 16 image covers 16 x 32 pixels around the same center.
        var turned = Scene(source, EffectList.From(ElementList.Create([Picture(new Rect(16, 24, 32, 16), red, rotation: Math.PI / 2)]), 1, 96, 64,
                                                   includeImageLayers: true));
        Assert.Equal(255, turned[32, 18].Red);
        Assert.Equal(128, turned[20, 32].Red);
    }

    [Fact]
    public void ImageLayersNeedImageData()
    {
        var empty = new Element(Tool.Image, new Rect(0, 0, 10, 10));
        Assert.Throws<ArgumentException>(() => EffectList.From(ElementList.Create([empty]), 1, 50, 50, includeImageLayers: true));
        Assert.True(EffectList.From(ElementList.Create([empty]), 1, 50, 50).ImageLayers.IsEmpty);
    }

    // Mac CaptureCompositor with a privacy region (the shared region window): outside turns black before any filter.
    [Fact]
    public void PrivacyCropsBlackenTheSceneBeforeFilters()
    {
        Assert.Equal(new PixelRect(17, 17, 156, 106), EffectList.PrivacyRect(new Rect(16.5, 16.5, 140, 90), 1, 192, 128));
        Assert.Equal(new PixelRect(21, 21, 195, 133), EffectList.PrivacyRect(new Rect(16.5, 16.5, 140, 90), 1.25, 240, 160));
        Assert.Throws<ArgumentException>(() => EffectList.PrivacyRect(new Rect(-1, 0, 10, 10), 1, 100, 100));
        Assert.Throws<ArgumentException>(() => EffectList.PrivacyRect(new Rect(95, 0, 10, 10), 1, 100, 100));
        Assert.Throws<ArgumentException>(() => EffectList.PrivacyRect(new Rect(10.2, 10.2, 0.6, 5), 1, 100, 100));
        var source = EffectTextures.Image(96, 64, (_, _) => ((byte)255, (byte)255, (byte)255));
        var crop = new Rect(10, 10, 60, 40);
        var plain = Scene(source, EffectList.From(ElementList.Empty, 1, 96, 64, privacyRegion: crop));
        Assert.Equal((0, 0, 0, 255), plain[5, 5]);
        Assert.Equal((255, 255, 255, 255), plain[40, 30]);
        // A blur that reaches across the crop's edge pulls black in, never the pixels outside.
        var blurred = Scene(source, EffectList.From(ElementList.Create([Effect(Tool.Blur, new Rect(5, 5, 40, 40), 6)]), 1, 96, 64, privacyRegion: crop));
        Assert.True(blurred[10, 30].Red < 230, $"Black from outside the crop: {blurred[10, 30]}.");
        Assert.Equal((0, 0, 0, 255), blurred[90, 60]);
    }

    [Fact]
    public void ScenesWithoutEffectsCopyTheSourceAndCovers()
    {
        var source = Checker();
        Assert.True(Pixels.SamePixels(source, Scene(source, List(64, 64, 1))));
        var covered = Scene(source, List(64, 64, 1, new Element(Tool.Cover, new Rect(10.5, 10, 10, 10)), Effect(Tool.Blur, new Rect(30, 30, 10, 10), 0)));
        Assert.Equal((0, 0, 0, 255), covered[10, 10]);
        Assert.Equal((0, 0, 0, 255), covered[20, 19]);
        Assert.Equal(source[35, 35], covered[35, 35]);
        Assert.Equal(source[9, 9], covered[9, 9]);
    }

    // Mac BlurLevelTests.zeroStrengthBypassesFiltersWithoutErasingOtherProtection.
    [Fact]
    public void ZeroStrengthLeavesPixelsUnchanged()
    {
        var source = EffectTextures.Image(64, 64, (x, _) => x < 32 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var blur = Effect(Tool.Blur, new Rect(0, 0, 64, 64), 12);
        var cover = new Element(Tool.Cover, new Rect(16, 16, 32, 32));
        foreach (var tool in new[] { Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            var zero = Effect(tool, new Rect(0, 0, 64, 64), 0);
            zero.Validate();
            // Like the Mac, capture runs for a region of strength zero, which draws nothing.
            Assert.True(List(64, 64, 1, zero).NeedsPixels);
            Assert.False(List(64, 64, 1, zero).HasVisibleRegions);
            Assert.True(Pixels.SamePixels(Layer(source, List(64, 64, 1, zero)), Layer(source, List(64, 64, 1))));
            Assert.True(Pixels.SamePixels(Flattened(source, List(64, 64, 1, zero)), source));
            Assert.True(Pixels.SamePixels(Layer(source, List(64, 64, 1, blur, zero)), Layer(source, List(64, 64, 1, blur))));
            Assert.True(Pixels.SamePixels(Layer(source, List(64, 64, 1, cover, zero)), Layer(source, List(64, 64, 1, cover))));
            zero.Style.EffectAmount = -0.1;
            Assert.Throws<CanvasError>(() => zero.Validate());
            Assert.Throws<ArgumentException>(() => List(64, 64, 1, zero));
        }
    }

    // Mac BlurLevelTests.changingBlurStrengthChangesTheRenderedRegion.
    [Fact]
    public void ChangingBlurStrengthChangesTheRegion()
    {
        var source = EffectTextures.Image(128, 32, (x, _) => x < 64 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var samples = new List<int>();
        foreach (var strength in new[] { 4.0, 40 })
        {
            var output = Flattened(source, List(128, 32, 1, Effect(Tool.Blur, new Rect(0, 0, 128, 32), strength)));
            samples.Add(output[48, 16].Red);
        }
        Assert.True(samples[1] > samples[0] + 20, $"Strength 4 gave {samples[0]}, strength 40 gave {samples[1]}.");
    }

    // Mac CensorSamplingTests.filtersCannotSampleSecretsOutOfASolidCensor.
    [Fact]
    public void FiltersCannotSampleSecretsOutOfACover()
    {
        RasterImage Secret((byte, byte, byte) color) =>
            EffectTextures.Image(64, 64, (x, y) => x >= 24 && x < 40 && y >= 24 && y < 40 ? color : ((byte)255, (byte)255, (byte)255));
        var red = Secret((255, 0, 0));
        var blue = Secret((0, 0, 255));
        var cover = new Element(Tool.Cover, new Rect(24, 24, 16, 16));
        foreach (var tool in new[] { Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            var list = List(64, 64, 1, cover, Effect(tool, new Rect(8, 8, 48, 48), 40));
            Assert.True(Pixels.SamePixels(Layer(red, list), Layer(blue, list)), $"{tool} read pixels under the cover.");
            if (tool != Tool.Pixelate)
            {
                // Without the cover the secret's color reaches the region. (Pixelate's 40-pixel cells sample at 12 and 52 here.)
                var open = List(64, 64, 1, Effect(tool, new Rect(8, 8, 48, 48), 40));
                Assert.False(Pixels.SamePixels(Layer(red, open), Layer(blue, open)), $"{tool} without the cover");
            }
        }
    }

    // Mac CaptureUtilityTests.visualEffectsChangeOnlySelectedPixels.
    [Fact]
    public void EffectsChangeOnlyTheirRegion()
    {
        var source = Checker();
        foreach (var tool in new[] { Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            var list = List(64, 64, 1, Effect(tool, new Rect(16, 16, 32, 32), 25));
            var layer = Layer(source, list);
            Assert.True(Pixels.TransparentOutside(layer, 16, 16, 48, 48), $"{tool} drew outside its region.");
            Assert.Equal(32 * 32, Pixels.Painted(layer));
            var result = Flattened(source, list);
            Assert.False(Pixels.SamePixels(result, source), $"{tool} changed nothing.");
            Assert.Equal(source[0, 0], result[0, 0]);
            Assert.Equal(source[63, 63], result[63, 63]);
        }
    }

    // Mac CaptureUtilityTests.opaqueCoverWinsAndOverlayOutsideIsTransparent.
    [Fact]
    public void OpaqueCoversWinAndOutsideIsTransparent()
    {
        var source = Checker();
        var cover = new Element(Tool.Cover, new Rect(16, 16, 32, 32));
        cover.Style.Opacity = 0;
        var blur = Effect(Tool.Blur, cover.Frame);
        var layer = Layer(source, List(64, 64, 1, cover, blur));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), layer[32, 32]);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), layer[16, 16]);
        Assert.Equal(0, Layer(source, List(64, 64, 1, blur))[0, 0].Alpha);
        // A cover drawn earlier in the document still wins over a later effect.
        var later = Layer(source, List(64, 64, 1, blur, cover));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), later[40, 20]);
    }

    [Fact]
    public void PixelRectsRoundOutwardAtTheDisplayScale()
    {
        Assert.Equal(new PixelRect(12, 25, 51, 58), EffectList.PixelRect(new Rect(10.3, 20.6, 30.2, 25.1), 1.25, 200, 100));
        Assert.Equal(new PixelRect(0, 0, 10, 12), EffectList.PixelRect(new Rect(-5, -5, 13, 14.5), 1.25, 200, 100));
        Assert.Equal(new PixelRect(190, 90, 200, 100), EffectList.PixelRect(new Rect(152, 72, 50, 50), 1.25, 200, 100));
        Assert.True(EffectList.PixelRect(new Rect(300, 10, 10, 10), 1, 200, 100).IsEmpty);
        Assert.Throws<ArgumentException>(() => EffectList.PixelRect(new Rect(double.NaN, 0, 1, 1), 1, 200, 100));

        var list = List(200, 100, 1.5, new Element(Tool.Cover, new Rect(1, 1, 2, 2)), Effect(Tool.Pixelate, new Rect(10, 10, 20, 20)));
        var region = Assert.Single(list.Regions.ToArray());
        Assert.Equal(EffectKind.Pixelate, region.Kind);
        Assert.Equal(EffectList.DefaultAmount, region.Amount);
        Assert.Equal(new PixelRect(15, 15, 45, 45), region.Rect);
        Assert.Equal([new PixelRect(1, 1, 5, 5)], list.Covers.ToArray());
        Assert.Equal(list, List(200, 100, 1.5, new Element(Tool.Cover, new Rect(1, 1, 2, 2)), Effect(Tool.Pixelate, new Rect(10, 10, 20, 20))));
        Assert.NotEqual(list, List(200, 100, 1.5, Effect(Tool.Pixelate, new Rect(10, 10, 20, 20))));
    }

    [Fact]
    public void BlurKeepsUniformColorsUpToTheDisplayEdges()
    {
        var source = EffectTextures.Image(96, 48, (_, _) => (128, 64, 200));
        foreach (var strength in new[] { 1.5, 6, 30, 100 })
        {
            var layer = Layer(source, List(96, 48, 1.25, Effect(Tool.Blur, new Rect(0, 0, 96 / 1.25, 48 / 1.25), strength)));
            for (var y = 0; y < 48; y += 5)
            {
                for (var x = 0; x < 96; x += 5)
                {
                    var (red, green, blue, alpha) = layer[x, y];
                    Assert.True(Math.Abs(red - 128) <= 1 && Math.Abs(green - 64) <= 1 && Math.Abs(blue - 200) <= 1 && alpha == 255,
                                $"Strength {strength} at ({x}, {y}): ({red}, {green}, {blue}, {alpha}).");
                }
            }
        }
    }

    [Fact]
    public void BlurWorksInLinearLightLikeCoreImage()
    {
        var source = EffectTextures.Image(128, 16, (x, _) => x < 64 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var layer = Layer(source, List(128, 16, 1, Effect(Tool.Blur, new Rect(0, 0, 128, 16), 3)));
        var left = layer[63, 8].Red;
        var right = layer[64, 8].Red;
        // Symmetric around the edge in linear light: the two values add up to one; in sRGB values they would add up to 255.
        Assert.InRange(Linear(left) + Linear(right), 0.98, 1.02);
        Assert.True(left + right > 285, $"{left} + {right}");
    }

    [Fact]
    public void LargeBlursFollowAGaussianStepResponse()
    {
        const double Deviation = 30;
        var source = EffectTextures.Image(512, 8, (x, _) => x < 256 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var layer = Layer(source, List(512, 8, 1, Effect(Tool.Blur, new Rect(0, 0, 512, 8), Deviation)));
        Assert.True(EffectPipeline.BlurPlan(Deviation).Levels > 0, "This deviation uses reduced levels.");
        foreach (var distance in new[] { -60, -30, -10, 0, 10, 30, 60 })
        {
            var x = 256 + distance;
            // Pixel x covers [x, x + 1); its center is distance + 0.5 from the edge.
            var expected = NormalCdf((distance + 0.5) / Deviation);
            var actual = Linear(layer[x, 4].Red);
            Assert.True(Math.Abs(actual - expected) < 0.025, $"At {distance}: {actual:F4}, a Gaussian gives {expected:F4}.");
        }
    }

    [Fact]
    public void BlurPlansKeepTheSeparablePassSmall()
    {
        Assert.Equal((0, 2.0), EffectPipeline.BlurPlan(2));
        Assert.Equal(0, EffectPipeline.BlurPlan(EffectPipeline.LevelDeviation).Levels);
        var (levels, deviation) = EffectPipeline.BlurPlan(22.5);
        Assert.Equal(2, levels);
        Assert.InRange(deviation, 5.55, 5.65);
        Assert.Equal(EffectPipeline.MaxLevels, EffectPipeline.BlurPlan(5_000).Levels);
        Span<float> weights = stackalloc float[28];
        var taps = EffectPipeline.BlurWeights(2.5, weights);
        Assert.Equal(9, taps);
        var total = weights[0];
        for (var index = 1; index <= taps; index++)
        {
            total += 2 * weights[index];
            Assert.True(weights[index] < weights[index - 1]);
        }
        Assert.InRange(total, 0.9999, 1.0001);
        Assert.Equal(EffectPipeline.MaxTaps, EffectPipeline.BlurWeights(100, weights));
        Assert.True(EffectPipeline.BlurWeights(EffectPipeline.LevelDeviation, weights) <= EffectPipeline.MaxTaps);
    }

    [Fact]
    public void PixelateCellsAreUniformAndAlignedToTheRegionCenter()
    {
        var source = EffectTextures.Image(160, 80, (x, y) => ((byte)x, (byte)(y * 3), 90));
        // Region (10, 10)-(110, 70): center (60, 40), cells of 8 pixels, grid lines at 60 + 8k and 40 + 8k.
        var layer = Layer(source, List(160, 80, 1, Effect(Tool.Pixelate, new Rect(10, 10, 100, 60), 8)));
        for (var y = 24; y < 32; y++)
        {
            for (var x = 20; x < 28; x++)
            {
                Assert.Equal(layer[20, 24], layer[x, y]);
            }
        }
        Assert.NotEqual(layer[19, 24], layer[20, 24]);
        Assert.NotEqual(layer[20, 23], layer[20, 24]);
        Assert.Equal(layer[59, 39], layer[52, 32]);
        Assert.NotEqual(layer[59, 39], layer[60, 40]);
        // A cell shows the color at its center: the cell [20, 28) x [24, 32) has its center at (24, 28).
        var (red, green, _, _) = layer[20, 24];
        Assert.InRange(red, 23, 24);
        Assert.InRange(green, 81, 84);
    }

    [Fact]
    public void DistortTurnsAroundTheRegionCenter()
    {
        var source = EffectTextures.Image(128, 128, (x, y) => ((byte)(x * 2), (byte)(y * 2), (byte)((x + y) % 256)));
        var list = List(128, 128, 1, Effect(Tool.Distort, new Rect(32, 32, 64, 64), 50));
        var layer = Layer(source, list);
        // The center point does not move (the pixel there moves by less than a pixel); the rest turns.
        var (red, green, blue, _) = layer[64, 64];
        Assert.True(Math.Abs(red - 128) <= 6 && Math.Abs(green - 128) <= 6 && Math.Abs(blue - 128) <= 6, $"Center: ({red}, {green}, {blue}).");
        var turned = 0;
        for (var y = 32; y < 96; y += 4)
        {
            for (var x = 32; x < 96; x += 4)
            {
                if (layer[x, y] != source[x, y])
                {
                    turned++;
                }
            }
        }
        Assert.True(turned > 200, $"{turned} sampled pixels moved.");
        var region = list.Regions[0];
        Assert.Equal(new PixelRect(0, 0, 128, 128), EffectPipeline.Footprint(region, list));
    }

    [Fact]
    public void FootprintsHoldWhatEachFilterReads()
    {
        var list = List(4000, 2000, 2, Effect(Tool.Blur, new Rect(500, 400, 100, 50), 20), Effect(Tool.Pixelate, new Rect(10, 10, 20, 20), 10),
                        Effect(Tool.Distort, new Rect(1000, 500, 40, 20), 30));
        var blur = EffectPipeline.Footprint(list.Regions[0], list);
        var reach = (int)Math.Ceiling(3 * 20 * 2.0);
        Assert.True(blur.Left <= 1000 - reach && blur.Right >= 1200 + reach && blur.Top <= 800 - reach && blur.Bottom >= 900 + reach);
        var pixelate = EffectPipeline.Footprint(list.Regions[1], list);
        Assert.Equal(0, pixelate.Left);
        Assert.True(pixelate.Right >= 60 + 20);
        var twirl = EffectPipeline.Footprint(list.Regions[2], list);
        Assert.True(twirl.Left <= 2040 - 80 && twirl.Right >= 2040 + 80 && twirl.Top <= 1020 - 80 && twirl.Bottom >= 1020 + 80);
    }

    [Fact]
    public void RepeatedRendersAreIdenticalAndRegionsKeepTheirOrder()
    {
        var source = Checker();
        var blur = Effect(Tool.Blur, new Rect(4, 4, 40, 40), 6);
        var pixelate = Effect(Tool.Pixelate, new Rect(24, 24, 36, 36), 6);
        var list = List(64, 64, 1, blur, pixelate);
        var devices = Warp.Devices;
        using var pipeline = new EffectPipeline(devices);
        var input = EffectTextures.Upload(devices, source);
        var target = EffectTextures.Target(devices, 64, 64);
        try
        {
            pipeline.Render(input, target, list, [new PixelRect(0, 0, 64, 64)]);
            var first = EffectTextures.Read(devices, target, 64, 64);
            var passes = pipeline.Passes;
            pipeline.Render(input, target, list, [new PixelRect(0, 0, 64, 64)]);
            Assert.True(Pixels.SamePixels(first, EffectTextures.Read(devices, target, 64, 64)));
            Assert.Equal(passes * 2, pipeline.Passes);
            // The later pixelate region is on top where the two overlap.
            Assert.Equal(Layer(source, List(64, 64, 1, pixelate))[40, 40], first[40, 40]);
        }
        finally
        {
            Marshal.Release(target);
            Marshal.Release(input);
        }
    }

    private static double NormalCdf(double value) => 0.5 * (1 + Erf(value / Math.Sqrt(2)));

    private static double Erf(double x)
    {
        // Abramowitz and Stegun 7.1.26, error below 1.5e-7.
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1 / (1 + 0.3275911 * x);
        var y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }
}
