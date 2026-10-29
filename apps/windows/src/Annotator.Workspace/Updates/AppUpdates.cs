using Annotator.Workspace.Settings;

namespace Annotator.Workspace.Updates;

/// <summary>Where this copy of Annotator gets its updates (WIN-SYS-03).</summary>
public enum UpdateChannel
{
    /// <summary>An unpackaged development build: no updates, like the Mac's build without an update feed.</summary>
    Development,

    /// <summary>A package that was not installed from the Microsoft Store, such as a Developer Mode test install.</summary>
    Sideloaded,

    /// <summary>Installed from the Microsoft Store, which updates it.</summary>
    Store,

    /// <summary>The portable release from the website or winget: winget, or a new download, updates it.</summary>
    Portable,
}

/// <summary>What the Microsoft Store said when Annotator asked for updates.</summary>
public enum StoreUpdateCheck
{
    UpToDate,
    Available,
}

/// <summary>How an update the person started ended (the Store's StorePackageUpdateState).</summary>
public enum StoreInstallOutcome
{
    Installed,

    /// <summary>The person said no in Windows' question, or the download was cancelled.</summary>
    Cancelled,

    LowBattery,

    /// <summary>The Microsoft Store waits for Wi-Fi to download it (a metered connection).</summary>
    NeedsWiFi,

    Failed,
}

/// <summary>The Microsoft Store as <see cref="AppUpdates"/> uses it; the app's implementation calls Windows.Services.Store.</summary>
public interface IStoreUpdates
{
    UpdateChannel Channel { get; }

    /// <summary>The Microsoft Store installs updates by itself; false when the organization turned that off.</summary>
    bool AutomaticUpdates { get; }

    /// <summary>Asks the Store for updates of this package. Throws <see cref="StoreUpdateFailure"/> when it cannot answer.</summary>
    Task<StoreUpdateCheck> CheckAsync();

    /// <summary>
    /// Asks the Store to download and install what the last check found. Windows asks the person first and may close
    /// Annotator to install. Throws <see cref="StoreUpdateFailure"/> when the request fails.
    /// </summary>
    Task<StoreInstallOutcome> InstallAsync();
}

/// <summary>A Store request that failed. The message goes to the log; the person reads <see cref="AppUpdates.Status"/>.</summary>
public sealed class StoreUpdateFailure(string detail) : Exception(detail);

/// <summary>Where Check for updates... stands (the Mac's canCheckForUpdates and updateStatus as one typed state).</summary>
public enum UpdatePhase
{
    /// <summary>A development build or a package not from the Microsoft Store: nothing to check.</summary>
    Unavailable,

    /// <summary>Installed from the Store; nothing checked yet.</summary>
    Ready,

    Checking,

    UpToDate,

    /// <summary>The Store has an update, and the person chose Not now.</summary>
    Available,

    /// <summary>The Store downloads and installs the update.</summary>
    Installing,

    /// <summary>The Store installed the update while Annotator kept running.</summary>
    Installed,

    /// <summary>The update was not installed; <see cref="AppUpdates.Outcome"/> says why.</summary>
    NotInstalled,

    /// <summary>The Store could not answer, or did not answer in time.</summary>
    CheckFailed,
}

/// <summary>Annotator's question before an install that could close it (WIN-SYS-03): what closing would lose.</summary>
public sealed record UpdateQuestion(bool LosesDrawings, bool StopsSharing)
{
    /// <summary>
    /// The question to ask before installing, or null when closing Annotator loses nothing: no display has drawings, shown or
    /// hidden, or an edit in progress, and no region is shared.
    /// </summary>
    public static UpdateQuestion? For(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool sharing = snapshot.RegionSharing.IsCapturing;
        bool drawings = false;
        foreach (DisplaySnapshot display in snapshot.Displays)
        {
            drawings |= !display.Document.Elements.IsEmpty || display.ActiveEdit is not null;
        }
        return drawings || sharing ? new UpdateQuestion(drawings, sharing) : null;
    }

    public string Message => (LosesDrawings, StopsSharing) switch
    {
        (true, true) => UpdateText.QuestionWithDrawingsAndSharing,
        (false, true) => UpdateText.QuestionWithSharing,
        _ => UpdateText.QuestionWithDrawings,
    };
}

