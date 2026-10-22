using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Rendering.Interop;
using Windows.UI.Composition;
using WinRT;

namespace Annotator.Rendering.Presentation;

/// <summary>
/// ICompositionDrawingSurfaceInterop for one surface, queried once and called through its vtable, so a frame's
/// BeginDraw and EndDraw allocate nothing (Annotator.Platform's <c>CompositionInterop.BeginDraw</c> creates a session
/// object per call). Hand-written for Native AOT, like the rest of the composition interop (Gate 0 S-12).
/// </summary>
internal sealed unsafe class SurfaceInterop : IDisposable
{
    private static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    private static readonly Guid IID_ID2D1DeviceContext = Windows.Win32.Graphics.Direct2D.ID2D1DeviceContext.IID_Guid;

    private const int BeginDrawSlot = 3;
    private const int EndDrawSlot = 4;

    private nint interop;

    public SurfaceInterop(CompositionDrawingSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var unknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Native.Check(Marshal.QueryInterface(unknown, in IID_ICompositionDrawingSurfaceInterop, out interop), "QueryInterface(ICompositionDrawingSurfaceInterop)");
        GC.KeepAlive(surface);
    }

    /// <summary>
    /// BeginDraw for <paramref name="update"/> (surface pixels). Returns the HRESULT; on success
    /// <paramref name="context"/> is an ID2D1DeviceContext the caller releases after <see cref="EndDraw"/>, and
    /// <paramref name="offsetX"/>, <paramref name="offsetY"/> locate the update rectangle inside its target.
    /// </summary>
    public int BeginDraw(in PixelRect update, out nint context, out int offsetX, out int offsetY)
    {
        ObjectDisposedException.ThrowIf(interop == 0, this);
        var rect = update;
        Guid iid = IID_ID2D1DeviceContext;
        nint updateObject = 0;
        var offset = default(NativePoint);
        var beginDraw = (delegate* unmanaged[Stdcall]<nint, PixelRect*, Guid*, nint*, NativePoint*, int>)(*(void***)interop)[BeginDrawSlot];
        var result = beginDraw(interop, &rect, &iid, &updateObject, &offset);
        context = updateObject;
        offsetX = offset.X;
        offsetY = offset.Y;
        return result;
    }

    public int EndDraw()
    {
        ObjectDisposedException.ThrowIf(interop == 0, this);
        var endDraw = (delegate* unmanaged[Stdcall]<nint, int>)(*(void***)interop)[EndDrawSlot];
        return endDraw(interop);
    }

    public void Dispose()
    {
        if (interop != 0)
        {
            Marshal.Release(interop);
            interop = 0;
        }
    }

    private readonly record struct NativePoint(int X, int Y);
}
