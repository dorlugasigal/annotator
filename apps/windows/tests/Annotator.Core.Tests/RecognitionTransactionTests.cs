// Port of apps/macos/Tests/AnnotationCoreTests/RecognitionTransactionTests.swift.
namespace Annotator.Core.Tests;

public class RecognitionTransactionTests
{
    [Fact]
    public void RecognitionSharesUndoAndRejectsLateEdits()
    {
        var points = Enumerable.Range(0, 101).Select(index =>
        {
            var angle = index / 100.0 * Math.PI * 2;
            return new Point(100 + 50 * Math.Cos(angle), 100 + 50 * Math.Sin(angle));
        }).ToValueList();
        var stroke = new Element(Tool.Pen, points: points);
        var document = CanvasDocument.Empty();
        document.Add(stroke);
        var generation = document.Generation;
        var recognized = Assert.NotNull(ShapeRecognition.Recognize(stroke));
        Assert.True(document.ApplyRecognition(recognized, generation, stroke.GeometryToken));
        Assert.Equal(Tool.Ellipse, document.Elements[0].Tool);
        document.Undo();
        Assert.Empty(document.Elements);
        document.Redo();
        Assert.Equal(Tool.Ellipse, document.Elements[0].Tool);

        var edited = CanvasDocument.Empty();
        edited.Add(stroke);
        var prior = edited.Generation;
        edited.Select([stroke.Id]);
        edited.SetSelectionLocked(true);
        Assert.False(edited.ApplyRecognition(recognized, prior, stroke.GeometryToken));
        Assert.Equal(Tool.Pen, edited.Elements[0].Tool);
        Assert.True(edited.Elements[0].Locked);
    }
}
