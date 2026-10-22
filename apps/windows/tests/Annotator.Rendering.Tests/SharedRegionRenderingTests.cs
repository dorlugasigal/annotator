using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform.Displays;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Sharing;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct3D11;

namespace Annotator.Rendering.Tests;

/// <summary>
/// The shared region's pixels (WIN-CAP-08): filtering only the region and a one-pixel margin gives exactly the pixels of
/// filtering the whole display, and the drawings are part of the scene the filters read (the Mac's CaptureCompositor with
/// a drawing layer and a privacy crop).
/// </summary>
public sealed class SharedRegionRenderingTests
{
    private static Element Effect(Tool tool, Rect frame, double amount)
    {
        var element = new Element(tool, frame);
        element.Style.EffectAmount = amount;
        return element;
    }

    private static RasterImage Noise(int width, int height) => EffectTextures.Image(width, height, (x, y) =>
    {
        var hash = (uint)(x * 73856093) ^ (uint)(y * 19349663);
        hash ^= hash >> 13;
        hash *= 0x5bd1e995;
        return ((byte)hash, (byte)(hash >> 8), (byte)(hash >> 16));
    });

    /// <summary>RenderScene of <paramref name="list"/> over <paramref name="source"/> with an optional drawing layer.</summary>
    private static RasterImage Render(RasterImage source, EffectList list, RasterImage? drawing = null, int drawingLeft = 0, int drawingTop = 0)
    {
        var devices = Warp.Devices;
        using var pipeline = new EffectPipeline(devices);
        var input = EffectTextures.Upload(devices, source);
        var target = EffectTextures.Target(devices, list.Width, list.Height);
        var layer = drawing is null ? 0 : EffectTextures.Upload(devices, drawing);
        try
        {
            SceneLayer? scene = drawing is null ? null : new SceneLayer(layer, drawing.Width, drawing.Height, drawingLeft, drawingTop);
            pipeline.RenderScene(input, target, list, scene);
            return EffectTextures.Read(devices, target, list.Width, list.Height);
        }
        finally
        {
            Marshal.Release(target);
            Marshal.Release(input);
            if (layer != 0)
            {
                Marshal.Release(layer);
            }
        }
    }

    private static RasterImage Crop(RasterImage image, PixelRect area)
    {
        var pixels = new byte[area.Width * area.Height * 4];
        for (var row = 0; row < area.Height; row++)
        {
            image.Pixels.Slice((area.Top + row) * image.Stride + area.Left * 4, area.Width * 4).CopyTo(pixels.AsSpan(row * area.Width * 4));
        }
        return new RasterImage(area.Width, area.Height, area.Width * 4, pixels);
    }

    public static TheoryData<double, double, double, double, double> Regions => new()
    {
        // Inside the display, at its edges and corners, at whole and fractional scales.
        { 1, 30, 20, 70, 50 },
        { 1, 0, 0, 60, 40 },
        { 1, 60, 40, 60, 40 },
        { 1.25, 24.4, 16.2, 50.3, 33.9 },
        { 1.5, 0, 10.5, 80, 30 },
    };

