using Annotator.Core;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// The canvas side of MultiDisplayEditingTests (spec 002, ADR 0006): drags between displays with previews, transfer
/// failures and disconnects, workspace-wide keys, the context menu on another display, drawing styles on another display,
/// boards and cursors. Display 101 (Primary) is at the origin with scale 2; display 202 (Left) is to its left with scale 1.
/// </summary>
public sealed class CanvasMultiDisplayTests
{
    private static Point LocalPoint(DisplayDescriptor display, Point desktop) => display.ToLocal(desktop);

    [Fact]
    public void DragAcrossDisplaysPreviewsTransfersAndUndoesWithoutChangingScreenPosition()
    {
        // testDragAcrossDisplaysPreviewsTransfersAndUndoesWithoutChangingScreenPosition.
        using CanvasHarness h = new();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80)) { Rotation = 0.2 };
        shape.Style.Color = Palette.Coral;
        var existing = new Element(Tool.Ellipse, new Rect(500, 200, 80, 60));
        h.Add(Primary, [shape]);
        h.Add(Left, [existing]);
        h.Choose(Tool.Select);
        Point start = shape.Frame.Center;
        var screenEnd = new Point(-300, 350);
        Point finish = LocalPoint(PrimaryDisplay, screenEnd);

        h.Down(Primary, start);
        h.Move(Primary, finish);
        CanvasLiveState destination = h.Left.State;
        Assert.Equal(2, destination.Elements.Count);
        Assert.Equal([existing], h.Elements(Left));
        Assert.Equal(Primary, h.Model.Snapshot.ActiveDisplay);
        Element preview = destination.Elements.Single(element => element.Id == shape.Id);
        Assert.Equal(LocalPoint(LeftDisplay, screenEnd).X, preview.Frame.Center.X, 6);
        Assert.Equal(LocalPoint(LeftDisplay, screenEnd).Y, preview.Frame.Center.Y, 6);
        Assert.Equal([shape.Id], destination.Selection);
        Assert.Equal(shape.Rotation, preview.Rotation);
        Assert.Equal(shape.Style, preview.Style);

        h.Up(Primary, finish);
        Assert.Null(h.Model.Snapshot.ErrorMessage);
        Assert.Empty(h.Elements(Primary));
        Element transferred = h.Elements(Left).Single(element => element.Id == shape.Id);
        Assert.Equal(preview.Frame, transferred.Frame);
        Assert.Equal([existing, transferred], h.Elements(Left));
        Assert.False(h.Document(Left).IsEditing);
        Assert.Equal(Left, h.Model.Snapshot.ActiveDisplay);
        Assert.Equal([shape.Id], h.Document(Left).Selection);
        Assert.Equal(2, h.Left.State.Elements.Count);
        Assert.True(h.Primary.State.Elements.IsEmpty);
        h.Model.Undo();
        Assert.Equal([shape], h.Elements(Primary));
        Assert.Equal([existing], h.Elements(Left));
        h.Model.Redo();
        Assert.Empty(h.Elements(Primary));
        Assert.Equal([existing, transferred], h.Elements(Left));

        Point returnStart = transferred.Frame.Center;
        Point returnEnd = LocalPoint(LeftDisplay, new Point(240, 240));
        h.Down(Left, returnStart);
        h.Up(Left, returnEnd);
        Assert.Equal(Primary, h.Model.Snapshot.ActiveDisplay);
        Assert.Equal([existing], h.Elements(Left));
        Point back = h.Elements(Primary)[0].Frame.Center;
        Assert.Equal(LocalPoint(PrimaryDisplay, new Point(240, 240)).X, back.X, 6);
        Assert.Equal(LocalPoint(PrimaryDisplay, new Point(240, 240)).Y, back.Y, 6);
        h.Model.Undo();
        Assert.Empty(h.Elements(Primary));
        Assert.Equal([existing, transferred], h.Elements(Left));
    }

    [Fact]
    public void CrossDisplayDragCancellationAndOutsideDropRestoreBothDocuments()
    {
        // testCrossDisplayDragCancellationAndOutsideDropRestoreBothDocuments.
        using CanvasHarness h = new();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80));
        h.Add(Primary, [shape]);
        h.Choose(Tool.Select);
        Point end = LocalPoint(PrimaryDisplay, new Point(-300, 350));
        foreach (bool outsideDrop in new[] { false, true })
        {
            h.Down(Primary, shape.Frame.Center);
            h.Move(Primary, end);
            Assert.Single(h.Left.State.Elements);
            if (outsideDrop)
            {
                h.Up(Primary, LocalPoint(PrimaryDisplay, new Point(4000, 4000)));
            }
            else
            {
                h.Model.UseApps();
                h.Model.BeginDrawing();
                h.Pump();
            }
            Assert.Equal(shape.Frame, h.Elements(Primary).Single().Frame);
            Assert.True(h.Left.State.Elements.IsEmpty);
            Assert.Empty(h.Elements(Left));
            Assert.False(h.Document(Left).IsEditing);
            Assert.False(h.Document(Primary).IsEditing);
            Assert.Equal(shape.Frame, h.Primary.State.Elements.Single().Frame);
        }
        h.Model.Undo();
        Assert.Empty(h.Elements(Primary));
    }

    [Fact]
    public void CrossDisplayTransferFailurePreservesObjectsAndHistory()
    {
        // testCrossDisplayTransferFailurePreservesObjectsAndHistory.
        using CanvasHarness h = new();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80));
        var existing = new Element(Tool.Text, text: string.Concat(Enumerable.Repeat("Long text ", 100)));
        h.Add(Primary, [shape]);
        h.Model.ReplaceDocumentForTesting(Left, new CanvasDocument(ElementList.Create([existing]), historyByteLimit: 100));
        h.Choose(Tool.Select);
        h.Down(Primary, shape.Frame.Center);
        h.Up(Primary, LocalPoint(PrimaryDisplay, new Point(-300, 350)));
        Assert.NotNull(h.Model.Snapshot.ErrorMessage);
        Assert.Equal(shape.Frame, h.Elements(Primary).Single().Frame);
        Assert.Equal(shape.Frame, h.Primary.State.Elements.Single().Frame);
        Assert.Equal([existing], h.Elements(Left));
        Assert.Equal(Primary, h.Model.Snapshot.ActiveDisplay);
        Assert.False(h.Document(Primary).IsEditing);
        Assert.False(h.Document(Left).IsEditing);
        Assert.Null(h.Model.Display(Primary).ActiveEdit);
        h.Model.Undo();
        Assert.Empty(h.Elements(Primary));
    }

    [Fact]
    public void DisconnectDuringDragCancelsPreviewsAndResumesOnTheRemainingDisplay()
    {
        // testDisconnectDuringDragCancelsPreviewsAndResumesOnRemainingDisplay.
        using CanvasHarness h = new();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80));
        h.Add(Primary, [shape]);
        h.Choose(Tool.Select);
        h.Down(Primary, shape.Frame.Center);
        h.Move(Primary, LocalPoint(PrimaryDisplay, new Point(-300, 350)));
        Assert.Single(h.Left.State.Elements);
        h.Displays = [PrimaryDisplay];
        h.Model.DisplaysChanged();
        h.Pump();
        Assert.Single(h.Model.Snapshot.Displays);
        Assert.False(h.Primary.HasActiveEdit);
        Assert.True(h.Left.State.Elements.IsEmpty);
        Assert.Equal(shape.Frame, h.Elements(Primary).Single().Frame);
        Assert.False(h.Document(Primary).IsEditing);
        Assert.False(h.Primary.IsDrawing);
        h.Model.BeginDrawing();
        h.Pump();
        Assert.Equal(Primary, h.Model.Snapshot.ActiveDisplay);
        Assert.True(h.Primary.IsDrawing);
    }

    [Fact]
    public void CrossDisplayTransferKeepsGroupsAndInternalBindingsButLeavesLockedObjects()
    {
        // testCrossDisplayTransferKeepsGroupsAndInternalBindingsButLeavesLockedObjects.
        using CanvasHarness h = new();
        Guid group = Guid.NewGuid();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80)) { GroupId = group };
        var locked = new Element(Tool.Ellipse, new Rect(300, 80, 100, 80)) { Locked = true };
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(180, 120), new(300, 120)]))
        {
            GroupId = group,
            StartAnchor = new Anchor(shape.Id, new Point(1, 0.5)),
            EndAnchor = new Anchor(locked.Id, new Point(0, 0.5)),
        };
        h.Choose(Tool.Select);
        h.Add(Primary, [shape, locked, arrow], shape.Id, arrow.Id, locked.Id);
        h.Down(Primary, new Point(120, 120));
        h.Up(Primary, LocalPoint(PrimaryDisplay, new Point(-300, 350)));
        Assert.Null(h.Model.Snapshot.ErrorMessage);
        Assert.Equal([locked.Id], h.Elements(Primary).Select(element => element.Id));
        Assert.Equal(2, h.Elements(Left).Length);
        Element moved = h.Elements(Left).Single(element => element.Id == arrow.Id);
        Assert.Equal(arrow.StartAnchor, moved.StartAnchor);
        Assert.Null(moved.EndAnchor);
        Assert.Equal(group, moved.GroupId);
        h.Model.Undo();
        Assert.Equal(3, h.Elements(Primary).Length);
        Assert.Empty(h.Elements(Left));
    }

    [Fact]
    public void SelectAllFromAnEmptyDisplaySupportsGlobalStylesDeleteAndUndo()
    {
        // testSelectAllFromEmptyDisplaySupportsGlobalStylesDeleteAndUndo (keys through the canvas).
        using CanvasHarness h = new();
        var shape = new Element(Tool.Rectangle, new Rect(30, 40, 100, 80));
        var locked = new Element(Tool.Ellipse, new Rect(230, 40, 100, 80)) { Locked = true };
        h.Add(Left, [shape, locked]);
        Assert.True(h.Key(Primary, VirtualKeys.A, KeyModifiers.Ctrl, "a"));
        Assert.True(h.Model.Snapshot.SelectionIsWorkspaceWide);
        Assert.Equal(2, h.Model.Snapshot.SelectionCount);
        Assert.Equal(Tool.Rectangle, h.Model.Snapshot.EffectiveTool);
        h.Model.BeginStyleEdit();
        h.Model.ApplyStyle((ref Style style) => style.Fill = Palette.Blue);
        h.Model.ApplyStyle((ref Style style) => style.Fill = Palette.Coral);
        h.Model.FinishStyleEdit();
        Assert.Equal(Palette.Coral, h.Elements(Left)[0].Style.Fill);
        Assert.Equal(locked, h.Elements(Left)[1]);
        h.Model.Undo();
        Assert.Equal([shape, locked], h.Elements(Left));
        h.Model.Redo();
        Assert.Equal(Palette.Coral, h.Elements(Left)[0].Style.Fill);
        Assert.True(h.Key(Primary, VirtualKeys.Back));
        Assert.Equal([locked], h.Elements(Left));
        h.Model.Undo();
        Assert.Equal(2, h.Elements(Left).Length);
        Assert.Equal(2, h.Model.Snapshot.SelectionCount);
        Assert.True(h.Model.Snapshot.SelectionIsWorkspaceWide);
    }

    [Fact]
    public void WorkspaceSelectionNudgesAllDisplaysAndCanvasClickReturnsToLocalEditing()
    {
        // testWorkspaceSelectionNudgesAllDisplaysAndCanvasClickReturnsToLocalEditing (keys, menu and click on the canvases).
        using CanvasHarness h = new();
        var a = new Element(Tool.Rectangle, new Rect(50, 50, 100, 80));
        var b = new Element(Tool.Ellipse, new Rect(50, 50, 100, 80));
        h.Add(Primary, [a]);
        h.Add(Left, [b]);
        h.Model.SelectAll();
        Assert.Equal(2, h.Model.Snapshot.SelectionCount);
        Assert.True(h.Key(Primary, VirtualKeys.Right));
        Assert.Equal(a.Frame.X + 1, h.Elements(Primary)[0].Frame.X);
        Assert.Equal(b.Frame.X + 1, h.Elements(Left)[0].Frame.X);
        h.Model.Undo();
        Assert.Equal([a], h.Elements(Primary));
        Assert.Equal([b], h.Elements(Left));

        CanvasMenu? menu = null;
        h.Left.ContextMenuRequested += shown => menu = shown;
        h.Sync();
        h.Down(Left, b.Frame.Center, button: PointerButtons.Secondary);
        Assert.NotNull(menu);
        Assert.True(h.Model.Snapshot.SelectionIsWorkspaceWide);
        h.Left.PerformMenuItem(CanvasMenuCommand.DeleteSelected);
        h.Pump();
        Assert.Empty(h.Elements(Primary));
        Assert.Empty(h.Elements(Left));
        h.Model.Undo();
        Assert.Equal([a], h.Elements(Primary));
        Assert.Equal([b], h.Elements(Left));

        h.Click(Left, b.Frame.Center);
        Assert.False(h.Model.Snapshot.SelectionIsWorkspaceWide);
        Assert.Empty(h.Document(Primary).Selection);
        Assert.Equal([b.Id], h.Document(Left).Selection);
    }

    [Fact]
    public void PointerCursorUsesTheDisplayUnderItWithoutChangingTheEditingTarget()
    {
        // testPointerCursorUsesDestinationDisplayWithoutChangingEditingTarget.
        using CanvasHarness h = new();
        h.Choose(Tool.Highlighter);
        h.Model.ApplyStyle((ref Style style) => style.Width = 4);
        h.Pump();
        DisplayId? active = h.Model.Snapshot.ActiveDisplay;
        var footprint = new CanvasCursor(CanvasCursorKind.Footprint, Tool.Highlighter, 4);
        foreach (DisplayId display in new[] { Primary, Left })
        {
            h.Move(display, new Point(250, 250), held: false);
            Assert.Equal(footprint, h[display].State.Cursor);
            Assert.Equal(active, h.Model.Snapshot.ActiveDisplay);
        }
        h.Model.UseApps();
        h.Pump();
        Assert.Equal(CanvasCursor.Arrow, h.Primary.HoverCursor(new Point(250, 250)));
    }

    [Fact]
    public void EveryDisplayReceivesDrawingAndKeepsItsOwnLocalDocument()
    {
        // testEveryDisplayReceivesDrawingAndKeepsItsOwnLocalDocument (drawing through each canvas).
        using CanvasHarness h = new();
        foreach (DisplayId display in new[] { Left, Primary })
        {
            h.Choose(Tool.Rectangle);
            h.Drag(display, new Point(60, 80), new Point(180, 160));
            Assert.Equal(display, h.Model.Snapshot.ActiveDisplay);
            Assert.Equal(new Rect(60, 80, 120, 80), h.Elements(display).Single().Frame);
        }
        h.Model.Undo();
        Assert.Empty(h.Elements(Primary));
        Assert.Single(h.Elements(Left));
        h.Model.UseApps();
        h.Pump();
        Assert.False(h.Primary.IsDrawing);
        h.Down(Left, new Point(10, 10));
        Assert.False(h.Left.HasActiveEdit);
        Assert.Single(h.Elements(Left));
    }

    [Fact]
    public void DrawingOnAnotherDisplayUsesToolStyleRatherThanItsOldSelection()
    {
        // testDrawingOnAnotherDisplayUsesToolStyleRatherThanItsOldSelection.
        using CanvasHarness h = new();
        var old = new Element(Tool.Rectangle, new Rect(300, 300, 100, 80));
        old.Style.Color = Palette.Blue;
        old.Style.Width = 11;
        h.Add(Left, [old], old.Id);
        AssertAccepted(h.Model.Edit(Primary, _ => { }));
        h.Choose(Tool.Pen);
        h.Model.ApplyStyle((ref Style style) =>
        {
            style.Color = Palette.Coral;
            style.Width = 3;
        });
        h.Drag(Left, new Point(60, 80), new Point(180, 160));
        Element ink = h.Elements(Left)[^1];
        Assert.Equal(Tool.Pen, ink.Tool);
        Assert.Equal(Palette.Coral, ink.Style.Color);
        Assert.Equal(3, ink.Style.Width);
        Assert.Equal(old, h.Elements(Left)[0]);
        Assert.Empty(h.Document(Left).Selection);
    }

    [Fact]
    public void BoardBackgroundsBelongOnlyToTheLastUsedDisplay()
    {
        // testBoardBackgroundsBelongOnlyToTheLastUsedDisplay (model part; the pixels are the presenter's).
        using CanvasHarness h = new();
        h.Model.SetBoard(Board.Whiteboard);
        Assert.Equal(Board.Whiteboard, h.Model.Display(Primary).Board);
        Assert.Equal(Board.Desktop, h.Model.Display(Left).Board);
        h.Choose(Tool.Rectangle);
        h.Drag(Left, new Point(60, 80), new Point(180, 160));
        Assert.Equal(Board.Desktop, h.Model.Snapshot.Board);
        Assert.Equal(Board.Whiteboard, h.Model.Display(Primary).Board);
        h.Model.SetBoard(Board.Blackboard);
        Assert.Equal(Board.Blackboard, h.Model.Display(Left).Board);
        h.Click(Primary, new Point(400, 400));
        Assert.Equal(Board.Whiteboard, h.Model.Snapshot.Board);
        h.Model.SetBoard(Board.Desktop);
        Assert.Equal(Board.Desktop, h.Model.Display(Primary).Board);
        Assert.Equal(Board.Blackboard, h.Model.Display(Left).Board);
        h.Model.HideTools();
        h.Model.ResumeDrawing();
        Assert.Equal(Board.Desktop, h.Model.Display(Primary).Board);
        Assert.Equal(Board.Blackboard, h.Model.Display(Left).Board);
    }

    [Fact]
    public void RightClickSelectsTheCorrectDisplayForActions()
    {
        // testRightClickSelectsTheCorrectDisplayForActions.
        using CanvasHarness h = new();
        var item = new Element(Tool.Rectangle, new Rect(40, 40, 120, 100));
        h.Add(Left, [item]);
        AssertAccepted(h.Model.Edit(Primary, _ => { }));
        h.Choose(Tool.Select);
        Assert.Equal(Primary, h.Model.Snapshot.ActiveDisplay);
        h.Down(Left, item.Frame.Center, button: PointerButtons.Secondary);
        Assert.Equal(Left, h.Model.Snapshot.ActiveDisplay);
        h.Left.PerformMenuItem(CanvasMenuCommand.DeleteSelected);
        h.Pump();
        Assert.Empty(h.Elements(Left));
        h.Model.Undo();
        Assert.Equal([item], h.Elements(Left));
        Assert.Empty(h.Elements(Primary));
    }
}
