using System.Runtime.InteropServices;
using Annotator.Platform;
using Microsoft.UI.Composition;
using WinRT;

namespace Annotator.App.Introduction;

/// <summary>
/// WinUI's compositor (Microsoft.UI.Composition) interop for Direct2D drawing surfaces, written by hand for Native AOT like
/// Annotator.Platform's CompositionInterop for the system compositor (Gate 0 S-12). The lifted interfaces have their own
/// IIDs and vtable slots (Microsoft.UI.Composition.Interop.h). Use it on the window's thread.
/// </summary>
internal static unsafe class LiftedComposition
{
    private static readonly Guid IID_ICompositorInterop = new("FAB19398-6D19-4D8A-B752-8F096C396069");
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("2D6355C2-AD57-4EAE-92E4-4C3EFF65D578");
    private static readonly Guid IID_ID2D1DeviceContext = new("E8F7FE7A-191C-466D-AD95-975678BDA998");

    // Vtable slots after the three IUnknown methods.
    private const int CreateGraphicsDeviceSlot = 3;
    private const int BeginDrawSlot = 3;
    private const int EndDrawSlot = 4;

    /// <summary>ICompositorInterop::CreateGraphicsDevice from an ID2D1Device; the pointer is borrowed.</summary>
    public static CompositionGraphicsDevice CreateGraphicsDevice(Compositor compositor, nint d2dDevice)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        nint interop = Query(((IWinRTObject)compositor).NativeObject.ThisPtr, IID_ICompositorInterop, "QueryInterface(ICompositorInterop)");
        try
        {
            nint device = 0;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(interop, CreateGraphicsDeviceSlot);
            Check(create(interop, d2dDevice, &device), "ICompositorInterop::CreateGraphicsDevice");
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

    /// <summary>The surface's ICompositionDrawingSurfaceInterop; release it with <see cref="Marshal.Release"/>.</summary>
    public static nint DrawingInterop(CompositionDrawingSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        try
        {
            return Query(((IWinRTObject)surface).NativeObject.ThisPtr, IID_ICompositionDrawingSurfaceInterop,
                "QueryInterface(ICompositionDrawingSurfaceInterop)");
        }
        finally
        {
            GC.KeepAlive(surface);
        }
    }

    /// <summary>
    /// BeginDraw for the whole <paramref name="width"/> x <paramref name="height"/> surface. Returns the HRESULT; on success
    /// <paramref name="context"/> is an ID2D1DeviceContext to release after <see cref="EndDraw"/>, and the offset locates the
    /// surface inside its target.
    /// </summary>
    public static int BeginDraw(nint interop, int width, int height, out nint context, out int offsetX, out int offsetY)
    {
        var rect = new NativeRect(0, 0, width, height);
        Guid iid = IID_ID2D1DeviceContext;
        nint updateObject = 0;
        var offset = default(NativePoint);
        var beginDraw = (delegate* unmanaged[Stdcall]<nint, NativeRect*, Guid*, nint*, NativePoint*, int>)Slot(interop, BeginDrawSlot);
        int result = beginDraw(interop, &rect, &iid, &updateObject, &offset);
        context = updateObject;
        offsetX = offset.X;
        offsetY = offset.Y;
        return result;
    }

    public static int EndDraw(nint interop) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(interop, EndDrawSlot))(interop);

    private static nint Query(nint unknown, Guid iid, string operation)
    {
        Check(Marshal.QueryInterface(unknown, in iid, out nint result), operation);
        return result;
    }

    private static void Check(int hresult, string operation)
    {
        if (hresult < 0)
        {
            throw new NativeCallException(operation, hresult);
        }
    }

    private static void* Slot(nint instance, int slot) => (*(void***)instance)[slot];

    private readonly record struct NativePoint(int X, int Y);

    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
}
