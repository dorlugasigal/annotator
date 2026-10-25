using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Updates;

namespace Annotator.Workspace.Tests.Updates;

/// <summary>
/// A portable copy's own update checks (WIN-SYS-03, the Mac's Sparkle schedule): the website's feed, the daily automatic
/// check without timers, the switch, the once-per-release announcement and the quiet failures. The feed is
/// <see cref="FixedReleaseFeed"/>, so nothing here uses the network.
/// </summary>
public sealed class PortableUpdatesTests
{
    private static readonly Uri Downloads = new("https://getannotator.app/downloads/");
    private readonly List<string> _notes = [];
    private readonly AppDefaults _defaults = AppDefaults.InMemory();
    private readonly FixedReleaseFeed _feed = new(new ReleaseFeedEntry("0.1.0-beta.13", 31, Downloads));
    private DateTimeOffset _now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    private AppUpdates Make(int build = 30, UpdateChannel channel = UpdateChannel.Portable) =>
        new(new FixedStoreUpdates(channel), static () => null, static _ => Task.FromResult(false), _notes.Add,
            portable: new PortableUpdateSource(_feed, build, _defaults, () => _now));

    [Fact]
    public async Task APortableCopyWithAFeedChecksAndOnlySaysWhereToGetTheUpdate()
    {
        AppUpdates updates = Make();
        Assert.True(updates.IsConfigured);
        Assert.True(updates.ChecksFeed);
        Assert.True(updates.CanCheck);
        Assert.True(updates.AutomaticChecks);
        Assert.True(updates.CanChangeAutomaticChecks);
        Assert.Equal(UpdatePhase.Ready, updates.Phase);
        Assert.Equal("Annotator checks for updates once a day. Checks need an internet connection.", updates.Status);

        await updates.CheckAsync();

        Assert.Equal(1, _feed.Fetches);
        Assert.Equal(UpdatePhase.Available, updates.Phase);
        Assert.Equal(new ReleaseFeedEntry("0.1.0-beta.13", 31, Downloads), updates.Release);
        Assert.Equal("Annotator 0.1.0-beta.13 is available. Download it from getannotator.app, or run winget upgrade DorLugasiGal.Annotator.",
                     updates.Status);
        Assert.Equal(31, _defaults.Integer(AppUpdates.AnnouncedBuildKey));
        Assert.Equal(_now, _defaults.Date(AppUpdates.LastCheckKey));
    }

    [Fact]
    public async Task OpeningTheMenuChecksQuietlyAfterSixHoursWithoutAnnouncing()
    {
        AppUpdates updates = Make(build: 31);
        Assert.False(await updates.CheckIfDueAsync(menuOpened: true));
        Assert.Equal(1, _feed.Fetches);
        Assert.False(updates.UpdateWaiting);

        _feed.Answer = new ReleaseFeedEntry("0.1.0-beta.14", 32, Downloads);
        _now = _now.AddHours(5);
        Assert.False(await updates.CheckIfDueAsync(menuOpened: true));
        Assert.Equal(1, _feed.Fetches);
        Assert.False(await updates.CheckIfDueAsync(), "Settings keeps the daily schedule.");
        Assert.Equal(1, _feed.Fetches);

        _now = _now.AddHours(1);
        Assert.False(await updates.CheckIfDueAsync(menuOpened: true), "A quiet check never asks.");
        Assert.Equal(2, _feed.Fetches);
        Assert.True(updates.UpdateWaiting);
        Assert.Equal(UpdatePhase.Available, updates.Phase);
        Assert.Equal(0, _defaults.Integer(AppUpdates.AnnouncedBuildKey));
        Assert.Equal(_now, _defaults.Date(AppUpdates.LastCheckKey));

        _now = _now.AddDays(1);
        Assert.True(await updates.CheckIfDueAsync(menuOpened: true), "The daily check still announces the release once.");
        Assert.Equal(32, _defaults.Integer(AppUpdates.AnnouncedBuildKey));
    }

