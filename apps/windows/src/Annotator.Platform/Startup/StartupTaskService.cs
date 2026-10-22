using System.ComponentModel;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.Platform.Startup;

/// <summary>The startup task's state as Windows reports it (StartupTaskState), plus the two states the wrapper adds.</summary>
public enum StartupTaskStatus
{
    Disabled,
    DisabledByUser,
    Enabled,
    DisabledByPolicy,
    EnabledByPolicy,

    /// <summary>The process has no package identity (an unpackaged development build), so it has no startup task.</summary>
    NotPackaged,

    /// <summary>Windows reported a value this wrapper does not know.</summary>
    Unknown,
}

/// <summary>
/// The MSIX startup task behind launch at sign-in (WIN-SYS-01). Reading never changes it; only
/// <see cref="RequestEnableAsync"/> and <see cref="DisableAsync"/> do, when the person turns the switch. An unpackaged
/// process has no task and reports <see cref="StartupTaskStatus.NotPackaged"/> without calling Windows. Package.appxmanifest
/// declares the task with <see cref="DefaultTaskId"/>.
/// </summary>
public sealed class StartupTaskService(string taskId = StartupTaskService.DefaultTaskId)
{
    /// <summary>The TaskId of the windows.startupTask extension in the package manifest.</summary>
    public const string DefaultTaskId = "AnnotatorStartup";

    /// <summary>The Windows Settings page that lists startup apps.</summary>
    public const string StartupSettingsUri = "ms-settings:startupapps";

    private const int AppModelErrorNoPackage = 15700;

    /// <summary>True when the process runs with a package identity, as an installed MSIX package does.</summary>
    public static bool HasPackageIdentity { get; } = ReadPackageIdentity();

    /// <summary>
    /// True when Windows started this packaged process for its startup task at sign-in (WIN-SYS-01, the Mac's
    /// AppLaunchContext.isLoginItem). The package's task also passes "--startup-task" (LaunchArguments); this reads the
    /// activation kind as a second sign. Call it once, first thing in Main: Windows hands a packaged app its activation only
    /// on the first call. An unpackaged process is never started by a startup task and does not call Windows.
    /// </summary>
    public static bool ActivatedByStartupTask()
    {
        if (!HasPackageIdentity)
        {
            return false;
        }
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == Windows.ApplicationModel.Activation.ActivationKind.StartupTask;
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Without the activation kind the launch counts as an ordinary one: the toolbar shows, nothing is lost.
            return false;
        }
    }

    public async Task<StartupTaskStatus> ReadAsync()
    {
        if (!HasPackageIdentity)
        {
            return StartupTaskStatus.NotPackaged;
        }
        StartupTask task = await StartupTask.GetAsync(taskId);
        return Map(task.State);
    }

    /// <summary>
    /// Asks Windows to turn the task on. Windows may show its own confirmation and keeps a choice the person made in
    /// Settings (DisabledByUser) or a policy; the returned state says what happened.
    /// </summary>
    public async Task<StartupTaskStatus> RequestEnableAsync()
    {
        if (!HasPackageIdentity)
        {
            return StartupTaskStatus.NotPackaged;
        }
        StartupTask task = await StartupTask.GetAsync(taskId);
        return Map(await task.RequestEnableAsync());
    }

    public async Task<StartupTaskStatus> DisableAsync()
    {
        if (!HasPackageIdentity)
        {
            return StartupTaskStatus.NotPackaged;
        }
        StartupTask task = await StartupTask.GetAsync(taskId);
        task.Disable();
        return Map((await StartupTask.GetAsync(taskId)).State);
    }

    /// <summary>The typed status of a StartupTaskState value.</summary>
    public static StartupTaskStatus Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Disabled => StartupTaskStatus.Disabled,
        StartupTaskState.DisabledByUser => StartupTaskStatus.DisabledByUser,
        StartupTaskState.Enabled => StartupTaskStatus.Enabled,
        StartupTaskState.DisabledByPolicy => StartupTaskStatus.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupTaskStatus.EnabledByPolicy,
        _ => StartupTaskStatus.Unknown,
    };

    /// <summary>Opens a Windows Settings page in the Settings app.</summary>
    /// <exception cref="InvalidOperationException">Windows could not open it; the message says where to go instead.</exception>
    public static void OpenSettingsPage(string uri, string manualPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        if (!uri.StartsWith("ms-settings:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only Windows Settings pages open here.", nameof(uri));
        }
        try
        {
            using Process? settings = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException($"Windows Settings could not be opened. Open {manualPath} manually.");
        }
    }

    private static unsafe bool ReadPackageIdentity()
    {
        uint length = 0;
        WIN32_ERROR result = PInvoke.GetCurrentPackageFullName(ref length, null);
        return (int)result != AppModelErrorNoPackage;
    }
}
