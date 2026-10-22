using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Platform.Startup;
using Annotator.Platform.Text;
using Annotator.Workspace;
using Annotator.Workspace.Capture;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Updates;

namespace Annotator.App.Settings;

/// <summary>Launch at sign-in on Windows: the MSIX startup task of <see cref="StartupTaskService"/> in the workspace's states.</summary>
internal sealed class WindowsStartupTask : IStartupTask
{
    private readonly StartupTaskService _service = new();

    public Task<LaunchAtSignInState> ReadAsync() => Run(_service.ReadAsync);

    public Task<LaunchAtSignInState> RequestEnableAsync() => Run(_service.RequestEnableAsync);

    public Task<LaunchAtSignInState> DisableAsync() => Run(_service.DisableAsync);

    public void OpenSettings() => StartupTaskService.OpenSettingsPage(StartupTaskService.StartupSettingsUri, "Settings > Apps > Startup");

    public static LaunchAtSignInState Map(StartupTaskStatus status) => status switch
    {
        StartupTaskStatus.Disabled => LaunchAtSignInState.Off,
        StartupTaskStatus.Enabled => LaunchAtSignInState.On,
        StartupTaskStatus.DisabledByUser => LaunchAtSignInState.DisabledByUser,
        StartupTaskStatus.DisabledByPolicy => LaunchAtSignInState.DisabledByPolicy,
        StartupTaskStatus.EnabledByPolicy => LaunchAtSignInState.EnabledByPolicy,
        StartupTaskStatus.NotPackaged => LaunchAtSignInState.NotPackaged,
        _ => LaunchAtSignInState.Unavailable,
    };

    /// <summary>A package without the startup task, or a Windows refusal, reads as unavailable (the Mac's notFound).</summary>
    private static async Task<LaunchAtSignInState> Run(Func<Task<StartupTaskStatus>> call)
    {
        try
        {
            return Map(await call());
        }
        catch (Exception error) when (error is COMException or ArgumentException or KeyNotFoundException or NotImplementedException
                                          or UnauthorizedAccessException or InvalidOperationException)
        {
            StartupLog.Note("startup-task", error.Message);
            throw StartupTaskFailure.Unavailable();
        }
    }
}

/// <summary>
/// What Settings needs from the app around it: the build's facts and links, its bundled files and its services. The app
/// passes the real ones; reference states pass fixed facts and <see cref="Live"/> false, so nothing opens or changes.
/// </summary>
internal sealed record SettingsEnvironment
{
    public required string VersionLabel { get; init; }

    public required string BuildLabel { get; init; }

    /// <summary>The release-notes version (with its prerelease suffix) and build of this app.</summary>
    public required string NotesVersion { get; init; }

    public int? BuildNumber { get; init; }

    /// <summary>The Local build badge of development builds (the Mac's .dev bundle).</summary>
    public bool LocalBuild { get; init; }

    public string? IssuesUrl { get; init; }

    public string? Website { get; init; }

    public Uri? SupportLink { get; init; }

    /// <summary>The folder with the app's bundled files: notes.json, annotator\SKILL.md and the license notices.</summary>
    public required string AppFolder { get; init; }

    /// <summary>The facts of support info that the workspace does not know (WIN-PRV-04).</summary>
    public required Func<SupportFacts> Facts { get; init; }

    /// <summary>Registers an imported profile's global shortcuts and returns why it could not; null where the app has none.</summary>
    public Func<WorkspacePreferences, string?>? RegisterShortcuts { get; init; }

    public bool HasTrayIcon { get; init; }

    /// <summary>False for reference states: links, dialogs, files, the clipboard and Windows Settings are left alone.</summary>
    public bool Live { get; init; } = true;

    /// <summary>The running app's environment.</summary>
    public static SettingsEnvironment ForApp(Func<SupportFacts> facts, Func<WorkspacePreferences, string?>? registerShortcuts, bool hasTrayIcon) => new()
    {
        VersionLabel = BuildInfo.VersionLabel,
        BuildLabel = BuildInfo.BuildLabel,
        NotesVersion = BuildInfo.NotesVersion,
        BuildNumber = BuildInfo.BuildNumber,
        LocalBuild = BuildInfo.IsDevelopment,
        IssuesUrl = BuildInfo.IssuesUrl,
        Website = BuildInfo.Website,
        SupportLink = BuildInfo.SupportLink,
        AppFolder = AppContext.BaseDirectory,
        Facts = facts,
        RegisterShortcuts = registerShortcuts,
        HasTrayIcon = hasTrayIcon,
    };
}

