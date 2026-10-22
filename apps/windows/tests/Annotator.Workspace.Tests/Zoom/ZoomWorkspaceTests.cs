using Annotator.Core;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.Toolbar;
using Annotator.Workspace.Zoom;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Zoom;

/// <summary>A timer the test advances by hand (the app's is the dispatcher's).</summary>
internal sealed class ManualTimer
{
    private readonly List<(TimeSpan Due, Action Callback, Handle Handle)> _pending = [];

    public TimeSpan Now { get; private set; }

    public int Pending => _pending.Count(item => !item.Handle.Cancelled);

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var handle = new Handle();
        _pending.Add((Now + delay, callback, handle));
        return handle;
    }

    public void Advance(TimeSpan by)
    {
        TimeSpan end = Now + by;
        while (true)
        {
            int next = -1;
            for (int index = 0; index < _pending.Count; index++)
            {
                if (!_pending[index].Handle.Cancelled && _pending[index].Due <= end && (next < 0 || _pending[index].Due < _pending[next].Due))
                {
                    next = index;
                }
            }
            if (next < 0)
            {
                break;
            }
            (TimeSpan due, Action callback, Handle handle) = _pending[next];
            _pending.RemoveAt(next);
            Now = due;
            handle.Cancelled = true;
            callback();
        }
        Now = end;
        _pending.RemoveAll(item => item.Handle.Cancelled);
    }

    private sealed class Handle : IDisposable
    {
        public bool Cancelled { get; set; }

        public void Dispose() => Cancelled = true;
    }
}

/// <summary>Ports of the Mac ZoomStartControllerTests.</summary>
public sealed class ZoomStartControllerTests
{
    [Fact]
    public void ScreenPermissionCanStartZoomWithoutGestureAccess()
    {
        int requests = 0, starts = 0;
        var controller = new ZoomStartController(() => true, () => false, () => requests++, () => requests++,
            () => starts++, _ => { }, timer: null, requiresGesturesForStart: false);
        controller.Request();
        Assert.Equal(1, starts);
        Assert.Equal(0, requests);
        controller.Presented();
        Assert.Equal(ZoomStartState.Active, controller.State);
    }

    [Fact]
    public void GrantingBothPermissionsResumesTheOriginalRequestOnce()
    {
        bool screen = false, gestures = false;
        int screenRequests = 0, gestureRequests = 0, starts = 0;
        var states = new List<ZoomStartState>();
        var controller = new ZoomStartController(() => screen, () => gestures, () => screenRequests++, () => gestureRequests++,
            () => starts++, states.Add, timer: null);
        controller.Request();
        Assert.Equal(ZoomStartPhase.ScreenPermission, controller.State.Phase);
        controller.Refresh();
        controller.Request();
        Assert.Equal(1, screenRequests);
        screen = true;
        controller.Refresh();
        Assert.Equal(ZoomStartPhase.GesturePermission, controller.State.Phase);
        Assert.Equal(1, gestureRequests);
        controller.Refresh();
        Assert.Equal(1, gestureRequests);
        gestures = true;
        controller.Refresh();
        Assert.Equal(ZoomStartPhase.Starting, controller.State.Phase);
        Assert.Equal(1, starts);
        controller.Request();
        controller.Refresh();
        Assert.Equal(1, starts);
        controller.Presented();
        Assert.Equal(ZoomStartState.Active, controller.State);
        Assert.Equal([ZoomStartPhase.ScreenPermission, ZoomStartPhase.GesturePermission, ZoomStartPhase.Starting, ZoomStartPhase.Active],
            states.Select(state => state.Phase));
    }

    [Fact]
    public void ExistingGrantsDoNotRequestAgainAndFailureCanRetry()
    {
        int requests = 0, starts = 0;
        var controller = new ZoomStartController(() => true, () => true, () => requests++, () => requests++, () => starts++, _ => { }, timer: null);
        controller.Request();
        Assert.Equal(0, requests);
        Assert.Equal(1, starts);
        controller.Fail("Capture unavailable");
        Assert.Equal(ZoomStartState.Failed("Capture unavailable"), controller.State);
        controller.Request();
        Assert.Equal(2, starts);
        controller.Cancel();
        controller.Presented();
        Assert.Equal(ZoomStartState.Idle, controller.State);
    }

