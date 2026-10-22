// Port of apps/macos/Tests/AnnotationCoreTests/PendingUndoRegressionTests.swift.
namespace Annotator.Core.Tests;

public class PendingUndoRegressionTests
{
    [Fact]
    public void UndoCancelsDraftBeforeTouchingCompletedHistory()
    {
        var document = CanvasDocument.Empty();
        var completed = new Element(Tool.Rectangle, new Rect(10, 10, 80, 50));
        var future = new Element(Tool.Ellipse, new Rect(100, 10, 40, 40));
        document.Add(completed);
        document.Select([completed.Id]);
        document.Add(future);
        document.Undo();
        var generation = document.Generation;
        var historyBytes = document.HistoryBytes;
        var restored = document.Elements;

        document.Begin();
        document.Add(new Element(Tool.Pen, points: [new(20, 20), new(30, 30)]));
        document.SetPresentation(new PresentationSettings(PresentationMode.Lens, 3));
        document.Select([]);
        document.Undo();

        Assert.False(document.IsEditing);
        Assert.Equal(restored, document.Elements);
        Assert.True(document.Selection.SetEquals([completed.Id]));
        Assert.Equal(PresentationMode.Off, document.Presentation.Mode);
        Assert.Equal(generation, document.Generation);
        Assert.Equal(historyBytes, document.HistoryBytes);
        Assert.True(document.CanUndo);
        Assert.True(document.CanRedo);
        document.Redo();
        Assert.Equal(new[] { completed.Id, future.Id }, document.Elements.Select(element => element.Id));
        document.Undo();
        document.Undo();
        Assert.Empty(document.Elements);
    }
}
