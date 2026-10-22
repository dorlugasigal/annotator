using Annotator.Core;
using static Annotator.Workspace.Tests.TestWorkspace;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.Workspace.Tests.Motion;

/// <summary>
/// The workspace side of animated show and hide (spec 009, WIN-MOT-02): ports of the model assertions in
/// WorkspaceVisibilityTests.swift (global toggle, rapid reversal, large boards, Off, Reduce Motion, suspension).
/// </summary>
public sealed class VisibilityTransitionTests
{
    private static WorkspaceModel Animated(bool reduced = false, Style? style = null, bool? animate = null)
    {
        var preferences = new WorkspacePreferences { VisibilityAnimationStyle = style, AnimateVisibility = animate };
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [PrimaryDisplay, LeftDisplay],
            Pointer = () => new Point(10, 10),
            PreferencesStore = new MemoryPreferencesStore(preferences.Data()),
            AnimatesVisibility = true,
            ReducesMotion = () => reduced,
        });
        return model;
    }

    private static void Complete(WorkspaceModel model) =>
        model.CompleteVisibilityTransition(Assert.IsType<VisibilityTransition>(model.Snapshot.Visibility).Serial);

    [Fact]
    public void HidingAnimatesUntilTheHostCompletesIt()
    {
        WorkspaceModel model = Animated();
        model.ResumeDrawing();
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        model.HideTools();
        WorkspaceSnapshot hiding = model.Snapshot;
        VisibilityTransition transition = Assert.IsType<VisibilityTransition>(hiding.Visibility);
        Assert.False(transition.Showing);
        Assert.Equal(Style.Slide, transition.Style);
        Assert.True(hiding.WorkspaceHidden);
        Assert.True(hiding.ToolbarVisible, "The toolbar stays until its motion ends.");
        Assert.False(hiding.ToolsPresented);
        Assert.Equal(WorkspaceMode.UseApps, hiding.Mode);
        Assert.Equal(SurfaceMode.Hidden, hiding.Surfaces);
        Assert.All(hiding.Displays, display => Assert.False(display.TakesInput));
        model.CompleteVisibilityTransition(transition.Serial);
        Assert.Null(model.Snapshot.Visibility);
        Assert.False(model.Snapshot.ToolbarVisible);
        Assert.True(model.Snapshot.WorkspaceHidden);
    }

    [Fact]
    public void GlobalToggleUsesVisibilityRatherThanDrawingMode()
    {
        // WorkspaceVisibilityTests.testGlobalToggleUsesVisibilityRatherThanDrawingMode.
        WorkspaceModel model = Animated();
        model.ToggleTools();
        model.UseApps();
        Assert.True(model.Snapshot.ToolbarVisible);
        model.ToggleTools();
        Assert.True(model.Snapshot.WorkspaceHidden);
        model.ToggleTools();
        model.ToggleTools();
        Complete(model);
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.False(model.Snapshot.ToolbarVisible);
        model.ToggleTools();
        VisibilityTransition show = Assert.IsType<VisibilityTransition>(model.Snapshot.Visibility);
        Assert.True(show.Showing);
        Assert.True(show.RevealDrawing, "Ctrl+2 reveals ready to draw; drawing state is prepared before snapshots.");
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.All(model.Snapshot.Displays, display => Assert.False(display.TakesInput));
        model.CompleteVisibilityTransition(show.Serial);
        Assert.False(model.Snapshot.WorkspaceHidden);
        Assert.True(model.Snapshot.ToolbarVisible);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        DisplayId active = Assert.IsType<DisplayId>(model.Snapshot.ActiveDisplay);
        Assert.True(model.Display(active).TakesInput);
    }

    [Fact]
    public void RapidReversalCancelsStaleCompletions()
    {
        // WorkspaceVisibilityTests.testEveryStyleRapidReversalCancelsStaleCompletionAndRestoresControls.
        foreach (Style style in VisibilityAnimationStyle.Available)
        {
            if (style == Style.None)
            {
                continue;
            }
            WorkspaceModel model = Animated(style: style);
            model.ResumeDrawing();
            DisplayId session = model.Snapshot.ActiveDisplay!.Value;
            var cover = new Element(Tool.Rectangle, new Rect(80, 80, 60, 60));
            AssertAccepted(model.Add(session, cover));
            var serials = new List<ulong>();
            model.HideTools();
            serials.Add(model.Snapshot.Visibility!.Serial);
            model.ShowToolbar();
            serials.Add(model.Snapshot.Visibility!.Serial);
            model.HideTools();
            serials.Add(model.Snapshot.Visibility!.Serial);
            model.ShowToolbar();
            ulong last = model.Snapshot.Visibility!.Serial;
            Assert.Equal(serials.Count, serials.Distinct().Count());
            foreach (ulong stale in serials)
            {
                model.CompleteVisibilityTransition(stale);
                Assert.NotNull(model.Snapshot.Visibility);
            }
            model.CompleteVisibilityTransition(last);
            Assert.Null(model.Snapshot.Visibility);
            Assert.False(model.Snapshot.WorkspaceHidden, $"{style}");
            Assert.True(model.Snapshot.ToolbarVisible);
            Assert.Equal([cover], model.Elements(session));
        }
    }

    [Fact]
    public void AShowThatIsRunningIgnoresAnotherShow()
    {
        WorkspaceModel model = Animated();
        model.ShowToolbar();
        model.HideTools();
        Complete(model);
        model.ShowToolbar();
        ulong serial = model.Snapshot.Visibility!.Serial;
        model.ShowToolbar();
        model.OpenDrawing();
        Assert.Equal(serial, model.Snapshot.Visibility!.Serial);
        model.CompleteVisibilityTransition(serial);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
    }

    [Fact]
    public void UseAppsDuringAShowWithdrawsTheDrawingRequest()
    {
        // Mac performMenuAction(.useApps) during a show: resumeAfterShowing and the reveal's drawing request are dropped.
        WorkspaceModel model = Animated();
        model.ResumeDrawing();
        model.HideTools();
        Complete(model);
        model.ResumeDrawing();
        Assert.True(model.Snapshot.Visibility!.RevealDrawing);
        model.UseApps();
        Complete(model);
        Assert.False(model.Snapshot.WorkspaceHidden);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.All(model.Snapshot.Displays, display => Assert.False(display.TakesInput));
    }

    [Fact]
    public void ResumeDuringAShowEntersDrawWhenItEnds()
    {
        WorkspaceModel model = Animated();
        model.ShowToolbar();
        model.BeginDrawing();
        model.HideTools();
        Complete(model);
        model.ShowToolbar();
        Assert.False(model.Snapshot.Visibility!.RevealDrawing);
        model.ResumeDrawing();
        Complete(model);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Contains(model.Snapshot.Displays, display => display.TakesInput);
    }

    [Fact]
    public void LargeBoardsShowAndHideAtOnceWithAMessage()
    {
        // WorkspaceVisibilityTests.testLargeBoardsSkipCosmeticWindowsWithoutDroppingShapes.
        WorkspaceModel model = Animated(style: Style.Dissolve);
        model.ResumeDrawing();
        DisplayId session = model.Snapshot.ActiveDisplay!.Value;
        Element[] shapes = [.. Enumerable.Range(0, 65).Select(index => new Element(Tool.Rectangle, new Rect(100 + index, 100, 20, 20)))];
        AssertAccepted(model.Add(session, shapes));
        model.HideTools();
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.Null(model.Snapshot.Visibility);
        Assert.False(model.Snapshot.ToolbarVisible);
        Assert.Contains("64", model.Snapshot.Status);
        model.ShowToolbar();
        Assert.Null(model.Snapshot.Visibility);
        Assert.Equal(shapes, model.Elements(session));
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
    }

    [Fact]
    public void OffNeverAnimatesAndReduceMotionOnlyCrossfades()
    {
        // WorkspaceVisibilityTests.testOffNeverCreatesTimelinesAndReduceMotionOnlyCrossfades (the model's part).
        foreach (Style style in VisibilityAnimationStyle.Available)
        {
            WorkspaceModel off = Animated(style: style, animate: false);
            off.ResumeDrawing();
            off.HideTools();
            Assert.Null(off.Snapshot.Visibility);
            Assert.False(off.Snapshot.ToolbarVisible);
            off.ShowToolbar();
            Assert.Null(off.Snapshot.Visibility);
            Assert.False(off.Snapshot.WorkspaceHidden);
        }
        WorkspaceModel none = Animated(style: Style.None);
        none.ResumeDrawing();
        none.HideTools();
        Assert.Null(none.Snapshot.Visibility);
        WorkspaceModel reduced = Animated(reduced: true, style: Style.Magic);
        reduced.ResumeDrawing();
        reduced.HideTools();
        Assert.Equal(Style.Fade, reduced.Snapshot.Visibility!.Style);
        Assert.Equal(Style.Slide, Animated(style: Style.Fluid).WithHidden().Snapshot.Visibility!.Style);
        Assert.Equal(Style.Fade, Animated(style: Style.Curtain).WithHidden().Snapshot.Visibility!.Style);
    }

    [Fact]
    public void WithoutAnimationsTheHostSeesNoTransition()
    {
        WorkspaceModel model = Create();
        model.ResumeDrawing();
        model.HideTools();
        Assert.Null(model.Snapshot.Visibility);
        Assert.False(model.Snapshot.ToolbarVisible);
    }

    [Fact]
    public void DisplayChangesCancelAPendingShowWithoutReactivatingInput()
    {
        // WorkspaceVisibilityTests.testSystemSuspensionCancelsPendingShowWithoutReactivatingInput.
        WorkspaceModel model = Animated(style: Style.Curtain);
        model.ResumeDrawing();
        DisplayId session = model.Snapshot.ActiveDisplay!.Value;
        var shape = new Element(Tool.Rectangle, new Rect(40, 40, 80, 60));
        AssertAccepted(model.Add(session, shape));
        model.HideTools();
        Complete(model);
        model.ToggleTools();
        ulong serial = model.Snapshot.Visibility!.Serial;
        model.DisplaysChanged();
        Assert.Null(model.Snapshot.Visibility);
        model.CompleteVisibilityTransition(serial);
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.False(model.Snapshot.ToolbarVisible);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.All(model.Snapshot.Displays, display => Assert.False(display.TakesInput));
        Assert.Equal([shape], model.Elements(session));
    }

    [Fact]
    public void HidingWhatIsHiddenDoesNothing()
    {
        WorkspaceModel model = Animated();
        model.ResumeDrawing();
        model.HideTools();
        Complete(model);
        model.HideTools();
        Assert.Null(model.Snapshot.Visibility);
        Assert.False(model.Snapshot.ToolbarVisible);
    }
}

file static class TransitionTestExtensions
{
    public static WorkspaceModel WithHidden(this WorkspaceModel model)
    {
        model.ResumeDrawing();
        model.HideTools();
        return model;
    }
}
