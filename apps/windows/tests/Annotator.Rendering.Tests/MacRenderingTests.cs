// Pixel ports of apps/macos/Tests/RenderingTests/RenderingTests.swift, ArrowHeadSizeTests.swift and EditableStyleRenderingTests.swift:
// the checks the scene-geometry port deferred because they need real pixels. They render on WARP with the Mac's
// tolerances; they are regression checks, not parity evidence (that needs the Mac corpus, plan task M-1).
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests;

public sealed class MacRenderingTests
{
    private static ValueList<Point> Points(params Point[] points) => ValueList.Create<Point>(points);

    [Fact]
    public void ExportsRealPngAndFlattensOpaqueCover()
    {
        var style = new Style { Opacity = 0, Color = new RGBA(1, 0, 0, 0) };
        var cover = new Element(Tool.Cover, new Rect(0, 0, 10, 10), style: style);
        using var exporter = Warp.Exporter();
        var image = exporter.Image(ElementList.Create([cover]), new Size(10, 10));
        Assert.Equal((0, 0, 0, 255), image[0, 0]);
        var png = exporter.Data(ElementList.Create([cover]), new Size(10, 10));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var decoded = RasterAsset.Decode(png);
        Assert.Equal((10, 10), (decoded.Width, decoded.Height));
        Assert.Equal((0, 0, 0, 255), decoded[9, 9]);
    }

    [Fact]
    public void SeededShapesAreStable()
    {
        var element = new Element(Tool.Rectangle, new Rect(20, 20, 100, 80));
        element.Style.Roughness = 4;
        using var exporter = Warp.Exporter();
        var a = exporter.Data(ElementList.Create([element]), new Size(160, 140));
        var b = exporter.Data(ElementList.Create([element]), new Size(160, 140));
        Assert.Equal(a, b);
        var failure = Assert.Throws<RenderFailure>(() => exporter.Data(ElementList.Empty, new Size(100_000, 100_000)));
        Assert.Equal(RenderFailureKind.InvalidSize, failure.Kind);
        Assert.Equal("Export dimensions exceed the supported raster budget.", failure.Message);
        foreach (var size in new[] { new Size(0.5, 10), new Size(10, double.NaN), new Size(16_385, 1), new Size(8_000, 5_001) })
        {
            Assert.Throws<RenderFailure>(() => exporter.Image(ElementList.Empty, size));
        }
        Assert.Throws<RenderFailure>(() => exporter.Image(ElementList.Empty, new Size(10, 10), scale: double.PositiveInfinity));
        // Fractional sizes round up; 16,384 at scale 2 is too wide, 8,192 at scale 2 is not.
        Assert.Equal((11, 4), Size(exporter.Image(ElementList.Empty, new Size(10.2, 3.5))));
        Assert.Throws<RenderFailure>(() => exporter.Image(ElementList.Empty, new Size(16_384, 1), scale: 2));
        Assert.Equal((16_384, 2), Size(exporter.Image(ElementList.Empty, new Size(8_192, 1), scale: 2)));
    }

    [Fact]
    public void BackgroundsFillTheUnroundedPixelSize()
    {
        using var exporter = Warp.Exporter();
        var image = exporter.Image(ElementList.Empty, new Size(10.5, 4), background: Palette.White);
        Assert.Equal((255, 255, 255, 255), image[9, 2]);
        Assert.InRange(image[10, 2].Alpha, 120, 135);
        var scaled = exporter.Image(ElementList.Empty, new Size(10, 4), scale: 2, background: Palette.Blue);
        Assert.Equal((20, 8), (scaled.Width, scaled.Height));
        Assert.Equal(255, scaled[19, 7].Alpha);
    }

    [Fact]
    public void MultipleSelectionDrawsSharedHandlesInBothRenderPaths()
    {
        var first = new Element(Tool.Rectangle, new Rect(30, 70, 50, 40));
        var second = new Element(Tool.Ellipse, new Rect(170, 110, 50, 40));
        var elements = ElementList.Create([first, second]);
        var ids = new HashSet<Guid> { first.Id, second.Id };
        using var exporter = Warp.Exporter();
        var builder = Warp.Builder();
        var scene = builder.Build(elements, ids);
        var withScene = exporter.Render(scene, new Size(260, 200));
        var overlay = exporter.Render(DisplayListFactory.Scene([], builder.Selection(elements, ids)), new Size(260, 200));
        foreach (var image in new[] { withScene, overlay })
        {
            // These handles are on the shared bounds, away from either object's handles.
            foreach (var (x, y) in new[] { (220, 70), (30, 150), (125, 46) })
            {
                Assert.True(image[x, y].Alpha > 0, $"No handle at ({x}, {y}).");
            }
        }
    }