    [Theory]
    [MemberData(nameof(Regions))]
    public void FilteringTheRegionGivesTheWholeDisplaysPixels(double scale, double x, double y, double width, double height)
    {
        int pixelWidth = (int)Math.Round(120 * scale), pixelHeight = (int)Math.Round(80 * scale);
        var source = Noise(pixelWidth, pixelHeight);
        var region = new Rect(x, y, width, height);
        var elements = ElementList.Create(
        [
            // Each effect reaches across the region's edge, so the margin, the crop and the clamped reads all matter.
            Effect(Tool.Blur, new Rect(x - 12, y + 4, 30, 25), 9),
            Effect(Tool.Pixelate, new Rect(x + width - 15, y - 7, 33, 21), 7),
            Effect(Tool.Distort, new Rect(x + 10, y + height - 12, 26, 30), 40),
            Effect(Tool.Blur, new Rect(0, 0, 120, 80), 2.5),
            new Element(Tool.Cover, new Rect(x + width / 2, y + height / 2, 9, 7)),
            // Wholly outside the region: it must change nothing there.
            Effect(Tool.Pixelate, new Rect(x + width + 3, y + height + 3, 10, 10), 30),
        ]);
        var list = EffectList.From(elements, scale, pixelWidth, pixelHeight, privacyRegion: region);
        var privacy = list.Privacy!.Value;
        var area = SharedRegionPresenter.ReadArea(privacy, pixelWidth, pixelHeight);
        var whole = Crop(Render(source, list), privacy);
        var local = Render(Crop(source, area), list.Within(area));
        var shown = Crop(local, new PixelRect(privacy.Left - area.Left, privacy.Top - area.Top, privacy.Right - area.Left, privacy.Bottom - area.Top));
        Assert.True(Pixels.SamePixels(whole, shown), "The region's pixels differ from filtering the whole display.");
    }

    [Fact]
    public void TheReadAreaKeepsAMarginInsideTheDisplay()
    {
        Assert.Equal(new PixelRect(9, 19, 51, 61), SharedRegionPresenter.ReadArea(new PixelRect(10, 20, 50, 60), 100, 100));
        Assert.Equal(new PixelRect(0, 0, 100, 100), SharedRegionPresenter.ReadArea(new PixelRect(0, 0, 100, 100), 100, 100));
        Assert.Equal(new PixelRect(0, 39, 100, 81), SharedRegionPresenter.ReadArea(new PixelRect(0, 40, 100, 80), 100, 100));
        Assert.Equal(new PixelRect(17, 17, 156, 106), SharedRegionPresenter.RegionPixels(new Rect(16.5, 16.5, 140, 90), 1, 192, 128));
    }

    [Fact]
    public void WithinMovesWhatReachesTheAreaAndKeepsItsGeometry()
    {
        var elements = ElementList.Create(
        [
            Effect(Tool.Pixelate, new Rect(5, 5, 30, 30), 8),
            Effect(Tool.Blur, new Rect(80, 80, 10, 10), 8),
            new Element(Tool.Cover, new Rect(20, 0, 30, 12)),
        ]);
        var list = EffectList.From(elements, 1, 100, 100, privacyRegion: new Rect(21, 11, 40, 40));
        var local = list.Within(new PixelRect(20, 10, 62, 52));
        Assert.Equal((42, 42), (local.Width, local.Height));
        var region = Assert.Single(local.Regions.ToArray());
        // Not clipped to the area: a pixelate grid keeps its origin.
        Assert.Equal(new PixelRect(-15, -5, 15, 25), region.Rect);
        Assert.Equal(new PixelRect(0, -10, 30, 2), Assert.Single(local.Covers.ToArray()));
        Assert.Equal(new PixelRect(1, 1, 41, 41), local.Privacy);
        Assert.Throws<ArgumentOutOfRangeException>(() => list.Within(new PixelRect(-1, 0, 10, 10)));
    }

