using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Annotator.Platform.Displays;

/// <summary>Lists the connected monitors in physical pixels with their effective DPI.</summary>
public static unsafe class DisplayEnumerator
{
    /// <summary>
    /// Returns every monitor, primary first, then left to right and top to bottom. Coordinates
    /// are physical pixels whatever the calling thread's DPI awareness. Throws
    /// <see cref="NativeCallException"/> if Windows cannot describe a monitor.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        var context = new Enumeration();
        GCHandle handle = GCHandle.Alloc(context);
        try
        {
            if (!PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle)) && context.Failure is null)
            {
                throw NativeCallException.FromLastError("EnumDisplayMonitors");
            }
        }
        finally
        {
            handle.Free();
        }

        if (context.Failure is not null)
        {
            throw context.Failure;
        }
        return [.. context.Displays
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.Bounds.Left)
            .ThenBy(display => display.Bounds.Top)];
    }

    private static DisplayInfo Describe(HMONITOR monitor)
    {
        var info = new MONITORINFOEXW();
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
        {
            throw NativeCallException.FromLastError("GetMonitorInfo");
        }
        NativeCallException.ThrowIfFailed(PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpi, out _), "GetDpiForMonitor(MDT_EFFECTIVE_DPI)");

        ReadOnlySpan<char> device = info.szDevice.AsReadOnlySpan();
        int end = device.IndexOf('\0');
        return new DisplayInfo(
            (nint)monitor,
            new string(end < 0 ? device : device[..end]),
            ToPixels(info.monitorInfo.rcMonitor),
            ToPixels(info.monitorInfo.rcWork),
            dpi,
            (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0);
    }

    private static PixelRect ToPixels(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnMonitor(HMONITOR monitor, HDC dc, RECT* clip, LPARAM data)
    {
        var context = (Enumeration)GCHandle.FromIntPtr(data.Value).Target!;
        try
        {
            context.Displays.Add(Describe(monitor));
            return true;
        }
        catch (Exception exception)
        {
            context.Failure = exception;
            return false;
        }
    }

    private sealed class Enumeration
    {
        public List<DisplayInfo> Displays { get; } = [];

        public Exception? Failure { get; set; }
    }
}
