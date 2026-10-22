// Port of apps/macos/Tests/AnnotationCoreTests/CopyAndStackingTests.swift.
namespace Annotator.Core.Tests;

public class CopyAndStackingTests
{
    [Fact]
    public void TextInsideANewerOutlineShapeStaysReachable()
    {
        var text = new Element(Tool.Text, new Rect(60, 60, 80, 24), text: "Orders");
        var box = new Element(Tool.Rectangle, new Rect(20, 20, 200, 120));
        var document = new CanvasDocument([text, box]);
        Assert.Equal(text.Id, ElementStacking.Frontmost(new Point(80, 70), document.Elements)?.Id);
        Assert.Equal(box.Id, ElementStacking.Frontmost(new Point(180, 120), document.Elements)?.Id);
        document.Select(new Point(80, 70));
        Assert.True(document.Selection.SetEquals([text.Id]));
    }

    [Fact]
    public void SolidFilledShapeStillHidesTextBehindIt()
    {
        var text = new Element(Tool.Text, new Rect(60, 60, 80, 24), text: "Hidden");
        var box = new Element(Tool.Ellipse, new Rect(20, 20, 200, 120));
        box.Style.Fill = Palette.Blue;
        Assert.Equal(box.Id, ElementStacking.Frontmost(new Point(100, 70), [text, box])?.Id);
        box.Style.FillPattern = FillPattern.Hatch;
        Assert.Equal(text.Id, ElementStacking.Frontmost(new Point(100, 70), [text, box])?.Id);
        var pen = new Element(Tool.Pen, points: [new(60, 70), new(140, 70)]);
        Assert.Equal(pen.Id, ElementStacking.Frontmost(new Point(100, 70), [text, pen])?.Id);
    }

    [Fact]
    public void InsertCopiesKeepsInternalLinksAndDropsOutsideOnes()
    {
        var target = new Element(Tool.Rectangle, new Rect(0, 0, 100, 100));
        var outside = new Element(Tool.Ellipse, new Rect(300, 0, 100, 100));
        var arrow = new Element(Tool.Arrow, points: [new(100, 50), new(300, 50)]);
        arrow.StartAnchor = new Anchor(target.Id, new Point(1, 0.5));
        arrow.EndAnchor = new Anchor(outside.Id, new Point(0, 0.5));
        var grouped = new Element(Tool.Text, new Rect(10, 10, 40, 20), text: "A");
        grouped.GroupId = Guid.NewGuid();
        grouped.Locked = true;
        var document = new CanvasDocument([target, outside, arrow]);
        document.InsertCopies([target, arrow, grouped], new Point(10, 20));
        Assert.Equal(6, document.Elements.Count);
        var selection = document.Selection;
        var copies = document.Elements.Where(element => selection.Contains(element.Id)).ToList();
        Assert.Equal(3, copies.Count);
        Assert.DoesNotContain(copies, copy => copy.Id == target.Id || copy.Id == arrow.Id || copy.Id == grouped.Id);
        var copiedTarget = copies.First(element => element.Tool == Tool.Rectangle);
        var copiedArrow = copies.First(element => element.Tool == Tool.Arrow);
        var copiedText = copies.First(element => element.Tool == Tool.Text);
        Assert.Equal(new Point(10, 20), copiedTarget.Frame.Origin);
        Assert.Equal(copiedTarget.Id, copiedArrow.StartAnchor?.ElementId);
        Assert.Null(copiedArrow.EndAnchor);
        Assert.True(copiedText.GroupId is not null && copiedText.GroupId != grouped.GroupId);
        Assert.False(copiedText.Locked);
        document.Undo();
        Assert.Equal(3, document.Elements.Count);
    }

    [Fact]
    public void AnchorLocationsMatchNearestAnchor()
    {
        var box = new Element(Tool.Rectangle, new Rect(0, 0, 100, 60));
        Assert.Equal(4, box.AnchorLocations.Count);
        var document = new CanvasDocument([box]);
        Assert.Equal(new Anchor(box.Id, new Point(1, 0.5)), document.NearestAnchor(new Point(104, 30), excluding: Guid.NewGuid()));
        Assert.Null(document.NearestAnchor(new Point(150, 30), excluding: Guid.NewGuid()));
    }
}
