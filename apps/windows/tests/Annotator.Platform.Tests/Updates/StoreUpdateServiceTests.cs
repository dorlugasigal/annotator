using Annotator.Platform.Updates;
using Windows.Services.Store;

namespace Annotator.Platform.Tests.Updates;

/// <summary>
/// The Store update service outside a package (WIN-SYS-03): an unpackaged process is a development build that never calls
/// the Store; the install states and the organization's policy map to Annotator's types. A Store-installed copy is the
/// owner's check.
/// </summary>
public sealed class StoreUpdateServiceTests
{
    [Fact]
    public void AnUnpackagedProcessIsADevelopmentBuild() => Assert.Equal(PackageSource.Unpackaged, StoreUpdateService.Source);

    [Fact]
    public async Task AnUnpackagedProcessNeverCallsTheStore()
    {
        var service = new StoreUpdateService();
        InvalidOperationException check = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync(0));
        Assert.Equal("Only a copy installed from the Microsoft Store can get updates from it.", check.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(0));
    }

    [Fact]
    public async Task InstallingNeedsAWindowForWindowsQuestion()
    {
        // The tray menu's quiet check has no window; an install through such a context failed with "Invalid window handle".
        InvalidOperationException install = await Assert.ThrowsAsync<InvalidOperationException>(() => new StoreUpdateService().InstallAsync(0));
        Assert.Equal("Installing a Store update needs a window for Windows' question.", install.Message);
    }

    [Theory]
    [InlineData(StorePackageUpdateState.Completed, StoreInstallState.Completed)]
    [InlineData(StorePackageUpdateState.Canceled, StoreInstallState.Canceled)]
    [InlineData(StorePackageUpdateState.ErrorLowBattery, StoreInstallState.LowBattery)]
    [InlineData(StorePackageUpdateState.ErrorWiFiRecommended, StoreInstallState.WiFiNeeded)]
    [InlineData(StorePackageUpdateState.ErrorWiFiRequired, StoreInstallState.WiFiNeeded)]
    [InlineData(StorePackageUpdateState.OtherError, StoreInstallState.Failed)]
    [InlineData(StorePackageUpdateState.Pending, StoreInstallState.Failed)]
    [InlineData(StorePackageUpdateState.Downloading, StoreInstallState.Failed)]
    [InlineData(StorePackageUpdateState.Deploying, StoreInstallState.Failed)]
    public void InstallStatesMapToTypedEnds(StorePackageUpdateState state, StoreInstallState expected) =>
        Assert.Equal(expected, StoreUpdateService.Map(state));

    [Fact]
    public void OnlyThePolicyTurnsAutomaticUpdatesOff()
    {
        Assert.True(StoreUpdateService.AutomaticUpdatesAllowed(null));
        Assert.True(StoreUpdateService.AutomaticUpdatesAllowed(4));
        Assert.True(StoreUpdateService.AutomaticUpdatesAllowed(0));
        Assert.True(StoreUpdateService.AutomaticUpdatesAllowed("2"));
        Assert.False(StoreUpdateService.AutomaticUpdatesAllowed(2));
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\WindowsStore", StoreUpdateService.PolicyKey);
        Assert.Equal("AutoDownload", StoreUpdateService.PolicyValue);
    }

    [Fact]
    public void ReadingThePolicyChangesNothing()
    {
        bool first = StoreUpdateService.AutomaticUpdatesAllowed();
        Assert.Equal(first, StoreUpdateService.AutomaticUpdatesAllowed());
    }
}
