using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Settings;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.FirstUse;

/// <summary>
/// What's changed after an update (Mac WhatsChangedTests): once per newer build with bundled notes, never after a first
/// install, a downgrade or a build without notes, and only when it covers nothing the person presents.
/// </summary>
public sealed class WhatsChangedTests
{
    private readonly AppDefaults _defaults = AppDefaults.InMemory();

    private static Func<ReleaseNotesEntry> Notes(int build) =>
        () => new ReleaseNotesEntry($"0.1.0-beta.{build}", build, "2026-10-24", [("Fixed", ["A fix."])], []);

    [Fact]
    public void AFirstInstallRecordsTheBuildWithoutShowing()
    {
        var tracker = new WhatsChangedTracker(_defaults);
        Assert.Null(tracker.Due(30, usedBefore: false, Notes(30)));
        Assert.Equal(30, _defaults.Integer(WhatsChangedTracker.LastBuildKey));
        Assert.Equal(31, tracker.Due(31, usedBefore: true, Notes(31))?.Build);
        Assert.Equal(31, tracker.Due(31, usedBefore: false, Notes(31))?.Build);
    }

    [Fact]
    public void AnUpdateFromBeforeWhatsChangedExistedStillShowsIt()
    {
        Assert.Equal(31, new WhatsChangedTracker(_defaults).Due(31, usedBefore: true, Notes(31))?.Build);
        Assert.False(_defaults.Contains(WhatsChangedTracker.LastBuildKey), "Recorded only once the window shows.");
    }

    [Fact]
    public void OlderEqualAndNotelessBuildsNeverShow()
    {
        var tracker = new WhatsChangedTracker(_defaults);
        tracker.Record(31);
        Assert.Null(tracker.Due(31, true, Notes(31)));
        Assert.Null(tracker.Due(30, true, Notes(30)));
        Assert.Equal(31, _defaults.Integer(WhatsChangedTracker.LastBuildKey));
        Assert.Null(tracker.Due(32, true, () => throw ReleaseNotesFailure.MissingVersion()));
        Assert.Equal(32, _defaults.Integer(WhatsChangedTracker.LastBuildKey));
        Assert.Null(tracker.Due(33, true, Notes(34)));
        Assert.Equal(33, _defaults.Integer(WhatsChangedTracker.LastBuildKey));
    }

    [Fact]
    public void AnnouncementsWaitForDrawingZoomSharingBreaksErrorsAndTheIntroduction()
    {
        WorkspaceModel model = Create();
        Assert.True(AnnouncementRules.CanShow(model.Snapshot, false, false), "A quiet launch with nothing shown.");
        model.ShowToolbar();
        model.BeginDrawing();
        Assert.False(AnnouncementRules.CanShow(model.Snapshot, false, false), "Drawing is never covered.");
        model.UseApps();
        Assert.True(AnnouncementRules.CanShow(model.Snapshot, false, false));
        Assert.False(AnnouncementRules.CanShow(model.Snapshot, introductionVisible: true, false));
        Assert.False(AnnouncementRules.CanShow(model.Snapshot, false, breakRunning: true));
        model.ToggleMagnifier();
        Assert.False(AnnouncementRules.CanShow(model.Snapshot, false, false));
        model.StopZoom();
        model.Report("Test error");
        Assert.False(AnnouncementRules.CanShow(model.Snapshot, false, false));
        model.DismissError();
        model.ReportCaptureFailure("Screen capture stopped.");
        Assert.False(AnnouncementRules.CanShow(model.Snapshot, false, false));
        model.NoteCaptureFrame();
        model.HideTools();
        Assert.True(AnnouncementRules.CanShow(model.Snapshot, false, false), "Hidden drawings cover nothing.");
    }

    [Fact]
    public void TheWordsMatchTheMac()
    {
        var entry = new ReleaseNotesEntry("0.1.0-beta.13", 31, "2026-10-24", [("Added", ["x"])], []);
        Assert.Equal("You now have version 0.1.0-beta.13 (31).", WhatsChangedText.Version(entry));
        Assert.Equal("What's changed", WhatsChangedText.Title);
        Assert.Equal("Annotator is updated", WhatsChangedText.Heading);
    }
}
