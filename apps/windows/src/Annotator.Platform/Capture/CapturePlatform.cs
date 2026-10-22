using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Interop;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Annotator.Platform.Capture;

/// <summary>The native pieces of capture: the Magnification API, Windows.Graphics.Capture and session queries. Tests replace them.</summary>
internal interface ICapturePlatform
{
    /// <summary>Whether Windows.Graphics.Capture works on this system.</summary>
    bool IsSupported();

    /// <summary>Whether the session is locked now; false when Windows cannot tell.</summary>
    bool IsSessionLocked();

    IReadOnlyList<DisplayInfo> GetDisplays();

    GraphicsDevices CreateDevices(GraphicsDeviceOptions? options);

    /// <summary>Runs on the capture thread before the first source starts. Throws <see cref="CaptureStartException"/>.</summary>
    void Initialize();

    /// <summary>Runs on the capture thread after the last source stopped.</summary>
    void Uninitialize();

    /// <summary>
    /// Starts one display's pixel source on the capture thread with <paramref name="exclusions"/> left out before
    /// anything is captured. Throws <see cref="CaptureStartException"/>.
    /// </summary>
    IPixelSource Start(DisplayInfo display, GraphicsDevices devices, ReadOnlySpan<nint> exclusions, IPixelSink sink, CaptureServiceOptions options);
}

/// <summary>One display's running pixel source.</summary>
internal interface IPixelSource : IDisposable
{
    /// <summary>Asks for a new frame. Called on the pacer thread; it may race with <see cref="IDisposable.Dispose"/>.</summary>
    void Invalidate();

    /// <summary>Leaves exactly these windows out from now on (capture thread). Throws <see cref="NativeCallException"/>.</summary>
    void ApplyExclusions(ReadOnlySpan<nint> windows);
}

/// <summary>Receives what a pixel source produces, on any thread.</summary>
internal interface IPixelSink
{
    /// <summary>
    /// A frame arrived. The sink owns both references: <paramref name="closable"/> (the frame's IClosable, or 0) and
    /// <paramref name="texture"/> (its ID3D11Texture2D); release them with <see cref="FrameReferences.Release"/>.
    /// </summary>
    void OnFrame(nint closable, nint texture, int width, int height, TimeSpan time);

    /// <summary>The source stopped on its own.</summary>
    void OnClosed(CaptureFailure failure);
}

internal sealed class CaptureStartException(CaptureFailure failure) : Exception(failure.Operation, failure.Error)
{
    public CaptureFailure Failure { get; } = failure;
}

/// <summary>Releases a captured frame: closes it (which returns its buffer to the frame pool) and drops its texture.</summary>
internal static unsafe class FrameReferences
{
    private const int CloseSlot = 6; // IClosable::Close, after IUnknown and IInspectable

    public static void Release(nint closable, nint texture)
    {
        if (closable != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)(*(void***)closable)[CloseSlot])(closable);
            Marshal.Release(closable);
        }
        if (texture != 0)
        {
            Marshal.Release(texture);
        }
    }
}

/// <summary>The real platform: Gate 0 S-1 method A.</summary>
internal sealed unsafe class WindowsCapturePlatform : ICapturePlatform
{
    public static readonly WindowsCapturePlatform Instance = new();

    private WindowsCapturePlatform()
    {
    }

    public bool IsSupported() => GraphicsCaptureSession.IsSupported();

    public bool IsSessionLocked()
    {
        Windows.Win32.Foundation.PWSTR buffer = default;
        uint size;
        if (!Windows.Win32.PInvoke.WTSQuerySessionInformation(Windows.Win32.Foundation.HANDLE.Null, Windows.Win32.PInvoke.WTS_CURRENT_SESSION,
                                                             Windows.Win32.System.RemoteDesktop.WTS_INFO_CLASS.WTSSessionInfoEx, &buffer, &size))
        {
            return false;
        }
        try
        {
            if (size < sizeof(Windows.Win32.System.RemoteDesktop.WTSINFOEXW))
            {
                return false;
            }
            var info = (Windows.Win32.System.RemoteDesktop.WTSINFOEXW*)buffer.Value;
            // WTS_SESSIONSTATE_LOCK is 0 and WTS_SESSIONSTATE_UNLOCK is 1 on Windows 10 and later.
            return info->Level == 1 && info->Data.WTSInfoExLevel1.SessionFlags == 0;
        }
        finally
        {
            Windows.Win32.PInvoke.WTSFreeMemory(buffer.Value);
        }
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() => DisplayEnumerator.GetDisplays();

    public GraphicsDevices CreateDevices(GraphicsDeviceOptions? options) => GraphicsDevices.Create(options);

    public void Initialize()
    {
        if (!Windows.Win32.PInvoke.MagInitialize())
        {
            throw new CaptureStartException(new CaptureFailure(CaptureFailureKind.MagnifierUnavailable, "MagInitialize"));
        }
    }

    public void Uninitialize() => Windows.Win32.PInvoke.MagUninitialize();

    public IPixelSource Start(DisplayInfo display, GraphicsDevices devices, ReadOnlySpan<nint> exclusions, IPixelSink sink, CaptureServiceOptions options)
    {
        // A packaged app captures only with the person's consent, which the app asks for; without it Windows would draw the
        // yellow border (WIN-CAP-04). Never waits, so the capture thread keeps serving exclusions.
        CaptureAccess.Require();
        return MagnifierPixelSource.Start(display, devices, exclusions, sink, options);
    }
}

/// <summary>
/// Hand-written Windows.Graphics.Capture interop, AOT-safe (as in Gate 0 S-1): capture items for windows, the
/// IDirect3DDevice wrapper and raw vtable calls for the per-frame path, which allocate nothing.
/// </summary>
internal static unsafe partial class CaptureInterop
{
    public static readonly Guid IID_IClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    public static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // IDirect3D11CaptureFramePool after IUnknown and IInspectable: Recreate, TryGetNextFrame, ... (Windows.winmd order).
    private const int TryGetNextFrameSlot = 7;

