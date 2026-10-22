// Port of apps/macos/Tests/AnnotationCoreTests/ShapeLabelTests.swift.
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class ShapeLabelTests
{
    [Fact]
    public void ShapeLabelsStayInsideTheirOwnersAcrossHistoryAndCopies()
    {
        var shape = new Element(Tool.Database, new Rect(40, 50, 160, 120));
        shape.Label = new ShapeLabel("Orders");
        var document = new CanvasDocument([shape]);
        document.Select([shape.Id]);
        document.DuplicateSelection();
        var copy = document.Elements[document.Elements.Count - 1];
        Assert.NotEqual(shape.Id, copy.Id);
        Assert.Equal(shape.Label, copy.Label);
        document.Update(copy.Id, (ref Element element) => element.Label = new ShapeLabel("Archive"));
        Assert.Equal("Orders", document.Elements[0].Label?.Text);
        document.Undo();
        Assert.Equal("Orders", document.Elements[document.Elements.Count - 1].Label?.Text);
        document.Update(shape.Id, (ref Element element) => element.Translate(new Point(25, 30)));
        Assert.Equal("Orders", document.Elements[0].Label?.Text);
        var label = document.Elements[0].Label;
        Assert.NotNull(label);
        var oldFont = label.Style.FontSize;
        document.Update(shape.Id, (ref Element element) => element.Resize(new Rect(65, 80, 320, 240)));
        Assert.Equal(oldFont * 2, document.Elements[0].Label?.Style.FontSize);
        // Swift compares with MemoryLayout<Element>.stride; the port counts the 512-byte floor (see Element.cs).
        Assert.True(shape.EstimatedBytes >= Element.MinimumEstimatedBytes + shape.Label.EstimatedBytes);
    }

    [Fact]
    public void LabeledBoardsUseVersionTwoAndOldBoardsStillLoad()
    {
        var plain = new Element(Tool.Database, new Rect(0, 0, 160, 120));
        var old = new DrawingFile(new CanvasDocument([plain]));
        Assert.Equal(1, old.SchemaVersion);
        Assert.Null(DrawingFile.Read(old.Data()).Elements[0].Label);
        var labeled = plain;
        labeled.Label = new ShapeLabel("Customer records");
        var file = new DrawingFile(new CanvasDocument([labeled]));
        Assert.Equal(2, file.SchemaVersion);
        Assert.Equal([labeled], DrawingFile.Read(file.Data()).Elements);
        var json = JsonObjectOf(file.Data());
        json["schemaVersion"] = 1;
        var malformed = Bytes(json);
        Assert.Throws<CanvasError>(() => DrawingFile.Read(malformed));
    }

    [Fact]
    public void SavedShapesKeepLabelsAndLegacyCylinderIdentity()
    {
        var shape = new Element(Tool.Database, new Rect(40, 50, 160, 120));
        shape.Label = new ShapeLabel("Orders");
        var document = new CanvasDocument([shape]);
        document.Select([shape.Id]);
        var saved = new SavedShape("Labeled cylinder", document);
        var loaded = SavedShape.Read(saved.Data());
        var inserted = loaded.Adding(CanvasDocument.Empty(), new Point(300, 200));
        Assert.Equal(shape.Label, inserted.Elements[0].Label);
        Assert.Equal(new Point(300, 200), inserted.Elements[0].Frame.Center);
        Assert.Equal("database", Tool.Database.RawValue);
        Assert.Equal("Cylinder", Tool.Database.Label);
        Assert.Equal("Database", Tool.DatabaseStack.Label);
    }

    [Fact]
    public void InvalidLabelsAreRejectedAndDefaultTextHasContrast()
    {
        var cover = new Element(Tool.Cover, new Rect(0, 0, 50, 50));
        cover.Label = new ShapeLabel("Not a shape");
        Assert.Throws<CanvasError>(() => cover.Validate());
        Assert.Equal(Palette.White, ShapeLabel.DefaultColor(new RGBA(0.1, 0.1, 0.1)));
        Assert.Equal(new RGBA(0, 0, 0), ShapeLabel.DefaultColor(Palette.White));
    }
}
