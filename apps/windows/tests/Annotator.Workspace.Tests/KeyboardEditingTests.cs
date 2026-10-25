using Annotator.Core;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

/// <summary>Ports of the model assertions in apps/macos/Tests/NativeAppTests/KeyboardEditingTests.swift and the canvas key commands.</summary>
public sealed class KeyboardEditingTests
{
    [Fact]
    public void NudgingSelectionHonorsStepSizeLocksAndUndo()
    {
        // testNudgingSelectionHonorsStepSizeLocksAndUndo: arrows move 1 point, Shift+arrows 10; locked objects stay.
        WorkspaceModel model = Drawing();
        var rectangle = new Element(Tool.Rectangle, new Rect(80, 70, 120, 60));
        var locked = new Element(Tool.Cover, new Rect(250, 70, 50, 50)) { Locked = true };
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(rectangle);
            document[0].Add(locked);
            document[0].Select([rectangle.Id, locked.Id]);
        }));

        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Nudge, Primary) { Offset = new Point(1, 0) }));
        Assert.Equal(81, model.Elements(Primary)[0].Frame.X);
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Nudge, Primary) { Offset = new Point(0, 10) }));
        Assert.Equal(80, model.Elements(Primary)[0].Frame.Y);
        Assert.Equal(locked.Frame, model.Elements(Primary)[1].Frame);
        model.Undo();
        Assert.Equal(70, model.Elements(Primary)[0].Frame.Y);
        model.Undo();
        Assert.Equal(rectangle.Frame, model.Elements(Primary)[0].Frame);

        AssertAccepted(model.Edit(Primary, document => document[0].Select([locked.Id])));
        model.Perform(new CanvasCommand(CanvasCommandKind.Nudge, Primary) { Offset = new Point(-1, 0) });
        Assert.Equal(locked.Frame, model.Elements(Primary)[1].Frame);
        Assert.Contains("locked", model.Snapshot.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeCancelsAGestureFirstThenReleasesInput()
    {
        // Mac keyDown(Escape): commit text, cancel a gesture, then release input.
        WorkspaceModel model = Drawing();
        DisplaySnapshot display = model.Display(Primary);
        AssertAccepted(model.BeginEdit(Primary, display.Version, EditKind.Gesture));
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Escape, Primary)));
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Null(model.Display(Primary).ActiveEdit);
        Assert.NotEqual(display.Version, model.Display(Primary).Version);

        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Escape, Primary)));
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.All(model.Snapshot.Displays, snapshot => Assert.False(snapshot.TakesInput));
    }

    [Fact]
    public void EscapeCommitsTheLastReportedText()
    {
        WorkspaceModel model = Drawing();
        DisplaySnapshot display = model.Display(Primary);
        AssertAccepted(model.BeginEdit(Primary, display.Version, EditKind.Text));
        CanvasDocument typing = display.Document;
        typing.Begin();
        var text = new Element(Tool.Text, new Rect(10, 10, 80, 30), text: "Hello");
        typing.Add(text);
        AssertAccepted(model.UpdateEdit(Primary, display.Version, typing));

        model.Perform(new CanvasCommand(CanvasCommandKind.Escape, Primary));

        Assert.Equal([text], model.Elements(Primary));
        Assert.False(model.Document(Primary).IsEditing);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        model.Undo();
        Assert.Empty(model.Elements(Primary));
    }

    [Fact]
    public void ReleaseInputActsLikeUseApps()
    {
        // Alt+F4 or a close request on an overlay (WIN-OVR-12).
        WorkspaceModel model = Drawing();
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.ReleaseInput, Left)));
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.False(model.Snapshot.WorkspaceHidden);
        Assert.Equal(CanvasCommandResult.UnknownDisplay, model.Perform(new CanvasCommand(CanvasCommandKind.Undo, new DisplayId("missing"))));
    }

    [Fact]
    public void KeyCommandsMakeTheirDisplayTheEditingTarget()
    {
        WorkspaceModel model = Drawing();
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        model.Perform(new CanvasCommand(CanvasCommandKind.SelectAll, Left));
        Assert.Equal(Left, model.Snapshot.ActiveDisplay);
        Assert.Equal(Tool.Select, model.Snapshot.Tool);
    }

    [Fact]
    public void DeleteCancelsAnEditBeforeDeleting()
    {
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 40, 40));
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        DisplaySnapshot display = model.Display(Primary);
        AssertAccepted(model.BeginEdit(Primary, display.Version, EditKind.Gesture));
        Assert.Equal("Cancel current drawing", model.Snapshot.DeleteActionLabel);
        Assert.Equal("xmark.circle", model.Snapshot.DeleteActionSymbol);
        model.DeleteCurrent();
        Assert.Equal([shape], model.Elements(Primary));
        Assert.Equal("Delete selection", model.Snapshot.DeleteActionLabel);
        model.DeleteCurrent();
        Assert.Empty(model.Elements(Primary));
        Assert.Equal("Delete", model.Snapshot.DeleteActionLabel);
    }

    [Fact]
    public void LockedSelectionsAreNotDeleted()
    {
        WorkspaceModel model = Drawing();
        var locked = new Element(Tool.Rectangle) { Locked = true };
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(locked);
            document[0].Select([locked.Id]);
        }));
        Assert.True(model.Snapshot.SelectionIsLocked);
        Assert.Equal("Unlock selection to delete", model.Snapshot.DeleteActionLabel);
        Assert.False(model.Snapshot.CanDeleteCurrent);
        model.DeleteSelected();
        Assert.Equal([locked], model.Elements(Primary));
        Assert.Equal("The selection is locked. Unlock it from Selection actions before deleting.", model.Snapshot.Status);
    }

    [Fact]
    public void RedoWaitsForTheCurrentEdit()
    {
        WorkspaceModel model = Drawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Rectangle)));
        model.Undo();
        Assert.True(model.Snapshot.CanRedo);
        DisplaySnapshot display = model.Display(Primary);
        AssertAccepted(model.BeginEdit(Primary, display.Version, EditKind.Gesture));
        Assert.False(model.Snapshot.CanRedo);
        Assert.True(model.Snapshot.CanUndo);
        model.Redo();
        Assert.Equal("Finish or cancel the current edit before redoing.", model.Snapshot.Status);
        Assert.Empty(model.Elements(Primary));
        model.Undo();
        Assert.Null(model.Display(Primary).ActiveEdit);
        model.Redo();
        Assert.Single(model.Elements(Primary));
    }

    [Fact]
    public void InkKeysChangeTheToolColorAndPersistIt()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        model.BeginDrawing();
        model.Choose(Tool.Pen);
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.InkKey, Primary) { Key = "b" }));
        Assert.Equal("Blue ink", model.Snapshot.Status);
        Assert.Equal(Palette.Blue, model.Snapshot.Style.Color);
        Assert.Null(model.Snapshot.Style.StrokeGradient);
        Assert.Equal(Palette.Blue, model.Snapshot.Preferences.ToolStyles["pen"].Color);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(Palette.Blue, WorkspacePreferences.Read(store.Data!).ToolStyles["pen"].Color);
        Assert.Equal(CanvasCommandResult.NotHandled, model.Perform(new CanvasCommand(CanvasCommandKind.InkKey, Primary) { Key = "z" }));

        WorkspaceModel restarted = Create(store: store);
        restarted.BeginDrawing();
        restarted.Choose(Tool.Pen);
        Assert.Equal(Palette.Blue, restarted.Snapshot.Style.Color);
    }

    [Fact]
    public void InkKeysColorTheSelectionAsOneUndoStep()
    {
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle);
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        model.ApplyInkKey("g");
        Assert.Equal(new RGBA(0.28, 0.69, 0.4), model.Elements(Primary)[0].Style.Color);
        Assert.Equal("Green ink", model.Snapshot.Status);
        model.Undo();
        Assert.Equal(shape.Style.Color, model.Elements(Primary)[0].Style.Color);
    }

    [Fact]
    public void InkKeysDoNotApplyToCovers()
    {
        WorkspaceModel model = Drawing();
        var cover = new Element(Tool.Cover, new Rect(0, 0, 50, 50));
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(cover);
            document[0].Select([cover.Id]);
        }));
        Assert.True(model.ApplyInkKey("r"));
        Assert.Equal("Color keys apply to drawing and text tools.", model.Snapshot.Status);
        Assert.Equal([cover], model.Elements(Primary));
    }
}
