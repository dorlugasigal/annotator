// Ports of apps/macos/Tests/CaptureTests/CaptureUtilityTests.swift, ImageCensorTests.swift (censorsApplyAfterPastedImageLayers) and
// CensorSamplingTests.swift for SnapshotExporter, whose covers and pixel effects run through the effect pipeline as the
// Mac's CaptureCompositor does.
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests;

public sealed class SnapshotExportTests
{
    private static SnapshotExporter Exporter() => new(Warp.Devices, Warp.Measurer, FixedSystemColors.Light);

    /// <summary>A 64 x 64 checkerboard of 2 x 2 black and white squares.</summary>
    private static RasterImage Checker()
    {
        var pixels = new byte[64 * 64 * 4];
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var white = (x / 2 + y / 2) % 2 == 0 ? (byte)255 : (byte)0;
                var offset = (y * 64 + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = white;
                pixels[offset + 3] = 255;
            }
        }
        return new RasterImage(64, 64, 256, pixels);
    }

    private static RasterImage Solid(int width, int height, RGBA color)
    {
        using var exporter = Warp.Exporter();
        return exporter.Image(ElementList.Empty, new Size(width, height), background: color);
    }

    [Fact]
    public void CropAndBackdropProduceExactExportDimensions()
    {
        using var exporter = Exporter();
        var source = Checker();
        var crop = new Rect(8, 12, 24, 16);
        var scaled = exporter.Image(source, ElementList.Empty, crop, new ExportBackdrop { Scale = 2 });
        Assert.Equal((48, 32), (scaled.Width, scaled.Height));
        var framed = exporter.Image(source, ElementList.Empty, crop, new ExportBackdrop { Kind = ExportBackdropKind.Solid, Padding = 12 });
        Assert.Equal((48, 40), (framed.Width, framed.Height));
        Assert.Throws<RenderFailure>(() => exporter.Image(source, ElementList.Empty, new Rect(60, 60, 20, 20)));
        foreach (var backdrop in new[]
                 {
                     new ExportBackdrop { Padding = 257 }, new ExportBackdrop { Radius = -1 }, new ExportBackdrop { Scale = 0.2 },
                     new ExportBackdrop { Scale = double.NaN }, new ExportBackdrop { Color = new RGBA(2, 0, 0) },
                 })
        {
            Assert.Throws<RenderFailure>(() => exporter.Image(source, ElementList.Empty, backdrop: backdrop));
        }
        Assert.Throws<RenderFailure>(() => exporter.Image(source, ElementList.Empty, new Rect(0, 0, 0.5, 10)));
    }

    [Fact]
    public void UncroppedSnapshotsKeepEverySourcePixel()
    {
        using var exporter = Exporter();
        var source = Checker();
        var copy = exporter.Image(source, ElementList.Empty);
        Assert.True(Pixels.SamePixels(source, copy));
    }

    [Fact]
    public void BackdropsFrameTheCropWithRoundedCornersAndAGradient()
    {
        using var exporter = Exporter();
        var source = Solid(40, 40, Palette.White);
        var backdrop = new ExportBackdrop { Kind = ExportBackdropKind.Gradient, Padding = 10, Radius = 12 };
        var image = exporter.Image(source, ElementList.Empty, backdrop: backdrop);
        Assert.Equal((60, 60), (image.Width, image.Height));
        // Inside the frame the source shows; its rounded corner shows the backdrop; the backdrop runs from the
        // backdrop color at the top left to purple at the bottom right.
        Assert.Equal((255, 255, 255, 255), image[30, 30]);
        Assert.NotEqual((255, 255, 255, 255), image[11, 11]);
        var start = image[0, 0];
        var end = image[59, 59];
        Assert.InRange(start.Red, 40, 52);
        Assert.InRange(end.Red, 145, 155);
        Assert.Equal(255, start.Alpha);
    }

    [Fact]
    public void OpaqueCoverWinsOverEffects()
    {
        using var exporter = Exporter();
        var source = Checker();
        var cover = new Element(Tool.Cover, new Rect(16, 16, 32, 32));
        cover.Style.Opacity = 0;
        var blur = new Element(Tool.Blur, cover.Frame);
        var image = exporter.FlattenedSource(source, ElementList.Create([cover, blur]));
        Assert.Equal((0, 0, 0, 255), image[32, 32]);
        Assert.Equal((0, 0, 0, 255), image[16, 16]);
        Assert.Equal(source[0, 0], image[0, 0]);
        Assert.Equal(SnapshotIssues.None, exporter.LastIssues);
        Assert.Same(source, exporter.FlattenedSource(source, ElementList.Empty));
    }

    // Mac CaptureUtilityTests.visualEffectsChangeOnlySelectedPixels and BlurLevelTests.zeroStrengthBypassesFilters.
    [Fact]
    public void EffectsChangeOnlyTheirRoundedOutRegion()
    {
        using var exporter = Exporter();
        var source = Checker();
        foreach (var tool in new[] { Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            var effect = new Element(tool, new Rect(16.4, 16, 32, 31.5));
            effect.Style.EffectAmount = 25;
            var result = exporter.FlattenedSource(source, ElementList.Create([effect]));
            Assert.False(Pixels.SamePixels(source, result), $"{tool} changed nothing.");
            // The region rounds outward to whole pixels: (16, 16) to (49, 48). Nothing outside it changes, and it is
            // filtered, not black.
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    if (x < 16 || x >= 49 || y < 16 || y >= 48)
                    {
                        Assert.Equal(source[x, y], result[x, y]);
                    }
                }
            }
            var shown = false;
            for (var y = 16; y < 48 && !shown; y++)
            {
                for (var x = 16; x < 49 && !shown; x++)
                {
                    shown = result[x, y] != (0, 0, 0, 255);
                }
            }
            Assert.True(shown, $"{tool} drew its region black.");
            Assert.Equal(SnapshotIssues.None, exporter.LastIssues);
            effect.Style.EffectAmount = 0;
            Assert.True(Pixels.SamePixels(source, exporter.FlattenedSource(source, ElementList.Create([effect]))), "Strength zero leaves pixels unchanged.");
        }
    }

    [Fact]
    public void CensorsApplyAfterPastedImageLayers()
    {
        using var exporter = Exporter();
        using var png = Warp.Exporter();
        var background = Solid(64, 64, Palette.White);
        var art = png.Image(ElementList.Empty, new Size(32, 32), background: Palette.Coral);
        var image = new Element(Tool.Image, new Rect(16, 16, 32, 32)) { ImagePng = ValueList.Create<byte>(png.Encode(art)) };
        var cover = new Element(Tool.Cover, image.Frame);
        cover.Style.Opacity = 0;
        var rendered = exporter.Image(background, ElementList.Create([cover, image]));
        Assert.Equal((0, 0, 0, 255), rendered[32, 32]);
        Assert.Equal((255, 255, 255, 255), rendered[4, 4]);
        // Without the cover the pasted image shows.
        var uncovered = exporter.Image(background, ElementList.Create([image]));
        var (red, _, blue, _) = uncovered[32, 32];
        Assert.True(red > 200 && blue < 100, $"Expected coral, got red {red} and blue {blue}.");
    }

    [Fact]
    public void FiltersCannotSampleSecretsOutOfASolidCensor()
    {
        RasterImage Source(RGBA secret)
        {
            var pixels = new byte[64 * 64 * 4];
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    var inside = x >= 24 && x < 40 && y >= 24 && y < 40;
                    var color = inside ? secret : Palette.White;
                    var offset = (y * 64 + x) * 4;
                    pixels[offset] = (byte)(color.Blue * 255);
                    pixels[offset + 1] = (byte)(color.Green * 255);
                    pixels[offset + 2] = (byte)(color.Red * 255);
                    pixels[offset + 3] = 255;
                }
            }
            return new RasterImage(64, 64, 256, pixels);
        }
        using var exporter = Exporter();
        var first = Source(new RGBA(1, 0, 0));
        var second = Source(new RGBA(0, 0, 1));
        var cover = new Element(Tool.Cover, new Rect(24, 24, 16, 16));
        foreach (var tool in new[] { Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            var effect = new Element(tool, new Rect(8, 8, 48, 48));
            effect.Style.EffectAmount = 40;
            var a = RasterAsset.EncodePng(exporter.Image(first, ElementList.Create([cover, effect])));
            var b = RasterAsset.EncodePng(exporter.Image(second, ElementList.Create([cover, effect])));
            Assert.Equal(a, b);
        }
    }
}
