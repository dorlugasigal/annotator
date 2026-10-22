using Annotator.Core;
using Annotator.Workspace.Library;
using Annotator.Workspace.Sharing;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Sharing;

/// <summary>
/// Region sharing without windows (Mac RegionSharingTests, RegionSharingController): presets, custom regions and their
/// checks, the display choice, Start and Stop, the safety stops and the readiness wait. The host is a stand-in that records
/// what the chooser, the outline and the selection panels would show.
/// </summary>
public sealed class RegionSharingTests
{
    private static (WorkspaceModel Model, RegionSharing Sharing, FakeHost Host) Workspace(bool drawing = true,
        Func<IReadOnlyList<DisplayDescriptor>>? displays = null)
    {
        WorkspaceModel model = Create(displays, pointer: () => new Point(100, 100));
        if (drawing)
        {
            model.BeginDrawing();
        }
        var host = new FakeHost();
        return (model, new RegionSharing(model, host), host);
    }

    [Fact]
    public void PresetsAndOutlineDoNotStartCaptureOrChangeDrawings()
    {
        // Mac testPresetControlsAndOutlineDoNotStartCaptureOrChangeDrawings.
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        var shape = new Element(Tool.Rectangle, new Rect(80, 80, 120, 100));
        AssertAccepted(model.Add(Primary, shape));
        model.Choose(Tool.Select);
        CanvasDocument before = model.Document(Primary);

        sharing.ShowControls();
        Assert.Equal(Primary, sharing.SelectedDisplayId);
        Assert.True(host.ControlsVisible);
        foreach (ShareRegionPreset preset in ShareRegionPreset.AllCases)
        {
            sharing.ChoosePreset(preset);
            Assert.Equal(new SharingRegion(preset, PrimaryDisplay.Bounds), sharing.Region);
            Assert.Equal(preset, sharing.SelectedPreset);
            Assert.Equal((sharing.Region!.Value, Primary, true), host.Outline);
        }
        Assert.False(sharing.HasOutput);
        Assert.Null(model.Snapshot.RegionSharing.Output);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Equal(Tool.Select, model.Snapshot.Tool);
        Assert.Equal(before.Elements, model.Document(Primary).Elements);
        Assert.Equal(before.Selection, model.Document(Primary).Selection);
        Assert.Equal(before.CanUndo, model.Document(Primary).CanUndo);

        sharing.SelectDisplay(Left);
        Assert.Equal(new SharingRegion(ShareRegionPreset.BottomRight, LeftDisplay.Bounds), sharing.Region);
        Assert.Equal(Left, host.Outline?.Display);
        host.ControlsVisible = false;
        sharing.ControlsClosed();
        Assert.Null(host.Outline);
    }

    [Fact]
    public void CustomFieldsRejectInvalidRequestsAndKeepTheLastRegion()
    {
        // Mac testCustomFieldsRejectInvalidRequestsAndKeepTheLastRegion: display 101 is 640 by 480 points here.
        (_, RegionSharing sharing, _) = Workspace();
        sharing.ShowControls();
        SharingRegion? original = sharing.Region;
        foreach (Rect invalid in new[]
        {
            new Rect(-1, 0, 100, 100),
            new Rect(600, 0, 200, 100),
            new Rect(0, 0, 31, 100),
            new Rect(double.NaN, 0, 100, 100),
        })
        {
            sharing.ApplyCustom(invalid);
            Assert.Equal(original, sharing.Region);
            Assert.Equal(RegionSharingText.RegionInvalid, sharing.ValidationMessage);
        }
        sharing.ApplyCustom(new Rect(20, 40, 320, 200));
        Assert.Equal(new Rect(20, 40, 320, 200), sharing.Region?.Bounds);
        Assert.Null(sharing.SelectedPreset);
        Assert.Null(sharing.ValidationMessage);
        sharing.SelectDisplay(Left);
        Assert.Null(sharing.Region);
        Assert.Equal(RegionSharingText.CustomNeeded, sharing.ValidationMessage);
        Assert.False(sharing.HasOutput);

        sharing.InvalidCustomNumbers();
        Assert.Equal(RegionSharingText.NumbersNeeded, sharing.ValidationMessage);
    }

