using Annotator.Core;
using Annotator.Workspace.FirstUse;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.FirstUse;

/// <summary>
/// The tour's navigation, story timings, launch rules and words (Mac IntroductionMotionTests and FirstUseTests, the parts
/// that need no window).
/// </summary>
public sealed class IntroductionTests
{
    [Fact]
    public void PagesHaveTheMacTitlesDurationsAndDescriptions()
    {
        Assert.Equal(["Make a point", "Make it yours", "Lead the way", "A little setup"], IntroductionPageExtensions.All.Select(page => page.Title));
        Assert.Equal([4.8, 3.6, 4.2, 1.1], IntroductionPageExtensions.All.Select(page => page.StoryDuration));
        Assert.Equal(["Step 1 of 4", "Step 2 of 4", "Step 3 of 4", "Step 4 of 4"], IntroductionPageExtensions.All.Select(page => page.StepValue));
        Assert.StartsWith("A pen circles a bar on a chart", IntroductionPage.Draw.IllustrationDescription, StringComparison.Ordinal);
        Assert.Contains("\"Make your point\"", IntroductionPage.Color.IllustrationDescription, StringComparison.Ordinal);
        Assert.Contains("zoom lens", IntroductionPage.Focus.IllustrationDescription, StringComparison.Ordinal);
        Assert.Null(IntroductionPage.Setup.IllustrationDescription);
    }

    [Fact]
    public void ArrowsAndButtonsMoveOnePageInTheirDirectionAndStopAtTheEnds()
    {
        // Mac testArrowKeysAndButtonsMoveOnePageInTheirDirection, without the panel's key events.
        var navigator = new IntroductionNavigator();
        int changes = 0;
        navigator.Changed += () => changes++;
        Assert.False(navigator.Move(IntroductionDirection.Backward));
        Assert.Equal(IntroductionPage.Draw, navigator.Page);
        Assert.Equal(0, changes);
        Assert.True(navigator.Move(IntroductionDirection.Forward));
        Assert.Equal(IntroductionPage.Color, navigator.Page);
        Assert.Equal(IntroductionPage.Draw, navigator.PreviousPage);
        Assert.Equal(IntroductionDirection.Forward, navigator.Direction);
        navigator.Move(IntroductionDirection.Forward);
        navigator.Move(IntroductionDirection.Forward);
        Assert.Equal(IntroductionPage.Setup, navigator.Page);
        Assert.False(navigator.CanGoForward);
        Assert.False(navigator.Move(IntroductionDirection.Forward));
        Assert.Equal(IntroductionPage.Setup, navigator.Page);
        navigator.Move(IntroductionDirection.Backward);
        Assert.Equal(IntroductionPage.Focus, navigator.Page);
        Assert.Equal(IntroductionDirection.Backward, navigator.Direction);
        navigator.Select(IntroductionPage.Draw);
        Assert.Equal(IntroductionDirection.Backward, navigator.Direction);
        Assert.False(navigator.CanGoBack);
        navigator.Select(IntroductionPage.Setup);
        Assert.Equal(IntroductionDirection.Forward, navigator.Direction);
        int before = changes;
        navigator.Select(IntroductionPage.Setup);
        Assert.Equal(before, changes);
    }

    [Fact]
    public void ReplayPlaysOnlyWhileShownAndPresentingRestartsTheStory()
    {
        var navigator = new IntroductionNavigator();
        int token = navigator.PlaybackToken;
        navigator.Replay();
        Assert.Equal(token, navigator.PlaybackToken);
        navigator.SetPresented(true);
        Assert.True(navigator.IsPresented);
        Assert.Equal(token + 1, navigator.PlaybackToken);
        navigator.Replay();
        Assert.Equal(token + 2, navigator.PlaybackToken);
        navigator.SetPresented(true);
        Assert.Equal(token + 2, navigator.PlaybackToken);
        navigator.SetPresented(false);
        Assert.Equal(token + 3, navigator.PlaybackToken);
        for (int index = 0; index < 30; index++)
        {
            navigator.NotePlayback(new IntroductionPlayback(IntroductionPage.Draw, index % 2 == 0));
            navigator.NoteFinished(IntroductionPage.Color);
        }
        Assert.Equal(24, navigator.Playbacks.Count);
        Assert.Equal(24, navigator.FinishedPlaybacks.Count);
    }

    [Fact]
    public void StoriesStartAtTheirEndWithReduceMotionOrWhileHidden()
    {
        // Mac testReduceMotionShowsEveryIllustrationInItsFinalStillState.
        Assert.Equal(1, IntroductionStory.InitialProgress(reduceMotion: true, presented: true, review: null));
        Assert.Equal(0, IntroductionStory.InitialProgress(reduceMotion: false, presented: true, review: null));
        Assert.Equal(1, IntroductionStory.InitialProgress(reduceMotion: false, presented: false, review: null));
        Assert.Equal(0.25, IntroductionStory.InitialProgress(reduceMotion: true, presented: true, review: 0.25));
        Assert.Equal(1, IntroductionStory.InitialProgress(reduceMotion: false, presented: true, review: 3));
    }

