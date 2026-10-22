// Port of apps/macos/Tests/AnnotationCoreTests/BreakTimerTests.swift (specification 014).
using System.Text;
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class BreakTimerTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 9, 12, 0, 0, TimeSpan.Zero);

    private static ActivationShortcut Keys(string character, uint keyCode, bool option = false, bool shift = false, bool command = false) =>
        new(keyCode, character, control: true, option: option, command: command, shift: shift);

    [Fact]
    public void BreakSettingsDefaultAndDecodeMissingKeys()
    {
        var defaults = new BreakTimerSettings();
        Assert.Equal(10, defaults.Minutes);
        Assert.Equal("Coffee break", defaults.Title);
        Assert.Equal(BreakBackground.Moving, defaults.Background);
        Assert.Equal(BreakPalette.Forest, defaults.Palette);
        Assert.True(defaults.ShowEndTime && defaults.Chime && defaults.CountOver && defaults.AllDisplays && defaults.LockWhenTouched);
        var partial = AnnotatorJson.Decode<BreakTimerSettings>("""{"minutes":25,"palette":"ocean"}"""u8);
        Assert.Equal(25, partial.Minutes);
        Assert.Equal(BreakPalette.Ocean, partial.Palette);
        Assert.Equal("Coffee break", partial.Title);
        Assert.True(partial.LockWhenTouched);
        Assert.Equal(defaults, AnnotatorJson.Decode<BreakTimerSettings>("{}"u8));
    }

    [Fact]
    public void BreakSettingsRefuseOutOfRangeLengthsAndMultilineTitles()
    {
        var settings = new BreakTimerSettings();
        foreach (var minutes in new long[] { 0, -5, 181 })
        {
            settings.Minutes = minutes;
            var candidate = settings;
            AssertCanvasError(CanvasError.InvalidDocument("A break lasts 1 to 180 minutes."), () => candidate.Validate());
        }
        settings.Minutes = 180;
        settings.Validate();
        foreach (var title in new[] { "Back\nsoon", "Tab\there", new string('a', 61) })
        {
            settings.Title = title;
            var candidate = settings;
            AssertCanvasError(CanvasError.InvalidDocument("The break title can have up to 60 characters on one line."),
                () => candidate.Validate());
        }
        foreach (var title in new[] { "", "Lunch 🍕", "👩‍🏫 Class starts again", new string('a', 60) })
        {
            settings.Title = title;
            settings.Validate();
        }
    }

    [Fact]
    public void BreakTitleCleaningKeepsOneLineAndEmojiSequences()
    {
        Assert.Equal("Back at two", BreakTimerSettings.CleanTitle("Back\nat two"));
        Assert.Equal("Tabgone", BreakTimerSettings.CleanTitle("Tab\tgone"));
        Assert.Equal("👩‍🏫 Class", BreakTimerSettings.CleanTitle("👩‍🏫 Class"));
        Assert.Equal(60, SwiftText.CharacterCount(BreakTimerSettings.CleanTitle(new string('b', 80))));
    }

    [Fact]
    public void BreakSettingsAndShortcutRoundTripInProfiles()
    {
        var preferences = new WorkspacePreferences();
        var settings = new BreakTimerSettings
        {
            Minutes = 45, Title = "Lunch", Background = BreakBackground.Blur, Palette = BreakPalette.Sunset, LockWhenTouched = false,
        };
        preferences.BreakTimer = settings;
        preferences.BreakShortcut = new ActivationShortcut(11, "b", control: true, option: true);
        var restored = WorkspacePreferences.Read(preferences.Data());
        Assert.Equal(preferences, restored);
        Assert.Equal(settings, restored.BreakTimer);
    }

    [Fact]
    public void ProfilesWithoutBreakSettingsWriteNoBreakKeys()
    {
        var json = Encoding.UTF8.GetString(new WorkspacePreferences().Data());
        Assert.DoesNotContain("breakTimer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("breakShortcut", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MacProfileWithBreakSettingsReadsTheSame()
    {
        var mac = """
            {"activationShortcut":{"character":"2","command":false,"control":true,"keyCode":19,"option":false,"shift":false},
             "breakShortcut":{"character":"b","command":false,"control":true,"keyCode":11,"option":true,"shift":false},
             "breakTimer":{"allDisplays":true,"background":"blur","chime":true,"countOver":false,"lockWhenTouched":false,
                           "minutes":25,"palette":"sunset","showEndTime":true,"title":"Lunch"},
             "schemaVersion":1,"shortcutVersion":2,"smartDraw":false,"spotlightRadius":130,"toolShortcuts":{"1":"select"},"toolStyles":{}}
            """u8;
        var read = WorkspacePreferences.Read(mac);
        Assert.Equal(25, read.BreakTimer?.Minutes);
        Assert.Equal("Lunch", read.BreakTimer?.Title);
        Assert.Equal(BreakBackground.Blur, read.BreakTimer?.Background);
        Assert.False(read.BreakTimer?.CountOver);
        Assert.Equal("⌃⌥B", read.BreakShortcut?.Label);
        Assert.Equal(read, WorkspacePreferences.Read(read.Data()));
    }

    [Fact]
    public void InvalidBreakSettingsRejectTheProfile()
    {
        var preferences = new WorkspacePreferences { BreakTimer = new BreakTimerSettings { Minutes = 500 } };
        var data = AnnotatorJson.Encode(preferences);
        AssertCanvasError(CanvasError.InvalidDocument("A break lasts 1 to 180 minutes."), () => WorkspacePreferences.Read(data));
    }

    [Fact]
    public void BreakShortcutMustDifferFromOtherGlobalShortcuts()
    {
        var preferences = new WorkspacePreferences { BreakShortcut = ActivationShortcut.LiveZoom };
        var clash = preferences;
        AssertCanvasError(CanvasError.InvalidDocument("Break timer needs its own shortcut. Choose keys that no other global shortcut uses."),
            () => clash.Validate());
        preferences.BreakShortcut = new ActivationShortcut(0, "a");
        var unmodified = preferences;
        Assert.Throws<CanvasError>(() => unmodified.Validate());
        preferences.BreakShortcut = null;
        preferences.Validate();
        Assert.Equal("⌃3", ActivationShortcut.BreakTimer.Label);
    }

    [Fact]
    public void OlderProfileUsingControlThreeMovesTheBreakTimerToAFreeKey()
    {
        var preferences = new WorkspacePreferences { ZoomShortcut = ActivationShortcut.BreakTimer };
        var first = WorkspacePreferences.Read(AnnotatorJson.Encode(preferences));
        Assert.Equal(ActivationShortcut.BreakTimer, first.ZoomShortcut);
        Assert.Equal("⌃⌥3", first.BreakShortcut?.Label);
        preferences.HideShortcut = Keys("3", 20, option: true);
        preferences.ResumeShortcut = Keys("3", 20, shift: true);
        preferences.ActivationShortcut = Keys("3", 20, command: true);
        var crowded = WorkspacePreferences.Read(AnnotatorJson.Encode(preferences));
        Assert.Equal("⌃⌥⇧3", crowded.BreakShortcut?.Label);
        Assert.Null(WorkspacePreferences.Read(new WorkspacePreferences().Data()).BreakShortcut);
    }

    [Fact]
    public void ToolLibraryRuleStillAppliesToTheBreakShortcut()
    {
        var preferences = new WorkspacePreferences { BreakShortcut = new ActivationShortcut(37, "l", command: true) };
        preferences.Validate();
    }

    [Fact]
    public void CountdownRoundsUpAndShowsHoursOnlyWhenNeeded()
    {
        var countdown = new BreakCountdown(10, Start);
        Assert.Equal("10:00", countdown.Label(Start, countOver: true));
        Assert.Equal("10:00", countdown.Label(Start.AddSeconds(0.4), countOver: true));
        Assert.Equal("9:59", countdown.Label(Start.AddSeconds(1), countOver: true));
        Assert.Equal("0:01", countdown.Label(Start.AddSeconds(599.5), countOver: true));
        Assert.Equal("0:00", countdown.Label(Start.AddSeconds(600), countOver: true));
        Assert.Equal("1:05:00", new BreakCountdown(65, Start).Label(Start, countOver: true));
        Assert.Equal("59:59", BreakCountdown.Clock(3_599));
        Assert.Equal("0:00", BreakCountdown.Clock(-4));
    }

    [Fact]
    public void CountdownCountsTimeOverOnlyWhenAsked()
    {
        var countdown = new BreakCountdown(1, Start);
        var later = Start.AddSeconds(60 + 83.7);
        Assert.True(countdown.IsTimeUp(later));
        Assert.Equal("+1:23", countdown.Label(later, countOver: true));
        Assert.Equal("0:00", countdown.Label(later, countOver: false));
        Assert.Equal("0:00", countdown.Label(Start.AddSeconds(60.5), countOver: true));
        Assert.Equal(0, countdown.Fraction(later));
    }

    [Fact]
    public void CountdownFollowsTheWallClockAcrossSleep()
    {
        var countdown = new BreakCountdown(15, Start);
        var afterSleep = Start.AddSeconds(9 * 60 + 30);
        Assert.Equal("5:30", countdown.Label(afterSleep, countOver: true));
        Assert.Equal(Start.AddSeconds(15 * 60), countdown.EndDate(afterSleep));
        Assert.True(Math.Abs(countdown.Fraction(afterSleep) - 330.0 / 900.0) < 1e-12);
    }

    [Fact]
    public void PauseHoldsTheTimeAndMovesTheEndLater()
    {
        var countdown = new BreakCountdown(10, Start);
        var pausedAt = Start.AddSeconds(120);
        countdown.Pause(pausedAt);
        Assert.True(countdown.IsPaused);
        var muchLater = pausedAt.AddSeconds(300);
        Assert.Equal("8:00", countdown.Label(muchLater, countOver: true));
        Assert.Equal(muchLater.AddSeconds(480), countdown.EndDate(muchLater));
        countdown.Resume(muchLater);
        Assert.False(countdown.IsPaused);
        Assert.Equal("7:00", countdown.Label(muchLater.AddSeconds(60), countOver: true));
        var finished = new BreakCountdown(1, Start);
        finished.Pause(Start.AddSeconds(70));
        Assert.False(finished.IsPaused);
    }

    [Fact]
    public void AddingAndRemovingMinutesStaysWithinLimits()
    {
        var countdown = new BreakCountdown(10, Start);
        var now = Start.AddSeconds(480);
        countdown.Adjust(1, now);
        Assert.Equal("3:00", countdown.Label(now, countOver: true));
        Assert.Equal(660, countdown.Total);
        countdown.Adjust(-1, now);
        Assert.Equal("2:00", countdown.Label(now, countOver: true));
        Assert.Equal(660, countdown.Total);
        countdown.Adjust(-5, now);
        Assert.True(countdown.IsTimeUp(now));
        Assert.Equal("0:00", countdown.Label(now, countOver: true));
        countdown.Adjust(1, now.AddSeconds(200));
        Assert.Equal("1:00", countdown.Label(now.AddSeconds(200), countOver: true));
        Assert.Equal(60, countdown.Total);
        Assert.Equal(1, countdown.Fraction(now.AddSeconds(200)));
        var longBreak = new BreakCountdown(179, Start);
        longBreak.Adjust(5, Start);
        Assert.Equal(BreakCountdown.MaximumSeconds, longBreak.Remaining(Start));
        Assert.Equal(BreakCountdown.MaximumSeconds, longBreak.Total);
    }

    [Fact]
    public void RemovingTimeAfterTimeUpKeepsCountingOver()
    {
        var countdown = new BreakCountdown(1, Start);
        var late = Start.AddSeconds(60 + 312);
        countdown.Adjust(-1, late);
        Assert.Equal("+5:12", countdown.Label(late, countOver: true));
        countdown.Adjust(2, late);
        Assert.Equal("2:00", countdown.Label(late, countOver: true));
    }

    [Fact]
    public void AdjustingWhilePausedKeepsThePause()
    {
        var countdown = new BreakCountdown(5, Start);
        countdown.Pause(Start.AddSeconds(30));
        countdown.Adjust(2, Start.AddSeconds(90));
        Assert.True(countdown.IsPaused);
        Assert.Equal("6:30", countdown.Label(Start.AddSeconds(400), countOver: true));
        countdown.Adjust(-10, Start.AddSeconds(400));
        Assert.False(countdown.IsPaused);
        Assert.True(countdown.IsTimeUp(Start.AddSeconds(400)));
    }

    [Fact]
    public void CountdownRefusesInvalidLengths()
    {
        foreach (var minutes in new long[] { 0, 181 })
        {
            Assert.Throws<CanvasError>(() => new BreakCountdown(minutes, Start));
        }
    }

    [Fact]
    public void NextLabelChangeLinesUpWithTheDisplayedSecond()
    {
        var countdown = new BreakCountdown(1, Start);
        Assert.True(Math.Abs(countdown.SecondsUntilLabelChanges(Start.AddSeconds(0.25)) - 0.75) < 1e-6);
        Assert.Equal(1, countdown.SecondsUntilLabelChanges(Start));
        Assert.True(Math.Abs(countdown.SecondsUntilLabelChanges(Start.AddSeconds(61.4)) - 0.6) < 1e-6);
    }

    [Fact]
    public void SpokenTimeUsesPlainWords()
    {
        Assert.Equal("0 seconds", BreakCountdown.Spoken(0));
        Assert.Equal("1 second", BreakCountdown.Spoken(1));
        Assert.Equal("10 minutes", BreakCountdown.Spoken(600));
        Assert.Equal("1 hour 5 minutes 5 seconds", BreakCountdown.Spoken(3_905));
    }
}
