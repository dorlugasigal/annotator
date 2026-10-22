// Port of apps/macos/Tests/AnnotationCoreTests/HistoryByteCacheTests.swift.
namespace Annotator.Core.Tests;

public class HistoryByteCacheTests
{
    [Fact]
    public void CachedSnapshotBytesRemainExactThroughUndoRedoAndBranching()
    {
        var first = new Element(Tool.Text, new Rect(0, 0, 80, 30), text: "First");
        var second = new Element(Tool.Text, new Rect(0, 50, 80, 30), text: "Second");
        var document = new CanvasDocument();
        document.Add(first);
        Assert.Equal(64, document.HistoryBytes);
        document.Add(second);
        Assert.Equal(128 + first.EstimatedBytes, document.HistoryBytes);
        document.Undo();
        Assert.Equal(128 + first.EstimatedBytes + second.EstimatedBytes, document.HistoryBytes);
        document.Redo();
        Assert.Equal(128 + first.EstimatedBytes, document.HistoryBytes);
        document.Update(second.Id, (ref Element element) => element.Text = new string('A', 8_000));
        var beforeUndo = document.HistoryBytes;
        document.Undo();
        Assert.True(document.HistoryBytes > beforeUndo);
        document.Redo();
        Assert.Equal(beforeUndo, document.HistoryBytes);
        document.Undo();
        document.Update(first.Id, (ref Element element) => element.Text = "Branch");
        Assert.False(document.CanRedo);
        Assert.True(document.HistoryBytes <= document.HistoryByteLimit);
    }
}
