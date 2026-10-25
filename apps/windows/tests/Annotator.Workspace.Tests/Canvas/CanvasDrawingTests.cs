using Annotator.Core;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// Drawing gestures: freehand ink, shapes and their Shift rules, lines and arrows, the counter, the ruler and the eraser
/// (Mac DrawingCanvas mouse handling; DrawingFeedbackTests, ReportedInteractionTests, CurvedConstructionPreviewTests and
/// AreaEscapeCancellationTests).
/// </summary>
public sealed class CanvasDrawingTests
{
    [Fact]
    public void PenStrokesKeepEverySampleWithPressureAndAreOneUndoStep()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        h.Down(Main, new Point(10, 10), device: PointerDevice.Pen, pressure: 0.5, hasPressure: true);
        h.Samples(Main, [new(new Point(11, 11), 0.6), new(new Point(12, 12), 0.7), new(new Point(13, 13), 0)], hasPressure: true);

        CanvasLiveState live = h[Main].State;
        Element draft = Assert.IsType<Element>(live.Draft);
        Assert.Equal(Tool.Pen, draft.Tool);
        Assert.Equal(4, draft.Points.Count);
        Assert.True(live.Elements.IsEmpty);
        Assert.True(live.HasActiveEdit);
        Assert.Equal(EditKind.Gesture, h.Model.Display(Main).ActiveEdit);
        Assert.True(h.Model.Snapshot.CanUndo);

        h.Up(Main, new Point(14, 14), pressure: 0, hasPressure: true);

        Element stroke = Assert.Single(h.Elements(Main));
        Assert.Equal([new Point(10, 10), new Point(11, 11), new Point(12, 12), new Point(13, 13), new Point(14, 14)], stroke.Points);
        Assert.Equal([0.5, 0.6, 0.7, 1, 1], stroke.Pressures);
        Assert.Equal(new Rect(10, 10, 4, 4), stroke.Frame);
        Assert.Equal(Tool.Pen, h.Model.Snapshot.Tool);
        Assert.Null(h[Main].State.Draft);
        Assert.False(h[Main].State.HasActiveEdit);
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void MouseStrokesGetInkPressureAndTheReleasePointOnce()
    {
        // Without a pressure-sensitive pen, Ink simulates pressure from speed and tapers both ends (InkPressure).
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        h.Down(Main, new Point(5, 5));
        h.Move(Main, new Point(9, 9));
        h.Move(Main, new Point(40, 9));
        h.Up(Main, new Point(40, 9));
        Element stroke = Assert.Single(h.Elements(Main));
        Assert.Equal([new Point(5, 5), new Point(9, 9), new Point(40, 9)], stroke.Points);
        var ink = new InkPressure(stroke.Style.Width);
        double[] drawn = [ink.Next(new Point(5, 5), 11.0), ink.Next(new Point(9, 9), 11.008), ink.Next(new Point(40, 9), 11.016)];
        Assert.Equal(InkPressure.TaperEnd(stroke.Points, ValueList.Create<double>(drawn), stroke.Style.Width), stroke.Pressures);
        Assert.True(stroke.Pressures[0] < 1 && stroke.Pressures[2] < stroke.Pressures[1]);
    }

