using Annotator.Core;
using Annotator.Rendering.Export;

namespace Annotator.Rendering.Tests;

/// <summary>The Pointer page preview's halo (Mac CursorPreviewCanvas.draw), drawn with a WARP device.</summary>
public sealed class CursorPreviewRendererTests
{
    [Fact]
    public void DrawsTheHaloAroundThePointAtTheDisplayScale()
    {
        using var renderer = new CursorPreviewRenderer();
        var settings = new CursorHighlightSettings();
        var image = renderer.Render(settings, 200, 200, 1.25, new Point(100, 100), clickProgress: null, click: null, reduceMotion: false);
        Assert.Equal(250, image.Width);
        Assert.Equal(250, image.Height);
        // The default ring: 36 points across, 3 points wide, yellow at full opacity.
        Assert.Equal(0, image[125, 125].Alpha);
        Assert.Equal(255, image[125 + (int)Math.Round(18 * 1.25), 125].Alpha);
        Assert.Equal(0, image[125 + (int)Math.Round(24 * 1.25), 125].Alpha);
        Assert.Equal(0, image[10, 10].Alpha);
        // Off center, the halo follows the point.
        var moved = renderer.Render(settings, 200, 200, 1, new Point(40, 60), clickProgress: null, click: null, reduceMotion: false);
        Assert.Equal(255, moved[40 + 18, 60].Alpha);
        Assert.Equal(0, moved[100 + 18, 100].Alpha);
    }

    [Fact]
    public void ShrinksALargeHaloToFitAndKeepsSmallOnes()
    {
        var small = new CursorHighlightSettings();
        Assert.Equal(1, CursorPreviewRenderer.FitScale(small, 200, 200));
        var large = new CursorHighlightSettings { Diameter = 160, BorderWidth = 12, GlowRadius = 24 };
        // (200 − 24) / (160 · 1.6 + 24 + 96) = 176 / 376.
        Assert.Equal(176.0 / 376, CursorPreviewRenderer.FitScale(large, 200, 200), 1e-12);
        using var renderer = new CursorPreviewRenderer();
        // Without the glow the footprint is 280 points: the 160-point ring shows 80 · 176/280 ≈ 50 points from the middle.
        var image = renderer.Render(large with { GlowRadius = 0 }, 200, 200, 1, new Point(100, 100), clickProgress: null, click: null, reduceMotion: false);
        Assert.True(image[100 + 50, 100].Alpha > 200, $"Alpha {image[150, 100].Alpha}.");
        Assert.Equal(0, image[100 + 70, 100].Alpha);
        Assert.Equal(0, image[100 + 30, 100].Alpha);
    }

    [Fact]
    public void AClickFrameDiffersFromTheRestingHalo()
    {
        using var renderer = new CursorPreviewRenderer();
        var settings = new CursorHighlightSettings { ClickEffect = CursorClickEffect.Ripple };
        var resting = renderer.Render(settings, 200, 200, 1, new Point(100, 100), clickProgress: null, click: null, reduceMotion: false);
        var ripple = renderer.Render(settings, 200, 200, 1, new Point(100, 100), clickProgress: 0.5, click: new Point(100, 100), reduceMotion: false);
        Assert.NotEqual(Convert.ToHexString(resting.Pixels), Convert.ToHexString(ripple.Pixels));
    }
}