    [Fact]
    public void StartingDuringWorkspaceMotionDoesNotStart()
    {
        // Mac testStartingDuringWorkspaceMotionDoesNotRequestCapture.
        WorkspaceModel model = new(new WorkspaceOptions
        {
            Displays = () => [PrimaryDisplay, LeftDisplay],
            Pointer = () => new Point(100, 100),
            AnimatesVisibility = true,
        });
        model.BeginDrawing();
        model.ShowToolbar();
        var host = new FakeHost();
        var sharing = new RegionSharing(model, host);
        sharing.ShowControls();
        model.HideTools();
        Assert.NotNull(model.Snapshot.Visibility);

        sharing.Start();
        Assert.False(sharing.State.IsCapturing);
        Assert.False(sharing.HasOutput);
        Assert.Contains("workspace animation", sharing.State.Message, StringComparison.Ordinal);
        Assert.Null(model.Snapshot.RegionSharing.Output);
    }

    [Fact]
    public void ChooserOutlinesWithHiddenToolsAndKeepsItsFillUntilSharingStarts()
    {
        // Mac testChooserPreviewsWithHiddenToolbarAndFitsWithoutOptionalToggles and testLiveOutlineClearsSelectionTint.
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        model.UseApps();
        model.HideTools();
        Assert.True(model.OpenRegionSharing());
        sharing.ChoosePreset(ShareRegionPreset.CenterThird);
        Assert.Equal((new SharingRegion(ShareRegionPreset.CenterThird, PrimaryDisplay.Bounds), Primary, true), host.Outline);
        Assert.True(model.Snapshot.WorkspaceHidden);

        sharing.Start();
        Assert.True(sharing.State.IsCapturing);
        Assert.False(host.Outline?.Fill);
        Assert.True(model.Snapshot.WorkspaceHidden);
        host.ControlsVisible = false;
        sharing.ControlsClosed();
        Assert.NotNull(host.Outline);
        sharing.Stop();
        Assert.Null(host.Outline);
    }

    [Fact]
    public void CustomSelectionUsesTheDragDisplayAndKeepsDrawings()
    {
        // Mac testCustomSelectionUsesDragDisplayAndReleasesEveryPanel.
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        sharing.ShowControls();
        SharingRegion? previous = sharing.Region;
        CanvasDocument[] documents = [model.Document(Primary), model.Document(Left)];
        foreach (DisplayId target in new[] { Left, Primary })
        {
            DisplayId? chosen = sharing.SelectedDisplayId;
            sharing.DrawCustomRegion();
            Assert.True(host.IsSelecting);
            Assert.False(host.ControlsVisible);
            Assert.Null(host.Outline);
            Assert.Equal([Primary, Left], host.Selection!.Value.Displays.Select(display => display.Id));
            Assert.Equal(chosen, host.Selection.Value.Preferred);
            host.Complete(new RegionSelectionResult.Selected(target, new Rect(80, 60, 270, 220)));
            Assert.Equal(target, sharing.SelectedDisplayId);
            Assert.Equal(new Rect(80, 60, 270, 220), sharing.Region?.Bounds);
            Assert.Equal(target, host.Outline?.Display);
            Assert.True(host.ControlsVisible);
            Assert.True(host.Activated);
            Assert.False(host.IsSelecting);
        }
        Assert.NotEqual(previous, sharing.Region);
        Assert.Null(sharing.SelectedPreset);
        Assert.False(sharing.HasOutput);
        Assert.Equal(documents[0].Elements, model.Document(Primary).Elements);
        Assert.Equal(documents[1].Elements, model.Document(Left).Elements);
    }

    [Fact]
    public void CancelledOrInvalidSelectionKeepsThePreviousDisplayAndRegion()
    {
        // Mac testCustomSelectionCancelAndInvalidDragKeepPreviousDisplayAndRegion and the focus loss.
        (_, RegionSharing sharing, FakeHost host) = Workspace();
        sharing.ShowControls();
        SharingRegion? previous = sharing.Region;
        foreach (RegionSelectionResult result in new RegionSelectionResult[]
        {
            RegionSelectionResult.Cancelled.Instance,
            new RegionSelectionResult.Selected(Left, new Rect(80, 60, 10, 10)),
            RegionSelectionResult.FocusLost.Instance,
        })
        {
            sharing.DrawCustomRegion();
            host.Complete(result);
            Assert.Equal(Primary, sharing.SelectedDisplayId);
            Assert.Equal(previous, sharing.Region);
            Assert.False(host.IsSelecting);
            Assert.Equal(result is not RegionSelectionResult.FocusLost, host.Activated);
            if (result is RegionSelectionResult.Selected)
            {
                Assert.Equal(RegionSharingText.RegionInvalid, sharing.ValidationMessage);
            }
        }
    }

