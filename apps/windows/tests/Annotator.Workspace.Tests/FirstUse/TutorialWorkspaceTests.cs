using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Tutorial;
using Annotator.Workspace.FirstUse;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.FirstUse;

/// <summary>
/// The tutorial's launch rules, words, demonstrations and a whole run against a real workspace (Mac TutorialTests, the parts
/// that need no window).
/// </summary>
public sealed class TutorialWorkspaceTests
{
    private static Element Box(double x = 100) => new(Tool.Rectangle, new Rect(x, 100, 120, 80));

    [Fact]
    public void AWholeRunOnARealWorkspaceAcceptsKeysAndClicks()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        var progress = new TutorialProgress(TutorialObserver.Observe(model.Snapshot));
        TutorialChange Look() => progress.Observe(TutorialObserver.Observe(model.Snapshot));

        model.ResumeDrawing();
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Rectangle, progress.Step);
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Rectangle));
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Shape, progress.Step);
        AssertAccepted(model.Add(Primary, Box()));
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Color, progress.Step);
        Assert.True(model.ApplyInkKey("b"));
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Arrow, progress.Step);

        model.Choose(Tool.Arrow);
        Assert.Equal(TutorialChange.Goal, Look());
        model.ApplyStyle((ref Style style) => style.Curved = true);
        Assert.Equal(TutorialChange.Goal, Look());
        var arrow = new Element(Tool.Arrow, points: [new Point(10, 10), new Point(60, 0), new Point(120, 40)],
            style: model.Snapshot.Style);
        AssertAccepted(model.Add(Primary, arrow));
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Pen, progress.Step);

        model.Choose(Tool.Pen);
        Assert.Equal(TutorialChange.Goal, Look());
        AssertAccepted(model.Add(Left, new Element(Tool.Pen, points: [new Point(5, 5), new Point(40, 30), new Point(10, 60)])));
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Move, progress.Step);

        model.Choose(Tool.Select);
        Assert.Equal(TutorialChange.Goal, Look());
        AssertAccepted(model.Edit(Primary, document =>
        {
            Guid first = document[0].Elements[0].Id;
            document[0].Update(first, (ref Element element) => element.Frame = element.Frame.Translated(new Point(40, 10)));
        }));
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Undo, progress.Step);

        ulong before = model.Snapshot.UndoSerial;
        model.Undo();
        Assert.Equal(before + 1, model.Snapshot.UndoSerial);
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.UseApps, progress.Step);
        model.UseApps();
        Assert.Equal(TutorialChange.Step, Look());
        model.ResumeDrawing();
        Assert.Equal(TutorialChange.Step, Look());
        Assert.Equal(TutorialStep.Hide, progress.Step);
        model.ToggleTools();
        Assert.Equal(TutorialChange.Goal, Look());
        model.ToggleTools();
        Assert.Equal(TutorialChange.Finished, Look());
        Assert.Equal(TutorialSteps.All, progress.Completed);
    }

    [Fact]
    public void UndoCountsOnlyWhatItTakesBack()
    {
        WorkspaceModel model = Drawing();
        model.Undo();
        Assert.Equal(0UL, model.Snapshot.UndoSerial);
        AssertAccepted(model.Add(Primary, Box()));
        model.Undo();
        Assert.Equal(1UL, model.Snapshot.UndoSerial);
        model.Redo();
        Assert.Equal(1UL, model.Snapshot.UndoSerial);
    }

    [Fact]
    public void TheWelcomeCardShowsUntilFinishedOrTurnedOff()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        Assert.True(TutorialRules.IsEligible(model.Snapshot));
        model.BeginDrawing();
        Assert.False(TutorialRules.IsEligible(model.Snapshot));
        model.UseApps();
        model.ToggleMagnifier();
        Assert.False(TutorialRules.IsEligible(model.Snapshot));
        model.StopZoom();
        model.Report("Test error");
        Assert.False(TutorialRules.IsEligible(model.Snapshot));
        model.DismissError();
        Assert.True(TutorialRules.IsEligible(model.Snapshot));

        Assert.True(TutorialRules.ShowsAutomatically(startupLaunch: false, suppressed: false, completed: false, eligible: true));
        Assert.False(TutorialRules.ShowsAutomatically(startupLaunch: true, suppressed: false, completed: false, eligible: true));
        Assert.False(TutorialRules.ShowsAutomatically(startupLaunch: false, suppressed: true, completed: false, eligible: true));
        Assert.False(TutorialRules.ShowsAutomatically(startupLaunch: false, suppressed: false, completed: true, eligible: true));
        Assert.False(TutorialRules.ShowsAutomatically(startupLaunch: false, suppressed: false, completed: false, eligible: false));

        AppDefaults defaults = AppDefaults.InMemory();
        var preferences = new TutorialPreferences(defaults);
        int changes = 0;
        preferences.Changed += () => changes++;
        preferences.SetSuppressed(true);
        preferences.SetCompleted(true);
        Assert.True(defaults.Bool("introduction-suppressed"));
        Assert.True(defaults.Bool("tutorial-completed"));
        Assert.True(new TutorialPreferences(defaults).Completed);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void TheWordsUseWindowsKeysAndThePersonsShortcuts()
    {
        WorkspaceModel model = Create();
        WorkspaceSnapshot snapshot = model.Snapshot;
        Assert.Equal("Press Ctrl+Shift+2, or click any tool in the toolbar.", TutorialText.Step(TutorialStep.Draw, snapshot).Detail);
        Assert.Equal("Press 2, or click Rectangle in the toolbar.", TutorialText.Step(TutorialStep.Rectangle, snapshot).Detail);
        Assert.Equal(["Pick the Arrow: press 5 or click it.", "Choose Curved in the properties.", "Click a few points, then press Enter."],
            TutorialText.Step(TutorialStep.Arrow, snapshot).Goals);
        Assert.Equal("Press Esc, or click Use apps in the toolbar. Your drawings stay on the screen.", TutorialText.Step(TutorialStep.UseApps, snapshot).Detail);
        Assert.Equal("Press Ctrl+Z, or click Undo in the toolbar.", TutorialText.Step(TutorialStep.Undo, snapshot).Detail);
        Assert.Equal(["Ctrl", "Shift", "2"], TutorialText.Keys(TutorialGoal.Drawing, snapshot));
        Assert.Equal(["Ctrl", "2"], TutorialText.Keys(TutorialGoal.Hidden, snapshot));
        Assert.Equal(["7"], TutorialText.Keys(TutorialGoal.PenTool, snapshot));
        Assert.Empty(TutorialText.Keys(TutorialGoal.ShapeDrawn, snapshot));
        Assert.Equal("Step 5 of 11", TutorialText.StepValue(TutorialStep.Arrow));
        Assert.Equal(["Draw", "Use apps", "Show / hide", "Undo"], TutorialText.FinishRows(snapshot).Select(row => row.Label));

        foreach (TutorialStep step in TutorialSteps.All)
        {
            TutorialStepText text = TutorialText.Step(step, snapshot);
            Assert.True(text.Goals.IsEmpty || text.Goals.Length == step.Goals.Length, $"{step} has a line per goal.");
            string all = string.Join(' ', [text.Title, text.Detail, text.Hint, .. text.Goals]);
            Assert.DoesNotContain("Command", all, StringComparison.Ordinal);
            Assert.DoesNotContain("Return", all, StringComparison.Ordinal);
            Assert.DoesNotContain("menu bar", all, StringComparison.Ordinal);
        }

        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ResumeDrawing, "Ctrl+Shift+2", GlobalShortcutStatus.AlreadyUsed, "In use."));
        snapshot = model.Snapshot;
        Assert.Equal("Click any tool in the toolbar.", TutorialText.Step(TutorialStep.Draw, snapshot).Detail);
        Assert.Empty(TutorialText.Keys(TutorialGoal.Drawing, snapshot));
        Assert.Equal(["Use apps", "Show / hide", "Undo"], TutorialText.FinishRows(snapshot).Select(row => row.Label));
    }

    [Fact]
    public void DemosShowTheKeyThenTheClickAndDrawTheCurvedArrowWithClicks()
    {
        WorkspaceSnapshot snapshot = Create().Snapshot;
        ImmutableArray<TutorialDemoAction> pick = TutorialDemos.For(TutorialGoal.RectangleTool, snapshot, null);
        Assert.Equal(["2"], Assert.IsType<TutorialDemoKeys>(pick[0]).Keys);
        Assert.Equal(TutorialSpot.Control("Rectangle"), Assert.IsType<TutorialDemoClick>(pick[1]).Target);

        ImmutableArray<TutorialDemoAction> curve = TutorialDemos.For(TutorialGoal.CurvedArrowDrawn, snapshot, null);
        Assert.Equal(3, curve.Count(action => action is TutorialDemoClick { Ink: TutorialInk.Curve }));
        Assert.Equal(["Enter"], Assert.IsType<TutorialDemoKeys>(curve[^1]).Keys);

        TutorialDemoDrag move = Assert.IsType<TutorialDemoDrag>(TutorialDemos.For(TutorialGoal.Moved, snapshot, new Point(300, 200)).Single());
        Assert.Equal(TutorialSpot.OnDisplay(new Point(420, 240)), move.To);
        Assert.IsType<TutorialDemoLoop>(TutorialDemos.For(TutorialGoal.PenDrawn, snapshot, null).Single());
        Assert.IsType<TutorialDemoKeys>(TutorialDemos.For(TutorialGoal.Shown, snapshot, null).Single());
        Assert.Equal("Hide drawings and tools", TutorialDemos.Target(TutorialGoal.Hidden));
        Assert.Null(TutorialDemos.Target(TutorialGoal.ShapeDrawn));
    }

    [Fact]
    public void TheMoveDemoPressesTheNewestDrawingsLine()
    {
        WorkspaceModel model = Drawing();
        model.ShowToolbar();
        Assert.Null(TutorialObserver.NewestGrip(model.Snapshot, Primary));
        AssertAccepted(model.Add(Primary, Box(100), Box(300)));
        // A shape is pressed in the middle of its top edge, where its line is.
        Assert.Equal(new Point(360, 100), TutorialObserver.NewestGrip(model.Snapshot, Primary));
        Assert.Null(TutorialObserver.NewestGrip(model.Snapshot, Left));
        AssertAccepted(model.Add(Left, new Element(Tool.Pen, points: [new Point(0, 0), new Point(10, 20), new Point(30, 30)])));
        Assert.Equal(new Point(10, 20), TutorialObserver.NewestGrip(model.Snapshot, Left));
        TutorialObservation observation = TutorialObserver.Observe(model.Snapshot);
        Assert.Equal(3, observation.Elements.Length);
        Assert.Equal(TutorialMode.Draw, observation.Mode);
        Assert.True(observation.Presented);
    }
}
