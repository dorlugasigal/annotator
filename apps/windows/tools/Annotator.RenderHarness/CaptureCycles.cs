using System.Diagnostics;
using Annotator.Core;
using Annotator.Platform.Capture;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Presentation;

namespace Annotator.RenderHarness;

internal static partial class CaptureModes
{
    /// <summary>
    /// Live effects started and stopped <see cref="HarnessOptions.Cycles"/> times on the primary display, as when a person adds
    /// and deletes a region: one 640 x 360-pixel Blur region through the effect layer and the capture service, which the app
    /// creates the same way (the capture stays open, paused, for <see cref="CaptureServiceOptions.Linger"/> after each stop).
    /// After each stop it reports the process's video memory as Direct3D counts it (<c>QueryVideoMemoryInfo</c>) and as Task
    /// Manager does (PDH <c>GPU Process Memory</c>, dedicated), private bytes and handles, and the capture sessions opened and
    /// reused; then the same once the service has let go of everything. Only PDH counts the buffers that
    /// Windows.Graphics.Capture shares with the compositor, and Windows keeps one frame-sized buffer of every capture session
    /// that ends until the process quits.
    /// </summary>
    public static void CaptureCycles(HarnessOptions options)
    {
        using var session = OverlaySession.Start(LayerKind.VirtualSurface, LayerKind.VirtualSurface);
        var display = session.Display;
        var settings = new CaptureServiceOptions { Registry = session.Registry, FrameComparer = new CaptureFrameComparer() };
        if (options.Linger is { } linger)
        {
            settings = settings with { Linger = linger };
        }
        var service = CaptureService.Create(settings);
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
        using var counters = new MotionCapture.PerfCounters();
        using var self = Process.GetCurrentProcess();
        (double Local, double Dedicated, double Private, int Handles) Memory()
        {
            // Threads, WinRT wrappers and wait handles of a stopped capture close their handles when they are finalized.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var local = session.Run(() => GpuTimer.LocalVideoMemory(session.Devices), "reading video memory");
            self.Refresh();
            return (local / 1048576.0, counters.Sample(self).AppDedicated / 1048576.0, self.PrivateMemorySize64 / 1048576.0, self.HandleCount);
        }
        var start = Memory();
        var handleTypes = HandleCensus.Take();
        try
        {
            Console.WriteLine($"{display.DeviceName}: {display.Bounds.Width} x {display.Bounds.Height} pixels; {options.Cycles} cycles, linger "
                              + $"{settings.Linger.TotalSeconds:F1} s. Before: Direct3D {start.Local:F1} MiB, dedicated (PDH) {start.Dedicated:F1} MiB, "
                              + $"private {start.Private:F1} MiB, {start.Handles} handles.");
            var blur = new Element(Tool.Blur, new Rect(8, 8, 640 / display.Scale, 360 / display.Scale));
            for (var cycle = 1; cycle <= options.Cycles; cycle++)
            {
                session.Run(() => effects.Show(ElementList.Create([blur])), "showing the blur region");
                WaitUntil(() => session.Run(() => effects.State is EffectsState.Live or EffectsState.Failed, "reading the effects state"), StartTimeout, "live effects");
                if (session.Run(() => effects.State, "reading the effects state") is EffectsState.Failed failed)
                {
                    throw new InvalidOperationException($"The effects failed: {failed}.");
                }
                Thread.Sleep(1000);
                var live = Memory();
                session.Run(() => effects.Show(ElementList.Empty), "removing the region");
                WaitUntil(() => !service.IsCapturing, StartTimeout, "capture to stop");
                Thread.Sleep(1500);
                var stopped = Memory();
                var statistics = service.Statistics;
                Console.WriteLine($"Cycle {cycle}: live {live.Local - start.Local:F1} / {live.Dedicated - start.Dedicated:F1} MiB more; "
                                  + $"stopped {stopped.Local - start.Local:F1} / {stopped.Dedicated - start.Dedicated:F1} MiB more (Direct3D / dedicated); "
                                  + $"private {stopped.Private - start.Private:F1} MiB more; {stopped.Handles - start.Handles:+0;-0;0} handles "
                                  + $"({HandleCensus.Difference(handleTypes, HandleCensus.Take())}); "
                                  + $"capture {(service.HoldsCapture ? "paused" : "closed")}, sessions opened {statistics.SessionsStarted}, reused {statistics.SessionsReused}.");
            }
        }
        finally
        {
            session.Run(effects.Dispose, "releasing the effects presenter");
            service.Dispose();
        }
        Thread.Sleep(1500);
        var end = Memory();
        Console.WriteLine($"After the capture service let go of everything: {end.Local - start.Local:F1} / {end.Dedicated - start.Dedicated:F1} MiB more "
                          + $"(Direct3D / dedicated); private {end.Private - start.Private:F1} MiB more; {end.Handles - start.Handles:+0;-0;0} handles "
                          + $"({HandleCensus.Difference(handleTypes, HandleCensus.Take())}).");
    }