    [Fact]
    public void SystemTransitionCancelsSelectionWithoutChangingHistory()
    {
        // Mac testSystemTransitionCancelsRegionSelectionWithoutChangingHistory.
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        var item = new Element(Tool.Text, new Rect(60, 60, 100, 40), text: "Keep me");
        AssertAccepted(model.Add(Primary, item));
        sharing.ShowControls();
        SharingRegion? previous = sharing.Region;
        sharing.DrawCustomRegion();
        Assert.True(host.IsSelecting);
        model.SuspendPresentation();
        Assert.False(host.IsSelecting);
        Assert.Equal(previous, sharing.Region);
        Assert.Equal([item], model.Elements(Primary));
        Assert.True(model.Document(Primary).CanUndo);
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
    }

    [Fact]
    public void MissingDisplayNeverFallsBackToAnotherDisplay()
    {
        // Mac testMissingDisplayNeverFallsBackToAnotherDisplay.
        List<DisplayDescriptor> displays = [PrimaryDisplay, LeftDisplay];
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace(displays: () => displays);
        sharing.ShowControls();
        sharing.SelectDisplay(Primary);
        displays.RemoveAt(0);
        model.DisplaysChanged();
        Assert.Equal(Primary, sharing.SelectedDisplayId);
        Assert.Null(sharing.Region);
        Assert.Null(host.Outline);
        Assert.Equal(RegionSharingText.DisplayUnavailable, sharing.ValidationMessage);
        Assert.False(sharing.HasOutput);
    }

    [Fact]
    public void StartOpensTheOutputBehindAppsAndPresentedFramesMakeItLive()
    {
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        model.ShowToolbar();
        sharing.ShowControls();
        sharing.Start();

        RegionSharingSnapshot shared = model.Snapshot.RegionSharing;
        RegionSharingOutput output = Assert.IsType<RegionSharingOutput>(shared.Output);
        Assert.Equal(RegionSharingState.Starting.Instance, shared.State);
        Assert.Equal("Starting region sharing", shared.State.Title);
        Assert.True(output.Capturing);
        Assert.Equal(RegionSharingText.StartingCover, output.Cover);
        Assert.Equal(PrimaryDisplay, output.Display);
        Assert.Equal(new SharingRegion(ShareRegionPreset.LeftHalf, PrimaryDisplay.Bounds), output.Region);
        // Mac start: model.interact() gives the apps their input back.
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.False(host.Outline?.Fill);
        Assert.Single(host.Timers);
        Assert.True(model.Snapshot.IsOn(ShortcutAction.ShareRegion));

        sharing.OutputPresented(output.Generation - 1);
        Assert.Equal(RegionSharingState.Starting.Instance, sharing.State);
        sharing.OutputPresented(output.Generation);
        Assert.Equal(RegionSharingState.Live.Instance, sharing.State);
        Assert.Equal("Region sharing is on", model.Snapshot.RegionSharing.State.Title);
        Assert.True(host.Timers[0].Disposed);

        // Every later frame of a live output is reported too; it changes nothing and publishes nothing.
        WorkspaceSnapshot live = model.Snapshot;
        sharing.OutputPresented(output.Generation);
        Assert.Same(live, model.Snapshot);

        // A scene change covers the output again until its new frame is on screen.
        sharing.OutputWaiting(output.Generation);
        Assert.Equal(RegionSharingState.Starting.Instance, sharing.State);
        sharing.OutputPresented(output.Generation);
        Assert.Equal(RegionSharingState.Live.Instance, sharing.State);

        sharing.Toggle();
        RegionSharingOutput stopped = model.Snapshot.RegionSharing.Output!;
        Assert.Equal(RegionSharingState.Idle.Instance, sharing.State);
        Assert.False(stopped.Capturing);
        Assert.Equal(RegionSharingText.OffCover, stopped.Cover);
        Assert.True(stopped.Generation > output.Generation);
        Assert.Equal(output.Identity, stopped.Identity);
        Assert.False(model.Snapshot.IsOn(ShortcutAction.ShareRegion));

        // A late frame of an earlier generation cannot uncover a stopped output.
        sharing.OutputPresented(output.Generation);
        Assert.Equal(RegionSharingState.Idle.Instance, sharing.State);

        sharing.Toggle();
        Assert.Equal(output.Identity, model.Snapshot.RegionSharing.Output!.Identity);
        sharing.OutputClosed();
        Assert.Null(model.Snapshot.RegionSharing.Output);
        Assert.False(sharing.HasOutput);
        sharing.Start();
        Assert.NotEqual(output.Identity, model.Snapshot.RegionSharing.Output!.Identity);
    }

