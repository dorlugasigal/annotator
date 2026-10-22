using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Interop;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Magnification;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Capture;

/// <summary>
/// Gate 0 S-1 method A for one display: an in-process Magnification API window shows the display at 1x without
/// Annotator's windows, and Windows.Graphics.Capture reads that window into a free-threaded frame pool of two
/// buffers. The host window covers the display but is cloaked, so it never appears on screen or in a full-screen
/// share while DWM still composes it (S-1 case e). The magnifier renders only when invalidated, so the pacer
/// invalidates it once per frame; a new exclude list applies only after the source rectangle changes, so every list
/// change moves the source by one pixel and back in the same call (S-12, S-1). Windows.Graphics.Capture's dirty regions
/// (Windows 11 24H2) are not asked for: for this window every frame reports the whole window as changed, still or not
/// (measured 2026-10-20), so the capture service compares frames itself (<see cref="CaptureServiceOptions.FrameComparer"/>).
/// </summary>
internal sealed unsafe class MagnifierPixelSource : IPixelSource
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int FramePoolBuffers = 2;

    private readonly IPixelSink sink;
    private readonly RECT source;
    private readonly HostWindow host;
    private HWND magnifier;
    private IDirect3DDevice? device;
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private nint poolPointer;
    private TypedEventHandler<Direct3D11CaptureFramePool, object>? frameHandler;
    private TypedEventHandler<GraphicsCaptureItem, object>? closedHandler;
    private int disposed;

    private MagnifierPixelSource(DisplayInfo display, IPixelSink sink)
    {
        this.sink = sink;
        source = new RECT { left = display.Bounds.Left, top = display.Bounds.Top, right = display.Bounds.Right, bottom = display.Bounds.Bottom };
        host = new HostWindow(display.Bounds);
    }

    /// <summary>The magnifier window, for tests and measurements.</summary>
    internal nint Magnifier => magnifier;

    public static MagnifierPixelSource Start(DisplayInfo display, GraphicsDevices devices, ReadOnlySpan<nint> exclusions, IPixelSink sink,
                                             CaptureServiceOptions options)
    {
        MagnifierPixelSource? created = null;
        try
        {
            created = new MagnifierPixelSource(display, sink);
            created.CreateMagnifier(display.Bounds, exclusions);
            created.StartCapture(display.Bounds, devices, options);
            return created;
        }
        catch (CaptureStartException)
        {
            created?.Dispose();
            throw;
        }
        catch (NativeCallException error)
        {
            created?.Dispose();
            throw new CaptureStartException(new CaptureFailure(CaptureFailureKind.MagnifierUnavailable, error.Operation, error));
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            created?.Dispose();
            var kind = error.HResult == E_ACCESSDENIED ? CaptureFailureKind.AccessDenied : CaptureFailureKind.Other;
            throw new CaptureStartException(new CaptureFailure(kind, "Start Windows.Graphics.Capture for the magnifier window", error));
        }
    }

    public void Invalidate()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            PInvoke.InvalidateRect(magnifier, (RECT*)null, false);
        }
    }

    public void ApplyExclusions(ReadOnlySpan<nint> windows)
    {
        fixed (nint* list = windows)
        {
            if (!PInvoke.MagSetWindowFilterList(magnifier, MW_FILTERMODE.MW_FILTERMODE_EXCLUDE, windows.Length, (HWND*)list))
            {
                throw NativeCallException.FromLastError("MagSetWindowFilterList(MW_FILTERMODE_EXCLUDE)");
            }
        }
        // The list applies only after the source rectangle changes (S-12): change it by one pixel and back. The change
        // stays inside the display; a full-display source moved outward did not apply a new list (measured).
        var nudged = new RECT { left = source.left, top = source.top, right = source.right - 1, bottom = source.bottom };
        if (!PInvoke.MagSetWindowSource(magnifier, nudged) || !PInvoke.MagSetWindowSource(magnifier, source))
        {
            throw NativeCallException.FromLastError("MagSetWindowSource (apply the exclude list)");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        if (item is not null && closedHandler is not null)
        {
            item.Closed -= closedHandler;
        }
        if (pool is not null && frameHandler is not null)
        {
            pool.FrameArrived -= frameHandler;
        }
        session?.Dispose();
        pool?.Dispose();
        device?.Dispose();
        session = null;
        pool = null;
        item = null;
        device = null;
        poolPointer = 0;
        host.Destroy();
        magnifier = HWND.Null;
    }

    private void CreateMagnifier(PixelRect bounds, ReadOnlySpan<nint> exclusions)
    {
        var module = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        // Hidden until its source, transform and exclude list are set: a visible magnifier renders its default picture
        // (the screen from its top-left corner, with no exclude list), which capture would take as its first frames.
        magnifier = PInvoke.CreateWindowEx(0, PInvoke.WC_MAGNIFIER, null, WINDOW_STYLE.WS_CHILD, 0, 0, bounds.Width, bounds.Height,
                                           host.Handle, HMENU.Null, module, null);
        if (magnifier.IsNull)
        {
            throw NativeCallException.FromLastError("CreateWindowEx(WC_MAGNIFIER)");
        }
        var transform = default(MAGTRANSFORM);
        transform.v[0] = 1;
        transform.v[4] = 1;
        transform.v[8] = 1;
        if (!PInvoke.MagSetWindowTransform(magnifier, &transform))
        {
            throw NativeCallException.FromLastError("MagSetWindowTransform (1x)");
        }
        // The exclude list is in place before the first frame is captured. A hidden magnifier takes the source but not the
        // list, so the list is applied again once the window shows, and the window is painted with both before capture starts.
        ApplyExclusions(exclusions);
        PInvoke.ShowWindow(magnifier, SHOW_WINDOW_CMD.SW_SHOWNA);
        PInvoke.ShowWindow(host.Handle, SHOW_WINDOW_CMD.SW_SHOWNA);
        ApplyExclusions(exclusions);
        PInvoke.InvalidateRect(magnifier, (RECT*)null, false);
        PInvoke.UpdateWindow(magnifier);
    }

    private void StartCapture(PixelRect bounds, GraphicsDevices devices, CaptureServiceOptions options)
    {
        device = CaptureInterop.CreateDirect3DDevice(devices.DxgiDevice);
        item = CaptureInterop.CreateItemForWindow((nint)host.Handle);
        if (item.Size.Width != bounds.Width || item.Size.Height != bounds.Height)
        {
            throw new CaptureStartException(new CaptureFailure(CaptureFailureKind.Other,
                $"The magnifier window captures at {item.Size.Width} x {item.Size.Height} instead of {bounds.Width} x {bounds.Height}"));
        }
        pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, FramePoolBuffers, item.Size);
        poolPointer = CaptureInterop.FramePoolPointer(pool);
        frameHandler = OnFrameArrived;
        pool.FrameArrived += frameHandler;
        closedHandler = OnItemClosed;
        item.Closed += closedHandler;
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        try
        {
            session.IsBorderRequired = false;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            throw new CaptureStartException(new CaptureFailure(CaptureFailureKind.BorderRequired, "GraphicsCaptureSession.IsBorderRequired = false", error));
        }
        if (session.IsBorderRequired)
        {
            throw new CaptureStartException(new CaptureFailure(CaptureFailureKind.BorderRequired, "GraphicsCaptureSession.IsBorderRequired stayed true"));
        }
        // Windows 11 24H2 added the setting (UniversalApiContract 19); setting it on an earlier Windows would fail the start.
        if (CaptureInterop.HasMinUpdateInterval)
        {
            session.MinUpdateInterval = options.MinUpdateInterval;
        }
        session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            nint pointer = Volatile.Read(ref poolPointer);
            if (pointer == 0 || Volatile.Read(ref disposed) != 0)
            {
                return;
            }
            // Raw calls: the projection would allocate wrapper objects for every frame.
            if (CaptureInterop.TryGetNextFrame(pointer, out nint closable, out nint texture, out int width, out int height, out TimeSpan time))
            {
                sink.OnFrame(closable, texture, width, height, time);
            }
        }
        catch (Exception error)
        {
            sink.OnClosed(new CaptureFailure(CaptureFailureKind.Other, "Read a captured frame", error));
        }
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            sink.OnClosed(new CaptureFailure(CaptureFailureKind.StreamClosed, "GraphicsCaptureItem.Closed"));
        }
    }

    /// <summary>
    /// The magnifier's parent: a borderless, cloaked, click-through tool window over the display. It never paints
    /// itself, never activates and never follows a DPI change.
    /// </summary>
    private sealed class HostWindow : NativeWindow
    {
        public HostWindow(PixelRect bounds)
        {
            const WINDOW_EX_STYLE exStyle = WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_LAYERED |
                WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
            using (PerMonitorDpiScope.Enter())
            {
                CreateHandle("Annotator.Platform.CaptureHost", exStyle, WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_CLIPCHILDREN,
                             bounds.Left, bounds.Top, bounds.Width, bounds.Height, HWND.Null, "CreateWindowEx (capture host)");
            }
            try
            {
                if (!PInvoke.SetLayeredWindowAttributes(Handle, new COLORREF(0), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
                {
                    throw NativeCallException.FromLastError("SetLayeredWindowAttributes (capture host)");
                }
                BOOL cloak = true;
                NativeCallException.ThrowIfFailed(
                    PInvoke.DwmSetWindowAttribute(Handle, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &cloak, (uint)sizeof(BOOL)),
                    "DwmSetWindowAttribute(DWMWA_CLOAK) (capture host)");
            }
            catch
            {
                Destroy();
                throw;
            }
        }

        public void Destroy() => DestroyHandle("DestroyWindow (capture host)");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
                case PInvoke.WM_ERASEBKGND:
                    return (LRESULT)1;
                case PInvoke.WM_NCHITTEST:
                    return (LRESULT)PInvoke.HTTRANSPARENT;
                case PInvoke.WM_MOUSEACTIVATE:
                    return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                case PInvoke.WM_DPICHANGED:
                    return default;
                default:
                    return base.WndProc(message, wParam, lParam);
            }
        }
    }
}
