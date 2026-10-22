// Port of the label assertions in apps/macos/Tests/RenderingTests/SystemShapeAndLabelTests.swift and GradientRenderingTests.swift,
// plus the NativeTextFont content-size rules (WIN-REND-03).
using Annotator.Core.Geometry;

namespace Annotator.Core.Tests;

public class TextLayoutTests
{
    private sealed class FixedMeasurer(Size size, FontLineMetrics metrics) : ITextMeasurer
    {
        public string? LastText { get; private set; }

        public Size Measure(string text, TextFont font)
        {
            LastText = text;
            return size;
        }

        public FontLineMetrics LineMetrics(TextFont font) => metrics;
    }

    [Fact]
    public void ContentSizeRoundsUpAddsTwoPointsAndKeepsOneLine()
    {
        var font = new TextFont(FontDesign.System, FontWeight.Regular, 20);
        var measurer = new FixedMeasurer(new Size(100.2, 24.1), new FontLineMetrics(19.1, 4.6, 0.5));
        Assert.Equal(new Size(103, 27), TextLayout.ContentSize("Hello", font, measurer));
        var narrow = new FixedMeasurer(new Size(3, 2), new FontLineMetrics(19.1, 4.6, 0.5));
        Assert.Equal(new Size(32, 24), TextLayout.ContentSize("i", font, narrow));
        Assert.Equal(new Size(32, 24), TextLayout.ContentSize("", font, narrow));
        Assert.Equal(" ", narrow.LastText);
    }

    [Fact]
    public void ATrailingLineFeedAddsALineButCarriageReturnLineFeedIsOneCharacter()
    {
        var font = new TextFont(FontDesign.System, FontWeight.Regular, 20);
        var measurer = new FixedMeasurer(new Size(50, 24), new FontLineMetrics(19, 5, 1));
        Assert.Equal(26, TextLayout.ContentSize("Line", font, measurer).Height);
        Assert.Equal(Math.Ceiling(24 + 25.0) + 2, TextLayout.ContentSize("Line\n", font, measurer).Height);
        Assert.Equal(26, TextLayout.ContentSize("Line\r\n", font, measurer).Height);
        Assert.True(TextLayout.EndsWithLineFeed("\n"));
        Assert.False(TextLayout.EndsWithLineFeed("a\r\n"));
        Assert.False(TextLayout.EndsWithLineFeed(""));
    }

    [Fact]
    public void FontsResolveDesignWeightAndScale()
    {
        var style = new Style { FontSize = 18, Bold = true, FontName = "monospace" };
        Assert.Equal(new TextFont(FontDesign.Monospace, FontWeight.Semibold, 36), TextFont.Resolve(style, scale: 2));
        style.Bold = false;
        style.FontName = "system";
        Assert.Equal(new TextFont(FontDesign.System, FontWeight.Regular, 18), TextFont.Resolve(style));
        style.FontName = "Monospace";
        Assert.Equal(FontDesign.System, TextFont.Resolve(style).Design);
    }

    [Fact]
    public void LabelGeometryRemainsCenteredAndFollowsOwnerOpacity()
    {
        var measurer = new FakeTextMeasurer();
        foreach (var tool in new[] { Tool.Rectangle, Tool.Ellipse, Tool.Database, Tool.DatabaseStack, Tool.Server })
        {
            var shape = new Element(tool, new Rect(50, 70, 200, 140));
            shape.Style.Opacity = 0.5;
            shape.Label = new ShapeLabel("Orders");
            shape.Rotation = 0.6;
            var label = Assert.NotNull(ShapeLabelLayout.TextElement(shape, measurer));
            Assert.Equal(shape.Frame.Center, label.Frame.Center);
            Assert.Equal(shape.Rotation, label.Rotation);
            Assert.Equal(0.5, label.Style.Opacity);
            Assert.Equal(shape.Id, label.Id);
            Assert.Equal(Tool.Text, label.Tool);
            Assert.Equal("Orders", label.Text);
        }
        var text = new Element(Tool.Text, new Rect(0, 0, 10, 10), text: "x") { Label = null };
        Assert.Null(ShapeLabelLayout.TextElement(text, measurer, new ShapeLabel("Label")));
        Assert.Null(ShapeLabelLayout.TextElement(new Element(Tool.Rectangle, new Rect(0, 0, 1, 1)), measurer));
    }