/// <summary>
/// The words of Store updates (the Mac's updateStatus and its update alerts, with the Microsoft Store of specification
/// 013). A development build keeps the Mac's sentence, <see cref="SettingsCopy.UpdatesUnavailable"/>.
/// </summary>
public static class UpdateText
{
    /// <summary>The winget command that updates the portable release.</summary>
    public const string WingetCommand = "winget upgrade DorLugasiGal.Annotator";

    /// <summary>The portable release's line: winget, or the website, brings new versions.</summary>
    public const string Portable =
        "This copy didn't come from the Microsoft Store. To update it, run winget upgrade DorLugasiGal.Annotator, or download the new version from getannotator.app.";

    /// <summary>A portable copy with automatic checks on (the Mac's status with Sparkle's daily check).</summary>
    public const string PortableReady = "Annotator checks for updates once a day. Checks need an internet connection.";

    /// <summary>The Mac's status once automatic checks are off.</summary>
    public const string PortableReadyManual = "Automatic checks are off. You can still check here.";

    public const string PortableChecking = "Checking for updates...";

    public const string PortableCheckFailed = "Annotator could not check for updates. Check your internet connection and try again.";

    /// <summary>A portable copy found a newer release; Annotator never downloads or installs it itself.</summary>
    public static string PortableAvailable(string version) =>
        $"Annotator {version} is available. Download it from getannotator.app, or run {WingetCommand}.";

    /// <summary>The once-per-version question after an automatic check found a release (Sparkle's update window).</summary>
    public static string AvailableTitle(string version) => $"Annotator {version} is available";

    public const string AvailableDetail =
        "Download it from getannotator.app, or copy the winget command and run it in Terminal. Save your board and quit Annotator before you install it.";

    public const string OpenDownloads = SettingsCopy.OpenDownloads;
    public const string CopyWinget = "Copy winget command";
    public const string WingetCopied = "The winget command is copied. Run it in Terminal to update Annotator.";
    public const string WingetNotCopied = "Could not copy the winget command. Try again.";
    public const string UpdateAvailableRow = "Update available…";

    public const string NotFromStore =
        "This copy wasn't installed from the Microsoft Store, so it can't check for updates. Find new versions in Settings > About.";
    public const string Ready = "The Microsoft Store installs updates automatically. Checks need an internet connection.";
    public const string ReadyManual = "Your organization turned off automatic updates from the Microsoft Store. You can still check here.";
    public const string Checking = "Checking the Microsoft Store for updates...";
    public const string UpToDate = "Annotator is up to date.";
    public const string Available = "An update is ready in the Microsoft Store. Select Check for updates to install it.";
    public const string Installing = "The Microsoft Store is installing the update. If Windows closes Annotator to finish, Annotator opens again by itself.";
    public const string Installed = "The update is installed. Quit Annotator and open it again to use the new version.";
    public const string Cancelled = "The update was not installed. Select Check for updates to try again.";
    public const string LowBattery = "The update was not installed because the battery is low. Plug in your computer and try again.";
    public const string NeedsWiFi = "The Microsoft Store needs Wi-Fi to download this update. Connect to Wi-Fi and try again.";
    public const string InstallFailed =
        "The Microsoft Store could not install the update. Try again later, or update Annotator in the Microsoft Store app.";
    public const string CheckFailed = "The Microsoft Store could not check for updates. Check your internet connection and try again.";

    public const string QuestionTitle = "Install the update now?";
    public const string QuestionWithDrawings =
        "Windows may close Annotator to install the update. If it does, your drawings are lost. Save your board first if you want to keep it.";
    public const string QuestionWithSharing = "Windows may close Annotator to install the update. If it does, region sharing stops.";
    public const string QuestionWithDrawingsAndSharing =
        "Windows may close Annotator to install the update. If it does, your drawings are lost and region sharing stops. Save your board first if you want to keep it.";
    public const string Install = "Install update";
    public const string NotNow = "Not now";
}

