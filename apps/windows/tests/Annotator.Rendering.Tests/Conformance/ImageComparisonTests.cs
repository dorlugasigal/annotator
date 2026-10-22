using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>The WIN-REND-05 comparison itself: CIEDE2000, the pixel classes and their rules, and the corpus PNG reader.</summary>
public sealed class ImageComparisonTests
{
    // Test pairs from Sharma, Wu and Dalal, "The CIEDE2000 color-difference formula: implementation notes,
    // supplementary test data, and mathematical observations" (2005), table 1.
    [Theory]
    [InlineData(50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425)]
    [InlineData(50.0000, 0.0000, 0.0000, 50.0000, -1.0000, 2.0000, 2.3669)]
    [InlineData(50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0009, 7.1792)]
    [InlineData(50.0000, 2.5000, 0.0000, 73.0000, 25.0000, -18.0000, 27.1492)]
    [InlineData(60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644)]
    [InlineData(22.7233, 20.0904, -46.6940, 23.0331, 14.9730, -42.5619, 2.0373)]
    [InlineData(90.8027, -2.0831, 1.4410, 91.1528, -1.6435, 0.0447, 1.4441)]
    [InlineData(2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082)]
    public void Ciede2000MatchesThePublishedTestData(double l1, double a1, double b1, double l2, double a2, double b2, double expected)
    {
        Assert.Equal(expected, ColorDifference.DeltaE2000(new Lab(l1, a1, b1), new Lab(l2, a2, b2)), 4);
        Assert.Equal(expected, ColorDifference.DeltaE2000(new Lab(l2, a2, b2), new Lab(l1, a1, b1)), 4);
    }

    [Fact]
    public void SrgbWhiteAndBlackMapToTheLabEnds()
    {
        var white = ColorDifference.FromSrgb(255, 255, 255);
        Assert.Equal(100, white.L, 3);
        Assert.Equal(0, white.A, 3);
        Assert.Equal(0, white.B, 3);
        Assert.Equal(0, ColorDifference.FromSrgb(0, 0, 0).L, 6);
    }

    [Fact]
    public void IdenticalImagesPassWithEveryPixelJudged()
    {
        var image = Image(40, 10, (x, _) => x < 20 ? (0, 0, 0, 255) : (0, 0, 0, 0));
        var result = ImageComparison.Compare(image, image);
        Assert.True(result.Passed, result.Summary());
        Assert.Equal(400, result.FlatCount + result.EdgeCount + result.OtherCount);
        Assert.Equal(0, result.DifferentPixels);
    }

    [Fact]
    public void FlatAreasAllowOneStep()
    {
        var reference = Image(20, 20, (_, _) => (100, 50, 25, 255));
        Assert.True(ImageComparison.Compare(reference, Image(20, 20, (_, _) => (101, 49, 25, 255))).Passed);
        var off = ImageComparison.Compare(reference, Image(20, 20, (x, y) => x == 5 && y == 5 ? (102, 50, 25, 255) : (100, 50, 25, 255)));
        Assert.False(off.Passed);
        Assert.Equal(1, off.FlatFailures);
        Assert.Equal(2, off.FlatMaxDifference);
    }

    [Fact]
    public void EdgesMayMoveByOnePixel()
    {
        var reference = Image(40, 10, (x, _) => x < 20 ? (0, 0, 0, 255) : (0, 0, 0, 0));
        var oneOver = Image(40, 10, (x, _) => x < 21 ? (0, 0, 0, 255) : (0, 0, 0, 0));
        var halfOver = Image(40, 10, (x, _) => x < 20 ? (0, 0, 0, 255) : x == 20 ? (0, 0, 0, 128) : (0, 0, 0, 0));
        var twoOver = Image(40, 10, (x, _) => x < 22 ? (0, 0, 0, 255) : (0, 0, 0, 0));
        Assert.True(ImageComparison.Compare(reference, oneOver).Passed);
        Assert.True(ImageComparison.Compare(reference, halfOver).Passed);
        var moved = ImageComparison.Compare(reference, twoOver);
        Assert.False(moved.Passed);
        Assert.True(moved.FlatFailures > 0, moved.Summary());
    }

