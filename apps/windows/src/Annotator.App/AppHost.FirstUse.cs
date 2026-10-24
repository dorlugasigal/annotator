using Annotator.App.Tutorial;
using Annotator.App.Reference;
using Annotator.App.Settings;
using Annotator.App.Shell;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Microsoft.UI.Dispatching;

namespace Annotator.App;

/// <summary>
/// First use (spec 005, WIN-UI-04): the tutorial's welcome card once per launch after the toolbar, the coffee reminder over
/// the toolbar after days of use, and the Help entry that opens the tutorial again.
/// </summary>
internal sealed partial class AppHost
{
    /// <summary>
    /// Mac finishLaunchPresentation: days of use count from this launch, and the welcome card shows once the toolbar has drawn
    /// its first frame and the overlays exist, without taking focus. A quiet start at sign-in uses up the launch's card, as the
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
        _whatsChangedWaits = quiet;
        // A portable copy's daily check (Sparkle's scheduled check): at launch, and when the tray flyout or Settings opens.
        _queue.TryEnqueue(DispatcherQueuePriority.Low, CheckForUpdatesIfDue);
        if (quiet)
        {
            _tutorial.ShowAutomatically(startupLaunch: true);
            return;
        }
        _toolbar.FirstFrame += () => _queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_quitting)
            {
                _tutorial.ShowAutomatically(startupLaunch: false);
                UpdateWhatsChanged();
            }
        });
    }

    /// <summary>
    /// Opens the tutorial's welcome card with focus (Mac onShowIntroduction: Settings > Help's Tutorial and General's Show tutorial call
    /// this too), or leaves the Mac's status message that it cannot open now in the workspace.
    /// </summary>
    internal void ShowTutorial()
    {
        if (_quitting)
        {
            return;
        }
        CloseTransientWindows();
        // Settings floats above other windows, so it steps back while the tutorial card it opened shows in front of it.
        if (_settings is { IsShown: true } settings && _tutorial.IsEligible)
        {
            settings.StepBack();
        }
        _tutorial.ShowManually();
    }

    /// <summary>
    /// Each snapshot: entering Draw counts a day of use (Mac recordUse), the tutorial follows the workspace, and the
    /// coffee reminder follows whether the workspace allows it.
    /// </summary>
    private void OnFirstUsePublished(WorkspaceSnapshot snapshot)
    {
        if (snapshot.Mode == WorkspaceMode.Draw && _lastMode != WorkspaceMode.Draw)
        {
            _reminder.RecordUse();
        }
        _lastMode = snapshot.Mode;
        _tutorial.Apply(snapshot);
        UpdateSupportReminder();
        UpdateWhatsChanged();
    }

    /// <summary>
    /// The Mac's WhatsChangedController.showIfReady: after an update, What's changed shows once, when it covers nothing the
    /// person presents (<see cref="AnnouncementRules"/>), and after a quiet sign-in launch only once the person opened
    /// Annotator (the toolbar or the tray flyout). The build counts as seen once the window shows.
    /// </summary>
    private void UpdateWhatsChanged()
    {
        if (_quitting || _whatsChangedDue is not { } entry || _whatsChanged is not null)
        {
            return;
        }
        WorkspaceSnapshot snapshot = _model.Snapshot;
        if (_whatsChangedWaits && snapshot.ToolbarVisible)
        {
            _whatsChangedWaits = false;
        }
        if (_whatsChangedWaits || !AnnouncementRules.CanShow(snapshot, _tutorial.IsVisible, _breaks.IsRunning))
        {
            return;
        }
        _whatsChangedDue = null;
        _whatsChangedTracker.Record(entry.Build);
        var window = new WhatsChangedWindow(entry, _settingsActions);
        window.Dismissed += () =>
        {
            if (_whatsChanged == window)
            {
                _whatsChanged = null;
                UpdateSupportReminder();
            }
        };
        _whatsChanged = window;
        UpdateSupportReminder();
        window.Present();
    }

    /// <summary>The person opened the tray flyout after a quiet sign-in launch: What's changed may follow.</summary>
    private void PersonOpenedAnnotator()
    {
        if (_whatsChangedWaits)
        {
            _whatsChangedWaits = false;
            UpdateWhatsChanged();
        }
    }

    /// <summary>
    /// A portable copy's automatic check, at most once a day (WIN-SYS-03, WIN-SYS-07: no timer runs). When it finds a release
    /// that was never announced and nothing is being presented, Settings asks about it once, as Sparkle's window does on the
    /// Mac; otherwise the tray flyout and Settings say Update available.
    /// </summary>
    private void CheckForUpdatesIfDue() => _ = CheckForUpdatesIfDueAsync();

    private async Task CheckForUpdatesIfDueAsync()
    {
        if (_quitting || !_updates.ChecksFeed)
        {
            return;
        }
        bool announce = await _updates.CheckIfDueAsync();
        if (!announce || _quitting || _updates.Release is not { } release
            || !AnnouncementRules.CanShow(_model.Snapshot, _tutorial.IsVisible, _breaks.IsRunning) || _whatsChanged is not null)
        {
            return;
        }
        OpenSettings(Annotator.Workspace.Settings.SettingsTab.General);
        if (_settings is not { } settings)
        {
            return;
        }
        switch (await settings.AskAboutReleaseAsync(release))
        {
            case UpdateDialog.ReleaseChoice.Download:
                _settingsActions.OpenUpdateDownloads();
                break;
            case UpdateDialog.ReleaseChoice.CopyWinget:
                _settingsActions.CopyWingetCommand();
                break;
        }
    }

    /// <summary>
    /// Mac SupportReminderPresentation: the reminder shows next to the visible toolbar while it is due and the workspace allows
    /// it (Use apps, nothing animating or failing, no tutorial card, no edit in progress), and goes when that ends. Shown counts once
    /// per launch; Not now, an opened support link and closing it by hand snooze it.
    /// </summary>
    private void UpdateSupportReminder()
    {
        if (_quitting)
        {
            return;
        }
        WorkspaceSnapshot snapshot = _model.Snapshot;
        bool show = _reminder.Available && snapshot.ToolbarVisible && _whatsChanged is null &&
            SupportReminderRules.CanShow(snapshot, BuildInfo.SupportLink is not null, _settings?.IsShown == true, _tutorial.IsVisible);
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

    /// <summary>Quit: the reminder, What's changed and the tutorial close for good.</summary>
    private void CloseFirstUse()
    {
        _reminderPopover?.Dismiss(byPerson: false);
        _reminderPopover = null;
        _whatsChanged?.Close();
        _whatsChanged = null;
        _tutorial.Shutdown();
    }

    /// <summary>
    /// Development only (live check): when the script sets the first-use event, this launch counts as one, four days of use
    /// are already recorded, and the welcome card shows as it does after the toolbar at a real launch. The next Draw then makes the
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
            _tutorial.ShowAutomatically(startupLaunch: false);
        }), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>The live check's first-use event (<see cref="WatchLiveCheckFirstUse"/>).</summary>
    public static string LiveCheckFirstUseEventName(int processId) => $@"Local\Annotator.LiveCheck.{processId}.first-use";

    /// <summary>The tutorial, for UI checks and measurements.</summary>
    internal TutorialController Tutorial => _tutorial;
}
