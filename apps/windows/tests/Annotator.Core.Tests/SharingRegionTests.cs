// Port of apps/macos/Tests/AnnotationCoreTests/SharingRegionTests.swift.
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class SharingRegionTests
{
    [Fact]
    public void SharingPresetsStayInsideAnOffsetUltrawideDisplay()
    {
        var display = new Rect(-5120, 120, 5120, 1440);
        foreach (var preset in ShareRegionPreset.AllCases)
        {
            var region = new SharingRegion(preset, display);
            Assert.True(display.Contains(region.Bounds));
            Assert.True(region.Bounds.Width >= 32 && region.Bounds.Height >= 32);
        }
        Assert.Equal(new Rect(-5120, 120, 2560, 1440), new SharingRegion(ShareRegionPreset.LeftHalf, display).Bounds);
        Assert.Equal(new Rect(-2560, 840, 2560, 720), new SharingRegion(ShareRegionPreset.BottomRight, display).Bounds);
        var thirds = new[] { ShareRegionPreset.LeftThird, ShareRegionPreset.CenterThird, ShareRegionPreset.RightThird }
            .Select(preset => new SharingRegion(preset, display).Bounds)
            .ToArray();
        Assert.True(Math.Abs(thirds[0].MaxX - thirds[1].X) < 0.000001);
        Assert.True(Math.Abs(thirds[1].MaxX - thirds[2].X) < 0.000001);
        Assert.True(Math.Abs(thirds[2].MaxX - display.MaxX) < 0.000001);
    }

    [Fact]
    public void CustomSharingRegionsRejectInvalidOrOutsideCoordinates()
    {
        var display = new Rect(0, 0, 800, 600);
        Rect[] invalid =
        [
            new(-1, 0, 100, 100),
            new(750, 0, 100, 100),
            new(0, 580, 100, 100),
            new(0, 0, 31, 100),
            new(0, 0, 100, 31),
            new(double.NaN, 0, 100, 100),
            new(0, 0, double.PositiveInfinity, 100),
        ];
        foreach (var bounds in invalid)
        {
            AssertCanvasError(CanvasError.InvalidGeometry, () => _ = new SharingRegion(bounds, display));
        }
        Assert.Equal(800, new SharingRegion(new Rect(768, 568, 32, 32), display).Bounds.MaxX);
    }

    [Fact]
    public void SharingRegionLetterboxesAndUsesTheScreenZoomSource()
    {
        var display = new Rect(0, 0, 1000, 600);
        var region = new SharingRegion(ShareRegionPreset.LeftHalf, display);
        var fit = region.AspectFit(new Rect(10, 20, 800, 600));
        Assert.Equal(new Rect(160, 20, 500, 600), fit);
        var viewport = new Viewport(display, new Rect(200, 100, 500, 300));
        Assert.Equal(new Rect(200, 100, 250, 300), region.SourceBounds(viewport));
        Assert.Equal(region.Bounds, region.SourceBounds(null));
        AssertCanvasError(CanvasError.InvalidGeometry, () => region.AspectFit(Rect.Zero));
    }

    [Fact]
    public void SharingOutputShowsHalfAnUltrawideDisplayBesideTheRegion()
    {
        var display = new Rect(0, 0, 5120, 1440);
        var region = new SharingRegion(ShareRegionPreset.LeftHalf, display);
        var content = region.OutputContentFrame(new Rect(0, 25, 5120, 1415), new FrameInsets(28));
        Assert.Equal(1415 - 2 * 24 - 28, content.Height);
        Assert.True(Math.Abs(content.Width / content.Height - 2560.0 / 1440) < 0.000001);
        Assert.Equal(5120 - 24, content.MaxX);
        Assert.Equal(25 + 24 + 28, content.Y);
        Assert.True(content.X > region.Bounds.MaxX, "The window stays out of the shared half.");
    }

    [Fact]
    public void SharingOutputKeepsTheRegionsOwnSizeWhenItFits()
    {
        var display = new Rect(0, 0, 1920, 1080);
        var region = new SharingRegion(new Rect(100, 100, 800, 600), display);
        Assert.Equal(new Rect(1096, 77, 800, 600), region.OutputContentFrame(new Rect(0, 25, 1920, 1055), new FrameInsets(28)));
    }

    [Fact]
    public void SharingOutputTakesTheCornerThatCoversTheLeastOfTheRegion()
    {
        var laptop = new Rect(0, 0, 1512, 982);
        var right = new SharingRegion(ShareRegionPreset.RightHalf, laptop);
        var besideRight = right.OutputContentFrame(new Rect(0, 38, 1512, 944), new FrameInsets(28));
        Assert.Equal((24.0, 38.0 + 24 + 28), (besideRight.X, besideRight.Y));
        Assert.Equal(944 - 2 * 24 - 28, besideRight.Height);
        Assert.True(besideRight.MaxX < right.Bounds.X);

        var wide = new Rect(0, 0, 1920, 1080);
        var top = new SharingRegion(ShareRegionPreset.TopHalf, wide);
        Assert.Equal(new Rect(24, 529.5, 1872, 526.5), top.OutputContentFrame(new Rect(0, 25, 1920, 1055), new FrameInsets(28)));

        var tie = new SharingRegion(wide, wide).OutputContentFrame(new Rect(0, 25, 1920, 1055), new FrameInsets(28));
        Assert.Equal((1920.0 - 24, 25.0 + 24 + 28), (tie.MaxX, tie.Y));
    }

    [Fact]
    public void SharingOutputHasAMinimumSizeAndChecksItsInputs()
    {
        var display = new Rect(-1440, 0, 1440, 900);
        var small = new SharingRegion(new Rect(-1440, 0, 40, 40), display);
        Assert.Equal(new Rect(-24 - 1 - 320, 24 + 28, 320, 200), small.OutputContentFrame(display, new FrameInsets(28, 1, 1, 1)));
        AssertCanvasError(CanvasError.InvalidGeometry, () => small.OutputContentFrame(Rect.Zero, new FrameInsets(28)));
        AssertCanvasError(CanvasError.InvalidGeometry, () => small.OutputContentFrame(display, new FrameInsets(-1)));
        AssertCanvasError(CanvasError.InvalidGeometry, () => small.OutputContentFrame(display, new FrameInsets(double.NaN)));
    }
}
