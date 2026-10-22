// Port of apps/macos/Tests/AnnotationCoreTests/InteractionTests.swift.
namespace Annotator.Core.Tests;

public class InteractionTests
{
    [Fact]
    public void SelectionMatchesVisibleProtectionStacking()
    {
        var frame = new Rect(20, 20, 100, 80);
        var solid = new Element(Tool.Cover, frame);
        var blur = new Element(Tool.Blur, frame);
        var shape = new Element(Tool.Rectangle, frame);
        var document = new CanvasDocument([solid, blur, shape]);
        document.Select(frame.Center);
        Assert.True(document.Selection.SetEquals([solid.Id]));
        document.DeleteSelection();
        document.Select(frame.Center);
        Assert.True(document.Selection.SetEquals([blur.Id]));
        document.DuplicateSelection();
        Assert.Equal(2, document.Elements.Count(element => element.Tool == Tool.Blur));
        document.Undo();
        Assert.Single(document.Elements, element => element.Tool == Tool.Blur);
    }

    [Fact]
    public void CurvedConnectorsAndHighlighterEdgesAreSelectable()
    {
        var arrow = new Element(Tool.Arrow, points: [new(10, 90), new(90, 10), new(170, 110)]);
        arrow.Style.Curved = true;
        var curves = ConnectorGeometry.Curves(arrow.Points);
        Assert.NotEmpty(curves);
        var point = curves[0].PointAt(0.5);
        Assert.True(HitTesting.Contains(point, arrow, tolerance: 0.5));
        var marker = new Element(Tool.Highlighter, points: [new(10, 50), new(150, 50)]);
        marker.Style.Width = 6;
        Assert.True(HitTesting.Contains(new Point(80, 63), marker, tolerance: 0));
        Assert.False(HitTesting.Contains(new Point(80, 80), marker, tolerance: 0));
    }

    [Fact]
    public void PresentationAndDrawingShareUndoAndRedo()
    {
        var document = CanvasDocument.Empty();
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 100, 60));
        document.Add(shape);
        document.SetPresentation(new PresentationSettings(PresentationMode.Lens, 3));
        document.Undo();
        Assert.Equal(PresentationMode.Off, document.Presentation.Mode);
        Assert.Single(document.Elements);
        document.Undo();
        Assert.Empty(document.Elements);
        document.Redo();
        document.Redo();
        Assert.Equal(new PresentationSettings(PresentationMode.Lens, 3), document.Presentation);
        Assert.Single(document.Elements);
        var reopened = DrawingFile.Read(new DrawingFile(document).Data());
        Assert.Equal(PresentationMode.Off, reopened.Presentation.Mode);
    }

    [Fact]
    public void CursorAnchoredZoomKeepsNativeInputAligned()
    {
        foreach (var screen in new[] { new Rect(0, 0, 1920, 1080), new Rect(-1440, -900, 1440, 900) })
        {
            foreach (var zoom in new[] { 1.0, 1.5, 2, 4, 16 })
            {
                foreach (var pointer in new[] { screen.Origin, screen.Center, new Point(screen.MaxX, screen.MaxY), new Point(screen.X + 53, screen.Y + 91) })
                {
                    var viewport = Viewport.CursorAnchored(screen, zoom, pointer);
                    Assert.True(viewport.SourcePoint(pointer).Distance(pointer) < 0.000_001);
                    Assert.True(viewport.OutputPoint(pointer).Distance(pointer) < 0.000_001);
                    Assert.True(screen.Contains(viewport.Source));
                }
            }
        }
    }
}