    [Fact]
    public void ChangingTheRegionOrDisplayWhileLiveStopsAndKeepsTheOutput()
    {
        (_, RegionSharing sharing, FakeHost host) = Workspace();
        sharing.ShowControls();
        sharing.Start();
        sharing.OutputPresented(sharing.Generation);
        ulong live = sharing.Generation;

        sharing.ChoosePreset(ShareRegionPreset.LeftHalf);
        Assert.Equal(RegionSharingState.Live.Instance, sharing.State);
        sharing.ChoosePreset(ShareRegionPreset.TopRight);
        Assert.Equal(new RegionSharingState.Stopped(RegionSharingText.RegionChanged), sharing.State);
        Assert.True(sharing.Generation > live);
        Assert.True(sharing.HasOutput);
        Assert.True(host.Outline?.Fill);

        sharing.Start();
        sharing.SelectDisplay(Left);
        Assert.Equal(new RegionSharingState.Stopped(RegionSharingText.RegionChanged), sharing.State);
        Assert.Equal(new SharingRegion(ShareRegionPreset.TopRight, LeftDisplay.Bounds), sharing.Region);
    }

    [Fact]
    public void QuickHalvesShareTheSelectedDisplayWithoutTheChooser()
    {
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace(drawing: false);
        sharing.Share(ShareRegionPreset.RightHalf);
        Assert.False(host.ControlsVisible);
        Assert.True(sharing.State.IsCapturing);
        Assert.Equal(ShareRegionPreset.RightHalf, model.Snapshot.RegionSharing.SelectedPreset);
        Assert.Equal(new SharingRegion(ShareRegionPreset.RightHalf, PrimaryDisplay.Bounds), model.Snapshot.RegionSharing.Output!.Region);
        // Mac prepareRegionSharingSession: Off becomes Use apps, with the shared display's canvas.
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.NotNull(model.Snapshot.Display(Primary));
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        Assert.False(host.Outline?.Fill);

        // Mac share(preset:): another half stops the running share for the region change and starts it again.
        RegionSharingOutput right = model.Snapshot.RegionSharing.Output!;
        sharing.Share(ShareRegionPreset.LeftHalf);
        Assert.True(sharing.State.IsCapturing);
        RegionSharingOutput left = model.Snapshot.RegionSharing.Output!;
        Assert.Equal(right.Identity, left.Identity);
        Assert.Equal(right.Generation + 2, left.Generation);
        Assert.Equal(new SharingRegion(ShareRegionPreset.LeftHalf, PrimaryDisplay.Bounds), left.Region);
    }

    [Fact]
    public void ReadinessWaitFailsOpaquelyAndReportsTheCaptureFailure()
    {
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        model.ShowToolbar();
        sharing.Share(ShareRegionPreset.LeftHalf);
        FakeTimer timer = Assert.Single(host.Timers);
        Assert.Equal(RegionSharing.ReadinessTimeout, timer.Delay);
        timer.Fire();
        Assert.Equal(new RegionSharingState.Failed(RegionSharingText.NoFrame), sharing.State);
        RegionSharingOutput output = model.Snapshot.RegionSharing.Output!;
        Assert.False(output.Capturing);
        Assert.Equal(RegionSharingText.NoFrame, output.Cover);
        Assert.Equal(RegionSharingText.NoFrame, model.Snapshot.ErrorMessage);
        Assert.Equal(WorkspaceModel.CaptureUnavailableWarning, model.Snapshot.CaptureWarning);

        // A wait that a newer start replaced does nothing.
        sharing.Start();
        FakeTimer first = host.Timers[^1];
        sharing.OutputWaiting(sharing.Generation);
        FakeTimer second = host.Timers[^1];
        first.Fire();
        Assert.Equal(RegionSharingState.Starting.Instance, sharing.State);
        second.Fire();
        Assert.Equal(new RegionSharingState.Failed(RegionSharingText.NoFrame), sharing.State);
    }

