// Port of apps/macos/Tests/AnnotationCoreTests/RecognitionTests.swift.
namespace Annotator.Core.Tests;

public class MaskAndRecognitionTests
{
    [Fact]
    public void RecognitionIsBoundedAndPreservesIdentity()
    {
        var points = Enumerable.Range(0, 1_001).Select(index =>
        {
            var angle = index / 1_000.0 * 2 * Math.PI;
            return new Point(100 + 80 * Math.Cos(angle), 100 + 60 * Math.Sin(angle));
        }).ToValueList();
        var stroke = new Element(Tool.Pen, points: points);
        var recognized = Assert.NotNull(ShapeRecognition.Recognize(stroke));
        Assert.Equal(Tool.Ellipse, recognized.Tool);
        Assert.Equal(stroke.Id, recognized.Id);
        Assert.Equal(stroke.Seed, recognized.Seed);
        Assert.Null(ShapeRecognition.Recognize(new Element(Tool.Pen, points: [new(0, 0), new(100, 100)])));
    }

    [Fact]
    public void AppendingImportedFreehandKeepsPressureAligned()
    {
        var element = new Element(Tool.Pen, points: [new(0, 0), new(10, 10)]);
        var document = new CanvasDocument([element]);
        document.AppendSample(element.Id, new Point(20, 20), pressure: 0.5);
        document.Elements[0].Validate();
        Assert.Equal(new[] { 1, 1, 0.5 }, document.Elements[0].Pressures);
    }
}
