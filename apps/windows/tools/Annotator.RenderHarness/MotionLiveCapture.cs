using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Presentation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>motion-live</c>: replays the show and hide takes of the Mac's live recordings (<c>reference/ux-reference/macos-live</c>)
/// in the app's development-only motion run (<c>Annotator.exe --motion-reference &lt;style&gt; --take ...</c>) on the Mac's
/// stage, and records them at real speed as Windows.Graphics.Capture composes the screen. <c>sharing-live</c> shares a region
/// of the stage, draws in it, hides and shows the workspace with Magic and stops sharing, and records the output window on
/// its own at the same time as <c>sharing-output</c>; <c>sharing-controls</c> replays only that take's toolbar steps (draw
/// again, tool choices, Use apps), for the toolbar's control motion; <c>effects-live</c> replays the live effects over the
/// stage, which the app captures; <c>effects-measure</c>, <c>sharing-measure</c> and <c>zoom-measure</c> are no Mac takes
/// but measure the app's effects, region sharing and screen zoom on the stage (see <see cref="Measure"/>). For each take it
/// writes, under <c>--evidence</c>: the video re-timed to a constant 60 frames per second as the Mac's were packed (ffmpeg's
/// fps filter: each output frame shows the last frame captured at or before it), a JSON file in the Mac's format with the
/// take's segments and events on the same clock, and the time of every captured frame. Media Foundation reads the Mac's
/// backdrop and encodes the videos (<see cref="VideoWriter"/>); <c>--ffmpeg</c> uses an ffmpeg instead.
/// </summary>
internal static unsafe partial class MotionLiveCapture
{
    private const string PhaseMapName = @"Local\Annotator.MotionReference.Phase";
    private const int Rate = 60;
    private const double AreaWidth = 1440, AreaHeight = 900;

    /// <summary>Media Foundation's H.264 quality for the recordings (0 to 100; see <see cref="VideoWriter"/>).</summary>
    internal const uint VideoQuality = 85;

