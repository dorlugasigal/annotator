using Annotator.Core;
using Annotator.Workspace.Capture;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Sharing;
using Annotator.Workspace.Tests.Zoom;
using Annotator.Workspace.Toolbar;
using Annotator.Workspace.Zoom;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Capture;

/// <summary>A host whose answers the test sets; it records each request (the app's shows its explanation and asks Windows).</summary>
internal sealed class FakeScreenAccess : IScreenAccess
{
    private Action<ScreenAccessAnswer>? _answered;

    public ScreenAccess Access { get; set; } = ScreenAccess.NotAsked;

    /// <summary>Whether each request showed the explanation first.</summary>
    public List<bool> Requests { get; } = [];

    public bool IsAsking => _answered is not null;

    public ScreenAccess Check() => Access;

    public void Ask(bool explain, Action<ScreenAccessAnswer> answered)
    {
        Requests.Add(explain);
        _answered = answered;
    }

    /// <summary>The open request ends: Windows answered with <paramref name="access"/>, or the person closed the explanation.</summary>
    public void Answer(ScreenAccess access, ScreenAccessOutcome outcome = ScreenAccessOutcome.Answered)
    {
        Access = access;
        Action<ScreenAccessAnswer> answered = _answered ?? throw new InvalidOperationException("No request is open.");
        _answered = null;
        answered(new ScreenAccessAnswer(access, outcome));
    }
}