/// <summary>
/// The actions of Settings and its feedback windows (the Mac WorkspaceModel's openProductPage, copySupportInfo,
/// saveSupportInfo, exportPreferences, importPreferences, exportComputerUseSkill, openOptionalSupport, reportIssue,
/// openCaptureSettings and checkForUpdates). Outcomes go to the workspace's status and error, as on the Mac: the error
/// window shows errors, and the status stays in the workspace, which shows it nowhere, like the Mac since its 2026-09-24
/// interface pass. Use it on the app thread.
/// </summary>
internal sealed class SettingsActions(WorkspaceModel model, LaunchAtSignIn launch, AppUpdates updates, SettingsEnvironment environment)
{
    private static readonly FileDialogs.FileType s_json = new("JSON", "*.json");
    private static readonly FileDialogs.FileType s_text = new("Text", "*.txt");

    public WorkspaceModel Model => model;

    public LaunchAtSignIn Launch => launch;

    /// <summary>Check for updates... and its status (WIN-SYS-03).</summary>
    public AppUpdates Updates => updates;

    public SettingsEnvironment Environment => environment;

    /// <summary>The window that owns dialogs and the clipboard: the Settings window, set when it is made.</summary>
    public Func<nint> Owner { get; set; } = () => 0;

    /// <summary>Shows a tab of the Settings window (Help's Shortcuts and Open Permissions).</summary>
    public Action<SettingsTab>? OpenTab { get; set; }

    /// <summary>Opens Report a problem or Share feedback.</summary>
    public Action<FeedbackKind>? OpenFeedback { get; set; }

    /// <summary>Opens the tour (the Mac's onShowIntroduction): General's Show introduction and Help's Quick start.</summary>
    public Action? ShowIntroduction { get; set; }

    /// <summary>
    /// Usage counts for General's Privacy section (the Mac's model.usageMetrics, WIN-PRV-01): the app sets its own; reference
    /// states keep an in-memory one.
    /// </summary>
    public Annotator.Workspace.Metrics.UsageMetrics UsageMetrics { get; set; } = Annotator.Workspace.Metrics.UsageMetrics.Unavailable();

    /// <summary>The Mac's issueReportURL: the issue form when this build names a valid one.</summary>
    public Uri? IssueUrl => IssueReporting.IssueUrl(environment.IssuesUrl, environment.VersionLabel);

    /// <summary>An outcome for the workspace's status (Mac status).</summary>
    public void Say(string status) => model.ShowStatus(status);

    /// <summary>A failure: the workspace reports it, and the error window shows it (Mac report).</summary>
    public void Fail(string message) => model.Report(message);

    /// <summary>Opens a web page in the default browser; false when Windows could not.</summary>
    public bool OpenLink(Uri link)
    {
        if (!environment.Live)
        {
            return true;
        }
        try
        {
            using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StartupLog.Note("settings-link", error.Message);
            return false;
        }
    }

    /// <summary>Mac openProductPage: the website's Help, Downloads, AI or Privacy page.</summary>
    public void OpenProductPage(ProductPage page)
    {
        if (IssueReporting.ProductPageUrl(environment.Website, page) is not { } link || !OpenLink(link))
        {
            Fail(IssueReporting.BrowserUnavailableMessage);
        }
    }

    /// <summary>Mac openOptionalSupport: Buy me a coffee.</summary>
    public void BuyCoffee()
    {
        if (environment.SupportLink is not { } link)
        {
            Fail(IssueReporting.SupportNotConfiguredMessage);
        }
        else if (!OpenLink(link))
        {
            Fail(IssueReporting.BrowserUnavailableMessage);
        }
    }

    /// <summary>Mac reportIssue(feedback:): the issue form with only the version (Open GitHub form).</summary>
    public void ReportIssue(bool feedback)
    {
        if (IssueReporting.IssueUrl(environment.IssuesUrl, environment.VersionLabel, feedback) is not { } link)
        {
            Fail(IssueReporting.NotConfiguredMessage);
        }
        else if (!OpenLink(link))
        {
            Fail(IssueReporting.BrowserUnavailableMessage);
        }
    }

    /// <summary>
    /// The Permissions tab's Refresh status, where Settings shows a zoom or capture failure (the Mac's retryLiveEffects): a
    /// lens or screen zoom that failed starts again (<see cref="WorkspaceModel.RetryLiveZoom"/>), else failed live effects try
    /// again.
    /// </summary>
    public void RetryFailures()
    {
        if (!model.RetryLiveZoom() && model.Snapshot.CaptureFailureMessage is not null)
        {
            model.RetryLiveEffects();
        }
    }

