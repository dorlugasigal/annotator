using Annotator.Core;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

/// <summary>Ports of the model assertions in apps/macos/Tests/NativeAppTests/ResumeDrawingTests.swift (spec 002, "Return to drawing").</summary>
public sealed class ResumeDrawingTests
{
    [Fact]
    public void ShowHideRevealPreparesDrawingModeAndPreservesSelectionAndHistory()
    {
        // testControl2RevealPreparesDrawingModeAndPreservesSelectionAndHistory (the no-motion pass).
        WorkspaceModel model = Create();
        model.ResumeDrawing();
        DisplayId session = Assert.IsType<DisplayId>(model.Snapshot.ActiveDisplay);
        var shape = new Element(Tool.Rectangle, new Rect(100, 100, 100, 80));
        AssertAccepted(model.Edit(session, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        ulong generation = model.Document(session).Generation;

        model.HideTools();
        model.ToggleTools();

        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.True(model.Display(session).TakesInput);
        Assert.Equal([shape.Id], model.Document(session).Selection);
        Assert.Equal(generation, model.Document(session).Generation);
        model.ResumeDrawing();
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.True(model.Display(session).TakesInput);
    }

    [Fact]
    public void ShowHideRevealCreatesADrawingSessionAfterAnEmptyStartup()
    {
        // testControl2RevealCreatesADrawingSessionAfterAnEmptyStartup.
        WorkspaceModel model = Create();
        model.ShowToolbar();
        Assert.Null(model.Snapshot.ActiveDisplay);
        Assert.Equal(WorkspaceMode.Off, model.Snapshot.Mode);
        Assert.Equal(SurfaceMode.Hidden, model.Snapshot.Surfaces);

        model.ToggleTools();
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.False(model.Snapshot.ToolbarVisible);

        model.ToggleTools();
        Assert.False(model.Snapshot.WorkspaceHidden);
        Assert.True(model.Snapshot.ToolbarVisible);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        DisplayId active = Assert.IsType<DisplayId>(model.Snapshot.ActiveDisplay);
        Assert.True(model.Display(active).TakesInput);
        Assert.Equal(SurfaceMode.Draw, model.Snapshot.Surfaces);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopResumePreservesSelectionAndHistoryAndNeverHides(bool hide)
    {
        // testDesktopResumePreservesSelectionAndHistoryAndNeverHides.
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.ResumeDrawing();
        DisplayId session = Assert.IsType<DisplayId>(model.Snapshot.ActiveDisplay);
        var shape = new Element(Tool.Rectangle, new Rect(40, 50, 80, 60));
        AssertAccepted(model.Edit(session, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        ulong generation = model.Document(session).Generation;

        if (hide)
        {
            model.HideTools();
        }
        else
        {
            model.UseApps();
        }
        for (int press = 0; press < 3; press++)
        {
            model.ResumeDrawing();
        }

        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.False(model.Snapshot.WorkspaceHidden);
        Assert.True(model.Snapshot.ToolbarVisible);
        Assert.True(model.Display(session).TakesInput);
        Assert.Equal([shape], model.Elements(session));
        Assert.Equal([shape.Id], model.Document(session).Selection);
        Assert.Equal(generation, model.Document(session).Generation);
        model.Undo();
        Assert.Empty(model.Elements(session));
    }

    [Fact]
    public void RepeatedResumeAfterHideNeverHidesAgain()
    {
        // testResumeReversesHideAndRepeatedPressDoesNotRestartShow, without motion: Resume reverses Hide and
        // later presses keep drawing.
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.ResumeDrawing();
        model.HideTools();
        Assert.True(model.Snapshot.WorkspaceHidden);
        for (int press = 0; press < 8; press++)
        {
            model.ResumeDrawing();
            Assert.False(model.Snapshot.WorkspaceHidden);
            Assert.True(model.Snapshot.ToolbarVisible);
            Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        }
    }

    [Fact]
    public void HideOnlyAlwaysHidesAndShowHideToggles()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.HideTools();
        model.HideTools();
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.False(model.Snapshot.ToolsPresented);
        Assert.Equal(SurfaceMode.Hidden, model.Snapshot.Surfaces);

        model.ToggleTools();
        Assert.True(model.Snapshot.ToolsPresented);
        Assert.Equal(SurfaceMode.Draw, model.Snapshot.Surfaces);
        model.ToggleTools();
        Assert.False(model.Snapshot.ToolsPresented);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.Equal(SurfaceMode.Hidden, model.Snapshot.Surfaces);
    }

    [Fact]
    public void UseAppsMakesEveryDisplayClickThroughAtOnce()
    {
        // WIN-OVR-02: no display keeps input alone.
        WorkspaceModel model = Drawing();
        Assert.All(model.Snapshot.Displays, display => Assert.True(display.TakesInput));
        model.UseApps();
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.Equal(SurfaceMode.UseApps, model.Snapshot.Surfaces);
        Assert.All(model.Snapshot.Displays, display => Assert.False(display.TakesInput));
        Assert.Equal("Interact: your apps receive clicks; drawings remain visible.", model.Snapshot.Status);
    }

    [Fact]
    public void EnteringDrawRequestsFocusForTheTargetDisplayOnce()
    {
        WorkspaceModel model = Create(pointer: () => new Point(-400, 300));
        model.ShowToolbar();
        model.BeginDrawing();
        FocusRequest first = Assert.IsType<FocusRequest>(model.Snapshot.FocusRequest);
        Assert.Equal(Left, first.Display);
        Assert.Equal(Left, model.Snapshot.ActiveDisplay);
        Assert.Equal("Draw on any display. Escape returns to Interact.", model.Snapshot.Status);

        model.UseApps();
        Assert.Equal(first, model.Snapshot.FocusRequest);
        model.ResumeDrawing();
        FocusRequest second = Assert.IsType<FocusRequest>(model.Snapshot.FocusRequest);
        Assert.True(second.Serial > first.Serial);
        Assert.Equal(Left, second.Display);
    }

    [Fact]
    public void DrawTargetsTheLastUsedDisplayBeforeThePointer()
    {
        Point pointer = new(10, 10);
        WorkspaceModel model = Create(pointer: () => pointer);
        model.BeginDrawing();
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        AssertAccepted(model.Add(Left, new Element(Tool.Ellipse, new Rect(5, 5, 20, 20))));
        Assert.Equal(Left, model.Snapshot.ActiveDisplay);
        model.UseApps();
        model.BeginDrawing();
        Assert.Equal(Left, model.Snapshot.FocusRequest?.Display);
    }

    [Fact]
    public void NoDisplaysReportsAndStaysOff()
    {
        WorkspaceModel model = Create(displays: () => []);
        model.BeginDrawing();
        Assert.Equal(WorkspaceMode.Off, model.Snapshot.Mode);
        Assert.Equal("The drawing could not be opened: No attached display is available.", model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void DuplicateDisplayIdentitiesAreRefused()
    {
        WorkspaceModel model = Create(displays: () => [PrimaryDisplay, PrimaryDisplay]);
        model.BeginDrawing();
        Assert.Equal(WorkspaceMode.Off, model.Snapshot.Mode);
        Assert.Equal("The drawing could not be opened: The display list contains duplicate identities.", model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void DisplaySourceFailureIsReported()
    {
        WorkspaceModel model = Create(displays: () => throw new IOException("The display list could not be read."));
        model.BeginDrawing();
        Assert.Equal("The display list could not be read.", model.Snapshot.ErrorMessage);
        Assert.Equal(WorkspaceMode.Off, model.Snapshot.Mode);
    }
}