    [Fact]
    public void TheStepCapsuleAndTheRowsFollowTheMacTimings()
    {
        // Mac testStepIndicatorCapsuleCoversOnlyTheActiveStepWhenSettled.
        (double start, double width) = IntroductionMotion.Capsule(2, 2, 4, 400);
        Assert.Equal(2 * (94 + 8), start, 2);
        Assert.Equal(94, width, 2);
        Assert.Equal(94 * 2 + 8, IntroductionMotion.Capsule(1, 2, 4, 400).Width, 2);
        Assert.Equal(94 * 2 + 8, IntroductionMotion.Capsule(2, 1, 4, 400).Width, 2);
        Assert.Equal(0, IntroductionStory.ShortcutFocus(1).Opacity, 3);
        Assert.Equal(0, IntroductionStory.ShortcutFocus(0.2).Position, 3);
        Assert.Equal(1, IntroductionStory.ShortcutFocus(0.55).Position, 3);
        Assert.Equal(2, IntroductionStory.ShortcutFocus(0.85).Position, 3);
        Assert.Equal(1, IntroductionStory.ShortcutFocus(0.5).Opacity, 3);
        for (int index = 0; index < 2; index++)
        {
            (double opacity, double offset, double scale) = IntroductionStory.CardArrival(index, 1);
            Assert.Equal(1, opacity, 3);
            Assert.Equal(0, offset, 3);
            Assert.Equal(1, scale, 3);
            Assert.True(IntroductionStory.CardArrival(index, 0).Opacity > 0, "Cards stay readable to screen readers before they settle.");
        }
        Assert.Equal(1, IntroductionStory.ShortcutPress(0, 0.045), 3);
        Assert.Equal(0, IntroductionStory.ShortcutPress(0, 0.5), 3);
        Assert.Equal(1, IntroductionStory.ShortcutPress(2, 0.86), 3);
        Assert.Equal(1, IntroductionStory.CheckDraw(1, 1), 6);
        Assert.Equal(0, IntroductionStory.CheckDraw(1, 0.6), 6);
        Assert.Equal((0.3 + 1.1 * 0.65) * 2, IntroductionStory.CheckDelay(1, 2), 9);
        Assert.Equal(0.35, IntroductionStory.SharingNoteOpacity(0), 9);
        Assert.Equal(1, IntroductionStory.SharingNoteOpacity(1), 9);
    }

    [Fact]
    public void EaseHelpersMatchTheMac()
    {
        Assert.Equal(0.5, IntroductionEase.Smooth(0.5), 12);
        Assert.Equal(0.875, IntroductionEase.EaseOut(0.5), 12);
        Assert.Equal(0.5, IntroductionEase.EaseInOut(0.5), 12);
        Assert.Equal(1, IntroductionEase.Settle(1), 12);
        Assert.True(IntroductionEase.Settle(0.6) > 1 && IntroductionEase.Settle(0.6) < 1.03, "The settle overshoots by under 3 percent.");
        Assert.Equal(0, IntroductionEase.Pulse(0.3, 0.3, 0.1), 12);
        Assert.Equal(1, IntroductionEase.Pulse(0.35, 0.3, 0.1), 12);
        Assert.Equal(0, IntroductionEase.Pulse(0.4, 0.3, 0.1), 12);
        Assert.Equal(0.25, IntroductionEase.Segment(0.25, 0, 1), 12);
        Assert.Equal(1, IntroductionEase.Segment(2, 0, 1), 12);
    }

    [Fact]
    public void BusyStatesPreventPresentationWithoutChangingInteraction()
    {
        // Mac testBusyStatesPreventPresentationWithoutChangingInteraction and testLoginLaunchDoesNotShow.
        WorkspaceModel model = Create();
        model.ShowToolbar();
        Assert.True(IntroductionRules.IsEligible(model.Snapshot));
        model.ToggleMagnifier();
        Assert.True(model.Snapshot.ZoomActive);
        Assert.False(IntroductionRules.IsEligible(model.Snapshot));
        model.StopZoom();
        Assert.True(IntroductionRules.IsEligible(model.Snapshot));
        model.Report("Test error");
        Assert.False(IntroductionRules.IsEligible(model.Snapshot));
        model.DismissError();
        model.ReportCaptureFailure("Screen capture stopped.");
        Assert.False(IntroductionRules.IsEligible(model.Snapshot));
        model.NoteCaptureFrame();
        Assert.True(IntroductionRules.IsEligible(model.Snapshot));
        model.BeginDrawing();
        Assert.False(IntroductionRules.IsEligible(model.Snapshot));
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        model.UseApps();
        Assert.True(IntroductionRules.IsEligible(model.Snapshot));
        Assert.True(IntroductionRules.ShowsAutomatically(startupLaunch: false, suppressed: false, eligible: true));
        Assert.False(IntroductionRules.ShowsAutomatically(startupLaunch: true, suppressed: false, eligible: true));
        Assert.False(IntroductionRules.ShowsAutomatically(startupLaunch: false, suppressed: true, eligible: true));
        Assert.False(IntroductionRules.ShowsAutomatically(startupLaunch: false, suppressed: false, eligible: false));
    }

