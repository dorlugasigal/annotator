using Annotator.Core;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// How a canvas on the overlay thread and the workspace on the app thread agree: chained edits, results shown before the
/// workspace confirms them, refusals, interruptions, blocked input and displays the workspace has not prepared.
/// </summary>
public sealed class CanvasContractTests
{
    [Fact]
    public void TheCanvasShowsItsEditUntilTheWorkspaceConfirmsItAndChainsTheNextOne()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Rectangle);
        h.Down(Main, new Point(10, 10), pump: false);
        h.Move(Main, new Point(60, 60), pump: false);
        h.Up(Main, new Point(60, 60), pump: false);
        Assert.Empty(h.Elements(Main));
        Assert.Single(h[Main].State.Elements);
        Assert.False(h[Main].State.HasActiveEdit);

        // A second gesture before the workspace answered builds on the version the first one predicts.
        h.Down(Main, new Point(100, 100), pump: false);
        h.Move(Main, new Point(160, 160), pump: false);
        h.Up(Main, new Point(160, 160), pump: false);
        Assert.Equal(2, h[Main].State.Elements.Count);
        Assert.Empty(h.Elements(Main));

        h.Pump();
        Assert.Equal(2, h.Elements(Main).Length);
        Assert.Equal(h.Elements(Main), h[Main].State.Elements);
        Assert.All(h.Channel.Results, result => Assert.IsType<EditResult.Accepted>(result));
        h.Model.Undo();
        Assert.Single(h.Elements(Main));
    }

    [Fact]
    public void ARefusedEditFallsBackToTheWorkspaceDocument()
    {
        using CanvasHarness h = Single();
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 80, 60));
        h.Choose(Tool.Select);
        h.Model.ReplaceDocumentForTesting(Main, CanvasDrawingTests.CanvasDocumentWithSelection([shape], shape.Id));
        h.Model.BeginStyleEdit();
        h.Pump();
        h.Drag(Main, shape.Frame.Center, shape.Frame.Center + new Point(50, 0));
        Assert.Equal(1UL, h.Model.Display(Main).RefusedEdits);
        Assert.Equal(shape.Frame, h.Elements(Main)[0].Frame);
        Assert.Equal(shape.Frame, h[Main].State.Elements[0].Frame);
        Assert.Null(h.Model.Display(Main).ActiveEdit);
        h.Model.FinishStyleEdit();
        h.Drag(Main, shape.Frame.Center, shape.Frame.Center + new Point(50, 0));
        Assert.Equal(shape.Frame.Translated(new Point(50, 0)), h.Elements(Main)[0].Frame);
    }

    [Fact]
    public void WorkspaceChangesDuringAGestureCancelIt()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        h.Down(Main, new Point(10, 10));
        h.Move(Main, new Point(20, 20));
        Assert.NotNull(h[Main].State.Draft);
        h.Model.UseApps();
        h.Pump();
        Assert.Null(h[Main].State.Draft);
        Assert.False(h[Main].HasActiveEdit);
        Assert.False(h[Main].State.IsDrawing);
        h.Move(Main, new Point(30, 30));
        h.Up(Main, new Point(30, 30));
        Assert.Empty(h.Elements(Main));

        h.Model.BeginDrawing();
        h.Down(Main, new Point(10, 10));
        h.Move(Main, new Point(20, 20));
        h.Model.Undo();
        h.Pump();
        Assert.False(h[Main].HasActiveEdit);
        Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);
        h.Up(Main, new Point(30, 30));
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void BlockedInputAndUnpreparedDisplaysTakeNoGestures()
    {
        using CanvasHarness h = Single();
        WorkspaceSnapshot snapshot = h.Model.Snapshot;
        h[Main].Update(snapshot, snapshot.Display(Main), inputBlocked: true);
        h[Main].OnPointer(new PointerEvent { Phase = PointerPhase.Down, ChangedButton = PointerButtons.Primary, Samples = new[] { new PointerSample(new Point(5, 5)) } });
        Assert.False(h[Main].HasActiveEdit);
        Assert.Equal(0, h.Channel.Pending);

        using var lone = new CanvasController(new DisplayId("unprepared"), h.Channel, new CanvasOptions { TextMeasurer = new FixedTextMeasurer() });
        lone.Update(snapshot, null);
        lone.OnPointer(new PointerEvent { Phase = PointerPhase.Down, ChangedButton = PointerButtons.Primary, Samples = new[] { new PointerSample(new Point(5, 5)) } });
        Assert.False(lone.HasActiveEdit);
        Assert.False(lone.OnKey(new KeyEvent(VirtualKeys.Escape)));
        Assert.True(lone.State.Elements.IsEmpty);
        Assert.False(lone.State.IsDrawing);
    }

    [Fact]
    public void EscapeAndReleaseWorkInTheMacOrder()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Text);
        h.Down(Main, new Point(50, 50));
        h.Type("Note");
        Assert.True(h.Key(Main, VirtualKeys.Escape));
        Assert.Equal("Note", h.Elements(Main)[0].Text);
        Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);

        h.Choose(Tool.Pen);
        h.Down(Main, new Point(200, 200));
        Assert.True(h.Key(Main, VirtualKeys.Escape));
        Assert.False(h[Main].HasActiveEdit);
        Assert.Equal(WorkspaceMode.Draw, h.Model.Snapshot.Mode);
        h.Up(Main, new Point(210, 210));
        Assert.Single(h.Elements(Main));

        Assert.True(h.Key(Main, VirtualKeys.Escape));
        Assert.Equal(WorkspaceMode.UseApps, h.Model.Snapshot.Mode);
        Assert.False(h.Key(Main, VirtualKeys.Escape), "A canvas that does not take drawing input leaves Escape to Windows.");
    }

    [Fact]
    public void PointerCancelEndsTheGestureWithoutAChange()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Rectangle);
        h.Down(Main, new Point(10, 10));
        h.Move(Main, new Point(90, 90));
        h[Main].OnPointer(new PointerEvent { Phase = PointerPhase.Cancel, PointerId = 1, Samples = new[] { new PointerSample(new Point(90, 90)) } });
        h.Pump();
        Assert.False(h[Main].HasActiveEdit);
        Assert.Null(h[Main].State.Draft);
        Assert.Empty(h.Elements(Main));
        Assert.Null(h.Model.Display(Main).ActiveEdit);
    }

    [Fact]
    public void AnotherPointerWaitsUntilTheGestureEnds()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        h.Down(Main, new Point(10, 10));
        h[Main].OnPointer(new PointerEvent
        {
            Phase = PointerPhase.Down, PointerId = 2, Device = PointerDevice.Touch, ChangedButton = PointerButtons.Primary,
            Samples = new[] { new PointerSample(new Point(500, 500)) },
        });
        h[Main].OnPointer(new PointerEvent
        {
            Phase = PointerPhase.Move, PointerId = 2, Device = PointerDevice.Touch, Buttons = PointerButtons.Primary,
            Samples = new[] { new PointerSample(new Point(520, 520)) },
        });
        h.Move(Main, new Point(20, 20));
        h.Up(Main, new Point(20, 20));
        Element stroke = Assert.Single(h.Elements(Main));
        Assert.Equal([new Point(10, 10), new Point(20, 20)], stroke.Points);
    }

    [Fact]
    public void OneFingerTouchDrawsLikeAMouse()
    {
        // D-9: one-finger touch draws like a mouse.
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        h[Main].OnPointer(new PointerEvent
        {
            Phase = PointerPhase.Down, PointerId = 7, Device = PointerDevice.Touch, ChangedButton = PointerButtons.Primary,
            Buttons = PointerButtons.Primary, Samples = new[] { new PointerSample(new Point(10, 10), 0, 1) },
        });
        h[Main].OnPointer(new PointerEvent
        {
            Phase = PointerPhase.Up, PointerId = 7, Device = PointerDevice.Touch, ChangedButton = PointerButtons.Primary,
            Samples = new[] { new PointerSample(new Point(15, 10), 0, 9), new PointerSample(new Point(30, 10), 0, 17) },
        });
        h.Pump();
        Element stroke = Assert.Single(h.Elements(Main));
        Assert.Equal([new Point(10, 10), new Point(15, 10), new Point(30, 10)], stroke.Points);
        Assert.Equal([1.0, 1.0, 1.0], stroke.Pressures);
    }

    [Fact]
    public void MultiDisplayEditsNeedTheOtherDisplaysFree()
    {
        // Mac finishDisplayDrag refuses while the other display is editing.
        WorkspaceModel model = TestWorkspace.Drawing();
        DisplaySnapshot primary = model.Snapshot.Display(TestWorkspace.Primary)!;
        DisplaySnapshot left = model.Snapshot.Display(TestWorkspace.Left)!;
        TestWorkspace.AssertAccepted(model.BeginEdit(TestWorkspace.Left, left.Version, EditKind.Text));
        Guid operation = Guid.NewGuid();
        CanvasDocument departure = primary.Document;
        CanvasDocument arrival = model.Snapshot.Display(TestWorkspace.Left)!.Document;
        departure.Begin();
        departure.Commit(operation);
        arrival.Begin();
        arrival.Add(new Element(Tool.Rectangle));
        arrival.Commit(operation);
        EditResult.Rejected refused = TestWorkspace.AssertRejected(model.Submit(new EditSubmission(
            [new DisplayEdit(TestWorkspace.Left, left.Version, arrival), new DisplayEdit(TestWorkspace.Primary, primary.Version, departure)],
            operation) { Origin = TestWorkspace.Primary }), EditRejection.StillEditing);
        Assert.Equal("The drawing could not be opened: Finish the edit on the other display before moving these objects.", refused.Message);
        Assert.Equal(1UL, model.Snapshot.Display(TestWorkspace.Primary)!.RefusedEdits);
        Assert.Equal(0UL, model.Snapshot.Display(TestWorkspace.Left)!.RefusedEdits);
        Assert.Equal(EditKind.Text, model.Snapshot.Display(TestWorkspace.Left)!.ActiveEdit);
    }

    [Fact]
    public void TextEditsShowTheirStyleAndSelectionOnTheToolbar()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        DisplaySnapshot display = model.Snapshot.Display(TestWorkspace.Primary)!;
        TestWorkspace.AssertAccepted(model.BeginEdit(TestWorkspace.Primary, display.Version, EditKind.Text));
        Assert.Equal(Tool.Text, model.Snapshot.EffectiveTool);
        var style = Style.Defaults(Tool.Text);
        style.FontSize = 61;
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.TextStyle, TestWorkspace.Primary) { Style = style }));
        Assert.Equal(style, model.Snapshot.Style);
        Assert.Equal(style, model.Snapshot.Display(TestWorkspace.Primary)!.TextStyle);
        CanvasDocument typing = display.Document;
        var text = new Element(Tool.Text, new Rect(5, 5, 60, 30), text: "Hi", style: style);
        typing.Add(text);
        typing.Select([text.Id]);
        int published = 0;
        model.Published += _ => published++;
        TestWorkspace.AssertAccepted(model.UpdateEdit(TestWorkspace.Primary, display.Version, typing));
        Assert.Equal(1, published);
        Assert.Equal(1, model.Snapshot.SelectionCount);

        model.ApplyStyle((ref Style changed) => changed.Bold = true);
        Assert.True(model.Snapshot.Display(TestWorkspace.Primary)!.TextStyle!.Value.Bold);
        Assert.True(model.Snapshot.Style.Bold);
        Assert.True(model.Snapshot.Preferences.ToolStyles[Tool.Text.RawValue].Bold);
        Assert.Equal(display.Version, model.Snapshot.Display(TestWorkspace.Primary)!.Version);
        model.UseApps();
        Assert.Equal([text], model.Snapshot.Display(TestWorkspace.Primary)!.Document.Elements);
    }
}