    [Fact]
    public void SelectedArrowShowsPointHandlesWithoutABoundingBox()
    {
        var arrow = new Element(Tool.Arrow, points: Points(new Point(30, 40), new Point(130, 150), new Point(230, 60)));
        var locked = arrow;
        locked.Locked = true;
        using var exporter = Warp.Exporter();
        foreach (var (element, expectsBox) in new[] { (arrow, false), (locked, true) })
        {
            var elements = ElementList.Create([element]);
            var selection = Warp.Builder().Selection(elements, new HashSet<Guid> { element.Id });
            var image = exporter.Render(DisplayListFactory.Scene([], selection), new Size(260, 200));
            // The box would run along y = 37 and x = 233; selection does not draw the arrow itself.
            Assert.Equal(expectsBox, image[100, 37].Alpha > 0);
            Assert.Equal(expectsBox, image[233, 120].Alpha > 0);
            Assert.Equal(!element.Locked, image[130, 150].Alpha > 0);
        }
    }

    [Fact]
    public void EveryArrowheadUsesTheDoubledFootprintAtBothEnds()
    {
        using var exporter = Warp.Exporter();
        foreach (var start in new[] { false, true })
        {
            foreach (var head in new[] { ArrowHead.Open, ArrowHead.Filled, ArrowHead.Circle, ArrowHead.Square, ArrowHead.Bar })
            {
                var style = new Style { Width = 3, StartHead = start ? head : ArrowHead.None, EndHead = start ? ArrowHead.None : head };
                var arrow = new Element(Tool.Arrow, points: Points(new Point(60, 60), new Point(220, 60)), style: style);
                var image = exporter.Image(ElementList.Create([arrow]), new Size(300, 120));
                var tip = start ? 60 : 220;
                var (x, y) = head switch
                {
                    ArrowHead.Open => (start ? 82 : 198, 73),
                    ArrowHead.Filled => (start ? 82 : 198, 68),
                    ArrowHead.Circle or ArrowHead.Square => (tip, 67),
                    _ => (tip, 69),
                };
                Assert.True(image[x, y].Alpha > 0.1 * 255, $"The expanded {head} head must paint beyond its previous footprint.");
            }
        }
    }

