using System.Runtime.InteropServices;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Interop;
using Microsoft.Win32.SafeHandles;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.Rendering.Presentation;

/// <summary>
/// A layer on a composition swap chain: flip model with two buffers, premultiplied alpha, a frame-latency waitable
/// object with maximum latency 1, and Present1 with the frame's dirty rectangles. A flip-model back buffer holds the
/// frame from two presents ago, so each frame redraws its own damage and the previous frame's.
/// </summary>
/// <remarks>
/// The layer never presents without a free frame, so Present never blocks the overlay thread, even while the display
/// is off. Free frames arrive as signals of the waitable object, which a thread-pool registration turns into a flag and,
/// when an update waits, into one dispatcher work item; nothing polls.
/// </remarks>
internal sealed unsafe class SwapChainLayer : PresentationLayer
{
    private readonly CompositionSurfaceBrush brush;
    private readonly ICompositionSurface surface;
    private readonly DamageRegion previous = new();
    private readonly DamageRegion drawn = new();
    private readonly RECT[] dirty = new RECT[DamageRegion.Capacity];
    private readonly FrameSlot slot;
    private IDXGISwapChain2* swapChain;
    private ID2D1DeviceContext* context;
    private ID2D1Bitmap1* target;
    private HANDLE waitable;
    private bool frameTaken;
    private int presented;

