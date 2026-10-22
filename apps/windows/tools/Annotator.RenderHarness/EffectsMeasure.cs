using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text.Json.Nodes;
using Annotator.Platform.Displays;

namespace Annotator.RenderHarness;

internal static partial class MotionLiveCapture
{
    private static readonly TimeSpan MeasureLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>effects-live's regions in the sample content's points (UXLiveRecordingTests.recordEffects).</summary>
    private static readonly (string Label, double X, double Y, double Width, double Height)[] EffectRegions =
        [("blur", 40, 40, 488, 296), ("pixelate", 588, 446, 552, 180), ("distort", 580, 248, 290, 160)];

    /// <summary>
    /// What each measurement changes on the stage, in the sample content's points: in phase 7 what the app shows (the effect
    /// regions, the inside of sharing-live's region from (12, 12) to (560, 652), or the part of the content that screen zoom
    /// shows at 2x around the stage's pointer), in phase 11 content beside it.
    /// </summary>
    private static ((double X, double Y, double Width, double Height)[] Inside, (double X, double Y, double Width, double Height)[] Outside) StageChanges(string take) => take switch
    {
        "sharing-measure" => ([(70, 90, 420, 480)], [(610, 40, 540, 560)]),
        "zoom-measure" => ([(40, 40, 488, 296), (580, 248, 290, 160)], [(-90, 640, 1360, 160)]),
        _ => ([.. EffectRegions.Select(region => (region.X, region.Y, region.Width, region.Height))], []),
    };