    /// <summary>The measurements on the stage, which are no Mac takes (see <see cref="Measure"/>).</summary>
    private static readonly string[] MeasureTakes = ["effects-measure", "sharing-measure", "zoom-measure"];
    private static readonly string[] AllTakes =
        ["show-hide-corners", "show-hide-dissolve", "show-hide-drop", "show-hide-fade", "show-hide-focus", "show-hide-magic",
         "show-hide-none", "show-hide-pop", "show-hide-reversal", "show-hide-slide"];
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(45);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Run(HarnessOptions options)
    {
        var folder = Path.GetFullPath(options.Evidence ?? throw new ArgumentException("--evidence names the folder for the recordings."));
        var reference = Path.GetFullPath(options.Reference ?? DefaultReference());
        // Media Foundation reads and encodes the videos unless --ffmpeg names an ffmpeg to use instead.
        var ffmpeg = options.Ffmpeg;
        var app = Path.GetFullPath(options.App ?? DefaultApp());
        if (!File.Exists(app))
        {
            throw new ArgumentException($"The app was not found at {app}. Build Annotator.App (Debug) or pass --app.");
        }
        if (!Directory.Exists(reference))
        {
            throw new ArgumentException($"The Mac recordings were not found at {reference}. Pass --reference.");
        }
        DisplayInfo display = DisplayEnumerator.GetDisplays().First(candidate => candidate.IsPrimary);
        var scale = display.Scale;
        int width = (int)Math.Round(AreaWidth * scale), height = (int)Math.Round(AreaHeight * scale);
        PixelRect work = display.WorkArea;
        // The Mac recorded the bottom center of its display's visible frame; the take puts its stage the same way here.
        PixelRect area = PixelRect.FromSize(work.Left + (work.Width - width) / 2, work.Bottom - height, width, height);
        if (area.Left < display.Bounds.Left || area.Top < display.Bounds.Top || area.Right > display.Bounds.Right || area.Bottom > display.Bounds.Bottom)
        {
            throw new ArgumentException($"A 1,440 x 900-point area does not fit the primary monitor's work area at {scale:P0}.");
        }
        Directory.CreateDirectory(folder);
        var backdrop = options.Backdrop ?? Path.Combine(folder, "mac-backdrop.png");
        if (options.Backdrop is null)
        {
            // Everything is hidden 1.0 s into the Mac's Off take: its frame is the owned backdrop and sample window alone.
            var macOff = Path.Combine(reference, "show-hide-none.mp4");
            if (ffmpeg is not null)
            {
                Ffmpeg(ffmpeg, ["-y", "-loglevel", "error", "-ss", "1.0", "-i", macOff, "-frames:v", "1", backdrop], null);
            }
            else
            {
                File.WriteAllBytes(backdrop, RasterAsset.EncodePng(VideoReader.FrameAt(macOff, 1.0)));
            }
        }
        var picture = RasterAsset.Decode(File.ReadAllBytes(backdrop));
        var refresh = RefreshRate(display.DeviceName);
        using var map = MemoryMappedFile.CreateOrOpen(PhaseMapName, 64);
        using var phase = map.CreateViewAccessor();
        phase.Write(0, 0);
        var takes = options.Takes ?? AllTakes;
        foreach (var measurement in MeasureTakes.Where(takes.Contains))
        {
            // Before the recorder's monitor capture exists, so the compositor does only the stage's and Annotator's work.
            using var measured = StageBackdrop.Start(area, display.Dpi, picture);
            Measure(options, measurement, app, display, area, refresh, folder, phase, measured);
        }
        takes = [.. takes.Where(take => !MeasureTakes.Contains(take))];
        if (takes.Length == 0)
        {
            return;
        }
        using var capture = new MonitorCapture(display, start: false);
        var monitorArea = PixelRect.FromSize(area.Left - display.Bounds.Left, area.Top - display.Bounds.Top, width, height);
        using var stage = StageBackdrop.Start(area, display.Dpi, picture);
        _ = TimeBeginPeriod(1);
        try
        {
            // Only the stage may ever be recorded: check on the newest captured frame that it covers the area before any take.
            RasterImage? latest = null;
            var count = 0;
            var gate = new Lock();
            capture.OnFrame((texture, _) =>
            {
                var image = capture.Read(texture, monitorArea);
                lock (gate)
                {
                    latest = image;
                }
                Interlocked.Increment(ref count);
            });
            capture.Start();
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref count) == 0)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(2))
                {
                    capture.OnFrame(null);
                    throw new NoFramesException("Windows.Graphics.Capture delivered no frame within two seconds.");
                }
                Thread.Sleep(10);
            }
            // Frames that arrive meanwhile replace the first one while the stage finishes showing.
            Thread.Sleep(700);
            capture.OnFrame(null);
            Thread.Sleep(50);
            lock (gate)
            {
                StageBackdrop.Verify(latest!, picture);
            }
            foreach (var take in takes)
            {
                RunTake(options, app, ffmpeg, reference, take, display, area, refresh, folder, phase, capture, monitorArea, picture);
            }
        }
        finally
        {
            _ = TimeEndPeriod(1);
            capture.OnFrame(null);
        }
        Console.WriteLine($"Saved the takes under {folder}.");
    }

    private static void RunTake(HarnessOptions options, string app, string? ffmpeg, string reference, string take, DisplayInfo display,
                                PixelRect area, int refresh, string folder, MemoryMappedViewAccessor phase, MonitorCapture capture, PixelRect monitorArea,
                                RasterImage picture)
    {
        // sharing-controls is sharing-live's toolbar steps alone (draw again, tool choices, Use apps), as the app's controls take.
        var macPath = Path.Combine(reference, (take == "sharing-controls" ? "sharing-live" : take) + ".json");
        if (!File.Exists(macPath))
        {
            throw new ArgumentException($"The Mac take '{take}' was not found at {macPath}.");
        }
        var mac = JsonNode.Parse(File.ReadAllText(macPath))!.AsObject();
        var style = mac["style"]?.GetValue<string>() ?? "slide";
        var reversal = take == "show-hide-reversal";
        var kind = reversal ? "reversal" : take switch
        {
            "sharing-controls" => "controls",
            "sharing-live" => "sharing",
            "effects-live" => "effects",
            "zoom-screen" or "zoom-lens" => take,
            _ => "show-hide",
        };
        var dark = options.ThemeGiven ? options.Dark : mac["appearance"]?.GetValue<string>() == "dark";
        var output = Path.Combine(folder, take);
        Directory.CreateDirectory(output);
        phase.Write(0, 0);
        using var recorder = new Recorder(capture.Devices, phase, monitorArea);
        // sharing-live records the output window on its own at the same time, as the Mac's sharing-output.
        using var shared = kind == "sharing" ? new OutputRecording(phase) : null;
        capture.OnFrame(recorder.Frame);
        var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
        foreach (var argument in new[]
                 {
                     "--motion-reference", style, "--take", kind, "--area",
                     $"{area.Left},{area.Top},{area.Width},{area.Height}", "--out", Path.Combine(output, "app"),
                     "--timeout", "30", "--theme", dark ? "dark" : "light",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        if (options.ReduceMotion)
        {
            start.ArgumentList.Add("--reduce-motion");
        }
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("The motion run did not start."))
        {
            if (!process.WaitForExit(RunLimit))
            {
                process.Kill();
                throw new TimeoutException($"The take '{take}' did not end within {RunLimit.TotalSeconds:F0} s.");
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The take '{take}' ended with exit code {process.ExitCode}.");
            }
        }
        capture.OnFrame(null);
        recorder.Finish();
        shared?.Finish();
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "app", "motion.json")))!.AsObject();
        foreach (var failure in report["failures"]!.AsArray())
        {
            Console.WriteLine($"  {take}: the app reported: {failure}");
        }
        var frequency = (double)report["frequency"]!.GetValue<long>();
        var info = report["take"]!.AsObject();
        var t0 = info["startTicks"]!.GetValue<long>() / frequency;
        var t1 = info["endTicks"]!.GetValue<long>() / frequency;
        if (Math.Abs(recorder.Origin - t0) > 1e-6)
        {
            throw new InvalidOperationException($"The take '{take}' started at {t0:F6} s but the recording counted from {recorder.Origin:F6} s.");
        }
        double Relative(JsonNode? ticks) => ticks!.GetValue<long>() / frequency - t0;
        var duration = t1 - t0;
        var video = $"windows-{take}.mp4";
        var slots = (int)Math.Round(duration * Rate);
        // Screen zoom magnifies the stage into the area's corners, so the corners are checked only before and after it.
        var zoomed = (First: int.MaxValue, Last: int.MinValue);
        foreach (var segment in info["segments"]!.AsArray())
        {
            if (segment!["label"]!.GetValue<string>() is "zoom in" or "zoom out" or "lens on" or "lens off")
            {
                zoomed = (Math.Min(zoomed.First, (int)Math.Floor(Relative(segment["startTicks"]) * Rate)),
                          Math.Max(zoomed.Last, (int)Math.Ceiling(Relative(segment["endTicks"]) * Rate) + 6));
            }
        }
        if (recorder.FirstForeign(slots, picture, slot => slot >= zoomed.First && slot <= zoomed.Last) is { } foreign)
        {
            // Something else covered the stage (a notification, another always-on-top window): keep nothing of the take.
            Directory.Delete(output, recursive: true);
            throw new InvalidOperationException($"Another window covered a corner of the stage at {foreign / (double)Rate:F3} s into '{take}', so the take was deleted.");
        }
        var toolbarRect = info["toolbar"]!.AsArray();
        var toolbarArea = PixelRect.FromSize(toolbarRect[0]!.GetValue<int>() - area.Left, toolbarRect[1]!.GetValue<int>() - area.Top,
                                             toolbarRect[2]!.GetValue<int>(), toolbarRect[3]!.GetValue<int>());
        if (recorder.ShowsStage(0, toolbarArea, picture)
            || (kind is "controls" or "effects" or "sharing" or "zoom-screen" or "zoom-lens" && recorder.ShowsStage(slots, toolbarArea, picture)))
        {
            // Every take starts with the toolbar on screen (and the controls, effects, sharing and zoom takes end with it): the stage was above it.
            Directory.Delete(output, recursive: true);
            throw new InvalidOperationException($"The stage covered Annotator's toolbar in '{take}', so the take was deleted.");
        }
        // The Mac's recordings show its scripted pointer (a window of the recorder above everything): draw it where the take's
        // pointer was at each frame.
        var stageDisplay = info["display"]!.AsArray();
        var pointer = ScriptedPointer.FromReport(info["pointer"]?.AsArray(), frequency, t0, display.Scale,
            stageDisplay[0]!.GetValue<int>() - area.Left, stageDisplay[1]!.GetValue<int>() - area.Top);
        WriteVideo(ffmpeg, Path.Combine(output, video), recorder, slots, pointer);
        var segments = new JsonArray();
        foreach (var segment in info["segments"]!.AsArray())
        {
            segments.Add(new JsonObject
            {
                ["label"] = segment!["label"]!.GetValue<string>(),
                ["start"] = Math.Round(Relative(segment["startTicks"]), 6),
                ["end"] = Math.Round(Relative(segment["endTicks"]), 6),
                ["completed"] = Math.Round(Relative(segment["completedTicks"]), 6),
            });
        }
        var events = new JsonArray();
        foreach (var item in info["events"]!.AsArray())
        {
            events.Add(new JsonObject { ["time"] = Math.Round(Relative(item!["ticks"]), 6), ["label"] = item["label"]!.GetValue<string>() });
        }
        var starts = new JsonArray();
        foreach (var item in info["motionStarts"]!.AsArray())
        {
            starts.Add(new JsonObject { ["time"] = Math.Round(Relative(item!["ticks"]), 6), ["plannedSeconds"] = item["plannedSeconds"]!.GetValue<double>() });
        }
        var toolbarReports = new JsonArray();
        foreach (var item in info["toolbarReports"]!.AsArray())
        {
            toolbarReports.Add(new JsonObject { ["time"] = Math.Round(Relative(item!["ticks"]), 6), ["kind"] = item["kind"]!.GetValue<string>() });
        }
        var visibility = VisibilityAnimationStyle.FromRawValue(style) ?? VisibilityAnimationStyle.Slide;
        var result = new JsonObject
        {
            ["take"] = take,
            ["title"] = mac["title"]?.GetValue<string>(),
            ["notes"] = mac["notes"]?.GetValue<string>(),
            ["style"] = style,
            ["appearance"] = dark ? "dark" : "light",
            ["video"] = video,
            ["duration"] = Math.Round(duration, 6),
            ["fps"] = Rate,
            ["packedFps"] = Rate,
            ["pixels"] = new JsonArray(area.Width & ~1, area.Height & ~1),
            ["scale"] = display.Scale,
            ["areaPoints"] = new JsonObject { ["x"] = 560, ["y"] = 540, ["width"] = AreaWidth, ["height"] = AreaHeight },
            ["areaPixels"] = new JsonArray(area.Left, area.Top, area.Width, area.Height),
            ["display"] = new JsonObject
            {
                ["name"] = display.DeviceName,
                ["points"] = new JsonArray(2560, 1440),
                ["monitorPixels"] = new JsonArray(display.Bounds.Width, display.Bounds.Height),
                ["scale"] = display.Scale,
                ["refreshRate"] = refresh,
            },
            ["hideDuration"] = MotionTiming.VisibilityDuration(visibility, appearing: false),
            ["showDuration"] = MotionTiming.VisibilityDuration(visibility, appearing: true),
            ["segments"] = segments,
            ["events"] = events,
            ["motionStarts"] = starts,
            ["toolbarReports"] = toolbarReports,
            ["toolbarPixels"] = info["toolbar"]!.DeepClone(),
            ["pointer"] = pointer?.ToJson(),
            ["frames"] = recorder.Times.Count,
            ["frameTimes"] = new JsonArray([.. recorder.Times.Select(time => (JsonNode)Math.Round(time - t0, 6))]),
            ["recorded"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["input"] = "Model calls in the development-only motion run (Annotator.exe --motion-reference --take); not human input.",
            ["source"] = "Windows.Graphics.Capture of the primary monitor, cropped to the area: the toolbar, the overlay and the motion over the harness's stage window, which shows the Mac recording's backdrop and sample window. The take's scripted pointer is drawn onto each frame where it was at that time, as the Mac recorder's pointer window shows it.",
            ["packing"] = Packing(ffmpeg) + " frameTimes are the capture's composition times.",
            ["failures"] = report["failures"]!.DeepClone(),
        };
        if (reversal)
        {
            result["reverseAfter"] = MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Slide, appearing: false) * 0.45;
        }
        if (info["sharing"] is JsonObject sharing)
        {
            var reports = new JsonArray();
            foreach (var item in sharing["reports"]!.AsArray())
            {
                reports.Add(new JsonObject { ["time"] = Math.Round(Relative(item!["ticks"]), 6), ["state"] = item["state"]!.GetValue<string>() });
            }
            result["sharing"] = new JsonObject
            {
                ["regionPoints"] = sharing["regionPoints"]?.DeepClone(),
                ["outputWindowPixels"] = sharing["outputWindow"]!.DeepClone(),
                ["stateAtEnd"] = sharing["state"]?.DeepClone(),
                ["outputReports"] = reports,
                ["motion"] = sharing["sharingMotion"]?.DeepClone(),
            };
            WriteSharingOutput(ffmpeg, reference, folder, shared!, sharing, slots, t0, duration, dark, display, refresh);
        }
        File.WriteAllText(Path.Combine(output, $"windows-{take}.json"), result.ToJsonString(Indented));
        Console.WriteLine($"  {take}: {duration:F2} s, {recorder.Times.Count} frames captured, {slots} at 60 fps; segments "
                          + string.Join(", ", segments.Select(segment => $"{segment!["label"]} {segment["start"]}-{segment["end"]}")));
    }

    /// <summary>
    /// sharing-output: the output window's recording on the take's clock, as a video and a JSON file in the Mac's format.
    /// </summary>
    private static void WriteSharingOutput(string? ffmpeg, string reference, string folder, OutputRecording shared, JsonObject sharing, int slots,
                                           double t0, double duration, bool dark, DisplayInfo display, int refresh)
    {
        if (shared.Error is { } error)
        {
            throw new InvalidOperationException($"The output window could not be recorded: {error.Message}", error);
        }
        Recorder recorder = shared.Recorder ?? throw new InvalidOperationException("The run never named its output window, so it was not recorded.");
        if (Math.Abs(recorder.Origin - t0) > 1e-6)
        {
            throw new InvalidOperationException($"The output window's recording counted from {recorder.Origin:F6} s, not the take's start at {t0:F6} s.");
        }
        var macPath = Path.Combine(reference, "sharing-output.json");
        JsonObject? mac = File.Exists(macPath) ? JsonNode.Parse(File.ReadAllText(macPath))!.AsObject() : null;
        var output = Path.Combine(folder, "sharing-output");
        Directory.CreateDirectory(output);
        const string video = "windows-sharing-output.mp4";
        WriteVideo(ffmpeg, Path.Combine(output, video), recorder, slots);
        var result = new JsonObject
        {
            ["take"] = "sharing-output",
            ["title"] = mac?["title"]?.GetValue<string>() ?? "Region sharing: the output window",
            ["notes"] = "The output window people share in a call, recorded with the sharing-live take on its clock.",
            ["appearance"] = dark ? "dark" : "light",
            ["video"] = video,
            ["duration"] = Math.Round(duration, 6),
            ["fps"] = Rate,
            ["packedFps"] = Rate,
            ["pixels"] = new JsonArray(recorder.Width & ~1, recorder.Height & ~1),
            ["scale"] = display.Scale,
            ["areaPixels"] = sharing["outputWindow"]!.DeepClone(),
            ["display"] = new JsonObject
            {
                ["name"] = display.DeviceName,
                ["monitorPixels"] = new JsonArray(display.Bounds.Width, display.Bounds.Height),
                ["scale"] = display.Scale,
                ["refreshRate"] = refresh,
            },
            ["segments"] = new JsonArray(),
            ["events"] = new JsonArray(),
            ["startsBeforeSharingLive"] = 0.0,
            ["frames"] = recorder.Times.Count,
            ["frameTimes"] = new JsonArray([.. recorder.Times.Select(time => (JsonNode)Math.Round(time - t0, 6))]),
            ["recorded"] = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["input"] = "Model calls and scripted canvas input in the development-only motion run (Annotator.exe --motion-reference --take sharing); not human input.",
            ["source"] = "Windows.Graphics.Capture of the output window alone, with its title bar and without the system pointer, as a meeting app's window share sees it (the Mac recorder's desktopIndependentWindow filter). The window's own arrow follows the scripted pointer.",
            ["packing"] = Packing(ffmpeg) + " frameTimes are the capture's composition times; a window capture delivers a frame only when the window changes.",
        };
        File.WriteAllText(Path.Combine(output, "windows-sharing-output.json"), result.ToJsonString(Indented));
        Console.WriteLine($"  sharing-output: {recorder.Width} x {recorder.Height}, {recorder.Times.Count} frames captured, {slots} at 60 fps");
    }

    private static string Packing(string? ffmpeg) => ffmpeg is null
        ? $"Re-timed to a constant 60 fps (H.264, Media Foundation's encoder at quality {VideoQuality}) as ffmpeg's fps filter does: each frame shows the last captured frame at or before it."
        : "Re-timed to a constant 60 fps (H.264, CRF 18) as ffmpeg's fps filter does: each frame shows the last captured frame at or before it.";

    /// <summary>
    /// Encodes the 60 fps frames as H.264, cropped to even sizes, with <paramref name="pointer"/> drawn on each: with Media
    /// Foundation, or with ffmpeg (raw BGRA piped in) when <paramref name="ffmpeg"/> names it.
    /// </summary>
    private static void WriteVideo(string? ffmpeg, string path, Recorder recorder, int slots, ScriptedPointer? pointer = null)
    {
        var frame = pointer is null ? null : new byte[recorder.Width * recorder.Height * 4];
        byte[] Picture(int slot)
        {
            var pixels = recorder.Slot(slot);
            if (frame is null)
            {
                return pixels;
            }
            pixels.CopyTo(frame, 0);
            pointer!.Draw(frame, recorder.Width, recorder.Height, slot / (double)Rate);
            return frame;
        }
        if (ffmpeg is null)
        {
            var writer = VideoWriter.Create(path, recorder.Width & ~1, recorder.Height & ~1, Rate, VideoQuality);
            try
            {
                for (var slot = 0; slot <= slots; slot++)
                {
                    writer.Write(Picture(slot), recorder.Width * 4);
                }
                writer.Finish();
            }
            finally
            {
                writer.Dispose();
            }
            return;
        }
        Ffmpeg(ffmpeg, ["-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{recorder.Width}x{recorder.Height}", "-r", "60", "-i", "-",
                        "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2:0:0", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                        "-movflags", "+faststart", path],
               input =>
               {
                   for (var slot = 0; slot <= slots; slot++)
                   {
                       input.Write(Picture(slot));
                   }
               });
    }

    private static void Ffmpeg(string ffmpeg, string[] arguments, Action<Stream>? input)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardInput = input is not null };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg did not start.");
        if (input is not null)
        {
            using (var stream = process.StandardInput.BaseStream)
            {
                input(stream);
            }
        }
        if (!process.WaitForExit(TimeSpan.FromSeconds(180)))
        {
            process.Kill();
            throw new TimeoutException("ffmpeg did not finish within 180 s.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg ended with exit code {process.ExitCode}.");
        }
    }

    private static string DefaultReference() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..", "reference", "ux-reference", "macos-live"));

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
    /// The recording's desktop, as the Mac recorder's owned backdrop and sample window: a click-through window covering the
    /// area with the Mac recording's picture, on its own overlay thread. It is created before the takes' apps, whose windows
    /// stay above it, and it stays while the workspace hides, so a capture of the area shows only the stage and Annotator.
    /// </summary>
    private sealed class StageBackdrop : IDisposable
    {
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
        private readonly DispatcherThread _thread = DispatcherThread.Start("Motion-live stage");
        private readonly List<(Windows.UI.Composition.SpriteVisual Bar, Windows.UI.Composition.CompositionColorBrush Brush)> _moving = [];
        private GraphicsDevices? _devices;
        private OverlayManager? _manager;
        private ReferencePicture? _picture;

        public static StageBackdrop Start(PixelRect area, uint dpi, RasterImage picture)
        {
            var stage = new StageBackdrop();
            try
            {
                stage.Await(stage._thread.RunAsync(() =>
                {
                    stage._devices = GraphicsDevices.Create();
                    stage._manager = OverlayManager.Create(new OverlayManagerOptions
                    {
                        Registry = new WindowRegistry(),
                        InputSink = new IgnoredInput(),
                        InitialMode = OverlayInputMode.UseApps,
                        // The stage stays under the app's windows: it never puts itself back on top.
                        RestoresStackOrder = false,
                        DisplaySource = () => [new DisplayInfo(0, "motion-live-stage", area, area, dpi, IsPrimary: false)],
                    });
                    OverlayWindow overlay = stage._manager.Overlays[0];
                    stage._picture = new ReferencePicture(stage._manager.Compositor, stage._devices, picture, new PixelRect(0, 0, area.Width, area.Height));
                    overlay.Root.Children.InsertAtBottom(stage._picture.Visual);
                }), "showing the stage");
                // Let the compositor show it before anything is captured.
                Thread.Sleep(300);
            }
            catch
            {
                stage.Dispose();
                throw;
            }
            return stage;
        }

        /// <summary>
        /// Throws unless <paramref name="shown"/> (the captured area) shows the picture: at least 60 of 64 points within 48 per
        /// channel of the picture scaled to the area.
        /// </summary>
        public static void Verify(RasterImage shown, RasterImage picture)
        {
            var matches = 0;
            var misses = new List<string>();
            for (var row = 0; row < 8; row++)
            {
                for (var column = 0; column < 8; column++)
                {
                    int x = (column * 2 + 1) * shown.Width / 16, y = (row * 2 + 1) * shown.Height / 16;
                    var actual = shown[x, y];
                    var expected = picture[x * picture.Width / shown.Width, y * picture.Height / shown.Height];
                    if (Math.Abs(actual.Red - expected.Red) <= 48 && Math.Abs(actual.Green - expected.Green) <= 48 && Math.Abs(actual.Blue - expected.Blue) <= 48)
                    {
                        matches++;
                    }
                    else
                    {
                        misses.Add($"({x}, {y})");
                    }
                }
            }
            if (matches < 60)
            {
                throw new InvalidOperationException($"The stage does not cover the area ({matches} of 64 points match; not at {string.Join(", ", misses)}), so nothing was recorded.");
            }
        }

        /// <summary>
        /// Changes the stage under <paramref name="rects"/> (stage pixels) on every composition frame: a bar a third as wide
        /// sweeps across each rectangle and cycles its color. The compositor runs the animations, so the harness does no
        /// work per frame.
        /// </summary>
        public void Animate(IReadOnlyList<PixelRect> rects) => Await(_thread.RunAsync(() =>
        {
            Windows.UI.Composition.Compositor compositor = _manager!.Compositor;
            Windows.UI.Composition.CompositionEasingFunction linear = compositor.CreateLinearEasingFunction();
            foreach (PixelRect rect in rects)
            {
                float width = Math.Max(8, rect.Width / 3f);
                Windows.UI.Composition.CompositionColorBrush brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 230, 80, 40));
                Windows.UI.Composition.SpriteVisual bar = compositor.CreateSpriteVisual();
                bar.Brush = brush;
                bar.Size = new System.Numerics.Vector2(width, rect.Height);
                bar.Offset = new System.Numerics.Vector3(rect.Left, rect.Top, 0);
                Windows.UI.Composition.Vector3KeyFrameAnimation sweep = compositor.CreateVector3KeyFrameAnimation();
                sweep.InsertKeyFrame(0, new System.Numerics.Vector3(rect.Left, rect.Top, 0), linear);
                sweep.InsertKeyFrame(0.5f, new System.Numerics.Vector3(rect.Right - width, rect.Top, 0), linear);
                sweep.InsertKeyFrame(1, new System.Numerics.Vector3(rect.Left, rect.Top, 0), linear);
                sweep.Duration = TimeSpan.FromSeconds(1.2);
                sweep.IterationBehavior = Windows.UI.Composition.AnimationIterationBehavior.Forever;
                Windows.UI.Composition.ColorKeyFrameAnimation colors = compositor.CreateColorKeyFrameAnimation();
                colors.InsertKeyFrame(0, Windows.UI.Color.FromArgb(255, 230, 80, 40), linear);
                colors.InsertKeyFrame(0.33f, Windows.UI.Color.FromArgb(255, 40, 160, 230), linear);
                colors.InsertKeyFrame(0.66f, Windows.UI.Color.FromArgb(255, 60, 200, 90), linear);
                colors.InsertKeyFrame(1, Windows.UI.Color.FromArgb(255, 230, 80, 40), linear);
                colors.Duration = TimeSpan.FromSeconds(0.9);
                colors.IterationBehavior = Windows.UI.Composition.AnimationIterationBehavior.Forever;
                _manager.Overlays[0].Root.Children.InsertAtTop(bar);
                bar.StartAnimation("Offset", sweep);
                brush.StartAnimation("Color", colors);
                _moving.Add((bar, brush));
            }
        }), "animating the stage");

        /// <summary>Removes what <see cref="Animate"/> added.</summary>
        public void StopAnimating() => Await(_thread.RunAsync(() =>
        {
            foreach (var (bar, brush) in _moving)
            {
                _manager!.Overlays[0].Root.Children.Remove(bar);
                bar.Dispose();
                brush.Dispose();
            }
            _moving.Clear();
        }), "stopping the stage's animation");

        public void Dispose()
        {
            try
            {
                if (!_thread.IsShutDown)
                {
                    Await(_thread.RunAsync(() =>
                    {
                        foreach (var (bar, brush) in _moving)
                        {
                            bar.Dispose();
                            brush.Dispose();
                        }
                        _moving.Clear();
                        _picture?.Dispose();
                        _manager?.Dispose();
                        _devices?.Dispose();
                    }), "removing the stage");
                }
            }
            finally
            {
                if (!((IAsyncResult)_thread.ShutdownAsync()).AsyncWaitHandle.WaitOne(StepTimeout))
                {
                    Console.Error.WriteLine("WARNING: the stage thread did not shut down in time; the process exit removes it.");
                }
            }
        }

        private void Await(Task task, string step)
        {
            if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(StepTimeout))
            {
                throw new TimeoutException($"The stage thread did not finish {step} within {StepTimeout.TotalSeconds:F0} s.");
            }
            task.GetAwaiter().GetResult();
        }

        private sealed class IgnoredInput : IOverlayInputSink
        {
            public void OnPointer(OverlayWindow overlay, in PointerInput input)
            {
            }

            public bool OnKey(OverlayWindow overlay, in KeyInput input) => false;

            public void OnInputReleaseRequested(OverlayWindow overlay, InputReleaseReason reason)
            {
            }

            public void OnFocusChanged(OverlayWindow overlay, bool hasFocus)
            {
            }
        }
    }

    /// <summary>
    /// Takes the take's frames on the capture thread. Each frame goes to a GPU copy of the area; when a frame belongs to a
    /// later 60 fps slot, the copy of the previous one is read back as its slot's picture, so the CPU reads at most one frame
    /// per slot. Before the take starts it keeps only the newest frame, which is what the first slot shows.
    /// </summary>
    private sealed class Recorder : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly GraphicsDevices _devices;
        private readonly MemoryMappedViewAccessor _phase;
        private readonly PixelRect _area;
        private readonly Dictionary<int, byte[]> _slots = [];
        private ID3D11Texture2D* _pending;
        private ID3D11Texture2D* _staging;
        private int _pendingSlot = -1;
        private bool _hasPending;
        private bool _finished;

        public Recorder(GraphicsDevices devices, MemoryMappedViewAccessor phase, PixelRect area)
        {
            _devices = devices;
            _phase = phase;
            _area = area;
            _pending = Texture(D3D11_USAGE.D3D11_USAGE_DEFAULT);
            _staging = Texture(D3D11_USAGE.D3D11_USAGE_STAGING);
        }

        public int Width => _area.Width;

        public int Height => _area.Height;

        /// <summary>The take's start on the steady clock, in seconds; 0 until it started.</summary>
        public double Origin { get; private set; }

        /// <summary>Every captured frame's composition time during the take, in seconds on the steady clock.</summary>
        public List<double> Times { get; } = new(2048);

        public void Frame(nint texture, TimeSpan time)
        {
            lock (_gate)
            {
                if (_finished)
                {
                    return;
                }
                var phase = _phase.ReadInt32(0);
                if (Origin == 0 && phase == 4)
                {
                    Origin = _phase.ReadInt64(8) / (double)_phase.ReadInt64(16);
                }
                var seconds = time.Ticks / 1e7;
                var slot = Origin == 0 ? -1 : Math.Max(0, (int)Math.Round((seconds - Origin) * Rate, MidpointRounding.AwayFromZero));
                if (Origin != 0 && phase != 4)
                {
                    // The take ended: later frames are not part of it.
                    return;
                }
                // A frame from before the take (slot -1) is the first slot's picture unless a frame of that slot replaces it.
                if (_hasPending && slot != _pendingSlot && !(_pendingSlot == -1 && slot == 0))
                {
                    _slots[Math.Max(0, _pendingSlot)] = ReadBack();
                }
                Copy(texture);
                _hasPending = true;
                _pendingSlot = slot;
                if (Origin != 0)
                {
                    Times.Add(seconds);
                }
            }
        }

        /// <summary>Reads back the last frame after the capture stopped calling <see cref="Frame"/>.</summary>
        public void Finish()
        {
            lock (_gate)
            {
                if (_hasPending)
                {
                    _slots[Math.Max(0, _pendingSlot)] = ReadBack();
                }
                _finished = true;
            }
        }

        /// <summary>The picture of a 60 fps slot: its own last frame, else the latest slot before it.</summary>
        public byte[] Slot(int slot)
        {
            for (var index = slot; index >= 0; index--)
            {
                if (_slots.TryGetValue(index, out var pixels))
                {
                    return pixels;
                }
            }
            throw new InvalidOperationException("No frame was captured before the take started.");
        }

        /// <summary>
        /// The first slot whose 24-pixel corner patches do not show the stage's picture (within 48 per channel on average), or
        /// null; slots that <paramref name="skip"/> names are not checked. Annotator draws nothing there during the takes,
        /// except screen zoom, so a difference means another window covered the stage.
        /// </summary>
        public int? FirstForeign(int slots, RasterImage picture, Func<int, bool>? skip = null)
        {
            const int patch = 24;
            (int X, int Y)[] corners = [(0, 0), (_area.Width - patch, 0), (0, _area.Height - patch), (_area.Width - patch, _area.Height - patch)];
            for (var slot = 0; slot <= slots; slot++)
            {
                if (!_slots.TryGetValue(slot, out var pixels) || skip?.Invoke(slot) == true)
                {
                    continue;
                }
                foreach (var (left, top) in corners)
                {
                    long difference = 0;
                    for (var y = top; y < top + patch; y++)
                    {
                        for (var x = left; x < left + patch; x++)
                        {
                            var offset = (y * _area.Width + x) * 4;
                            var expected = picture[x * picture.Width / _area.Width, y * picture.Height / _area.Height];
                            difference += Math.Abs(pixels[offset + 2] - expected.Red) + Math.Abs(pixels[offset + 1] - expected.Green)
                                          + Math.Abs(pixels[offset] - expected.Blue);
                        }
                    }
                    if (difference > 48L * 3 * patch * patch)
                    {
                        return slot;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Whether a 24-pixel patch at the middle of <paramref name="rect"/> (area pixels) shows the stage's picture in
        /// <paramref name="slot"/>, so a window that should be there, such as the toolbar, is missing.
        /// </summary>
        public bool ShowsStage(int slot, PixelRect rect, RasterImage picture)
        {
            const int patch = 24;
            var pixels = Slot(slot);
            int left = Math.Clamp(rect.Left + rect.Width / 2 - patch / 2, 0, _area.Width - patch);
            int top = Math.Clamp(rect.Top + rect.Height / 2 - patch / 2, 0, _area.Height - patch);
            long difference = 0;
            for (var y = top; y < top + patch; y++)
            {
                for (var x = left; x < left + patch; x++)
                {
                    var offset = (y * _area.Width + x) * 4;
                    var expected = picture[x * picture.Width / _area.Width, y * picture.Height / _area.Height];
                    difference += Math.Abs(pixels[offset + 2] - expected.Red) + Math.Abs(pixels[offset + 1] - expected.Green)
                                  + Math.Abs(pixels[offset] - expected.Blue);
                }
            }
            return difference <= 48L * 3 * patch * patch;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Native.Release(ref _pending);
                Native.Release(ref _staging);
                _slots.Clear();
            }
        }

        private ID3D11Texture2D* Texture(D3D11_USAGE usage)
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
            return texture;
        }

        private void Copy(nint frame)
        {
            var box = new D3D11_BOX { left = (uint)_area.Left, top = (uint)_area.Top, front = 0, right = (uint)_area.Right, bottom = (uint)_area.Bottom, back = 1 };
            ((ID3D11DeviceContext*)_devices.D3D11DeviceContext)->CopySubresourceRegion((ID3D11Resource*)_pending, 0, 0, 0, 0, (ID3D11Resource*)frame, 0, &box);
        }

        private byte[] ReadBack()
        {
            var context = (ID3D11DeviceContext*)_devices.D3D11DeviceContext;
            context->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_pending);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Native.Check(context->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "ID3D11DeviceContext::Map");
            try
            {
                var stride = _area.Width * 4;
                var pixels = GC.AllocateUninitializedArray<byte>(stride * _area.Height);
                for (var row = 0; row < _area.Height; row++)
                {
                    new ReadOnlySpan<byte>((byte*)mapped.pData + (long)row * mapped.RowPitch, stride).CopyTo(pixels.AsSpan(row * stride));
                }
                return pixels;
            }
            finally
            {
                context->Unmap((ID3D11Resource*)_staging, 0);
            }
        }
    }

    /// <summary>
    /// The sharing take's output window, recorded on its own as the Mac's sharing-output: a thread waits for the run to
    /// write the window's handle (offset 40 of the shared memory), captures that window from then on with a recorder on the
    /// take's clock, and writes 1 at offset 48 so the run starts its take only once the recording runs.
    /// </summary>
    private sealed class OutputRecording : IDisposable
    {
        private readonly MemoryMappedViewAccessor _phase;
        private readonly Thread _thread;
        private volatile bool _stop;
        private MonitorCapture? _capture;

        public OutputRecording(MemoryMappedViewAccessor phase)
        {
            _phase = phase;
            phase.Write(40, 0L);
            phase.Write(48, 0);
            _thread = new Thread(Watch) { IsBackground = true, Name = "Output window recording" };
            _thread.Start();
        }

        /// <summary>The output window's recorder, once the run named the window.</summary>
        public Recorder? Recorder { get; private set; }

        /// <summary>Why the output window could not be captured, or null.</summary>
        public Exception? Error { get; private set; }

        /// <summary>After the run ended: stops taking frames and reads back the last one.</summary>
        public void Finish()
        {
            if (!Stop())
            {
                throw new TimeoutException("The output window's recording did not start within 10 seconds.");
            }
            _capture?.OnFrame(null);
            Recorder?.Finish();
        }

        public void Dispose()
        {
            if (Stop())
            {
                _capture?.Dispose();
                Recorder?.Dispose();
            }
        }

        private bool Stop()
        {
            _stop = true;
            return _thread.Join(TimeSpan.FromSeconds(10));
        }

        private void Watch()
        {
            try
            {
                while (!_stop)
                {
                    var window = (nint)_phase.ReadInt64(40);
                    if (window != 0)
                    {
                        var capture = MonitorCapture.ForWindow(window, start: false);
                        _capture = capture;
                        var (width, height) = capture.Size;
                        var recorder = new Recorder(capture.Devices, _phase, PixelRect.FromSize(0, 0, width, height));
                        Recorder = recorder;
                        capture.OnFrame(recorder.Frame);
                        capture.Start();
                        _phase.Write(48, 1);
                        return;
                    }
                    Thread.Sleep(10);
                }
            }
            catch (Exception error)
            {
                Error = error;
            }
        }
    }
}