    [Fact]
    public void SystemAndDisplayChangesStopSharingWithTheMacMessages()
    {
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        sharing.Share(ShareRegionPreset.LeftHalf);
        model.SuspendPresentation();
        Assert.Equal(new RegionSharingState.Stopped(RegionSharingText.SystemPaused), sharing.State);
        Assert.Equal(RegionSharingText.SystemPaused, model.Snapshot.RegionSharing.Output!.Cover);
        Assert.False(model.Snapshot.RegionSharing.Output!.Capturing);

        sharing.Start();
        model.DisplaysChanged();
        Assert.Equal(new RegionSharingState.Stopped(RegionSharingText.DisplaysChanged), sharing.State);
        Assert.NotNull(sharing.Region);

        sharing.Start();
        sharing.OutputStopped(sharing.Generation, RegionSharingText.CaptureStopped);
        Assert.Equal(new RegionSharingState.Stopped(RegionSharingText.CaptureStopped), sharing.State);

        host.DesktopIsLocked = true;
        sharing.Start();
        Assert.Equal(new RegionSharingState.Failed(RegionSharingText.DesktopLocked), sharing.State);
        Assert.Equal(WorkspaceModel.CaptureUnavailableWarning, model.Snapshot.CaptureWarning);
        host.DesktopIsLocked = false;

        sharing.Start();
        sharing.OutputFailed(sharing.Generation - 1, "stale", captureFailure: false);
        Assert.True(sharing.State.IsCapturing);
        sharing.OutputFailed(sharing.Generation, "Screen capture stopped. Select an effect tool to try again.", captureFailure: true);
        Assert.Equal(new RegionSharingState.Failed("Screen capture stopped. Select an effect tool to try again."), sharing.State);
    }

    [Fact]
    public void StopForSafetyBeforeAnyOutputChangesNothing()
    {
        (WorkspaceModel model, RegionSharing sharing, _) = Workspace();
        sharing.ShowControls();
        ulong generation = sharing.Generation;
        model.SuspendPresentation();
        Assert.Equal(RegionSharingState.Idle.Instance, sharing.State);
        Assert.Equal(generation, sharing.Generation);
    }

    [Fact]
    public void ToolbarButtonKeyAndToolsTileOpenTheChooserAndShowSharing()
    {
        (WorkspaceModel model, RegionSharing sharing, FakeHost host) = Workspace();
        Assert.True(WorkspaceModel.IsAvailable(ShortcutAction.ShareRegion));
        Assert.True(model.PerformShortcutAction(ShortcutAction.ShareRegion));
        Assert.True(host.ControlsVisible);
        ToolLibraryTile tile = ToolLibraryTiles.For(ToolLibrarySection.Focus, model.Snapshot)
            .Single(candidate => candidate.Action == ToolLibraryAction.ShareRegion);
        Assert.True(tile.Available);
        Assert.False(tile.Selected);
        sharing.Start();
        tile = ToolLibraryTiles.For(ToolLibrarySection.Focus, model.Snapshot).Single(candidate => candidate.Action == ToolLibraryAction.ShareRegion);
        Assert.True(tile.Selected);
        Assert.Equal(CanvasCommandResult.Done,
            model.Perform(new CanvasCommand(CanvasCommandKind.ShortcutAction, Primary) { Action = ShortcutAction.ShareRegion }));

        WorkspaceModel plain = Create();
        Assert.False(plain.PerformShortcutAction(ShortcutAction.ShareRegion));
    }

    [Fact]
    public void QuitClosesTheOutputChooserAndGuides()
    {
        (_, RegionSharing sharing, FakeHost host) = Workspace();
        sharing.ShowControls();
        sharing.Start();
        sharing.Close();
        Assert.False(sharing.HasOutput);
        Assert.False(host.ControlsVisible);
        Assert.Null(host.Outline);
        Assert.Equal(RegionSharingState.Idle.Instance, sharing.State);
    }

