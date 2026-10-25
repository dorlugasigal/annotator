using Annotator.Core;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Sharing;
using Annotator.Workspace.Updates;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Updates;

/// <summary>
/// Check for updates... on Windows (WIN-SYS-03, the Mac's AppUpdateController): the three channels, the typed phases and
/// their words, the question before an install that could close Annotator, and the bounded waits. The Store is
/// <see cref="FixedStoreUpdates"/>, so nothing here calls Windows.
/// </summary>
public sealed class AppUpdatesTests
{
    private readonly List<string> _notes = [];
    private readonly List<UpdateQuestion> _asked = [];
    private readonly List<(TimeSpan Timeout, CancellationToken Token, TaskCompletionSource Fire)> _timers = [];
    private UpdateQuestion? _question;
    private bool _answer;

    private AppUpdates Make(IStoreUpdates store, Func<DateTimeOffset>? now = null) => new(store, () => _question, question =>
    {
        _asked.Add(question);
        return Task.FromResult(_answer);
    }, _notes.Add, Delay, now: now);

    [Fact]
    public async Task OpeningTheMenuAsksTheStoreQuietlyAtMostEverySixHours()
    {
        DateTimeOffset now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
        var store = new FixedStoreUpdates(UpdateChannel.Store);
        AppUpdates updates = Make(store, () => now);
        var phases = new List<UpdatePhase>();
        updates.Changed += () => phases.Add(updates.Phase);

        Assert.False(await updates.CheckIfDueAsync(), "Only the menu asks the Store on its own.");
        Assert.Equal(0, store.Checks);

        Assert.False(await updates.CheckIfDueAsync(menuOpened: true));
        Assert.Equal(1, store.Checks);
        Assert.Equal(new[] { UpdatePhase.Checking, UpdatePhase.Ready }, phases);
        Assert.False(updates.UpdateWaiting);

        now = now.AddHours(5);
        store.Answer = StoreUpdateCheck.Available;
        await updates.CheckIfDueAsync(menuOpened: true);
        Assert.Equal(1, store.Checks);

        now = now.AddHours(1);
        await updates.CheckIfDueAsync(menuOpened: true);
        Assert.Equal(2, store.Checks);
        Assert.Equal(UpdatePhase.Available, updates.Phase);
        Assert.True(updates.UpdateWaiting);
        Assert.Equal(UpdateText.Available, updates.Status);
        Assert.Equal(0, store.Installs);
        Assert.Empty(_asked);

        now = now.AddDays(1);
        await updates.CheckIfDueAsync(menuOpened: true);
        Assert.Equal(2, store.Checks);
        Assert.True(updates.UpdateWaiting, "The update stays until it is installed.");
    }

    [Fact]
    public async Task AQuietStoreCheckThatFailsStaysQuietAndAManualCheckCountsAsTheLastCheck()
    {
        DateTimeOffset now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Failure = new StoreUpdateFailure("0x80072EE7") };
        AppUpdates updates = Make(store, () => now);

        await updates.CheckIfDueAsync(menuOpened: true);
        Assert.Equal(UpdatePhase.Ready, updates.Phase);
        Assert.Contains("Update check: 0x80072EE7", _notes);
        await updates.CheckIfDueAsync(menuOpened: true);
        Assert.Equal(2, store.Checks);

