using System.Diagnostics;
using System.Text.Json.Nodes;
using Annotator.Rendering.Imaging;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>video-check</c>, nothing on screen: checks the Media Foundation path that the live replays use instead of ffmpeg.
/// It decodes every Mac recording in <c>--reference</c> and compares its frames, size and duration with the recording's JSON
/// file, then encodes 120 frames of the Mac's desktop with a moving bar and colored corners at the 125% stage size (or
/// <c>--area</c>'s width and height) and decodes them again: the frame count, times, orientation and the error per channel.
/// </summary>
internal static class VideoCheck
{
    private const int Frames = 120;
    private const int Rate = 60;

    public static void Run(HarnessOptions options)
    {
        var reference = Path.GetFullPath(options.Reference ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..",
                                                                             "reference", "ux-reference", "macos-live"));
        var folder = Path.GetFullPath(options.Evidence ?? throw new ArgumentException("--evidence names the folder for the check's video."));
        Directory.CreateDirectory(folder);
        var failures = new List<string>();
        RasterImage? backdrop = null;
        foreach (var json in Directory.EnumerateFiles(reference, "*.json").Order(StringComparer.Ordinal))
        {
            var meta = JsonNode.Parse(File.ReadAllText(json)) as JsonObject;
            if (meta?["video"]?.GetValue<string>() is not { } name)
            {
                continue;
            }
            var path = Path.Combine(reference, name);
            var clock = Stopwatch.StartNew();
            using var reader = VideoReader.Open(path);
            var pixels = new byte[reader.Width * reader.Height * 4];
            double first = double.NaN, last = 0;
            var irregular = 0;
            var count = 0;
            while (reader.Read(pixels, out var time))
            {
                if (count == 0)
                {
                    first = time;
                }
                else if (Math.Abs(time - last - 1.0 / Rate) > 0.002)
                {
                    irregular++;
                }
                last = time;
                count++;
                if (name == "show-hide-none.mp4" && backdrop is null && time - first >= 1.0 - 0.004)
                {
                    backdrop = new RasterImage(reader.Width, reader.Height, reader.Width * 4, (byte[])pixels.Clone());
                }
            }
            var seconds = clock.Elapsed.TotalSeconds;
            var duration = meta["duration"]!.GetValue<double>();
            var size = meta["pixels"]!.AsArray();
            int width = size[0]!.GetValue<int>(), height = size[1]!.GetValue<int>();
            var expected = (int)Math.Round(duration * Rate);
            var problems = new List<string>();
            if (reader.Width != width || reader.Height != height)
            {
                problems.Add($"{reader.Width} x {reader.Height} pixels where the JSON file says {width} x {height}");
            }
            if (Math.Abs(count - expected) > 1)
            {
                problems.Add($"{count} frames where {duration:F3} s at 60 fps has {expected}");
            }
            if (irregular > 0)
            {
                problems.Add($"{irregular} frames not 1/60 s after the one before");
            }
            Console.WriteLine(FormattableString.Invariant(
                $"  {name}: {reader.Width} x {reader.Height}, {count} frames from {first:F3} to {last:F3} s, {reader.FrameRate:F2} fps, ")
                + FormattableString.Invariant($"file {reader.Duration:F3} s, JSON {duration:F3} s; decoded at {count / seconds:F0} frames a second")
                + (problems.Count == 0 ? "" : "; " + string.Join("; ", problems)));
            failures.AddRange(problems.Select(problem => $"{name}: {problem}"));
        }
        if (backdrop is null)
        {
            throw new InvalidOperationException($"show-hide-none.mp4 in {reference} had no frame at 1.0 s.");
        }
        int videoWidth = (options.Area?.Width ?? 1800) & ~1, videoHeight = (options.Area?.Height ?? 1125) & ~1;
        failures.AddRange(RoundTrip(backdrop, videoWidth, videoHeight, Path.Combine(folder, "video-check.mp4")));
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Video check failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        }
        Console.WriteLine("  Every check passed.");
    }

    /// <summary>Encodes the backdrop with a moving bar and colored corners, decodes it again and compares.</summary>
    private static List<string> RoundTrip(RasterImage backdrop, int width, int height, string path)
    {
        var scaler = new AreaScaler(backdrop.Width, backdrop.Height, width, height);
        var background = new byte[width * height * 4];
        scaler.Scale(backdrop.Pixels, backdrop.Stride, background);
        (byte Blue, byte Green, byte Red)[] corners = [(0, 0, 255), (0, 200, 0), (255, 0, 0), (0, 220, 255)];
        var frames = new List<byte[]>(Frames);
        for (var index = 0; index < Frames; index++)
        {
            var frame = (byte[])background.Clone();
            var bar = width / 8;
            var left = (int)((width - bar) * (index / (double)(Frames - 1)));
            for (var y = 0; y < height; y++)
            {
                for (var x = left; x < left + bar; x++)
                {
                    var offset = (y * width + x) * 4;
                    frame[offset] = (byte)(40 + index);
                    frame[offset + 1] = (byte)(200 - index);
                    frame[offset + 2] = 230;
                }
            }
            var patch = Math.Max(16, width / 40);
            for (var corner = 0; corner < 4; corner++)
            {
                int x0 = corner % 2 == 0 ? 0 : width - patch, y0 = corner < 2 ? 0 : height - patch;
                for (var y = y0; y < y0 + patch; y++)
                {
                    for (var x = x0; x < x0 + patch; x++)
                    {
                        var offset = (y * width + x) * 4;
                        (frame[offset], frame[offset + 1], frame[offset + 2]) = corners[corner];
                    }
                }
            }
            frames.Add(frame);
        }
        var clock = Stopwatch.StartNew();
        var writer = VideoWriter.Create(path, width, height, Rate, MotionLiveCapture.VideoQuality);
        try
        {
            foreach (var frame in frames)
            {
                writer.Write(frame, width * 4);
            }
            writer.Finish();
        }
        finally
        {
            writer.Dispose();
        }
        var encode = clock.Elapsed.TotalSeconds;
        clock.Restart();
        var failures = new List<string>();
        using var reader = VideoReader.Open(path);
        if (reader.Width != width || reader.Height != height)
        {
            failures.Add($"round trip: decoded {reader.Width} x {reader.Height}, encoded {width} x {height}");
            return failures;
        }
        var decoded = new byte[width * height * 4];
        var count = 0;
        double worstMean = 0, worstTime = 0, sum = 0, start = 0;
        var cornerMisses = 0;
        while (reader.Read(decoded, out var time))
        {
            if (count >= Frames)
            {
                count++;
                continue;
            }
            // Media Foundation's times can start one frame late (the MP4 edit list); the spacing must be exact.
            if (count == 0)
            {
                start = time;
            }
            worstTime = Math.Max(worstTime, Math.Abs(time - start - count / (double)Rate));
            var source = frames[count];
            long difference = 0;
            for (var offset = 0; offset < decoded.Length; offset += 4)
            {
                difference += Math.Abs(decoded[offset] - source[offset]) + Math.Abs(decoded[offset + 1] - source[offset + 1])
                              + Math.Abs(decoded[offset + 2] - source[offset + 2]);
            }
            var mean = difference / (3.0 * width * height);
            sum += mean;
            worstMean = Math.Max(worstMean, mean);
            var patch = Math.Max(16, width / 40);
            for (var corner = 0; corner < 4; corner++)
            {
                int x = (corner % 2 == 0 ? 0 : width - patch) + patch / 2, y = (corner < 2 ? 0 : height - patch) + patch / 2;
                var offset = (y * width + x) * 4;
                var (blue, green, red) = corners[corner];
                if (Math.Abs(decoded[offset] - blue) > 40 || Math.Abs(decoded[offset + 1] - green) > 40 || Math.Abs(decoded[offset + 2] - red) > 40)
                {
                    cornerMisses++;
                }
            }
            count++;
        }
        var decode = clock.Elapsed.TotalSeconds;
        var bytes = new FileInfo(path).Length;
        Console.WriteLine(FormattableString.Invariant(
            $"  round trip {width} x {height}: {Frames} frames encoded in {encode:F2} s ({Frames / encode:F0} a second), {bytes / 1024.0 / 1024.0:F2} MiB; ")
            + FormattableString.Invariant($"{count} decoded in {decode:F2} s; mean error per channel {sum / Math.Max(1, Math.Min(count, Frames)):F2} (worst frame {worstMean:F2}); ")
            + FormattableString.Invariant($"first frame at {start * 1000:F2} ms, the others off their slot by at most {worstTime * 1000:F2} ms; {cornerMisses} corner colors missed"));
        if (count != Frames)
        {
            failures.Add($"round trip: {count} frames decoded of {Frames}");
        }
        if (worstTime > 0.001)
        {
            failures.Add($"round trip: a frame's time is {worstTime * 1000:F2} ms from its slot");
        }
        if (cornerMisses > 0)
        {
            failures.Add($"round trip: {cornerMisses} corner colors were wrong (orientation or colors)");
        }
        if (worstMean > 4)
        {
            failures.Add($"round trip: a frame differs by {worstMean:F2} per channel on average");
        }
        return failures;
    }
}
