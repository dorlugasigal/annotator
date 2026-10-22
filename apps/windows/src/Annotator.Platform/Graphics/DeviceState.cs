namespace Annotator.Platform.Graphics;

/// <summary>The state of a <see cref="GraphicsDevices"/> bundle. Lost and Disposed are final.</summary>
public abstract record DeviceState
{
    private DeviceState()
    {
    }

    /// <summary>The devices work.</summary>
    public sealed record Ready : DeviceState;

    /// <summary>
    /// The GPU device was removed, reset, hung or must be recreated. Every resource made from
    /// it is unusable: dispose the bundle and create a new one, then rebuild surfaces.
    /// </summary>
    public sealed record Lost(DeviceLossKind Kind, int HResult) : DeviceState;

    /// <summary>The bundle was disposed.</summary>
    public sealed record Disposed : DeviceState;
}

/// <summary>Why a device was lost, from its HRESULT.</summary>
public enum DeviceLossKind
{
    /// <summary>DXGI_ERROR_DEVICE_REMOVED: the GPU was removed, disabled or its driver updated.</summary>
    Removed,

    /// <summary>DXGI_ERROR_DEVICE_RESET: a badly formed command reset the device.</summary>
    Reset,

    /// <summary>DXGI_ERROR_DEVICE_HUNG: the GPU stopped responding.</summary>
    Hung,

    /// <summary>DXGI_ERROR_DRIVER_INTERNAL_ERROR.</summary>
    DriverInternalError,

    /// <summary>DXGI_ERROR_INVALID_CALL reported as the removal reason.</summary>
    InvalidCall,

    /// <summary>D2DERR_RECREATE_TARGET: Direct2D needs new device resources.</summary>
    RecreateTarget,

    /// <summary>The device reported another removal reason; see <see cref="DeviceState.Lost.HResult"/>.</summary>
    Other,
}

/// <summary>Classifies DirectX HRESULTs that mean the device is gone.</summary>
public static class DeviceLoss
{
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    public const int DXGI_ERROR_DEVICE_HUNG = unchecked((int)0x887A0006);
    public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
    public const int DXGI_ERROR_DRIVER_INTERNAL_ERROR = unchecked((int)0x887A0020);
    public const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    /// <summary>True for HRESULTs a draw or present call returns when the device is lost.</summary>
    public static bool IsDeviceLoss(int hresult) => hresult is
        DXGI_ERROR_DEVICE_REMOVED or
        DXGI_ERROR_DEVICE_RESET or
        DXGI_ERROR_DEVICE_HUNG or
        DXGI_ERROR_DRIVER_INTERNAL_ERROR or
        D2DERR_RECREATE_TARGET;

    /// <summary>Maps a failure HRESULT (a draw result or a device removed reason) to a loss kind.</summary>
    public static DeviceLossKind Classify(int hresult) => hresult switch
    {
        DXGI_ERROR_DEVICE_REMOVED => DeviceLossKind.Removed,
        DXGI_ERROR_DEVICE_RESET => DeviceLossKind.Reset,
        DXGI_ERROR_DEVICE_HUNG => DeviceLossKind.Hung,
        DXGI_ERROR_DRIVER_INTERNAL_ERROR => DeviceLossKind.DriverInternalError,
        DXGI_ERROR_INVALID_CALL => DeviceLossKind.InvalidCall,
        D2DERR_RECREATE_TARGET => DeviceLossKind.RecreateTarget,
        _ => DeviceLossKind.Other,
    };
}

/// <summary>Raised once when a bundle moves to <see cref="DeviceState.Lost"/>.</summary>
public sealed class DeviceLostEventArgs(DeviceState.Lost state) : EventArgs
{
    public DeviceState.Lost State { get; } = state;
}

/// <summary>The adapter that runs a <see cref="GraphicsDevices"/> bundle.</summary>
public sealed record GraphicsAdapterInfo(string Description, uint VendorId, uint DeviceId, long Luid);

/// <summary>Options for <see cref="GraphicsDevices.Create(GraphicsDeviceOptions?)"/>.</summary>
public sealed record GraphicsDeviceOptions
{
    /// <summary>
    /// Enables the Direct3D 11 debug layer and Direct2D debug messages. Needs the Windows
    /// "Graphics Tools" optional feature; creation fails without it. Development only.
    /// </summary>
    public bool EnableDebugLayer { get; init; }

    /// <summary>
    /// Uses WARP, the Windows software rasterizer, instead of the GPU. Its pixels do not depend
    /// on the graphics card or driver, so tests use it for repeatable images; never for the overlay.
    /// </summary>
    public bool UseWarp { get; init; }
}
