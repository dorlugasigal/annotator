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
    /// <summary>The portable release's line: winget, or the website, brings new versions.</summary>
    public const string Portable =
        "This copy didn't come from the Microsoft Store. To update it, run winget upgrade DorLugasiGal.Annotator, or download the new version from getannotator.app.";

    public const string NotFromStore =
        "This copy wasn't installed from the Microsoft Store, so it can't check for updates. Find new versions in Settings > About.";
    public const string Ready = "The Microsoft Store installs updates automatically. Checks need an internet connection.";
    public const string ReadyManual = "Your organization turned off automatic updates from the Microsoft Store. You can still check here.";
    public const string Checking = "Checking the Microsoft Store for updates...";
    public const string UpToDate = "Annotator is up to date.";
    public const string Available = "An update is ready in the Microsoft Store. Select Check for updates to install it.";
    public const string Installing = "The Microsoft Store is installing the update. Windows may close Annotator to finish.";
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
/// Check for updates... on Windows (WIN-SYS-03, the Mac's AppUpdateController with Sparkle). The Microsoft Store answers,
/// and Windows asks before it installs. When an install could close Annotator while drawings exist or a region is shared,
/// Annotator asks first. Only a copy installed from the Store can check; the others say why. Use it on the app thread.
/// </summary>
public sealed class AppUpdates
{
    /// <summary>How long a check waits for the Store's answer.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long an install may take, including Windows' question, before Annotator stops waiting for it.</summary>
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    private readonly IStoreUpdates _store;
    private readonly Func<UpdateQuestion?> _question;
    private readonly Func<UpdateQuestion, Task<bool>> _confirm;
    private readonly Action<string> _note;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="question">The question to ask before installing now (<see cref="UpdateQuestion.For"/>), or null.</param>
    /// <param name="confirm">Asks the question; true installs, false waits (Not now).</param>
    /// <param name="note">Records the details of a Store failure for the log.</param>
    /// <param name="delay">Waits for a timeout; tests replace it.</param>
    public AppUpdates(IStoreUpdates store, Func<UpdateQuestion?> question, Func<UpdateQuestion, Task<bool>> confirm, Action<string> note,
                      Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(note);
        _store = store;
        _question = question;
        _confirm = confirm;
        _note = note;
        _delay = delay ?? Task.Delay;
        Phase = store.Channel == UpdateChannel.Store ? UpdatePhase.Ready : UpdatePhase.Unavailable;
    }

    /// <summary>Raised after <see cref="Phase"/> changed.</summary>
    public event Action? Changed;

    public UpdatePhase Phase { get; private set; }

    /// <summary>Why the last install ended without the update, while <see cref="Phase"/> is <see cref="UpdatePhase.NotInstalled"/>.</summary>
    public StoreInstallOutcome? Outcome { get; private set; }

    public UpdateChannel Channel => _store.Channel;

    /// <summary>The Mac's updatesConfigured: this copy gets its updates from the Microsoft Store.</summary>
    public bool IsConfigured => _store.Channel == UpdateChannel.Store;

    /// <summary>The Mac's canCheckForUpdates: Check for updates... works now.</summary>
    public bool CanCheck => IsConfigured && Phase is not (UpdatePhase.Checking or UpdatePhase.Installing);

    /// <summary>
    /// The Mac's automaticallyCheckForUpdates, which Windows leaves to the Microsoft Store: on for a Store copy unless the
    /// organization turned the Store's automatic updates off. Annotator cannot change it, so its switch stays disabled.
    /// </summary>
    public bool AutomaticChecks => IsConfigured && _store.AutomaticUpdates;

    /// <summary>The Mac's updateStatus: the line under Check for updates... in Settings and in the flyout.</summary>
    public string Status => Phase switch
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
