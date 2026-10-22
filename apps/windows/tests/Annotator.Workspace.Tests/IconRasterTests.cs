using Annotator.Core;
using Annotator.Workspace.Artwork;

namespace Annotator.Workspace.Tests;

/// <summary>App icons drawn into pixels for the tray icon's native menu (WIN-UI-01).</summary>
public sealed class IconRasterTests
{
    private static readonly RGBA Black = new(0, 0, 0);

    private static byte Alpha(byte[] pixels, int size, int x, int y) => pixels[(y * size + x) * 4 + 3];

    [Fact]
    public void ALineIsTwoUnitsWideWithRoundEnds()
    {
        byte[] pixels = IconRaster.RenderBgra([new IconRaster.Part("M 4,12 L 20,12", false)], 24, Black);
        Assert.Equal(24 * 24 * 4, pixels.Length);
        Assert.Equal(255, Alpha(pixels, 24, 12, 11));
        Assert.Equal(255, Alpha(pixels, 24, 12, 12));
        Assert.Equal(0, Alpha(pixels, 24, 12, 9));
        Assert.Equal(0, Alpha(pixels, 24, 12, 14));
        // The round cap reaches a unit past the end, and not to its corners.
        Assert.InRange(Alpha(pixels, 24, 3, 11), 150, 254);
        Assert.Equal(0, Alpha(pixels, 24, 2, 11));
        Assert.InRange(Alpha(pixels, 24, 20, 11), 150, 254);
        Assert.Equal(0, Alpha(pixels, 24, 21, 10));
    }

    [Fact]
    public void AFilledPartIsFilledAndAnOutlineIsNot()
    {
        const string square = "M 6,6 L 18,6 L 18,18 L 6,18 Z";
        byte[] filled = IconRaster.RenderBgra([new IconRaster.Part(square, true)], 24, Black);
        byte[] outline = IconRaster.RenderBgra([new IconRaster.Part(square, false)], 24, Black);
        Assert.Equal(255, Alpha(filled, 24, 12, 12));
        Assert.Equal(0, Alpha(outline, 24, 12, 12));
        Assert.Equal(255, Alpha(outline, 24, 5, 12));
        Assert.Equal(0, Alpha(outline, 24, 3, 12));
    }

    [Fact]
    public void ArcsFollowTheCircle()
    {
        byte[] pixels = IconRaster.RenderBgra(
            [new IconRaster.Part("M 2,12 A 10,10 0 1 0 22,12 A 10,10 0 1 0 2,12", false)], 48, Black);
        // A circle of radius 20 pixels around (24, 24).
        foreach ((int x, int y) in new[] { (24, 4), (43, 24), (24, 43), (4, 24), (38, 10), (10, 38) })
        {
            Assert.True(Alpha(pixels, 48, x, y) > 200, $"({x}, {y}) is on the circle");
        }
        Assert.Equal(0, Alpha(pixels, 48, 24, 24));
        Assert.Equal(0, Alpha(pixels, 48, 24, 8));
    }

    [Fact]
    public void ColorsArePremultiplied()
    {
        byte[] pixels = IconRaster.RenderBgra([new IconRaster.Part("M 4,12 L 20,12", false)], 24, new RGBA(1, 0.5, 0, 0.5));
        int index = (12 * 24 + 12) * 4;
        Assert.Equal((0, 64, 128, 128), (pixels[index], pixels[index + 1], pixels[index + 2], pixels[index + 3]));
    }

    [Fact]
    public void ScalesWithTheSize()
    {
        byte[] small = IconRaster.RenderBgra([new IconRaster.Part("M 12,4 L 12,20", false)], 16, Black);
        byte[] large = IconRaster.RenderBgra([new IconRaster.Part("M 12,4 L 12,20", false)], 32, Black);
        // At 16 pixels the 1.33-pixel line straddles two columns; at 32 it covers two and a third.
        Assert.InRange(Alpha(small, 16, 7, 8), 150, 190);
        Assert.InRange(Alpha(small, 16, 8, 8), 150, 190);
        Assert.Equal(0, Alpha(small, 16, 9, 8));
        Assert.Equal(255, Alpha(large, 32, 15, 16));
        Assert.Equal(255, Alpha(large, 32, 16, 16));
        Assert.Equal(0, Alpha(large, 32, 18, 16));
    }
}