    [Fact]
    public void ThinFeaturesMayNotDisappear()
    {
        var reference = Image(40, 10, (x, _) => x == 20 ? (255, 0, 0, 255) : (0, 0, 0, 0));
        var missing = ImageComparison.Compare(reference, Image(40, 10, (_, _) => (0, 0, 0, 0)));
        Assert.False(missing.Passed);
        Assert.Equal(10, missing.EdgeFailures);
        Assert.Equal(255, missing.EdgeMaxExcess);
        // The same line a pixel to the right is an edge moved by one pixel.
        Assert.True(ImageComparison.Compare(reference, Image(40, 10, (x, _) => x == 21 ? (255, 0, 0, 255) : (0, 0, 0, 0))).Passed);
        // A lighter line is explained by neither image within one pixel, beyond the edge allowance.
        var lighter = ImageComparison.Compare(reference, Image(40, 10, (x, _) => x == 20 ? (200, 0, 0, 200) : (0, 0, 0, 0)));
        Assert.Equal(55, lighter.EdgeMaxExcess);
        Assert.False(lighter.Passed);
        Assert.True(ImageComparison.Compare(reference, Image(40, 10, (x, _) => x == 20 ? (200, 0, 0, 200) : (0, 0, 0, 0)),
                                            new ComparisonThresholds { EdgeAllowance = 64 }).Passed);
    }

    [Fact]
    public void SmoothAreasAreJudgedByColorDifference()
    {
        // Two gray levels per pixel: neither flat nor an edge.
        var reference = Image(60, 20, (x, _) => (100 + 2 * x, 100 + 2 * x, 100 + 2 * x, 255));
        var close = ImageComparison.Compare(reference, Image(60, 20, (x, _) => (102 + 2 * x, 102 + 2 * x, 102 + 2 * x, 255)));
        Assert.True(close.Passed, close.Summary());
        Assert.True(close.OtherCount > 1000);
        var far = ImageComparison.Compare(reference, Image(60, 20, (x, _) => (106 + 2 * x, 106 + 2 * x, 106 + 2 * x, 255)));
        Assert.False(far.Passed);
        Assert.True(far.OtherShareWithin < 0.995);
    }

    [Fact]
    public void FrozenThresholdsAllowAntialiasingAndRareOutliers()
    {
        var line = Image(40, 60, (x, _) => x == 20 ? (255, 0, 0, 255) : (0, 0, 0, 0));
        var lighter = Image(40, 60, (x, _) => x == 20 ? (230, 0, 0, 230) : (0, 0, 0, 0));
        Assert.False(ImageComparison.Compare(line, lighter, ComparisonThresholds.Initial).Passed);
        Assert.True(ImageComparison.Compare(line, lighter, ComparisonThresholds.Frozen).Passed);
        Assert.False(ImageComparison.Compare(line, Image(40, 60, (x, _) => x == 20 ? (200, 0, 0, 200) : (0, 0, 0, 0)), ComparisonThresholds.Frozen).Passed);
        // 12,000 flat pixels: one may break the rule, two may not.
        var flat = Image(200, 60, (_, _) => (90, 90, 90, 255));
        var one = Image(200, 60, (x, y) => x == 50 && y == 30 ? (95, 90, 90, 255) : (90, 90, 90, 255));
        var two = Image(200, 60, (x, y) => (x == 50 || x == 150) && y == 30 ? (95, 90, 90, 255) : (90, 90, 90, 255));
        Assert.False(ImageComparison.Compare(flat, one, ComparisonThresholds.Initial).Passed);
        Assert.True(ImageComparison.Compare(flat, one, ComparisonThresholds.Frozen).Passed);
        Assert.False(ImageComparison.Compare(flat, two, ComparisonThresholds.Frozen).Passed);
    }

