// Port of apps/macos/Tests/AnnotationCoreTests/SavedShapeTests.swift.
using System.Text;
using System.Text.Json.Nodes;
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class SavedShapeTests
{
    private static CanvasDocument Selection(params Element[] elements)
    {
        var document = new CanvasDocument(ElementList.Create(elements));
        document.Select(elements.Select(element => element.Id).ToArray());
        return document;
    }

    private static Element Rectangle() => new(Tool.Rectangle, new Rect(40, 60, 100, 80));

    [Fact]
    public void SavingPreservesSourceAndRoundTripsAllDrawingKinds()
    {
        var tools = Tool.AllCases.Where(tool => !tool.IsProtection && tool is not (Tool.Select or Tool.Eraser or Tool.Laser or Tool.Image));
        var elements = tools.Select(tool =>
        {
            var item = new Element(tool, new Rect(10, 20, 80, 70),
                                   points: tool.IsFreehand || tool.IsConnector ? [new(10, 20), new(90, 90)] : [],
                                   text: tool == Tool.Text ? "Two\nlines" : "");
            if (tool.IsFreehand)
            {
                item.Pressures = [0.25, 0.75];
            }
            item.Rotation = 0.25;
            item.Style.Roughness = 2;
            return item;
        }).ToArray();
        var document = Selection(elements);
        document.Add(Rectangle());
        document.Undo();
        var original = document.Elements;
        var selected = document.Selection;
        var shape = new SavedShape("  Diagram  ", document);
        var restored = SavedShape.Read(shape.Data());
        Assert.Equal("Diagram", restored.Name);
        Assert.Equal(shape.Id, restored.Id);
        Assert.Equal(original, restored.Elements);
        Assert.Equal(original, document.Elements);
        Assert.True(document.Selection.SetEquals(selected));
        Assert.True(document.CanRedo);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void FreshCopiesRemapGroupsAnchorsAndSupportOneUndoRedo()
    {
        var target = Rectangle();
        var text = new Element(Tool.Text, new Rect(50, 70, 60, 30), text: "Label");
        var external = new Element(Tool.Ellipse, new Rect(260, 60, 80, 80));
        var arrow = new Element(Tool.Arrow, points: [new(140, 100), new(260, 100)]);
        arrow.StartAnchor = new Anchor(target.Id, new Point(1, 0.5));
        arrow.EndAnchor = new Anchor(external.Id, new Point(0, 0.5));
        target.GroupId = Guid.NewGuid();
        text.GroupId = target.GroupId;
        arrow.GroupId = Guid.NewGuid();
        target.Locked = true;
        var document = Selection(target, text, arrow, external);
        document.Select([target.Id, text.Id, arrow.Id]);
        var original = document.Elements;
        var originalSelection = document.Selection;
        var shape = new SavedShape("Connected group", document);
        Assert.Null(shape.Elements[2].EndAnchor);
        Assert.Equal(original[2].Points[original[2].Points.Count - 1], shape.Elements[2].Points[shape.Elements[2].Points.Count - 1]);
        Assert.Equal(original, document.Elements);
        var center = new Point(700, 500);
        var result = shape.Adding(document, center);
        var inserted = result.Elements.Skip(original.Count).ToArray();
        Assert.Equal(3, inserted.Length);
        Assert.True(result.Selection.SetEquals(inserted.Select(element => element.Id)));
        Assert.Equal(original, ElementList.CreateRange(result.Elements.Take(original.Count)));
        Assert.DoesNotContain(inserted, element => original.Contains(element.Id));
        Assert.All(inserted, element => Assert.False(element.Locked));
        Assert.Equal(inserted[0].GroupId, inserted[1].GroupId);
        Assert.NotEqual(target.GroupId, inserted[0].GroupId);
        Assert.NotEqual(arrow.GroupId, inserted[2].GroupId);
        Assert.Equal(inserted[0].GroupId, inserted[2].GroupId);
        Assert.Equal(inserted[0].Id, inserted[2].StartAnchor?.ElementId);
        Assert.Null(inserted[2].EndAnchor);
        Assert.Equal(target.Seed, inserted[0].Seed);
        Assert.NotEqual(target.GeometryToken, inserted[0].GeometryToken);
        Assert.NotEqual(target.AppendEpoch, inserted[0].AppendEpoch);
        var insertedShape = new SavedShape("Copy", result);
        Assert.Equal(center, insertedShape.Bounds.Center);
        var second = shape.Adding(result, center);
        Assert.True(second.Selection.Intersect(result.Selection).IsEmpty);
        var secondGroups = second.Elements.Skip(second.Elements.Count - 3).Select(element => element.GroupId).OfType<Guid>().ToHashSet();
        Assert.DoesNotContain(inserted.Select(element => element.GroupId).OfType<Guid>(), group => secondGroups.Contains(group));
        result.Undo();
        Assert.Equal(original, result.Elements);
        Assert.True(result.Selection.SetEquals(originalSelection));
        Assert.False(result.CanUndo);
        result.Redo();
        Assert.Equal(inserted, result.Elements.Skip(result.Elements.Count - 3).ToArray());
        Assert.True(result.Selection.SetEquals(inserted.Select(element => element.Id)));
        result.Select([inserted[0].Id]);
        result.EditSelection((ref Element element) => element.Translate(new Point(12, 18)));
        Assert.Equal(inserted[2].Points[0] + new Point(12, 18), result.Elements[6].Points[0]);
    }

    [Fact]
    public void UnsupportedSelectionFailsAsAWhole()
    {
        foreach (var tool in Tool.AllCases.Where(tool => tool.IsProtection || tool == Tool.Image))
        {
            var item = new Element(tool, new Rect(0, 0, 20, 20));
            if (tool == Tool.Image)
            {
                item.ImagePng = ValueList.Create<byte>([137, 80, 78, 71, 13, 10, 26, 10]);
            }
            var document = Selection(Rectangle(), item);
            AssertShapeError(ShapeLibraryError.UnsupportedContent, () => _ = new SavedShape("Private", document));
            Assert.Equal(2, document.Elements.Count);
        }
    }

    [Fact]
    public void EmptyNamesControlsAndPendingEditsAreRejected()
    {
        var document = Selection(Rectangle());
        foreach (var name in new[] { "", "   ", "Line\nbreak", "A\u0000B", "\u202Ereversed", new string('A', 121) })
        {
            AssertShapeError(ShapeLibraryError.InvalidName, () => _ = new SavedShape(name, document));
        }
        Assert.Equal(120, new SavedShape(new string('A', 120), document).Name.Length);
        AssertShapeError(ShapeLibraryError.NoSelection, () => _ = new SavedShape("Empty", CanvasDocument.Empty()));
        var pending = document;
        pending.Begin();
        AssertShapeError(ShapeLibraryError.UnfinishedEdit, () => _ = new SavedShape("Draft", pending));
        var shape = new SavedShape("Ready", document);
        AssertShapeError(ShapeLibraryError.UnfinishedEdit, () => shape.Adding(pending, Point.Zero));
        Assert.True(pending.IsEditing);
        Assert.Equal(document.Elements, pending.Elements);
    }

    [Fact]
    public void MalformedSchemaIdentityGeometryAndHiddenImageDataAreRejected()
    {
        var shape = new SavedShape("Valid", Selection(Rectangle()));
        var data = shape.Data();
        Assert.ThrowsAny<Exception>(() => SavedShape.Read("broken"u8));
        for (var mutation = 0; mutation < 6; mutation++)
        {
            var changed = JsonObjectOf(data);
            var drawing = changed["drawing"]!.AsObject();
            var elements = drawing["elements"]!.AsArray();
            switch (mutation)
            {
                case 0: changed["schemaVersion"] = 99; break;
                case 1: drawing["schemaVersion"] = 99; break;
                case 2: elements.Add(elements[0]!.DeepClone()); break;
                case 3: elements[0]!["frame"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = -1, ["height"] = 5 }; break;
                case 4: elements[0]!["imagePNG"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("private pixels")); break;
                default: elements[0]!["tool"] = "cover"; break;
            }
            var invalid = Bytes(changed);
            Assert.ThrowsAny<Exception>(() => SavedShape.Read(invalid));
        }
    }

    [Fact]
    public void RotatedBoundsAndInvalidPlacement()
    {
        var item = Rectangle();
        item.Rotation = Math.PI / 2;
        var document = Selection(item);
        var shape = new SavedShape("Rotated", document);
        var result = shape.Adding(CanvasDocument.Empty(), new Point(-300, 400));
        Assert.True(Math.Abs(result.Elements[0].Frame.Center.X + 300) < 0.0001);
        Assert.True(Math.Abs(result.Elements[0].Frame.Center.Y - 400) < 0.0001);
        foreach (var center in new[] { new Point(double.PositiveInfinity, 0), new Point(10_000_001, 0) })
        {
            AssertCanvasError(CanvasError.InvalidGeometry, () => shape.Adding(document, center));
        }
        Assert.Equal([item], document.Elements);
    }

    [Fact]
    public void InputAndUndoBudgetsRejectWithoutTruncation()
    {
        var shape = new SavedShape("Box", Selection(Rectangle()));
        AssertShapeError(ShapeLibraryError.EntryTooLarge, () => SavedShape.Read(Repeated(32, SavedShape.MaximumBytes + 1)));
        var pointCount = SavedShape.MaximumBytes / 16;
        var longStroke = new Element(Tool.Pen, points: ValueList.Repeat(new Point(1, 2), pointCount));
        var document = Selection(longStroke);
        AssertShapeError(ShapeLibraryError.EntryTooLarge, () => _ = new SavedShape("Long", document));
        Assert.Equal(pointCount, document.Elements[0].Points.Count);
        var limited = new CanvasDocument([Rectangle()], historyByteLimit: 1_000);
        AssertShapeError(ShapeLibraryError.UndoBudget, () => shape.Adding(limited, Point.Zero));
        Assert.Single(limited.Elements);
        Assert.False(limited.CanUndo);
    }

    [Fact]
    public void FullStrokeAndTextSurviveSavingAndInsertion()
    {
        var points = Enumerable.Range(0, 20_000).Select(index => new Point(index / 100.0, Math.Sin(index))).ToValueList();
        var stroke = new Element(Tool.Pen, points: points);
        stroke.Pressures = ValueList.Repeat(0.75, points.Count);
        var text = new Element(Tool.Text, new Rect(10, 20, 50, 40), text: "A\nB");
        var shape = SavedShape.Read(new SavedShape("Ink", Selection(stroke, text)).Data());
        var copy = shape.Adding(CanvasDocument.Empty(), shape.Bounds.Center);
        Assert.Equal(points, copy.Elements[0].Points);
        Assert.Equal(stroke.Pressures, copy.Elements[0].Pressures);
        Assert.Equal(text.Text, copy.Elements[1].Text);
    }
}