    /// <summary>
    /// <c>effects-measure</c>, <c>sharing-measure</c> and <c>zoom-measure</c>, not Mac takes: the app's development-only run
    /// (<c>--take &lt;take&gt;</c>) puts effects-live's three regions on the stage, shares sharing-live's region or zooms the
    /// stage at 2x, in Use apps, and marks its phases in the shared memory (5 before, 6 over still content, 7 while the stage
    /// changes under what it shows, 11 while the stage changes beside it, 10 while the scripted pointer circles, 8 after
    /// the regions were deleted or sharing or zoom stopped, 9 between phases). Meanwhile this samples the app's 3D-engine time
    /// and video memory and dwm.exe's 3D-engine time with PDH every 100 ms, and animates the stage in phases 7 and 11. Writes
    /// <c>&lt;take&gt;.json</c>: each phase's rates beside the app's own counters (CPU, managed allocations, frames drawn,
    /// memory, whether capture runs).
    /// </summary>
    private static void Measure(HarnessOptions options, string take, string app, DisplayInfo display, PixelRect area, int refresh, string folder,
                                MemoryMappedViewAccessor phase, StageBackdrop stage)
    {
        var output = Path.Combine(folder, take);
        Directory.CreateDirectory(output);
        var scale = display.Scale;
        // The sample content starts 130 points in from the area's left and 60 down from its top.
        PixelRect Stage((double X, double Y, double Width, double Height) rect) => PixelRect.FromSize((int)Math.Round((130 + rect.X) * scale),
            (int)Math.Round((60 + rect.Y) * scale), (int)Math.Round(rect.Width * scale), (int)Math.Round(rect.Height * scale));
        var (changed, nearby) = StageChanges(take);
        PixelRect[] regions = [.. changed.Select(Stage)];
        PixelRect[] beside = [.. nearby.Select(Stage)];
        phase.Write(0, 0);
        var start = new ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! };
        foreach (var argument in new[]
                 {
                     "--motion-reference", "slide", "--take", take, "--area", $"{area.Left},{area.Top},{area.Width},{area.Height}",
                     "--out", Path.Combine(output, "app"), "--timeout", "80", "--theme", options.ThemeGiven && !options.Dark ? "light" : "dark",
                 })
        {
            start.ArgumentList.Add(argument);
        }
        using var counters = new MotionCapture.PerfCounters();
        var samples = new List<MotionCapture.Sample>();
        using (var process = Process.Start(start) ?? throw new InvalidOperationException("The measurement run did not start."))
        {
            var clock = Stopwatch.StartNew();
            var animating = 0;
            long sampled = 0;
            try
            {
                while (!process.WaitForExit(5))
                {
                    if (clock.Elapsed > MeasureLimit)
                    {
                        process.Kill();
                        throw new TimeoutException($"The {take} run did not end within {MeasureLimit.TotalSeconds:F0} s.");
                    }
                    var now = phase.ReadInt32(0);
                    var wanted = now is 7 or 11 ? now : 0;
                    if (wanted != animating)
                    {
                        if (animating != 0)
                        {
                            stage.StopAnimating();
                        }
                        if (wanted != 0)
                        {
                            stage.Animate(wanted == 7 ? regions : beside);
                        }
                        animating = wanted;
                    }
                    if (Stopwatch.GetElapsedTime(sampled) >= SampleInterval)
                    {
                        sampled = Stopwatch.GetTimestamp();
                        samples.Add(counters.Sample(process));
                    }
                }
            }
            finally
            {
                if (animating != 0)
                {
                    stage.StopAnimating();
                }
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The {take} run ended with exit code {process.ExitCode}.");
            }
        }
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "app", "motion.json")))!.AsObject();
        foreach (var failure in report["failures"]!.AsArray())
        {
            Console.WriteLine($"  {take}: the app reported: {failure}");
        }
        var frequency = (double)report["frequency"]!.GetValue<long>();
        var info = report["take"]!.AsObject();
        var origin = info["startTicks"]!.GetValue<long>();
        const double MiB = 1024 * 1024;
        var phases = new JsonArray();
        foreach (var item in info["measurements"]!.AsArray())
        {
            var measured = item!.AsObject();
            long first = measured["startTicks"]!.GetValue<long>(), last = measured["endTicks"]!.GetValue<long>();
            var seconds = measured["seconds"]!.GetValue<double>();
            // Engine running time is in 100 ns units: the share of one engine's time.
            double Busy(Func<MotionCapture.Sample, long> counter) => Math.Round((At(samples, last, counter) - At(samples, first, counter)) / 1e7 / seconds * 100, 3);
            List<MotionCapture.Sample> inside = [.. samples.Where(sample => sample.Ticks >= first && sample.Ticks <= last && sample.AppDedicated >= 0)];
            // Builds before 2026-10-20 (capture performance) do not report the capture's own counters.
            double? Rate(string name) => measured[name] is { } value ? Math.Round(value.GetValue<long>() / seconds, 2) : null;
            long? Count(string name) => measured[name]?.GetValue<long>();
            phases.Add(new JsonObject
            {
                ["phase"] = measured["phase"]!.GetValue<int>(),
                ["label"] = measured["label"]!.GetValue<string>(),
                ["start"] = Math.Round((first - origin) / frequency, 3),
                ["seconds"] = Math.Round(seconds, 3),
                ["appCpuPercentOfOneCore"] = Math.Round(measured["cpuMilliseconds"]!.GetValue<double>() / (seconds * 10), 3),
                ["appGpu3dPercent"] = Busy(sample => sample.AppGpu),
                ["dwmGpu3dPercent"] = Busy(sample => sample.DwmGpu),
                ["drawnFramesPerSecond"] = Math.Round(measured["effectFrames"]!.GetValue<long>() / seconds, 2),
                ["skippedFramesPerSecond"] = Math.Round(measured["skippedFrames"]!.GetValue<long>() / seconds, 2),
                ["repeatedFramesPerSecond"] = Rate("repeatedFrames"),
                ["capturedFramesPerSecond"] = Rate("capturedFrames"),
                ["unchangedFramesPerSecond"] = Rate("unchangedFrames"),
                ["comparedFramesPerSecond"] = Rate("comparedFrames"),
                ["renotifiedPerSecond"] = Rate("renotified"),
                ["captureSessions"] = Count("captureSessions"),
                ["captureSessionsReused"] = Count("captureSessionsReused"),
                ["comparisonWaitMeanMilliseconds"] = measured["comparisonsRead"] is { } read && read.GetValue<long>() > 0
                    ? Math.Round(measured["comparisonWaitMilliseconds"]!.GetValue<double>() / read.GetValue<long>(), 3) : null,
                ["longestComparisonWaitMilliseconds"] = measured["longestComparisonWaitMilliseconds"] is { } longest
                    ? Math.Round(longest.GetValue<double>(), 3) : null,
                ["allocatedBytes"] = measured["allocatedBytes"]!.GetValue<long>(),
                ["workingSetMiB"] = Math.Round(measured["workingSet"]!.GetValue<long>() / MiB, 1),
                ["privateMiB"] = Math.Round(measured["privateBytes"]!.GetValue<long>() / MiB, 1),
                ["threads"] = measured["threads"]!.GetValue<int>(),
                ["videoMemoryDedicatedMiB"] = inside.Count == 0 ? null : Math.Round(inside.Average(sample => sample.AppDedicated) / MiB, 1),
                ["videoMemorySharedMiB"] = inside.Count == 0 ? null : Math.Round(inside.Average(sample => sample.AppShared) / MiB, 1),
                ["capturingAtStart"] = measured["capturingAtStart"]!.GetValue<bool>(),
                ["capturingAtEnd"] = measured["capturingAtEnd"]!.GetValue<bool>(),
            });
        }
        var events = new JsonArray();
        foreach (var item in info["events"]!.AsArray())
        {
            events.Add(new JsonObject
            {
                ["time"] = Math.Round((item!["ticks"]!.GetValue<long>() - origin) / frequency, 3),
                ["label"] = item["label"]!.GetValue<string>(),
            });
        }
        var result = new JsonObject
        {
            ["take"] = take,
            ["recorded"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["display"] = new JsonObject
            {
                ["name"] = display.DeviceName,
                ["monitorPixels"] = new JsonArray(display.Bounds.Width, display.Bounds.Height),
                ["scale"] = scale,
                ["refreshRate"] = refresh,
            },
            ["areaPixels"] = new JsonArray(area.Left, area.Top, area.Width, area.Height),
            ["changedPixels"] = new JsonArray([.. regions.Select(rect => (JsonNode)new JsonArray(rect.Left, rect.Top, rect.Width, rect.Height))]),
            ["changedBesidePixels"] = new JsonArray([.. beside.Select(rect => (JsonNode)new JsonArray(rect.Left, rect.Top, rect.Width, rect.Height))]),
            ["method"] = $"Annotator.exe --motion-reference --take {take} in Use apps over the motion-live stage; app CPU, managed allocations, "
                         + "frames drawn (effect layer, zoomed screen and shared region together), memory and threads from the app at each "
                         + "phase's ends; 3D-engine time and video memory from PDH every 100 ms. In phase 7 the stage animates a bar across "
                         + "each changed rectangle on every composition frame, in phase 11 across each rectangle beside it (stage pixels).",
            ["phases"] = phases,
            ["events"] = events,
            ["failures"] = report["failures"]!.DeepClone(),
        };
        File.WriteAllText(Path.Combine(output, take + ".json"), result.ToJsonString(Indented));
        Console.WriteLine($"  {take}: {samples.Count} samples");
        foreach (var item in phases)
        {
            Console.WriteLine($"    {item!["phase"],2} {item["label"],-36} CPU {item["appCpuPercentOfOneCore"],6}%  GPU {item["appGpu3dPercent"],6}%  dwm GPU {item["dwmGpu3dPercent"],6}%  "
                              + $"{item["drawnFramesPerSecond"],6} fps  {item["allocatedBytes"],8} B  WS {item["workingSetMiB"]} MiB  private {item["privateMiB"]} MiB  "
                              + $"video {item["videoMemoryDedicatedMiB"]} MiB  capturing {item["capturingAtEnd"]}");
            if (item["capturedFramesPerSecond"] is not null)
            {
                Console.WriteLine($"      captured {item["capturedFramesPerSecond"]}/s, compared {item["comparedFramesPerSecond"]}/s, unchanged {item["unchangedFramesPerSecond"]}/s, "
                                  + $"repeated draws skipped {item["repeatedFramesPerSecond"]}/s, told again {item["renotifiedPerSecond"]}/s, "
                                  + $"comparison read after {item["comparisonWaitMeanMilliseconds"]} ms on average (longest so far {item["longestComparisonWaitMilliseconds"]} ms), "
                                  + $"capture sessions {item["captureSessions"]} (reused {item["captureSessionsReused"]})");
            }
        }
    }

    /// <summary>A cumulative counter at <paramref name="ticks"/>, interpolated between the samples around it.</summary>
    private static double At(List<MotionCapture.Sample> samples, long ticks, Func<MotionCapture.Sample, long> counter)
    {
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index].Ticks >= ticks)
            {
                MotionCapture.Sample before = samples[index - 1], after = samples[index];
                var share = Math.Clamp((ticks - before.Ticks) / (double)(after.Ticks - before.Ticks), 0, 1);
                return counter(before) + (counter(after) - counter(before)) * share;
            }
        }
        return samples.Count == 0 ? 0 : counter(samples[^1]);
    }
}