/// <summary>
/// What a portable copy needs to check for updates itself (WIN-SYS-03): the website's feed, its own build, the saved
/// settings and the clock. Builds are derived from versions (0.2.0 is 200) and grow with every release, so a copy of an
/// earlier version, with build 35 or lower, finds any later release newer.
/// </summary>
public sealed record PortableUpdateSource(IReleaseFeed Feed, int Build, Annotator.Workspace.FirstUse.AppDefaults Defaults,
                                          Func<DateTimeOffset>? Now = null);

/// <summary>
/// Check for updates... on Windows (WIN-SYS-03, the Mac's AppUpdateController with Sparkle). The Microsoft Store answers,
/// and Windows asks before it installs. When an install could close Annotator while drawings exist or a region is shared,
/// Annotator asks first. A portable copy reads the website's update feed instead and only says where to get a newer
/// release. Other copies say why they cannot check. Use it on the app thread.
/// </summary>
public sealed class AppUpdates
{
    /// <summary>How long a check waits for the Store's answer.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long an install may take, including Windows' question, before Annotator stops waiting for it.</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>A portable copy checks at most once in this time on its own, like Sparkle's SUScheduledCheckInterval.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// When the tray flyout opens and the last check is at least this old, Annotator checks again quietly, so the flyout can
    /// say Update available (the Mac's menu check, Sparkle's checkForUpdateInformation).
    /// </summary>
    public static readonly TimeSpan MenuCheckInterval = TimeSpan.FromHours(6);

    /// <summary>Sparkle's key for automatic checks; missing means on, as the Mac's Info.plist sets.</summary>
    public const string AutomaticChecksKey = "SUEnableAutomaticChecks";

    /// <summary>Sparkle's key for the last finished check.</summary>
    public const string LastCheckKey = "SULastCheckTime";

    /// <summary>The newest build that a check found and announced, so the question shows once per release.</summary>
    public const string AnnouncedBuildKey = "update-announced-build";

    private readonly IStoreUpdates _store;
    private readonly PortableUpdateSource? _portable;
    private readonly Func<UpdateQuestion?> _question;
    private readonly Func<UpdateQuestion, Task<bool>> _confirm;
    private readonly Action<string> _note;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>When the Microsoft Store last answered a check in this launch.</summary>
    private DateTimeOffset? _lastStoreCheck;

    /// <param name="question">The question to ask before installing now (<see cref="UpdateQuestion.For"/>), or null.</param>
    /// <param name="confirm">Asks the question; true installs, false waits (Not now).</param>
    /// <param name="note">Records the details of a Store or feed failure for the log.</param>
    /// <param name="delay">Waits for a timeout; tests replace it.</param>
    /// <param name="portable">The feed of a portable copy; without it, a portable copy cannot check.</param>
    /// <param name="now">The clock of a Store copy's menu checks; tests replace it. A portable copy uses its source's clock.</param>
    public AppUpdates(IStoreUpdates store, Func<UpdateQuestion?> question, Func<UpdateQuestion, Task<bool>> confirm, Action<string> note,
                      Func<TimeSpan, CancellationToken, Task>? delay = null, PortableUpdateSource? portable = null,
                      Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(note);
        _store = store;
        _portable = store.Channel == UpdateChannel.Portable ? portable : null;
        _question = question;
        _confirm = confirm;
        _note = note;
        _delay = delay ?? Task.Delay;
        _now = now ?? (static () => DateTimeOffset.Now);
        Phase =  IsConfigured ? UpdatePhase.Ready : UpdatePhase.Unavailable;
    }

    /// <summary>Raised after <see cref="Phase"/> changed.</summary>
    public event Action? Changed;

    public UpdatePhase Phase { get; private set; }

    /// <summary>Why the last install ended without the update, while <see cref="Phase"/> is <see cref="UpdatePhase.NotInstalled"/>.</summary>
    public StoreInstallOutcome? Outcome { get; private set; }

    public UpdateChannel Channel => _store.Channel;

    /// <summary>
    /// The Mac's updatesConfigured: this copy gets its updates from the Microsoft Store, or it is a portable copy built
    /// with the website's update feed.
    /// </summary>
    public bool IsConfigured => _store.Channel == UpdateChannel.Store || _portable is not null;

