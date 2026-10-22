using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Presentation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.RenderHarness;

/// <summary>
/// The capture service and the effect layer on the real desktop: modes <c>capture</c> and <c>effects</c> (README.md).
/// Every wait is bounded, and content is on screen only while a step runs.
/// </summary>
internal static unsafe partial class CaptureModes
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly RGBA Magenta = new(1, 0, 1);
    private static readonly RGBA Cyan = new(0, 1, 1);

    /// <summary>
    /// Capture of the primary display through the service: start time, frame rate, CPU of this process and of the
    /// compositor, managed allocations; then the leak check, in which the overlay shows a patch that a plain monitor
    /// capture sees and the service's frames must never show.
    /// </summary>
    public static void Capture(HarnessOptions options)
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        var display = session.Display;
        Console.WriteLine($"Display {display.DeviceName}: {display.Bounds.Width} x {display.Bounds.Height} pixels at {display.Scale:P0}.");
        var patch = new Rect(8, 8, 48, 48);
        var area = Inset(Pixels(patch, display.Scale), 4);
        var idle = Measure(TimeSpan.FromSeconds(2));
        Console.WriteLine($"Idle, no capture: {idle}");

        // No linger: this mode checks that the last release frees the capture at once (capture-cycles measures lingering).
        using var service = CaptureService.Create(new CaptureServiceOptions { Registry = session.Registry, Linger = TimeSpan.Zero });
        var probe = new LeakProbe(area, [Bytes(Magenta), Bytes(Cyan)]);
        try
        {
            var memoryBefore = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            var acquired = Stopwatch.GetTimestamp();
            var lease = service.Acquire(display, probe);
            try
            {
                WaitUntil(() => lease.State is CaptureState.Live or CaptureState.Failed, StartTimeout, "the first captured frame");
                if (lease.State is CaptureState.Failed failed)
                {
                    throw new InvalidOperationException($"Capture failed: {failed.Failure}");
                }
                Console.WriteLine($"Capture started: first frame {Stopwatch.GetElapsedTime(acquired).TotalMilliseconds:F0} ms after Acquire.");
                Thread.Sleep(500);
                // Three windows in a row: one-time work shows in the first only, steady work in each.
                for (var window = 1; window <= 3; window++)
                {
                    var frames = probe.Frames;
                    var running = Measure(TimeSpan.FromSeconds(3));
                    frames = probe.Frames - frames;
                    Console.WriteLine($"Capturing ({window}): {frames / running.Elapsed.TotalSeconds:F1} frames per second; {running}");
                }
                var memoryLive = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
                Console.WriteLine($"Local video memory of the process while capturing: {(memoryLive - memoryBefore) / 1048576.0:F1} MiB more than before.");

                using (var monitor = new MonitorCapture(display))
                {
                    var probePixel = new PixelRect(area.Left, area.Top, area.Left + 1, area.Top + 1);
                    var (seen, _, _) = monitor.Expect(probePixel, Bytes(Magenta), TimeSpan.FromSeconds(2), () => session.Run(() =>
                    {
                        var start = Stopwatch.GetTimestamp();
                        session.Presenter.ShowScene(Scenes.Patch(patch, Magenta));
                        return (start, Stopwatch.GetTimestamp());
                    }, "drawing the patch"));
                    if (seen is null)
                    {
                        throw new NoFramesException("a plain monitor capture never showed the overlay's patch within 2 s; the display may be off or the session locked.");
                    }
                    long monitorFrames = 0, monitorShowed = 0;
                    var expected = new[] { Bytes(Magenta), Bytes(Cyan) };
                    monitor.OnFrame((texture, _) =>
                    {
                        var pixel = monitor.Read(texture, probePixel)[0, 0];
                        Interlocked.Increment(ref monitorFrames);
                        if (Array.IndexOf(expected, (pixel.Red, pixel.Green, pixel.Blue)) >= 0)
                        {
                            Interlocked.Increment(ref monitorShowed);
                        }
                    });
                    probe.Start();
                    var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
                    var colors = new[] { Cyan, Magenta };
                    var turn = 0;
                    while (probe.Checked < options.Samples)
                    {
                        if (Stopwatch.GetTimestamp() > deadline)
                        {
                            throw new TimeoutException($"Only {probe.Checked} of {options.Samples} captured frames were checked within 30 s.");
                        }
                        var color = colors[turn++ % 2];
                        session.Run(() => session.Presenter.ShowScene(Scenes.Patch(patch, color)), "changing the patch color");
                        // 2.5 changes per second: below the three flashes per second that can harm people with photosensitivity.
                        Thread.Sleep(400);
                    }
                    probe.Stop();
                    monitor.OnFrame(null);
                    Console.WriteLine($"Leak check: {probe.Checked} service frames read in {area.Width} x {area.Height} pixels under the overlay's patch, "
                                      + $"which changed color {turn} times; {probe.Leaked} showed the patch (most of one frame: {probe.WorstFraction:P0}); "
                                      + $"{probe.Unread} could not be read. Control: a plain monitor capture showed the patch in "
                                      + $"{Interlocked.Read(ref monitorShowed)} of {Interlocked.Read(ref monitorFrames)} frames.");
                    if (probe.Leaked > 0)
                    {
                        throw new InvalidOperationException($"LEAK: {probe.Leaked} captured frames showed the overlay.");
                    }
                }
            }
            finally
            {
                lease.Dispose();
                // Its staging texture would keep the capture device, and every resource on it, alive.
                probe.Dispose();
            }
            Console.WriteLine($"After release the lease is {lease.State}.");
            // The capture thread releases the magnifier, the device and the pacer meanwhile.
            Thread.Sleep(1000);
            var memoryAfter = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            Console.WriteLine($"After release: video memory {(memoryAfter - memoryBefore) / 1048576.0:F1} MiB above the start; {Measure(TimeSpan.FromSeconds(2))}");
        }
        finally
        {
            probe.Dispose();
            session.Run(() => session.Presenter.ShowScene(Annotator.Core.Scene.SceneSnapshot.Empty), "clearing the patch");
        }
    }

    /// <summary>
    /// GPU time of the effect pipeline at 5,120 x 1,440 pixels with synthetic frames (nothing on screen), then, unless
    /// <c>--offscreen</c>, one blur region on the real overlay from the service's frames: start time, drawn frames, CPU,
    /// allocations, memory, suspension and release.
    /// </summary>
    public static void Effects(HarnessOptions options)
    {
        PipelineTimes(options.Samples);
        if (!options.Offscreen)
        {
            LiveEffects();
        }
    }

    private static void PipelineTimes(int samples)
    {
        const int Width = 5120, Height = 1440;
        using var devices = GraphicsDevices.Create();
        Console.WriteLine($"Effect pipeline at {Width} x {Height} pixels on {devices.Adapter.Description}: GPU time between timestamps, {samples} frames per workload.");
        var memoryBefore = GpuTimer.LocalVideoMemory(devices);
        var source = Texture(devices, Width, Height, D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, pattern: true);
        var target = Texture(devices, Width, Height, D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET, pattern: false);
        try
        {
            using var pipeline = new EffectPipeline(devices);
            using var timer = new GpuTimer(devices);
            var texturesOnly = GpuTimer.LocalVideoMemory(devices);
            foreach (var (name, list) in Workloads(Width, Height))
            {
                var clear = list.Regions.ToArray().Where(region => region.IsVisible).Select(region => region.Rect).ToArray();
                var gpu = new List<double>(samples);
                var cpu = new List<double>(samples);
                long passes = 0;
                for (var index = -5; index < samples; index++)
                {
                    var before = pipeline.Passes;
                    var start = Stopwatch.GetTimestamp();
                    timer.Begin();
                    pipeline.Render(source, target, list, clear);
                    timer.End();
                    var submit = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var (gpuMs, _) = timer.Read(start);
                    if (index >= 0)
                    {
                        gpu.Add(gpuMs);
                        cpu.Add(submit);
                        passes += pipeline.Passes - before;
                    }
                }
                Console.WriteLine($"  {name}: GPU {Stats.Summary(gpu)}; CPU submit p50 {Stats.Percentile(cpu, 0.5):F3} ms; "
                                  + $"passes per frame {passes / (double)samples:F0}");
            }
            Console.WriteLine($"  GPU read timeouts {timer.Timeouts}. Local video memory: test textures {(texturesOnly - memoryBefore) / 1048576.0:F1} MiB, "
                              + $"pipeline scratch after all workloads {(GpuTimer.LocalVideoMemory(devices) - texturesOnly) / 1048576.0:F1} MiB.");
        }
        finally
        {
            Marshal.Release(source);
            Marshal.Release(target);
        }
    }

    private static IEnumerable<(string Name, EffectList List)> Workloads(int width, int height)
    {
        EffectList List(double scale, params Element[] elements) => EffectList.From(ElementList.Create(elements), scale, width, height);
        var whole = new Rect(0, 0, width, height);
        yield return ("Blur 18 (the default), one 1,280 x 720 region", List(1, Effect(Tool.Blur, new Rect(1920, 360, 1280, 720), 18)));
        yield return ("Blur 18, whole display", List(1, Effect(Tool.Blur, whole, 18)));
        yield return ("Blur 18 at 200% (deviation 36 pixels), whole display", List(2, Effect(Tool.Blur, new Rect(0, 0, width / 2.0, height / 2.0), 18)));
        yield return ("Blur 100, whole display", List(1, Effect(Tool.Blur, whole, 100)));
        yield return ("Blur 4 (no reduction levels), whole display", List(1, Effect(Tool.Blur, whole, 4)));
        yield return ("Pixelate 18, whole display", List(1, Effect(Tool.Pixelate, whole, 18)));
        yield return ("Distort 18, whole display", List(1, Effect(Tool.Distort, whole, 18)));
        var mixed = new List<Element>();
        Tool[] tools = [Tool.Blur, Tool.Pixelate, Tool.Distort];
        for (var index = 0; index < 10; index++)
        {
            mixed.Add(Effect(tools[index % 3], new Rect(40 + index % 5 * 1000, 60 + index / 5 * 700, 800, 450), 18));
        }
        mixed.Add(new Element(Tool.Cover, new Rect(300, 200, 400, 200)));
        mixed.Add(new Element(Tool.Cover, new Rect(2400, 900, 500, 300)));
        yield return ("Ten 800 x 450 regions (blur, pixelate, distort) and two covers", List(1, [.. mixed]));
    }

    private static void LiveEffects()
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        var display = session.Display;
        // No linger: this mode checks that removing the region frees the capture at once (capture-cycles measures lingering).
        using var service = CaptureService.Create(new CaptureServiceOptions { Registry = session.Registry, Linger = TimeSpan.Zero });
        var effects = session.Run(() => EffectsPresenter.Create(new EffectsPresenterOptions
        {
            Compositor = session.Compositor,
            Root = session.Overlay.Root,
            Capture = service,
            Display = display,
            Scale = display.Scale,
            PixelWidth = display.Bounds.Width,
            PixelHeight = display.Bounds.Height,
        }), "creating the effects presenter");
        try
        {
            var memoryBefore = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            var idle = Measure(TimeSpan.FromSeconds(2));
            Console.WriteLine($"Live effects on {display.DeviceName} ({display.Bounds.Width} x {display.Bounds.Height} at {display.Scale:P0}), one 640 x 360-pixel blur region (amount 18).");
            Console.WriteLine($"  Idle overlay, no effects: {idle}");
            var blur = new Element(Tool.Blur, new Rect(8, 8, 640 / display.Scale, 360 / display.Scale));
            var shown = Stopwatch.GetTimestamp();
            session.Run(() => effects.Show(ElementList.Create([blur])), "showing the blur region");
            double? captureLive = null;
            WaitUntil(() =>
            {
                var (state, capture) = session.Run(() => (effects.State, effects.CaptureState), "reading the effects state");
                if (captureLive is null && capture is CaptureState.Live)
                {
                    captureLive = Stopwatch.GetElapsedTime(shown).TotalMilliseconds;
                }
                return state is EffectsState.Live or EffectsState.Failed || capture is CaptureState.Failed;
            }, StartTimeout, "live effects");
            var started = session.Run(() => effects.State, "reading the effects state");
            if (started is not EffectsState.Live)
            {
                throw new InvalidOperationException($"The effects did not start: {started} (capture {session.Run(() => effects.CaptureState, "reading the capture state")}).");
            }
            Console.WriteLine($"  Start: capture live after {captureLive:F0} ms, first effect frame shown after {Stopwatch.GetElapsedTime(shown).TotalMilliseconds:F0} ms.");
            Thread.Sleep(500);

            var statistics = effects.Statistics;
            var drawn = statistics.Frames;
            var skipped = statistics.Skipped;
            var frameTimes = new List<double>(1024);
            var running = Measure(TimeSpan.FromSeconds(5), () =>
            {
                if (frameTimes.Count < frameTimes.Capacity)
                {
                    frameTimes.Add(statistics.LastFrameTime.TotalMilliseconds);
                }
            });
            drawn = statistics.Frames - drawn;
            skipped = statistics.Skipped - skipped;
            var memoryLive = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            Console.WriteLine($"  Running: {drawn / running.Elapsed.TotalSeconds:F1} effect frames per second, {skipped / running.Elapsed.TotalSeconds:F1} skipped per second "
                              + $"(no free swap-chain buffer); CPU per effect frame (sampled) {Stats.Summary(frameTimes)}");
            Console.WriteLine($"  Running: {running}");
            Console.WriteLine($"  Local video memory of the process: {(memoryLive - memoryBefore) / 1048576.0:F1} MiB more than before the region.");

            var paused = Stopwatch.GetTimestamp();
            service.Suspend(CaptureSuspension.RequestedByApp);
            WaitUntil(() => session.Run(() => effects.State is EffectsState.Paused, "reading the effects state"), StartTimeout, "the paused state");
            var pausedMs = Stopwatch.GetElapsedTime(paused).TotalMilliseconds;
            var resumed = Stopwatch.GetTimestamp();
            service.Resume();
            WaitUntil(() => session.Run(() => effects.State is EffectsState.Live, "reading the effects state"), StartTimeout, "live effects after Resume");
            Console.WriteLine($"  Suspend: regions black (Paused) after {pausedMs:F1} ms; Resume: live again after {Stopwatch.GetElapsedTime(resumed).TotalMilliseconds:F0} ms.");

            // A second lease keeps capture running while the region goes, so the effect layer's share shows alone.
            var capturing = service.Acquire(display, new IgnoredFrames());
            try
            {
                var released = Stopwatch.GetTimestamp();
                session.Run(() => effects.Show(ElementList.Empty), "removing the region");
                var releasedMs = Stopwatch.GetElapsedTime(released).TotalMilliseconds;
                Thread.Sleep(500);
                var memoryCapture = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
                Console.WriteLine($"  Release: the last region's removal took {releasedMs:F1} ms on the overlay thread and freed "
                                  + $"{(memoryLive - memoryCapture) / 1048576.0:F1} MiB; capture alone holds {(memoryCapture - memoryBefore) / 1048576.0:F1} MiB.");
            }
            finally
            {
                capturing.Dispose();
            }
            Thread.Sleep(1000);
            var memoryAfter = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            Console.WriteLine($"  Idle again: video memory {(memoryAfter - memoryBefore) / 1048576.0:F1} MiB above the start; {Measure(TimeSpan.FromSeconds(2))}");
            Console.WriteLine($"  Presenter: {statistics.Frames} effect frames drawn, {statistics.Skipped} skipped; state {session.Run(() => effects.State, "reading the effects state")}.");
        }
        finally
        {
            session.Run(effects.Dispose, "releasing the effects presenter");
        }
    }

    private static Element Effect(Tool tool, Rect frame, double amount)
    {
        var element = new Element(tool, frame);
        element.Style.EffectAmount = amount;
        return element;
    }

    private static PixelRect Pixels(Rect rect, double scale) => new(
        (int)Math.Ceiling(rect.X * scale), (int)Math.Ceiling(rect.Y * scale), (int)Math.Floor(rect.MaxX * scale), (int)Math.Floor(rect.MaxY * scale));

    private static PixelRect Inset(PixelRect rect, int by) => new(rect.Left + by, rect.Top + by, rect.Right - by, rect.Bottom - by);

    private static (byte, byte, byte) Bytes(RGBA color) =>
        ((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255));

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F0} s waiting for {what}.");
            }
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// CPU of this process and of the compositor (dwm.exe, when Windows lets this process read it), as a share of one
    /// core, and managed bytes allocated by every thread of this process, over <paramref name="duration"/>.
    /// <paramref name="sample"/> runs every 5 ms meanwhile and must not allocate.
    /// </summary>
    private static Usage Measure(TimeSpan duration, Action? sample = null)
    {
        using var process = Process.GetCurrentProcess();
        using var compositor = FindCompositor();
        var processStart = process.TotalProcessorTime;
        var compositorStart = CpuTime(compositor);
        var input = new LastInput { Size = (uint)sizeof(LastInput) };
        GetLastInputInfo(ref input);
        var inputBefore = input.Time;
        var foreground = GetForegroundWindow();
        var foregroundChanges = 0;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var start = Stopwatch.GetTimestamp();
        var end = start + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < end)
        {
            sample?.Invoke();
            var now = GetForegroundWindow();
            if (now != foreground)
            {
                foreground = now;
                foregroundChanges++;
            }
            Thread.Sleep(5);
        }
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        GetLastInputInfo(ref input);
        var elapsed = Stopwatch.GetElapsedTime(start);
        process.Refresh();
        var processCpu = (process.TotalProcessorTime - processStart) / elapsed;
        double? compositorCpu = CpuTime(compositor) is { } compositorEnd && compositorStart is { } begun ? (compositorEnd - begun) / elapsed : null;
        return new Usage(elapsed, processCpu, compositorCpu, allocated, input.Time != inputBefore, foregroundChanges);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput
    {
        public uint Size;
        public uint Time;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInput info);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    private static Process? FindCompositor()
    {
        using var current = Process.GetCurrentProcess();
        Process? found = null;
        foreach (var candidate in Process.GetProcessesByName("dwm"))
        {
            if (found is null && candidate.SessionId == current.SessionId)
            {
                found = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }
        return found;
    }

    private static TimeSpan? CpuTime(Process? process)
    {
        if (process is null)
        {
            return null;
        }
        try
        {
            process.Refresh();
            return process.TotalProcessorTime;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static nint Texture(GraphicsDevices devices, int width, int height, D3D11_BIND_FLAG bind, bool pattern)
    {
        var description = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = bind,
        };
        ID3D11Texture2D* texture;
        if (!pattern)
        {
            Native.Check(((ID3D11Device*)devices.D3D11Device)->CreateTexture2D(&description, null, &texture), "ID3D11Device::CreateTexture2D(target)");
            return (nint)texture;
        }
        // A busy synthetic screen: stripes, gradients and a checkerboard.
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x * 255 / width);
                pixels[offset + 1] = (byte)((x / 7 + y / 5) % 2 * 200);
                pixels[offset + 2] = (byte)(y * 255 / height);
                pixels[offset + 3] = 255;
            }
        }
        fixed (byte* data = pixels)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = (uint)(width * 4) };
            Native.Check(((ID3D11Device*)devices.D3D11Device)->CreateTexture2D(&description, &initial, &texture), "ID3D11Device::CreateTexture2D(source)");
        }
        return (nint)texture;
    }

    /// <summary>
    /// CPU and allocations over a measured span, and whether someone used the computer meanwhile: the overlay reacts
    /// to foreground changes, and other work on the computer changes the CPU numbers.
    /// </summary>
    private readonly record struct Usage(TimeSpan Elapsed, double ProcessCpu, double? CompositorCpu, long Allocated, bool Input, int ForegroundChanges)
    {
        public override string ToString() =>
            $"this process {ProcessCpu:P1} of one core, compositor {(CompositorCpu is { } compositor ? compositor.ToString("P1") : "not readable")}, "
            + $"{Allocated} managed bytes allocated in {Elapsed.TotalSeconds:F1} s "
            + $"({(Input ? "with" : "without")} user input, {ForegroundChanges} foreground changes)";
    }


    /// <summary>A consumer that only keeps capture running.</summary>
    private sealed class IgnoredFrames : ICaptureConsumer
    {
        public void OnFrameAvailable(ICaptureLease lease)
        {
        }

        public void OnStateChanged(ICaptureLease lease, CaptureState state)
        {
        }
    }

    /// <summary>
    /// Reads an area of every captured frame back from the GPU while started and counts frames in which at least a
    /// quarter of its pixels show one of the patch colors (each channel within 24).
    /// </summary>
    private sealed class LeakProbe(PixelRect area, (byte Red, byte Green, byte Blue)[] colors) : ICaptureConsumer, IDisposable
    {
        private readonly List<double> fractions = [];
        private GraphicsDevices? stagingDevices;
        private ID3D11Texture2D* staging;
        private volatile bool reading;
        private long frames;
        private long checkedFrames;
        private long leaked;
        private long unread;
        private double worst;


        public long Frames => Interlocked.Read(ref frames);

        /// <summary>The patch's share of the area in each frame read since <see cref="Start"/>, in order.</summary>
        public List<double> Fractions
        {
            get
            {
                lock (fractions)
                {
                    return [.. fractions];
                }
            }
        }

        public long Checked => Interlocked.Read(ref checkedFrames);

        public long Leaked => Interlocked.Read(ref leaked);

        public long Unread => Interlocked.Read(ref unread);

        public double WorstFraction => Volatile.Read(ref worst);

        public void Start()
        {
            Interlocked.Exchange(ref checkedFrames, 0);
            Interlocked.Exchange(ref leaked, 0);
            Interlocked.Exchange(ref unread, 0);
            reading = true;
        }

        public void Stop() => reading = false;

        public void OnFrameAvailable(ICaptureLease lease)
        {
            Interlocked.Increment(ref frames);
            if (!reading || !lease.TryAcquireFrame(out var frame))
            {
                return;
            }
            using (frame)
            {
                try
                {
                    var fraction = Read(frame);
                    Interlocked.Increment(ref checkedFrames);
                    lock (fractions)
                    {
                        if (fractions.Count < 4096)
                        {
                            fractions.Add(fraction);
                        }
                    }
                    if (fraction > worst)
                    {
                        Volatile.Write(ref worst, fraction);
                    }
                    if (fraction >= 0.25)
                    {
                        Interlocked.Increment(ref leaked);
                    }
                }
                catch (NativeCallException)
                {
                    Interlocked.Increment(ref unread);
                }
            }
        }

        public void OnStateChanged(ICaptureLease lease, CaptureState state)
        {
        }

        /// <summary>Call after the lease is disposed: no frame callback runs any more.</summary>
        public void Dispose() => Native.Release(ref staging);

        private double Read(in CaptureFrame frame)
        {
            if (!ReferenceEquals(stagingDevices, frame.Devices))
            {
                Native.Release(ref staging);
                var description = new D3D11_TEXTURE2D_DESC
                {
                    Width = (uint)area.Width,
                    Height = (uint)area.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                };
                ID3D11Texture2D* created;
                Native.Check(((ID3D11Device*)frame.Devices.D3D11Device)->CreateTexture2D(&description, null, &created), "ID3D11Device::CreateTexture2D(leak probe)");
                staging = created;
                stagingDevices = frame.Devices;
            }
            var context = (ID3D11DeviceContext*)frame.Devices.D3D11DeviceContext;
            var box = new D3D11_BOX { left = (uint)area.Left, top = (uint)area.Top, front = 0, right = (uint)area.Right, bottom = (uint)area.Bottom, back = 1 };
            context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)frame.Texture, 0, &box);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Native.Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext::Map(leak probe)");
            var matches = 0;
            try
            {
                for (var y = 0; y < area.Height; y++)
                {
                    var row = (byte*)mapped.pData + (long)y * mapped.RowPitch;
                    for (var x = 0; x < area.Width; x++)
                    {
                        byte blue = row[x * 4], green = row[x * 4 + 1], red = row[x * 4 + 2];
                        foreach (var (r, g, b) in colors)
                        {
                            if (Math.Abs(red - r) <= 24 && Math.Abs(green - g) <= 24 && Math.Abs(blue - b) <= 24)
                            {
                                matches++;
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
            return matches / (double)(area.Width * area.Height);
        }
    }
}
