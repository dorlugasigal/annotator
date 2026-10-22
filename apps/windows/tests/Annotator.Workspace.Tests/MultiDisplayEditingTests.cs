using Annotator.Core;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

/// <summary>Ports of the model assertions in apps/macos/Tests/NativeAppTests/MultiDisplayEditingTests.swift (spec 002, ADR 0006).</summary>
public sealed class MultiDisplayEditingTests
{
    /// <summary>What a canvas submits when a drag drops objects on another display (Mac finishDisplayDrag).</summary>
    private static EditResult Transfer(WorkspaceModel model, DisplayId from, DisplayId to, IReadOnlySet<Guid> ids, Point offset)
    {
        DisplaySnapshot source = model.Display(from);
        DisplaySnapshot destination = model.Display(to);
        ElementList moved = DisplayTransfer.MovedElements(source.Document, ids, offset);
        Guid operation = Guid.NewGuid();
        CanvasDocument departure = source.Document;
        CanvasDocument arrival = destination.Document;
        departure.Begin();
        departure.Select(moved.Select(element => element.Id));
        departure.DeleteSelection(includingProtection: true);
        arrival.Begin();
        foreach (Element element in moved)
        {
            arrival.Add(element);
        }
        arrival.Select(moved.Select(element => element.Id));
        departure.Commit(operation);
        arrival.Commit(operation);
        return model.Submit(new EditSubmission(
            [new DisplayEdit(to, destination.Version, arrival), new DisplayEdit(from, source.Version, departure)], operation));
    }

