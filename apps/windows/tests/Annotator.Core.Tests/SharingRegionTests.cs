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
}
