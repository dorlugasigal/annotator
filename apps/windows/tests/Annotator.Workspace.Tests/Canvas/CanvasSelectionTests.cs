using Annotator.Core;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// Selection gestures and keys: click, Shift-click, marquee, move, resize and rotation handles, vertex edits, nudges and
/// Ctrl shortcuts (UniformEditingTests, RotationHandleTests, SelectedContentEditingTests, KeyboardEditingTests and
/// ParityRegressionTests).
/// </summary>
public sealed class CanvasSelectionTests
{
    private static readonly ValueList<byte> Png = ValueList.Create<byte>([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0]);

    [Fact]
    public void EveryPlacedToolCanBeSelectedMovedDeletedAndUndone()
    {
        // UniformEditingTests.testEveryPlacedToolCanBeSelectedMovedDeletedAndUndone.
        using CanvasHarness h = Single();
        foreach (Tool tool in Tool.AllCases)
        {
            if (tool is Tool.Select or Tool.Eraser)
            {
                continue;
            }
            var item = new Element(tool, new Rect(200, 200, 120, 100), text: tool == Tool.Counter ? "1" : "Editable");
            Point hit = item.Frame.Center;
            if (tool.IsFreehand || tool.IsConnector || tool == Tool.Ruler)
            {
                item = new Element(tool, points: ValueList.Create<Point>([new(200, 250), new(260, 210), new(320, 250)]), text: "Editable");
                if (tool == Tool.Ruler)
                {
                    item.Points = [new Point(200, 250), new Point(320, 250)];
                }
                item.Frame = Rect.Enclosing(item.Points);
                hit = tool == Tool.Ruler ? new Point(260, 250) : new Point(260, 210);
                if (tool == Tool.Arrow)
                {
                    item.Style.Curved = true;
                    hit = ConnectorGeometry.Curves(item.Points)[0].PointAt(0.5);
                }
            }
            if (tool == Tool.Image)
            {
                item.ImagePng = Png;
            }
            h.Choose(Tool.Select);
            h.SetDocument(Main, item);
            h.Down(Main, hit);
            Assert.True(h[Main].State.Selection.SetEquals([item.Id]), $"Selection failed for {tool}");
            var displacement = new Point(30, 20);
            h.Move(Main, hit + displacement);
            h.Up(Main, hit + displacement);
            AssertFrame(item.Frame.Translated(displacement), h.Elements(Main)[0].Frame, $"Move failed for {tool}");
            Assert.True(h.Key(Main, VirtualKeys.Back));
            Assert.True(h.Elements(Main).Length == 0, $"Delete failed for {tool}");
            h.Model.Undo();
            AssertFrame(item.Frame.Translated(displacement), h.Elements(Main)[0].Frame, $"Undo of delete failed for {tool}");
            h.Model.Undo();
            Assert.Equal(item.Frame, h.Elements(Main)[0].Frame);
        }
    }

