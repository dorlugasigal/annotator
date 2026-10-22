using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Text;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>motion</c>: plays each show and hide style in the app's development-only motion run
/// (<c>Annotator.exe --motion-reference</c>, which runs beside a running Annotator) and records it as
/// Windows.Graphics.Capture composes it: the composition time of every frame, 12 frames per transition for a contact sheet
/// in the format of the Mac reference (<c>reference/ux-reference/macos/*/motion</c>), and the CPU and 3D-engine time of the app
/// and of the compositor (dwm.exe) from PDH, next to an idle window just before. Writes everything under
/// <c>--evidence</c>.
/// </summary>
internal static unsafe partial class MotionCapture
{
    private const string PhaseMapName = @"Local\Annotator.MotionReference.Phase";
    private const int SheetFrames = 12;
    private const int Columns = 4;
    private const int TileWidth = 480;
    private static readonly string[] AllStyles = ["dissolve", "slide", "magic", "corners", "pop", "drop", "focus", "fade"];
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Run(HarnessOptions options)
    {
        var folder = Path.GetFullPath(options.Evidence ?? throw new ArgumentException("--evidence names the folder for the contact sheets and timings."));
        var app = Path.GetFullPath(options.App ?? DefaultApp());
        if (!File.Exists(app))
        {
            throw new ArgumentException($"The app was not found at {app}. Build Annotator.App (Debug) or pass --app.");
        }
        DisplayInfo display = DisplayEnumerator.GetDisplays().First(candidate => candidate.IsPrimary);
        PixelRect bounds = display.Bounds;
        PixelRect area = options.Area is { } given
            ? PixelRect.FromSize(bounds.Left + given.Left, bounds.Top + given.Top, given.Width, given.Height)
            : PixelRect.FromSize(bounds.Left + (bounds.Width - 1600) / 2, bounds.Top + (bounds.Height - 900) / 2, 1600, 900);
        if (area.Left < bounds.Left || area.Top < bounds.Top || area.Right > bounds.Right || area.Bottom > bounds.Bottom || area.Width < 400 || area.Height < 300)
        {
            throw new ArgumentException($"--area must lie inside the {bounds.Width} x {bounds.Height} primary monitor and be at least 400 x 300 pixels.");
        }
        var refresh = RefreshRate(display.DeviceName);
        Directory.CreateDirectory(folder);
        using var map = MemoryMappedFile.CreateOrOpen(PhaseMapName, 64);
        using var phase = map.CreateViewAccessor();
        phase.Write(0, 0);
        using var capture = new MonitorCapture(display, start: false);
        var monitorArea = PixelRect.FromSize(area.Left - bounds.Left, area.Top - bounds.Top, area.Width, area.Height);
        using var recorder = new Recorder(capture.Devices, phase, monitorArea);
        using var counters = new PerfCounters();
        capture.OnFrame(recorder.Frame);
        capture.Start();
        var rows = new List<JsonObject>();
        _ = TimeBeginPeriod(1);
        try
        {
            foreach (var style in options.Styles ?? AllStyles)
            {
                rows.AddRange(RunStyle(options, app, style, display, area, refresh, folder, phase, recorder, counters));
            }
        }
        finally
        {
            _ = TimeEndPeriod(1);
            capture.OnFrame(null);
        }
        var summary = new JsonObject
        {
            ["display"] = $"{bounds.Width} x {bounds.Height} pixels at {display.Dpi / 96.0:P0}, {refresh} Hz",
            ["area"] = new JsonArray(area.Left, area.Top, area.Width, area.Height),
            ["captured"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            ["transitions"] = new JsonArray([.. rows.Select(row => (JsonNode)new JsonObject
            {
                ["state"] = row["state"]!.DeepClone(),
                ["plannedSeconds"] = row["plannedSeconds"]!.DeepClone(),
                ["frames"] = row["frames"]!.DeepClone(),
                ["fps"] = row["fps"]!.DeepClone(),
                ["frameIntervalMs"] = row["frameIntervalMs"]!.DeepClone(),
                ["cost"] = row["cost"]!.DeepClone(),
            })]),
        };
        var suffix = (options.ReduceMotion ? "-reduced" : "") + (options.Dark ? "-dark" : "-light");
        File.WriteAllText(Path.Combine(folder, $"summary{suffix}.json"), summary.ToJsonString(Indented));
        Console.WriteLine($"Saved {rows.Count} transitions under {folder}.");
    }

    private static List<JsonObject> RunStyle(HarnessOptions options, string app, string style, DisplayInfo display, PixelRect area, int refresh,
                                             string folder, MemoryMappedViewAccessor phase, Recorder recorder, PerfCounters counters)
    {
        var appearance = options.Dark ? "dark" : "light";
        var name = style + (options.ReduceMotion ? "-reduced" : "");
        var output = Path.Combine(folder, name);
        Directory.CreateDirectory(output);
        recorder.Reset();
        phase.Write(0, 0);
        var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
        foreach (var argument in new[]
                 {
                     "--motion-reference", style, "--area", $"{area.Left},{area.Top},{area.Width},{area.Height}", "--out", output,
                     "--timeout", "30", "--theme", appearance, "--objects", options.Objects.ToString(CultureInfo.InvariantCulture),
                 })
        {
            start.ArgumentList.Add(argument);
        }
        if (options.ReduceMotion)
        {
            start.ArgumentList.Add("--reduce-motion");
        }
        var samples = new List<(int Phase, Sample Sample)>();
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("The motion run did not start."))
        {
            var last = -1;
            var clock = Stopwatch.StartNew();
            // Waiting 1 ms at a time (with a 1 ms timer period) is the poll that samples the counters at each phase change.
            while (!process.WaitForExit(1))
            {
                if (clock.Elapsed > RunLimit)
                {
                    process.Kill();
                    throw new TimeoutException($"The motion run for '{style}' did not end within {RunLimit.TotalSeconds:F0} s.");
                }
                var current = phase.ReadInt32(0);
                if (current != last)
                {
                    samples.Add((current, counters.Sample(process)));
                    last = current;
                }
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The motion run for '{style}' ended with exit code {process.ExitCode}.");
            }
        }
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "motion.json")))!.AsObject();
        var failures = report["failures"]!.AsArray();
        foreach (var failure in failures)
        {
            Console.WriteLine($"  {name}: the app reported: {failure}");
        }
        var frequency = (double)report["frequency"]!.GetValue<long>();
        var idle = Window(samples, 3, 1);
        var results = new List<JsonObject>();
        foreach (var node in report["runs"]!.AsArray())
        {
            var run = node!.AsObject();
            var direction = run["direction"]!.GetValue<string>();
            var hiding = direction == "hide";
            var state = $"{direction}-{name}";
            var planned = run["plannedSeconds"]!.GetValue<double>();
            var started = run["motionStartTicks"]!.GetValue<long>() / frequency;
            var requested = run["startTicks"]!.GetValue<long>() / frequency;
            var recorded = hiding ? recorder.Hide : recorder.Show;
            var relative = recorded.Times.Select(ticks => ticks / 1e7 - started).ToList();
            var moving = started > 0 ? relative.Where(time => time >= 0 && time <= planned + 1e-6).ToList() : [];
            var intervals = moving.Zip(moving.Skip(1), (first, second) => (second - first) * 1000).ToList();
            var fps = moving.Count > 1 ? (moving.Count - 1) / (moving[^1] - moving[0]) : 0;
            var window = Window(samples, hiding ? 1 : 2, 0);
            var cost = Cost(window, idle, frequency);
            var result = new JsonObject
            {
                ["appearance"] = appearance,
                ["contactSheetScale"] = Math.Round(TileWidth / (area.Width * 96.0 / display.Dpi), 4),
                ["fps"] = Math.Round(fps, 1),
                ["frames"] = moving.Count,
                ["kind"] = "sequence",
                ["notes"] = $"{(hiding ? "Hide" : "Show")} with the {style} style{(options.ReduceMotion ? " under Reduce Motion" : "")}, "
                            + $"{run["seconds"]!.GetValue<double>():F3} s from the request to the end in the workspace. Times count from the start of the "
                            + "motion on the overlay; Windows.Graphics.Capture delivers a frame only when the screen changes.",
                ["scale"] = display.Dpi / 96.0,
                ["size"] = new JsonArray(Math.Round(area.Width * 96.0 / display.Dpi, 1), Math.Round(area.Height * 96.0 / display.Dpi, 1)),
                ["spec"] = new JsonArray("009", "WIN-MOT-02", "WIN-MOT-04"),
                ["state"] = state,
                ["surface"] = "workspace",
                ["times"] = new JsonArray([.. moving.Select(time => (JsonNode)Math.Round(time, 4))]),
                ["refreshHz"] = refresh,
                ["plannedSeconds"] = Math.Round(planned, 4),
                ["requestToMotionMs"] = started > 0 ? Math.Round((started - requested) * 1000, 1) : null,
                ["frameIntervalMs"] = new JsonObject
                {
                    ["p50"] = Round(Stats.Percentile(intervals, 0.5)),
                    ["p95"] = Round(Stats.Percentile(intervals, 0.95)),
                    ["max"] = intervals.Count > 0 ? Round(intervals.Max()) : null,
                    ["display"] = Math.Round(1000.0 / refresh, 3),
                },
                ["cost"] = cost,
                ["app"] = new JsonObject
                {
                    ["animations"] = run["animations"]!.DeepClone(),
                    ["keyframes"] = run["keyframes"]!.DeepClone(),
                    ["style"] = run["style"]!.DeepClone(),
                    ["failures"] = failures.DeepClone(),
                },
            };
            if (started > 0 && planned > 0)
            {
                var (frames, times) = recorder.Read(recorded, planned);
                if (frames.Count > 0)
                {
                    var sheet = $"{state}-{appearance}.png";
                    WriteSheet(Path.Combine(output, sheet), frames, times);
                    result["contactSheet"] = sheet;
                    result["contactSheetFrames"] = new JsonArray([.. times.Select(time => (JsonNode)Math.Round(time, 4))]);
                }
            }
            File.WriteAllText(Path.Combine(output, $"{state}-{appearance}.json"), result.ToJsonString(Indented));
            Console.WriteLine($"  {state}: planned {planned * 1000:F0} ms, {moving.Count} frames at {fps:F0} fps, intervals {Stats.Summary(intervals)}; "
                              + $"CPU app {cost["appCpuMs"]} ms, dwm {cost["dwmCpuMs"]} ms; 3D app {cost["app3dMs"]} ms, dwm {cost["dwm3dMs"]} ms "
                              + $"(dwm idle {cost["dwm3dIdleMs"]} ms for the same time).");
            results.Add(result);
        }
        return results;
    }

    private static double? Round(double value) => double.IsFinite(value) ? Math.Round(value, 3) : null;

    /// <summary>The samples from the first change to <paramref name="from"/> to the next change to <paramref name="to"/>.</summary>
    private static (Sample Start, Sample End)? Window(List<(int Phase, Sample Sample)> samples, int from, int to)
    {
        var first = samples.FindIndex(sample => sample.Phase == from);
        if (first < 0)
        {
            return null;
        }
        var last = samples.FindIndex(first + 1, sample => sample.Phase == to);
        return last < 0 ? null : (samples[first].Sample, samples[last].Sample);
    }

    /// <summary>CPU and 3D-engine milliseconds of the app and dwm.exe over a window, and dwm's idle rate over as long.</summary>
    private static JsonObject Cost((Sample Start, Sample End)? window, (Sample Start, Sample End)? idle, double frequency)
    {
        if (window is not var (start, end))
        {
            return new JsonObject { ["error"] = "The phase changes were not seen." };
        }
        var seconds = (end.Ticks - start.Ticks) / frequency;
        double? IdleRate(Func<Sample, long> value) => idle is var (begin, finish) && finish.Ticks > begin.Ticks
            ? (value(finish) - value(begin)) / 1e4 / ((finish.Ticks - begin.Ticks) / frequency)
            : null;
        double? Scaled(double? rate) => rate is { } value ? Math.Round(value * seconds, 2) : null;
        return new JsonObject
        {
            ["seconds"] = Math.Round(seconds, 4),
            ["appCpuMs"] = Math.Round((end.AppCpu - start.AppCpu) / 1e4, 2),
            ["dwmCpuMs"] = Math.Round((end.DwmCpu - start.DwmCpu) / 1e4, 2),
            ["app3dMs"] = Math.Round((end.AppGpu - start.AppGpu) / 1e4, 2),
            ["dwm3dMs"] = Math.Round((end.DwmGpu - start.DwmGpu) / 1e4, 2),
            ["appCpuIdleMs"] = Scaled(IdleRate(sample => sample.AppCpu)),
            ["dwmCpuIdleMs"] = Scaled(IdleRate(sample => sample.DwmCpu)),
            ["dwm3dIdleMs"] = Scaled(IdleRate(sample => sample.DwmGpu)),
        };
    }

    /// <summary>The newest development build of the app next to this harness.</summary>
    private static string DefaultApp()
    {
        var windows = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var bin = Path.Combine(windows, "src", "Annotator.App", "bin");
        return Directory.Exists(bin)
            ? new DirectoryInfo(bin).EnumerateFiles("Annotator.exe", SearchOption.AllDirectories)
                .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName ?? Path.Combine(bin, "Annotator.exe")
            : Path.Combine(bin, "Annotator.exe");
    }

    /// <summary>A 4-column sheet of the frames with their times, like the Mac reference sheets.</summary>
    private static void WriteSheet(string path, IReadOnlyList<RasterImage> frames, IReadOnlyList<double> times)
    {
        const int margin = 12, caption = 28;
        var tileHeight = (int)Math.Round(frames[0].Height * (TileWidth / (double)frames[0].Width));
        var rows = (frames.Count + Columns - 1) / Columns;
        var width = margin + Columns * (TileWidth + margin);
        var height = margin + rows * (tileHeight + caption + margin);
        using var devices = GraphicsDevices.Create();
        using var canvas = new OffscreenCanvas(devices);
        using var text = TextSystem.Create();
        var font = new TextFont(FontDesign.Monospace, FontWeight.Regular, 15);
        RasterImage sheet = canvas.Render(width, height, context =>
        {
            ID2D1SolidColorBrush* background = null;
            ID2D1SolidColorBrush* ink = null;
            try
            {
                var backgroundColor = new D2D1_COLOR_F { r = 0.118f, g = 0.118f, b = 0.118f, a = 1 };
                var inkColor = new D2D1_COLOR_F { r = 0.85f, g = 0.85f, b = 0.85f, a = 1 };
                Native.Check(context->CreateSolidColorBrush(&backgroundColor, null, &background), "ID2D1DeviceContext::CreateSolidColorBrush");
                Native.Check(context->CreateSolidColorBrush(&inkColor, null, &ink), "ID2D1DeviceContext::CreateSolidColorBrush");
                var all = D2D.Rect(0, 0, width, height);
                context->FillRectangle(&all, (ID2D1Brush*)background);
                for (var index = 0; index < frames.Count; index++)
                {
                    double x = margin + index % Columns * (TileWidth + margin), y = margin + index / Columns * (tileHeight + caption + margin);
                    ID2D1Bitmap1* bitmap = canvas.Upload(frames[index]);
                    try
                    {
                        var destination = D2D.Rect(x, y, x + TileWidth, y + tileHeight);
                        context->DrawBitmap((ID2D1Bitmap*)bitmap, &destination, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC, null, null);
                    }
                    finally
                    {
                        Native.Release(ref bitmap);
                    }
                    IDWriteTextLayout* layout = text.CreateLayout(string.Create(CultureInfo.InvariantCulture, $"{Math.Round(times[index] * 1000):0} ms"),
                                                                  font, TextAlignment.Left, TileWidth, caption);
                    try
                    {
                        context->DrawTextLayout(D2D.Point(x, y + tileHeight + 5), layout, (ID2D1Brush*)ink, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
                    }
                    finally
                    {
                        Native.Release(ref layout);
                    }
                }
            }
            finally
            {
                Native.Release(ref background);
                Native.Release(ref ink);
            }
        });
        File.WriteAllBytes(path, RasterAsset.EncodePng(sheet));
    }

    private static int RefreshRate(string device)
    {
        var mode = new DeviceMode { Size = (ushort)sizeof(DeviceMode) };
        return EnumDisplaySettings(device, -1, ref mode) ? (int)mode.DisplayFrequency : 0;
    }

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string device, int mode, ref DeviceMode settings);

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    /// <summary>DEVMODEW, for the display frequency.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TrueTypeOption;
        public short Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPixel;
        public uint PixelsWidth;
        public uint PixelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    /// <summary>
    /// Counter values at one moment: steady-clock ticks, 3D-engine and CPU time in 100 ns units, and the app's dedicated and
    /// shared video memory in bytes (-1 where the GPU Process Memory counters are unavailable).
    /// </summary>
    internal readonly record struct Sample(long Ticks, long DwmGpu, long AppGpu, long DwmCpu, long AppCpu, long AppDedicated = -1, long AppShared = -1);

    /// <summary>One show or hide as captured: every frame's composition time and the GPU copies for its contact sheet.</summary>
    internal sealed class Transition
    {
        public List<long> Times { get; } = new(1024);

        public nint[] Sheet { get; } = new nint[SheetFrames];

        public double[] SheetTimes { get; } = new double[SheetFrames];

        public int Next { get; set; }

        public nint Latest { get; set; }

        public bool HasLatest { get; set; }
    }

    /// <summary>
    /// Takes the frames of the run on the capture thread: their times, and GPU copies of the area at 12 even times of each
    /// transition (copies only, so the capture never waits for a readback), plus the newest frame for times after the last
    /// change on screen.
    /// </summary>
    private sealed class Recorder : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly GraphicsDevices _devices;
        private readonly MemoryMappedViewAccessor _phase;
        private readonly PixelRect _area;

        public Recorder(GraphicsDevices devices, MemoryMappedViewAccessor phase, PixelRect area)
        {
            _devices = devices;
            _phase = phase;
            _area = area;
        }

        public Transition Hide { get; private set; } = new();

        public Transition Show { get; private set; } = new();

        public void Reset()
        {
            lock (_gate)
            {
                Release(Hide);
                Release(Show);
                Hide = Allocate();
                Show = Allocate();
            }
        }

        public void Frame(nint texture, TimeSpan time)
        {
            var phase = _phase.ReadInt32(0);
            lock (_gate)
            {
                Transition? transition = phase switch { 1 => Hide, 2 => Show, _ => null };
                if (transition is null || transition.Latest == 0)
                {
                    return;
                }
                transition.Times.Add(time.Ticks);
                Copy(transition.Latest, texture);
                transition.HasLatest = true;
                var started = _phase.ReadInt64(24);
                var duration = _phase.ReadDouble(32);
                var frequency = _phase.ReadInt64(16);
                if (started == 0 || duration <= 0 || frequency <= 0)
                {
                    return;
                }
                var relative = time.Ticks / 1e7 - started / (double)frequency;
                while (transition.Next < SheetFrames && relative >= duration * transition.Next / (SheetFrames - 1))
                {
                    Copy(transition.Sheet[transition.Next], texture);
                    transition.SheetTimes[transition.Next] = relative;
                    transition.Next++;
                }
            }
        }

        /// <summary>
        /// The sheet's frames: those taken on time, then the newest frame for the times after it (the screen did not change
        /// since), read back to the CPU.
        /// </summary>
        public (List<RasterImage> Frames, List<double> Times) Read(Transition transition, double duration)
        {
            lock (_gate)
            {
                if (transition.HasLatest)
                {
                    for (; transition.Next < SheetFrames; transition.Next++)
                    {
                        CopyWhole(transition.Sheet[transition.Next], transition.Latest);
                        transition.SheetTimes[transition.Next] = duration * transition.Next / (SheetFrames - 1);
                    }
                }
                var frames = new List<RasterImage>(transition.Next);
                var times = new List<double>(transition.Next);
                for (var index = 0; index < transition.Next; index++)
                {
                    frames.Add(ReadBack(transition.Sheet[index]));
                    times.Add(transition.SheetTimes[index]);
                }
                return (frames, times);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Release(Hide);
                Release(Show);
            }
        }

        private Transition Allocate()
        {
            var transition = new Transition { Latest = Texture(D3D11_USAGE.D3D11_USAGE_DEFAULT) };
            for (var index = 0; index < SheetFrames; index++)
            {
                transition.Sheet[index] = Texture(D3D11_USAGE.D3D11_USAGE_DEFAULT);
            }
            return transition;
        }

        private static void Release(Transition transition)
        {
            for (var index = 0; index < SheetFrames; index++)
            {
                if (transition.Sheet[index] != 0)
                {
                    Marshal.Release(transition.Sheet[index]);
                    transition.Sheet[index] = 0;
                }
            }
            if (transition.Latest != 0)
            {
                Marshal.Release(transition.Latest);
                transition.Latest = 0;
            }
        }

        private nint Texture(D3D11_USAGE usage)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)_area.Width,
                Height = (uint)_area.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = usage,
                CPUAccessFlags = usage == D3D11_USAGE.D3D11_USAGE_STAGING ? D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ : 0,
            };
            ID3D11Texture2D* texture;
            Native.Check(((ID3D11Device*)_devices.D3D11Device)->CreateTexture2D(&desc, null, &texture), "ID3D11Device::CreateTexture2D");
            return (nint)texture;
        }

        private void Copy(nint destination, nint frame)
        {
            var box = new D3D11_BOX { left = (uint)_area.Left, top = (uint)_area.Top, front = 0, right = (uint)_area.Right, bottom = (uint)_area.Bottom, back = 1 };
            ((ID3D11DeviceContext*)_devices.D3D11DeviceContext)->CopySubresourceRegion((ID3D11Resource*)destination, 0, 0, 0, 0, (ID3D11Resource*)frame, 0, &box);
        }

        private void CopyWhole(nint destination, nint source) =>
            ((ID3D11DeviceContext*)_devices.D3D11DeviceContext)->CopyResource((ID3D11Resource*)destination, (ID3D11Resource*)source);

        private RasterImage ReadBack(nint texture)
        {
            var context = (ID3D11DeviceContext*)_devices.D3D11DeviceContext;
            var staging = (ID3D11Texture2D*)Texture(D3D11_USAGE.D3D11_USAGE_STAGING);
            try
            {
                context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)texture);
                D3D11_MAPPED_SUBRESOURCE mapped;
                Native.Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext::Map");
                try
                {
                    var stride = _area.Width * 4;
                    var pixels = new byte[stride * _area.Height];
                    for (var row = 0; row < _area.Height; row++)
                    {
                        new ReadOnlySpan<byte>((byte*)mapped.pData + (long)row * mapped.RowPitch, stride).CopyTo(pixels.AsSpan(row * stride));
                    }
                    return new RasterImage(_area.Width, _area.Height, stride, pixels);
                }
                finally
                {
                    context->Unmap((ID3D11Resource*)staging, 0);
                }
            }
            finally
            {
                Native.Release(ref staging);
            }
        }
    }

    /// <summary>
    /// PDH counters: the 3D engine's running time per process (GPU Engine, 100 ns units), dwm.exe's processor time and,
    /// where the driver provides them, each process's video memory (GPU Process Memory); the app's processor time comes from
    /// its process. Readable without elevation.
    /// </summary>
    internal sealed partial class PerfCounters : IDisposable
    {
        private const uint MoreData = 0x800007D2;
        private readonly nint _query;
        private readonly nint _engines;
        private readonly nint _compositorCpu;
        private readonly nint _dedicated;
        private readonly nint _shared;
        private readonly string _compositor;

        public PerfCounters()
        {
            using var current = Process.GetCurrentProcess();
            var dwm = Process.GetProcessesByName("dwm");
            _compositor = $"pid_{dwm.FirstOrDefault(process => process.SessionId == current.SessionId)?.Id ?? 0}_";
            foreach (var process in dwm)
            {
                process.Dispose();
            }
            Check(PdhOpenQuery(null, 0, out _query), "PdhOpenQuery");
            Check(PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Running Time", 0, out _engines), @"PdhAddEnglishCounter(\GPU Engine(*)\Running Time)");
            Check(PdhAddEnglishCounter(_query, @"\Process(dwm)\% Processor Time", 0, out _compositorCpu), @"PdhAddEnglishCounter(\Process(dwm)\% Processor Time)");
            if (PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out _dedicated) != 0
                || PdhAddEnglishCounter(_query, @"\GPU Process Memory(*)\Shared Usage", 0, out _shared) != 0)
            {
                (_dedicated, _shared) = (0, 0);
            }
            _ = PdhCollectQueryData(_query);
        }

        public Sample Sample(Process app)
        {
            var ticks = Stopwatch.GetTimestamp();
            long dwmGpu = 0, appGpu = 0, dwmCpu = 0, appCpu = 0, dedicated = -1, shared = -1;
            var prefix = $"pid_{app.Id}_";
            if (PdhCollectQueryData(_query) == 0)
            {
                foreach ((var instance, var value) in Raw(_engines))
                {
                    if (!instance.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (instance.StartsWith(_compositor, StringComparison.OrdinalIgnoreCase))
                    {
                        dwmGpu += value;
                    }
                    else if (instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        appGpu += value;
                    }
                }
                foreach ((_, var value) in Raw(_compositorCpu))
                {
                    dwmCpu += value;
                }
                if (_dedicated != 0)
                {
                    dedicated = Raw(_dedicated).Where(item => item.Instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Sum(item => item.Value);
                    shared = Raw(_shared).Where(item => item.Instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Sum(item => item.Value);
                }
            }
            try
            {
                app.Refresh();
                appCpu = app.TotalProcessorTime.Ticks;
            }
            catch (InvalidOperationException)
            {
            }
            return new Sample(ticks, dwmGpu, appGpu, dwmCpu, appCpu, dedicated, shared);
        }

        public void Dispose() => _ = PdhCloseQuery(_query);

        private static List<(string Instance, long Value)> Raw(nint counter)
        {
            uint size = 0;
            if (PdhGetRawCounterArray(counter, ref size, out _, 0) != MoreData || size == 0)
            {
                return [];
            }
            var buffer = NativeMemory.Alloc(size);
            try
            {
                if (PdhGetRawCounterArray(counter, ref size, out var count, (nint)buffer) != 0)
                {
                    return [];
                }
                var items = (RawItem*)buffer;
                var values = new List<(string, long)>((int)count);
                for (var index = 0; index < count; index++)
                {
                    // PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA.
                    if (items[index].Status is 0 or 1)
                    {
                        values.Add((new string((char*)items[index].Name), items[index].First));
                    }
                }
                return values;
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        private static void Check(uint status, string operation)
        {
            if (status != 0)
            {
                throw new InvalidOperationException($"{operation} failed with PDH status 0x{status:X8}.");
            }
        }

        /// <summary>PDH_RAW_COUNTER_ITEM_W.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct RawItem
        {
            public nint Name;
            public uint Status;
            public uint TimeLow;
            public uint TimeHigh;
            public long First;
            public long Second;
            public uint MultiCount;
        }

        [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhOpenQuery(string? source, nint user, out nint query);

        [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial uint PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);

        [LibraryImport("pdh.dll")]
        private static partial uint PdhCollectQueryData(nint query);

        [LibraryImport("pdh.dll", EntryPoint = "PdhGetRawCounterArrayW")]
        private static partial uint PdhGetRawCounterArray(nint counter, ref uint size, out uint count, nint buffer);

        [LibraryImport("pdh.dll")]
        private static partial uint PdhCloseQuery(nint query);
    }
}
