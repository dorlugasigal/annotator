namespace Annotator.Workspace.Settings;

/// <summary>
/// Launch at sign-in (WIN-SYS-01, the Mac's LoginItemState): the MSIX startup task's five states, plus the states a build
/// without a package and an unreadable task report. Windows is the source of truth; nothing here registers on its own.
/// </summary>
public enum LaunchAtSignInState
{
    /// <summary>The first read has not finished.</summary>
    Checking,

    /// <summary>StartupTaskState.Disabled (the Mac's off).</summary>
    Off,

    /// <summary>StartupTaskState.Enabled (the Mac's enabled).</summary>
    On,

    /// <summary>StartupTaskState.DisabledByUser: turned off in Windows Settings; only the person can turn it on there.</summary>
    DisabledByUser,

    /// <summary>StartupTaskState.DisabledByPolicy: the organization turned it off.</summary>
    DisabledByPolicy,

    /// <summary>StartupTaskState.EnabledByPolicy: the organization turned it on.</summary>
    EnabledByPolicy,

    /// <summary>The app is not installed as a package, so it has no startup task (development builds).</summary>
    NotPackaged,

    /// <summary>Windows reported a state Annotator does not know, or the task could not be read (the Mac's unavailable).</summary>
    Unavailable,
}

public static class LaunchAtSignInStateExtensions
{
    extension(LaunchAtSignInState state)
    {
        /// <summary>Mac isRequested: the switch shows on.</summary>
        public bool IsRequested => state is LaunchAtSignInState.On or LaunchAtSignInState.EnabledByPolicy;

        /// <summary>Mac canChange: the switch is available. Policy states, a missing package and unknown states disable it.</summary>
        public bool CanChange => state is LaunchAtSignInState.Off or LaunchAtSignInState.On or LaunchAtSignInState.DisabledByUser;

        /// <summary>The coarse state for support information.</summary>
        public string RawValue => state switch
        {
            LaunchAtSignInState.Checking => "checking",
            LaunchAtSignInState.Off => "off",
            LaunchAtSignInState.On => "enabled",
            LaunchAtSignInState.DisabledByUser => "disabledByUser",
            LaunchAtSignInState.DisabledByPolicy => "disabledByPolicy",
            LaunchAtSignInState.EnabledByPolicy => "enabledByPolicy",
            LaunchAtSignInState.NotPackaged => "notPackaged",
            LaunchAtSignInState.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        /// <summary>The line under the switch (Mac LoginItemState.message, with the Windows terms of specification 013).</summary>
        public string Message => state switch
        {
            LaunchAtSignInState.Checking => SettingsCopy.LaunchChecking,
            LaunchAtSignInState.Off => "Annotator will not start automatically.",
            LaunchAtSignInState.On => "Annotator will start quietly when you sign in. Use the tray icon or your drawing shortcut.",
            LaunchAtSignInState.DisabledByUser =>
                "Launch at sign-in was turned off in Windows. Turn on Annotator in Windows Settings > Apps > Startup.",
            LaunchAtSignInState.DisabledByPolicy => "Your organization manages launch at sign-in. Annotator will not start automatically.",
            LaunchAtSignInState.EnabledByPolicy =>
                "Your organization manages launch at sign-in. Annotator will start quietly when you sign in.",
            LaunchAtSignInState.NotPackaged =>
                "This development build is not installed as a package, so Windows cannot start it when you sign in.",
            LaunchAtSignInState.Unavailable =>
                "Windows returned an unsupported startup status. Check Startup apps in Windows Settings.",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
    }
}

/// <summary>
/// The startup task as Windows keeps it (the Mac's LoginItemService). Reading never registers or removes anything; only
/// <see cref="RequestEnableAsync"/> and <see cref="DisableAsync"/> change it. Failures are exceptions with a message for
/// the person.
/// </summary>
public interface IStartupTask
{
    Task<LaunchAtSignInState> ReadAsync();

    /// <summary>Asks Windows to turn the task on and returns the state Windows reports afterwards.</summary>
    Task<LaunchAtSignInState> RequestEnableAsync();

    /// <summary>Turns the task off and returns the state Windows reports afterwards.</summary>
    Task<LaunchAtSignInState> DisableAsync();

    /// <summary>Opens Windows Settings > Apps > Startup.</summary>
    void OpenSettings();
}

/// <summary>A startup task change that Windows refused or did not apply, with a message for the person.</summary>
public sealed class StartupTaskFailure(string message) : Exception(message)
{
    /// <summary>Mac LoginItemFailure.unavailable.</summary>
    public static StartupTaskFailure Unavailable() =>
        new("Windows cannot manage launch at sign-in for this app. Check Startup apps in Windows Settings.");

