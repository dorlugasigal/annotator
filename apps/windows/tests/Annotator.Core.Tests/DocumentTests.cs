// Port of apps/macos/Tests/AnnotationCoreTests/DocumentTests.swift.
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class DocumentTests
{
    [Fact]
    public void GroupedHistoryRetainsOperationIdentityAcrossEmptyParticipants()
    {
        var operation = Guid.NewGuid();
        var document = CanvasDocument.Empty();
        document.Begin();
        document.ClearAll();
        document.Commit(operation);
        Assert.True(document.CanUndo);
        Assert.Equal(operation, document.UndoOperationId);
        document.Undo();
        Assert.Equal(operation, document.RedoOperationId);
        document.Redo();
        Assert.Equal(operation, document.UndoOperationId);
        document.Add(new Element(Tool.Rectangle));
        Assert.Null(document.UndoOperationId);
        document.Undo();
        Assert.Equal(operation, document.UndoOperationId);
    }

    [Fact]
    public void TransactionCancellationPreservesRedo()
    {
        var document = CanvasDocument.Empty();
        var element = new Element(Tool.Rectangle, new Rect(10, 10, 80, 50));
        document.Add(element);
        document.Undo();
        Assert.True(document.CanRedo);
        document.Begin();
        document.Add(new Element(Tool.Ellipse));
        document.Cancel();
        Assert.Empty(document.Elements);
        Assert.True(document.CanRedo);
        document.Redo();
        Assert.Equal(new[] { element.Id }, document.Elements.Select(item => item.Id));
    }

    [Fact]
    public void CancellingNewTextRestoresSelectionHistoryAndSavedContent()
    {
        var original = new Element(Tool.Text, new Rect(10, 10, 80, 50), text: "Keep");
        var undone = new Element(Tool.Ellipse);
        var document = new CanvasDocument([original]);
        document.Add(undone);
        document.Undo();
        document.Select([original.Id]);
        var generation = document.Generation;
        var historyBytes = document.HistoryBytes;
        document.Begin();
        var draft = new Element(Tool.Text, text: " \n");
        document.Add(draft);
        document.Select([draft.Id]);
        document.Cancel();
        Assert.Equal([original], document.Elements);
        Assert.True(document.Selection.SetEquals([original.Id]));
        Assert.Equal(generation, document.Generation);
        Assert.Equal(historyBytes, document.HistoryBytes);
        Assert.False(document.CanUndo);
        Assert.True(document.CanRedo);
        Assert.False(document.IsEditing);
        Assert.Equal([original], DrawingFile.Read(new DrawingFile(document).Data()).Elements);
        Assert.Equal(new[] { "Keep" }, new SavedShape("Text", document).Elements.Select(item => item.Text));
        document.Redo();
        Assert.Equal([original, undone], document.Elements);
    }

    [Fact]
    public void LongStrokeIsOneTransactionWithoutTruncation()
    {
        var document = CanvasDocument.Empty();
        var stroke = new Element(Tool.Pen);
        document.Begin();
        document.Add(stroke);
        for (var index = 0; index < 20_000; index++)
        {
            document.AppendSample(stroke.Id, new Point(index, Math.Sin(index)));
        }
        document.Commit();
        Assert.Equal(20_000, document.Elements[0].Points.Count);
        Assert.Equal(20_000, document.Elements[0].Pressures.Count);
        document.Undo();
        Assert.Empty(document.Elements);
        document.Redo();
        Assert.Equal(20_000, document.Elements[0].Points.Count);
    }

    [Fact]
    public void ProtectionSurvivesUntargetedCleanup()
    {
        var document = CanvasDocument.Empty();
        var cover = new Element(Tool.Cover, new Rect(0, 0, 100, 40));
        document.Add(cover);
        document.Select([cover.Id]);
        document.DeleteSelection(includingProtection: false);
        document.ClearAnnotations();
        Assert.True(document.HasProtection);
        document.ClearProtection();
        Assert.False(document.HasProtection);
    }

    [Fact]
    public void BindingsFollowAndDuplicateTargets()
    {
        var target = new Element(Tool.Rectangle, new Rect(0, 0, 100, 100));
        var arrow = new Element(Tool.Arrow, points: [new(100, 50), new(200, 50)]);
        arrow.StartAnchor = new Anchor(target.Id, new Point(1, 0.5));
        var document = new CanvasDocument([target, arrow]);
        document.Select([target.Id]);
        document.EditSelection((ref Element element) => element.Translate(new Point(30, 40)));
        Assert.Equal(new Point(130, 90), document.Elements[1].Points[0]);
        document.Select([target.Id, arrow.Id]);
        document.DuplicateSelection();
        var selection = document.Selection;
        var copies = document.Elements.Where(element => selection.Contains(element.Id)).ToList();
        var copiedTarget = copies.First(element => element.Tool == Tool.Rectangle);
        var copiedArrow = copies.First(element => element.Tool == Tool.Arrow);
        Assert.Equal(copiedTarget.Id, copiedArrow.StartAnchor?.ElementId);
        Assert.Equal(new Point(148, 108), copiedArrow.Points[0]);
    }

    [Fact]
    public void LocksAndHistoryBudgetPreserveVisibleElements()
    {
        var document = new CanvasDocument(historyByteLimit: 1_500, historyCountLimit: 3);
        var element = new Element(Tool.Rectangle);
        document.Add(element);
        document.Select([element.Id]);
        document.SetSelectionLocked(true);
        document.EditSelection((ref Element item) => item.Text = "must not apply");
        document.DeleteSelection();
        Assert.Single(document.Elements);
        Assert.Empty(document.Elements[0].Text);
        for (var index = 0; index < 20; index++)
        {
            document.Add(new Element(Tool.Ellipse));
        }
        Assert.Equal(21, document.Elements.Count);
        Assert.True(document.HistoryBytes <= 1_500);
    }

    [Fact]
    public void FileRoundTripAndInvalidInput()
    {
        var document = CanvasDocument.Empty();
        document.Add(new Element(Tool.Text, new Rect(-200, 20, 100, 90), text: "Hello\nworld"));
        var decoded = DrawingFile.Read(new DrawingFile(document).Data());
        Assert.Equal(document.Elements, decoded.Elements);
        AssertCanvasError(CanvasError.UnsupportedSchema(99), () => DrawingFile.Read("""{"schemaVersion":99,"elements":[]}"""u8));
        var invalid = new Element(Tool.Pen);
        invalid.Points = [new(double.PositiveInfinity, 0)];
        AssertCanvasError(CanvasError.InvalidGeometry, () => document.Add(invalid));
    }

    [Fact]
    public void ViewportRoundTripWithNegativeOrigin()
    {
        var display = new Rect(-1_440, -900, 1_440, 900);
        var viewport = Viewport.Zoom(display, 4, new Point(-50, -50));
        foreach (var point in new[] { display.Origin, display.Center, new Point(display.MaxX, display.MaxY) })
        {
            var roundTrip = viewport.OutputPoint(viewport.SourcePoint(point));
            Assert.True(roundTrip.Distance(point) < 0.000_001);
        }
        Assert.True(display.Contains(viewport.Source));
        AssertCanvasError(CanvasError.InvalidGeometry, () => Viewport.Zoom(display, double.NaN, Point.Zero));
    }
}