        store.Failure = null;
        await updates.CheckAsync();
        Assert.Equal(UpdatePhase.UpToDate, updates.Phase);
        await updates.CheckIfDueAsync(menuOpened: true);
        Assert.Equal(3, store.Checks);
    }

    [Fact]
    public async Task TheMenuDoesNotAskTheStoreWhenTheOrganizationTurnedUpdatesOff()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store, automaticUpdates: false);
        AppUpdates updates = Make(store);
        await updates.CheckIfDueAsync(menuOpened: true);
        Assert.Equal(0, store.Checks);
    }

    private Task Delay(TimeSpan timeout, CancellationToken token)
    {
        var fire = new TaskCompletionSource();
        _timers.Add((timeout, token, fire));
        return fire.Task;
    }

    [Fact]
    public async Task DevelopmentBuildsKeepTheMacSentenceAndNeverAskTheStore()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Development);
        AppUpdates updates = Make(store);

        Assert.Equal(UpdatePhase.Unavailable, updates.Phase);
        Assert.Equal("Updates aren't set up in this build. Find new versions in Settings > About.", updates.Status);
        Assert.Equal(SettingsCopy.UpdatesUnavailable, updates.Status);
        Assert.False(updates.IsConfigured);
        Assert.False(updates.CanCheck);
        Assert.False(updates.AutomaticChecks);

        await updates.CheckAsync();
        Assert.Equal(0, store.Checks);
        Assert.Equal(UpdatePhase.Unavailable, updates.Phase);
    }

    [Fact]
    public async Task ThePortableCopySaysHowToUpdateWithWinget()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Portable);
        AppUpdates updates = Make(store);

        Assert.Equal(UpdatePhase.Unavailable, updates.Phase);
        Assert.Equal(UpdateText.Portable, updates.Status);
        Assert.Contains("winget upgrade DorLugasiGal.Annotator", updates.Status, StringComparison.Ordinal);
        Assert.False(updates.IsConfigured);
        Assert.False(updates.CanCheck);
        await updates.CheckAsync();
        Assert.Equal(0, store.Checks);
    }

    [Fact]
    public async Task APackageNotFromTheStoreSaysWhyItCannotCheck()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Sideloaded);
        AppUpdates updates = Make(store);

        Assert.Equal(UpdatePhase.Unavailable, updates.Phase);
        Assert.Equal(UpdateText.NotFromStore, updates.Status);
        Assert.False(updates.IsConfigured);
        Assert.False(updates.CanCheck);
        Assert.False(updates.AutomaticChecks);
        await updates.CheckAsync();
        Assert.Equal(0, store.Checks);
    }

    [Fact]
    public void AStoreCopyCanCheckAndFollowsTheStoresAutomaticUpdates()
    {
        AppUpdates automatic = Make(new FixedStoreUpdates(UpdateChannel.Store));
        Assert.Equal(UpdatePhase.Ready, automatic.Phase);
        Assert.True(automatic.IsConfigured);
        Assert.True(automatic.CanCheck);
        Assert.True(automatic.AutomaticChecks);
        Assert.Equal(UpdateText.Ready, automatic.Status);

        AppUpdates manual = Make(new FixedStoreUpdates(UpdateChannel.Store, automaticUpdates: false));
        Assert.True(manual.CanCheck);
        Assert.False(manual.AutomaticChecks);
        Assert.Equal(UpdateText.ReadyManual, manual.Status);
    }

    [Fact]
    public async Task UpToDateChecksOnceAndSaysSo()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.UpToDate };
        AppUpdates updates = Make(store);
        var phases = new List<UpdatePhase>();
        updates.Changed += () => phases.Add(updates.Phase);

        await updates.CheckAsync();

        Assert.Equal([UpdatePhase.Checking, UpdatePhase.UpToDate], phases);
        Assert.Equal(UpdateText.UpToDate, updates.Status);
        Assert.Equal(1, store.Checks);
        Assert.Equal(0, store.Installs);
        Assert.True(updates.CanCheck);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task WithNothingToLoseTheStoreInstallsWithoutAnnotatorsQuestion()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.Available };
        AppUpdates updates = Make(store);
        var phases = new List<UpdatePhase>();
        updates.Changed += () => phases.Add(updates.Phase);

        await updates.CheckAsync();

        Assert.Equal([UpdatePhase.Checking, UpdatePhase.Installing, UpdatePhase.Installed], phases);
        Assert.Empty(_asked);
        Assert.Equal(1, store.Installs);
        Assert.Equal(UpdateText.Installed, updates.Status);
    }

    [Fact]
    public async Task NotNowKeepsTheUpdateWaitingAndInstallsNothing()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.Available };
        AppUpdates updates = Make(store);
        _question = new UpdateQuestion(LosesDrawings: true, StopsSharing: false);
        _answer = false;

        await updates.CheckAsync();

        Assert.Equal([_question], _asked);
        Assert.Equal(0, store.Installs);
        Assert.Equal(UpdatePhase.Available, updates.Phase);
        Assert.Equal(UpdateText.Available, updates.Status);
        Assert.True(updates.CanCheck);
    }

    [Fact]
    public async Task InstallUpdateAfterTheQuestionLetsTheStoreInstall()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.Available };
        AppUpdates updates = Make(store);
        _question = new UpdateQuestion(LosesDrawings: true, StopsSharing: true);
        _answer = true;

        await updates.CheckAsync();

        Assert.Single(_asked);
        Assert.Equal(1, store.Installs);
        Assert.Equal(UpdatePhase.Installed, updates.Phase);
    }

    [Theory]
    [InlineData(StoreInstallOutcome.Cancelled, UpdateText.Cancelled)]
    [InlineData(StoreInstallOutcome.LowBattery, UpdateText.LowBattery)]
    [InlineData(StoreInstallOutcome.NeedsWiFi, UpdateText.NeedsWiFi)]
    [InlineData(StoreInstallOutcome.Failed, UpdateText.InstallFailed)]
    public async Task AnInstallThatDidNotFinishSaysWhy(StoreInstallOutcome outcome, string status)
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.Available, Outcome = outcome };
        AppUpdates updates = Make(store);

        await updates.CheckAsync();

        Assert.Equal(UpdatePhase.NotInstalled, updates.Phase);
        Assert.Equal(outcome, updates.Outcome);
        Assert.Equal(status, updates.Status);
        Assert.True(updates.CanCheck);
    }

    [Fact]
    public async Task AFailedCheckIsATypedStateAndItsDetailsGoToTheLog()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Failure = new StoreUpdateFailure("0x80072EE7 no connection") };
        AppUpdates updates = Make(store);

        await updates.CheckAsync();

        Assert.Equal(UpdatePhase.CheckFailed, updates.Phase);
        Assert.Equal(UpdateText.CheckFailed, updates.Status);
        Assert.Equal(["Update check: 0x80072EE7 no connection"], _notes);
        Assert.True(updates.CanCheck);
    }

    [Fact]
    public async Task AFailedInstallRequestEndsAsNotInstalled()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.Available };
        AppUpdates updates = Make(store);
        updates.Changed += () =>
        {
            if (updates.Phase == UpdatePhase.Installing)
            {
                store.Failure = new StoreUpdateFailure("0x80070005");
            }
        };

        await updates.CheckAsync();

        Assert.Equal(UpdatePhase.NotInstalled, updates.Phase);
        Assert.Equal(StoreInstallOutcome.Failed, updates.Outcome);
        Assert.Equal(UpdateText.InstallFailed, updates.Status);
        Assert.Equal(["Update install: 0x80070005"], _notes);
    }

    [Fact]
    public async Task OneCheckAtATime()
    {
        var gate = new TaskCompletionSource();
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Gate = gate.Task };
        AppUpdates updates = Make(store);

        Task first = updates.CheckAsync();
        Assert.Equal(UpdatePhase.Checking, updates.Phase);
        Assert.Equal(UpdateText.Checking, updates.Status);
        Assert.False(updates.CanCheck);
        await updates.CheckAsync();
        Assert.Equal(1, store.Checks);

        gate.SetResult();
        await first;
        Assert.Equal(UpdatePhase.UpToDate, updates.Phase);
        Assert.True(updates.CanCheck);
    }

    [Fact]
    public async Task ACheckWithoutAnAnswerStopsWaitingAfterAMinute()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Gate = new TaskCompletionSource().Task };
        AppUpdates updates = Make(store);

        Task check = updates.CheckAsync();
        (TimeSpan timeout, _, TaskCompletionSource fire) = Assert.Single(_timers);
        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
        Assert.Equal(AppUpdates.CheckTimeout, timeout);
        fire.SetResult();
        await check;

        Assert.Equal(UpdatePhase.CheckFailed, updates.Phase);
        Assert.Equal(["Update check: the Microsoft Store did not answer within 60 seconds."], _notes);
        Assert.True(updates.CanCheck);
    }

    [Fact]
    public async Task AnInstallWithoutAnEndStopsWaitingAfterHalfAnHour()
    {
        var store = new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.Available };
        AppUpdates updates = Make(store);
        updates.Changed += () =>
        {
            if (updates.Phase == UpdatePhase.Installing)
            {
                store.Gate = new TaskCompletionSource().Task;
            }
        };

        Task check = updates.CheckAsync();
        Assert.Equal(UpdatePhase.Installing, updates.Phase);
        Assert.Equal(UpdateText.Installing, updates.Status);
        Assert.False(updates.CanCheck);
        Assert.Equal(2, _timers.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), _timers[1].Timeout);
        _timers[1].Fire.SetResult();
        await check;

        Assert.Equal(UpdatePhase.NotInstalled, updates.Phase);
        Assert.Equal(StoreInstallOutcome.Failed, updates.Outcome);
        Assert.Equal(["Update install: the Microsoft Store did not finish within 30 minutes."], _notes);
    }

    [Fact]
    public async Task TheTimerStopsWhenTheStoreAnswers()
    {
        AppUpdates updates = Make(new FixedStoreUpdates(UpdateChannel.Store));

        await updates.CheckAsync();

        (_, CancellationToken token, _) = Assert.Single(_timers);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void ClosingLosesNothingWithoutDrawingsOrSharing()
    {
        Assert.Null(UpdateQuestion.For(Drawing().Snapshot));
        Assert.Null(UpdateQuestion.For(Create().Snapshot));
    }

    [Fact]
    public void DrawingsAskFirstEvenWhileHidden()
    {
        WorkspaceModel model = Drawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Rectangle, new Rect(80, 80, 120, 100))));

        UpdateQuestion? question = UpdateQuestion.For(model.Snapshot);
        Assert.Equal(new UpdateQuestion(LosesDrawings: true, StopsSharing: false), question);
        Assert.Equal(UpdateText.QuestionWithDrawings, question!.Message);

        model.UseApps();
        model.HideTools();
        Assert.True(model.Snapshot.WorkspaceHidden);
        Assert.Equal(question, UpdateQuestion.For(model.Snapshot));
    }

    [Fact]
    public void RegionSharingAsksFirst()
    {
        (WorkspaceModel model, RegionSharing sharing) = Sharing();
        sharing.Start();
        Assert.True(model.Snapshot.RegionSharing.IsCapturing);

        UpdateQuestion? question = UpdateQuestion.For(model.Snapshot);
        Assert.Equal(new UpdateQuestion(LosesDrawings: false, StopsSharing: true), question);
        Assert.Equal(UpdateText.QuestionWithSharing, question!.Message);

        (WorkspaceModel drawn, RegionSharing both) = Sharing();
        AssertAccepted(drawn.Add(Primary, new Element(Tool.Ellipse, new Rect(80, 80, 90, 60))));
        both.Start();
        question = UpdateQuestion.For(drawn.Snapshot);
        Assert.Equal(new UpdateQuestion(LosesDrawings: true, StopsSharing: true), question);
        Assert.Equal(UpdateText.QuestionWithDrawingsAndSharing, question!.Message);

        both.Stop();
        Assert.Equal(new UpdateQuestion(LosesDrawings: true, StopsSharing: false), UpdateQuestion.For(drawn.Snapshot));
    }

    private static (WorkspaceModel Model, RegionSharing Sharing) Sharing()
    {
        WorkspaceModel model = Create(pointer: () => new Point(100, 100));
        model.BeginDrawing();
        var sharing = new RegionSharing(model, new Host());
        model.ShowToolbar();
        sharing.ShowControls();
        return (model, sharing);
    }

    [Fact]
    public void TheWordsArePlainSentences()
    {
        string[] sentences =
        [
            UpdateText.NotFromStore, UpdateText.Portable, UpdateText.Ready, UpdateText.ReadyManual, UpdateText.Checking, UpdateText.UpToDate,
            UpdateText.Available, UpdateText.Installing, UpdateText.Installed, UpdateText.Cancelled, UpdateText.LowBattery,
            UpdateText.NeedsWiFi, UpdateText.InstallFailed, UpdateText.CheckFailed, UpdateText.QuestionWithDrawings,
            UpdateText.QuestionWithSharing, UpdateText.QuestionWithDrawingsAndSharing,
        ];
        foreach (string sentence in sentences)
        {
            Assert.EndsWith(".", sentence, StringComparison.Ordinal);
            Assert.DoesNotContain('\u2014', sentence);
            Assert.DoesNotContain('\u2013', sentence);
        }
        Assert.Equal("Install the update now?", UpdateText.QuestionTitle);
        Assert.Equal("Install update", UpdateText.Install);
        Assert.Equal("Not now", UpdateText.NotNow);
    }

    /// <summary>The native side of region sharing, which these tests never show.</summary>
    private sealed class Host : IRegionSharingHost
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

        public IDisposable Schedule(TimeSpan delay, Action action) => new Timer();

        private sealed class Timer : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