    /// <summary>Mac LoginItemFailure.unchanged.</summary>
    public static StartupTaskFailure Unchanged() =>
        new("Windows did not apply the requested startup change. Check Startup apps and try again.");
}

/// <summary>
/// Port of LoginItemController for the Settings switch (WIN-SYS-01, 005 launch-at-login.md): reads the state when Settings
/// appears or becomes active, changes it only on an explicit toggle, re-reads Windows afterwards and never keeps an
/// optimistic state. Use it on the app thread.
/// </summary>
public sealed class LaunchAtSignIn
{
    private readonly IStartupTask _task;
    private readonly Action<string> _report;
    private bool _reading;

    /// <param name="report">Shows a failure to the person, as the Mac's report(error).</param>
    public LaunchAtSignIn(IStartupTask task, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(report);
        _task = task;
        _report = report;
    }

    /// <summary>Raised after the state, the change in progress or the error changed.</summary>
    public event Action? Changed;

    public LaunchAtSignInState State { get; private set; } = LaunchAtSignInState.Checking;

    /// <summary>True while a change runs; the switch and the Settings button wait (Mac isChanging).</summary>
    public bool IsChanging { get; private set; }

    /// <summary>The last change's failure, shown under the switch until Windows reports another state.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Mac refresh(): reads Windows again unless a change runs. A new state clears an old error.</summary>
    public async Task RefreshAsync()
    {
        if (IsChanging || _reading)
        {
            return;
        }
        _reading = true;
        try
        {
            LaunchAtSignInState updated = await ReadOrUnavailableAsync();
            if (IsChanging)
            {
                return;
            }
            if (updated != State)
            {
                ErrorMessage = null;
            }
            State = updated;
        }
        finally
        {
            _reading = false;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Mac setEnabled: reads the current state, does nothing when it already matches, changes it, reads again and reports a
    /// change Windows did not apply. Policy states, a missing package and unknown states cannot change.
    /// </summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        if (IsChanging)
        {
            return;
        }
        IsChanging = true;
        ErrorMessage = null;
        Changed?.Invoke();
        try
        {
            State = await ReadOrUnavailableAsync();
            if (!State.CanChange)
            {
                throw StartupTaskFailure.Unavailable();
            }
            if (enabled == State.IsRequested)
            {
                return;
            }
            State = enabled ? await _task.RequestEnableAsync() : await _task.DisableAsync();
            if (enabled ? !State.IsRequested : State != LaunchAtSignInState.Off)
            {
                throw StartupTaskFailure.Unchanged();
            }
        }
        catch (Exception error) when (error is StartupTaskFailure or IOException or UnauthorizedAccessException
                                          or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            State = await ReadOrUnavailableAsync();
            ErrorMessage = $"Could not change launch at sign-in: {error.Message} Open Startup apps to check its status.";
            _report(ErrorMessage);
        }
        finally
        {
            IsChanging = false;
            Changed?.Invoke();
        }
    }

    /// <summary>The explicit Open Startup apps... button (Mac openSystemSettings).</summary>
    public void OpenSettings()
    {
        try
        {
            _task.OpenSettings();
        }
        catch (Exception error) when (error is StartupTaskFailure or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _report(error.Message);
        }
    }

    private async Task<LaunchAtSignInState> ReadOrUnavailableAsync()
    {
        try
        {
            return await _task.ReadAsync();
        }
        catch (Exception error) when (error is StartupTaskFailure or IOException or UnauthorizedAccessException
                                          or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return LaunchAtSignInState.Unavailable;
        }
    }
}

/// <summary>A startup task for runs that must never touch Windows (tests, the live check and reference states).</summary>
public sealed class FixedStartupTask(LaunchAtSignInState state) : IStartupTask
{
    public LaunchAtSignInState State { get; set; } = state;

    public int Requests { get; private set; }

    public int SettingsOpened { get; private set; }

    public Task<LaunchAtSignInState> ReadAsync() => Task.FromResult(State);

    public Task<LaunchAtSignInState> RequestEnableAsync()
    {
        Requests++;
        if (State == LaunchAtSignInState.Off)
        {
            State = LaunchAtSignInState.On;
        }
        return Task.FromResult(State);
    }

    public Task<LaunchAtSignInState> DisableAsync()
    {
        Requests++;
        if (State == LaunchAtSignInState.On)
        {
            State = LaunchAtSignInState.Off;
        }
        return Task.FromResult(State);
    }

    public void OpenSettings() => SettingsOpened++;
}
