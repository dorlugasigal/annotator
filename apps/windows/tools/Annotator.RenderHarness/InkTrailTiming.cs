using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Presentation;
using Windows.UI.Composition;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>ink</c>: the latency of Windows 11's delegated ink trail (WIN-PERF-04) next to a swap chain's own present. A
/// display-sized composition swap chain on the harness's click-through overlay gets a delegated ink trail
/// (IDCompositionInkTrailDevice::CreateDelegatedInkTrailForSwapChain); each sample starts a new trail in a new color and
/// adds two points at the probe, and Windows.Graphics.Capture reports the composition time of the first frame that shows
/// it. With <c>--live SwapChain</c> each sample instead draws the same dot into the swap chain and presents it.
/// <c>--dcomp</c> hosts the swap chain in a DirectComposition tree of the overlay window instead of Windows.UI.Composition,
/// <c>--duplication</c> looks for the samples in DXGI desktop duplication instead, and <c>--press</c> puts the overlay in
/// Draw with the left button held on it (the harness's own window, checked with WindowFromPoint), as while a person draws.
/// </summary>
internal static unsafe partial class InkTrailTiming
{
    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    private static readonly Guid IID_IDCompositionDesktopDevice = new("5F4633FE-1E08-4CB8-8C75-CE24333F5602");
    private static readonly Guid IID_IDCompositionInkTrailDevice = new("DF0C7CEC-CDEB-4D4A-B91C-721BF22F4E6C");
    private static readonly (float R, float G, float B)[] Colors = [(1, 0, 0), (0, 0.8f, 0), (0, 0, 1)];
    private const int Warmup = 5;