    [Fact]
    public async Task TheMenuChecksNothingWhileAutomaticChecksAreOff()
    {
        AppUpdates updates = Make();
        updates.SetAutomaticChecks(false);
        Assert.False(await updates.CheckIfDueAsync(menuOpened: true));
        Assert.Equal(0, _feed.Fetches);
    }

    [Fact]
    public async Task TheSameOrAnOlderBuildIsUpToDate()
    {
        AppUpdates updates = Make(build: 31);
        await updates.CheckAsync();
        Assert.Equal(UpdatePhase.UpToDate, updates.Phase);
        Assert.Null(updates.Release);
        Assert.Equal(UpdateText.UpToDate, updates.Status);
        Assert.Equal(0, _defaults.Integer(AppUpdates.AnnouncedBuildKey));
    }

    [Fact]
    public async Task AutomaticChecksRunAtMostOnceADayAndAnnounceARelease()
    {
        AppUpdates updates = Make();
        Assert.True(await updates.CheckIfDueAsync());
        Assert.Equal(1, _feed.Fetches);
        Assert.False(await updates.CheckIfDueAsync());
        Assert.Equal(1, _feed.Fetches);

        _now = _now.AddHours(23);
        Assert.False(await updates.CheckIfDueAsync());
        Assert.Equal(1, _feed.Fetches);
        _now = _now.AddHours(1);
        Assert.False(await updates.CheckIfDueAsync(), "The same release is announced once.");
        Assert.Equal(2, _feed.Fetches);
        Assert.Equal(UpdatePhase.Available, updates.Phase);

        _feed.Answer = new ReleaseFeedEntry("0.1.0-beta.14", 32, Downloads);
        _now = _now.AddDays(1);
        Assert.True(await updates.CheckIfDueAsync());
        Assert.Equal(32, _defaults.Integer(AppUpdates.AnnouncedBuildKey));
    }

    [Fact]
    public async Task ARelaunchKeepsTheLastCheckAndAClockSetBackChecksAgain()
    {
        Assert.True(await Make().CheckIfDueAsync());
        AppUpdates relaunched = Make();
        Assert.False(await relaunched.CheckIfDueAsync());
        Assert.Equal(1, _feed.Fetches);
        _now = _now.AddDays(-2);
        Assert.False(await relaunched.CheckIfDueAsync(), "Found again, but already announced.");
        Assert.Equal(2, _feed.Fetches);
    }

    [Fact]
    public async Task AManualCheckAnnouncesTooSoTheScheduleDoesNotAskAgain()
    {
        AppUpdates updates = Make();
        await updates.CheckAsync();
        _now = _now.AddDays(2);
        Assert.False(await updates.CheckIfDueAsync());
        Assert.Equal(2, _feed.Fetches);
    }

    [Fact]
    public async Task TurningAutomaticChecksOffIsSavedAndStopsTheSchedule()
    {
        AppUpdates updates = Make();
        int changes = 0;
        updates.Changed += () => changes++;
        updates.SetAutomaticChecks(false);
        Assert.Equal(1, changes);
        Assert.False(updates.AutomaticChecks);
        Assert.Equal("Automatic checks are off. You can still check here.", updates.Status);
        Assert.False(await updates.CheckIfDueAsync());
        Assert.Equal(0, _feed.Fetches);
        Assert.False(Make().AutomaticChecks);

        await updates.CheckAsync();
        Assert.Equal(1, _feed.Fetches);
        updates.SetAutomaticChecks(true);
        Assert.True(_defaults.Bool(AppUpdates.AutomaticChecksKey));
    }

    [Fact]
    public async Task AFailedManualCheckSaysSoAndAFailedAutomaticCheckStaysQuiet()
    {
        _feed.Failure = new ReleaseFeedFailure("HttpRequestException: no network");
        AppUpdates updates = Make();

        Assert.False(await updates.CheckIfDueAsync());
        Assert.Equal(UpdatePhase.Ready, updates.Phase);
        Assert.Null(_defaults.Date(AppUpdates.LastCheckKey));
        Assert.Contains("Update check: HttpRequestException: no network", _notes);

        await updates.CheckAsync();
        Assert.Equal(UpdatePhase.CheckFailed, updates.Phase);
        Assert.Equal("Annotator could not check for updates. Check your internet connection and try again.", updates.Status);
        Assert.True(updates.CanCheck);

        _feed.Failure = null;
        Assert.True(await updates.CheckIfDueAsync(), "A failed check doesn't count, so the next launch tries again.");
    }