    [Fact]
    public void ShiftMakesAHighlighterStrokeStraightUntilShiftIsReleased()
    {
        // Spec 009: Shift straightens the current stroke from its start, snapped to 45 degrees; releasing Shift resumes freehand.
        using CanvasHarness h = Single();
        h.Choose(Tool.Highlighter);
        h.Down(Main, new Point(100, 100));
        h.Move(Main, new Point(150, 120));
        h.Move(Main, new Point(200, 104), KeyModifiers.Shift);
        Element draft = h[Main].State.Draft!.Value;
        Assert.Equal(2, draft.Points.Count);
        Assert.Equal(new Point(100, 100), draft.Points[0]);
        Assert.Equal(100, draft.Points[1].Y, 6);
        Assert.Equal(100 + new Point(100, 4).Distance(Point.Zero), draft.Points[1].X, 6);
        Assert.True(draft.Pressures.IsEmpty);

        h.Move(Main, new Point(260, 140));
        Assert.Equal(3, h[Main].State.Draft!.Value.Points.Count);
        Assert.Equal(3, h[Main].State.Draft!.Value.Pressures.Count);

        h.Up(Main, new Point(300, 300), KeyModifiers.Shift);
        Element line = Assert.Single(h.Elements(Main));
        Assert.Equal(2, line.Points.Count);
        Assert.Equal(new Point(100, 100), line.Points[0]);
        Assert.Equal(line.Points[1].X, line.Points[1].Y, 6);
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void ShapesFollowTheDragAndShiftMakesThemSquare()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Rectangle);
        h.Down(Main, new Point(100, 100));
        h.Move(Main, new Point(160, 130));
        Assert.Equal(new Rect(100, 100, 60, 30), h[Main].State.Draft!.Value.Frame);
        Assert.True(h[Main].State.Elements.IsEmpty);
        h.Move(Main, new Point(40, 130), KeyModifiers.Shift);
        Assert.Equal(new Rect(40, 100, 60, 60), h[Main].State.Draft!.Value.Frame);
        h.Up(Main, new Point(40, 130), KeyModifiers.Shift);

