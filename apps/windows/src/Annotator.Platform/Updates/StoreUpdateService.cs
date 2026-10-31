using System.Runtime.InteropServices;
using Annotator.Platform.Interop;
using Annotator.Platform.Startup;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Services.Store;
using WinRT;

namespace Annotator.Platform.Updates;

/// <summary>Where the running copy of Annotator came from, which decides whether the Microsoft Store can update it.</summary>
public enum PackageSource
{
    /// <summary>No package identity: an unpackaged development build.</summary>
    Unpackaged,

    /// <summary>A package the Microsoft Store did not sign, such as a Developer Mode or enterprise install.</summary>
    Sideloaded,

    /// <summary>A package signed by the Microsoft Store, including package flights.</summary>
    Store,
}

/// <summary>How a Store download and install ended (StorePackageUpdateState).</summary>
public enum StoreInstallState
{
    Completed,
    Canceled,
    LowBattery,
    WiFiNeeded,
    Failed,
}

/// <summary>
/// Updates from the Microsoft Store (WIN-SYS-03) through Windows.Services.Store: whether this copy can get them, a check
/// for updates of its package, and the download and install that Windows asks the person about. Only a package installed
/// from the Store calls the Store; other copies throw before any Windows call. Use it on the app thread: the install shows
/// Windows' question over the owner window.
/// </summary>
public sealed class StoreUpdateService
{
    /// <summary>Where the organization's policy for the Store's automatic updates lives.</summary>
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\WindowsStore";

    /// <summary>AutoDownload 2 turns the Store's automatic updates off; 4 turns them on.</summary>
    public const string PolicyValue = "AutoDownload";

    private const int InitializeSlot = 3;
    private const int AlreadyInitialized = unchecked((int)0x800704DF);
    private static readonly Guid IID_IInitializeWithWindow = new("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1");
    private static readonly Lazy<PackageSource> s_source = new(ReadSource);

    private StoreContext? _context;
    private nint _contextOwner;
    private IReadOnlyList<StorePackageUpdate> _updates = [];

    /// <summary>Where this copy came from; read once, without the Store.</summary>
    public static PackageSource Source => s_source.Value;

    /// <summary>
    /// False when the organization turned off the Store's automatic updates. People can only pause them in the Store app,
    /// so the policy is what keeps them off.
    /// </summary>
    public static bool AutomaticUpdatesAllowed()
    {
        try
        {
            using RegistryKey? policy = Registry.LocalMachine.OpenSubKey(PolicyKey);
            return AutomaticUpdatesAllowed(policy?.GetValue(PolicyValue));
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>The Store's automatic updates for a policy value: only AutoDownload 2 turns them off.</summary>
    public static bool AutomaticUpdatesAllowed(object? autoDownload) => autoDownload is not 2;

    /// <summary>
    /// Asks the Store for updates of this package and keeps them for <see cref="InstallAsync"/>; returns how many there are.
    /// Windows' question appears over <paramref name="owner"/> later.
    /// </summary>
    /// <exception cref="InvalidOperationException">This copy was not installed from the Microsoft Store.</exception>
    public async Task<int> CheckAsync(nint owner)
    {
        StoreContext context = Context(owner);
        _updates = await context.GetAppAndOptionalStorePackageUpdatesAsync();
        return _updates.Count;
    }

    /// <summary>
    /// Asks the Store to download and install what the last check found. Windows asks the person first and may close
    /// Annotator to install; while Annotator still runs, the result says how it ended.
    /// </summary>
    /// <exception cref="InvalidOperationException">This copy was not installed from the Microsoft Store, or no update was found.</exception>
    public async Task<StoreInstallState> InstallAsync(nint owner)
    {
        if (owner == 0)
        {
            throw new InvalidOperationException("Installing a Store update needs a window for Windows' question.");
        }
        StoreContext context = Context(owner);
        if (_updates.Count == 0)
        {
            throw new InvalidOperationException("No Store update was found to install. Check for updates first.");
        }
        StorePackageUpdateResult result = await context.RequestDownloadAndInstallStorePackageUpdatesAsync(_updates);
        _updates = [];
        return Map(result.OverallState);
    }

    /// <summary>The typed end of an install for a StorePackageUpdateState; a state that is not an end counts as failed.</summary>
    public static StoreInstallState Map(StorePackageUpdateState state) => state switch
    {
        StorePackageUpdateState.Completed => StoreInstallState.Completed,
        StorePackageUpdateState.Canceled => StoreInstallState.Canceled,
        StorePackageUpdateState.ErrorLowBattery => StoreInstallState.LowBattery,
        StorePackageUpdateState.ErrorWiFiRecommended or StorePackageUpdateState.ErrorWiFiRequired => StoreInstallState.WiFiNeeded,
        _ => StoreInstallState.Failed,
    };

    /// <summary>
    /// The Store context for this user, tied to <paramref name="owner"/>, as a desktop app's must be. A new owner gets a new
    /// context: the tray menu's quiet check runs without a window and Settings gets a new window each time it opens, and the
    /// Store refused to install through a context tied to no window or a closed one ("Invalid window handle").
    /// </summary>
    private StoreContext Context(nint owner)
    {
        if (Source != PackageSource.Store)
        {
            throw new InvalidOperationException("Only a copy installed from the Microsoft Store can get updates from it.");
        }
        if (_context is { } context && _contextOwner == owner)
        {
            return context;
        }
        context = StoreContext.GetDefault();
        if (owner != 0)
        {
            InitializeWithWindow(context, owner);
        }
        _context = context;
        _contextOwner = owner;
        return context;
    }

    /// <summary>IInitializeWithWindow::Initialize, called by hand like the other COM interop here (Native AOT, S-12).</summary>
    private static unsafe void InitializeWithWindow(StoreContext context, nint owner)
    {
        nint unknown = ((IWinRTObject)context).NativeObject.ThisPtr;
        nint initialize = ComPtr.QueryInterface(unknown, IID_IInitializeWithWindow, "QueryInterface(IInitializeWithWindow)");
        try
        {
            var call = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(void***)initialize)[InitializeSlot];
            int result = call(initialize, owner);
            if (result != AlreadyInitialized)
            {
                NativeCallException.ThrowIfFailed(result, "IInitializeWithWindow::Initialize");
            }
        }
        finally
        {
            Marshal.Release(initialize);
            GC.KeepAlive(context);
        }
    }

    private static PackageSource ReadSource()
    {
        if (!StartupTaskService.HasPackageIdentity)
        {
            return PackageSource.Unpackaged;
        }
        try
        {
            return Package.Current.SignatureKind == PackageSignatureKind.Store ? PackageSource.Store : PackageSource.Sideloaded;
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Without the signature the copy counts as not from the Store, so nothing calls the Store.
            return PackageSource.Sideloaded;
        }
    }
}
