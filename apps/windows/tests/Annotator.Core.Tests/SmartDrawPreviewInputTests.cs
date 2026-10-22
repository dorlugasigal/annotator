// Port of apps/macos/Tests/AnnotationCoreTests/SmartDrawPreviewInputTests.swift.
namespace Annotator.Core.Tests;

public class SmartDrawPreviewInputTests
{
    [Fact]
    public void PreviewInputIsBoundedAndPreservesEndpoints()
    {
        var stroke = new Element(Tool.Pen, points: Enumerable.Range(0, 200_000).Select(index => new Point(index, index % 100)).ToValueList());
        var preview = ShapeRecognition.PreviewInput(stroke);
        Assert.Equal(256, preview.Points.Count);
        Assert.Equal(stroke.Points[0], preview.Points[0]);
        Assert.Equal(stroke.Points[stroke.Points.Count - 1], preview.Points[preview.Points.Count - 1]);
        Assert.Equal(stroke.Id, preview.Id);
        Assert.Equal(stroke.Seed, preview.Seed);
        Assert.Equal(200_000, stroke.Points.Count);
    }
}