    [Fact]
    public void EffectThresholdsAllowNoChannelMoreThanTwoStepsApart()
    {
        // Smooth gray steps 3/255 lighter: within CIEDE2000 2.0, so WIN-REND-05 as first written passes, the effects do not.
        var reference = Image(60, 20, (x, _) => (100 + 2 * x, 100 + 2 * x, 100 + 2 * x, 255));
        var lighter = Image(60, 20, (x, _) => (103 + 2 * x, 103 + 2 * x, 103 + 2 * x, 255));
        Assert.True(ImageComparison.Compare(reference, lighter, ComparisonThresholds.Initial).Passed);
        var effect = ImageComparison.Compare(reference, lighter, ComparisonThresholds.Effects);
        Assert.False(effect.Passed);
        Assert.False(effect.ChannelsPassed);
        Assert.True(ImageComparison.Compare(reference, Image(60, 20, (x, _) => (102 + 2 * x, 102 + 2 * x, 102 + 2 * x, 255)),
                                            ComparisonThresholds.Effects).Passed);
    }

    [Fact]
    public void TheThresholdsAreTheFrozenWinRend05Values()
    {
        // Frozen on 2026-10-19 from S-13 (spec 013, WIN-REND-05); only an ADR may loosen them.
        var render = ComparisonThresholds.Frozen;
        Assert.Equal((1, 1, 32, 2.0, 0.995, 2.0, 0.0001, (int?)null),
                     (render.FlatTolerance, render.EdgeDistance, render.EdgeAllowance, render.DeltaE, render.OtherShare, render.EdgeContrast,
                      render.OutlierShare, render.MaxChannelDifference));
        var effects = ComparisonThresholds.Effects;
        Assert.Equal((1, 1, 1, 2.0, 0.995, 2.0, 0.0, (int?)2),
                     (effects.FlatTolerance, effects.EdgeDistance, effects.EdgeAllowance, effects.DeltaE, effects.OtherShare, effects.EdgeContrast,
                      effects.OutlierShare, effects.MaxChannelDifference));
    }

    [Fact]
    public void IgnoredPixelsAreLeftOut()
    {
        var reference = Image(20, 20, (_, _) => (0, 0, 0, 0));
        var candidate = Image(20, 20, (x, y) => x >= 5 && x < 10 && y >= 5 && y < 10 ? (255, 255, 255, 255) : (0, 0, 0, 0));
        Assert.False(ImageComparison.Compare(reference, candidate).Passed);
        var ignore = new bool[400];
        for (var y = 4; y < 11; y++)
        {
            for (var x = 4; x < 11; x++)
            {
                ignore[y * 20 + x] = true;
            }
        }
        var result = ImageComparison.Compare(reference, candidate, ignore: ignore);
        Assert.True(result.Passed, result.Summary());
        Assert.Equal(49, result.IgnoredCount);
    }

    [Fact]
    public void PngsReadBackTheExactPremultipliedPixels()
    {
        var image = Image(16, 4, (x, y) => ((x * 16 + y) * 255 / 255 * (y + 1) / 4, x * 3 * (y + 1) / 4, 7 * (y + 1) / 4, 255 * (y + 1) / 4));
        var decoded = PngImages.DecodePremultiplied(RasterAsset.EncodePng(image));
        Assert.True(Pixels.SamePixels(image, decoded));
    }

    /// <summary>A premultiplied image from RGBA values.</summary>
    private static RasterImage Image(int width, int height, Func<int, int, (int Red, int Green, int Blue, int Alpha)> color)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (red, green, blue, alpha) = color(x, y);
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)blue;
                pixels[offset + 1] = (byte)green;
                pixels[offset + 2] = (byte)red;
                pixels[offset + 3] = (byte)alpha;
            }
        }
        return new RasterImage(width, height, width * 4, pixels);
    }
}