    [Fact]
    public void LabelsThatFollowTheStrokeShareTheOwnersGradient()
    {
        var shape = new Element(Tool.Rectangle, new Rect(20, 20, 440, 100));
        shape.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        shape.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: true);
        shape.Label = new ShapeLabel("Make your point", followsStroke: true);
        var label = Assert.NotNull(ShapeLabelLayout.TextElement(shape, new FakeTextMeasurer()));
        Assert.Equal(shape.Style.StrokeGradient, label.Style.StrokeGradient);
        Assert.Equal(shape.Style.Color, label.Style.Color);
    }

    [Fact]
    public void FittedOwnerFramesLeaveTwelvePointsAroundTheLabel()
    {
        var measurer = new FixedMeasurer(new Size(230, 30), new FontLineMetrics(19, 5, 0));
        var shape = new Element(Tool.Rectangle, new Rect(0, 0, 128, 128));
        var fitted = ShapeLabelLayout.FittedOwnerFrame(shape, new ShapeLabel("Wide label"), measurer);
        Assert.Equal(new Rect(-64, 0, 256, 128), fitted);
        var symbol = new Element(Tool.Server, new Rect(0, 0, 128, 128));
        var scaled = ShapeLabelLayout.FittedOwnerFrame(symbol, new ShapeLabel("Wide label"), measurer);
        Assert.Equal(new Rect(-64, -64, 256, 256), scaled);
        Assert.Equal(symbol.Frame, ShapeLabelLayout.FittedOwnerFrame(symbol with { Tool = Tool.Text }, new ShapeLabel("x"), measurer));
    }

    [Fact]
    public void TheMacBaselineIsOneSfAscenderBelowTheFrameTop()
    {
        // geometry/text.jsonl: SF Pro and SF Mono at 28 points have an ascender of 27.0703125 and a descender of 5.90625.
        Assert.Equal(27.0703125, MacTextMetrics.Ascender(28));
        Assert.Equal(5.90625, MacTextMetrics.Descender(28));
        Assert.Equal(33, MacTextMetrics.LineHeight(28));
        Assert.Equal(16, MacTextMetrics.LineHeight(13));
        Assert.Equal(10 + 27.0703125, MacTextMetrics.FirstBaseline(new Rect(4, 10, 200, 40), 28));
    }

    [Fact]
    public void TheMacContentHeightCountsTextKitLines()
    {
        Assert.Equal(35, MacTextMetrics.ContentHeight("Service", 28));
        Assert.Equal(35, MacTextMetrics.ContentHeight("", 28));
        Assert.Equal(68, MacTextMetrics.ContentHeight("Two\nlines", 28));
        Assert.Equal(68, MacTextMetrics.ContentHeight("Two\r\nlines", 28));
        Assert.Equal(68, MacTextMetrics.ContentHeight("Two\rlines", 28));
        Assert.Equal(68, MacTextMetrics.ContentHeight("Two\u2028lines", 28));
        // TextKit counts the empty line after a final line feed, and the content size adds one more unrounded line.
        Assert.Equal(101, MacTextMetrics.ContentHeight("Ends with a line break\n", 28));
        Assert.Equal(50, MacTextMetrics.ContentHeight("Ends with a line break\n", 13));
    }

    [Fact]
    public void ACenteredLabelKeepsTheMacBaselineWhateverTheWindowsFrameHeight()
    {
        // The Mac centers a 35-point frame on y = 100 and draws from its top: the baseline is 82.5 + 27.07.
        foreach (var height in new[] { 35.0, 40, 47.5 })
        {
            var frame = new Rect(10, 100 - height / 2, 120, height);
            Assert.Equal(82.5 + 27.0703125, MacTextMetrics.CenteredFirstBaseline(frame, "Service", 28), 9);
        }
    }
}