    /// <summary>The process's handles counted by object type (NtQueryInformationProcess and NtQueryObject), for leak checks.</summary>
    private static unsafe partial class HandleCensus
    {
        private const int ProcessHandleInformation = 51;
        private const int ObjectTypeInformation = 2;
        private const int InfoLengthMismatch = unchecked((int)0xC0000004);

        public static Dictionary<string, int> Take()
        {
            var types = new Dictionary<string, int>();
            var size = 1u << 16;
            while (true)
            {
                var buffer = System.Runtime.InteropServices.NativeMemory.Alloc(size);
                try
                {
                    var status = NtQueryInformationProcess(-1, ProcessHandleInformation, buffer, size, out var needed);
                    if (status == InfoLengthMismatch)
                    {
                        size = Math.Max(size * 2, needed + 4096);
                        continue;
                    }
                    if (status < 0)
                    {
                        return types;
                    }
                    // PROCESS_HANDLE_SNAPSHOT_INFORMATION: the count, a reserved field, then entries of a handle, two counts and
                    // four ULONGs.
                    var count = (long)*(nuint*)buffer;
                    var entries = (byte*)buffer + 2 * sizeof(nuint);
                    var entrySize = 3 * sizeof(nuint) + 4 * sizeof(uint);
                    var name = stackalloc byte[1024];
                    for (long index = 0; index < count; index++)
                    {
                        var handle = *(nint*)(entries + index * entrySize);
                        var type = "unknown";
                        if (NtQueryObject(handle, ObjectTypeInformation, name, 1024, out _) >= 0)
                        {
                            // PUBLIC_OBJECT_TYPE_INFORMATION starts with the type name's UNICODE_STRING.
                            type = new string(*(char**)(name + 8), 0, *(ushort*)name / 2);
                        }
                        types[type] = types.GetValueOrDefault(type) + 1;
                    }
                    return types;
                }
                finally
                {
                    System.Runtime.InteropServices.NativeMemory.Free(buffer);
                }
            }
        }

        public static string Difference(Dictionary<string, int> before, Dictionary<string, int> after)
        {
            var changes = after.Keys.Union(before.Keys).Select(type => (type, after.GetValueOrDefault(type) - before.GetValueOrDefault(type)))
                .Where(item => item.Item2 != 0).OrderByDescending(item => Math.Abs(item.Item2)).Select(item => $"{item.type} {item.Item2:+0;-0}");
            var text = string.Join(", ", changes);
            return text.Length == 0 ? "no type changed" : text;
        }

        [System.Runtime.InteropServices.LibraryImport("ntdll.dll")]
        private static partial int NtQueryInformationProcess(nint process, int informationClass, void* buffer, uint length, out uint returned);

        [System.Runtime.InteropServices.LibraryImport("ntdll.dll")]
        private static partial int NtQueryObject(nint handle, int informationClass, void* buffer, uint length, out uint returned);
    }
}