    // IDirect3D11CaptureFrame: get_Surface, get_SystemRelativeTime, get_ContentSize.
    private const int SurfaceSlot = 6;
    private const int SystemRelativeTimeSlot = 7;
    private const int ContentSizeSlot = 8;

    /// <summary>
    /// Whether <see cref="GraphicsCaptureSession.MinUpdateInterval"/> exists (Windows 11 24H2, UniversalApiContract 19). The
    /// app runs from Windows 11 22H2, where setting it would throw.
    /// </summary>
    public static readonly bool HasMinUpdateInterval =
        Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval");

    /// <summary>IGraphicsCaptureItemInterop::CreateForWindow.</summary>
    public static GraphicsCaptureItem CreateItemForWindow(nint window)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        NativeCallException.ThrowIfFailed(WindowsCreateString(name, name.Length, out nint hstring), "WindowsCreateString");
        nint factory;
        try
        {
            NativeCallException.ThrowIfFailed(RoGetActivationFactory(hstring, IID_IGraphicsCaptureItemInterop, out factory),
                                              "RoGetActivationFactory(IGraphicsCaptureItemInterop)");
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
        try
        {
            Guid iid = IID_IGraphicsCaptureItem;
            nint item = 0;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)factory)[3];
            NativeCallException.ThrowIfFailed(create(factory, window, &iid, &item), "IGraphicsCaptureItemInterop::CreateForWindow");
            try
            {
                return GraphicsCaptureItem.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>The WinRT Direct3D device that Windows.Graphics.Capture needs, over a borrowed IDXGIDevice.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(nint dxgiDevice)
    {
        NativeCallException.ThrowIfFailed(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out nint inspectable), "CreateDirect3D11DeviceFromDXGIDevice");
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    /// <summary>The default interface (IDirect3D11CaptureFramePool*) of a projected pool, borrowed while the pool lives.</summary>
    public static nint FramePoolPointer(Direct3D11CaptureFramePool pool) => ((IWinRTObject)pool).NativeObject.ThisPtr;

    /// <summary>
    /// IDirect3D11CaptureFramePool::TryGetNextFrame without projection objects. On success the caller owns
    /// <paramref name="closable"/> and <paramref name="texture"/>; returns false when there is no frame or a call failed.
    /// </summary>
    public static bool TryGetNextFrame(nint pool, out nint closable, out nint texture, out int width, out int height, out TimeSpan time)
    {
        closable = 0;
        texture = 0;
        width = 0;
        height = 0;
        time = default;
        nint frame = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(void***)pool)[TryGetNextFrameSlot])(pool, &frame) < 0 || frame == 0)
        {
            return false;
        }
        nint surface = 0, access = 0, found = 0, frameClosable = 0;
        try
        {
            long ticks;
            Windows.Graphics.SizeInt32 size;
            void** vtable = *(void***)frame;
            if (((delegate* unmanaged[Stdcall]<nint, Windows.Graphics.SizeInt32*, int>)vtable[ContentSizeSlot])(frame, &size) < 0
                || ((delegate* unmanaged[Stdcall]<nint, long*, int>)vtable[SystemRelativeTimeSlot])(frame, &ticks) < 0
                || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)vtable[SurfaceSlot])(frame, &surface) < 0 || surface == 0
                || Marshal.QueryInterface(surface, in IID_IDirect3DDxgiInterfaceAccess, out access) < 0)
            {
                return false;
            }
            Guid iid = IID_ID3D11Texture2D;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)access)[3])(access, &iid, &found) < 0 || found == 0
                || Marshal.QueryInterface(frame, in IID_IClosable, out frameClosable) < 0)
            {
                return false;
            }
            closable = frameClosable;
            texture = found;
            frameClosable = 0;
            found = 0;
            width = size.Width;
            height = size.Height;
            time = TimeSpan.FromTicks(ticks);
            return true;
        }
        finally
        {
            if (found != 0)
            {
                Marshal.Release(found);
            }
            if (frameClosable != 0)
            {
                Marshal.Release(frameClosable);
            }
            if (access != 0)
            {
                Marshal.Release(access);
            }
            if (surface != 0)
            {
                Marshal.Release(surface);
            }
            if (closable == 0)
            {
                // Not handed out: close the frame so its buffer returns to the pool.
                nint failedClosable = 0;
                if (Marshal.QueryInterface(frame, in IID_IClosable, out failedClosable) >= 0)
                {
                    FrameReferences.Release(failedClosable, 0);
                }
            }
            Marshal.Release(frame);
        }
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