    [Fact]
    public async Task ChecksWhileOneRunsAreIgnored()
    {
        var gate = new TaskCompletionSource();
        _feed.Gate = gate.Task;
        AppUpdates updates = Make();
        Task first = updates.CheckAsync();
        Assert.Equal(UpdatePhase.Checking, updates.Phase);
        Assert.Equal(UpdateText.PortableChecking, updates.Status);
        Assert.False(updates.CanCheck);
        await updates.CheckAsync();
        Assert.False(await updates.CheckIfDueAsync());
        gate.SetResult();
        await first;
        Assert.Equal(1, _feed.Fetches);
    }

    [Fact]
    public async Task StoreSideloadedAndDevelopmentCopiesIgnoreTheFeed()
    {
        foreach (UpdateChannel channel in new[] { UpdateChannel.Store, UpdateChannel.Sideloaded, UpdateChannel.Development })
        {
            AppUpdates updates = Make(channel: channel);
            Assert.False(updates.ChecksFeed);
            Assert.False(updates.CanChangeAutomaticChecks);
            Assert.False(await updates.CheckIfDueAsync());
            updates.SetAutomaticChecks(false);
            Assert.False(_defaults.Contains(AppUpdates.AutomaticChecksKey));
        }
        Assert.Equal(0, _feed.Fetches);
    }

    [Fact]
    public void TheFeedIsReadStrictly()
    {
        ReleaseFeedEntry entry = ReleaseFeed.Parse("""
            {"schemaVersion": 1, "track": "preview", "releaseVersion": "0.1.0-beta.13", "build": 31,
             "downloadPage": "https://getannotator.app/downloads/", "releaseURL": "https://github.com/dorlugasigal/annotator/releases/tag/v0.1.0-beta.13"}
            """u8);
        Assert.Equal(new ReleaseFeedEntry("0.1.0-beta.13", 31, Downloads), entry);
        foreach (string bad in new[]
        {
            "", "[]", "{}", """{"schemaVersion": 2, "releaseVersion": "1", "build": 1, "downloadPage": "https://a.example/"}""",
            """{"schemaVersion": 1, "releaseVersion": " ", "build": 1, "downloadPage": "https://a.example/"}""",
            """{"schemaVersion": 1, "releaseVersion": "1", "build": 0, "downloadPage": "https://a.example/"}""",
            """{"schemaVersion": 1, "releaseVersion": "1", "build": "31", "downloadPage": "https://a.example/"}""",
            """{"schemaVersion": 1, "releaseVersion": "1", "build": 1, "downloadPage": "http://a.example/"}""",
            """{"schemaVersion": 1, "releaseVersion": "1", "build": 1, "downloadPage": "https://user:pass@a.example/"}""",
            """{"schemaVersion": 1, "releaseVersion": "1", "build": 1}""",
        })
        {
            Assert.Throws<ReleaseFeedFailure>(() => ReleaseFeed.Parse(System.Text.Encoding.UTF8.GetBytes(bad)));
        }
        Assert.Throws<ReleaseFeedFailure>(() => ReleaseFeed.Parse(new byte[ReleaseFeed.MaximumBytes + 1]));
    }

    [Fact]
    public void OnlyAPlainHttpsFeedAddressIsUsed()
    {
        Assert.Equal(new Uri("https://getannotator.app/updates/windows-preview.json"),
                     ReleaseFeed.Address("https://getannotator.app/updates/windows-preview.json"));
        foreach (string? bad in new[] { null, "", "$(AnnotatorUpdateFeedUrl)", "http://getannotator.app/updates/windows-preview.json",
                     "https://user@getannotator.app/x.json", "https://getannotator.app:8443/x.json", "https://getannotator.app/x.json?y=1",
                     "updates/windows-preview.json" })
        {
            Assert.Null(ReleaseFeed.Address(bad));
        }
    }
}
