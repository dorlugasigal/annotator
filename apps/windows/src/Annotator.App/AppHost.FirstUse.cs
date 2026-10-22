using Annotator.App.Introduction;
using Annotator.App.Reference;
using Annotator.App.Shell;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Microsoft.UI.Dispatching;

namespace Annotator.App;

/// <summary>
/// First use (spec 005, WIN-UI-04): the tour once per launch after the toolbar, the coffee reminder over the toolbar after
/// days of use, and the Help entry that opens the tour again.
/// </summary>
internal sealed partial class AppHost
{
    /// <summary>
    /// Mac finishLaunchPresentation: days of use count from this launch, and the tour shows once the toolbar has drawn its
    /// first frame and the overlays exist, without taking focus. A quiet start at sign-in uses up the launch's tour, as the
    /// Mac's login item does. A file of first-use state that could not be read is reported; its values start again.
    /// </summary>
    private void StartFirstUse(bool quiet)
    {
        if (_defaults.LoadFailure is { } failure)
        {
            _model.Report(failure);
        }
        _reminder.RecordLaunch();
        _usage.RecordLaunch();
        if (quiet)
        {
            _introduction.ShowAutomatically(startupLaunch: true);
            return;
        }
        _toolbar.FirstFrame += () => _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_quitting)
            {
                _introduction.ShowAutomatically(startupLaunch: false);
            }
        });
    }

    /// <summary>
    /// Opens the tour with focus (Mac onShowIntroduction: Settings > Help's Quick start and General's Show introduction call
    /// this too), or leaves the Mac's status message that it cannot open now in the workspace.
    /// </summary>
    internal void ShowIntroduction()
    {
        if (_quitting)
        {
            return;
        }
        CloseTransientWindows();
        // Settings floats above other windows and the tour is a normal one (the Mac's levels), so Settings steps back while
        // the tour it opened shows in front of it.
        if (_settings is { IsShown: true } settings && _introduction.IsEligible)
        {
            settings.StepBack();
        }
        _introduction.ShowManually();
    }

    /// <summary>
    /// Each snapshot: entering Draw counts a day of use (Mac recordUse), an open tour closes once it is no longer safe, and the
    /// coffee reminder follows whether the workspace allows it.
    /// </summary>
    private void OnFirstUsePublished(WorkspaceSnapshot snapshot)
    {
        if (snapshot.Mode == WorkspaceMode.Draw && _lastMode != WorkspaceMode.Draw)
        {
            _reminder.RecordUse();
        }
        _lastMode = snapshot.Mode;
        _introduction.Apply(snapshot);
        UpdateSupportReminder();
    }

    /// <summary>
    /// Mac SupportReminderPresentation: the reminder shows next to the visible toolbar while it is due and the workspace allows
    /// it (Use apps, nothing animating or failing, no tour, no edit in progress), and goes when that ends. Shown counts once
    /// per launch; Not now, an opened support link and closing it by hand snooze it.
    /// </summary>
    private void UpdateSupportReminder()
    {
        if (_quitting)
        {
            return;
        }
        WorkspaceSnapshot snapshot = _model.Snapshot;
        bool show = _reminder.Available && snapshot.ToolbarVisible &&
            SupportReminderRules.CanShow(snapshot, BuildInfo.SupportLink is not null, _settings?.IsShown == true, _introduction.IsVisible);
        if (show && _reminderPopover is null)
        {
            var popover = new SupportReminderPopover(_registry, OpenSupportLink, _reminder.Snooze);
            popover.Dismissed += byPerson =>
            {
                if (_reminderPopover == popover)
                {
                    _reminderPopover = null;
                }
                if (byPerson && _reminder.Available)
                {
                    _reminder.Snooze();
                }
            };
            _reminderPopover = popover;
            popover.ShowNear(_toolbar.Frame);
            _reminder.MarkPresented();
        }
        else if (!show && _reminderPopover is { } shown)
        {
            _reminderPopover = null;
            shown.Dismiss(byPerson: false);
        }
    }

    /// <summary>Quit: the reminder and the tour close for good.</summary>
    private void CloseFirstUse()
    {
        _reminderPopover?.Dismiss(byPerson: false);
        _reminderPopover = null;
        _introduction.Shutdown();
    }

    /// <summary>
    /// Development only (live check): when the script sets the first-use event, this launch counts as one, four days of use
    /// are already recorded, and the tour shows as it does after the toolbar at a real launch. The next Draw then makes the
    /// coffee reminder due, and Use apps shows it. The values live in memory only.
    /// </summary>
    private void WatchLiveCheckFirstUse()
    {
        var firstUse = new EventWaitHandle(false, EventResetMode.ManualReset, LiveCheckFirstUseEventName(Environment.ProcessId));
        ThreadPool.RegisterWaitForSingleObject(firstUse, (_, _) => _queue.TryEnqueue(() =>
        {
            firstUse.Dispose();
            if (_quitting)
            {
                return;
            }
            _defaults.Set(SupportReminder.UseDaysKey, SupportReminder.FirstUseDays - 1);
            _defaults.Set(SupportReminder.LastUseKey, DateTimeOffset.Now.AddDays(-1));
            _reminder.RecordLaunch();
            _introduction.ShowAutomatically(startupLaunch: false);
        }), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>The live check's first-use event (<see cref="WatchLiveCheckFirstUse"/>).</summary>
    public static string LiveCheckFirstUseEventName(int processId) => $@"Local\Annotator.LiveCheck.{processId}.first-use";

    /// <summary>The tour, for UI checks and measurements.</summary>
    internal IntroductionController Introduction => _introduction;
}