    public static void Run(HarnessOptions options)
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        using var capture = new MonitorCapture(session.Display);
        var display = session.Display;
        int width = display.Bounds.Width, height = display.Bounds.Height;
        var presents = options.Live == LayerKind.SwapChain;
        var grid = CompositionTiming.Read() ?? throw new NoFramesException("The compositor reports no refresh timing.");
        Console.WriteLine($"Ink: {(presents ? "swap chain presents" : "delegated ink trail on a swap chain")}; display {width} x {height} at {display.Scale:P0}, {grid.RefreshHz:F3} Hz; {options.Samples} samples after {Warmup} warm-up samples.");
        var state = session.Run(() => Setup(session, width, height, options.DirectComposition), "creating the swap chain and the ink trail");
        using var duplication = options.Duplication ? new DesktopDuplication(display) : null;
        if (duplication is not null)
        {
            Console.WriteLine("  detecting with DXGI desktop duplication (frame times are its last present times)");
        }
        // With --press, the overlay takes input and the left button is held on it, as while a person draws.
        var pressed = options.Press;
        int pressX = display.Bounds.Left + 400, pressY = display.Bounds.Top + 400;
        if (pressed)
        {
            session.SetInputMode(Annotator.Platform.Overlays.OverlayInputMode.Draw);
            Thread.Sleep(100);
            InputLatency.Press(pressX, pressY, Environment.ProcessId);
            Thread.Sleep(50);
            Console.WriteLine("  the overlay is in Draw with the left button held on it");
        }
        try
        {
            var probe = new PixelRect(60, 60, 61, 61);
            var phases = new List<(double StartPhase, double Latency, double FramePhase)>();
            var rows = new List<string> { "sample,start_phase_ms,latency_ms,submit_ms" };
            var submits = new List<double>();
            var missed = 0;
            for (var sample = 0; sample < options.Samples + Warmup; sample++)
            {
                var color = Colors[sample % Colors.Length];
                var expected = ((byte)Math.Round(color.R * 255), (byte)Math.Round(color.G * 255), (byte)Math.Round(color.B * 255));
                Func<(byte Red, byte Green, byte Blue), bool> matches = pixel =>
                    Math.Abs(pixel.Red - expected.Item1) + Math.Abs(pixel.Green - expected.Item2) + Math.Abs(pixel.Blue - expected.Item3) <= 30;
                Func<(long, long)> submit = () => session.Run(() =>
                {
                    var begin = Stopwatch.GetTimestamp();
                    if (presents)
                    {
                        Present(state, color);
                    }
                    else
                    {
                        Trail(state, color);
                    }
                    return (begin, Stopwatch.GetTimestamp());
                }, "drawing an ink sample");
                long? frameQpc;
                long start, end;
                if (duplication is not null)
                {
                    (frameQpc, start, end) = duplication.Expect(probe, matches, TimeSpan.FromMilliseconds(500), submit);
                }
                else
                {
                    var (seen, begin, finish) = capture.Expect(probe, matches, TimeSpan.FromMilliseconds(500), submit);
                    (frameQpc, start, end) = (seen is { } time ? CompositionTiming.Qpc(time) : null, begin, finish);
                }
                if (frameQpc is null)
                {
                    if (++missed >= 5)
                    {
                        throw new NoFramesException("Five ink samples never reached the screen; the display may be off, the session locked, or the trail not drawn.");
                    }
                    continue;
                }
                if (sample < Warmup)
                {
                    continue;
                }
                var frame = frameQpc.Value;
                var now = CompositionTiming.Read() ?? grid;
                var latency = CompositionTiming.Ms(frame - start);
                phases.Add((now.PhaseMs(start), latency, now.PhaseMs(frame)));
                submits.Add(CompositionTiming.Ms(end - start));
                rows.Add(FormattableString.Invariant($"{sample},{now.PhaseMs(start):F3},{latency:F3},{CompositionTiming.Ms(end - start):F3}"));
                Thread.Sleep(20 + sample % 13);
            }
            Console.WriteLine($"  latency: {Stats.Summary(phases.Select(sample => sample.Latency).ToList())}; submit CPU {Stats.Summary(submits)}; missed {missed}");
            CompositionTiming.PrintPhases("latency", phases, grid.PeriodMs);
            if (options.Evidence is { } evidence)
            {
                Directory.CreateDirectory(evidence);
                var path = Path.Combine(evidence, presents ? "ink-present.csv" : "ink-trail.csv");
                File.WriteAllLines(path, rows);
                Console.WriteLine($"  samples: {path}");
            }
        }
        finally
        {
            if (pressed)
            {
                InputLatency.Release(pressX, pressY, Environment.ProcessId);
                session.SetInputMode(Annotator.Platform.Overlays.OverlayInputMode.UseApps);
            }
            session.Run(() => Release(session, state), "releasing the swap chain and the ink trail");
        }
    }

    private sealed class State
    {
        public IDXGISwapChain1* SwapChain;
        public ID2D1DeviceContext* Context;
        public nint Device;
        public nint Target;
        public nint DCompVisual;
        public nint InkDevice;
        public nint Trail;
        public SpriteVisual? Visual;
        public CompositionSurfaceBrush? Brush;
        public ICompositionSurface? Surface;
        public int Width;
        public int Height;
    }

    private static State Setup(OverlaySession session, int width, int height, bool directComposition)
    {
        var state = new State { Width = width, Height = height };
        var devices = session.Devices;
        var dxgiDevice = (IDXGIDevice*)devices.DxgiDevice;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
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
            };
            IDXGISwapChain1* chain;
            Native.Check(factory->CreateSwapChainForComposition((IUnknown*)devices.D3D11Device, &description, null, &chain), "IDXGIFactory2::CreateSwapChainForComposition");
            state.SwapChain = chain;
        }
        finally
        {
            Native.Release(ref factory);
            Native.Release(ref adapter);
        }
        ID2D1DeviceContext* context;
        Native.Check(((ID2D1Device*)devices.D2DDevice)->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context), "ID2D1Device::CreateDeviceContext");
        state.Context = context;
        // A transparent first frame in both buffers.
        Clear(state);
        Native.Check(state.SwapChain->Present(0, 0), "IDXGISwapChain::Present");
        Clear(state);
        Native.Check(state.SwapChain->Present(0, 0), "IDXGISwapChain::Present");
        var compositor = session.Compositor;
        var dcompHosted = directComposition;
        Guid deviceIid = dcompHosted ? IID_IDCompositionDesktopDevice : IID_IDCompositionDevice;
        nint device;
        Native.Check(DCompositionCreateDevice3(devices.DxgiDevice, &deviceIid, &device), "DCompositionCreateDevice3");
        state.Device = device;
        if (dcompHosted)
        {
            // The swap chain in a DirectComposition tree of the overlay window, below its Windows.UI.Composition content.
            nint target;
            var createTarget = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)(*(void***)device)[24];
            Native.Check(createTarget(device, session.Overlay.Handle, 0, &target), "IDCompositionDesktopDevice::CreateTargetForHwnd");
            state.Target = target;
            nint visual;
            var createVisual = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(void***)device)[6];
            Native.Check(createVisual(device, &visual), "IDCompositionDevice2::CreateVisual");
            state.DCompVisual = visual;
            var setContent = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(void***)visual)[15];
            Native.Check(setContent(visual, (nint)state.SwapChain), "IDCompositionVisual::SetContent");
            var setRoot = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(void***)target)[3];
            Native.Check(setRoot(target, visual), "IDCompositionTarget::SetRoot");
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)(*(void***)device)[3];
            Native.Check(commit(device), "IDCompositionDevice2::Commit");
        }
        else
        {
            state.Surface = CompositionInterop.CreateCompositionSurfaceForSwapChain(compositor, (nint)state.SwapChain);
            state.Brush = compositor.CreateSurfaceBrush(state.Surface);
            state.Brush.Stretch = CompositionStretch.None;
            state.Brush.HorizontalAlignmentRatio = 0;
            state.Brush.VerticalAlignmentRatio = 0;
            state.Visual = compositor.CreateSpriteVisual();
            state.Visual.Size = new Vector2(width, height);
            state.Visual.Brush = state.Brush;
            session.Overlay.Root.Children.InsertAtTop(state.Visual);
        }
        // The delegated ink trail of that swap chain.
        Native.Check(Marshal.QueryInterface(device, in IID_IDCompositionInkTrailDevice, out var inkDevice), "QueryInterface(IDCompositionInkTrailDevice)");
        state.InkDevice = inkDevice;
        nint trail;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(void***)inkDevice)[4];
        Native.Check(create(inkDevice, (nint)state.SwapChain, &trail), "IDCompositionInkTrailDevice::CreateDelegatedInkTrailForSwapChain");
        state.Trail = trail;
        // A present after the trail exists, so the compositor has a frame of the swap chain to draw the trail on.
        Clear(state);
        Native.Check(state.SwapChain->Present(0, 0), "IDXGISwapChain::Present");
        return state;
    }

    private static void Clear(State state)
    {
        Guid surfaceIid = IDXGISurface.IID_Guid;
        void* buffer;
        Native.Check(state.SwapChain->GetBuffer(0, &surfaceIid, &buffer), "IDXGISwapChain::GetBuffer");
        ID2D1Bitmap1* bitmap = null;
        try
        {
            var properties = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            };
            Native.Check(state.Context->CreateBitmapFromDxgiSurface((IDXGISurface*)buffer, &properties, &bitmap), "ID2D1DeviceContext::CreateBitmapFromDxgiSurface");
            state.Context->SetTarget((ID2D1Image*)bitmap);
            state.Context->BeginDraw();
            var transparent = default(D2D1_COLOR_F);
            state.Context->Clear(&transparent);
            Native.Check(state.Context->EndDraw(null, null), "ID2D1DeviceContext::EndDraw");
            state.Context->SetTarget(null);
        }
        finally
        {
            Native.Release(ref bitmap);
            var unknown = (IUnknown*)buffer;
            Native.Release(ref unknown);
        }
    }

    private static void Present(State state, (float R, float G, float B) color)
    {
        Guid surfaceIid = IDXGISurface.IID_Guid;
        void* buffer;
        Native.Check(state.SwapChain->GetBuffer(0, &surfaceIid, &buffer), "IDXGISwapChain::GetBuffer");
        ID2D1Bitmap1* bitmap = null;
        ID2D1SolidColorBrush* brush = null;
        try
        {
            var properties = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96,
                dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            };
            Native.Check(state.Context->CreateBitmapFromDxgiSurface((IDXGISurface*)buffer, &properties, &bitmap), "ID2D1DeviceContext::CreateBitmapFromDxgiSurface");
            state.Context->SetTarget((ID2D1Image*)bitmap);
            state.Context->BeginDraw();
            var fill = new D2D1_COLOR_F { r = color.R, g = color.G, b = color.B, a = 1 };
            Native.Check(state.Context->CreateSolidColorBrush(&fill, null, &brush), "ID2D1DeviceContext::CreateSolidColorBrush");
            var rect = new D2D_RECT_F { left = 40, top = 40, right = 80, bottom = 80 };
            state.Context->FillRectangle(&rect, (ID2D1Brush*)brush);
            Native.Check(state.Context->EndDraw(null, null), "ID2D1DeviceContext::EndDraw");
            state.Context->SetTarget(null);
            Native.Check(state.SwapChain->Present(0, 0), "IDXGISwapChain::Present");
        }
        finally
        {
            Native.Release(ref brush);
            Native.Release(ref bitmap);
            var unknown = (IUnknown*)buffer;
            Native.Release(ref unknown);
        }
    }

    /// <summary>A new trail in <paramref name="color"/> with two points at the probe.</summary>
    private static void Trail(State state, (float R, float G, float B) color)
    {
        var fill = new D2D1_COLOR_F { r = color.R, g = color.G, b = color.B, a = 1 };
        var start = (delegate* unmanaged[Stdcall]<nint, D2D1_COLOR_F*, int>)(*(void***)state.Trail)[6];
        Native.Check(start(state.Trail, &fill), "IDCompositionDelegatedInkTrail::StartNewTrail");
        var points = stackalloc InkPoint[2];
        points[0] = new InkPoint(56, 60, 16);
        points[1] = new InkPoint(64, 60, 16);
        uint generation;
        var add = (delegate* unmanaged[Stdcall]<nint, InkPoint*, uint, uint*, int>)(*(void***)state.Trail)[3];
        Native.Check(add(state.Trail, points, 2, &generation), "IDCompositionDelegatedInkTrail::AddTrailPoints");
        // The trail needs no commit; one costs nothing here and rules out a batch that waits for it (IDCompositionDevice::Commit, slot 3).
        var commit = (delegate* unmanaged[Stdcall]<nint, int>)(*(void***)state.Device)[3];
        Native.Check(commit(state.Device), "IDCompositionDevice::Commit");
    }

    private static void Release(OverlaySession session, State state)
    {
        if (state.Visual is not null)
        {
            session.Overlay.Root.Children.Remove(state.Visual);
            state.Visual.Dispose();
        }
        state.Brush?.Dispose();
        (state.Surface as IDisposable)?.Dispose();
        foreach (var pointer in new[] { state.Trail, state.InkDevice, state.DCompVisual, state.Target, state.Device })
        {
            if (pointer != 0)
            {
                Marshal.Release(pointer);
            }
        }
        Native.Release(ref state.Context);
        Native.Release(ref state.SwapChain);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct InkPoint(float X, float Y, float Radius);

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionCreateDevice3(nint renderingDevice, Guid* iid, nint* device);
}