    /// <param name="frameReady">Runs on <paramref name="queue"/>'s thread when a deferred frame can be drawn.</param>
    public SwapChainLayer(Compositor compositor, GraphicsDevices devices, int width, int height, SystemDispatcherQueue queue,
                          Action<PresentationLayer> frameReady)
        : base(compositor, width, height)
    {
        try
        {
            swapChain = Create(devices, width, height);
            Native.Check(swapChain->SetMaximumFrameLatency(1), "IDXGISwapChain2::SetMaximumFrameLatency");
            waitable = swapChain->GetFrameLatencyWaitableObject();
            if (waitable == HANDLE.Null)
            {
                throw new NativeCallException("IDXGISwapChain2::GetFrameLatencyWaitableObject", unchecked((int)0x80004005));
            }
            var device = (ID2D1Device*)devices.D2DDevice;
            ID2D1DeviceContext* created;
            Native.Check(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &created), "ID2D1Device::CreateDeviceContext");
            context = created;
            Guid surfaceIid = IDXGISurface.IID_Guid;
            void* buffer;
            Native.Check(swapChain->GetBuffer(0, &surfaceIid, &buffer), "IDXGISwapChain::GetBuffer");
            try
            {
                var properties = new D2D1_BITMAP_PROPERTIES1
                {
                    pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                    dpiX = 96,
                    dpiY = 96,
                    bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
                };
                ID2D1Bitmap1* bitmap;
                Native.Check(context->CreateBitmapFromDxgiSurface((IDXGISurface*)buffer, &properties, &bitmap), "ID2D1DeviceContext::CreateBitmapFromDxgiSurface");
                target = bitmap;
            }
            finally
            {
                var unknown = (IUnknown*)buffer;
                Native.Release(ref unknown);
            }
            surface = Annotator.Platform.Interop.CompositionInterop.CreateCompositionSurfaceForSwapChain(compositor, (nint)swapChain);
            brush = Brush(compositor, surface);
            Visual.Brush = brush;
            slot = new FrameSlot(waitable, queue, () => frameReady(this));
        }
        catch
        {
            brush?.Dispose();
            (surface as IDisposable)?.Dispose();
            ReleaseNative();
            base.Dispose();
            throw;
        }
    }

    public override LayerKind Kind => LayerKind.SwapChain;

    public override bool TryTakeFrame()
    {
        if (!frameTaken)
        {
            frameTaken = slot.TryTake();
        }
        return frameTaken;
    }

    public override bool DeferFrame()
    {
        if (!frameTaken)
        {
            frameTaken = slot.Defer();
        }
        return frameTaken;
    }

    public override int Draw(DamageRegion damage, double scale, bool live, ILayerContent content)
    {
        if (!frameTaken)
        {
            throw new InvalidOperationException("Take a free frame of the swap chain before drawing.");
        }
        frameTaken = false;
        drawn.Clear();
        if (presented < 2)
        {
            drawn.AddFull(Width, Height);
        }
        else
        {
            drawn.Add(damage, Width, Height);
            drawn.Add(previous, Width, Height);
        }
        context->SetTarget((ID2D1Image*)target);
        context->BeginDraw();
        HRESULT ended;
        try
        {
            foreach (var rect in drawn.Rects)
            {
                Clear(context, rect);
                content.Render((nint)context, new DrawArea(scale, 0, 0, rect, live));
            }
        }
        finally
        {
            ended = context->EndDraw(null, null);
            context->SetTarget(null);
        }
        if (ended.Failed)
        {
            return ended.Value;
        }
        var full = presented < 2 || damage.IsFull || damage.IsEmpty;
        var parameters = default(DXGI_PRESENT_PARAMETERS);
        if (!full)
        {
            var count = 0;
            foreach (var rect in damage.Rects)
            {
                dirty[count++] = new RECT { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
            }
            parameters.DirtyRectsCount = (uint)count;
        }
        HRESULT result;
        fixed (RECT* rects = dirty)
        {
            parameters.pDirtyRects = full ? null : rects;
            result = swapChain->Present1(0, 0, &parameters);
        }
        if (result.Failed)
        {
            return result.Value;
        }
        presented++;
        previous.CopyFrom(damage);
        return 0;
    }

    public override void Dispose()
    {
        slot?.Dispose();
        brush?.Dispose();
        (surface as IDisposable)?.Dispose();
        ReleaseNative();
        base.Dispose();
    }

    private void ReleaseNative()
    {
        Native.Release(ref target);
        Native.Release(ref context);
        if (waitable != HANDLE.Null)
        {
            PInvoke.CloseHandle(waitable);
            waitable = HANDLE.Null;
        }
        Native.Release(ref swapChain);
    }

    private static IDXGISwapChain2* Create(GraphicsDevices devices, int width, int height)
    {
        var dxgiDevice = (IDXGIDevice*)devices.DxgiDevice;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        IDXGISwapChain1* swapChain = null;
        try
        {
            Native.Check(dxgiDevice->GetAdapter(&adapter), "IDXGIDevice::GetAdapter");
            Guid factoryIid = IDXGIFactory2.IID_Guid;
            void* parent;
            Native.Check(adapter->GetParent(&factoryIid, &parent), "IDXGIAdapter::GetParent(IDXGIFactory2)");
            factory = (IDXGIFactory2*)parent;
            var description = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
                Flags = DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT,
            };
            Native.Check(factory->CreateSwapChainForComposition((IUnknown*)devices.D3D11Device, &description, null, &swapChain),
                         "IDXGIFactory2::CreateSwapChainForComposition");
            return Native.Query<IDXGISwapChain2>(swapChain, IDXGISwapChain2.IID_Guid, "QueryInterface(IDXGISwapChain2)");
        }
        finally
        {
            Native.Release(ref swapChain);
            Native.Release(ref factory);
            Native.Release(ref adapter);
        }
    }

    /// <summary>
    /// Turns signals of the frame-latency waitable object into a "free frame" flag. A thread-pool registration waits on
    /// a duplicate of the handle (the registration keeps it open until it is unregistered, so releasing the swap chain
    /// never closes a handle under a wait). When an update waits for a frame, the signal also queues
    /// <see cref="ready"/> once on the overlay thread.
    /// </summary>
    private sealed class FrameSlot : IDisposable
    {
        private readonly WaitHandle handle;
        private readonly RegisteredWaitHandle registration;
        private readonly SystemDispatcherQueue queue;
        private readonly Windows.System.DispatcherQueueHandler ready;
        private int available;
        private int pending;

        public FrameSlot(HANDLE waitable, SystemDispatcherQueue queue, Windows.System.DispatcherQueueHandler ready)
        {
            this.queue = queue;
            this.ready = ready;
            // A new swap chain starts with a free frame; take that signal here so the first frame never waits for the
            // thread pool.
            if (PInvoke.WaitForSingleObject(waitable, 0) == (WAIT_EVENT)0)
            {
                available = 1;
            }
            handle = new DuplicatedWaitHandle(waitable);
            registration = ThreadPool.RegisterWaitForSingleObject(handle, static (state, _) => ((FrameSlot)state!).Signaled(), this,
                                                                  Timeout.Infinite, executeOnlyOnce: false);
        }

        public bool TryTake() => Interlocked.Exchange(ref available, 0) == 1;

        /// <summary>Marks an update as waiting; returns true when a frame freed meanwhile and was taken.</summary>
        public bool Defer()
        {
            Interlocked.Exchange(ref pending, 1);
            if (!TryTake())
            {
                return false;
            }
            Interlocked.Exchange(ref pending, 0);
            return true;
        }

        public void Dispose()
        {
            registration.Unregister(null);
            handle.Dispose();
        }

        private void Signaled()
        {
            Interlocked.Exchange(ref available, 1);
            if (Interlocked.Exchange(ref pending, 0) == 1)
            {
                queue.TryEnqueue(ready);
            }
        }
    }

    private sealed class DuplicatedWaitHandle : WaitHandle
    {
        public DuplicatedWaitHandle(HANDLE source)
        {
            var process = PInvoke.GetCurrentProcess();
            HANDLE duplicate;
            if (!PInvoke.DuplicateHandle(process, source, process, &duplicate, 0, false, DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new NativeCallException("DuplicateHandle (frame-latency waitable object)", error == 0 ? unchecked((int)0x80004005) : unchecked((int)(((uint)error & 0xFFFF) | 0x80070000)));
            }
            SafeWaitHandle = new SafeWaitHandle((nint)duplicate.Value, ownsHandle: true);
        }
    }
}
