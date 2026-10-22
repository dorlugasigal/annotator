using Annotator.Platform.Startup;
using Windows.ApplicationModel;

namespace Annotator.Platform.Tests;

/// <summary>
/// Launch at sign-in's startup task wrapper (WIN-SYS-01). The test process runs without a package identity, as the
/// development app does, so every call reports NotPackaged and nothing reaches Windows' startup task list.
/// </summary>
public sealed class StartupTaskTests
{
    [Fact]
    public async Task AnUnpackagedProcessHasNoStartupTaskAndChangesNothing()
    {
        Assert.False(StartupTaskService.HasPackageIdentity);
        var service = new StartupTaskService();
        Assert.Equal(StartupTaskStatus.NotPackaged, await service.ReadAsync());
        Assert.Equal(StartupTaskStatus.NotPackaged, await service.RequestEnableAsync());
        Assert.Equal(StartupTaskStatus.NotPackaged, await service.DisableAsync());
    }

    [Fact]
    public void AnUnpackagedProcessWasNotStartedByTheStartupTask()
    {
        // Only a packaged process can be started by a startup task; this one never asks Windows.
        Assert.False(StartupTaskService.ActivatedByStartupTask());
    }

    [Fact]
    public void ThePackageManifestDeclaresTheTaskThisServiceReads()
    {
        string manifest = File.ReadAllText(PackageManifest());
        Assert.Contains($"TaskId=\"{StartupTaskService.DefaultTaskId}\"", manifest, StringComparison.Ordinal);
        Assert.Contains("uap10:Parameters=\"--startup-task\"", manifest, StringComparison.Ordinal);
        Assert.Contains("Enabled=\"false\"", manifest, StringComparison.Ordinal);
    }

    internal static string PackageManifest()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "apps", "windows", "src", "Annotator.App", "Package.appxmanifest");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"apps/windows/src/Annotator.App/Package.appxmanifest was not found above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void EveryStartupTaskStateHasATypedStatus()
    {
        Assert.Equal(StartupTaskStatus.Disabled, StartupTaskService.Map(StartupTaskState.Disabled));
        Assert.Equal(StartupTaskStatus.DisabledByUser, StartupTaskService.Map(StartupTaskState.DisabledByUser));
        Assert.Equal(StartupTaskStatus.Enabled, StartupTaskService.Map(StartupTaskState.Enabled));
        Assert.Equal(StartupTaskStatus.DisabledByPolicy, StartupTaskService.Map(StartupTaskState.DisabledByPolicy));
        Assert.Equal(StartupTaskStatus.EnabledByPolicy, StartupTaskService.Map(StartupTaskState.EnabledByPolicy));
        Assert.Equal(StartupTaskStatus.Unknown, StartupTaskService.Map((StartupTaskState)42));
    }

    [Fact]
    public void OnlyWindowsSettingsPagesOpen()
    {
        Assert.Throws<ArgumentException>(() => StartupTaskService.OpenSettingsPage("https://example.com", "Settings"));
        Assert.Throws<ArgumentException>(() => StartupTaskService.OpenSettingsPage("", "Settings"));
        Assert.StartsWith("ms-settings:", StartupTaskService.StartupSettingsUri, StringComparison.Ordinal);
    }
}