    [Fact]
    public void DisplayNamesComeFromTheHostElseTheirPosition()
    {
        (_, RegionSharing sharing, FakeHost host) = Workspace();
        host.Names[Primary] = "Odyssey G95SD";
        sharing.ShowControls();
        Assert.Equal(["Odyssey G95SD", "Display 2"], sharing.Displays.Select(display => display.Name));
    }

    [Fact]
    public void FieldsUseTheMacNumberFormat()
    {
        Assert.Equal("2560", RegionSharingText.Field(2560));
        Assert.Equal("853.333333", RegionSharingText.Field(2560.0 / 3));
        Assert.Equal("0.5", RegionSharingText.Field(0.5));
        Assert.Equal("2,560 \u00d7 1,440 pixels", RegionSharingText.Pixels(new Rect(0, 0, 2048, 1152), 1.25));
        Assert.Equal(12.5, RegionSharingText.Number("12.5"));
        Assert.Equal(-3, RegionSharingText.Number("-3"));
        Assert.Null(RegionSharingText.Number(""));
        Assert.Null(RegionSharingText.Number(" 12"));
        Assert.Null(RegionSharingText.Number("1,200"));
        Assert.Null(RegionSharingText.Number("x"));
    }

    [Fact]
    public void StateWordsMatchTheMac()
    {
        Assert.Equal("Region sharing is off", RegionSharingState.Idle.Instance.Title);
        Assert.Equal("Starting region sharing", RegionSharingState.Starting.Instance.Title);
        Assert.Equal("Region sharing is on", RegionSharingState.Live.Instance.Title);
        Assert.Equal("Region sharing is off", new RegionSharingState.Stopped("x").Title);
        Assert.Equal("Region sharing is off", new RegionSharingState.Failed("y").Title);
        Assert.Equal("x", new RegionSharingState.Stopped("x").Message);
        Assert.Null(RegionSharingState.Live.Instance.Message);
        Assert.Equal("Annotator - Shared Region", RegionSharingText.OutputTitle(development: false));
        Assert.Equal("Annotator Dev - Shared Region", RegionSharingText.OutputTitle(development: true));
    }

    private sealed class FakeHost : IRegionSharingHost
    {
        private Action<RegionSelectionResult>? _completed;

        public Dictionary<DisplayId, string> Names { get; } = [];

        public bool DesktopIsLocked { get; set; }

        public bool ControlsVisible { get; set; }

        public bool Activated { get; private set; }

        public (SharingRegion Region, DisplayId Display, bool Fill)? Outline { get; private set; }

        public (IReadOnlyList<RegionSharingDisplay> Displays, DisplayId Preferred)? Selection { get; private set; }

        public List<FakeTimer> Timers { get; } = [];

        public bool IsSelecting => _completed is not null;

        public string? DisplayName(DisplayId display) => Names.GetValueOrDefault(display);

        public void ShowControls() => ControlsVisible = true;

        public void HideControls() => ControlsVisible = false;

        public void RestoreControls(bool activate)
        {
            ControlsVisible = true;
            Activated = activate;
        }

        public void CloseControls() => ControlsVisible = false;

        public void ShowOutline(SharingRegion region, DisplayDescriptor display, bool selectionFill) => Outline = (region, display.Id, selectionFill);

        public void HideOutline() => Outline = null;

        public void ChooseRegion(IReadOnlyList<RegionSharingDisplay> displays, DisplayId preferred, Action<RegionSelectionResult> completed)
        {
            CancelSelection();
            Selection = (displays, preferred);
            _completed = completed;
        }

        public void CancelSelection() => _completed = null;

        public void Complete(RegionSelectionResult result)
        {
            Action<RegionSelectionResult> completed = _completed ?? throw new InvalidOperationException("No selection runs.");
            _completed = null;
            completed(result);
        }

        public IDisposable Schedule(TimeSpan delay, Action action)
        {
            var timer = new FakeTimer(delay, action);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class FakeTimer(TimeSpan delay, Action action) : IDisposable
    {
        public TimeSpan Delay { get; } = delay;

        public bool Disposed { get; private set; }

        public void Fire()
        {
            if (!Disposed)
            {
                Disposed = true;
                action();
            }
        }

        public void Dispose() => Disposed = true;
    }
}
