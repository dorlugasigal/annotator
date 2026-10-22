// Port of apps/macos/Tests/AnnotationCoreTests/PolygonAndLibraryEditingTests.swift.
namespace Annotator.Core.Tests;

public class PolygonAndLibraryEditingTests
{
    [Fact]
    public void NewPolygonShapesHitTheirPaintedOutlineAndExposeRealBoundaryAnchors()
    {
        foreach (var tool in new[] { Tool.Triangle, Tool.Hexagon, Tool.Star })
        {
            var element = new Element(tool, new Rect(100, 100, 180, 120));
            foreach (var angle in new[] { 0.0, Math.PI / 3 })
            {
                element.Rotation = angle;
                var center = element.Frame.Center;
                Assert.True(HitTesting.Contains(center, element, tolerance: 0));
                var outside = new Point(102, 102).Rotated(center, angle);
                Assert.False(HitTesting.Contains(outside, element, tolerance: 0));
                var kind = Assert.NotNull(tool.PolygonKind);
                Assert.Equal(element.Frame, Rect.Enclosing(PolygonShape.Vertices(kind, element.Frame)));
                var tip = PolygonShape.Vertices(kind, element.Frame)[0].Rotated(center, angle);
                Assert.True(HitTesting.Contains(tip, element, tolerance: 0));
                var document = new CanvasDocument([element]);
                var anchor = Assert.NotNull(document.NearestAnchor(tip, excluding: Guid.NewGuid(), threshold: 0.1));
                Assert.True(element.AnchorPoint(anchor.Relative).Distance(tip) < 0.001);
            }
            Assert.Equal(tool, DrawingFile.Read(new DrawingFile(new CanvasDocument([element])).Data()).Elements[0].Tool);
        }
    }

    [Fact]
    public async Task RenamingSavedShapesPreservesIdentityGeometryAndOriginalCanvas()
    {
        var document = new CanvasDocument([new Element(Tool.Rectangle, new Rect(10, 10, 70, 50))]);
        document.Select(document.Elements.Select(element => element.Id).ToArray());
        var original = new SavedShape("Original", document);
        var store = new ShapeLibraryStore();
        await store.SaveAsync(original);
        var renamed = await store.RenameAsync(original.Id, "New name");
        Assert.Equal(original.Id, renamed[0].Id);
        Assert.Equal("New name", renamed[0].Name);
        Assert.Equal(original.Elements, renamed[0].Elements);
        await Assert.ThrowsAsync<ShapeLibraryError>(() => store.RenameAsync(original.Id, " "));
        Assert.Equal("New name", (await store.LoadAsync())[0].Name);
        Assert.Equal(original.Elements, document.Elements);
    }

    [Fact]
    public void MultiObjectSavedShapeInsertsAsOneGroupAndCanBeUngrouped()
    {
        var source = new CanvasDocument(
        [
            new Element(Tool.Rectangle, new Rect(10, 10, 40, 30)),
            new Element(Tool.Ellipse, new Rect(80, 10, 40, 30)),
        ]);
        source.Select(source.Elements.Select(element => element.Id).ToArray());
        var saved = new SavedShape("Pair", source);
        var result = saved.Adding(CanvasDocument.Empty(), new Point(200, 200));
        Assert.NotNull(result.Elements[0].GroupId);
        Assert.Equal(result.Elements[0].GroupId, result.Elements[1].GroupId);
        Assert.All(source.Elements, element => Assert.Null(element.GroupId));
        result.UngroupSelection();
        Assert.All(result.Elements, element => Assert.Null(element.GroupId));
        result.Undo();
        Assert.Equal(result.Elements[0].GroupId, result.Elements[1].GroupId);
        result.Undo();
        Assert.Empty(result.Elements);
    }
}