    /// <summary>A portable copy that checks the website's feed itself; it only tells, it never installs.</summary>
    public bool ChecksFeed => _portable is not null;

    /// <summary>The newer release a portable copy found, while <see cref="Phase"/> is <see cref="UpdatePhase.Available"/>.</summary>
    public ReleaseFeedEntry? Release { get; private set; }

    /// <summary>
    /// The Mac's updateReminderVersion: a check found an update that is not installed yet, so the tray flyout reads Update
    /// available… until a later check finds none.
    /// </summary>
    public bool UpdateWaiting => Phase == UpdatePhase.Available;

    /// <summary>The Mac's canCheckForUpdates: Check for updates... works now.</summary>
    public bool CanCheck => IsConfigured && Phase is not (UpdatePhase.Checking or UpdatePhase.Installing);

    /// <summary>
    /// The Mac's automaticallyCheckForUpdates. A Store copy leaves it to the Microsoft Store: on unless the organization turned
    /// the Store's automatic updates off, and Annotator cannot change it. A portable copy checks once a day unless the person
    /// turned that off (<see cref="SetAutomaticChecks"/>).
    /// </summary>
    public bool AutomaticChecks => _portable is { } portable
        ? !portable.Defaults.Contains(AutomaticChecksKey) || portable.Defaults.Bool(AutomaticChecksKey)
        : IsConfigured && _store.AutomaticUpdates;

    /// <summary>Only a portable copy's automatic checks belong to Annotator; the Store's belong to Windows.</summary>
    public bool CanChangeAutomaticChecks => _portable is not null;

    /// <summary>The Mac's setAutomaticChecks: a portable copy saves the person's choice.</summary>
    public void SetAutomaticChecks(bool enabled)
    {
        if (_portable is not { } portable || AutomaticChecks == enabled && portable.Defaults.Contains(AutomaticChecksKey))
        {
            return;
        }
        portable.Defaults.Set(AutomaticChecksKey, enabled);
        Changed?.Invoke();
    }

    /// <summary>The Mac's updateStatus: the line under Check for updates... in Settings and in the flyout.</summary>
    public string Status => _portable is not null ? PortableStatus : Phase switch
    {
        UpdatePhase.Unavailable => _store.Channel switch
        {
            UpdateChannel.Sideloaded => UpdateText.NotFromStore,
            UpdateChannel.Portable => UpdateText.Portable,
            _ => SettingsCopy.UpdatesUnavailable,
        },
        UpdatePhase.Ready => AutomaticChecks ? UpdateText.Ready : UpdateText.ReadyManual,
        UpdatePhase.Checking => UpdateText.Checking,
        UpdatePhase.UpToDate => UpdateText.UpToDate,
        UpdatePhase.Available => UpdateText.Available,
        UpdatePhase.Installing => UpdateText.Installing,
        UpdatePhase.Installed => UpdateText.Installed,
        UpdatePhase.NotInstalled => Outcome switch
        {
            StoreInstallOutcome.LowBattery => UpdateText.LowBattery,
            StoreInstallOutcome.NeedsWiFi => UpdateText.NeedsWiFi,
            StoreInstallOutcome.Failed => UpdateText.InstallFailed,
            _ => UpdateText.Cancelled,
        },
        _ => UpdateText.CheckFailed,
    };

    private string PortableStatus => Phase switch
    {
        UpdatePhase.Checking => UpdateText.PortableChecking,
        UpdatePhase.UpToDate => UpdateText.UpToDate,
        UpdatePhase.Available when Release is { } release => UpdateText.PortableAvailable(release.ReleaseVersion),
        UpdatePhase.CheckFailed => UpdateText.PortableCheckFailed,
        _ => AutomaticChecks ? UpdateText.PortableReady : UpdateText.PortableReadyManual,
    };