        Element shape = Assert.Single(h.Elements(Main));
        Assert.Equal(new Rect(40, 100, 60, 60), shape.Frame);
        Assert.Equal([shape.Id], h.Document(Main).Selection);
        Assert.Equal(Tool.Select, h.Model.Snapshot.Tool);
    }

    [Fact]
    public void SystemDesignShapesKeepTheirAspectRatio()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Mobile);
        h.Drag(Main, new Point(100, 100), new Point(300, 200));
        Element phone = Assert.Single(h.Elements(Main));
        Assert.Equal(100, phone.Frame.Height, 6);
        Assert.Equal(70, phone.Frame.Width, 6);
    }

    [Fact]
    public void LinesSnapTo45DegreesAndArrowsAttachWithinEighteenPoints()
    {
        using CanvasHarness h = Single();
        var left = new Element(Tool.Rectangle, new Rect(40, 100, 100, 80));
        var right = new Element(Tool.Ellipse, new Rect(360, 100, 100, 80));
        h.SetDocument(Main, left, right);
        h.Choose(Tool.Line);
        h.Drag(Main, new Point(600, 600), new Point(700, 690), modifiers: KeyModifiers.Shift);
        Element line = h.Elements(Main)[^1];
        Assert.Equal(line.Points[1].X - 600, line.Points[1].Y - 600, 6);

        h.Choose(Tool.Arrow);
        h.Drag(Main, new Point(150, 140), new Point(345, 140));
        Element arrow = h.Elements(Main)[^1];
        Assert.Equal(new Anchor(left.Id, new Point(1, 0.5)), arrow.StartAnchor);
        Assert.Equal(new Anchor(right.Id, new Point(0, 0.5)), arrow.EndAnchor);
        Assert.Equal(left.AnchorPoint(new Point(1, 0.5)), arrow.Points[0]);

        h.Choose(Tool.Arrow);
        h.Drag(Main, new Point(150, 260), new Point(300, 260));
        Element loose = h.Elements(Main)[^1];
        Assert.Null(loose.StartAnchor);
        Assert.Null(loose.EndAnchor);
    }

    [Fact]
    public void ArrowsAndRulersSnapTo45DegreesWithShiftOnEachMove()
    {
        // Mac mouseDragged: Shift snaps connectors and the ruler with snappedLineEnd on every drag event, keeping the
        // distance; the release keeps the last drag's end, and a move without Shift follows the pointer again.
        using CanvasHarness h = Single();
        h.Choose(Tool.Arrow);
        h.Down(Main, new Point(100, 300));
        h.Move(Main, new Point(200, 245), KeyModifiers.Shift);
        Element snapped = h[Main].State.Draft!.Value;
        double length = new Point(100, -55).Distance(Point.Zero);
        Assert.Equal(100 + length * Math.Cos(-Math.PI / 4), snapped.Points[1].X, 9);
        Assert.Equal(300 + length * Math.Sin(-Math.PI / 4), snapped.Points[1].Y, 9);
        h.Move(Main, new Point(200, 245));
        Assert.Equal(new Point(200, 245), h[Main].State.Draft!.Value.Points[1]);
        h.Move(Main, new Point(260, 280), KeyModifiers.Shift);
        h.Up(Main, new Point(262, 250));
        Element arrow = h.Elements(Main)[^1];
        Assert.Equal(arrow.Points[0].Y, arrow.Points[1].Y, 9);
        Assert.Equal(100 + new Point(160, -20).Distance(Point.Zero), arrow.Points[1].X, 9);

        h.Choose(Tool.Ruler);
        h.Drag(Main, new Point(400, 400), new Point(410, 500), modifiers: KeyModifiers.Shift);
        Element ruler = h.Elements(Main)[^1];
        Assert.Equal(ruler.Points[0].X, ruler.Points[1].X, 9);
        Assert.Equal(400 + new Point(10, 100).Distance(Point.Zero), ruler.Points[1].Y, 9);
    }

    [Fact]
    public void ArrowAttachHighlightMatchesTheAttachmentMade()
    {
        // AnnotationClipboardTests.testArrowAttachHighlightMatchesTheAttachmentMade.
        using CanvasHarness h = Single();
        var left = new Element(Tool.Rectangle, new Rect(40, 100, 100, 80));
        var right = new Element(Tool.Ellipse, new Rect(360, 100, 100, 80));
        h.SetDocument(Main, left, right);
        h.Choose(Tool.Arrow);
        CanvasController canvas = h[Main];
        h.Move(Main, new Point(250, 250), held: false);
        Assert.Empty(canvas.State.AttachHints);
        h.Move(Main, new Point(145, 141), held: false);
        Assert.Equal([new Anchor(left.Id, new Point(1, 0.5))], canvas.State.AttachHints);

        h.Down(Main, new Point(145, 141));
        h.Move(Main, new Point(250, 141));
        Assert.Equal([left.Id], canvas.State.AttachHints.Select(anchor => anchor.ElementId));
        h.Move(Main, new Point(356, 139));
        Assert.Equal([new Anchor(left.Id, new Point(1, 0.5)), new Anchor(right.Id, new Point(0, 0.5))], canvas.State.AttachHints);
        h.Up(Main, new Point(356, 139));
        Assert.Empty(canvas.State.AttachHints);
        Element arrow = h.Elements(Main).Single(element => element.Tool == Tool.Arrow);
        Assert.Equal(left.Id, arrow.StartAnchor?.ElementId);
        Assert.Equal(right.Id, arrow.EndAnchor?.ElementId);

        h.Down(Main, new Point(360, 140));
        h.Move(Main, new Point(300, 260));
        Assert.Empty(canvas.State.AttachHints);
        h.Move(Main, new Point(412, 184));
        Assert.Equal([new Anchor(right.Id, new Point(0.5, 1))], canvas.State.AttachHints);
        h.Up(Main, new Point(412, 184));
        Element moved = h.Elements(Main).Single(element => element.Tool == Tool.Arrow);
        Assert.Equal(new Anchor(right.Id, new Point(0.5, 1)), moved.EndAnchor);
        Assert.Empty(canvas.State.AttachHints);
    }

    [Fact]
    public void ArrowEndClicksWithoutDraggingAddNoUndoStep()
    {
        // AnnotationClipboardTests.testReviewEdgeCasesForCutLockedModeAndEndpointClicks (endpoint part).
        using CanvasHarness h = Single();
        var box = new Element(Tool.Rectangle, new Rect(40, 40, 120, 80));
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(200, 250), new(170, 82)]));
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDocumentWithSelection([box, arrow], arrow.Id));
        h.Pump();
        h.Click(Main, new Point(170, 82));
        Assert.False(h.Document(Main).CanUndo);
        Assert.Null(h.Elements(Main)[1].EndAnchor);
    }

    [Fact]
    public void RulersShowTheirLengthInDisplayPixels()
    {
        using CanvasHarness h = new([TestWorkspace.PrimaryDisplay]);
        h.Choose(Tool.Ruler);
        h.Drag(TestWorkspace.Primary, new Point(10, 10), new Point(40, 50));
        Element ruler = Assert.Single(h.Elements(TestWorkspace.Primary));
        Assert.Equal("100 px", ruler.Text);
        Assert.Equal([new Point(10, 10), new Point(40, 50)], ruler.Points);
    }

    [Fact]
    public void CountersNumberUpFromTheLargestValue()
    {
        using CanvasHarness h = Single();
        h.SetDocument(Main, new Element(Tool.Counter, new Rect(0, 0, 36, 36), text: "7"), new Element(Tool.Counter, new Rect(50, 0, 36, 36), text: "x"));
        h.Choose(Tool.Counter);
        h.Click(Main, new Point(200, 200));
        Element counter = h.Elements(Main)[^1];
        Assert.Equal("8", counter.Text);
        Assert.Equal(new Rect(182, 182, 36, 36), counter.Frame);
        Assert.Equal(Tool.Counter, h.Model.Snapshot.Tool);
        h.Model.Undo();
        Assert.Equal(2, h.Elements(Main).Length);

        h.SetDocument(Main, new Element(Tool.Counter, new Rect(0, 0, 36, 36), text: long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        h.Choose(Tool.Counter);
        h.Click(Main, new Point(200, 200));
        Assert.Single(h.Elements(Main));
        Assert.Equal("The drawing could not be opened: The counter value is too large.", h.Model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void EraserTargetsAreDeferredAndCancellationRestoresThem()
    {
        // DrawingFeedbackTests.testEraserTargetsAreDeferredAndCancellationRestoresThem.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(200, 100, 100, 100));
        h.SetDocument(Main, shape);
        h.Choose(Tool.Eraser);
        h.Down(Main, shape.Frame.Center);
        Assert.Equal([shape.Id], h[Main].State.EraserTargets);
        Assert.Contains(shape.Id, h[Main].State.FadedIds);
        Assert.Single(h.Elements(Main));
        h[Main].CancelGesture();
        h.Pump();
        Assert.Empty(h[Main].State.EraserTargets);
        Assert.Single(h.Elements(Main));
        Assert.Null(h.Model.Display(Main).ActiveEdit);
    }

    [Fact]
    public void EraserFadeDeletesOnReleaseAndEndsWithoutFrames()
    {
        // DrawingFeedbackTests.testEraserFadeDeletesOnReleaseAndStopsItsTicker.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(200, 100, 100, 100));
        h.SetDocument(Main, shape);
        h.Choose(Tool.Eraser);
        h.Click(Main, shape.Frame.Center);
        Assert.Empty(h.Elements(Main));
        EraseFade fade = Assert.IsType<EraseFade>(h[Main].State.EraseFade);
        Assert.True(fade.Scales);
        Assert.True(h[Main].State.HasTransitions);
        Assert.True(h[Main].AdvanceTransitions(5));
        Assert.Equal(0.22, fade.FrameAt(0).Alpha, 6);
        Assert.True(h[Main].AdvanceTransitions(5.06));
        Assert.Equal(0.5, h[Main].State.EraseFadeProgress, 6);
        Assert.False(h[Main].AdvanceTransitions(5.2));
        Assert.Null(h[Main].State.EraseFade);
        Assert.False(h[Main].State.HasTransitions);
        h.Model.Undo();
        h.Pump();
        Assert.Equal([shape.Id], h.Elements(Main).Select(element => element.Id));
    }

    [Fact]
    public void UndoEndsTheEraserFadeAndReducedMotionFadesWithoutShrinking()
    {
        // DrawingFeedbackTests.testUndoEndsTheEraserFadeAndRestoresTheObject.
        using CanvasHarness h = Single(reducesMotion: true);
        var shape = new Element(Tool.Ellipse, new Rect(200, 100, 100, 100));
        h.SetDocument(Main, shape);
        h.Choose(Tool.Eraser);
        h.Click(Main, shape.Frame.Center);
        EraseFade fade = Assert.IsType<EraseFade>(h[Main].State.EraseFade);
        Assert.False(fade.Scales);
        Assert.Equal(1, fade.FrameAt(0.5).Scale);
        h.Model.Undo();
        h.Pump();
        Assert.Equal([shape.Id], h.Elements(Main).Select(element => element.Id));
        Assert.Null(h[Main].State.EraseFade);
    }

    [Fact]
    public void EraserDeletesEveryUnlockedCoverAndUndoRestoresIt()
    {
        // ReportedInteractionTests.testEraserDeletesEveryUnlockedCensorAndUndoRestoresIt.
        using CanvasHarness h = Single();
        foreach (Tool tool in new[] { Tool.Cover, Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            var region = new Element(tool, new Rect(100, 100, 100, 100));
            var hidden = new Element(Tool.Rectangle, new Rect(120, 120, 60, 60));
            h.SetDocument(Main, hidden, region);
            h.Choose(Tool.Eraser);
            h.Down(Main, region.Frame.Center);
            Assert.Equal([region.Id], h[Main].State.EraserTargets);
            h.Up(Main, region.Frame.Center);
            Assert.Equal([hidden.Id], h.Elements(Main).Select(element => element.Id));
            Assert.Null(h[Main].State.EraseFade);
            h.Model.Undo();
            Assert.Contains(h.Elements(Main), element => element.Id == region.Id);
        }

        var locked = new Element(Tool.Cover, new Rect(100, 100, 100, 100)) { Locked = true };
        h.SetDocument(Main, locked);
        h.Choose(Tool.Eraser);
        h.Click(Main, locked.Frame.Center);
        Assert.Equal([locked], h.Elements(Main));
    }

    [Fact]
    public void ClickedArrowsAddPointsAndFinishWithEnterOrDoubleClick()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(100, 100));
        Assert.True(h[Main].ConstructingArrow);
        Assert.Equal(EditKind.Path, h.Model.Display(Main).ActiveEdit);
        Assert.Equal("Click to add arrow points. Enter or double-click finishes; Escape cancels.", h.Model.Snapshot.Status);
        Assert.Empty(h.Elements(Main));
        h.Click(Main, new Point(200, 100));
        h.Click(Main, new Point(200, 200));
        Assert.True(h.Key(Main, VirtualKeys.Enter));
        Element arrow = Assert.Single(h.Elements(Main));
        Assert.Equal([new Point(100, 100), new Point(200, 100), new Point(200, 200)], arrow.Points);
        Assert.Equal([arrow.Id], h.Document(Main).Selection);
        Assert.Equal(Tool.Select, h.Model.Snapshot.Tool);
        Assert.False(h[Main].ConstructingArrow);
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));

        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(100, 300));
        h.Click(Main, new Point(300, 300), count: 2);
        Assert.Equal([new Point(100, 300), new Point(300, 300)], Assert.Single(h.Elements(Main)).Points);

        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(100, 400));
        h.Click(Main, new Point(300, 400));
        Assert.True(h.Key(Main, VirtualKeys.Escape));
        Assert.Single(h.Elements(Main));
        Assert.False(h[Main].ConstructingArrow);
        Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);
    }

    [Fact]
    public void ChoosingAToolFinishesAClickedArrowAndOtherInterruptionsCancelIt()
    {
        using CanvasHarness h = Single();
        var target = new Element(Tool.Rectangle, new Rect(300, 80, 100, 80));
        h.SetDocument(Main, target);
        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(100, 120));
        h.Click(Main, new Point(296, 120));
        h.Choose(Tool.Pen);
        Element arrow = h.Elements(Main).Single(element => element.Tool == Tool.Arrow);
        Assert.Equal(new Anchor(target.Id, new Point(0, 0.5)), arrow.EndAnchor);
        Assert.False(h[Main].ConstructingArrow);
        Assert.Equal(Tool.Pen, h.Model.Snapshot.Tool);

        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(100, 500));
        h.Click(Main, new Point(200, 500));
        h.Model.UseApps();
        h.Model.BeginDrawing();
        h.Pump();
        Assert.Equal(2, h.Elements(Main).Length);
        Assert.False(h[Main].ConstructingArrow);
    }

    [Fact]
    public void PendingPointUsesFullCurvedGeometryWithoutEnteringHistory()
    {
        // CurvedConstructionPreviewTests.testPendingPointUsesFullCurvedGeometryWithoutEnteringHistory (model part).
        var curved = Style.Defaults(Tool.Arrow);
        curved.Curved = true;
        using CanvasHarness h = Single(Store((Tool.Arrow, curved)));
        h.Choose(Tool.Arrow);
        foreach (Point point in new[] { new Point(150, 200), new Point(350, 100) })
        {
            h.Click(Main, point);
        }
        var tentative = new Point(450, 350);
        h.Move(Main, tentative, held: false);
        Element draft = Assert.IsType<Element>(h[Main].State.Draft);
        Assert.Equal(3, draft.Points.Count);
        Assert.True(draft.Style.Curved);
        Assert.Equal(2, ConnectorGeometry.Curves(draft.Points).Count);
        Assert.Equal(2, h[Main].State.Elements.Single().Points.Count);
        Assert.Empty(h.Elements(Main));
        Assert.Equal(EditKind.Path, h.Model.Display(Main).ActiveEdit);

        h.Click(Main, tentative);
        h[Main].FinishLinearPath();
        h.Pump();
        Element committed = Assert.Single(h.Elements(Main));
        Assert.Equal(draft.Points, committed.Points);
        Assert.Equal(draft.Style.Curved, committed.Style.Curved);
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void UndoCancelsConstructionBeforeRemovingAnEarlierObject()
    {
        // UniformEditingTests.testUndoCancelsConstructionBeforeRemovingAnEarlierObject.
        using CanvasHarness h = Single();
        var existing = new Element(Tool.Rectangle, new Rect(50, 50, 100, 100));
        h.Add(Main, [existing]);
        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(250, 200));
        Assert.True(h.Model.Snapshot.CanUndo);
        h.Model.Undo();
        h.Pump();
        Assert.Equal([existing.Id], h.Elements(Main).Select(element => element.Id));
        Assert.False(h[Main].HasActiveEdit);
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void DoubleClickingASelectedArrowAddsAndRemovesBendPoints()
    {
        using CanvasHarness h = Single();
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(100, 100), new(300, 100)]));
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDocumentWithSelection([arrow], arrow.Id));
        h.Pump();
        h.Click(Main, new Point(200, 102), count: 2);
        Assert.Equal([new Point(100, 100), new Point(200, 102), new Point(300, 100)], h.Elements(Main)[0].Points);
        h.Click(Main, new Point(201, 103), count: 2);
        Assert.Equal([new Point(100, 100), new Point(300, 100)], h.Elements(Main)[0].Points);
        h.Model.Undo();
        Assert.Equal(3, h.Elements(Main)[0].Points.Count);
    }

    [Fact]
    public void EscapeCancelsAPendingRegionBeforeReleasingDrawingInput()
    {
        // AreaEscapeCancellationTests.testEscapeCancelsPendingRegionBeforeReleasingDrawingInput.
        using CanvasHarness h = Single();
        var completed = new Element(Tool.Rectangle, new Rect(20, 20, 60, 40));
        h.Add(Main, [completed]);
        h.Choose(Tool.Pen);
        Rect? selectedArea = null;
        h[Main].SelectRegion(area => selectedArea = area);
        h.Pump();
        Assert.True(h[Main].HasActiveEdit);
        Assert.Equal("Drag a region. Escape cancels.", h.Model.Snapshot.Status);

        Assert.True(h.Key(Main, VirtualKeys.Escape));

        Assert.False(h[Main].HasActiveEdit);
        Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);
        Assert.Equal([completed], h.Elements(Main));
        Assert.Null(selectedArea);
        Assert.True(h.Model.Snapshot.CanUndo);
        Assert.False(h.Document(Main).IsEditing);

        h.Drag(Main, new Point(120, 120), new Point(180, 160));
        Assert.Equal(2, h.Elements(Main).Length);
        Assert.Equal(Tool.Pen, h.Elements(Main)[^1].Tool);
        Assert.Null(selectedArea);
        h.Model.Undo();
        Assert.Equal([completed], h.Elements(Main));

        h.Key(Main, VirtualKeys.Escape);
        Assert.Equal(WorkspaceMode.UseApps, h.Model.Snapshot.Mode);
        Assert.Equal([completed], h.Elements(Main));
    }

    [Fact]
    public void ARegionDragReportsTheChosenArea()
    {
        using CanvasHarness h = Single();
        Rect? chosen = null;
        h[Main].SelectRegion(area => chosen = area);
        h.Pump();
        h.Down(Main, new Point(300, 200));
        h.Move(Main, new Point(100, 260));
        Assert.Equal(new Rect(100, 200, 200, 60), h[Main].State.RegionSelection);
        Assert.Null(h[Main].State.Marquee);
        h.Up(Main, new Point(100, 260));
        Assert.Equal(new Rect(100, 200, 200, 60), chosen);
        Assert.False(h[Main].HasActiveEdit);
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void PenStrokesReportOnlyTheirNewSegmentsAsDamage()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        var changes = new List<CanvasChange>();
        h[Main].Changed += changes.Add;
        h.Down(Main, new Point(100, 100));
        changes.Clear();
        h.Samples(Main, [new(new Point(110, 100)), new(new Point(120, 105))], pump: false);
        CanvasChange change = Assert.Single(changes);
        // Width 3 pen: padding 3 + 0 + 3 around the segments, rounded outward.
        Assert.Equal(new Rect(94, 94, 32, 17), change.Dirty);
        h.Up(Main, new Point(120, 105));

        h.Model.SetSmartDraw(true);
        h.Pump();
        h.Down(Main, new Point(300, 300));
        changes.Clear();
        h.Move(Main, new Point(310, 300), pump: false);
        Assert.Null(changes[0].Dirty);
    }

    [Fact]
    public void ToolKeysAndInkKeysUseTheCharacterWithoutModifiers()
    {
        using CanvasHarness h = Single();
        Assert.True(h.Key(Main, '7', text: "7"));
        Assert.Equal(Tool.Pen, h.Model.Snapshot.Tool);
        Assert.True(h.Key(Main, 'B', text: "b"));
        Assert.Equal(Palette.Blue, h.Model.Snapshot.Style.Color);
        Assert.Equal("Blue ink", h.Model.Snapshot.Status);
        Assert.True(h.Key(Main, '2', text: "2"));
        Assert.Equal(Tool.Rectangle, h.Model.Snapshot.Tool);
        Assert.False(h.Key(Main, 'J', text: "j"));
        Assert.False(h.Key(Main, '7', KeyModifiers.Alt, "7"));
    }

    internal static CanvasDocument CanvasDocumentWithSelection(Element[] elements, params Guid[] selection)
    {
        var document = new CanvasDocument(ElementList.Create(elements));
        document.Select(selection);
        return document;
    }
}
