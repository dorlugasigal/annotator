using Annotator.Core;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// Copy, cut and paste logic with the paste placement rules, and image paste placement (AnnotationClipboardTests,
/// ImagePasteTests and ParityRegressionTests, logic only: the clipboard formats and image decoding are platform code).
/// </summary>
public sealed class CanvasClipboardTests
{
    private static readonly ClipboardImage SmallImage = new(ValueList.Create<byte>([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]), 24, 16);

    [Fact]
    public void CtrlCAndCtrlVCopyAnyObjectWithCascadingPlacement()
    {
        // AnnotationClipboardTests.testCommandCAndCommandVCopyAnyObjectWithCascadingPlacement.
        using CanvasHarness h = Single();
        var box = new Element(Tool.Rectangle, new Rect(40, 40, 120, 80)) { Label = new ShapeLabel("Orders") };
        var note = new Element(Tool.Text, new Rect(300, 60, 80, 24), text: "Hello");
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([box, note], box.Id));
        h.Pump();
        Assert.True(h.Key(Main, VirtualKeys.C, KeyModifiers.Ctrl, "c"));
        Assert.NotNull(h.Clipboard.Annotations);
        Assert.Equal("Copied 1 object. Press Ctrl+V to paste.", h.Model.Snapshot.Status);
        Assert.True(h.Key(Main, VirtualKeys.V, KeyModifiers.Ctrl, "v"));
        Assert.Equal(3, h.Elements(Main).Length);
        Element first = h.Elements(Main)[^1];
        Assert.NotEqual(box.Id, first.Id);
        Assert.Equal([first.Id], h.Document(Main).Selection);
        Assert.Equal(new Point(58, 58), first.Frame.Origin);
        Assert.Equal("Orders", first.Label?.Text);
        Assert.Equal(Tool.Select, h.Model.Snapshot.Tool);
        Assert.Equal("Pasted 1 object.", h.Model.Snapshot.Status);

        h.Model.Paste();
        Assert.Equal(new Point(76, 76), h.Elements(Main)[^1].Frame.Origin);

        h.DesktopPointer = MainDisplay.ToDesktop(new Point(400, 250));
        h.Model.Paste();
        Assert.Equal(new Point(400, 250), h.Elements(Main)[^1].Frame.Center);

        h.Model.Undo();
        Assert.Equal(4, h.Elements(Main).Length);