    // Mac SharedRegionDrawingLayer: drawings are composited before the crop, covers and filters.
    [Fact]
    public void DrawingsAreFilteredAndCoveredLikeTheScreen()
    {
        var source = EffectTextures.Image(96, 64, (_, _) => ((byte)128, (byte)128, (byte)128));
        var red = new byte[20 * 20 * 4];
        for (var offset = 0; offset < red.Length; offset += 4)
        {
            red[offset + 2] = 255;
            red[offset + 3] = 255;
        }
        var drawing = new RasterImage(20, 20, 80, red);
        var plain = Render(source, EffectList.From(ElementList.Empty, 1, 96, 64, privacyRegion: new Rect(10, 10, 70, 44)), drawing, 30, 20);
        Assert.Equal((255, 0, 0, 255), plain[40, 30]);
        Assert.Equal((128, 128, 128, 255), plain[20, 20]);
        Assert.Equal((0, 0, 0, 255), plain[5, 5]);
        var elements = ElementList.Create([Effect(Tool.Blur, new Rect(20, 10, 40, 40), 6), new Element(Tool.Cover, new Rect(38, 28, 4, 4))]);
        var filtered = Render(source, EffectList.From(elements, 1, 96, 64, privacyRegion: new Rect(10, 10, 70, 44)), drawing, 30, 20);
        // The blur spreads the drawing's red beyond its edge, and the cover wins over both.
        Assert.True(filtered[27, 30].Red > 140 && filtered[27, 30].Green < 128, $"The blurred drawing shows: {filtered[27, 30]}.");
        Assert.Equal((0, 0, 0, 255), filtered[39, 29]);
        // Outside the crop the drawing never shows.
        var outside = Render(source, EffectList.From(ElementList.Empty, 1, 96, 64, privacyRegion: new Rect(50, 10, 40, 40)), drawing, 30, 20);
        Assert.Equal((0, 0, 0, 255), outside[40, 30]);
    }

    // Mac region.sourceBounds(through: screenViewport): under 2x zoom the region shows a quarter of itself, around the
    // viewport's source, and never more than the display.
    [Fact]
    public void ZoomShowsThePartOfTheDisplayThatTheScreenShowsInTheRegion()
    {
        var region = new SharingRegion(new Rect(100, 50, 400, 300), new Rect(0, 0, 800, 600));
        Assert.Null(SharedRegionPresenter.ZoomedSource(region, null, 1.25, 1000, 750));
        var zoom = new Viewport(new Rect(0, 0, 800, 600), new Rect(200, 150, 400, 300));
        Assert.Equal(new Rect(250, 175, 200, 150), SharedRegionPresenter.ZoomedSource(region, zoom, 1.25, 1000, 750));
        // A source a hair past the display's edge (rounding) stays inside it, so the privacy crop accepts it.
        var edge = new Viewport(new Rect(0, 0, 800, 600), new Rect(400.0000000001, 300, 400, 300));
        var inside = Assert.NotNull(SharedRegionPresenter.ZoomedSource(new SharingRegion(new Rect(400, 300, 400, 300), new Rect(0, 0, 800, 600)), edge, 1.25, 1000, 750));
        Assert.True(inside.MaxX <= 800 && inside.MaxY <= 600, $"{inside}");
        Assert.Equal(new PixelRect(751, 563, 1000, 750), EffectList.PrivacyRect(inside, 1.25, 1000, 750));
    }

    // Mac ViewportProjection: the zoomed part fills the output, each source pixel a block of the same color (nearest).
    [Fact]
    public unsafe void TheZoomedPartFillsTheOutputNearestNeighbor()
    {
        var devices = Warp.Devices;
        var source = Noise(8, 6);
        nint input = EffectTextures.Upload(devices, source);
        nint target = EffectTextures.Target(devices, 8, 6);
        ID2D1DeviceContext* context = null;
        ID2D1Bitmap1* from = null;
        ID2D1Bitmap1* to = null;
        try
        {
            Native.Check(((ID2D1Device*)devices.D2DDevice)->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context), "CreateDeviceContext");
            from = SharedRegionPresenter.Bitmap(context, (ID3D11Texture2D*)input, target: false, "test source");
            to = SharedRegionPresenter.Bitmap(context, (ID3D11Texture2D*)target, target: true, "test target");
            SharedRegionPresenter.Project(context, from, new PixelRect(2, 1, 6, 4), to, 8, 6);
            var shown = EffectTextures.Read(devices, target, 8, 6);
            for (var y = 0; y < 6; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    Assert.Equal(source[2 + x / 2, 1 + y / 2], shown[x, y]);
                }
            }
        }
        finally
        {
            if (to != null)
            {
                to->Release();
            }
            if (from != null)
            {
                from->Release();
            }
            if (context != null)
            {
                context->Release();
            }
            Marshal.Release(target);
            Marshal.Release(input);
        }
    }
}