    /// <summary>
    /// The Permissions tab's button once screen access is allowed (the Mac's Open Screen Recording settings): Windows Settings
    /// &gt; Privacy &amp; security &gt; Screenshots and screen recording, or Screenshot borders while that is what Windows refuses.
    /// </summary>
    public void OpenCaptureSettings() => OpenCaptureSettings(model.Snapshot.ScreenAccess);

    /// <summary>
    /// The Permissions tab's Allow screen access (the Mac's requestCapturePermission): asks Windows at once from Settings, which
    /// is in front, and starts what failed once allowed. Windows does not ask again after a refusal, so the button then opens
    /// the Windows Settings page that allows it, as the Mac opens System Settings.
    /// </summary>
    public void AllowScreenAccess()
    {
        if (!environment.Live)
        {
            return;
        }
        ScreenAccess access = model.RefreshScreenAccess();
        model.RequestScreenAccess();
        if (access.IsRefused)
        {
            OpenCaptureSettings(access);
        }
    }

    private void OpenCaptureSettings(ScreenAccess access)
    {
        if (!environment.Live)
        {
            return;
        }
        (string uri, string path) = access == ScreenAccess.BorderOff
            ? (Platform.Capture.CaptureAccess.BorderSettingsUri, "Settings > Privacy & security > Screenshot borders")
            : (Platform.Capture.CaptureAccess.ScreenSettingsUri, "Settings > Privacy & security > Screenshots and screen recording");
        try
        {
            StartupTaskService.OpenSettingsPage(uri, path);
        }
        catch (InvalidOperationException error)
        {
            Fail(error.Message);
        }
    }

    /// <summary>
    /// Mac checkForUpdates: asks the Microsoft Store when this copy can (the status under the button follows), or puts the
    /// reason it cannot in the workspace's status.
    /// </summary>
    public void CheckForUpdates()
    {
        if (!updates.CanCheck)
        {
            Say(updates.Status);
            return;
        }
        _ = updates.CheckAsync();
    }

    /// <summary>Support info: coarse app and system details, never drawings, screenshots, paths or names (WIN-PRV-04).</summary>
    public string SupportInfoText() => SupportInfo.Text(environment.Facts(), model.Snapshot);

    /// <summary>Mac copySupportInfo.</summary>
    public void CopySupportInfo()
    {
        if (!environment.Live)
        {
            return;
        }
        try
        {
            TextClipboard.SetText(Owner(), SupportInfoText());
            Say(SettingsCopy.SupportInfoCopied);
        }
        catch (InvalidOperationException error)
        {
            StartupLog.Note("support-info", error.Message);
            Fail(SettingsCopy.SupportInfoNotCopied);
        }
    }

    /// <summary>Mac saveSupportInfo: a text file where the person chooses.</summary>
    public void SaveSupportInfo() => Later(SaveSupportInfoNow);

    private void SaveSupportInfoNow()
    {
        if (!environment.Live || Choose(() => FileDialogs.Save(Owner(), SettingsCopy.SupportInfoFileName, s_text, "txt")) is not { } path)
        {
            return;
        }
        if (Write(path, System.Text.Encoding.UTF8.GetBytes(SupportInfoText()), "Support info could not be saved there. Choose another folder and try again."))
        {
            Say(SettingsCopy.SupportInfoSaved);
        }
    }

    /// <summary>Mac exportPreferences: the profile as JSON, written atomically where the person chooses.</summary>
    public void ExportProfile() => Later(ExportProfileNow);

    private void ExportProfileNow()
    {
        if (!environment.Live || Choose(() => FileDialogs.Save(Owner(), SettingsCopy.ProfileFileName, s_json, "json")) is not { } path)
        {
            return;
        }
        if (model.ExportPreferences() is not { } data)
        {
            // The workspace reported why; the error window shows it.
            return;
        }
        Write(path, data, "The profile could not be saved there. Choose another folder and try again.");
    }

    /// <summary>
    /// Mac importPreferences: reads at most one byte more than the largest profile, so a large file is refused without
    /// reading it all, and replaces settings and shortcuts. Boards do not change.
    /// </summary>
    public void ImportProfile() => Later(ImportProfileNow);

