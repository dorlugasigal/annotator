using Annotator.Workspace.FirstUse;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.FirstUse;

/// <summary>
/// The coffee reminder's cadence and rules (Mac FeedbackSupportTests, SupportReminder.swift) and the app's own settings file
/// that holds them (Mac UserDefaults).
/// </summary>
public sealed class SupportReminderTests : IDisposable
{
    private readonly string _folder = Path.Combine(AppContext.BaseDirectory, "scratch-app-defaults", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void ReminderRequiresActiveDaysAndRepeatsOnlyAfterCooldownAndContinuedUse()
    {
        // Mac testReminderRequiresActiveDaysAndRepeatsOnlyAfterCooldownAndContinuedUse, with a UTC calendar.
        AppDefaults defaults = AppDefaults.InMemory();
        var day = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var reminder = new SupportReminder(defaults, () => day, TimeZoneInfo.Utc);
        reminder.RecordUse();
        Assert.Equal(0, defaults.Integer(SupportReminder.UseDaysKey));
        reminder.RecordLaunch();
        for (int index = 0; index < 5; index++)
        {
            reminder.RecordUse();
            reminder.RecordUse();
            Assert.Equal(index + 1, defaults.Integer(SupportReminder.UseDaysKey));
            Assert.Equal(index == 4, reminder.Available);
            if (index < 4)
            {
                day = day.AddDays(1);
            }
        }
        reminder.MarkPresented();
        reminder.Snooze();
        Assert.False(reminder.Available);
        DateTimeOffset lastShown = day;
        for (int index = 0; index < 10; index++)
        {
            day = day.AddDays(1);
            reminder.RecordUse();
        }
        Assert.False(reminder.Available);
        day = lastShown.AddDays(30);
        var relaunched = new SupportReminder(defaults, () => day, TimeZoneInfo.Utc);
        relaunched.RecordLaunch();
        relaunched.RecordUse();
        Assert.True(relaunched.Available);
        relaunched.Snooze();
        day = day.AddDays(60);
        relaunched.RecordUse();
        Assert.False(relaunched.Available);
        relaunched.SetEnabled(false);
        Assert.False(relaunched.Enabled);
        var disabled = new SupportReminder(defaults, () => day, TimeZoneInfo.Utc);
        disabled.RecordLaunch();
        disabled.RecordUse();
        Assert.False(disabled.Available);
    }

    [Fact]
    public void DaysCountInTheLocalCalendarAndPresentingCountsOncePerLaunch()
    {
        AppDefaults defaults = AppDefaults.InMemory();
        TimeZoneInfo zone = TimeZoneInfo.CreateCustomTimeZone("Plus three", TimeSpan.FromHours(3), "Plus three", "Plus three");
        // 22:30 and 23:30 UTC are 01:30 and 02:30 the next day at +3: one day of use, not two.
        var day = new DateTimeOffset(2026, 10, 19, 22, 30, 0, TimeSpan.Zero);
        var reminder = new SupportReminder(defaults, () => day, zone);
        reminder.RecordLaunch();
        reminder.RecordUse();
        day = day.AddHours(1);
        reminder.RecordUse();
        Assert.Equal(1, defaults.Integer(SupportReminder.UseDaysKey));
        // 21:59 UTC is still the 20th at +3; 22:00 UTC is the 21st.
        day = new DateTimeOffset(2026, 10, 20, 20, 59, 0, TimeSpan.Zero);
        reminder.RecordUse();
        Assert.Equal(1, defaults.Integer(SupportReminder.UseDaysKey));
        day = new DateTimeOffset(2026, 10, 20, 21, 0, 0, TimeSpan.Zero);
        reminder.RecordUse();
        Assert.Equal(2, defaults.Integer(SupportReminder.UseDaysKey));

        reminder.MarkPresented();
        DateTimeOffset? shown = defaults.Date(SupportReminder.LastShownKey);
        day = day.AddDays(3);
        reminder.MarkPresented();
        Assert.Equal(shown, defaults.Date(SupportReminder.LastShownKey));
    }

    [Fact]
    public void ReminderNeverInterruptsDrawingZoomSettingsErrorsOrTheIntroduction()
    {
        // Mac testReminderNeverInterruptsDrawingZoomSettingsOrErrors and testIntroductionDoesNotOverlapSupportReminder.
        WorkspaceModel model = Create();
        model.ShowToolbar();
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, false, false), "Off: no drawing session yet.");
        model.BeginDrawing();
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        model.UseApps();
        Assert.True(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, hasSupportLink: false, false, false));
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, settingsVisible: true, false));
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, false, introductionVisible: true));
        model.ToggleMagnifier();
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, false, false), "The pointer lens is on.");
        model.StopZoom();
        Assert.True(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        model.Report("Test error");
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        model.DismissError();
        Assert.True(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        model.ReportCaptureFailure("Screen capture stopped.");
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        model.NoteCaptureFrame();
        Assert.True(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        model.HideTools();
        Assert.False(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
    }

    [Fact]
    public void AnEditInProgressOnAnyDisplayHoldsTheReminderAndTheTutorial()
    {
        // Canvases report edits only while drawing; Use apps cancels or commits them first.
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        Assert.False(SupportReminderRules.HasActiveEdit(model.Snapshot));
        DisplaySnapshot left = model.Display(Left);
        Assert.True(model.BeginEdit(Left, left.Version, EditKind.Gesture) is EditResult.Accepted);
        Assert.True(SupportReminderRules.HasActiveEdit(model.Snapshot));
        Assert.True(model.CancelEdit(Left, model.Display(Left).Version) is EditResult.Accepted);
        Assert.False(SupportReminderRules.HasActiveEdit(model.Snapshot));
        model.UseApps();
        Assert.True(SupportReminderRules.CanShow(model.Snapshot, true, false, false));
        Assert.True(TutorialRules.IsEligible(model.Snapshot));
    }

    [Fact]
    public void SettingsSurviveARestartInTheirFile()
    {
        string path = Path.Combine(_folder, "app-defaults-v1.json");
        AppDefaults first = AppDefaults.InFile(path);
        Assert.Null(first.LoadFailure);
        var when = new DateTimeOffset(2026, 10, 19, 12, 30, 15, 250, TimeSpan.FromHours(3));
        first.Set(TutorialPreferences.SuppressedKey, true);
        first.Set(SupportReminder.UseDaysKey, 4L);
        first.Set(SupportReminder.LastShownKey, when);
        AppDefaults second = AppDefaults.InFile(path);
        Assert.Null(second.LoadFailure);
        Assert.True(second.Bool(TutorialPreferences.SuppressedKey));
        Assert.Equal(4, second.Integer(SupportReminder.UseDaysKey));
        Assert.Equal(when, second.Date(SupportReminder.LastShownKey));
        Assert.False(second.Bool("missing"));
        Assert.Equal(0, second.Integer("missing"));
        Assert.Null(second.Date("missing"));
        second.Remove(SupportReminder.UseDaysKey);
        Assert.False(AppDefaults.InFile(path).Contains(SupportReminder.UseDaysKey));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ADamagedFileStartsOverAndSaysWhy()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "app-defaults-v1.json");
        File.WriteAllText(path, "{ \"introduction-suppressed\": tru");
        AppDefaults defaults = AppDefaults.InFile(path);
        Assert.NotNull(defaults.LoadFailure);
        Assert.StartsWith("Saved app settings could not be read", defaults.LoadFailure, StringComparison.Ordinal);
        Assert.False(defaults.Bool(TutorialPreferences.SuppressedKey));
        defaults.Set(TutorialPreferences.SuppressedKey, true);
        Assert.True(AppDefaults.InFile(path).Bool(TutorialPreferences.SuppressedKey));
    }

    [Fact]
    public void AFailedSaveKeepsTheValueAndReportsOncePerReason()
    {
        Directory.CreateDirectory(_folder);
        // A folder where the file should be: every save fails the same way.
        string path = Path.Combine(_folder, "blocked.json");
        Directory.CreateDirectory(path);
        AppDefaults defaults = AppDefaults.InFile(path);
        var failures = new List<string>();
        defaults.SaveFailed += failures.Add;
        defaults.Set(TutorialPreferences.SuppressedKey, true);
        defaults.Set(SupportReminder.UseDaysKey, 2L);
        Assert.True(defaults.Bool(TutorialPreferences.SuppressedKey));
        Assert.Equal(2, defaults.Integer(SupportReminder.UseDaysKey));
        Assert.Single(failures);
        Assert.StartsWith("App settings could not be saved", failures[0], StringComparison.Ordinal);
    }
}
