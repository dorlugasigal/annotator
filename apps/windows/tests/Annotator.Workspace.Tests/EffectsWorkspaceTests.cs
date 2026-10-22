using Annotator.Core;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

/// <summary>
/// Live effects in the workspace (Mac WorkspaceModel: choose, chooseCover, setCoverMode, censorFallbackActive,
/// reportCaptureFailure, noteCaptureFrame and suspendPresentation).
/// </summary>
public sealed class EffectsWorkspaceTests
{
    [Fact]
    public void EffectsAreChosenRememberedAndResumeCapture()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        model.ShowToolbar();
        ulong retry = model.Snapshot.EffectsRetry;

        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Cover));
        Assert.Equal(Tool.Blur, model.Snapshot.Tool);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Equal(Style.Defaults(Tool.Blur), model.Snapshot.Style);
        Assert.Equal(Tool.Blur, model.Snapshot.Preferences.LastCoverMode);
        Assert.True(model.Snapshot.EffectsRetry > retry);

        retry = model.Snapshot.EffectsRetry;
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Pixelate));
        Assert.True(model.Snapshot.EffectsRetry > retry);
        model.Choose(Tool.Pen);
        Assert.Equal(Tool.Pixelate, model.Snapshot.Preferences.LastCoverMode);
        retry = model.Snapshot.EffectsRetry;
        model.Choose(Tool.Rectangle);
        Assert.Equal(retry, model.Snapshot.EffectsRetry);

        // The Effects button and its key choose the last effect.
        model.Choose(Tool.Cover);
        Assert.Equal(Tool.Pixelate, model.Snapshot.Tool);
        Assert.Equal(Tool.Pixelate, WorkspaceModel.PreferredEffect(model.Snapshot.Preferences));
        WorkspacePreferences saved = WorkspacePreferences.Read(store.Data!);
        Assert.Equal(Tool.Pixelate, saved.LastCoverMode);
    }

    [Fact]
    public void FocusAndCleanupClearsDrawingsOrEffectsOnEveryDisplayAsOneUndoStep()
    {
        // Mac backgroundMenu: "Clear drawings (keep effects)" (clearDrawingsKeepingProtection, Core clearAnnotations) and
        // "Remove all censors" (clearProtection), each through performAcrossDisplays.
        WorkspaceModel model = Drawing();
        var stroke = new Element(Tool.Pen, new Rect(10, 10, 40, 40));
        var blur = new Element(Tool.Blur, new Rect(100, 100, 200, 120));
        var locked = new Element(Tool.Rectangle, new Rect(20, 300, 80, 60)) { Locked = true };
        var lockedCover = new Element(Tool.Cover, new Rect(400, 300, 80, 60)) { Locked = true };
        var arrow = new Element(Tool.Arrow, new Rect(30, 30, 120, 40));
        var pixelate = new Element(Tool.Pixelate, new Rect(200, 40, 90, 60));
        AssertAccepted(model.Add(Primary, stroke, blur, locked, lockedCover));
        AssertAccepted(model.Add(Left, arrow, pixelate));

        model.ClearDrawingsKeepingProtection();
        Assert.Equal([blur, locked, lockedCover], model.Elements(Primary));
        Assert.Equal([pixelate], model.Elements(Left));
        model.Undo();
        Assert.Equal([stroke, blur, locked, lockedCover], model.Elements(Primary));
        Assert.Equal([arrow, pixelate], model.Elements(Left));

        model.ClearProtection();
        Assert.Equal([stroke, locked], model.Elements(Primary));
        Assert.Equal([arrow], model.Elements(Left));
        model.Undo();
        Assert.Equal([stroke, blur, locked, lockedCover], model.Elements(Primary));
        Assert.Equal([arrow, pixelate], model.Elements(Left));

        // A second Remove all censors finds nothing and leaves no Undo step: one Undo brings the effects back.
        model.ClearProtection();
        model.ClearProtection();
        model.Undo();
        Assert.Equal([stroke, blur, locked, lockedCover], model.Elements(Primary));
        Assert.Equal([arrow, pixelate], model.Elements(Left));
    }

    [Fact]
    public void TheEffectPickerChangesSelectedRegionsAsOneUndoStep()
    {
        WorkspaceModel model = Drawing();
        var blur = new Element(Tool.Blur, new Rect(100, 100, 200, 120));
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(blur);
            document[0].Select([blur.Id]);
        }));
        model.Choose(Tool.Select);
        Assert.Equal(Tool.Blur, model.Snapshot.EffectiveTool);

        model.SetCoverMode(Tool.Distort);
        Assert.Equal(Tool.Distort, Assert.Single(model.Elements(Primary)).Tool);
        Assert.Equal(Tool.Distort, model.Snapshot.EffectiveTool);
        Assert.Equal(Tool.Distort, model.Snapshot.Preferences.LastCoverMode);
        Assert.Equal(Tool.Select, model.Snapshot.Tool);
        model.Undo();
        Assert.Equal(Tool.Blur, Assert.Single(model.Elements(Primary)).Tool);

        // Without such a selection the choice becomes the tool.
        model.Choose(Tool.Pen);
        model.SetCoverMode(Tool.Pixelate);
        Assert.Equal(Tool.Pixelate, model.Snapshot.Tool);
        Assert.Empty(model.Document(Primary).Selection);
    }

    [Fact]
    public void TheEffectPickerRefusesLockedRegionsAndSolidBlocks()
    {
        WorkspaceModel model = Drawing();
        var locked = new Element(Tool.Pixelate, new Rect(100, 100, 200, 120)) { Locked = true };
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(locked);
            document[0].Select([locked.Id]);
        }));
        model.Choose(Tool.Select);

        model.SetCoverMode(Tool.Blur);
        Assert.Equal(Tool.Pixelate, Assert.Single(model.Elements(Primary)).Tool);
        Assert.Contains("Unlock this cover before changing its mode.", model.Snapshot.ErrorMessage, StringComparison.Ordinal);

        model.SetCoverMode(Tool.Cover);
        Assert.Contains("Choose Blur, Pixelate or Distort. Use Rectangle for a solid block.", model.Snapshot.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(Tool.Select, model.Snapshot.Tool);
    }

    [Fact]
    public void TheToolbarWarnsWhileTheActiveDisplaysRegionsWaitOrCaptureFailed()
    {
        WorkspaceModel model = Drawing();
        Assert.Equal(Primary, model.Snapshot.ActiveDisplay);
        Assert.Null(model.Snapshot.CaptureWarning);
        ulong revision = model.Snapshot.Revision;

        model.SetEffectsState(Left, regions: true, ready: false);
        Assert.False(model.Snapshot.CensorFallbackActive);
        Assert.Null(model.Snapshot.CaptureWarning);

        model.SetEffectsState(Primary, regions: true, ready: false);
        Assert.True(model.Snapshot.CensorFallbackActive);
        Assert.Equal(WorkspaceModel.CaptureCoveredWarning, model.Snapshot.CaptureWarning);
        revision = model.Snapshot.Revision;
        model.SetEffectsState(Primary, regions: true, ready: false);
        Assert.Equal(revision, model.Snapshot.Revision);

        model.SetEffectsState(Primary, regions: true, ready: true);
        Assert.False(model.Snapshot.CensorFallbackActive);
        Assert.Null(model.Snapshot.CaptureWarning);

        const string failure = "Screen capture did not provide a usable image.";
        model.ReportCaptureFailure(failure);
        Assert.Equal(failure, model.Snapshot.CaptureFailureMessage);
        Assert.Equal(failure, model.Snapshot.Status);
        Assert.Equal(WorkspaceModel.CaptureUnavailableWarning, model.Snapshot.CaptureWarning);
        revision = model.Snapshot.Revision;
        model.ReportCaptureFailure(failure);
        Assert.Equal(revision, model.Snapshot.Revision);

        model.NoteCaptureFrame();
        Assert.Null(model.Snapshot.CaptureFailureMessage);
        Assert.Null(model.Snapshot.CaptureWarning);
    }

    [Fact]
    public void ShowingTheWorkspaceAgainResumesLiveEffects()
    {
        WorkspaceModel model = Drawing();
        model.ShowToolbar();
        ulong retry = model.Snapshot.EffectsRetry;
        model.HideTools();
        Assert.Equal(retry, model.Snapshot.EffectsRetry);
        model.ResumeDrawing();
        Assert.False(model.Snapshot.WorkspaceHidden);
        Assert.True(model.Snapshot.EffectsRetry > retry);

        retry = model.Snapshot.EffectsRetry;
        model.RetryLiveEffects();
        Assert.True(model.Snapshot.EffectsRetry > retry);
    }

    [Fact]
    public void ASystemTransitionReleasesInputAndSaysCaptureIsPaused()
    {
        WorkspaceModel model = Drawing();
        model.ShowToolbar();
        Assert.Equal(SurfaceMode.Draw, model.Snapshot.Surfaces);

        model.SuspendPresentation();
        Assert.Equal(WorkspaceMode.UseApps, model.Snapshot.Mode);
        Assert.Equal(SurfaceMode.UseApps, model.Snapshot.Surfaces);
        Assert.All(model.Snapshot.Displays, display => Assert.False(display.TakesInput));
        Assert.Equal(WorkspaceModel.CapturePausedMessage, model.Snapshot.Status);

        // Choosing an effect tool resumes drawing and asks the host to resume capture.
        ulong retry = model.Snapshot.EffectsRetry;
        model.Choose(Tool.Blur);
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.True(model.Snapshot.EffectsRetry > retry);
    }

    [Fact]
    public void ASystemTransitionStopsARunningShowOrHide()
    {
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [PrimaryDisplay],
            AnimatesVisibility = true,
        });
        model.BeginDrawing();
        model.ShowToolbar();
        model.HideTools();
        Assert.NotNull(model.Snapshot.Visibility);

        model.SuspendPresentation();
        Assert.Null(model.Snapshot.Visibility);
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.False(model.Snapshot.ToolbarVisible);
        Assert.Equal(WorkspaceModel.CapturePausedMessage, model.Snapshot.Status);
    }
}