    [Fact]
    public void RotationHandleHasALargerHitAreaADistinctCursorAndDoesNotJump()
    {
        // RotationHandleTests.testRotationHandleHasLargerHitAreaDistinctCursorAndDoesNotJump.
        using CanvasHarness h = Single();
        var element = new Element(Tool.Rectangle, new Rect(250, 200, 160, 100));
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([element], element.Id));
        h.Pump();
        var grab = new Point(element.Frame.Center.X + 14, element.Frame.Y - 24);
        Assert.True(h[Main].IsOverRotationHandle(grab));
        Assert.False(h[Main].IsOverRotationHandle(grab + new Point(10, 0)));
        h.Move(Main, grab, held: false);
        Assert.Equal(CanvasCursor.Rotation, h[Main].State.Cursor);
        h.Down(Main, grab);
        h.Move(Main, grab);
        Assert.Equal(0, h[Main].State.Elements[0].Rotation, 3);
        Point finish = grab.Rotated(element.Frame.Center, Math.PI / 3);
        h.Move(Main, finish);
        Assert.Equal(CanvasCursor.Rotation, h[Main].State.Cursor);
        h.Up(Main, finish);
        Assert.Equal(Math.PI / 3, h.Elements(Main)[0].Rotation, 3);
        h.Model.Undo();
        Assert.Equal(0, h.Elements(Main)[0].Rotation, 3);
    }

    [Fact]
    public void DraggingShiftSelectedShapesPreservesSelectionAndOneUndo()
    {
        // SelectedContentEditingTests.testDraggingShiftSelectedShapesPreservesSelectionAndOneUndo.
        using CanvasHarness h = Single();
        var first = new Element(Tool.Rectangle, new Rect(100, 100, 80, 60));
        var second = new Element(Tool.Ellipse, new Rect(240, 100, 80, 60));
        h.SetDocument(Main, first, second);
        h.Choose(Tool.Select);
        h.Click(Main, new Point(100, 130));
        h.Click(Main, new Point(240, 130), modifiers: KeyModifiers.Shift);
        Assert.True(h.Document(Main).Selection.SetEquals([first.Id, second.Id]));
        Point start = new(100, 130), finish = new(130, 170);
        h.Drag(Main, start, finish);
        Assert.True(h.Document(Main).Selection.SetEquals([first.Id, second.Id]));
        Assert.Equal(first.Frame.Translated(finish - start), h.Elements(Main)[0].Frame);
        Assert.Equal(second.Frame.Translated(finish - start), h.Elements(Main)[1].Frame);
        h.Model.Undo();
        Assert.Equal([first.Frame, second.Frame], h.Elements(Main).Select(element => element.Frame));
        h.Model.Redo();
        Assert.Equal(second.Frame.Translated(finish - start), h.Elements(Main)[1].Frame);

        h.Click(Main, second.Frame.Translated(finish - start).Center, modifiers: KeyModifiers.Shift);
        Assert.Equal([first.Id], h.Document(Main).Selection);
    }

    [Fact]
    public void SharedSelectionResizeRotationAndCancellationKeepLockedObjectsFixed()
    {
        // SelectedContentEditingTests.testSharedSelectionResizeRotationAndCancellationKeepLockedObjectsFixed.
        using CanvasHarness h = Single();
        var first = new Element(Tool.Rectangle, new Rect(100, 100, 80, 60)) { Label = new ShapeLabel("Orders", new Style()) };
        var second = new Element(Tool.Ellipse, new Rect(240, 100, 80, 60));
        var locked = new Element(Tool.Rectangle, new Rect(500, 400, 80, 60)) { Locked = true };
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([first, second, locked], first.Id, second.Id, locked.Id));
        h.Pump();
        SelectionGeometry group = SelectionGeometry.Create(h.Document(Main).Elements, h.Document(Main).Selection)!.Value;
        Assert.Equal(new Rect(100, 100, 220, 60), group.Bounds);
        Point corner = group.Corners[2];
        Point target = group.Corners[0] + (corner - group.Corners[0]) * 2;
        h.Drag(Main, corner, target);
        Assert.Equal(new Rect(100, 100, 160, 120), h.Elements(Main)[0].Frame);
        Assert.Equal(new Rect(380, 100, 160, 120), h.Elements(Main)[1].Frame);
        Assert.Equal(first.Label!.Style.FontSize * 2, h.Elements(Main)[0].Label!.Style.FontSize);
        Assert.Equal(locked, h.Elements(Main)[2]);
        h.Model.Undo();
        Assert.Equal([first.Frame, second.Frame, locked.Frame], h.Elements(Main).Select(element => element.Frame));
        h.Sync();

        var handle = new Point(group.Bounds.Center.X, group.Bounds.Y - 24);
        Point rotated = handle.Rotated(group.Bounds.Center, Math.PI / 2);
        Assert.True(h[Main].IsOverRotationHandle(handle));
        h.Drag(Main, handle, rotated);
        Element[] originals = [first, second];
        for (int index = 0; index < originals.Length; index++)
        {
            Element changed = h.Elements(Main)[index];
            Point center = originals[index].Frame.Center.Rotated(group.Bounds.Center, Math.PI / 2);
            Assert.Equal(center.X, changed.Frame.Center.X, 3);
            Assert.Equal(center.Y, changed.Frame.Center.Y, 3);
            Assert.Equal(Math.PI / 2, changed.Rotation, 3);
            changed.Validate();
        }
        Assert.Equal(locked, h.Elements(Main)[2]);
        h.Model.Undo();
        h.Down(Main, corner);
        h.Move(Main, target);
        Assert.Equal(new Rect(100, 100, 160, 120), h[Main].State.Elements[0].Frame);
        h[Main].CancelGesture();
        h.Pump();
        Assert.Equal([first.Frame, second.Frame, locked.Frame], h.Elements(Main).Select(element => element.Frame));
        Assert.Equal([first.Frame, second.Frame, locked.Frame], h[Main].State.Elements.Select(element => element.Frame));
        h.Model.Redo();
        Assert.Equal(Math.PI / 2, h.Elements(Main)[0].Rotation, 3);
    }

    [Fact]
    public void SelectionTransformPreservesBoundConnectorAndCoverRotationSafety()
    {
        // SelectedContentEditingTests.testSelectionTransformPreservesBoundConnectorAndCoverRotationSafety.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 80, 60));
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(180, 130), new(300, 130)]))
        {
            StartAnchor = new Anchor(shape.Id, new Point(1, 0.5)),
        };
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([shape, arrow], shape.Id, arrow.Id));
        h.Pump();
        SelectionGeometry group = SelectionGeometry.Create(h.Document(Main).Elements, h.Document(Main).Selection)!.Value;
        var handle = new Point(group.Bounds.Center.X, group.Bounds.Y - 24);
        h.Drag(Main, handle, handle.Rotated(group.Bounds.Center, Math.PI / 2));
        Element changed = h.Elements(Main)[1];
        Assert.Equal(arrow.StartAnchor, changed.StartAnchor);
        Assert.Equal(h.Elements(Main)[0].AnchorPoint(new Point(1, 0.5)), changed.Points[0]);
        Assert.Equal(arrow.Points[1].Rotated(group.Bounds.Center, Math.PI / 2).X, changed.Points[1].X, 3);
        Assert.Equal(0, changed.Rotation);
        h.Model.Undo();

        var cover = new Element(Tool.Cover, new Rect(400, 100, 60, 60));
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([shape, arrow, cover], shape.Id, cover.Id));
        h.Pump();
        SelectionGeometry covered = SelectionGeometry.Create(h.Document(Main).Elements, h.Document(Main).Selection)!.Value;
        Assert.False(covered.CanRotate);
        Assert.False(h[Main].IsOverRotationHandle(new Point(covered.Bounds.Center.X, covered.Bounds.Y - 24)));
    }

    [Fact]
    public void MixedStyleEditsDoNotChangeProtectiveCoversOrFillOpenStrokes()
    {
        // SelectedContentEditingTests.testMixedStyleEditsDoNotChangeProtectiveCoversOrFillOpenStrokes.
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(40, 100, 100, 80));
        var line = new Element(Tool.Line, points: ValueList.Create<Point>([new(200, 100), new(300, 100)]));
        var cover = new Element(Tool.Cover, new Rect(350, 100, 100, 80));
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([shape, line, cover], shape.Id, line.Id, cover.Id));
        h.Model.ApplyStyle((ref Style style) => style.Fill = Palette.Blue, element => element.Tool.SupportsFill);
        Assert.Equal(Palette.Blue, h.Elements(Main)[0].Style.Fill);
        Assert.Equal(line.Style, h.Elements(Main)[1].Style);
        h.Model.ApplyStyle((ref Style style) =>
        {
            style.Opacity = 0.25;
            style.Width = 8;
        });
        Assert.Equal(0.25, h.Elements(Main)[0].Style.Opacity);
        Assert.Equal(8, h.Elements(Main)[1].Style.Width);
        Assert.Equal(cover.Style, h.Elements(Main)[2].Style);
    }

    [Fact]
    public void ResizedRulerRecalculatesItsDisplayedDistance()
    {
        // SelectedContentEditingTests.testResizedRulerRecalculatesItsDisplayedDistance.
        using CanvasHarness h = Single();
        var ruler = new Element(Tool.Ruler, new Rect(100, 100, 100, 50), ValueList.Create<Point>([new(100, 100), new(200, 150)]), "111 px");
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([ruler], ruler.Id));
        h.Pump();
        h.Drag(Main, new Point(200, 150), new Point(300, 200));
        Assert.Equal("223 px", h.Elements(Main)[0].Text);
        h.Model.Undo();
        Assert.Equal("111 px", h.Elements(Main)[0].Text);
    }

    [Fact]
    public void ShiftResizeKeepsTheAspectRatioAndTheOppositeCornerOfRotatedObjects()
    {
        using CanvasHarness h = Single();
        var element = new Element(Tool.Rectangle, new Rect(200, 200, 100, 50)) { Rotation = 0.5 };
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([element], element.Id));
        h.Pump();
        Point fixedCorner = element.Frame.Origin.Rotated(element.Frame.Center, element.Rotation);
        Point grab = new Point(element.Frame.MaxX, element.Frame.MaxY).Rotated(element.Frame.Center, element.Rotation);
        Point target = new Point(element.Frame.MaxX + 150, element.Frame.MaxY + 10).Rotated(element.Frame.Center, element.Rotation);
        h.Drag(Main, grab, target, modifiers: KeyModifiers.Shift);
        Element resized = h.Elements(Main)[0];
        Assert.Equal(2, resized.Frame.Width / resized.Frame.Height, 6);
        Point fixedAfter = resized.Frame.Origin.Rotated(resized.Frame.Center, resized.Rotation);
        Assert.Equal(fixedCorner.X, fixedAfter.X, 6);
        Assert.Equal(fixedCorner.Y, fixedAfter.Y, 6);
    }

    [Fact]
    public void MarqueeSelectsObjectsInsideItAndClickingEmptySpaceClears()
    {
        using CanvasHarness h = Single();
        var inside = new Element(Tool.Rectangle, new Rect(100, 100, 50, 50));
        var partly = new Element(Tool.Ellipse, new Rect(180, 100, 100, 50));
        h.SetDocument(Main, inside, partly);
        h.Choose(Tool.Select);
        h.Down(Main, new Point(50, 50));
        h.Move(Main, new Point(200, 200));
        Assert.Equal(new Rect(50, 50, 150, 150), h[Main].State.Marquee);
        h.Up(Main, new Point(200, 200));
        Assert.Null(h[Main].State.Marquee);
        Assert.Equal([inside.Id], h.Document(Main).Selection);
        h.Click(Main, new Point(900, 900));
        Assert.Empty(h.Document(Main).Selection);
    }

    [Fact]
    public void ShiftDraggingAnArrowEndKeepsItsDirection()
    {
        using CanvasHarness h = Single();
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(100, 100), new(200, 200)]));
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([arrow], arrow.Id));
        h.Pump();
        h.Drag(Main, new Point(200, 200), new Point(320, 260), modifiers: KeyModifiers.Shift);
        Element changed = h.Elements(Main)[0];
        Assert.Equal(new Point(100, 100), changed.Points[0]);
        Assert.Equal(changed.Points[1].X, changed.Points[1].Y, 6);
        Assert.Equal(290, changed.Points[1].X, 6);
    }

    [Fact]
    public void NudgingSelectionHonorsStepSizeLocksAndUndo()
    {
        // KeyboardEditingTests.testNudgingSelectionHonorsStepSizeLocksAndUndo through the canvas keys.
        using CanvasHarness h = Single();
        var rectangle = new Element(Tool.Rectangle, new Rect(80, 70, 120, 60));
        var locked = new Element(Tool.Cover, new Rect(250, 70, 50, 50)) { Locked = true };
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([rectangle, locked], rectangle.Id, locked.Id));
        h.Pump();
        Assert.True(h.Key(Main, VirtualKeys.Right));
        Assert.Equal(81, h.Elements(Main)[0].Frame.X);
        Assert.True(h.Key(Main, VirtualKeys.Down, KeyModifiers.Shift));
        Assert.Equal(80, h.Elements(Main)[0].Frame.Y);
        Assert.Equal(locked.Frame, h.Elements(Main)[1].Frame);
        h.Model.Undo();
        Assert.Equal(70, h.Elements(Main)[0].Frame.Y);
        h.Model.Undo();
        Assert.Equal(rectangle.Frame, h.Elements(Main)[0].Frame);

        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([rectangle, locked], locked.Id));
        h.Pump();
        h.Key(Main, VirtualKeys.Left);
        Assert.Equal(locked.Frame, h.Elements(Main)[1].Frame);
        Assert.Contains("locked", h.Model.Snapshot.Status, StringComparison.Ordinal);

        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([rectangle, locked]));
        h.Pump();
        Assert.False(h.Key(Main, VirtualKeys.Left));
    }

    [Fact]
    public void BackspaceDeletesASelectedCoverAndUndoRestoresIt()
    {
        // ParityRegressionTests.testSelectedSolidCensorCanBeDeletedByKeyboard.
        using CanvasHarness h = Single();
        var censor = new Element(Tool.Cover, new Rect(200, 200, 100, 60));
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([censor], censor.Id));
        h.Pump();
        Assert.True(h.Key(Main, VirtualKeys.Delete));
        Assert.False(h.Document(Main).HasProtection);
        h.Model.Undo();
        Assert.True(h.Document(Main).HasProtection);
    }

    [Fact]
    public void BackspaceCancelsTheGestureInProgressFirst()
    {
        using CanvasHarness h = Single();
        var keep = new Element(Tool.Rectangle, new Rect(10, 10, 40, 40));
        h.Add(Main, [keep]);
        h.Choose(Tool.Pen);
        h.Down(Main, new Point(300, 300));
        h.Move(Main, new Point(320, 320));
        Assert.True(h.Key(Main, VirtualKeys.Back));
        Assert.False(h[Main].HasActiveEdit);
        Assert.Equal([keep], h.Elements(Main));
        h.Up(Main, new Point(330, 330));
        Assert.Equal([keep], h.Elements(Main));
        Assert.True(h.Key(Main, VirtualKeys.Back));
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void CtrlShortcutsRunTheCanvasCommands()
    {
        using CanvasHarness h = Single();
        h.Watch(h[Main]);
        var a = new Element(Tool.Rectangle, new Rect(10, 10, 40, 40));
        var b = new Element(Tool.Ellipse, new Rect(100, 10, 40, 40));
        h.Add(Main, [a, b]);
        Assert.True(h.Key(Main, VirtualKeys.A, KeyModifiers.Ctrl, "a"));
        Assert.Equal(2, h.Model.Snapshot.SelectionCount);
        Assert.Equal(Tool.Select, h.Model.Snapshot.Tool);
        Assert.True(h.Key(Main, VirtualKeys.G, KeyModifiers.Ctrl, "g"));
        Guid? group = h.Elements(Main)[0].GroupId;
        Assert.NotNull(group);
        Assert.Equal(group, h.Elements(Main)[1].GroupId);
        Assert.True(h.Key(Main, VirtualKeys.G, KeyModifiers.Ctrl | KeyModifiers.Shift, "g"));
        Assert.All(h.Elements(Main), element => Assert.Null(element.GroupId));
        Assert.True(h.Key(Main, VirtualKeys.D, KeyModifiers.Ctrl, "d"));
        Assert.Equal(4, h.Elements(Main).Length);
        Assert.Equal(a.Frame.Translated(new Point(18, 18)), h.Elements(Main)[2].Frame);
        Assert.True(h.Key(Main, VirtualKeys.Z, KeyModifiers.Ctrl, "z"));
        Assert.Equal(2, h.Elements(Main).Length);
        Assert.True(h.Key(Main, VirtualKeys.Z, KeyModifiers.Ctrl | KeyModifiers.Shift, "z"));
        Assert.Equal(4, h.Elements(Main).Length);
        h.Key(Main, VirtualKeys.Z, KeyModifiers.Ctrl, "z");
        Assert.True(h.Key(Main, VirtualKeys.Y, KeyModifiers.Ctrl, "y"));
        Assert.Equal(4, h.Elements(Main).Length);

        Assert.True(h.Key(Main, VirtualKeys.S, KeyModifiers.Ctrl, "s"));
        Assert.True(h.Key(Main, VirtualKeys.O, KeyModifiers.Ctrl, "o"));
        Assert.True(h.Key(Main, VirtualKeys.L, KeyModifiers.Ctrl, "l"));
        Assert.True(h.Key(Main, VirtualKeys.OemComma, KeyModifiers.Ctrl, ","));
        Assert.Equal([CanvasRequest.SaveBoard, CanvasRequest.OpenBoard, CanvasRequest.ToolLibrary, CanvasRequest.Settings], h.Requests);
        Assert.False(h.Key(Main, VirtualKeys.Tab, KeyModifiers.Ctrl));
    }

    [Fact]
    public void CtrlWAndCtrlBToggleTheBoardOfTheEditingTarget()
    {
        // Spec 009: Control-W and Control-B stay direct toggles; Ctrl on Windows.
        using CanvasHarness h = new();
        Assert.True(h.Key(TestWorkspace.Primary, VirtualKeys.W, KeyModifiers.Ctrl, "w"));
        Assert.Equal(Board.Whiteboard, h.Model.Display(TestWorkspace.Primary).Board);
        Assert.Equal(Board.Whiteboard, h.Model.Snapshot.Board);
        Assert.True(h.Key(TestWorkspace.Primary, VirtualKeys.W, KeyModifiers.Ctrl, "w"));
        Assert.Equal(Board.Desktop, h.Model.Display(TestWorkspace.Primary).Board);
        Assert.True(h.Key(TestWorkspace.Left, VirtualKeys.B, KeyModifiers.Ctrl, "b"));
        Assert.Equal(Board.Blackboard, h.Model.Display(TestWorkspace.Left).Board);
        Assert.Equal(Board.Desktop, h.Model.Display(TestWorkspace.Primary).Board);
        Assert.Equal(TestWorkspace.Left, h.Model.Snapshot.ActiveDisplay);
    }

    [Fact]
    public void ButtonKeysToggleSmartDrawAndCycleTheBoard()
    {
        var preferences = new WorkspacePreferences();
        preferences.Bind(LocalShortcut.Create("j"), ShortcutCommand.ForAction(ShortcutAction.SmartDraw));
        preferences.Bind(LocalShortcut.Create("l"), ShortcutCommand.ForAction(ShortcutAction.Board));
        using CanvasHarness h = Single(new MemoryPreferencesStore(preferences.Data()));
        Assert.True(h.Key(Main, 'J', text: "j"));
        Assert.True(h.Model.Snapshot.SmartDraw);
        Assert.Equal("Smart draw is on.", h.Model.Snapshot.Status);
        Assert.True(h.Key(Main, 'J', text: "j"));
        Assert.False(h.Model.Snapshot.SmartDraw);
        Assert.True(h.Key(Main, 'L', text: "l"));
        Assert.Equal(Board.Whiteboard, h.Model.Snapshot.Board);
        Assert.Equal("Whiteboard background.", h.Model.Snapshot.Status);
    }

    [Fact]
    public void HoverCursorsFollowTheToolAndRotationHandles()
    {
        using CanvasHarness h = Single();
        var point = new Point(500, 500);
        h.Choose(Tool.Pen);
        h.Move(Main, point, held: false);
        Assert.Equal(new CanvasCursor(CanvasCursorKind.Footprint, Tool.Pen, 3), h[Main].State.Cursor);
        h.Choose(Tool.Highlighter);
        CanvasCursor highlighter = h[Main].HoverCursor(point);
        Assert.Equal(CanvasCursorKind.Footprint, highlighter.Kind);
        Assert.Equal(20, highlighter.FootprintSize);
        Assert.Equal(30, highlighter.ImageSide);
        Assert.Equal(new Annotator.Core.Geometry.Size(6.4, 20), highlighter.Nib);
        h.Choose(Tool.Eraser);
        Assert.Equal(CanvasCursorKind.Footprint, h[Main].HoverCursor(point).Kind);
        // ParityRegressionTests.testCursorFootprintsAndToolbarPreferences (cursor part): a wide eraser shows a larger
        // footprint than a pen, and the same tool and width give the same cursor.
        Assert.True(CanvasCursor.ForTool(Tool.Eraser, 28).ImageSide > CanvasCursor.ForTool(Tool.Pen, 7).ImageSide);
        Assert.Equal(CanvasCursor.ForTool(Tool.Pen, 7), CanvasCursor.ForTool(Tool.Pen, 7));
        h.Choose(Tool.Rectangle);
        Assert.Equal(CanvasCursorKind.Crosshair, h[Main].HoverCursor(point).Kind);
        h.Choose(Tool.Text);
        Assert.Equal(CanvasCursor.IBeam, h[Main].HoverCursor(point));
        h.Choose(Tool.Select);
        Assert.Equal(CanvasCursor.Arrow, h[Main].HoverCursor(point));
        h.Model.UseApps();
        h.Pump();
        Assert.Equal(CanvasCursor.Arrow, h[Main].HoverCursor(point));
        Assert.False(h[Main].State.IsDrawing);
    }

    [Fact]
    public void FillPaletteEditsFourSelectedRectanglesAndPreservesOtherStyles()
    {
        // SelectedContentEditingTests.testFillPaletteEditsFourSelectedRectanglesAndPreservesOtherStyles, model part: the
        // palette's Blue button calls SetPaintColor inside one style edit.
        using CanvasHarness h = Single();
        Element[] shapes = Enumerable.Range(0, 4).Select(index =>
        {
            var shape = new Element(Tool.Rectangle, new Rect(40 + index * 140, 100, 100, 80));
            shape.Style.Width = index + 2;
            shape.Style.Color = index % 2 == 0 ? Palette.Coral : Palette.Ink;
            return shape;
        }).ToArray();
        RGBA blue = Palette.Blue with { Alpha = 0.4 };
        foreach (bool mixedAndLocked in new[] { false, true })
        {
            if (mixedAndLocked)
            {
                shapes[1].Tool = Tool.Ellipse;
                shapes[3].Locked = true;
            }
            h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection(shapes, [.. shapes.Select(shape => shape.Id)]));
            h.Choose(Tool.Select);
            Assert.True(h.Model.Snapshot.SelectionSupportsFill);
            Assert.False(h.Model.Snapshot.SelectionIsLocked);
            h.Model.BeginStyleEdit();
            h.Model.SetPaintColor(Palette.Blue, fill: true);
            h.Model.FinishStyleEdit();
            h.Pump();
            Element[] changed = h.Elements(Main);
            for (int index = 0; index < shapes.Length; index++)
            {
                Element original = shapes[index];
                Assert.Equal(original.Locked ? original.Style.Fill : blue, changed[index].Style.Fill);
                Assert.Equal(original.Style.Width, changed[index].Style.Width);
                Assert.Equal(original.Style.Color, changed[index].Style.Color);
                Assert.Equal(original.Frame, changed[index].Frame);
            }
            Assert.Equal(shapes.Select(shape => shape.Id).ToHashSet(), h.Model.DocumentOf(Main).Selection.ToHashSet());
            Assert.Equal(shapes.Select(shape => shape.Id).ToHashSet(), h[Main].State.Selection.ToHashSet());
            h.Model.Undo();
            Assert.Equal(shapes.Select(shape => shape.Style), h.Elements(Main).Select(element => element.Style));
            h.Model.Redo();
            Assert.Equal(blue, h.Elements(Main)[0].Style.Fill);
        }
    }

    [Fact]
    public void TheSelectionHidesWhileTextIsEditedAndOnEraserTargets()
    {
        // SelectionOverlayView: no selection handles while the text editor is open, or on objects the eraser will delete.
        using CanvasHarness h = Single();
        var text = new Element(Tool.Text, new Rect(50, 50, 180, 50), text: "Original");
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([text], text.Id));
        h.Pump();
        Assert.True(h[Main].State.Selection.SetEquals([text.Id]));
        h.Click(Main, new Point(80, 70), count: 2);
        Assert.NotNull(h[Main].State.Text);
        Assert.Empty(h[Main].State.Selection);
        h[Main].CommitText();
        h.Pump();
        Assert.Null(h[Main].State.Text);

        var first = new Element(Tool.Rectangle, new Rect(300, 300, 100, 100));
        var second = new Element(Tool.Rectangle, new Rect(600, 300, 100, 100));
        h.Choose(Tool.Eraser);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([first, second], first.Id, second.Id));
        h.Pump();
        Assert.True(h[Main].State.Selection.SetEquals([first.Id, second.Id]));
        h.Down(Main, new Point(300, 350));
        Assert.True(h[Main].State.EraserTargets.SetEquals([first.Id]));
        Assert.True(h[Main].State.Selection.SetEquals([second.Id]));
        h.Up(Main, new Point(300, 350));
        Assert.Equal([second.Id], h.Elements(Main).Select(element => element.Id));
    }

    [Fact]
    public void RightClickSelectsTheObjectAndOffersItsActions()
    {
        // MultiDisplayEditingTests.testRightClickSelectsTheCorrectDisplayForActions (menu part) on one display.
        using CanvasHarness h = Single();
        h.Watch(h[Main]);
        var item = new Element(Tool.Rectangle, new Rect(40, 40, 120, 100));
        var locked = new Element(Tool.Cover, new Rect(300, 40, 100, 100)) { Locked = true };
        h.SetDocument(Main, item, locked);
        h.Choose(Tool.Select);
        CanvasMenu? menu = null;
        h[Main].ContextMenuRequested += shown => menu = shown;
        h.Down(Main, item.Frame.Center, button: PointerButtons.Secondary);
        Assert.NotNull(menu);
        Assert.Equal([item.Id], h.Document(Main).Selection);
        string[] titles = [.. menu!.Items.Where(entry => !entry.IsSeparator).Select(entry => entry.Title)];
        Assert.Equal(["Properties", "Copy", "Cut", "Duplicate", "Group", "Ungroup", "Bring to front", "Send to back", "Lock", "Unlock",
                      "Save to My shapes...", "Delete selected", "Paste", "Undo", "Redo", "More shapes and tools"], titles);
        Assert.False(menu.Items.Single(entry => entry.Command == CanvasMenuCommand.Group).IsEnabled);
        Assert.False(menu.Items.Single(entry => entry.Command == CanvasMenuCommand.Unlock).IsEnabled);
        h[Main].PerformMenuItem(CanvasMenuCommand.DeleteSelected);
        h.Pump();
        Assert.Equal([locked], h.Elements(Main));
        h.Model.Undo();
        Assert.Equal(2, h.Elements(Main).Length);

        h.Down(Main, locked.Frame.Center, button: PointerButtons.Secondary);
        Assert.Equal("Delete selected censor region", menu!.Items.Single(entry => entry.Command == CanvasMenuCommand.DeleteSelected).Title);
        Assert.False(menu.Items.Single(entry => entry.Command == CanvasMenuCommand.DeleteSelected).IsEnabled);
        Assert.False(menu.Items.Single(entry => entry.Command == CanvasMenuCommand.SaveToMyShapes).IsEnabled);
        h[Main].PerformMenuItem(CanvasMenuCommand.Properties);
        h[Main].PerformMenuItem(CanvasMenuCommand.MoreShapesAndTools);
        Assert.Equal([CanvasRequest.Properties, CanvasRequest.ToolLibrary], h.Requests);

        h.Down(Main, new Point(900, 900), button: PointerButtons.Secondary);
        Assert.Equal(["Paste", "Undo", "Redo", "More shapes and tools"], menu!.Items.Select(entry => entry.Title));

        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(600, 600));
        h.Down(Main, new Point(700, 600), button: PointerButtons.Secondary);
        Assert.Equal(["Finish arrow", "Cancel edit"], menu!.Items.Select(entry => entry.Title));
        h[Main].PerformMenuItem(CanvasMenuCommand.CancelEdit);
        h.Pump();
        Assert.False(h[Main].HasActiveEdit);
    }

    [Fact]
    public void TheCanvasDescribesItselfAndOffersFocusDrawingTools()
    {
        // KeyboardEditingTests.testCanvasExposesItsStateAndFocusToolsAction (logic part).
        using CanvasHarness h = Single();
        h.Watch(h[Main]);
        Assert.Equal("Annotation canvas", CanvasAccessibility.Label);
        Assert.Equal("1 object, 0 selected. Drawing.", CanvasAccessibility.Summary(1, 0, drawing: true));
        Assert.Equal("2 objects, 1 selected. Using apps.", CanvasAccessibility.Summary(2, 1, drawing: false));
        Assert.Equal("Focus drawing tools", CanvasAccessibility.FocusToolsAction);
        h[Main].FocusTools();
        Assert.Equal([CanvasRequest.FocusTools], h.Requests);
    }

    [Fact]
    public void TheKeyboardMenuKeepsTheSelectionAndOffersFocusDrawingTools()
    {
        // WIN-A11Y-02: Shift+F10 or the Menu key opens the menu for the current selection at its center, and the Mac's
        // VoiceOver action Focus drawing tools is its last item.
        using CanvasHarness h = Single();
        h.Watch(h[Main]);
        var first = new Element(Tool.Rectangle, new Rect(40, 40, 120, 100));
        var second = new Element(Tool.Ellipse, new Rect(400, 300, 100, 100));
        h.SetDocument(Main, first, second);
        h.Choose(Tool.Select);
        CanvasMenu? menu = null;
        h[Main].ContextMenuRequested += shown => menu = shown;

        Assert.True(h[Main].OpenKeyboardContextMenu(new Point(450, 284)));
        Assert.Empty(h.Document(Main).Selection);
        Assert.Equal(new Point(450, 284), menu!.Position);
        Assert.Equal(["Paste", "Undo", "Redo", "More shapes and tools", "Focus drawing tools"],
            menu.Items.Where(entry => !entry.IsSeparator).Select(entry => entry.Title));
        Assert.True(menu.Items[^2].IsSeparator);

        h.Click(Main, first.Frame.Center);
        h.Pump();
        Assert.True(h[Main].OpenKeyboardContextMenu(new Point(450, 284)));
        Assert.Equal([first.Id], h.Document(Main).Selection);
        Assert.Equal(first.Frame.Center, menu!.Position);
        Assert.Equal(["Properties", "Copy", "Cut", "Duplicate", "Group", "Ungroup", "Bring to front", "Send to back", "Lock", "Unlock",
                      "Save to My shapes...", "Delete selected", "Paste", "Undo", "Redo", "More shapes and tools", "Focus drawing tools"],
            menu.Items.Where(entry => !entry.IsSeparator).Select(entry => entry.Title));
        h[Main].PerformMenuItem(CanvasMenuCommand.FocusDrawingTools);
        Assert.Equal([CanvasRequest.FocusTools], h.Requests);

        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(600, 600));
        Assert.True(h[Main].OpenKeyboardContextMenu(new Point(450, 284)));
        Assert.Equal(["Finish arrow", "Cancel edit", "Focus drawing tools"],
            menu!.Items.Where(entry => !entry.IsSeparator).Select(entry => entry.Title));
    }

    [Fact]
    public void TheShortcutHintFollowsModeToolAndEdits()
    {
        using CanvasHarness h = Single();
        WorkspaceSnapshot drawing = h.Model.Snapshot;
        Assert.Equal("Drawing  ·  Ctrl+Shift+2: draw\nEscape: use apps\nTurn off these tips in Settings > General.",
            WorkspaceHint.Text(drawing, hasTextEditor: false, hasActiveEdit: false));
        h.Choose(Tool.Highlighter);
        Assert.Equal("Drawing  ·  Ctrl+Shift+2: draw\nHold Shift for a straight highlight.\nEscape cancels the current edit.\n" +
                     "Turn off these tips in Settings > General.",
            WorkspaceHint.Text(h.Model.Snapshot, hasTextEditor: false, hasActiveEdit: true));
        Assert.Contains("Escape finishes text.", WorkspaceHint.Text(h.Model.Snapshot, hasTextEditor: true, hasActiveEdit: true));
        Assert.Contains("Ctrl+1: screen zoom  ·  Alt+scroll starts zoom",
            WorkspaceHint.Text(h.Model.Snapshot, false, false, new ZoomHint(false, 1, string.Empty)));
        Assert.True(WorkspaceHint.IsVisible(h.Model.Snapshot, h.Model.Display(Main)));
        h.Model.UseApps();
        Assert.StartsWith("Using apps  ·  Ctrl+Shift+2: draw\nTurn off", WorkspaceHint.Text(h.Model.Snapshot, false, false));
        h.Model.HideTools();
        Assert.False(WorkspaceHint.IsVisible(h.Model.Snapshot, h.Model.Display(Main)));
    }

    private static void AssertFrame(Rect expected, Rect actual, string message)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= 1e-6 && Math.Abs(expected.Y - actual.Y) <= 1e-6
                    && Math.Abs(expected.Width - actual.Width) <= 1e-6 && Math.Abs(expected.Height - actual.Height) <= 1e-6,
                    $"{message}: expected {expected}, got {actual}");
    }
}