/// <summary>
/// Screen access in a packaged build (WIN-CAP-04; the Mac's ensureCapturePermission, requestCapturePermission and the zoom
/// start's permission wait): every pixel feature waits for the explanation and Windows' answer, starts once Windows allows it,
/// and fails closed with where to allow it after a refusal. A development build needs none and never asks.
/// </summary>
public sealed class ScreenAccessWorkspaceTests
{
    private static (WorkspaceModel Model, FakeScreenAccess Access) Packaged(ScreenAccess access = ScreenAccess.NotAsked, ManualTimer? timer = null)
    {
        var host = new FakeScreenAccess { Access = access };
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [PrimaryDisplay, LeftDisplay],
            Pointer = () => new Point(100, 100),
            Timer = timer is null ? null : timer.Schedule,
            ScreenAccess = host,
        });
        model.BeginDrawing();
        return (model, host);
    }

    [Fact]
    public void ADevelopmentBuildNeedsNoAccessAndNeverAsks()
    {
        WorkspaceModel model = Drawing();
        Assert.Equal(ScreenAccess.NotNeeded, model.Snapshot.ScreenAccess);
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Blur));
        Assert.Equal(Tool.Blur, model.Snapshot.Tool);
        model.ToggleMagnifier();
        Assert.True(model.Snapshot.MagnifierEnabled);
        model.StopZoom();
        model.ToggleScreenZoomFromShortcut();
        Assert.Equal(ZoomStartPhase.Starting, model.Snapshot.ZoomStart.Phase);
        Assert.Equal(SettingsCopy.CaptureAllowed, SettingsStatus.Capture(model.Snapshot));
        Assert.Equal(SettingsCopy.OpenCaptureSettings, SettingsStatus.CaptureAction(model.Snapshot));
        Assert.Equal(string.Empty, SettingsStatus.CaptureProblem(model.Snapshot));
    }

    [Fact]
    public void AnEffectWaitsForTheExplanationAndIsChosenOnceWindowsAllowsIt()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        ulong retries = model.Snapshot.EffectsRetry;
        Assert.Equal(ToolChoice.NeedsScreenAccess, model.Choose(Tool.Blur));
        Assert.Equal(Tool.Pen, model.Snapshot.Tool);
        Assert.Equal([true], host.Requests);
        Assert.Equal(ScreenAccessText.Waiting, model.Snapshot.Status);
        Assert.Equal(ScreenAccess.NotAsked, model.Snapshot.ScreenAccess);
        Assert.Equal(SettingsCopy.CaptureNotAllowed, SettingsStatus.Capture(model.Snapshot));
        Assert.Equal(SettingsCopy.AllowScreenAccess, SettingsStatus.CaptureAction(model.Snapshot));
        Assert.Null(model.Snapshot.CaptureWarning);

        host.Answer(ScreenAccess.Allowed);
        Assert.Equal(Tool.Blur, model.Snapshot.Tool);
        Assert.Equal(ScreenAccess.Allowed, model.Snapshot.ScreenAccess);
        Assert.True(model.Snapshot.EffectsRetry > retries, "Live effects start again with access.");
        Assert.Equal(SettingsCopy.CaptureAllowed, SettingsStatus.Capture(model.Snapshot));
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Pixelate));
        Assert.Single(host.Requests);
    }

    [Fact]
    public void TheEffectPickerAndAnEffectKeyWaitToo()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        model.SetCoverMode(Tool.Distort);
        Assert.Equal(Tool.Pen, model.Snapshot.Tool);
        host.Answer(ScreenAccess.NotAsked, ScreenAccessOutcome.NotNow);
        Assert.Equal(ScreenAccessText.Declined, model.Snapshot.Status);
        Assert.NotEqual(CanvasCommandResult.NotHandled,
            model.Perform(new CanvasCommand(CanvasCommandKind.ChooseTool, Primary) { Tool = Tool.Blur }));
        Assert.Equal([true, true], host.Requests);
        host.Answer(ScreenAccess.Allowed);
        Assert.Equal(Tool.Blur, model.Snapshot.Tool);
    }

    [Fact]
    public void ARefusalChoosesNothingAndSaysWhereToAllowIt()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged(ScreenAccess.ScreenOff);
        Assert.Equal(ToolChoice.NeedsScreenAccess, model.Choose(Tool.Blur));
        Assert.Equal(Tool.Pen, model.Snapshot.Tool);
        Assert.Empty(host.Requests);
        string message = ScreenAccessText.Refused(ScreenAccess.ScreenOff, ScreenAccessFeature.Effects);
        Assert.Equal(message, model.Snapshot.CaptureFailureMessage);
        Assert.Contains("Windows Settings > Privacy & security > Screenshots and screen recording", message, StringComparison.Ordinal);
        Assert.Equal(WorkspaceModel.CaptureUnavailableWarning, model.Snapshot.CaptureWarning);
        Assert.Equal(SettingsCopy.CaptureNotAllowed, SettingsStatus.Capture(model.Snapshot));
        Assert.Equal(SettingsCopy.AllowScreenAccess, SettingsStatus.CaptureAction(model.Snapshot));
        Assert.Equal(message, SettingsStatus.CaptureProblem(model.Snapshot));

        host.Access = ScreenAccess.BorderOff;
        model.Choose(Tool.Pixelate);
        Assert.Contains("Screenshot borders", model.Snapshot.CaptureFailureMessage, StringComparison.Ordinal);
        Assert.Empty(host.Requests);
    }

    [Fact]
    public void TheLensStartsOnceAllowedAndFailsClosedAfterARefusal()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        model.ToggleMagnifier();
        Assert.False(model.Snapshot.MagnifierEnabled);
        host.Answer(ScreenAccess.Allowed);
        Assert.True(model.Snapshot.MagnifierEnabled);
        Assert.Equal(WorkspaceModel.LensStatus, model.Snapshot.Status);
        model.StopZoom();

        // The person turned it off in Windows Settings meanwhile.
        host.Access = ScreenAccess.ScreenOff;
        model.ToggleMagnifier();
        Assert.False(model.Snapshot.MagnifierEnabled);
        Assert.Equal(ScreenAccessText.Refused(ScreenAccess.ScreenOff, ScreenAccessFeature.Lens), model.Snapshot.CaptureFailureMessage);
        Assert.Equal(WorkspaceModel.CaptureUnavailableWarning, model.Snapshot.CaptureWarning);
        Assert.Single(host.Requests);

        // Allowed again in Windows Settings: Settings' Refresh status reads it and starts the lens that failed.
        host.Access = ScreenAccess.Allowed;
        Assert.Equal(ScreenAccess.Allowed, model.RefreshScreenAccess());
        Assert.True(model.RetryLiveZoom());
        Assert.True(model.Snapshot.MagnifierEnabled);
    }

    [Fact]
    public void ZoomWaitsWithTheMacsWaitingStateAndStartsOnceAllowed()
    {
        var timer = new ManualTimer();
        (WorkspaceModel model, FakeScreenAccess host) = Packaged(timer: timer);
        model.ToggleScreenZoomFromShortcut();
        WorkspaceSnapshot waiting = model.Snapshot;
        Assert.Equal(ZoomStartPhase.ScreenPermission, waiting.ZoomStart.Phase);
        Assert.Equal(new PendingZoomPresentation("Waiting", "Waiting for screen recording permission"), PendingZoomPresentation.For(waiting.ZoomStart));
        Assert.Equal("Zoom is waiting for screen recording permission.", waiting.Status);
        Assert.False(waiting.ScreenZoomEnabled);
        Assert.Equal([true], host.Requests);

        timer.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ZoomStartPhase.ScreenPermission, model.Snapshot.ZoomStart.Phase);
        Assert.Single(host.Requests);
        host.Answer(ScreenAccess.Allowed);
        Assert.Equal(ZoomStartPhase.Starting, model.Snapshot.ZoomStart.Phase);
        Assert.True(model.Snapshot.ScreenZoomEnabled);
        Assert.Equal(1.5, model.Snapshot.Magnification);
    }

    [Fact]
    public void NotNowCancelsThePendingZoom()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        model.ToggleScreenZoom();
        host.Answer(ScreenAccess.NotAsked, ScreenAccessOutcome.NotNow);
        Assert.Equal(ZoomStartState.Idle, model.Snapshot.ZoomStart);
        Assert.False(model.Snapshot.ScreenZoomEnabled);
        Assert.Equal("Zoom request cancelled.", model.Snapshot.Status);
        Assert.Null(model.Snapshot.CaptureFailureMessage);
    }

    [Fact]
    public void ARefusedZoomFailsAtOnceAndTheToolbarSaysWhereToAllowIt()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged(ScreenAccess.BorderOff);
        model.ToggleScreenZoomFromShortcut();
        string message = ScreenAccessText.Refused(ScreenAccess.BorderOff, ScreenAccessFeature.ScreenZoom);
        Assert.Equal(ZoomStartState.Failed(message), model.Snapshot.ZoomStart);
        Assert.Equal(message, model.Snapshot.CaptureWarning);
        Assert.Equal(message, model.Snapshot.CaptureFailureMessage);
        Assert.StartsWith("Live zoom needs screen access. Annotator isn't allowed to turn off the yellow screenshot border.", message, StringComparison.Ordinal);
        Assert.Empty(host.Requests);

        host.Access = ScreenAccess.Allowed;
        model.RefreshScreenAccess();
        Assert.True(model.RetryLiveZoom());
        Assert.Equal(ZoomStartPhase.Starting, model.Snapshot.ZoomStart.Phase);
    }

    [Fact]
    public void ZoomStopsWaitingAfterTheMacsBoundAndAnUnansweredRequestFails()
    {
        var timer = new ManualTimer();
        (WorkspaceModel model, FakeScreenAccess host) = Packaged(timer: timer);
        model.ToggleScreenZoomFromShortcut();
        timer.Advance(TimeSpan.FromMilliseconds(750 * 120));
        Assert.Equal(ZoomStartState.Failed(ZoomStartController.PermissionTimeoutMessage), model.Snapshot.ZoomStart);

        model.ToggleMagnifier();
        host.Answer(ScreenAccess.NotAsked, ScreenAccessOutcome.NoAnswer);
        Assert.False(model.Snapshot.MagnifierEnabled);
        Assert.Equal(ScreenAccessText.NoAnswer(ScreenAccessFeature.Lens), model.Snapshot.CaptureFailureMessage);
        Assert.Equal("The pointer lens needs screen access. Windows got no answer to its question. Try again.", model.Snapshot.CaptureFailureMessage);
    }

    [Fact]
    public void ANewerRequestReplacesTheOpenOne()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        model.Choose(Tool.Blur);
        model.ToggleMagnifier();
        Assert.Equal([true, true], host.Requests);
        host.Answer(ScreenAccess.Allowed);
        Assert.True(model.Snapshot.MagnifierEnabled);
        Assert.Equal(Tool.Pen, model.Snapshot.Tool);
    }

    [Fact]
    public void SettingsAsksAtOnceAndStartsWhatFailedForWantOfAccess()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        model.ToggleMagnifier();
        host.Answer(ScreenAccess.NotAsked, ScreenAccessOutcome.NoAnswer);
        Assert.False(model.Snapshot.MagnifierEnabled);

        model.RequestScreenAccess();
        Assert.Equal([true, false], host.Requests);
        host.Answer(ScreenAccess.Allowed);
        Assert.True(model.Snapshot.MagnifierEnabled);
        Assert.Equal(SettingsCopy.OpenCaptureSettings, SettingsStatus.CaptureAction(model.Snapshot));

        model.RequestScreenAccess();
        Assert.Equal(ScreenAccessText.Allowed, model.Snapshot.Status);
        Assert.Equal(2, host.Requests.Count);
    }

    [Fact]
    public void SettingsCannotAskAgainAfterARefusal()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged(ScreenAccess.ScreenOff);
        model.RequestScreenAccess();
        Assert.Empty(host.Requests);
        Assert.Equal(ScreenAccessText.RefusedInSettings(ScreenAccess.ScreenOff), model.Snapshot.Status);
        Assert.EndsWith("then select Refresh status.", model.Snapshot.Status, StringComparison.Ordinal);
        Assert.Equal(model.Snapshot.Status, SettingsStatus.CaptureProblem(model.Snapshot));

        (WorkspaceModel asked, FakeScreenAccess windows) = Packaged();
        asked.RequestScreenAccess();
        windows.Answer(ScreenAccess.ScreenOff);
        Assert.Equal(ScreenAccess.ScreenOff, asked.Snapshot.ScreenAccess);
        Assert.Equal(ScreenAccessText.RefusedInSettings(ScreenAccess.ScreenOff), asked.Snapshot.Status);
    }

    [Fact]
    public void RefreshReadsWindowsAgain()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        Assert.Equal(ScreenAccess.NotAsked, model.Snapshot.ScreenAccess);
        host.Access = ScreenAccess.Blocked;
        Assert.Equal(ScreenAccess.NotAsked, model.Snapshot.ScreenAccess);
        Assert.Equal(ScreenAccess.Blocked, model.RefreshScreenAccess());
        Assert.Equal(ScreenAccess.Blocked, model.Snapshot.ScreenAccess);
        Assert.Contains("ask your administrator", SettingsStatus.CaptureProblem(model.Snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void RegionSharingStartsOnceAllowedAndFailsWithWhereToAllowIt()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        var sharing = new RegionSharing(model, new SharingHost());
        sharing.ShowControls();
        sharing.Start();
        Assert.False(sharing.State.IsCapturing);
        Assert.False(sharing.HasOutput);
        host.Answer(ScreenAccess.NotAsked, ScreenAccessOutcome.NotNow);
        Assert.Equal(RegionSharingState.Idle.Instance, sharing.State);
        Assert.Null(model.Snapshot.ErrorMessage);

        sharing.Start();
        host.Answer(ScreenAccess.Allowed);
        Assert.True(sharing.State.IsCapturing);
        sharing.Stop();

        host.Access = ScreenAccess.ScreenOff;
        sharing.Start();
        string message = ScreenAccessText.Refused(ScreenAccess.ScreenOff, ScreenAccessFeature.RegionSharing);
        Assert.Equal(new RegionSharingState.Failed(message), sharing.State);
        Assert.Equal(message, model.Snapshot.ErrorMessage);
        Assert.Equal(message, model.Snapshot.CaptureFailureMessage);
        Assert.EndsWith("then start sharing again.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostFeatureGetsTheWordsOrNullForNotNow()
    {
        (WorkspaceModel model, FakeScreenAccess host) = Packaged();
        int started = 0;
        var said = new List<string?>();
        Assert.False(model.EnsureScreenAccess(ScreenAccessFeature.PickColor, () => started++, said.Add));
        host.Answer(ScreenAccess.NotAsked, ScreenAccessOutcome.NotNow);
        Assert.Equal([null], said);
        Assert.False(model.EnsureScreenAccess(ScreenAccessFeature.PickColor, () => started++, said.Add));
        host.Answer(ScreenAccess.Allowed);
        Assert.Equal(1, started);
        Assert.True(model.EnsureScreenAccess(ScreenAccessFeature.PickColor, () => started++, said.Add));
        Assert.Equal(1, started);

        host.Access = ScreenAccess.ScreenOff;
        Assert.False(model.EnsureScreenAccess(ScreenAccessFeature.PickColor, () => started++, said.Add));
        Assert.Equal(ScreenAccessText.Refused(ScreenAccess.ScreenOff, ScreenAccessFeature.PickColor), said[^1]);
        Assert.Null(model.Snapshot.CaptureFailureMessage);
    }

    [Fact]
    public void TheWordsNameTheWindowsPagesAndTheNextStep()
    {
        Assert.Equal("Effect regions need screen access, so they stay covered. Screenshots and screen recording is off for Annotator. " +
                     "Allow it in Windows Settings > Privacy & security > Screenshots and screen recording, then select an effect tool to try again.",
            ScreenAccessText.Refused(ScreenAccess.ScreenOff, ScreenAccessFeature.Effects));
        Assert.Equal("Annotator isn't allowed to turn off the yellow screenshot border. Allow it in Windows Settings > Privacy & security > " +
                     "Screenshot borders, then select Refresh status.", ScreenAccessText.RefusedInSettings(ScreenAccess.BorderOff));
        Assert.Equal("Effect regions need screen access, so they stay covered. Select an effect tool to allow it.",
            ScreenAccessText.NotAsked(ScreenAccessFeature.Effects));
        Assert.Equal("Region sharing needs screen access. Start sharing again to allow it.", ScreenAccessText.NotAsked(ScreenAccessFeature.RegionSharing));
        Assert.Equal(ScreenAccessText.Title, SettingsCopy.CaptureTitle);
        Assert.Equal("Allow screen access", SettingsCopy.AllowScreenAccess);
        foreach (string text in new[] { ScreenAccessText.Ask, ScreenAccessText.Asking, ScreenAccessText.Declined, ScreenAccessText.Allowed })
        {
            Assert.DoesNotContain("macOS", text, StringComparison.Ordinal);
            Assert.DoesNotContain("System Settings", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePackagedTutorialUsesTheMacsPermissionWords()
    {
        Assert.Equal(TutorialText.SharingBody, TutorialText.SharingBodyFor(ScreenAccess.NotNeeded));
        Assert.EndsWith("Drawing needs no permissions. Zoom, effects and region sharing ask when needed.",
            TutorialText.SharingBodyFor(ScreenAccess.Allowed), StringComparison.Ordinal);
        Assert.EndsWith("Drawing needs no permissions, and neither do zoom, effects or region sharing.",
            TutorialText.SharingBodyFor(ScreenAccess.NotNeeded), StringComparison.Ordinal);
    }

    /// <summary>The Share a region window, outline and selection panels, which these tests only need to exist.</summary>
    private sealed class SharingHost : IRegionSharingHost
    {
        public bool DesktopIsLocked => false;

        public bool ControlsVisible { get; private set; }

        public bool IsSelecting => false;

        public string? DisplayName(DisplayId display) => null;

        public void ShowControls() => ControlsVisible = true;

        public void HideControls() => ControlsVisible = false;

        public void RestoreControls(bool activate) => ControlsVisible = true;

        public void CloseControls() => ControlsVisible = false;

        public void ShowOutline(SharingRegion region, DisplayDescriptor display, bool selectionFill)
        {
        }

        public void HideOutline()
        {
        }

        public void ChooseRegion(IReadOnlyList<RegionSharingDisplay> displays, DisplayId preferred, Action<RegionSelectionResult> completed)
        {
        }

        public void CancelSelection()
        {
        }

        public IDisposable Schedule(TimeSpan delay, Action action) => new Unscheduled();

        private sealed class Unscheduled : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
