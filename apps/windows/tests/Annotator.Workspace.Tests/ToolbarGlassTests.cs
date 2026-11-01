using Annotator.Core;
using Annotator.Workspace.Toolbar;

namespace Annotator.Workspace.Tests;

/// <summary>
/// Glass toolbar's solid fallback (WIN-UI-09): <see cref="ToolbarGlass"/> shows the glass only while it is chosen and Windows
/// allows transparency, as the Mac's toolbar turns solid with Reduce transparency.
/// </summary>
public sealed class ToolbarGlassTests
{
    private static readonly SystemTransparency Allowed = new(Effects: true, ContrastTheme: false, EnergySaver: false);

    [Fact]
    public void GlassShowsOnlyOnceChosen()
    {
        Assert.True(Allowed.Allows);
        Assert.True(ToolbarGlass.Shows(new WorkspacePreferences { Glass = true }, Allowed));
        Assert.False(ToolbarGlass.Shows(new WorkspacePreferences(), Allowed));
        Assert.False(ToolbarGlass.Shows(new WorkspacePreferences { Glass = false }, Allowed));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void TheToolbarStaysSolidWhileWindowsTurnsTransparencyOff(bool effects, bool contrastTheme, bool energySaver, bool supported)
    {
        var system = new SystemTransparency(effects, contrastTheme, energySaver, supported);
        Assert.False(system.Allows);
        Assert.False(ToolbarGlass.Shows(new WorkspacePreferences { Glass = true }, system));
    }

    [Fact]
    public void ASnapshotGetsTheTintUnderItsGlassOnly()
    {
        const int width = 40, height = 20;
        var pixels = new byte[width * height * 4];
        // An opaque glyph pixel keeps its color: the tint goes under the snapshot's pixels.
        pixels[((10 * width) + 20) * 4 + 0] = 10;
        pixels[((10 * width) + 20) * 4 + 1] = 20;
        pixels[((10 * width) + 20) * 4 + 2] = 30;
        pixels[((10 * width) + 20) * 4 + 3] = 255;

        ToolbarGlass.TintUnder(pixels, width, height, new Rect(0, 0, 40, 20), radius: 10, new RGBA(1, 1, 1, 0.5));

        // Inside the capsule: white at half opacity, premultiplied.
        Assert.Equal(new byte[] { 128, 128, 128, 128 }, Pixel(pixels, width, 20, 5));
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, Pixel(pixels, width, 20, 10));
        // The rounded corners stay clear.
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, width, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, width, 39, 19));
    }

    [Fact]
    public void TheGlassEdgeIsSmoothAndNothingOutsideChanges()
    {
        const int width = 24, height = 10;
        var pixels = new byte[width * height * 4];

        ToolbarGlass.TintUnder(pixels, width, height, new Rect(0.5, 0, 20, 10), radius: 0, new RGBA(0, 0, 0, 1));

        // The left column is half inside the surface; the columns past its right edge are outside.
        Assert.Equal(128, Pixel(pixels, width, 0, 5)[3]);
        Assert.Equal(255, Pixel(pixels, width, 10, 5)[3]);
        Assert.Equal(128, Pixel(pixels, width, 20, 5)[3]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(pixels, width, 22, 5));
    }

    private static byte[] Pixel(byte[] pixels, int width, int x, int y) => pixels[((y * width + x) * 4)..((y * width + x) * 4 + 4)];
}
