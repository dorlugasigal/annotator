using System.Runtime.InteropServices;
using Annotator.Platform;
using Annotator.Platform.Updates;
using Annotator.Workspace.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Annotator.App.Settings;

/// <summary>
/// Annotator's question before a Store install that could close it (WIN-SYS-03): "Install the update now?" with what would
/// be lost, Install update and Not now. Not now is the default button, so Enter keeps the drawings.
/// </summary>
internal static class UpdateDialog
{
    private static bool s_asking;

    /// <summary>True for Install update; false for Not now, or when the question cannot show over <paramref name="host"/>.</summary>
    public static async Task<bool> AskAsync(FrameworkElement host, UpdateQuestion question)
    {
        if (s_asking || host.XamlRoot is not { } root)
        {
            return false;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            RequestedTheme = host.ActualTheme,
            Title = UpdateText.QuestionTitle,
            Content = new TextBlock { Text = question.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = UpdateText.Install,
            CloseButtonText = UpdateText.NotNow,
            DefaultButton = ContentDialogButton.Close,
        };
        s_asking = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            StartupLog.Note("update-question", error.Message);
            return false;
        }
        finally
        {
            s_asking = false;
        }
    }
}

/// <summary>Keeps a part of Settings in step with <see cref="AppUpdates"/> while it is in the window (the Mac's @Published updateStatus).</summary>
internal static class UpdatesBinding
{
    /// <param name="show">Shows the current state; it runs now, whenever the state changes, and when the part comes back.</param>
    public static void Follow(FrameworkElement part, AppUpdates updates, Action show)
    {
        show();
        part.Loaded += (_, _) =>
        {
            updates.Changed -= show;
            updates.Changed += show;
            show();
        };
        part.Unloaded += (_, _) =>
        {
            if (!part.IsLoaded)
            {
                updates.Changed -= show;
            }
        };
    }
}

/// <summary>
/// Store updates on Windows (WIN-SYS-03): <see cref="StoreUpdateService"/> in the workspace's terms. Windows' question about
/// the download appears over <paramref name="owner"/>, the Settings window.
/// </summary>
internal sealed class WindowsStoreUpdates(Func<nint> owner) : IStoreUpdates
{
    private readonly StoreUpdateService _service = new();

    public UpdateChannel Channel { get; } = StoreUpdateService.Source switch
    {
        PackageSource.Store => UpdateChannel.Store,
        PackageSource.Sideloaded => UpdateChannel.Sideloaded,
        _ => BuildInfo.IsPortable ? UpdateChannel.Portable : UpdateChannel.Development,
    };

    public bool AutomaticUpdates => Channel == UpdateChannel.Store && StoreUpdateService.AutomaticUpdatesAllowed();

    public Task<StoreUpdateCheck> CheckAsync() =>
        Run(async () => await _service.CheckAsync(owner()) > 0 ? StoreUpdateCheck.Available : StoreUpdateCheck.UpToDate);

    public Task<StoreInstallOutcome> InstallAsync() => Run(async () => Map(await _service.InstallAsync(owner())));

    public static StoreInstallOutcome Map(StoreInstallState state) => state switch
    {
        StoreInstallState.Completed => StoreInstallOutcome.Installed,
        StoreInstallState.Canceled => StoreInstallOutcome.Cancelled,
        StoreInstallState.LowBattery => StoreInstallOutcome.LowBattery,
        StoreInstallState.WiFiNeeded => StoreInstallOutcome.NeedsWiFi,
        _ => StoreInstallOutcome.Failed,
    };

    /// <summary>A Store or Windows refusal becomes a <see cref="StoreUpdateFailure"/>, which the update status shows.</summary>
    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException
                                          or NotImplementedException or NativeCallException)
        {
            throw new StoreUpdateFailure($"{error.GetType().Name} 0x{error.HResult:X8}: {error.Message}");
        }
    }
}