    private void ImportProfileNow()
    {
        if (!environment.Live || Choose(() => FileDialogs.Open(Owner(), s_json)) is not { } path)
        {
            return;
        }
        byte[] data;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            data = new byte[(int)Math.Min(file.Length, WorkspaceModel.MaximumProfileBytes + 1)];
            file.ReadExactly(data);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupLog.Note("profile", error.Message);
            Fail("The profile could not be read. Check that the file is still there and try again.");
            return;
        }
        // A refused profile is reported by the workspace, and the error window shows why.
        _ = model.ImportPreferences(data, environment.RegisterShortcuts, environment.HasTrayIcon);
    }

    /// <summary>Mac exportComputerUseSkill: the bundled annotator skill as a new folder where the person chooses.</summary>
    public void SaveSkill() => Later(SaveSkillNow);

    private void SaveSkillNow()
    {
        if (!environment.Live || Choose(() => FileDialogs.Folder(Owner(), SettingsCopy.SkillDialogTitle, SettingsCopy.SkillDialogButton)) is not { } folder)
        {
            return;
        }
        try
        {
            string saved = ComputerUseSkill.Export(folder, Path.Combine(environment.AppFolder, ComputerUseSkill.FolderName, ComputerUseSkill.FileName));
            Say(ComputerUseSkill.Saved(saved));
        }
        catch (SkillExportFailure failure)
        {
            Fail(failure.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupLog.Note("skill", error.Message);
            Fail("The skill could not be saved there. Choose another folder and try again.");
        }
    }

    /// <summary>
    /// Runs a dialog's action after the current input is handled, so a click, or a UI Automation Invoke from Narrator,
    /// returns before the dialog's modal loop starts.
    /// </summary>
    private static void Later(Action action)
    {
        if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not { } queue || !queue.TryEnqueue(() => action()))
        {
            action();
        }
    }

    /// <summary>Shows the dialog; a dialog Windows could not open is reported instead.</summary>
    private string? Choose(Func<string?> dialog)
    {
        try
        {
            return dialog();
        }
        catch (Platform.NativeCallException error)
        {
            StartupLog.Note("file-dialog", error.Message);
            Fail("The file window could not open. Try again.");
            return null;
        }
    }

    /// <summary>Writes next to the destination first, then replaces it, so a failure never leaves half a file.</summary>
    private bool Write(string path, byte[] data, string failure)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path) ?? ".", $".{Path.GetFileName(path)}.{System.Environment.ProcessId}.tmp");
        try
        {
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupLog.Note("settings-save", error.Message);
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                StartupLog.Note("settings-save", cleanup.Message);
            }
            Fail(failure);
            return false;
        }
    }
}

/// <summary>
/// Launch at sign-in for the portable copy, which has no package and so no startup task: the account's Run entry
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run) starts Annotator.exe with --startup-task, which shows no
/// toolbar. Windows Settings > Apps > Startup can turn the entry off (its StartupApproved value), which reads as turned
/// off in Windows, as for the Store package.
/// </summary>
internal sealed class RunKeyStartupTask : IStartupTask
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "Annotator";

    private static string Command => $"\"{Environment.ProcessPath}\" {Annotator.Workspace.Packaging.LaunchArguments.StartupTaskSwitch}";

    public Task<LaunchAtSignInState> ReadAsync() => Task.FromResult(Read());

    public Task<LaunchAtSignInState> RequestEnableAsync()
    {
        Change(key => key.SetValue(Name, Command, Microsoft.Win32.RegistryValueKind.String));
        return Task.FromResult(Read());
    }

    public Task<LaunchAtSignInState> DisableAsync()
    {
        Change(key => key.DeleteValue(Name, throwOnMissingValue: false));
        return Task.FromResult(Read());
    }

    public void OpenSettings() => StartupTaskService.OpenSettingsPage(StartupTaskService.StartupSettingsUri, "Settings > Apps > Startup");

    private static LaunchAtSignInState Read()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string command)
            {
                return LaunchAtSignInState.Off;
            }
            if (!string.Equals(command, Command, StringComparison.OrdinalIgnoreCase))
            {
                // An entry for a copy in another folder: turning the switch on points it here.
                return LaunchAtSignInState.Off;
            }
            using Microsoft.Win32.RegistryKey? approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // Windows writes 02 (or nothing) for on and 03 for turned off in Settings > Apps > Startup.
            return approved?.GetValue(Name) is byte[] { Length: > 0 } state && state[0] % 2 == 1
                ? LaunchAtSignInState.DisabledByUser : LaunchAtSignInState.On;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            StartupLog.Note("startup-run-key", error.Message);
            return LaunchAtSignInState.Unavailable;
        }
    }

    private static void Change(Action<Microsoft.Win32.RegistryKey> change)
    {
        try
        {
            using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            change(key);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            StartupLog.Note("startup-run-key", error.Message);
            throw StartupTaskFailure.Unavailable();
        }
    }
}