    [Fact]
    public void TransferAcrossDisplaysUndoesAndRedoesAsOneStepWithoutChangingScreenPosition()
    {
        // testDragAcrossDisplaysPreviewsTransfersAndUndoesWithoutChangingScreenPosition (the commit, undo and redo).
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80)) { Rotation = 0.2 };
        shape.Style.Color = Palette.Coral;
        var existing = new Element(Tool.Ellipse, new Rect(500, 200, 80, 60));
        AssertAccepted(model.Add(Primary, shape));
        AssertAccepted(model.Add(Left, existing));
        model.Choose(Tool.Select);
        var drop = new Point(-300, 350);
        Point offset = DisplayTransfer.Offset(PrimaryDisplay, LeftDisplay, drop);

        AssertAccepted(Transfer(model, Primary, Left, new HashSet<Guid> { shape.Id }, offset));

        Assert.Null(model.Snapshot.ErrorMessage);
        Assert.Empty(model.Elements(Primary));
        Element transferred = model.Elements(Left).Single(element => element.Id == shape.Id);
        Assert.Equal(shape.Frame.Origin + offset, transferred.Frame.Origin);
        Assert.Equal(shape.Frame.Width, transferred.Frame.Width);
        Assert.Equal(shape.Rotation, transferred.Rotation);
        Assert.Equal(shape.Style, transferred.Style);
        Assert.Equal([existing, transferred], model.Elements(Left));
        Assert.False(model.Document(Left).IsEditing);
        Assert.Equal(Left, model.Snapshot.ActiveDisplay);
        Assert.Equal([shape.Id], model.Document(Left).Selection);

        model.Undo();
        Assert.Equal([shape], model.Elements(Primary));
        Assert.Equal([existing], model.Elements(Left));
        model.Redo();
        Assert.Empty(model.Elements(Primary));
        Assert.Equal([existing, transferred], model.Elements(Left));
    }

    [Fact]
    public void TransferOffsetKeepsTheDropPointOnScreen()
    {
        var drop = new Point(-300, 350);
        Point offset = DisplayTransfer.Offset(PrimaryDisplay, LeftDisplay, drop);
        Assert.Equal(LeftDisplay.ToLocal(drop), PrimaryDisplay.ToLocal(drop) + offset);
        var sameScale = new DisplayDescriptor(new DisplayId("303"), new Rect(1280, 0, 1280, 960), new Rect(1280, 0, 1280, 960), 2);
        Point between = DisplayTransfer.Offset(PrimaryDisplay, sameScale, new Point(1500, 200));
        Assert.Equal(new Point(-640, 0), between);
        Assert.Equal(between, DisplayTransfer.Offset(PrimaryDisplay, sameScale, new Point(10, 900)));
    }

    [Fact]
    public void CancelledMovesAddNoHistory()
    {
        // testCrossDisplayDragCancellationAndOutsideDropRestoreBothDocuments (model side): a cancelled or
        // interrupted edit leaves both documents unchanged and adds no Undo step.
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80));
        AssertAccepted(model.Add(Primary, shape));
        model.Choose(Tool.Select);
        foreach (bool interrupted in new[] { false, true })
        {
            DisplaySnapshot source = model.Display(Primary);
            AssertAccepted(model.BeginEdit(Primary, source.Version, EditKind.Gesture));
            if (interrupted)
            {
                model.UseApps();
                model.BeginDrawing();
            }
            else
            {
                AssertAccepted(model.CancelEdit(Primary, source.Version));
            }
            Assert.Equal([shape], model.Elements(Primary));
            Assert.Empty(model.Elements(Left));
            Assert.False(model.Document(Primary).IsEditing);
            Assert.Null(model.Display(Primary).ActiveEdit);
        }
        model.Undo();
        Assert.Empty(model.Elements(Primary));
    }

    [Fact]
    public void TransferFailurePreservesObjectsAndHistory()
    {
        // testCrossDisplayTransferFailurePreservesObjectsAndHistory.
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80));
        var existing = new Element(Tool.Text, text: string.Concat(Enumerable.Repeat("Long text ", 100)));
        AssertAccepted(model.Add(Primary, shape));
        model.ReplaceDocumentForTesting(Left, new CanvasDocument(ElementList.Create([existing]), historyByteLimit: 100));
        model.Choose(Tool.Select);

        EditResult result = Transfer(model, Primary, Left, new HashSet<Guid> { shape.Id }, new Point(10, 10));

        AssertRejected(result, EditRejection.InvalidHistory);
        Assert.NotNull(model.Snapshot.ErrorMessage);
        Assert.Equal([shape], model.Elements(Primary));
        Assert.Equal([existing], model.Elements(Left));
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        Assert.False(model.Document(Primary).IsEditing);
        Assert.False(model.Document(Left).IsEditing);
        model.Undo();
        Assert.Empty(model.Elements(Primary));
    }

    [Fact]
    public void DisconnectDuringAnEditCancelsItAndResumesOnTheRemainingDisplay()
    {
        // testDisconnectDuringDragCancelsPreviewsAndResumesOnRemainingDisplay.
        DisplayDescriptor[] displays = [PrimaryDisplay, LeftDisplay];
        WorkspaceModel model = Create(displays: () => displays);
        model.BeginDrawing();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80));
        AssertAccepted(model.Add(Primary, shape));
        model.Choose(Tool.Select);
        DisplaySnapshot source = model.Display(Primary);
        AssertAccepted(model.BeginEdit(Primary, source.Version, EditKind.Gesture));

        displays = [PrimaryDisplay];
        model.DisplaysChanged();

        Assert.Single(model.Snapshot.Displays);
        Assert.Equal([shape], model.Elements(Primary));
        Assert.False(model.Document(Primary).IsEditing);
        Assert.Null(model.Display(Primary).ActiveEdit);
        Assert.False(model.Display(Primary).TakesInput);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        CanvasDocument late = source.Document;
        late.Add(new Element(Tool.Ellipse));
        AssertRejected(model.Submit(new EditSubmission(new DisplayEdit(Primary, source.Version, late))), EditRejection.StaleVersion);

        model.BeginDrawing();
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        Assert.True(model.Display(Primary).TakesInput);
    }

    [Fact]
    public void TransferKeepsGroupsAndInternalBindingsButLeavesLockedObjects()
    {
        // testCrossDisplayTransferKeepsGroupsAndInternalBindingsButLeavesLockedObjects.
        WorkspaceModel model = Drawing();
        Guid group = Guid.NewGuid();
        var shape = new Element(Tool.Rectangle, new Rect(60, 80, 120, 80)) { GroupId = group };
        var locked = new Element(Tool.Ellipse, new Rect(300, 80, 100, 80)) { Locked = true };
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(180, 120), new(300, 120)]))
        {
            GroupId = group,
            StartAnchor = new Anchor(shape.Id, new Point(1, 0.5)),
            EndAnchor = new Anchor(locked.Id, new Point(0, 0.5)),
        };
        AssertAccepted(model.Add(Primary, shape, locked, arrow));
        model.Choose(Tool.Select);
        Element storedArrow = model.Elements(Primary).Single(element => element.Id == arrow.Id);

        AssertAccepted(Transfer(model, Primary, Left, new HashSet<Guid> { shape.Id, arrow.Id, locked.Id }, new Point(-500, 50)));

        Assert.Null(model.Snapshot.ErrorMessage);
        Assert.Equal([locked.Id], model.Elements(Primary).Select(element => element.Id));
        Assert.Equal(2, model.Elements(Left).Length);
        Element movedArrow = model.Elements(Left).Single(element => element.Id == arrow.Id);
        Assert.Equal(storedArrow.StartAnchor, movedArrow.StartAnchor);
        Assert.Null(movedArrow.EndAnchor);
        Assert.Equal(group, movedArrow.GroupId);
        model.Undo();
        Assert.Equal(3, model.Elements(Primary).Length);
        Assert.Empty(model.Elements(Left));
    }

    [Fact]
    public void SelectAllFromAnEmptyDisplaySupportsGlobalStylesDeleteAndUndo()
    {
        // testSelectAllFromEmptyDisplaySupportsGlobalStylesDeleteAndUndo.
        WorkspaceModel model = Drawing();
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        var shape = new Element(Tool.Rectangle, new Rect(30, 40, 100, 80));
        var locked = new Element(Tool.Ellipse, new Rect(230, 40, 100, 80)) { Locked = true };
        AssertAccepted(model.Add(Left, shape, locked));
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.SelectAll, Primary)));

        Assert.True(model.Snapshot.SelectionIsWorkspaceWide);
        Assert.Equal(2, model.Snapshot.SelectionCount);
        Assert.Equal([shape.Id, locked.Id], model.Document(Left).Selection.OrderBy(id => id == shape.Id ? 0 : 1));
        Assert.Equal(Tool.Rectangle, model.Snapshot.EffectiveTool);

        model.BeginStyleEdit();
        model.SetPaintColor(Palette.Blue, fill: true, useColorAlpha: true);
        model.SetPaintColor(Palette.Coral, fill: true, useColorAlpha: true);
        model.FinishStyleEdit();
        Element[] styled = model.Elements(Left);
        Assert.Equal(Palette.Coral, styled[0].Style.Fill);
        Assert.Equal(locked, styled[1]);

        model.Undo();
        Assert.Equal([shape, locked], model.Elements(Left));
        model.Redo();
        Assert.Equal(Palette.Coral, model.Elements(Left)[0].Style.Fill);

        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.DeleteCurrent, Primary)));
        Assert.Equal([locked], model.Elements(Left));
        model.Undo();
        Assert.Equal(2, model.Elements(Left).Length);
        Assert.Equal(2, model.Snapshot.SelectionCount);
        Assert.True(model.Snapshot.SelectionIsWorkspaceWide);
    }

    [Fact]
    public void WorkspaceSelectionNudgesAllDisplaysAndCanvasClickReturnsToLocalEditing()
    {
        // testWorkspaceSelectionNudgesAllDisplaysAndCanvasClickReturnsToLocalEditing.
        WorkspaceModel model = Drawing();
        var a = new Element(Tool.Rectangle, new Rect(50, 50, 100, 80));
        var b = new Element(Tool.Ellipse, new Rect(50, 50, 100, 80));
        AssertAccepted(model.Add(Primary, a));
        AssertAccepted(model.Add(Left, b));
        model.SelectAll();
        Assert.Equal(2, model.Snapshot.SelectionCount);

        Assert.Equal(CanvasCommandResult.Done,
            model.Perform(new CanvasCommand(CanvasCommandKind.Nudge, Primary) { Offset = new Point(1, 0) }));
        Assert.Equal(a.Frame.X + 1, model.Elements(Primary)[0].Frame.X);
        Assert.Equal(b.Frame.X + 1, model.Elements(Left)[0].Frame.X);
        model.Undo();
        Assert.Equal([a], model.Elements(Primary));
        Assert.Equal([b], model.Elements(Left));

        Assert.True(model.Snapshot.SelectionIsWorkspaceWide);
        model.DeleteSelected();
        Assert.Empty(model.Elements(Primary));
        Assert.Empty(model.Elements(Left));
        model.Undo();
        Assert.Equal([a], model.Elements(Primary));
        Assert.Equal([b], model.Elements(Left));

        AssertAccepted(model.Edit(Left, document => document[0].Select([b.Id]), localSelection: true));
        Assert.False(model.Snapshot.SelectionIsWorkspaceWide);
        Assert.Empty(model.Document(Primary).Selection);
        Assert.Equal([b.Id], model.Document(Left).Selection);
    }

    [Fact]
    public void EveryDisplayReceivesDrawingAndKeepsItsOwnLocalDocument()
    {
        // testEveryDisplayReceivesDrawingAndKeepsItsOwnLocalDocument (model side).
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        DisplaySnapshot[] sessions = [.. model.Snapshot.Displays];
        Assert.Equal(2, sessions.Length);
        Assert.Equal(new Rect(-800, 120, 800, 600), sessions[0].Display.DesktopFrame);
        Assert.Equal(1, sessions[0].Display.Scale);
        Assert.Equal(2, sessions[1].Display.Scale);
        Assert.All(sessions, session => Assert.True(session.TakesInput));
        Assert.Equal(model.Snapshot.ActiveDisplay, model.Snapshot.FocusRequest?.Display);
        foreach (DisplaySnapshot session in sessions)
        {
            model.Choose(Tool.Rectangle);
            AssertAccepted(model.Add(session.Id, new Element(Tool.Rectangle, new Rect(60, 80, 120, 80))));
            Assert.Equal(session.Id, model.Snapshot.ActiveDisplay);
            Assert.Single(model.Elements(session.Id));
            Assert.Equal(new Rect(60, 80, 120, 80), model.Elements(session.Id)[0].Frame);
        }
        model.Undo();
        Assert.Empty(model.Elements(sessions[1].Id));
        Assert.Single(model.Elements(sessions[0].Id));
        model.UseApps();
        Assert.All(model.Snapshot.Displays, session => Assert.False(session.TakesInput));
        model.ResumeDrawing();
        Assert.All(model.Snapshot.Displays, session => Assert.True(session.TakesInput));
        model.HideTools();
        Assert.All(model.Snapshot.Displays, session => Assert.False(session.TakesInput));
        Assert.True(model.Snapshot.WorkspaceHidden);
        DisplaySnapshot hidden = model.Display(sessions[0].Id);
        AssertRejected(model.BeginEdit(hidden.Id, hidden.Version, EditKind.Gesture), EditRejection.NotDrawing);
    }

    [Fact]
    public void ClearFromAnEmptyDisplayClearsAndRestoresEveryDisplay()
    {
        // testClearFromAnEmptyDisplayClearsAndRestoresEveryDisplay.
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(20, 30, 100, 80));
        var cover = new Element(Tool.Cover, new Rect(150, 30, 100, 80));
        AssertAccepted(model.Add(Left, shape, cover));
        AssertAccepted(model.Edit(Primary, _ => { }));
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        Assert.True(model.Snapshot.CanClearCanvas);

        model.ClearCanvas();
        Assert.All(model.Snapshot.Displays, display => Assert.True(display.Document.Elements.IsEmpty));
        Assert.True(model.Snapshot.CanUndo);
        model.Undo();
        Assert.Empty(model.Elements(Primary));
        Assert.Equal([shape, cover], model.Elements(Left));
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Redo, Left)));
        Assert.Equal(Left, model.Snapshot.ActiveDisplay);
        Assert.All(model.Snapshot.Displays, display => Assert.True(display.Document.Elements.IsEmpty));
        model.Undo();
        Assert.Equal([shape, cover], model.Elements(Left));
    }

    [Fact]
    public void GlobalClearRejectsConflictingHistoryWithoutPartialRestoration()
    {
        // testGlobalClearRejectsConflictingHistoryWithoutPartialRestoration.
        WorkspaceModel model = Drawing();
        var a = new Element(Tool.Rectangle);
        var b = new Element(Tool.Ellipse);
        AssertAccepted(model.Add(Left, b));
        AssertAccepted(model.Add(Primary, a));
        model.ClearCanvas();
        var newer = new Element(Tool.Diamond);
        AssertAccepted(model.Add(Left, newer));
        AssertAccepted(model.Edit(Primary, _ => { }));

        model.Undo();
        Assert.Equal("Another display's drawing or history changed. No display was changed by this Undo or Redo.", model.Snapshot.ErrorMessage);
        Assert.Empty(model.Elements(Primary));
        Assert.Equal([newer], model.Elements(Left));

        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Undo, Left)));
        Assert.Empty(model.Elements(Left));
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Undo, Primary)));
        Assert.Equal([a], model.Elements(Primary));
        Assert.Equal([b], model.Elements(Left));
    }

    [Fact]
    public void ClearPreservesAllDisplaysWhenOneCannotRetainUndo()
    {
        // testClearPreservesAllDisplaysWhenOneCannotRetainUndo.
        WorkspaceModel model = Drawing();
        var a = new Element(Tool.Rectangle);
        var b = new Element(Tool.Text, text: string.Concat(Enumerable.Repeat("Long text ", 100)));
        AssertAccepted(model.Add(Primary, a));
        model.ReplaceDocumentForTesting(Left, new CanvasDocument(ElementList.Create([b]), historyByteLimit: 100));

        model.ClearCanvas();

        Assert.Equal("This change would exceed the Undo history limit. Your drawings have not changed. Save your boards first.",
            model.Snapshot.ErrorMessage);
        Assert.Equal([a], model.Elements(Primary));
        Assert.Equal([b], model.Elements(Left));
    }

    [Fact]
    public void DisplayChangesCoverProtectedDisplaysAndDropRemovedOnes()
    {
        // Mac displaysChanged: protected displays that change geometry get a full-display cover.
        DisplayDescriptor[] displays = [PrimaryDisplay, LeftDisplay];
        WorkspaceModel model = Create(displays: () => displays);
        model.BeginDrawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Cover, new Rect(10, 10, 50, 50))));
        AssertAccepted(model.Add(Left, new Element(Tool.Rectangle, new Rect(10, 10, 50, 50))));

        var resized = new DisplayDescriptor(Primary, new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), 1.5);
        displays = [resized];
        model.DisplaysChanged();

        Assert.Single(model.Snapshot.Displays);
        Element[] covers = model.Elements(Primary);
        Assert.Equal(2, covers.Length);
        Assert.Equal(new Rect(0, 0, 1280, 720), covers[1].Frame);
        Assert.Equal(Tool.Cover, covers[1].Tool);
        Assert.Equal(resized, model.Display(Primary).Display);
        Assert.Equal("Display geometry changed: a full-display opaque cover was added. Review before removing.", model.Snapshot.Status);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
    }

    [Fact]
    public void RemovingAProtectedDisplayWarns()
    {
        DisplayDescriptor[] displays = [PrimaryDisplay, LeftDisplay];
        WorkspaceModel model = Create(displays: () => displays);
        model.BeginDrawing();
        AssertAccepted(model.Add(Left, new Element(Tool.Cover, new Rect(10, 10, 50, 50))));
        displays = [PrimaryDisplay];
        model.DisplaysChanged();
        Assert.Equal("A protected display disconnected. Stop sharing and review all covers.", model.Snapshot.Status);
        Assert.Null(model.Snapshot.Display(Left));
        Assert.Null(model.Snapshot.ActiveDisplay);
        model.BeginDrawing();
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
    }

    [Fact]
    public void CoordinatedUndoNeedsEveryDisplayItChanged()
    {
        DisplayDescriptor[] displays = [PrimaryDisplay, LeftDisplay];
        WorkspaceModel model = Create(displays: () => displays);
        model.BeginDrawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Rectangle)));
        AssertAccepted(model.Add(Left, new Element(Tool.Ellipse)));
        AssertAccepted(model.Edit(Primary, _ => { }));
        model.ClearCanvas();
        displays = [PrimaryDisplay];
        model.DisplaysChanged();
        model.BeginDrawing();
        model.Undo();
        Assert.Equal("Another display's drawing or history changed. No display was changed by this Undo or Redo.", model.Snapshot.ErrorMessage);
        Assert.Empty(model.Elements(Primary));
    }
}