    /// <summary>
    /// A portable copy's automatic check (Sparkle's scheduled check): when automatic checks are on and the last check is at
    /// least <see cref="CheckInterval"/> old, reads the feed. Callers run it at launch and when the tray flyout or Settings
    /// opens, so no timer runs (WIN-SYS-07). A failed automatic check stays quiet and tries again next time. True when it found a release
    /// that was never announced, so the caller may ask about it once.
    /// <para>
    /// With <paramref name="menuOpened"/> (the tray flyout opened, the Mac's menu check) it also checks quietly when the last
    /// check is at least <see cref="MenuCheckInterval"/> old: a portable copy reads the feed, and a Store copy asks the
    /// Microsoft Store. A quiet check never announces; it only turns the flyout's row into Update available….
    /// </para>
    /// </summary>
    public async Task<bool> CheckIfDueAsync(bool menuOpened = false)
    {
        if (!AutomaticChecks || !CanCheck)
        {
            return false;
        }
        if (_portable is not { } portable)
        {
            if (menuOpened)
            {
                await CheckStoreQuietlyAsync();
            }
            return false;
        }
        TimeSpan? age = Age(portable.Defaults.Date(LastCheckKey), Now(portable));
        if (age is not { } since || since >= CheckInterval)
        {
            return await CheckFeedAsync(portable, automatic: true);
        }
        if (menuOpened && since >= MenuCheckInterval)
        {
            await CheckFeedAsync(portable, automatic: true, announce: false);
        }
        return false;
    }

    /// <summary>How long ago <paramref name="last"/> was, or null when there was none or the clock was set back since.</summary>
    private static TimeSpan? Age(DateTimeOffset? last, DateTimeOffset now) => last is { } at && now >= at ? now - at : null;

    /// <summary>
    /// A Store copy's quiet check when the tray flyout opens: asks the Microsoft Store whether an update waits, without
    /// installing it or asking anything. Only an update changes what the flyout says; a failure stays quiet.
    /// </summary>
    private async Task CheckStoreQuietlyAsync()
    {
        if (_store.Channel != UpdateChannel.Store || Phase is not (UpdatePhase.Ready or UpdatePhase.UpToDate or UpdatePhase.CheckFailed)
            || Age(_lastStoreCheck, _now()) is { } since && since < MenuCheckInterval)
        {
            return;
        }
        UpdatePhase before = Phase;
        Set(UpdatePhase.Checking);
        StoreUpdateCheck? found;
        try
        {
            found = await Bounded(_store.CheckAsync(), CheckTimeout);
        }
        catch (StoreUpdateFailure failure)
        {
            _note($"Update check: {failure.Message}");
            found = null;
        }
        if (found is not null)
        {
            _lastStoreCheck = _now();
        }
        Set(found == StoreUpdateCheck.Available ? UpdatePhase.Available : before);
    }

    /// <summary>
    /// Reads the feed; a found release is announced once, whether the person or the schedule checked. A quiet check
    /// (<paramref name="announce"/> false) leaves the announcement to the next scheduled check.
    /// </summary>
    private async Task<bool> CheckFeedAsync(PortableUpdateSource portable, bool automatic, bool announce = true)
    {
        UpdatePhase before = Phase;
        Set(UpdatePhase.Checking);
        ReleaseFeedEntry? found;
        try
        {
            found = await Bounded(portable.Feed.FetchAsync(), CheckTimeout);
        }
        catch (ReleaseFeedFailure failure)
        {
            _note($"Update check: {failure.Message}");
            found = null;
        }
        if (found is not { } release)
        {
            if (!automatic)
            {
                _note("Update check: the update feed did not answer.");
            }
            Set(automatic ? before : UpdatePhase.CheckFailed);
            return false;
        }
        portable.Defaults.Set(LastCheckKey, Now(portable));
        if (release.Build <= portable.Build)
        {
            Release = null;
            Set(UpdatePhase.UpToDate);
            return false;
        }
        Release = release;
        if (!announce)
        {
            Set(UpdatePhase.Available);
            return false;
        }
        bool announced = portable.Defaults.Integer(AnnouncedBuildKey) < release.Build;
        portable.Defaults.Set(AnnouncedBuildKey, (long)release.Build);
        Set(UpdatePhase.Available);
        return announced && automatic;
    }

    private static DateTimeOffset Now(PortableUpdateSource portable) => portable.Now?.Invoke() ?? DateTimeOffset.Now;

