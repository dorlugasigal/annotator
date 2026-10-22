using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Annotator.Platform.Capture;

internal static unsafe partial class CaptureInterop
{
    /// <summary>IGraphicsCaptureItemInterop::CreateForMonitor: the whole composed output of one monitor.</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(nint monitor)
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
            // IGraphicsCaptureItemInterop after IUnknown: CreateForWindow, then CreateForMonitor.
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(void***)factory)[4];
            NativeCallException.ThrowIfFailed(create(factory, monitor, &iid, &item), "IGraphicsCaptureItemInterop::CreateForMonitor");
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
}