    [Fact]
    public void SuppressionPersistsAndOnlyTheSwitchChangesIt()
    {
        // Mac testSuppressionPersistsButManualReopeningStillWorks (the stored part).
        AppDefaults defaults = AppDefaults.InMemory();
        var preferences = new IntroductionPreferences(defaults);
        Assert.False(preferences.Suppressed);
        int changes = 0;
        preferences.Changed += () => changes++;
        preferences.SetSuppressed(true);
        Assert.True(new IntroductionPreferences(defaults).Suppressed);
        Assert.True(defaults.Bool("introduction-suppressed"));
        preferences.SetSuppressed(false);
        Assert.False(new IntroductionPreferences(defaults).Suppressed);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void TheFirstPageUsesConfiguredShortcutsAndStaysUnder120Words()
    {
        // Mac testFirstPageUsesConfiguredShortcutsAndStaysShortWhileAnimating: the visible words of page 1 and the footer.
        string[] visible =
        [
            IntroductionText.DrawTitle, IntroductionText.DrawLead, IntroductionText.DrawRow, IntroductionText.DrawRowDetail,
            IntroductionText.UseAppsRow, IntroductionText.UseAppsRowDetail, IntroductionText.ShowHideRow, IntroductionText.ShowHideRowDetail,
            IntroductionText.EscapeLine, IntroductionText.SharingTitle, IntroductionText.UsageCounts, IntroductionText.UsageCountsNote,
            IntroductionText.ShowOnLaunch, IntroductionText.Next, IntroductionText.GotIt, IntroductionText.FindAgain,
            "Ctrl Shift 2", IntroductionText.EscapeKeycap, "Ctrl 2",
        ];
        int words = string.Join(' ', visible).Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(words <= 120, $"Page 1 shows {words} words.");
        Assert.Contains("Esc", IntroductionText.EscapeLine, StringComparison.Ordinal);
        Assert.Contains("Covers hide too", IntroductionText.ShowHideRowDetail, StringComparison.Ordinal);
        Assert.Contains("App or window sharing may leave out covers", IntroductionText.SharingBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Option", IntroductionText.SharingBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Command", string.Join(' ', visible), StringComparison.Ordinal);
    }

    [Fact]
    public void TheShortcutsCardNamesTheKeysThatTakeOverTabSwitching()
    {
        // DV-10: Ctrl with a digit replaces browsers' tab switching; WIN-INP-05: a taken shortcut says why.
        WorkspaceModel model = Create();
        Assert.Equal("While Annotator runs, its shortcuts replace the same keys in other apps. Change them in Settings > Toolbar & Shortcuts.",
            IntroductionText.ShortcutsDetail(model.Snapshot));
        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ShowHide, "Ctrl+2", GlobalShortcutStatus.Registered, null));
        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ResumeDrawing, "Ctrl+Shift+2", GlobalShortcutStatus.Registered, null));
        Assert.Equal("Ctrl+2 replaces tab switching in browsers while Annotator runs. Change it in Settings > Toolbar & Shortcuts.",
            IntroductionText.ShortcutsDetail(model.Snapshot));
        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.LiveZoom, "Ctrl+1", GlobalShortcutStatus.Registered, null));
        Assert.Equal("Ctrl+1 and Ctrl+2 replace tab switching in browsers while Annotator runs. Change them in Settings > Toolbar & Shortcuts.",
            IntroductionText.ShortcutsDetail(model.Snapshot));
        Assert.Empty(IntroductionText.UnavailableShortcuts(model.Snapshot));

        var taken = new GlobalShortcutState(GlobalShortcutAction.ShowHide, "Ctrl+2", GlobalShortcutStatus.AlreadyUsed, "In use.");
        model.SetGlobalShortcutState(taken);
        Assert.Equal([taken], IntroductionText.UnavailableShortcuts(model.Snapshot));
        Assert.Equal("Ctrl+2 is used by another app, so Show / hide has no shortcut.", IntroductionText.ShortcutUnavailable(taken));
        Assert.Equal("Windows doesn't allow Ctrl+Alt+2 as a shortcut, so Hide only has no shortcut.", IntroductionText.ShortcutUnavailable(
            new GlobalShortcutState(GlobalShortcutAction.HideOnly, "Ctrl+Alt+2", GlobalShortcutStatus.Refused, null)));
        Assert.Equal("Ctrl+1 replaces tab switching in browsers while Annotator runs. Change it in Settings > Toolbar & Shortcuts.",
            IntroductionText.ShortcutsDetail(model.Snapshot));
    }
}