    /// <summary>
    /// Check for updates...: asks the Store; when it has an update, asks the person first if installing could remove drawings
    /// or stop sharing, then lets the Store download and install it. Every wait is bounded. Does nothing while
    /// <see cref="CanCheck"/> is false.
    /// </summary>
    public async Task CheckAsync()
    {
        if (!CanCheck)
        {
            return;
        }
        if (_portable is { } portable)
        {
            await CheckFeedAsync(portable, automatic: false);
            return;
        }
        Set(UpdatePhase.Checking);
        StoreUpdateCheck? found;
        try
        {
            found = await Bounded(_store.CheckAsync(), CheckTimeout);
        }
        catch (StoreUpdateFailure failure)
        {
            _note($"Update check: {failure.Message}");
            Set(UpdatePhase.CheckFailed);
            return;
        }
        if (found is not { } result)
        {
            _note($"Update check: the Microsoft Store did not answer within {CheckTimeout.TotalSeconds:F0} seconds.");
            Set(UpdatePhase.CheckFailed);
            return;
        }
        _lastStoreCheck = _now();
        if (result == StoreUpdateCheck.UpToDate)
        {
            Set(UpdatePhase.UpToDate);
            return;
        }
        if (_question() is { } question && !await _confirm(question))
        {
            Set(UpdatePhase.Available);
            return;
        }
        Set(UpdatePhase.Installing);
        StoreInstallOutcome outcome;
        try
        {
            outcome = await Bounded(_store.InstallAsync(), InstallTimeout) ?? Late();
        }
        catch (StoreUpdateFailure failure)
        {
            _note($"Update install: {failure.Message}");
            outcome = StoreInstallOutcome.Failed;
        }
        if (outcome == StoreInstallOutcome.Installed)
        {
            Set(UpdatePhase.Installed);
        }
        else
        {
            Set(UpdatePhase.NotInstalled, outcome);
        }
    }

    private StoreInstallOutcome Late()
    {
        _note($"Update install: the Microsoft Store did not finish within {InstallTimeout.TotalMinutes:F0} minutes.");
        return StoreInstallOutcome.Failed;
    }

    /// <summary>The work's result, or null when it took longer than <paramref name="timeout"/>; the timer stops either way.</summary>
    private async Task<T?> Bounded<T>(Task<T> work, TimeSpan timeout) where T : struct
    {
        using var stop = new CancellationTokenSource();
        Task timer = _delay(timeout, stop.Token);
        Task first = await Task.WhenAny(work, timer);
        await stop.CancelAsync();
        return first == work ? await work : null;
    }

    private void Set(UpdatePhase phase, StoreInstallOutcome? outcome = null)
    {
        Phase = phase;
        Outcome = outcome;
        Changed?.Invoke();
    }
}

/// <summary>A Store with fixed answers for tests, the live check and reference states; it never calls Windows.</summary>
public sealed class FixedStoreUpdates(UpdateChannel channel, bool automaticUpdates = true) : IStoreUpdates
{
    public UpdateChannel Channel { get; } = channel;

    public bool AutomaticUpdates { get; } = automaticUpdates;

    /// <summary>The answer to the next checks.</summary>
    public StoreUpdateCheck Answer { get; set; } = StoreUpdateCheck.UpToDate;

    /// <summary>The outcome of the next installs.</summary>
    public StoreInstallOutcome Outcome { get; set; } = StoreInstallOutcome.Installed;

    /// <summary>When set, the next checks and installs throw it, as a failed Store request does.</summary>
    public StoreUpdateFailure? Failure { get; set; }

    /// <summary>When set, checks and installs wait for it, as a slow Store would.</summary>
    public Task? Gate { get; set; }

    public int Checks { get; private set; }

    public int Installs { get; private set; }

    public async Task<StoreUpdateCheck> CheckAsync()
    {
        Checks++;
        await Wait();
        return Answer;
    }

    public async Task<StoreInstallOutcome> InstallAsync()
    {
        Installs++;
        await Wait();
        return Outcome;
    }

    private async Task Wait()
    {
        if (Gate is { } gate)
        {
            await gate;
        }
        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}
