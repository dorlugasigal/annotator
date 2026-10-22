// Port of apps/macos/Tests/CaptureTests/ViewportProjectionTests.swift as geometry. Core Image probes use a bottom-left origin;
// here every point is converted to the top-left origin of Windows images.
using Annotator.Core.Geometry;

namespace Annotator.Core.Tests;

public class ViewportProjectionTests
{
    [Fact]
    public void ZoomProjectionMagnifiesTheRequestedRegionWithNearestSampling()
    {
        var full = new Rect(0, 0, 8, 8);
        var whole = ViewportProjection.ZoomedImage(full, new Size(8, 8), new Viewport(full, full), new Size(8, 8));
        Assert.Equal(new ImageProjection(full, full, ImageInterpolation.NearestNeighbor), whole);
        // The top-left quadrant (red in the Swift fixture) fills the whole target.
        var quadrant = ViewportProjection.ZoomedImage(full, new Size(8, 8), new Viewport(full, new Rect(0, 0, 4, 4)), new Size(8, 8));
        Assert.Equal(new Rect(0, 0, 4, 4), quadrant.Source);
        Assert.Equal(full, quadrant.Destination);
        // Retina capture: 16 by 16 pixels for an 8 by 8 point display.
        var retina = ViewportProjection.ZoomedImage(new Rect(0, 0, 16, 16), new Size(8, 8),
                                                    new Viewport(full, new Rect(4, 2, 4, 4)), new Size(16, 16));
        Assert.Equal(new Rect(8, 4, 8, 8), retina.Source);
        foreach (var size in new[] { new Size(0, 8), new Size(8, double.NaN), new Size(double.PositiveInfinity, 8) })
        {
            var failure = Assert.Throws<RenderFailure>(() => ViewportProjection.ZoomedImage(full, size, new Viewport(full, full), new Size(8, 8)));
            Assert.Equal(RenderFailureKind.InvalidSize, failure.Kind);
            Assert.Throws<RenderFailure>(() => ViewportProjection.ZoomedImage(full, new Size(8, 8), new Viewport(full, full), size));
        }
    }

    [Fact]
    public void ToolbarOverlayUsesDisplayCoordinatesAndRemainsUprightAtEveryScale()
    {
        // The image is blue on top and red at the bottom (Core Image rows 27-37 blue, 17-27 red).
        var overlay = new PixelScreenOverlay(new Rect(13, 17, 40, 20), new Rect(75, 50, 100, 60), new Rect(50, 20, 200, 100));
        foreach (var scale in new[] { 1, 2, 3 })
        {
            var size = new Size(200 * scale, 100 * scale);
            var projected = ViewportProjection.Overlay(overlay, size);
            Assert.Equal(new Rect(25 * scale, 30 * scale, 100 * scale, 60 * scale), projected.Destination);
            Assert.Equal(new Rect(0, 0, 200 * scale, 100 * scale), projected.Clip);
            Assert.Equal(1, projected.Opacity);
            Assert.Equal("red", Sample(projected, new Point(50 * scale, (100 - 15) * scale)));
            Assert.Equal("blue", Sample(projected, new Point(50 * scale, (100 - 65) * scale)));
            Assert.Null(Sample(projected, new Point(10 * scale, (100 - 50) * scale)));
            Assert.Null(Sample(projected, new Point(50 * scale, (100 - 85) * scale)));
        }
    }

    [Fact]
    public void ToolbarOverlayClipsToTheSharedRegionAndPreservesItsOpacity()
    {
        var overlay = new PixelScreenOverlay(new Rect(0, 0, 80, 80), new Rect(20, 20, 80, 80), new Rect(50, 50, 100, 100), 0.25);
        var projected = ViewportProjection.Overlay(overlay, new Size(200, 200));
        Assert.Equal(new Rect(-60, -60, 160, 160), projected.Destination);
        Assert.Equal(new Rect(0, 0, 200, 200), projected.Clip);
        Assert.Equal(0.25, projected.Opacity);
        Assert.InRange(projected.Opacity * 255, 63, 65);
        Assert.True(Visible(projected, new Point(50, 200 - 150)));
        Assert.False(Visible(projected, new Point(150, 200 - 150)));
        Assert.False(Visible(projected, new Point(50, 200 - 50)));
    }

    [Fact]
    public void ToolbarOverlayRejectsInvalidGeometryOpacityAndUnboundedImages()
    {
        var image = new Rect(0, 0, 32, 32);
        var valid = new Rect(0, 0, 100, 100);
        var infinite = new Rect(double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity);
        foreach (var overlay in new[]
        {
            new PixelScreenOverlay(image, Rect.Zero, valid),
            new PixelScreenOverlay(image, valid, Rect.Zero),
            new PixelScreenOverlay(image, new Rect(double.NaN, 0, 20, 20), valid),
            new PixelScreenOverlay(image, valid, valid, double.NaN),
            new PixelScreenOverlay(image, valid, valid, 1.1),
            new PixelScreenOverlay(infinite, valid, valid),
            new PixelScreenOverlay(new Rect(0, 0, 4_000, 4_000), valid, valid),
        })
        {
            var failure = Assert.Throws<RenderFailure>(() => ViewportProjection.Overlay(overlay, new Size(100, 100)));
            Assert.Equal(RenderFailureKind.InvalidSize, failure.Kind);
            Assert.Equal("Export dimensions exceed the supported raster budget.", failure.Message);
        }
    }

    private static bool Visible(OverlayProjection projection, Point pixel) =>
        projection.Destination.Contains(pixel) && projection.Clip.Contains(pixel);

    /// <summary>The image color under a target pixel: rows in the top half are blue, the bottom half red.</summary>
    private static string? Sample(OverlayProjection projection, Point pixel)
    {
        if (!Visible(projection, pixel))
        {
            return null;
        }
        var relative = (pixel.Y - projection.Destination.Y) / projection.Destination.Height;
        return relative < 0.5 ? "blue" : "red";
    }
}