    [Fact]
    public void CancellationDoesNotStartLaterWhenPermissionChanges()
    {
        bool granted = false;
        int starts = 0;
        var controller = new ZoomStartController(() => granted, () => granted, () => { }, () => { }, () => starts++, _ => { }, timer: null);
        controller.Request();
        controller.Cancel();
        granted = true;
        controller.Refresh();
        Assert.Equal(0, starts);
        Assert.Equal(ZoomStartState.Idle, controller.State);
    }

    [Fact]
    public void WaitingAndStartingAreBounded()
    {
        var timer = new ManualTimer();
        bool granted = false;
        var controller = new ZoomStartController(() => granted, () => true, () => { }, () => { }, () => { }, _ => { }, timer.Schedule);
        controller.Request();
        timer.Advance(TimeSpan.FromMilliseconds(750 * 120));
        Assert.Equal(ZoomStartState.Failed(ZoomStartController.PermissionTimeoutMessage), controller.State);

        granted = true;
        controller.Request();
        Assert.Equal(ZoomStartPhase.Starting, controller.State.Phase);
        timer.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(ZoomStartPhase.Starting, controller.State.Phase);
        timer.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(ZoomStartState.Failed(ZoomStartController.StartupTimeoutMessage), controller.State);

        controller.Request();
        controller.Presented();
        timer.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(ZoomStartState.Active, controller.State);
    }
}

