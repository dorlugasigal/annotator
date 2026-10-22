// Windows checks for Export PNG... (Mac WorkspaceModel.exportPNG): the annotation-only PNG of a whole display, at the
// display's own scale and size, and the image check that Open board... runs on every pasted image of a board.
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests;

public sealed class BoardExportTests
{
    /// <summary>
    /// A display's size in points is its pixels divided by its scale; the PNG has exactly the display's pixels at every scale
    /// Windows offers, although the product can be a little above a whole number.
    /// </summary>
    [Fact]
    public void DisplayExportsHaveTheDisplaysPixelSizeAtEveryWindowsScale()
    {
        int[][] displays = [[800, 600], [1366, 768], [1600, 900], [1920, 1080], [2560, 1440], [2736, 1824], [3024, 1964], [3840, 2160]];
        double[] scales = [1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 2.75, 3, 3.5, 4];
        foreach (double scale in scales)
        {
            foreach (int[] pixels in displays)
            {
                var size = new Size(pixels[0] / scale, pixels[1] / scale);
                Assert.Equal((pixels[0], pixels[1]), PngExporter.PixelSize(size, scale));
            }
        }
        // A size that really is fractional still rounds up, as on the Mac.
        Assert.Equal((11, 4), PngExporter.PixelSize(new Size(10.2, 3.5), 1));
        Assert.Equal((1601, 900), PngExporter.PixelSize(new Size(1600.01 / 1.75, 900 / 1.75), 1.75));
    }

    /// <summary>
    /// A blackboard's export at 175%: the board's ink color fills every pixel up to the last row, an annotation sits at its
    /// point position times the scale, and a cover is opaque black, so no desktop pixel can be in the file.
    /// </summary>
    [Fact]
    public void BlackboardExportFillsTheDisplayWithTheBoardAndDrawsAnnotationsAtTheScale()
    {
        const double Scale = 1.75;
        var size = new Size(1600 / Scale, 900 / Scale);
        var mark = new Element(Tool.Rectangle, new Rect(100, 100, 80, 60), style: new Style { Color = Palette.White, Width = 4 });
        var cover = new Element(Tool.Cover, new Rect(300, 200, 40, 40));
        using var exporter = Warp.Exporter();
        RasterImage image = RasterAsset.Decode(exporter.Data(ElementList.Create([mark, cover]), size, Scale, Palette.Ink));
        Assert.Equal((1600, 900), (image.Width, image.Height));
        foreach ((int x, int y) in new[] { (0, 0), (1599, 899), (800, 899), (1599, 450), (120, 210) })
        {
            AssertInk(image[x, y]);
        }
        // The rectangle's left edge, 100 points in: 175 pixels.
        Assert.True(image[175, 210].Red > 200, $"The white mark is at 175 pixels, found {image[175, 210]}.");
        Assert.Equal((0, 0, 0, 255), image[(int)(320 * Scale), (int)(220 * Scale)]);
    }

    [Fact]
    public void ImageCheckRefusesWhatDecodingRefuses()
    {
        foreach (var data in new[] { Array.Empty<byte>(), [1, 2, 3], new byte[64] })
        {
            Assert.Equal(RenderFailureKind.InvalidSize, Assert.Throws<RenderFailure>(() => RasterAsset.Check(data)).Kind);
        }
        using var exporter = Warp.Exporter();
        byte[] png = exporter.Data(ElementList.Empty, new Size(8, 8), background: Palette.White);
        RasterAsset.Check(png);
        // A PNG header claiming 20,000 x 8 pixels is over the 16,384-pixel side limit.
        byte[] wide = (byte[])png.Clone();
        wide[16] = 0;
        wide[17] = 0;
        wide[18] = 0x4E;
        wide[19] = 0x20;
        Assert.Equal(RenderFailureKind.InvalidSize, Assert.Throws<RenderFailure>(() => RasterAsset.Check(wide)).Kind);
    }

    /// <summary>The blackboard's opaque ink color, within one level for the 8-bit conversion.</summary>
    private static void AssertInk((byte Red, byte Green, byte Blue, byte Alpha) pixel)
    {
        Assert.Equal(255, pixel.Alpha);
        Assert.InRange(pixel.Red, Level(Palette.Ink.Red) - 1, Level(Palette.Ink.Red) + 1);
        Assert.InRange(pixel.Green, Level(Palette.Ink.Green) - 1, Level(Palette.Ink.Green) + 1);
        Assert.InRange(pixel.Blue, Level(Palette.Ink.Blue) - 1, Level(Palette.Ink.Blue) + 1);
    }

    private static int Level(double component) => (int)Math.Round(component * 255);
}
