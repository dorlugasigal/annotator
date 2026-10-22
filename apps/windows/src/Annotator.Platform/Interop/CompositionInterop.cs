using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Annotator.Platform.Interop;

/// <summary>
/// Windows.UI.Composition interop for Win32 windows and DirectX devices, written by hand.
/// CsWin32 0.3.363 generates these interfaces with WinRT objects behind a custom marshaler,
/// which Native AOT does not support (IL2057, Gate 0 S-12), so the vtable slots are called
/// directly. Every call checks its HRESULT; returned projection objects hold their own
/// reference and every temporary native reference is released.
/// </summary>
public static unsafe class CompositionInterop
{
    private static readonly Guid IID_ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");
    private static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");

    // Vtable slots after the three IUnknown methods.
    private const int CreateDesktopWindowTargetSlot = 3; // ICompositorDesktopInterop
    private const int CreateCompositionSurfaceForSwapChainSlot = 4; // ICompositorInterop
    private const int CreateGraphicsDeviceSlot = 5; // ICompositorInterop
    private const int BeginDrawSlot = 3; // ICompositionDrawingSurfaceInterop
    private const int EndDrawSlot = 4; // ICompositionDrawingSurfaceInterop

    /// <summary>
    /// ICompositorDesktopInterop::CreateDesktopWindowTarget. The window should have
    /// WS_EX_NOREDIRECTIONBITMAP so composition is its only content.
    /// </summary>
    public static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, nint window, bool isTopmost)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (window == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(window));
        }

        nint interop = ComPtr.QueryInterface(ThisPtr(compositor), IID_ICompositorDesktopInterop, "QueryInterface(ICompositorDesktopInterop)");
        try
        {
            nint target = 0;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)VtableSlot(interop, CreateDesktopWindowTargetSlot);
            NativeCallException.ThrowIfFailed(create(interop, window, isTopmost ? 1 : 0, &target), "ICompositorDesktopInterop::CreateDesktopWindowTarget");
            try
            {
                return DesktopWindowTarget.FromAbi(target);
            }
            finally
            {
                Marshal.Release(target);
            }
        }
        finally
        {
            Marshal.Release(interop);
            GC.KeepAlive(compositor);
        }
    }

    /// <summary>
    /// ICompositorInterop::CreateGraphicsDevice from an ID2D1Device (for Direct2D drawing surfaces)
    /// or an ID3D11Device. The pointer is borrowed; composition takes its own reference.
    /// </summary>
    public static CompositionGraphicsDevice CreateGraphicsDevice(Compositor compositor, nint renderingDevice)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (renderingDevice == 0)
        {
            throw new ArgumentException("A Direct2D or Direct3D device is required.", nameof(renderingDevice));
        }

        nint interop = ComPtr.QueryInterface(ThisPtr(compositor), IID_ICompositorInterop, "QueryInterface(ICompositorInterop)");
        try
        {
            nint device = 0;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VtableSlot(interop, CreateGraphicsDeviceSlot);
            NativeCallException.ThrowIfFailed(create(interop, renderingDevice, &device), "ICompositorInterop::CreateGraphicsDevice");
            try
            {
                return CompositionGraphicsDevice.FromAbi(device);
            }
            finally
            {
                Marshal.Release(device);
            }
        }
        finally
        {
            Marshal.Release(interop);
            GC.KeepAlive(compositor);
        }
    }

    /// <summary>
    /// ICompositorInterop::CreateCompositionSurfaceForSwapChain for a swap chain created with
    /// CreateSwapChainForComposition. The pointer is borrowed.
    /// </summary>
    public static ICompositionSurface CreateCompositionSurfaceForSwapChain(Compositor compositor, nint swapChain)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (swapChain == 0)
        {
            throw new ArgumentException("A swap chain is required.", nameof(swapChain));
        }

        nint interop = ComPtr.QueryInterface(ThisPtr(compositor), IID_ICompositorInterop, "QueryInterface(ICompositorInterop)");
        try
        {
            nint surface = 0;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VtableSlot(interop, CreateCompositionSurfaceForSwapChainSlot);
            NativeCallException.ThrowIfFailed(create(interop, swapChain, &surface), "ICompositorInterop::CreateCompositionSurfaceForSwapChain");
            try
            {
                return MarshalInterface<ICompositionSurface>.FromAbi(surface);
            }
            finally
            {
                Marshal.Release(surface);
            }
        }
        finally
        {
            Marshal.Release(interop);
            GC.KeepAlive(compositor);
        }
    }

    /// <summary>
    /// ICompositionDrawingSurfaceInterop::BeginDraw. <paramref name="updateObjectIid"/> is usually
    /// the IID of ID2D1DeviceContext (for a surface from a Direct2D graphics device) or IDXGISurface.
    /// Draw at <see cref="CompositionDrawingSession.Offset"/>, then end the session; ending it
    /// calls EndDraw and reports a failure, such as a lost device, as <see cref="NativeCallException"/>.
    /// </summary>
    public static CompositionDrawingSession BeginDraw(CompositionDrawingSurface surface, in Guid updateObjectIid, RectInt32? updateRect = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        NativeRect rect = default;
        NativeRect* rectPointer = null;
        if (updateRect is RectInt32 r)
        {
            if (r.Width <= 0 || r.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(updateRect), "The update rectangle must not be empty.");
            }
            rect = new NativeRect(r.X, r.Y, r.X + r.Width, r.Y + r.Height);
            rectPointer = &rect;
        }

        nint interop = ComPtr.QueryInterface(ThisPtr(surface), IID_ICompositionDrawingSurfaceInterop, "QueryInterface(ICompositionDrawingSurfaceInterop)");
        try
        {
            Guid iid = updateObjectIid;
            nint updateObject = 0;
            NativePoint offset = default;
            var beginDraw = (delegate* unmanaged[Stdcall]<nint, NativeRect*, Guid*, nint*, NativePoint*, int>)VtableSlot(interop, BeginDrawSlot);
            NativeCallException.ThrowIfFailed(beginDraw(interop, rectPointer, &iid, &updateObject, &offset), "ICompositionDrawingSurfaceInterop::BeginDraw");
            var session = new CompositionDrawingSession(interop, updateObject, new PointInt32(offset.X, offset.Y));
            interop = 0; // The session owns it now.
            return session;
        }
        finally
        {
            if (interop != 0)
            {
                Marshal.Release(interop);
            }
            GC.KeepAlive(surface);
        }
    }

    /// <summary>EndDraw on an ICompositionDrawingSurfaceInterop pointer; returns the HRESULT.</summary>
    public static int EndDraw(nint surfaceInterop)
    {
        var endDraw = (delegate* unmanaged[Stdcall]<nint, int>)VtableSlot(surfaceInterop, EndDrawSlot);
        return endDraw(surfaceInterop);
    }

    /// <summary>
    /// The surface's ICompositionDrawingSurfaceInterop, for drawing every frame without allocating: the caller owns the
    /// returned reference and keeps the surface alive while it uses it.
    /// </summary>
    public static nint DrawingSurfaceInterop(CompositionDrawingSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        try
        {
            return ComPtr.QueryInterface(ThisPtr(surface), IID_ICompositionDrawingSurfaceInterop, "QueryInterface(ICompositionDrawingSurfaceInterop)");
        }
        finally
        {
            GC.KeepAlive(surface);
        }
    }

    /// <summary>
    /// ICompositionDrawingSurfaceInterop::BeginDraw of the whole surface on a pointer from <see cref="DrawingSurfaceInterop"/>,
    /// without allocating. On success the caller owns <paramref name="updateObject"/> and calls <see cref="EndDraw"/>.
    /// Returns the HRESULT.
    /// </summary>
    public static int BeginDraw(nint surfaceInterop, in Guid updateObjectIid, out nint updateObject, out PointInt32 offset)
    {
        Guid iid = updateObjectIid;
        nint target = 0;
        NativePoint at = default;
        var beginDraw = (delegate* unmanaged[Stdcall]<nint, NativeRect*, Guid*, nint*, NativePoint*, int>)VtableSlot(surfaceInterop, BeginDrawSlot);
        int result = beginDraw(surfaceInterop, null, &iid, &target, &at);
        updateObject = result < 0 ? 0 : target;
        offset = new PointInt32(at.X, at.Y);
        return result;
    }

    /// <summary>The default interface pointer of a projected object. Borrowed: keep the object alive while using it.</summary>
    private static nint ThisPtr(object projected) => ((IWinRTObject)projected).NativeObject.ThisPtr;

    private static void* VtableSlot(nint instance, int slot) => (*(void***)instance)[slot];

    private readonly record struct NativePoint(int X, int Y);

    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
}

