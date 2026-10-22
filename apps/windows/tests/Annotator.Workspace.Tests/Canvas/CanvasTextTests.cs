using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// Text placement, commit and cancel behind the editor host, and shape labels (ShapeLabelInteractionTests,
/// SelectedContentEditingTests and AnnotationClipboardTests). The editor itself (TSF, spike S-7) is a later task, so
/// these tests type by replacing the editor's whole text. A real double-click sends its first click too, which selects
/// the object first, as on the Mac.
/// </summary>
public sealed class CanvasTextTests
{
    private static Style TextStyle(double size = 28, string font = "system", bool bold = false, TextAlignment alignment = TextAlignment.Left,
                                   RGBA? color = null, double opacity = 1)
    {
        var style = Style.Defaults(Tool.Text);
        style.FontSize = size;
        style.FontName = font;
        style.Bold = bold;
        style.Alignment = alignment;
        style.Color = color ?? style.Color;
        style.Opacity = opacity;
        return style;
    }

    [Fact]
    public void EmptySpaceDoubleClickUsesSavedTextSettingsAndUpdatesProperties()
    {
        // ShapeLabelInteractionTests.testEmptySpaceDoubleClickUsesSavedTextSettingsAndUpdatesProperties.
        Style expected = TextStyle(48, "monospace", bold: true, TextAlignment.Right, Palette.Blue, 0.6);
        var rectangle = new Style { Color = Palette.Coral, FontSize = 16 };
        using CanvasHarness h = Single(Store((Tool.Text, expected), (Tool.Rectangle, rectangle)));
        h.Choose(Tool.Select);
        Assert.NotEqual(expected, h.Model.Snapshot.Style);
        h.Click(Main, new Point(100, 100), count: 2);

        CanvasTextSession session = Assert.IsType<CanvasTextSession>(h.Editor.Session);
        Assert.False(session.IsLabel);
        Assert.Equal(expected, session.Style);
        Assert.Equal(TextFont.Resolve(expected), TextFont.Resolve(session.Style));
        Assert.Equal(expected, h.Model.Snapshot.Style);
        Assert.Equal(Tool.Text, h.Model.Snapshot.EffectiveTool);
        Assert.Equal(EditKind.Text, h.Model.Display(Main).ActiveEdit);
        Assert.Equal("Annotation text. Escape or Ctrl+Enter finishes editing.", session.AccessibilityLabel);

        h.Type("Saved text style");
        Assert.Equal(1, h.Model.Snapshot.SelectionCount);
        h[Main].CommitText();
        h.Pump();
        Element text = Assert.Single(h.Elements(Main));
        Assert.Equal(expected, text.Style);
        Assert.Equal("Saved text style", text.Text);
        Assert.Equal(new Rect(100, 100, 0.5 * 48 * 16 + 2, Math.Ceiling(48 * 1.2) + 2), text.Frame);
        Assert.Null(h.Editor.Session);
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));
        h.Model.Redo();
        Assert.Equal(expected, h.Elements(Main)[0].Style);
    }

    [Fact]
    public void DoubleClickStyleChangesAreSharedWithTextToolAndPersisted()
    {
        // ShapeLabelInteractionTests.testDoubleClickStyleChangesAreSharedWithTextToolAndPersisted.
        var store = new MemoryPreferencesStore();
        Style expected;
        using (CanvasHarness h = Single(store))
        {
            h.Choose(Tool.Select);
            h.Click(Main, new Point(100, 100), count: 2);
            Assert.NotNull(h.Editor.Session);
            h.Model.ApplyStyle((ref Style style) =>
            {
                style.FontSize = 52;
                style.Bold = true;
                style.Color = Palette.Blue;
            });
            h.Pump();
            expected = h.Model.Snapshot.Style;
            Assert.Equal(52, expected.FontSize);
            Assert.Equal(expected, h.Editor.Session!.Style);
            h[Main].CommitText();
            h.Pump();
            Assert.Empty(h.Elements(Main));

            h.Choose(Tool.Text);
            Assert.Equal(expected, h.Model.Snapshot.Style);
            h.Click(Main, new Point(200, 100));
            Assert.Equal(expected, h.Editor.Session!.Style);
            h.Type("Same text tool");
            h[Main].CommitText();
            h.Pump();
            Assert.Equal(expected, h.Elements(Main)[0].Style);
            h.Choose(Tool.Select);
            h.Click(Main, new Point(100, 250), count: 2);
            Assert.Equal(expected, h.Editor.Session!.Style);
            h[Main].CommitText();
            h.Pump();
        }
        Assert.Equal(expected, WorkspacePreferences.Read(store.Data!).ToolStyles[Tool.Text.RawValue]);
        using CanvasHarness restored = Single(store);
        restored.Choose(Tool.Select);
        restored.Click(Main, new Point(100, 100), count: 2);
        Assert.Equal(expected, restored.Editor.Session!.Style);
    }

    [Fact]
    public void NewTextDoesNotInheritPreviousSelectionAndExistingTextKeepsItsStyle()
    {
        // ShapeLabelInteractionTests.testNewTextDoesNotInheritPreviousSelectionAndExistingTextKeepsItsStyle.
        using CanvasHarness h = Single();
        h.Choose(Tool.Text);
        h.Model.ApplyStyle((ref Style style) =>
        {
            style.FontSize = 42;
            style.Color = Palette.Blue;
        });
        Style expected = h.Model.Snapshot.Style;
        var original = new Element(Tool.Text, new Rect(50, 50, 180, 50), text: "Original");
        original.Style.FontSize = 20;
        original.Style.Color = Palette.Coral;
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([original], original.Id));
        h.Pump();
        Assert.Equal(original.Style, h.Model.Snapshot.Style);
        h.Click(Main, new Point(300, 250), count: 2);
        Assert.Equal(expected, h.Editor.Session!.Style);
        h[Main].CommitText();
        h.Click(Main, new Point(80, 70), count: 2);
        Assert.Equal(original.Style, h.Editor.Session!.Style);
        Assert.Equal(original.Style, h.Model.Snapshot.Style);
        h[Main].CommitText();
        h.Pump();
        Element kept = Assert.Single(h.Elements(Main));
        Assert.Equal((original.Id, original.Text, original.Frame, original.Style), (kept.Id, kept.Text, kept.Frame, kept.Style));
        Assert.Equal(expected, h.Model.Snapshot.Preferences.ToolStyles[Tool.Text.RawValue]);
    }

    [Fact]
    public void DoubleClickInsideFilledAndEmptyShapesCreatesOneCenteredOwnerLabel()
    {
        // ShapeLabelInteractionTests.testDoubleClickInsideFilledAndEmptyShapesCreatesOneCenteredOwnerLabel.
        using CanvasHarness h = Single();
        foreach (Tool tool in new[] { Tool.Rectangle, Tool.Ellipse, Tool.Database, Tool.DatabaseStack })
        {
            foreach (bool filled in new[] { false, true })
            {
                var shape = new Element(tool, new Rect(80, 60, 220, 140));
                if (filled)
                {
                    shape.Style.Fill = Palette.Blue;
                }
                h.Choose(Tool.Select);
                h.SetDocument(Main, shape);
                h.Click(Main, new Point(140, 110), count: 2);
                CanvasTextSession session = Assert.IsType<CanvasTextSession>(h.Editor.Session);
                Assert.True(session.IsLabel);
                Assert.Equal(shape.Id, session.ElementId);
                Assert.Equal([shape.Id], h[Main].State.HiddenLabelIds);
                Assert.Equal(Tool.Text, h.Model.Snapshot.EffectiveTool);
                Assert.Single(h.Elements(Main));
                h.Type("Orders");
                Assert.Equal(shape.Frame.Center.X, h.Editor.Session!.Frame.Center.X, 6);
                Assert.Equal(shape.Frame.Center.Y, h.Editor.Session!.Frame.Center.Y, 6);
                h[Main].CommitText();
                h.Pump();
                Element owner = Assert.Single(h.Elements(Main));
                Assert.Equal("Orders", owner.Label?.Text);
                Assert.Equal(shape.Frame, owner.Frame);
                Assert.Equal(shape.Style, owner.Style);
                h.Model.Undo();
                Assert.Null(h.Elements(Main)[0].Label);
                h.Model.Redo();
                Assert.Equal("Orders", h.Elements(Main)[0].Label?.Text);
            }
        }
    }

    [Fact]
    public void TextToolBindsToShapeAndTextStyleDoesNotChangeItsStroke()
    {
        // ShapeLabelInteractionTests.testTextToolBindsToShapeAndTextStyleDoesNotChangeItsStroke.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Database, new Rect(80, 60, 220, 140));
        h.SetDocument(Main, shape);
        h.Choose(Tool.Text);
        h.Click(Main, new Point(140, 110));
        Assert.True(h.Editor.Session!.IsLabel);
        h.Type("Customer records");
        h.Model.ApplyStyle((ref Style style) =>
        {
            style.FontSize = 44;
            style.Color = Palette.Blue;
            style.Bold = true;
        });
        h.Pump();
        Assert.Equal(44, h.Editor.Session!.Style.FontSize);
        h[Main].CommitText();
        h.Pump();
        Element owner = h.Elements(Main)[0];
        Assert.Equal(shape.Style, owner.Style);
        Assert.Equal(44, owner.Label?.Style.FontSize);
        Assert.Equal(Palette.Blue, owner.Label?.Style.Color);
        Assert.False(owner.Label?.FollowsStroke);
        Assert.Equal(shape.Frame.Center, owner.Frame.Center);
        Assert.True(owner.Frame.Width > shape.Frame.Width);

        var measurer = new FixedTextMeasurer();
        CanvasDocument document = h.Document(Main);
        document.EditSelection((ref Element element) => element.Translate(new Point(25, 40)));
        Element moved = document.Elements[0];
        Assert.Equal(moved.Frame.Center, ShapeLabelLayout.TextElement(moved, measurer)?.Frame.Center);
        document.EditSelection((ref Element element) =>
            element.Resize(new Rect(moved.Frame.X, moved.Frame.Y, moved.Frame.Width * 1.5, moved.Frame.Height * 1.5)));
        Element resized = document.Elements[0];
        Assert.Equal(resized.Frame.Center, ShapeLabelLayout.TextElement(resized, measurer)?.Frame.Center);
        Assert.Equal(66, resized.Label?.Style.FontSize);
    }

    [Fact]
    public void CancelLockedObjectsAndRotatedLabelCenters()
    {
        // ShapeLabelInteractionTests.testCancelLockedObjectsAndRotatedLabelCenters.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Database, new Rect(80, 60, 220, 140)) { Rotation = 0.4 };
        h.SetDocument(Main, shape);
        h.Choose(Tool.Select);
        h.Click(Main, shape.Frame.Center, count: 2);
        h.Type("Orders");
        CanvasTextSession session = h.Editor.Session!;
        Assert.Equal(shape.Frame.Center.X, session.Frame.Center.X, 6);
        Assert.Equal(shape.Frame.Center.Y, session.Frame.Center.Y, 6);
        Assert.Equal(0.4, session.Rotation);
        Assert.Equal(CanvasCursor.IBeam, h[Main].HoverCursor(shape.Frame.Center));
        h[Main].CancelGesture();
        h.Pump();
        Assert.Null(h.Editor.Session);
        Element kept = Assert.Single(h.Elements(Main));
        Assert.Null(kept.Label);
        Assert.Equal((shape.Frame, shape.Rotation), (kept.Frame, kept.Rotation));

        shape.Locked = true;
        h.SetDocument(Main, shape);
        h.Click(Main, shape.Frame.Center, count: 2);
        Assert.False(h[Main].HasTextEditor);
        Assert.Null(h.Elements(Main)[0].Label);
        Assert.Equal("Unlock this object to edit its text.", h.Model.Snapshot.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("\u00a0\n")]
    public void BlankNewTextIsDiscardedAcrossCommitRoutesWithoutChangingHistory(string value)
    {
        // SelectedContentEditingTests.testBlankNewTextIsDiscardedAcrossCommitRoutesWithoutChangingHistory.
        using CanvasHarness h = Single();
        foreach (string route in new[] { "escape", "commit", "tool", "elsewhere", "useApps" })
        {
            var original = new Element(Tool.Rectangle, new Rect(10, 10, 40, 40));
            var undone = new Element(Tool.Ellipse, new Rect(500, 400, 40, 40));
            h.SetDocument(Main, original);
            h.Add(Main, [undone]);
            h.Model.Undo();
            h.Choose(Tool.Text);
            TestWorkspace.AssertAccepted(h.Model.Edit(Main, document => document[0].Select([original.Id])));
            long bytes = h.Document(Main).HistoryBytes;
            h.Down(Main, new Point(150, 150));
            Assert.True(h[Main].HasTextEditor, route);
            h.Type(value);
            switch (route)
            {
                case "escape":
                    Assert.True(h.Key(Main, VirtualKeys.Escape));
                    break;
                case "tool":
                    h.Choose(Tool.Select);
                    break;
                case "elsewhere":
                    h.Down(Main, new Point(350, 300));
                    Assert.True(h[Main].HasTextEditor);
                    Assert.Single(h.Elements(Main));
                    h[Main].CommitText();
                    h.Pump();
                    break;
                case "useApps":
                    h.Model.UseApps();
                    h.Pump();
                    break;
                default:
                    h[Main].CommitText();
                    h.Pump();
                    break;
            }
            Assert.False(h[Main].HasTextEditor, route);
            Assert.False(h.Document(Main).IsEditing, route);
            Assert.Equal([original], h.Elements(Main));
            Assert.Equal([original.Id], h.Document(Main).Selection);
            Assert.False(h.Document(Main).CanUndo, route);
            Assert.True(h.Document(Main).CanRedo, route);
            Assert.Equal(bytes, h.Document(Main).HistoryBytes);
            Assert.Equal([original], DrawingFile.Read(new DrawingFile(h.Document(Main)).Data()).Elements);
            Assert.Single(new SavedShape("Original only", h.Document(Main)).Drawing.Elements);
            h.Model.Redo();
            Assert.Equal([original, undone], h.Elements(Main));
        }
    }

    [Theory]
    [InlineData("Existing text")]
    [InlineData("")]
    [InlineData(" \n")]
    public void EnteringAndLeavingExistingTextDoesNotDeleteItOrAddHistory(string value)
    {
        // SelectedContentEditingTests.testEnteringAndLeavingExistingTextDoesNotDeleteItOrAddHistory.
        using CanvasHarness h = Single();
        var original = new Element(Tool.Text, new Rect(100, 100, 200, 100), text: value);
        h.SetDocument(Main, original);
        h.Choose(Tool.Text);
        h.Down(Main, new Point(130, 130));
        Assert.True(h[Main].HasTextEditor);
        Assert.DoesNotContain(h[Main].State.Elements, element => element.Id == original.Id);
        h[Main].CommitText();
        h.Pump();
        Assert.Equal([original], h.Elements(Main));
        Assert.Equal([original.Id], h.Document(Main).Selection);
        Assert.False(h.Document(Main).CanUndo);
    }

    [Fact]
    public void ClearingExistingTextKeepsObjectAndCanBeUndone()
    {
        // SelectedContentEditingTests.testClearingExistingTextKeepsObjectAndCanBeUndone.
        using CanvasHarness h = Single();
        var original = new Element(Tool.Text, new Rect(100, 100, 200, 100), text: "Before");
        h.SetDocument(Main, original);
        h.Choose(Tool.Text);
        h.Down(Main, new Point(130, 130));
        h.Type(string.Empty);
        h[Main].CommitText();
        h.Pump();
        Assert.Equal([original.Id], h.Elements(Main).Select(element => element.Id));
        Assert.Equal(string.Empty, h.Elements(Main)[0].Text);
        h.Model.Undo();
        Assert.Equal([original], h.Elements(Main));
    }

    [Fact]
    public void TypingThenClearingNewTextLeavesNoObjectOrUndoStep()
    {
        // SelectedContentEditingTests.testTypingThenClearingNewTextLeavesNoObjectOrUndoStep.
        using CanvasHarness h = Single();
        h.Choose(Tool.Text);
        h.Down(Main, new Point(150, 150));
        h.Type("Temporary");
        h.Type(string.Empty);
        h[Main].CommitText();
        h.Pump();
        Assert.Empty(h.Elements(Main));
        Assert.False(h.Document(Main).CanUndo);
    }

    [Fact]
    public void EscapeCommitsNewTextAndLabelWithOneUndo()
    {
        // SelectedContentEditingTests.testEscapeCommitsNewTextAndLabelWithOneUndo.
        using CanvasHarness h = Single();
        foreach (bool label in new[] { false, true })
        {
            var shape = new Element(Tool.Rectangle, new Rect(100, 100, 200, 120));
            if (label)
            {
                h.SetDocument(Main, shape);
            }
            else
            {
                h.SetDocument(Main);
            }
            h.Choose(Tool.Text);
            h.Down(Main, new Point(150, 150));
            h.Type("Keep this text");
            Assert.True(h.Key(Main, VirtualKeys.Escape));
            Assert.False(h[Main].HasTextEditor);
            Assert.Equal("Keep this text", label ? h.Elements(Main)[0].Label?.Text : h.Elements(Main)[0].Text);
            Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);
            h.Model.Undo();
            if (label)
            {
                Assert.Null(h.Elements(Main)[0].Label);
            }
            else
            {
                Assert.Empty(h.Elements(Main));
            }
            h.Model.Redo();
            Assert.Equal("Keep this text", label ? h.Elements(Main)[0].Label?.Text : h.Elements(Main)[0].Text);
        }
    }

    [Fact]
    public void CanvasEscapeAndCtrlEnterCommitExistingTextInsteadOfDiscardingIt()
    {
        // SelectedContentEditingTests.testCanvasAndZoomEscapeCommitExistingTextInsteadOfDiscardingIt (canvas route).
        using CanvasHarness h = Single();
        foreach (bool escape in new[] { true, false })
        {
            var original = new Element(Tool.Text, new Rect(100, 100, 200, 100), text: "Before");
            h.SetDocument(Main, original);
            h.Choose(Tool.Text);
            h.Down(Main, new Point(130, 130));
            h.Type("After");
            Assert.True(escape ? h.Key(Main, VirtualKeys.Escape) : h.Key(Main, VirtualKeys.Enter, KeyModifiers.Ctrl));
            Assert.False(h[Main].HasTextEditor);
            Assert.Equal("After", h.Elements(Main)[0].Text);
            Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);
            h.Model.Undo();
            Assert.Equal("Before", h.Elements(Main)[0].Text);
        }
    }

    [Fact]
    public void InterruptionsKeepTheTextAndLabelTyped()
    {
        // Mac commitTextIfNeeded on Use apps, tool choice and Select all: the workspace keeps what the canvas reported.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 200, 120));
        h.SetDocument(Main, shape);
        h.Choose(Tool.Text);
        h.Down(Main, new Point(150, 150));
        h.Type("Orders");
        h.Model.UseApps();
        h.Pump();
        Assert.False(h[Main].HasTextEditor);
        Assert.Equal(1, h.Editor.Ended);
        Assert.Equal("Orders", h.Elements(Main)[0].Label?.Text);
        Assert.Equal(shape.Frame, h.Elements(Main)[0].Frame);

        h.Choose(Tool.Text);
        h.Down(Main, new Point(500, 500));
        h.Type("Note");
        Assert.True(h.Key(Main, VirtualKeys.A, KeyModifiers.Ctrl, "a"));
        Assert.Equal("Note", h.Elements(Main)[1].Text);
        Assert.Equal(2, h.Model.Snapshot.SelectionCount);
        h.Model.Undo();
        Assert.Single(h.Elements(Main));
    }

    [Fact]
    public void DoubleClickOnTextInsideANewerShapeEditsThatText()
    {
        // AnnotationClipboardTests.testDoubleClickOnTextInsideANewerShapeEditsThatText.
        using CanvasHarness h = Single();
        var text = new Element(Tool.Text, new Rect(100, 100, 80, 30), text: "Label", style: Style.Defaults(Tool.Text));
        var box = new Element(Tool.Rectangle, new Rect(60, 60, 220, 140));
        h.SetDocument(Main, text, box);
        h.Choose(Tool.Select);
        h.Click(Main, new Point(120, 110));
        Assert.Equal([text.Id], h.Document(Main).Selection);
        Assert.Equal(Tool.Text, h.Model.Snapshot.EffectiveTool);
        h.Click(Main, new Point(120, 110), count: 2);
        Assert.True(h[Main].HasTextEditor);
        Assert.False(h.Editor.Session!.IsLabel, "The text object is edited, not a new label on the box.");
        h.Model.ApplyStyle((ref Style style) => style.FontSize = 48);
        h.Pump();
        h[Main].CommitText();
        h.Pump();
        Assert.Equal(48, h.Elements(Main).Single(element => element.Id == text.Id).Style.FontSize);
        Assert.Null(h.Elements(Main).Single(element => element.Id == box.Id).Label);

        h.Click(Main, new Point(250, 180), count: 2);
        Assert.Equal(box.Id, h.Editor.Session?.ElementId);
        Assert.True(h.Editor.Session!.IsLabel, "Empty box space still edits the box label.");
        h[Main].CancelGesture();
    }

    /// <summary>The label the canvas edits now (Mac session.document during text editing).</summary>
    private static ShapeLabel EditedLabel(CanvasHarness h, Guid owner)
    {
        CanvasDocument document = h[Main].Document ?? throw new InvalidOperationException("The canvas has no document.");
        return document.Elements[document.Elements.IndexOf(owner)].Label ?? throw new InvalidOperationException("The shape has no label.");
    }

    [Fact]
    public void GradientLabelUsesOwnerPaintWhileTypingAndKeepsExplicitOverrides()
    {
        // ColorWorkflowTests.testGradientLabelUsesOwnerPaintWhileTypingAndKeepsExplicitOverrides, with the stroke color
        // popover's Match outline (Mac matchLabelToStroke and editingLabelFollowsStroke).
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(60, 60, 440, 110));
        shape.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        shape.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: true);
        h.SetDocument(Main, shape);
        h.Choose(Tool.Text);
        Assert.False(h.Model.Snapshot.EditingShapeLabel);
        h.Click(Main, shape.Frame.Center);
        h.Type("Make your point");
        CanvasTextSession session = h.Editor.Session!;
        Assert.Equal(shape.Style.StrokeGradient, session.Style.StrokeGradient);
        Assert.Equal(shape.Frame.Width, session.PaintFrame.Width, 2);
        Assert.True(EditedLabel(h, shape.Id).FollowsStroke);
        Assert.True(h.Model.Snapshot.EditingShapeLabel);
        Assert.True(h.Model.Snapshot.EditingLabelFollowsStroke);

        h.Model.ApplyStyle((ref Style style) => style.Bold = true);
        h.Pump();
        Assert.True(EditedLabel(h, shape.Id).FollowsStroke);
        Assert.True(h.Model.Snapshot.EditingLabelFollowsStroke);
        h.Model.ApplyStyle((ref Style style) => style.SetPaintColor(Palette.Blue, fill: false));
        h.Pump();
        Assert.Null(h.Editor.Session!.Style.StrokeGradient);
        Assert.False(EditedLabel(h, shape.Id).FollowsStroke);
        Assert.False(h.Model.Snapshot.EditingLabelFollowsStroke);
        Assert.True(h.Model.Snapshot.EditingShapeLabel);
        Style own = EditedLabel(h, shape.Id).Style;

        h.Model.MatchLabelToStroke();
        // The toolbar shows the stroke's paint at once (Mac synchronize after matchLabelToStroke); the canvas follows.
        Assert.Equal(shape.Style.Color, h.Model.Snapshot.Style.Color);
        Assert.Equal(shape.Style.StrokeGradient, h.Model.Snapshot.Style.StrokeGradient);
        Assert.True(h.Model.Snapshot.Style.Bold);
        Assert.True(h.Model.Snapshot.EditingLabelFollowsStroke);
        h.Pump();
        Assert.Equal(shape.Style.StrokeGradient, h.Editor.Session!.Style.StrokeGradient);
        Assert.Equal(shape.Frame, h.Editor.Session!.PaintFrame);
        ShapeLabel matched = EditedLabel(h, shape.Id);
        Assert.True(matched.FollowsStroke);
        Assert.Equal(own, matched.Style);
        Assert.True(h.Model.Snapshot.EditingLabelFollowsStroke);

        h.Model.ApplyStyle((ref Style style) => style.SetPaintColor(Palette.Blue, fill: false));
        h.Pump();
        h[Main].CommitText();
        h.Pump();
        Element owner = h.Elements(Main)[0];
        Assert.Equal(shape.Style, owner.Style);
        Assert.Equal(Palette.Blue, owner.Label?.Style.Color);
        Assert.False(owner.Label?.FollowsStroke);
        Assert.False(h.Model.Snapshot.EditingShapeLabel);
        h.Model.Undo();
        Assert.Null(h.Elements(Main)[0].Label);
    }

    [Fact]
    public void MatchOutlineIsOnlyForShapeLabelsThatDoNotFollowTheStroke()
    {
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Ellipse, new Rect(300, 300, 200, 120));
        h.SetDocument(Main, shape);
        h.Choose(Tool.Text);
        h.Click(Main, new Point(80, 80));
        h.Type("Note");
        Assert.False(h.Editor.Session!.IsLabel);
        Assert.False(h.Model.Snapshot.EditingShapeLabel);
        Assert.False(h.Model.Snapshot.EditingLabelFollowsStroke);
        Style before = h.Model.Snapshot.Style;
        h.Model.MatchLabelToStroke();
        h.Pump();
        Assert.Equal(before, h.Model.Snapshot.Style);
        Assert.Equal(before, h.Editor.Session!.Style);
        h[Main].CommitText();
        h.Pump();

        // A label that follows the stroke already: Match outline changes nothing (the popover shows it unavailable).
        h.Click(Main, shape.Frame.Center);
        h.Type("Orders");
        Assert.True(h.Model.Snapshot.EditingLabelFollowsStroke);
        Style following = h.Model.Snapshot.Style;
        h.Model.MatchLabelToStroke();
        Assert.Equal(following, h.Model.Snapshot.Style);
        h.Pump();
        Assert.True(EditedLabel(h, shape.Id).FollowsStroke);
        h[Main].CommitText();
        h.Pump();
        Assert.True(h.Elements(Main).Single(element => element.Id == shape.Id).Label?.FollowsStroke);
    }

    [Fact]
    public void AMatchedLabelDrawsWhileEditedAsItDrawsOnceCommitted()
    {
        // No jump when editing ends (WIN-INP-09): after Match outline the editor's text uses the stroke gradient across the
        // shape, as the committed label does, on the same baseline and in the same turned frame.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 420, 160)) { Rotation = 0.3 };
        shape.Style.SetGradient(GradientPreset.Ocean.Gradient, fill: false);
        h.SetDocument(Main, shape);
        h.Choose(Tool.Text);
        h.Click(Main, shape.Frame.Center);
        h.Type("Orders\nqueue");
        h.Model.ApplyStyle((ref Style style) =>
        {
            style.SetPaintColor(Palette.Coral, fill: false);
            style.FontSize = 34;
        });
        h.Pump();
        h.Model.MatchLabelToStroke();
        h.Pump();
        var scene = new CanvasScene(new FixedTextMeasurer());
        scene.Update(h[Main].State, MainDisplay.Bounds);
        DisplayList edited = scene.Live.Elements.ToArray().Single(list => list.ElementId == shape.Id);

        h[Main].CommitText();
        h.Pump();
        Element owner = h.Elements(Main).Single(element => element.Id == shape.Id);
        Assert.Equal(shape.Frame, owner.Frame);
        Assert.True(owner.Label?.FollowsStroke);
        scene.Update(h[Main].State, MainDisplay.Bounds);
        DisplayList committed = scene.Scene.Elements.ToArray().Single(list => list.ElementId == shape.Id);

        // The edited list is the label's text inside the shape's turn; the committed list ends with the same items.
        DisplayItem[] text = edited.Items.ToArray();
        DisplayItem[] all = committed.Items.ToArray();
        Assert.Equal(DisplayOp.PushTransform, text[0].Op);
        Assert.Equal(DisplayOp.PushTransform, all[0].Op);
        Assert.Equal(DisplayOp.PopTransform, text[^1].Op);
        Assert.Equal(DisplayOp.PopTransform, all[^1].Op);
        AffineTransform editedTurn = text[0].Transform, committedTurn = all[0].Transform;
        double[] expectedTurn = [committedTurn.A, committedTurn.B, committedTurn.C, committedTurn.D, committedTurn.Tx, committedTurn.Ty];
        double[] editedValues = [editedTurn.A, editedTurn.B, editedTurn.C, editedTurn.D, editedTurn.Tx, editedTurn.Ty];
        for (int index = 0; index < expectedTurn.Length; index++)
        {
            Assert.Equal(expectedTurn[index], editedValues[index], 9);
        }
        DisplayItem[] inner = text[1..^1];
        Assert.Contains(inner, item => item.Op == DisplayOp.DrawText && item.Paint is { Kind: PaintKind.LinearGradient });
        DisplayItem[] tail = all[(all.Length - 1 - inner.Length)..^1];
        for (int index = 0; index < inner.Length; index++)
        {
            Assert.True(tail[index].ContentEquals(inner[index]), $"item {index}: {tail[index]} != {inner[index]}");
        }
    }
}