    [Fact]
    public void RenderCurvedArrowReadabilityGallery()
    {
        var style = new Style { Width = 6, HeadScale = 1.5, Curved = true };
        Point[][] paths =
        [
            [new Point(310, 40), new Point(110, 130), new Point(65, 320)],
            [new Point(380, 310), new Point(540, 240), new Point(580, 60)],
            [new Point(665, 75), new Point(850, 170), new Point(755, 320)],
        ];
        var arrows = paths.Select(points => new Element(Tool.Arrow, points: Points(points), style: style)).ToArray();
        using var exporter = Warp.Exporter();
        var data = exporter.Data(ElementList.Create(arrows), new Size(920, 370), background: Palette.White);
        Assert.Equal(Annotator.Rendering.Drawing.RenderIssues.None, exporter.Renderer.TakeIssues());
        var image = RasterAsset.Decode(data);
        Assert.Equal((920, 370), (image.Width, image.Height));
        var dark = 0;
        for (var y = 0; y < image.Height; y += 2)
        {
            for (var x = 0; x < image.Width; x += 2)
            {
                if (image[x, y].Green < 200)
                {
                    dark++;
                }
            }
        }
        Assert.True(dark > 500, $"Only {dark} sampled pixels show the arrows.");
        var directory = Path.Combine(AppContext.BaseDirectory, "ArrowReview");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "curved-heads.png"), data);
    }

    [Fact]
    public void DiamondCornerSettingChangesBothExports()
    {
        var frame = new Rect(30, 30, 120, 120);
        var sharp = new Element(Tool.Diamond, frame);
        sharp.Style.Width = 1;
        sharp.Style.Fill = Palette.Blue;
        var rounded = sharp;
        rounded.Style.CornerRadius = 24;
        rounded.InvalidateGeometry();
        var size = new Size(180, 180);
        using var exporter = Warp.Exporter();
        var sharpImage = exporter.Image(ElementList.Create([sharp]), size);
        var roundedImage = exporter.Image(ElementList.Create([rounded]), size);
        var sharpAlpha = sharpImage.AlphaChannel();
        var roundedAlpha = roundedImage.AlphaChannel();
        Assert.NotEqual(sharpAlpha, roundedAlpha);
        Assert.True(sharpAlpha.Count(alpha => alpha > 0) > roundedAlpha.Count(alpha => alpha > 0));
        Assert.NotEqual(exporter.Encode(sharpImage), exporter.Encode(roundedImage));
        var source = exporter.Image(ElementList.Empty, size, background: Palette.White);
        using var snapshots = new SnapshotExporter(Warp.Devices, Warp.Measurer, Annotator.Rendering.Drawing.FixedSystemColors.Light);
        var sharpSnapshot = snapshots.Image(source, ElementList.Create([sharp]));
        var roundedSnapshot = snapshots.Image(source, ElementList.Create([rounded]));
        Assert.NotEqual(exporter.Encode(sharpSnapshot), exporter.Encode(roundedSnapshot));
    }

    [Fact]
    public void TextRenderingMultipliesColorAlphaByObjectOpacity()
    {
        var text = new Element(Tool.Text, new Rect(10, 10, 180, 70), text: "MMMM");
        text.Style.FontSize = 40;
        text.Style.Color = Palette.Blue;
        var size = new Size(200, 100);
        using var exporter = Warp.Exporter();
        var opaque = Pixels.AlphaSum(exporter.Image(ElementList.Create([text]), size));
        Assert.True(opaque > 0);
        foreach (var (colorAlpha, opacity) in new[] { (0.0, 1.0), (0.5, 1.0), (0.5, 0.5), (1.0, 0.5) })
        {
            text.Style.Color = text.Style.Color with { Alpha = colorAlpha };
            text.Style.Opacity = opacity;
            var result = exporter.Image(ElementList.Create([text]), size);
            var ratio = Pixels.AlphaSum(result) / (double)opaque;
            Assert.True(Math.Abs(ratio - colorAlpha * opacity) < 0.02, $"Alpha {colorAlpha} x opacity {opacity} gave ratio {ratio}.");
            if (colorAlpha == 0)
            {
                Assert.Equal(0, Pixels.Painted(result));
            }
        }
    }

    [Fact]
    public void MeasurementOpacityCompositesEveryPaintedPartExactlyOnce()
    {
        var size = new Size(220, 160);
        var counter = new Element(Tool.Counter, new Rect(60, 40, 80, 80), text: "8");
        var ruler = new Element(Tool.Ruler, points: Points(new Point(30, 100), new Point(190, 100)), text: "160 px");
        using var exporter = Warp.Exporter();
        foreach (var original in new[] { counter, ruler })
        {
            var opaque = exporter.Image(ElementList.Create([original]), size).AlphaChannel();
            Assert.Contains((byte)255, opaque);
            foreach (var opacity in new[] { 0.0, 0.5 })
            {
                var translucent = original;
                translucent.Style.Opacity = opacity;
                var result = exporter.Image(ElementList.Create([translucent]), size).AlphaChannel();
                var largest = opaque.Zip(result, (a, b) => Math.Abs(b - a * opacity)).Max();
                Assert.True(largest <= 2, $"{original.Tool} at {opacity}: largest error {largest}.");
                if (opacity == 0)
                {
                    Assert.All(result, alpha => Assert.Equal(0, alpha));
                }
            }
        }
    }

    [Fact]
    public void RulerWidthChangesBothShaftAndEndpointMarks()
    {
        var size = new Size(220, 160);
        var ruler = new Element(Tool.Ruler, points: Points(new Point(30, 100), new Point(190, 100)), text: "160 px");
        ruler.Style.Width = 1;
        using var exporter = Warp.Exporter();
        var thin = exporter.Image(ElementList.Create([ruler]), size).AlphaChannel();
        ruler.Style.Width = 9;
        var thick = exporter.Image(ElementList.Create([ruler]), size).AlphaChannel();
        static long Painted(byte[] alpha, int from, int to)
        {
            long sum = 0;
            for (var row = 0; row < 160; row++)
            {
                for (var x = from; x < to; x++)
                {
                    sum += alpha[row * 220 + x];
                }
            }
            return sum;
        }
        Assert.True(Painted(thick, 80, 140) > Painted(thin, 80, 140));
        Assert.True(Painted(thick, 22, 38) > Painted(thin, 22, 38));
    }

    [Fact]
    public void RoughShapesStrokeTheirPassesAsOneArea()
    {
        var element = new Element(Tool.Rectangle, new Rect(20, 20, 100, 80));
        // A fixed seed keeps the rough outline the same on every run; about 1 random seed in 60 exceeds the pixel rule.
        element.Seed = 42;
        element.Style.Roughness = 3;
        element.Style.Color = element.Style.Color with { Alpha = 0.5 };
        var elements = ElementList.Create([element]);
        using var merged = new PngExporter(new Annotator.Rendering.Drawing.DisplayListRenderer(Warp.Devices, Annotator.Rendering.Drawing.FixedSystemColors.Light),
                                           Warp.Measurer, ownsRenderer: true);
        using var direct = new PngExporter(new Annotator.Rendering.Drawing.DisplayListRenderer(Warp.Devices, Annotator.Rendering.Drawing.FixedSystemColors.Light,
                                                                                                new Annotator.Rendering.Drawing.RendererOptions { FillOverlappingStrokes = false }),
                                           Warp.Measurer, ownsRenderer: true);
        var area = merged.Image(elements, new Size(140, 120));
        var stroked = direct.Image(elements, new Size(140, 120));
        Assert.True(merged.Renderer.Counters.WidenedStrokes > 0);
        Assert.Equal(0, direct.Renderer.Counters.WidenedStrokes);
        // Like one Core Graphics stroke: where the two passes overlap, the alpha is one pass's, not more.
        Assert.InRange(area.AlphaChannel().Max(), (byte)126, (byte)129);
        var result = Conformance.ImageComparison.Compare(stroked, area, Conformance.ComparisonThresholds.Frozen);
        Assert.True(result.Passed, result.Summary());
    }

    [Fact]
    public void StrokesMergedOnWorkerThreadsMatchStrokesMergedInPlace()
    {
        Tool[] tools = [Tool.Rectangle, Tool.Ellipse, Tool.Diamond];
        var shapes = new Element[8];
        for (var index = 0; index < shapes.Length; index++)
        {
            shapes[index] = new Element(tools[index % tools.Length], new Rect(10 + index % 4 * 60, 10 + index / 4 * 60, 50, 44));
            shapes[index].Style.Roughness = 1 + index % 3;
        }
        var scene = Warp.Builder().Build(ElementList.Create(shapes));
        (RasterImage Image, long Merged) Draw(bool together)
        {
            using var exporter = Warp.Exporter();
            var renderer = exporter.Renderer;
            foreach (var list in scene.Elements)
            {
                renderer.Enter(list);
            }
            var area = OffscreenCanvas.Area(500, 260, 2);
            RasterImage image;
            unsafe
            {
                image = exporter.Canvas.Render(500, 260, context =>
                {
                    // One draw of every list merges its strokes on worker threads; one draw per list merges in place.
                    if (together)
                    {
                        renderer.Draw((nint)context, scene.Elements, area);
                    }
                    else
                    {
                        foreach (var list in scene.Elements)
                        {
                            renderer.Draw((nint)context, list, area);
                        }
                    }
                });
            }
            return (image, renderer.Counters.WidenedStrokes);
        }
        var parallel = Draw(together: true);
        var inPlace = Draw(together: false);
        Assert.True(parallel.Merged >= shapes.Length, $"{parallel.Merged} strokes merged");
        Assert.Equal(inPlace.Merged, parallel.Merged);
        Assert.True(parallel.Image.Pixels.SequenceEqual(inPlace.Image.Pixels));
    }

    private static (int Width, int Height) Size(RasterImage image) => (image.Width, image.Height);
}