/// <summary>
/// One BeginDraw/EndDraw pair on a composition drawing surface. Use it on the thread that began
/// it; disposing ends it.
/// </summary>
public sealed class CompositionDrawingSession : IDisposable
{
    private nint _surfaceInterop;
    private nint _updateObject;

    internal CompositionDrawingSession(nint surfaceInterop, nint updateObject, PointInt32 offset)
    {
        _surfaceInterop = surfaceInterop;
        _updateObject = updateObject;
        Offset = offset;
    }

    /// <summary>
    /// The drawing target requested in BeginDraw, such as an ID2D1DeviceContext pointer.
    /// Borrowed: valid until the session ends.
    /// </summary>
    public nint UpdateObject
    {
        get
        {
            ObjectDisposedException.ThrowIf(_surfaceInterop == 0, this);
            return _updateObject;
        }
    }

    /// <summary>Where the requested area starts inside <see cref="UpdateObject"/>, in pixels.</summary>
    public PointInt32 Offset { get; }

    public bool IsEnded => _surfaceInterop == 0;

    /// <summary>Calls EndDraw. Throws <see cref="NativeCallException"/> if composition rejects the frame.</summary>
    public void EndDraw()
    {
        ObjectDisposedException.ThrowIf(_surfaceInterop == 0, this);
        ComPtr.Release(ref _updateObject);
        nint interop = _surfaceInterop;
        _surfaceInterop = 0;
        int hr;
        try
        {
            hr = CompositionInterop.EndDraw(interop);
        }
        finally
        {
            Marshal.Release(interop);
        }
        NativeCallException.ThrowIfFailed(hr, "ICompositionDrawingSurfaceInterop::EndDraw");
    }

    public void Dispose()
    {
        if (_surfaceInterop != 0)
        {
            EndDraw();
        }
    }
}
