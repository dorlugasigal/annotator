using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Text;

namespace Annotator.Rendering.Tests;

/// <summary>The DirectWrite measurer: the D-4 font rule, the AppKit metric mapping and the boundingRect rules.</summary>
public sealed class TextMeasurerTests
{
    private static readonly TextFont Body = new(FontDesign.System, FontWeight.Regular, 28);

    [Fact]
    public void SystemFontsFollowWindowsOpticalSizesAndFallBackSafely()
    {
        var measurer = Warp.Measurer;
        var variable = measurer.FamilyName(FontDesign.System, 16) != "Segoe UI";
        if (variable)
        {
            Assert.Equal("Segoe UI Variable Small", measurer.FamilyName(FontDesign.System, 12));
            Assert.Equal("Segoe UI Variable Text", measurer.FamilyName(FontDesign.System, 14));
            Assert.Equal("Segoe UI Variable Text", measurer.FamilyName(FontDesign.System, 19.5));
            Assert.Equal("Segoe UI Variable Display", measurer.FamilyName(FontDesign.System, 20));
            Assert.Equal("Segoe UI Variable Display", measurer.FamilyName(FontDesign.System, 300));
        }
        else
        {
            Assert.Equal("Segoe UI", measurer.FamilyName(FontDesign.System, 28));
        }
        Assert.Contains(measurer.FamilyName(FontDesign.Monospace, 28), new[] { "Cascadia Mono", "Consolas", "Courier New" });
    }

    [Fact]
    public void HandwritingTextUsesTheBundledCaveatFont()
    {
        var measurer = Warp.Measurer;
        Assert.Equal("Caveat", measurer.FamilyName(FontDesign.Handwriting, 28));
        // Caveat's own metrics, the same as on the Mac: 960 and 300 of 1000 units, with no line gap.
        var regular = new TextFont(FontDesign.Handwriting, FontWeight.Regular, 100);
        var metrics = measurer.LineMetrics(regular);
        Assert.Equal(96, metrics.Ascent, 3);
        Assert.Equal(30, metrics.Descent, 3);
        Assert.Equal(0, metrics.Leading, 3);
        // The Mac measures "why the dip?" 423.8 points wide in Regular and 426.8 in weight 600. Caveat's Bold (700), which the
        // weight classes would pick instead of the weight axis, is 428.3 wide.
        Assert.InRange(measurer.Measure("why the dip?", regular).Width, 422.8, 424.8);
        Assert.InRange(measurer.Measure("why the dip?", regular with { Weight = FontWeight.Semibold }).Width, 425.8, 427.6);
        Assert.NotEqual(measurer.Measure("why the dip?", Body with { Size = 100 }).Width, measurer.Measure("why the dip?", regular).Width);
    }

    [Fact]
    public void LineMetricsAreTheFontsAscentDescentAndLineGapInPoints()
    {
        var measurer = Warp.Measurer;
        var metrics = measurer.LineMetrics(Body);
        Assert.True(metrics.Ascent > 0 && metrics.Descent > 0 && metrics.Leading >= 0);
        if (measurer.FamilyName(FontDesign.System, 28).StartsWith("Segoe UI", StringComparison.Ordinal))
        {
            // Segoe UI and Segoe UI Variable: ascent 2210, descent 514 and line gap 0 in 2048 units per em.
            Assert.Equal(28 * 2210 / 2048.0, metrics.Ascent, 6);
            Assert.Equal(28 * 514 / 2048.0, metrics.Descent, 6);
            Assert.Equal(0, metrics.Leading);
        }
        var doubled = measurer.LineMetrics(Body with { Size = 56 });
        Assert.Equal(metrics.Ascent * 2, doubled.Ascent, 6);
    }

    [Fact]
    public void MeasuringFollowsAppKitBoundingRectRules()
    {
        var measurer = Warp.Measurer;
        var metrics = measurer.LineMetrics(Body);
        var line = metrics.Ascent + metrics.Descent + metrics.Leading;
        var one = measurer.Measure("Ag", Body);
        Assert.True(one.Width > 20 && one.Width < 60, $"Width {one.Width}.");
        Assert.Equal(line, one.Height, 3);
        Assert.Equal(line * 2, measurer.Measure("Ag\nAg", Body).Height, 3);
        // A final line break adds no line; Core's ContentSize adds it separately, like the Mac.
        Assert.Equal(one.Height, measurer.Measure("Ag\n", Body).Height, 3);
        Assert.Equal(one.Height, measurer.Measure("\n", Body).Height, 3);
        Assert.Equal(line * 2, measurer.Measure("\n\n", Body).Height, 3);
        // Trailing spaces count, as in AppKit's used rect.
        Assert.True(measurer.Measure("Ag  ", Body).Width > one.Width);
        Assert.True(measurer.Measure(" ", Body).Width > 0);
        Assert.Equal(one, measurer.Measure("Ag", Body));
        var bold = measurer.Measure("Annotator", Body with { Weight = FontWeight.Semibold });
        Assert.True(bold.Width > measurer.Measure("Annotator", Body).Width);
        var mono = new TextFont(FontDesign.Monospace, FontWeight.Regular, 20);
        Assert.Equal(measurer.Measure("iiii", mono).Width, measurer.Measure("MMMM", mono).Width, 3);
    }

    [Fact]
    public void ContentSizesUseTheMeasurerAndRoundLikeTheMac()
    {
        var measurer = Warp.Measurer;
        var size = TextLayout.ContentSize("Hello", Body, measurer);
        Assert.Equal(Math.Ceiling(measurer.Measure("Hello", Body).Width) + 2, size.Width);
        Assert.Equal(Math.Ceiling(measurer.Measure("Hello", Body).Height) + 2, size.Height);
        Assert.Equal(32, TextLayout.ContentSize("", Body, measurer).Width);
        var trailing = TextLayout.ContentSize("Hello\n", Body, measurer);
        Assert.True(trailing.Height > size.Height + 30);
    }

    [Fact]
    public void MeasuringIsSafeFromSeveralThreads()
    {
        var measurer = Warp.Measurer;
        var expected = measurer.Measure("Parallel text 42", Body);
        Parallel.For(0, 64, index =>
        {
            Assert.Equal(expected, measurer.Measure("Parallel text 42", Body));
            _ = measurer.Measure($"Line {index}", new TextFont(FontDesign.System, FontWeight.Regular, 10 + index));
        });
    }
}
