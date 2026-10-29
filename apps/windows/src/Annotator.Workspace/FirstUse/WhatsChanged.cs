using Annotator.Workspace.Settings;

namespace Annotator.Workspace.FirstUse;

/// <summary>
/// Port of the Mac's <c>WhatsChangedTracker</c>: What's changed follows an update once for each newer build with notes
/// bundled in the app. A first install, an older or equal build and a build without notes (a development build) never
/// show it, and record the build at once; a due build is recorded only once the window shows (<see cref="Record"/>).
/// Builds are derived from the version (0.2.0 is 200) and above the builds of earlier versions (35 and lower), so an
/// update from any of them shows it.
/// </summary>
public sealed class WhatsChangedTracker(AppDefaults defaults)
{
    public const string LastBuildKey = "whats-changed-last-build";

    /// <param name="build">The running build.</param>
    /// <param name="usedBefore">
    /// An earlier Annotator saved settings here, so a launch without <see cref="LastBuildKey"/> follows an update from a
    /// version before What's changed existed.
    /// </param>
    /// <param name="notes">The bundled notes of the running build; throws <see cref="ReleaseNotesFailure"/> without them.</param>
    public ReleaseNotesEntry? Due(int build, bool usedBefore, Func<ReleaseNotesEntry> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        bool known = defaults.Contains(LastBuildKey);
        if (known && build <= defaults.Integer(LastBuildKey))
        {
            return null;
        }
        if (!known && !usedBefore)
        {
            Record(build);
            return null;
        }
        ReleaseNotesEntry? entry;
        try
        {
            entry = notes();
        }
        catch (ReleaseNotesFailure)
        {
            entry = null;
        }
        if (entry is null || entry.Build != build)
        {
            Record(build);
            return null;
        }
        return entry;
    }

    public void Record(int build) => defaults.Set(LastBuildKey, (long)build);
}

/// <summary>
/// The Mac's <c>canShowAnnouncement</c>: a window about the app itself (an update, or What's changed after one) may appear
/// without covering a presentation.
/// </summary>
public static class AnnouncementRules
{
    /// <summary>
    /// Not drawing, nothing animating, no zoom, no region sharing, no break, no capture failure, no error, no introduction
    /// and no edit in progress on any display.
    /// </summary>
    public static bool CanShow(WorkspaceSnapshot snapshot, bool introductionVisible, bool breakRunning)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return !(snapshot.Mode == WorkspaceMode.Draw && !snapshot.WorkspaceHidden) && snapshot.Visibility is null
            && !snapshot.ZoomActive && !snapshot.RegionSharing.IsCapturing && !breakRunning
            && snapshot.CaptureFailureMessage is null && !introductionVisible && snapshot.ErrorMessage is null
            && !SupportReminderRules.HasActiveEdit(snapshot);
    }
}

/// <summary>The words of What's changed after an update (Mac WhatsChangedView) and its accessibility identifiers.</summary>
public static class WhatsChangedText
{
    public const string Title = "What's changed";
    public const string Heading = "Annotator is updated";
    public const string KnownLimits = "Known limits";
    public const string Close = "Close";
    public const string Identifier = "whats-changed";
    public const string CloseIdentifier = "whats-changed-close";
    public const string KnownLimitsIdentifier = "whats-changed-known-limits";

    /// <summary>The Mac's "You now have version 0.2.0.": the version only, without the build.</summary>
    public static string Version(ReleaseNotesEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"You now have version {entry.Version}.";
    }

    /// <summary>The window's fixed size in points (Mac WhatsChangedView.width and height).</summary>
    public const double Width = 480;
    public const double Height = 560;
}