/// <summary>
/// Ports of the zoom parts of the Mac WorkspaceModel tests (LocalZoomScrollTests, LiveZoomStartTests' model assertions,
/// LocalShortcutRecoveryTests, InspectorInteractionRegressionTests' mode stops and MenuBarPopoverTests' zoom Escape).
/// </summary>
public sealed class ZoomWorkspaceTests
{
    private static WorkspaceModel Create(ManualTimer? timer = null, DisplayedZoomBoard? board = null, Func<Point?>? pointer = null,
                                         bool reducesMotion = false)
    {
        DisplayDescriptor[] displays = [PrimaryDisplay, LeftDisplay];
        return new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => displays,
            Pointer = pointer ?? (() => new Point(10, 10)),
            Timer = timer is null ? null : timer.Schedule,
            DisplayedZoom = board,
            ReducesMotion = () => reducesMotion,
        });
    }

    private static PresentationSettings Presentation(WorkspaceModel model) => model.Document(Primary).Presentation;

    [Fact]
    public void LiveZoomShortcutStartsAt150PercentInUseAppsAndTogglesOff()
    {
        // Mac testControlOneStartsZoomFromOffDrawingAndUsingApps and testGrantedZoomRequestPresentsAdjustsStopsAndRestarts.
        foreach (WorkspaceMode start in new[] { WorkspaceMode.Off, WorkspaceMode.Draw, WorkspaceMode.UseApps })
        {
            WorkspaceModel model = Create();
            if (start != WorkspaceMode.Off)
            {
                model.BeginDrawing();
            }
            if (start == WorkspaceMode.UseApps)
            {
                model.UseApps();
            }
            model.ToggleScreenZoomFromShortcut();
            WorkspaceSnapshot snapshot = model.Snapshot;
            Assert.Equal(ZoomStartPhase.Starting, snapshot.ZoomStart.Phase);
            Assert.True(snapshot.ScreenZoomEnabled);
            Assert.Equal(1.5, snapshot.Magnification);
            Assert.Equal(new PresentationSettings(PresentationMode.Screen, 1.5), Presentation(model));
            Assert.Equal(WorkspaceMode.UseApps, snapshot.Mode);
            Assert.NotNull(snapshot.ZoomEntrance);
            Assert.Equal(Primary, snapshot.ZoomEntrance!.Display);

            model.NoteZoomPresented();
            Assert.Equal(ZoomStartState.Active, model.Snapshot.ZoomStart);
            Assert.Equal($"Live zoom is on. {WorkspaceModel.ZoomScrollNotConnected}", model.Snapshot.Status);

            model.ToggleScreenZoomFromShortcut();
            Assert.False(model.Snapshot.ScreenZoomEnabled);
            Assert.Equal(ZoomStartState.Idle, model.Snapshot.ZoomStart);
            Assert.Equal(PresentationMode.Off, Presentation(model).Mode);
        }
    }

    [Fact]
    public void ToolbarZoomStartsAtTheLastZoomAndAnUnzoomedLastZoomStartsAtTwo()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        model.SetMagnification(1);
        model.ToggleScreenZoom();
        Assert.Equal(2, model.Snapshot.Magnification);
        model.StopZoom();
        model.SetMagnification(4);
        model.ToggleScreenZoom();
        Assert.Equal(4, model.Snapshot.Magnification);
        Assert.True(model.Snapshot.ScreenZoomEnabled);
    }

    [Fact]
    public void StoppingZoomIsOneUndoStep()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        model.ToggleScreenZoomFromShortcut();
        model.NoteZoomPresented();
        model.StopZoom();
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        Assert.True(model.Snapshot.CanUndo);
        model.Undo();
        Assert.True(model.Snapshot.ScreenZoomEnabled, "Stopping is one undoable step.");
        Assert.Equal("Clear canvas and stop zoom", model.Snapshot.DeleteActionLabel);
        model.StopZoom();
        Assert.Equal("Delete", model.Snapshot.DeleteActionLabel);
    }

    [Fact]
    public void ContinuousScrollKeepsOneUndoStep()
    {
        // Mac testContinuousZoomPublishesOncePerEventAndKeepsOneUndoStep.
        WorkspaceModel model = Create();
        model.BeginDrawing();
        model.ToggleScreenZoom();
        model.SetMagnification(2);
        model.FinishZoomGesture();
        int published = 0;
        model.Published += _ => published++;
        for (int index = 0; index < 121; index++)
        {
            model.ScrollScreenZoom(0.25);
        }
        Assert.Equal(121, published);
        Assert.Equal(2 * Math.Exp(121 * 0.25 * 0.012), model.Snapshot.Magnification, 6);
        Assert.True(model.Snapshot.CanUndo);
        Assert.False(model.Snapshot.CanRedo);
        model.FinishZoomGesture();
        model.Undo();
        Assert.Equal(2, model.Snapshot.Magnification, 6);
        Assert.Equal(PresentationMode.Screen, Presentation(model).Mode);
    }

    [Fact]
    public void ScrollGestureEndsAfter180MillisecondsOrWithTheNextChange()
    {
        var timer = new ManualTimer();
        WorkspaceModel model = Create(timer);
        model.BeginDrawing();
        model.ToggleScreenZoom();
        model.NoteZoomPresented();
        model.ScrollScreenZoom(12);
        timer.Advance(TimeSpan.FromMilliseconds(100));
        model.ScrollScreenZoom(12);
        timer.Advance(TimeSpan.FromMilliseconds(179));
        Assert.Equal(1, timer.Pending);
        model.ScrollScreenZoom(12);
        Assert.Equal(2 * Math.Exp(36 * 0.012), model.Snapshot.Magnification, 6);
        timer.Advance(TimeSpan.FromMilliseconds(181));
        Assert.Equal(0, timer.Pending);
        model.ScrollScreenZoom(-12);
        model.Undo();
        Assert.Equal(2 * Math.Exp(36 * 0.012), model.Snapshot.Magnification, 6);
        model.Undo();
        Assert.Equal(2, model.Snapshot.Magnification, 6);
    }

    [Fact]
    public void ScrollsAreLimitedAndOnlyChangeScreenZoom()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        double before = model.Snapshot.Magnification;
        model.ScrollScreenZoom(12);
        Assert.Equal(before, model.Snapshot.Magnification);
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        model.ToggleScreenZoom();
        for (int index = 0; index < 40; index++)
        {
            model.ScrollScreenZoom(500);
        }
        Assert.Equal(16, model.Snapshot.Magnification);
        for (int index = 0; index < 80; index++)
        {
            model.ScrollScreenZoom(-500);
        }
        Assert.Equal(1, model.Snapshot.Magnification);
        model.ScrollScreenZoom(double.NaN);
        model.ScrollScreenZoom(0);
        Assert.Equal(1, model.Snapshot.Magnification);
    }

    [Fact]
    public void ZoomScrollSurvivesDrawingModeChanges()
    {
        // Mac testZoomScrollAndPendingZoomSurviveDrawingModeChanges.
        WorkspaceModel model = Create();
        model.BeginDrawing();
        model.ToggleScreenZoom();
        model.NoteZoomPresented();
        foreach (bool drawing in new[] { true, false, true })
        {
            if (drawing)
            {
                model.BeginDrawing();
            }
            else
            {
                model.UseApps();
            }
            double before = model.Snapshot.Magnification;
            model.ScrollScreenZoom(8);
            double after = model.Snapshot.Magnification;
            Assert.True(after > before);
            Assert.True(model.Snapshot.ScreenZoomEnabled);
        }
    }

    [Fact]
    public void AltScrollUpStartsZoomGraduallyFromOneAndScrollDownDoesNot()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.StartScreenZoomFromAltScroll(-8);
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        model.StartScreenZoomFromAltScroll(8);
        Assert.True(model.Snapshot.ScreenZoomEnabled);
        Assert.Equal(Math.Exp(8 * 0.012), model.Snapshot.Magnification, 9);
        // An Alt+scroll start applies its first scroll instead of ramping in.
        Assert.Null(model.Snapshot.ZoomEntrance);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
    }

    [Fact]
    public void ReduceMotionStartsWithoutAnEntrance()
    {
        WorkspaceModel model = Create(reducesMotion: true);
        model.ToggleScreenZoomFromShortcut();
        Assert.True(model.Snapshot.ScreenZoomEnabled);
        Assert.Null(model.Snapshot.ZoomEntrance);
        Assert.False(model.Snapshot.AnimatesScreenZoom);
    }

    [Fact]
    public void ScrollingUpDuringTheZoomOutRampStartsAgainFromWhatTheDisplayShows()
    {
        var board = new DisplayedZoomBoard();
        WorkspaceModel model = Create(board: board);
        model.BeginDrawing();
        model.ToggleScreenZoomFromShortcut();
        model.NoteZoomPresented();
        model.StopZoom();
        board.Set(Primary, new DisplayedZoom(PresentationMode.Screen, 1.3, Exiting: true));
        model.ScrollScreenZoom(10);
        Assert.True(model.Snapshot.ScreenZoomEnabled);
        Assert.Equal(1.3 * Math.Exp(10 * 0.012), model.Snapshot.Magnification, 9);
    }

    [Fact]
    public void EscapeOnTheCanvasStopsZoomAndTheLensBeforeReleasingInput()
    {
        // Mac testLocalEscapeStopsZoomWithoutAGlobalEventTap.
        WorkspaceModel model = Create();
        model.BeginDrawing();
        model.ToggleScreenZoom();
        model.BeginDrawing();
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Escape, Primary)));
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);

        model.BeginDrawing();
        model.ToggleMagnifier();
        Assert.True(model.Snapshot.MagnifierEnabled);
        Assert.Equal(WorkspaceModel.LensStatus, model.Snapshot.Status);
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.Escape, Primary)));
        Assert.False(model.Snapshot.MagnifierEnabled);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
    }

    [Fact]
    public void ZoomEscapeCommitsTextThenCancelsAnEditThenStopsZoom()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        model.ToggleScreenZoom();
        model.BeginDrawing();
        DisplaySnapshot display = model.Display(Primary);
        Assert.IsType<EditResult.Accepted>(model.BeginEdit(Primary, display.Version, EditKind.Text));
        CanvasDocument typed = display.Document;
        typed.Begin();
        typed.Add(new Element(Tool.Text, new Rect(20, 20, 60, 20), text: "Note"));
        Assert.IsType<EditResult.Accepted>(model.UpdateEdit(Primary, display.Version, typed));

        model.HandleZoomEscape();
        Assert.Equal("Note", Assert.Single(model.Elements(Primary)).Text);
        Assert.True(model.Snapshot.ScreenZoomEnabled);

        display = model.Display(Primary);
        Assert.IsType<EditResult.Accepted>(model.BeginEdit(Primary, display.Version, EditKind.Gesture));
        model.HandleZoomEscape();
        Assert.Null(model.Display(Primary).ActiveEdit);
        Assert.True(model.Snapshot.ScreenZoomEnabled);

        model.HandleZoomEscape();
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
    }

    [Fact]
    public void PointerLensKeyAndToolbarButtonToggleTheLens()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        Assert.True(WorkspaceModel.IsAvailable(ShortcutAction.PointerLens));
        Assert.True(model.PerformShortcutAction(ShortcutAction.PointerLens));
        Assert.True(model.Snapshot.MagnifierEnabled);
        Assert.True(model.Snapshot.IsOn(ShortcutAction.PointerLens));
        Assert.Equal(new PresentationSettings(PresentationMode.Lens, 2), Presentation(model));
        // Starting screen zoom replaces the lens.
        model.ToggleScreenZoom();
        Assert.False(model.Snapshot.MagnifierEnabled);
        Assert.True(model.Snapshot.ScreenZoomEnabled);
        model.StopZoom();
        model.SetMagnification(3);
        model.ToggleMagnifier();
        Assert.Equal(new PresentationSettings(PresentationMode.Lens, 3), Presentation(model));
        model.ToggleMagnifier();
        Assert.False(model.Snapshot.MagnifierEnabled);
    }

    [Fact]
    public void ModeStripShowsPendingAndActiveZoomAndStopsEachMode()
    {
        // Mac testActiveModesStopIndependently / testLensAndBlackboardHaveIndependentOneClickStops.
        WorkspaceModel model = Create();
        model.BeginDrawing();
        Assert.False(ActiveToolbarModes.ShowsStrip(model.Snapshot));
        model.ToggleScreenZoom();
        Assert.Equal("Zoom starting", PendingZoomPresentation.For(model.Snapshot.ZoomStart)!.Title);
        Assert.Equal("Starting live screen zoom", PendingZoomPresentation.For(model.Snapshot.ZoomStart)!.Detail);
        Assert.True(ActiveToolbarModes.ShowsStrip(model.Snapshot));
        Assert.Empty(ActiveToolbarModes.Active(model.Snapshot));
        Assert.False(ActiveToolbarModes.ShowsZoomControls(model.Snapshot));
        model.NoteZoomPresented();
        model.SetBoard(Board.Blackboard);
        Assert.Equal([ActiveToolbarMode.ScreenZoom, ActiveToolbarMode.Blackboard], ActiveToolbarModes.Active(model.Snapshot));
        Assert.True(ActiveToolbarModes.ShowsZoomControls(model.Snapshot));
        Assert.Equal("stop-active-screenZoom", "stop-active-" + ActiveToolbarMode.ScreenZoom.RawValue);
        Assert.Equal("Turn off screen zoom", ActiveToolbarMode.ScreenZoom.StopHint);
        model.CancelPendingZoom();
        Assert.Equal("Zoom request cancelled.", model.Snapshot.Status);
        Assert.Equal([ActiveToolbarMode.Blackboard], ActiveToolbarModes.Active(model.Snapshot));
        model.ToggleMagnifier();
        Assert.Equal([ActiveToolbarMode.Magnifier, ActiveToolbarMode.Blackboard], ActiveToolbarModes.Active(model.Snapshot));
        Assert.Equal("Lens", ActiveToolbarMode.Magnifier.ShortTitle);
    }

    [Fact]
    public void FailureStopsZoomWithoutHistoryAndTheToolbarSaysWhy()
    {
        var timer = new ManualTimer();
        WorkspaceModel model = Create(timer);
        model.BeginDrawing();
        model.ToggleScreenZoomFromShortcut();
        ulong cut = model.Display(Primary).PresentationCut;
        timer.Advance(TimeSpan.FromSeconds(10.1));
        WorkspaceSnapshot snapshot = model.Snapshot;
        Assert.Equal(ZoomStartState.Failed(ZoomStartController.StartupTimeoutMessage), snapshot.ZoomStart);
        Assert.False(snapshot.ScreenZoomEnabled);
        Assert.True(model.Display(Primary).PresentationCut > cut);
        Assert.Equal(ZoomStartController.StartupTimeoutMessage, snapshot.CaptureWarning);
        Assert.Equal(ZoomStartController.StartupTimeoutMessage, snapshot.Status);

        model.ToggleMagnifier();
        model.ReportZoomFailure(Primary, PresentationMode.Lens, "Live pixels are unavailable.");
        Assert.False(model.Snapshot.MagnifierEnabled);
        Assert.Equal("Live pixels are unavailable.", model.Snapshot.CaptureFailureMessage);
        Assert.True(model.RetryLiveZoom());
        Assert.True(model.Snapshot.MagnifierEnabled);
        model.NoteZoomPresented();
        Assert.Null(model.Snapshot.CaptureFailureMessage);
        Assert.False(model.RetryLiveZoom());
    }

    [Fact]
    public void SuspensionAndDisplaySwitchesStopZoomAtOnce()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        model.ToggleScreenZoom();
        ulong cut = model.Display(Primary).PresentationCut;
        model.SuspendPresentation();
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        Assert.Equal(ZoomStartState.Idle, model.Snapshot.ZoomStart);
        Assert.True(model.Display(Primary).PresentationCut > cut);
        Assert.Equal(WorkspaceModel.CapturePausedMessage, model.Snapshot.Status);
        Assert.False(model.Snapshot.CanUndo, "Stopping for a suspension leaves no Undo step.");

        // Mac synchronize(): zoom or the lens starting again ends the capture pause (presentation.retry()).
        ulong retry = model.Snapshot.EffectsRetry;
        model.ToggleMagnifier();
        Assert.Equal(retry + 1, model.Snapshot.EffectsRetry);
        model.ToggleMagnifier();
        Assert.Equal(retry + 1, model.Snapshot.EffectsRetry);

        // A virtual desktop switch keeps a pending start (Mac Space change).
        model.ToggleScreenZoom();
        Assert.Equal(retry + 2, model.Snapshot.EffectsRetry);
        model.SuspendPresentation(cancelPendingRequests: false);
        Assert.Equal(ZoomStartPhase.Starting, model.Snapshot.ZoomStart.Phase);

        // Another display becoming the editing target stops zoom here without history.
        WorkspaceModel other = Create();
        other.BeginDrawing();
        other.ToggleScreenZoom();
        other.BeginDrawing();
        cut = other.Display(Primary).PresentationCut;
        AssertAccepted(other.Add(Left, new Element(Tool.Rectangle, new Rect(10, 10, 40, 40))));
        Assert.Equal(PresentationMode.Off, other.Document(Primary).Presentation.Mode);
        Assert.True(other.Display(Primary).PresentationCut > cut);
        Assert.False(other.Snapshot.ScreenZoomEnabled);
    }

    [Fact]
    public void ClearingEveryDisplayStopsZoomAsOneUndoStep()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Rectangle, new Rect(10, 10, 40, 40))));
        model.ToggleScreenZoom();
        model.NoteZoomPresented();
        Assert.True(model.Snapshot.CanClearCanvas);
        model.ClearCanvas();
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        Assert.Empty(model.Elements(Primary));
        model.Undo();
        Assert.True(model.Snapshot.ScreenZoomEnabled);
        Assert.Single(model.Elements(Primary));
    }

    [Fact]
    public void ToolsWindowTilesStartTheLensAndScreenZoom()
    {
        WorkspaceModel model = Create();
        model.BeginDrawing();
        Annotator.Workspace.Library.ToolLibraryTile lens = Annotator.Workspace.Library.ToolLibraryTiles.For(Annotator.Workspace.Library.ToolLibrarySection.Focus, model.Snapshot)
            .Single(tile => tile.Action == Annotator.Workspace.Library.ToolLibraryAction.PointerLens);
        Annotator.Workspace.Library.ToolLibraryTile zoom = Annotator.Workspace.Library.ToolLibraryTiles.For(Annotator.Workspace.Library.ToolLibrarySection.Focus, model.Snapshot)
            .Single(tile => tile.Action == Annotator.Workspace.Library.ToolLibraryAction.ScreenZoom);
        Assert.True(lens.Available);
        Assert.True(zoom.Available);
        Assert.False(lens.Selected);
        Assert.Equal("Ctrl+1", zoom.Shortcut);
        model.ToggleScreenZoom();
        Assert.True(Annotator.Workspace.Library.ToolLibraryTiles.For(Annotator.Workspace.Library.ToolLibrarySection.Focus, model.Snapshot)
            .Single(tile => tile.Action == Annotator.Workspace.Library.ToolLibraryAction.ScreenZoom).Selected);
    }

    [Fact]
    public void ShapesInsertAtTheCenterOfWhatZoomShows()
    {
        var board = new DisplayedZoomBoard();
        // The pointer at desktop (200, 100) on the primary display at scale 2 is (100, 50) in its points.
        WorkspaceModel model = Create(board: board, pointer: () => new Point(200, 100));
        model.BeginDrawing();
        model.ToggleScreenZoom();
        board.Set(Primary, new DisplayedZoom(PresentationMode.Screen, 2, Exiting: false));
        CanvasDocument source = CanvasDocument.Empty();
        var box = new Element(Tool.Rectangle, new Rect(0, 0, 20, 20));
        source.Add(box);
        source.Select([box.Id]);
        var shape = new SavedShape("Box", source);
        model.InsertSavedShape(shape);
        Viewport viewport = Viewport.CursorAnchored(new Rect(0, 0, 640, 480), 2, new Point(100, 50));
        Rect inserted = Assert.Single(model.Elements(Primary)).Frame;
        Assert.Equal(viewport.Source.Center.X, inserted.Center.X, 9);
        Assert.Equal(viewport.Source.Center.Y, inserted.Center.Y, 9);
    }

    [Fact]
    public void HintDataFollowsZoom()
    {
        WorkspaceModel model = Create();
        model.ToggleScreenZoom();
        model.NoteZoomGestures(available: true);
        Assert.Equal(WorkspaceModel.ZoomScrollConnected, model.Snapshot.ZoomScrollStatus);
        Assert.True(model.Snapshot.ZoomGesturesAvailable);
        model.NoteZoomGestures(available: false, WorkspaceModel.ZoomScrollUnavailable);
        Assert.Equal(WorkspaceModel.ZoomScrollUnavailable, model.Snapshot.ZoomScrollStatus);
        Assert.Equal("Ctrl+1", model.Snapshot.ZoomShortcutLabel);
        // Mac WorkspaceHint: while zoom is on, the hint shows the zoom and how to stop it, then the scroll line.
        Assert.Equal(new ZoomHint(true, 2, WorkspaceModel.ZoomScrollUnavailable), model.Snapshot.ZoomHint);
        string hint = WorkspaceHint.Text(model.Snapshot, hasTextEditor: false, hasActiveEdit: false, model.Snapshot.ZoomHint);
        Assert.Contains("Zoom 2.0×  ·  Ctrl+1: stop\n" + WorkspaceModel.ZoomScrollUnavailable, hint);
    }
}