        TestWorkspace.AssertAccepted(h.Model.Edit(Main, document => document[0].Select([note.Id])));
        Assert.True(h.Key(Main, VirtualKeys.X, KeyModifiers.Ctrl, "x"));
        Assert.DoesNotContain(h.Elements(Main), element => element.Id == note.Id);
        Assert.Equal("Cut 1 object. Press Ctrl+V to paste.", h.Model.Snapshot.Status);
        h.DesktopPointer = null;
        h.Model.Paste();
        Assert.Equal("Hello", h.Elements(Main)[^1].Text);
    }

    [Fact]
    public void PasteLandsAtThePointerOnlyWhenItMovedAwayFromTheCopy()
    {
        var bounds = new Rect(40, 40, 120, 80);
        var canvas = new Rect(0, 0, 800, 600);
        (Point offset, Point target, Point? pointer) = PastePlacement.Origin(bounds, canvas, new Point(100, 80), null, null);
        Assert.Equal(new Point(58, 58), target);
        Assert.Equal(new Point(18, 18), offset);
        Assert.Equal(new Point(100, 80), pointer);
        (_, target, _) = PastePlacement.Origin(bounds, canvas, new Point(500, 300), null, null);
        Assert.Equal(new Point(440, 260), target);
        (_, target, _) = PastePlacement.Origin(bounds, canvas, new Point(500, 300), new Point(440, 260), new Point(500, 300));
        Assert.Equal(new Point(458, 278), target);
        (_, target, pointer) = PastePlacement.Origin(bounds, canvas, new Point(-5, 300), null, null);
        Assert.Null(pointer);
        Assert.Equal(new Point(58, 58), target);
        (_, target, _) = PastePlacement.Origin(bounds, canvas, new Point(790, 590), null, null);
        Assert.Equal(new Point(680, 520), target);
    }

    [Fact]
    public void CopyWithoutSelectionFallsThroughAndImagesStillPaste()
    {
        // AnnotationClipboardTests.testCopyWithoutSelectionFallsThroughAndImagesStillPaste.
        using CanvasHarness h = Single();
        Assert.False(h.Model.CopySelection());
        Assert.Equal(CanvasCommandResult.NotHandled, h.Model.Perform(new CanvasCommand(CanvasCommandKind.Copy, Main)));
        h.Clipboard.SetImage(SmallImage);
        h.Model.Paste();
        Element image = Assert.Single(h.Elements(Main));
        Assert.Equal(Tool.Image, image.Tool);
        Assert.Equal(new Rect(0, 0, 24, 16), image.Frame);
        Assert.Equal(SmallImage.Png, image.ImagePng);
        Assert.Equal("Image pasted as an editable layer. Resize, rotate, group or delete it like other objects.", h.Model.Snapshot.Status);
    }

    [Fact]
    public void LockedSelectionCopiesButDoesNotCut()
    {
        // AnnotationClipboardTests.testLockedSelectionCopiesButDoesNotCut.
        using CanvasHarness h = Single();
        var box = new Element(Tool.Rectangle, new Rect(40, 40, 120, 80)) { Locked = true };
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([box], box.Id));
        Assert.True(h.Model.CopySelection(cut: true));
        Assert.Single(h.Elements(Main));
        Assert.Null(h.Clipboard.Annotations);
        Assert.Equal("Some selected objects are locked. Unlock them before cutting, or use Copy.", h.Model.Snapshot.Status);
        Assert.True(h.Model.CopySelection());
        h.Model.Paste();
        Assert.Equal(2, h.Elements(Main).Length);
        Assert.False(h.Elements(Main)[1].Locked, "Pasted copies are editable.");
    }

    [Fact]
    public void ACutWithALockedObjectDoesNothingAndCopyNeedsDraw()
    {
        // AnnotationClipboardTests.testReviewEdgeCasesForCutLockedModeAndEndpointClicks (cut and mode parts).
        using CanvasHarness h = Single();
        var box = new Element(Tool.Rectangle, new Rect(40, 40, 120, 80));
        var pinned = new Element(Tool.Ellipse, new Rect(300, 40, 80, 80)) { Locked = true };
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([box, pinned], box.Id, pinned.Id));
        Assert.True(h.Model.CopySelection(cut: true));
        Assert.Equal(2, h.Elements(Main).Length);
        Assert.Null(h.Clipboard.Annotations);
        h.Model.UseApps();
        Assert.False(h.Model.CopySelection(), "Copy outside Draw mode leaves the key to other apps.");
    }

    [Fact]
    public void CopyAcrossDisplaysKeepsTheirRelativePlacement()
    {
        // AnnotationClipboardTests.testCopyAcrossDisplaysKeepsTheirRelativePlacement, with Windows desktop units: another
        // display's objects move by its top-left corner in the editing target's points.
        using CanvasHarness h = new();
        var here = new Element(Tool.Rectangle, new Rect(100, 100, 50, 40));
        var there = new Element(Tool.Ellipse, new Rect(30, 40, 50, 40));
        h.Add(TestWorkspace.Left, [there]);
        h.Add(TestWorkspace.Primary, [here]);
        h.Model.SelectAll();
        Assert.True(h.Model.Snapshot.SelectionIsWorkspaceWide);
        Assert.True(h.Model.CopySelection());
        h.Model.Paste();
        CanvasDocument first = h.Document(TestWorkspace.Primary);
        Element[] copies = [.. first.Elements.Where(element => first.Selection.Contains(element.Id))];
        Element a = copies.Single(element => element.Tool == Tool.Rectangle);
        Element b = copies.Single(element => element.Tool == Tool.Ellipse);
        Point corner = TestWorkspace.PrimaryDisplay.ToLocal(new Point(TestWorkspace.LeftDisplay.DesktopFrame.X, TestWorkspace.LeftDisplay.DesktopFrame.Y));
        Point offset = b.Frame.Origin - a.Frame.Origin;
        Assert.Equal(corner.X + 30 - 100, offset.X, 6);
        Assert.Equal(corner.Y + 40 - 100, offset.Y, 6);
        Assert.Single(h.Elements(TestWorkspace.Left));
    }

    [Fact]
    public void CtrlVPastesClipboardImagesAndUndoRemovesThem()
    {
        // ImagePasteTests.testNativePasteActionAndToolbarCommandVUseTheClipboard (logic part).
        using CanvasHarness h = Single();
        h.Clipboard.SetImage(SmallImage);
        Assert.True(h.Key(Main, VirtualKeys.V, KeyModifiers.Ctrl, "v"));
        Assert.Single(h.Elements(Main));
        h.Model.Paste();
        Assert.Equal(2, h.Elements(Main).Length);
        Assert.All(h.Elements(Main), element => Assert.Equal(Tool.Image, element.Tool));
        h.Model.Undo();
        Assert.Single(h.Elements(Main));
    }

    [Fact]
    public void AMissingClipboardImageIsExplicit()
    {
        // ImagePasteTests.testImageTypesAndMissingClipboardAreExplicit (the missing-image message).
        using CanvasHarness h = Single();
        h.Model.PasteImage();
        Assert.Equal("The drawing could not be opened: Copy an image or image file, then choose Paste image. " +
                     "A web link alone does not contain image pixels.", h.Model.Snapshot.ErrorMessage);
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void PastedImagesAreEditablePersistentAndUndoable()
    {
        // ParityRegressionTests.testPastedImageIsEditablePersistentAndUndoable (decoding is the platform's).
        using CanvasHarness h = Single();
        h.Model.InsertImage(new ClipboardImage(SmallImage.Png, 40, 30));
        Element image = Assert.Single(h.Elements(Main));
        Assert.Equal(Tool.Image, image.Tool);
        Assert.Equal([image.Id], h.Document(Main).Selection);
        Assert.Equal(Tool.Select, h.Model.Snapshot.Tool);
        Assert.Equal(SmallImage.Png, DrawingFile.Read(new DrawingFile(h.Document(Main)).Data()).Elements[0].ImagePng);
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void LargeImagesFitInSixHundredByFourHundredFiftyPointsUnderThePointer()
    {
        using CanvasHarness h = Single();
        h.DesktopPointer = new Point(1500, 1000);
        h.Model.InsertImage(new ClipboardImage(SmallImage.Png, 1200, 900));
        Assert.Equal(new Rect(1000, 750, 600, 450), h.Elements(Main)[0].Frame);
        Assert.Equal(new Rect(100, 50, 300, 200), PastePlacement.Image(300, 200, new Rect(0, 0, 800, 600), new Point(100, 50)));
    }

    [Fact]
    public void PasteOpensDrawingWhenTheWorkspaceIsInUseApps()
    {
        using CanvasHarness h = Single();
        _ = h.Clipboard.WriteAnnotations(new DrawingFile(new CanvasDocument(ElementList.Create([new Element(Tool.Rectangle, new Rect(1, 1, 10, 10))]))).Data());
        h.Model.UseApps();
        h.Model.Paste();
        Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);
        Assert.Single(h.Elements(Main));
    }
}
