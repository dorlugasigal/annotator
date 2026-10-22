using System.Text.Json.Nodes;
using Annotator.Rendering.Imaging;

namespace Annotator.RenderHarness;

/// <summary>The show and hide, live effects and region sharing comparisons (ports of the earlier ffmpeg-based scripts).</summary>
internal static partial class LiveCompare
{
    /// <summary>Area points: the toolbar's band starts here; above it are the drawings.</summary>
    private const int Band = 740;
    private const int Threshold = 24;
    private const int MinPixels = 40;

    /// <summary>What changes from frame to frame, and the toolbar's dark box, for every frame of a video.</summary>
    private sealed class MotionSeries
    {
        public List<int> Drawings { get; } = [];
        public List<int> Toolbar { get; } = [];
        public List<int> Anywhere { get; } = [];
        public List<(int X0, int Y0, int X1, int Y1, int Count)?> Boxes { get; } = [];
        public Dictionary<int, RasterImage> Kept { get; } = [];
        public int Count => Boxes.Count;
    }

    private static MotionSeries ScanMotion(string video, HashSet<int> keep)
    {
        var series = new MotionSeries();
        var previous = new byte[Width * Height * 4];
        Scan(video, (index, frame) =>
        {
            int drawings = 0, toolbar = 0;
            if (index > 0)
            {
                for (var y = 0; y < Height; y++)
                {
                    var changed = 0;
                    for (int x = 0, offset = y * Width * 4; x < Width; x++, offset += 4)
                    {
                        if (Math.Abs(frame[offset] - previous[offset]) > Threshold || Math.Abs(frame[offset + 1] - previous[offset + 1]) > Threshold
                            || Math.Abs(frame[offset + 2] - previous[offset + 2]) > Threshold)
                        {
                            changed++;
                        }
                    }
                    if (y < Band)
                    {
                        drawings += changed;
                    }
                    else
                    {
                        toolbar += changed;
                    }
                }
            }
            series.Drawings.Add(drawings);
            series.Toolbar.Add(toolbar);
            series.Anywhere.Add(drawings + toolbar);
            series.Boxes.Add(ToolbarBox(frame));
            if (keep.Contains(index))
            {
                series.Kept[index] = Image(frame);
            }
            Array.Copy(frame, previous, frame.Length);
        });
        return series;
    }

    /// <summary>The toolbar's dark box: pixels darker than 60 in every channel in the band, from x 150 to 1,290.</summary>
    private static (int X0, int Y0, int X1, int Y1, int Count)? ToolbarBox(byte[] frame)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, count = 0;
        for (var y = Band; y < Height; y++)
        {
            for (int x = 150, offset = (y * Width + 150) * 4; x < 1290; x++, offset += 4)
            {
                if (frame[offset] < 60 && frame[offset + 1] < 60 && frame[offset + 2] < 60)
                {
                    count++;
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y);
                }
            }
        }
        return count < 20 ? null : (x0, y0, x1 + 1, y1 + 1, count);
    }

    /// <summary>
    /// Show and hide: per segment, the visible events from the request (drawings start and end, the toolbar's look change,
    /// pulses, shrink, disappearance and reappearance, the last change), the same events from each platform's first moving
    /// frame, Windows minus Mac, and the lag in frames at which the two takes' drawing and toolbar activity match best.
    /// </summary>
    private static JsonObject ShowHide(Pair pair)
    {
        var (macKeep, windowsKeep) = SheetFrames(pair);
        var mac = ScanMotion(pair.Mac.Video, macKeep);
        var windows = ScanMotion(pair.Windows.Video, windowsKeep);
        Sheets(pair, mac.Kept, windows.Kept);
        var result = new JsonObject { ["style"] = pair.Mac.Meta["style"]?.DeepClone() };
        var segments = new JsonArray();
        var macSegments = pair.Mac.Segments;
        var windowsSegments = pair.Windows.Segments;
        for (var position = 0; position < macSegments.Count && position < windowsSegments.Count; position++)
        {
            var m = Events(mac, macSegments, position);
            var w = Events(windows, windowsSegments, position);
            var mr = Relative(m);
            var wr = Relative(w);
            var difference = new JsonObject();
            foreach (var (key, value) in mr)
            {
                if (wr.TryGetValue(key, out var other))
                {
                    difference[key] = Round(other - value);
                }
            }
            if (m.MotionStart is { } macStart && w.MotionStart is { } windowsStart)
            {
                difference["latency"] = Round(windowsStart - macStart);
            }
            var macStop = position + 1 < macSegments.Count ? macSegments[position + 1].Start : mac.Count / (double)Rate;
            var windowsStop = position + 1 < windowsSegments.Count ? windowsSegments[position + 1].Start : windows.Count / (double)Rate;
            segments.Add(new JsonObject
            {
                ["label"] = macSegments[position].Label,
                ["mac"] = m.Json,
                ["windows"] = w.Json,
                ["macFromMotionStart"] = Json(mr),
                ["windowsFromMotionStart"] = Json(wr),
                ["windowsMinusMac"] = difference,
                ["alignment"] = Alignment(mac, windows, macSegments[position].Start, windowsSegments[position].Start, macStop, windowsStop),
            });
        }
        result["segments"] = segments;
        foreach (var segment in segments)
        {
            Console.WriteLine($"    {segment!["label"]}: W - M {segment["windowsMinusMac"]!.ToJsonString()}; aligned {segment["alignment"]!.ToJsonString()}");
        }
        return result;
    }

    private sealed record SegmentEvents(JsonObject Json, double? MotionStart, Dictionary<string, double> Times);

    private static SegmentEvents Events(MotionSeries video, IReadOnlyList<(string Label, double Start, double End)> segments, int position)
    {
        var (_, start, end) = segments[position];
        var count = video.Count;
        var stop = position + 1 < segments.Count ? segments[position + 1].Start : count / (double)Rate;
        int first = Math.Clamp(Index(start), 1, count - 1), last = Math.Clamp(Index(stop) - 1, first, count - 1);
        double Rel(int index) => Round(index / (double)Rate - start);
        var drawings = Enumerable.Range(first, last - first + 1).Where(index => video.Drawings[index] > MinPixels).ToList();
        var anywhere = Enumerable.Range(first, last - first + 1).Where(index => video.Anywhere[index] > MinPixels).ToList();
        var boxes = video.Boxes.GetRange(first - 1, last - first + 2);
        var rest = boxes[0] ?? boxes.LastOrDefault(box => box is not null);
        var toolbar = new Dictionary<string, double>();
        if (rest is { } resting)
        {
            var width = resting.X1 - resting.X0;
            for (var offset = 0; offset < boxes.Count - 1; offset++)
            {
                var index = first + offset;
                var previous = boxes[offset];
                var current = boxes[offset + 1];
                if (current is not { } box)
                {
                    if (previous is not null)
                    {
                        toolbar.TryAdd("gone", Rel(index));
                    }
                    continue;
                }
                if (previous is null)
                {
                    toolbar.TryAdd("appear", Rel(index));
                }
                var boxWidth = box.X1 - box.X0;
                if (boxWidth >= 0.9 * width && toolbar.ContainsKey("appear"))
                {
                    toolbar.TryAdd("full", Rel(index));
                }
                if (boxWidth > 1.01 * width)
                {
                    toolbar.TryAdd("pulseStart", Rel(index));
                    toolbar["pulseEnd"] = Rel(index);
                }
                if (boxWidth < 0.9 * width && previous is not null && !toolbar.ContainsKey("appear"))
                {
                    toolbar.TryAdd("shrink", Rel(index));
                }
                if (previous is { } before && before != box && Math.Abs(boxWidth - (before.X1 - before.X0)) <= 1 && Math.Abs(box.Count - before.Count) > 300)
                {
                    toolbar.TryAdd("swap", Rel(index));
                }
            }
        }
        var moving = new SortedSet<int>(drawings);
        for (var offset = 0; offset < boxes.Count - 1; offset++)
        {
            var (a, b) = (boxes[offset], boxes[offset + 1]);
            if (a != b && (a is null || b is null || Math.Abs((a.Value.X1 - a.Value.X0) - (b.Value.X1 - b.Value.X0)) > 2))
            {
                moving.Add(first + offset);
            }
        }
        double? motionStart = moving.Count > 0 ? Rel(moving.Min) : null;
        var times = new Dictionary<string, double> { ["reportedEnd"] = Round(end - start) };
        if (drawings.Count > 0)
        {
            times["drawingsStart"] = Rel(drawings[0]);
            times["drawingsEnd"] = Rel(drawings[^1]);
        }
        if (anywhere.Count > 0)
        {
            times["lastChange"] = Rel(anywhere[^1]);
        }
        foreach (var (key, value) in toolbar)
        {
            times["toolbar " + key] = value;
        }
        var json = new JsonObject { ["request"] = Round(start), ["motionStart"] = Number(motionStart) };
        foreach (var (key, value) in times)
        {
            json[key] = value;
        }
        return new SegmentEvents(json, motionStart, times);
    }

    private static Dictionary<string, double> Relative(SegmentEvents events)
    {
        var relative = new Dictionary<string, double>();
        if (events.MotionStart is not { } start)
        {
            return relative;
        }
        foreach (var (key, value) in events.Times)
        {
            relative[key] = Round(value - start);
        }
        return relative;
    }

    private static JsonObject Json(Dictionary<string, double> values)
    {
        var json = new JsonObject();
        foreach (var (key, value) in values)
        {
            json[key] = value;
        }
        return json;
    }

    /// <summary>Frames by which Windows' activity follows the Mac's from each request, per band, by cross-correlating the change counts.</summary>
    private static JsonObject Alignment(MotionSeries mac, MotionSeries windows, double macStart, double windowsStart, double macStop, double windowsStop)
    {
        var result = new JsonObject();
        var span = Index(Math.Max(macStop - macStart, windowsStop - windowsStart));
        int m0 = Index(macStart), w0 = Index(windowsStart);
        foreach (var (name, macSeries, windowsSeries) in new[] { ("drawings", mac.Drawings, windows.Drawings), ("toolbar", mac.Toolbar, windows.Toolbar) })
        {
            double[] m = [.. Enumerable.Range(m0, Math.Max(0, Math.Min(macSeries.Count - 1, m0 + span) - m0 + 1)).Select(index => (double)macSeries[index])];
            double[] w = [.. Enumerable.Range(w0, Math.Max(0, Math.Min(windowsSeries.Count - 1, w0 + span) - w0 + 1)).Select(index => (double)windowsSeries[index])];
            if (m.Length == 0 || w.Length == 0 || m.Max() <= MinPixels || w.Max() <= MinPixels)
            {
                continue;
            }
            double macMax = m.Max(), windowsMax = w.Max();
            for (var index = 0; index < m.Length; index++)
            {
                m[index] /= macMax;
            }
            for (var index = 0; index < w.Length; index++)
            {
                w[index] /= windowsMax;
            }
            int bestLag = 0;
            var bestScore = double.MinValue;
            for (var lag = -24; lag <= 24; lag++)
            {
                double score = 0;
                for (var index = 0; index < m.Length; index++)
                {
                    if (index + lag >= 0 && index + lag < w.Length)
                    {
                        score += m[index] * w[index + lag];
                    }
                }
                if (score > bestScore)
                {
                    (bestScore, bestLag) = (score, lag);
                }
            }
            var macActive = Enumerable.Range(0, m.Length).Where(index => m[index] > 0.02).ToList();
            var windowsActive = Enumerable.Range(0, w.Length).Where(index => w[index] > 0.02).ToList();
            result[name] = new JsonObject
            {
                ["lagFrames"] = bestLag,
                ["lagSeconds"] = Round(bestLag / (double)Rate),
                ["macActive"] = new JsonArray(Round(macActive[0] / (double)Rate), Round(macActive[^1] / (double)Rate)),
                ["windowsActive"] = new JsonArray(Round(windowsActive[0] / (double)Rate), Round(windowsActive[^1] / (double)Rate)),
            };
        }
        return result;
    }

    /// <summary>effects-live's regions in area points (the sample content starts at 130, 60), and the cover over the blur.</summary>
    private static readonly (string Name, int X, int Y, int Width, int Height)[] EffectRegions =
        [("blur", 170, 100, 488, 296), ("pixelate", 718, 506, 552, 180), ("distort", 710, 308, 290, 160)];

    private static readonly (int X, int Y, int Width, int Height) Cover = (490, 270, 280, 120);

    /// <summary>
    /// Live effects: per frame, the share of black pixels inside each region (outside the cover) and the toolbar's height;
    /// at check times on each platform's own clock, the mean difference per channel between the platforms inside each region
    /// and the cover's mean level.
    /// </summary>
    private static JsonObject Effects(Pair pair)
    {
        Dictionary<string, double> Checks(Recording recording)
        {
            var strength = recording.Segment("blur strength 18 to 60 to 4");
            return new Dictionary<string, double>
            {
                ["blur done"] = recording.Segment("blur").End + 0.5,
                ["pixelate done"] = recording.Segment("pixelate").End + 0.5,
                ["distort done"] = recording.Segment("distort").End + 0.5,
                ["blur at 60"] = strength.Start + 15 * 0.05 + 0.02,
                ["blur at 4"] = strength.End + 0.3,
                ["cover added"] = recording.Segment("existing solid cover over the blur").End + 0.3,
                ["use apps"] = recording.Segment("use apps").End - 0.05,
            };
        }
        var macChecks = Checks(pair.Mac);
        var windowsChecks = Checks(pair.Windows);
        var (macKeep, windowsKeep) = SheetFrames(pair);
        macKeep.UnionWith(macChecks.Values.Select(Index));
        windowsKeep.UnionWith(windowsChecks.Values.Select(Index));
        (Dictionary<string, List<double>> Black, List<int?> ToolbarHeights, Dictionary<int, RasterImage> Kept, Dictionary<int, byte[]> Full) ScanEffects(string video, HashSet<int> keep, HashSet<int> full)
        {
            var black = EffectRegions.ToDictionary(region => region.Name, _ => new List<double>());
            var heights = new List<int?>();
            var kept = new Dictionary<int, RasterImage>();
            var frames = new Dictionary<int, byte[]>();
            Scan(video, (index, frame) =>
            {
                foreach (var (name, x0, y0, width, height) in EffectRegions)
                {
                    long dark = 0, total = 0;
                    for (var y = y0; y < y0 + height; y++)
                    {
                        for (var x = x0; x < x0 + width; x++)
                        {
                            if (x >= Cover.X && x < Cover.X + Cover.Width && y >= Cover.Y && y < Cover.Y + Cover.Height)
                            {
                                continue;
                            }
                            var offset = (y * Width + x) * 4;
                            total++;
                            if (frame[offset] < 16 && frame[offset + 1] < 16 && frame[offset + 2] < 16)
                            {
                                dark++;
                            }
                        }
                    }
                    black[name].Add(dark / (double)total);
                }
                var box = ToolbarBox(frame);
                heights.Add(box is { } found ? found.Y1 - found.Y0 : null);
                if (keep.Contains(index))
                {
                    kept[index] = Image(frame);
                }
                if (full.Contains(index))
                {
                    frames[index] = (byte[])frame.Clone();
                }
            });
            return (black, heights, kept, frames);
        }
        var mac = ScanEffects(pair.Mac.Video, macKeep, [.. macChecks.Values.Select(Index)]);
        var windows = ScanEffects(pair.Windows.Video, windowsKeep, [.. windowsChecks.Values.Select(Index)]);
        Sheets(pair, mac.Kept, windows.Kept);
        static byte[] Nearest(Dictionary<int, byte[]> frames, double time)
        {
            var index = Index(time);
            while (index > 0 && !frames.ContainsKey(index))
            {
                index--;
            }
            return frames[index];
        }
        var differences = new JsonObject();
        var crops = new List<(string, RasterImage)>();
        foreach (var name in macChecks.Keys)
        {
            byte[] m = Nearest(mac.Full, macChecks[name]), w = Nearest(windows.Full, windowsChecks[name]);
            var row = new JsonObject();
            foreach (var (region, x0, y0, width, height) in EffectRegions)
            {
                long sum = 0, count = 0;
                for (var y = y0; y < y0 + height; y++)
                {
                    for (var x = x0; x < x0 + width; x++)
                    {
                        if (x >= Cover.X && x < Cover.X + Cover.Width && y >= Cover.Y && y < Cover.Y + Cover.Height)
                        {
                            continue;
                        }
                        var offset = (y * Width + x) * 4;
                        sum += Math.Abs(m[offset] - w[offset]) + Math.Abs(m[offset + 1] - w[offset + 1]) + Math.Abs(m[offset + 2] - w[offset + 2]);
                        count += 3;
                    }
                }
                row[region] = Round(sum / (double)count, 2);
                if (name.StartsWith(region, StringComparison.Ordinal) || (region == "blur" && name is "blur at 60" or "blur at 4" or "cover added")
                    || (region == "distort" && name == "cover added"))
                {
                    crops.Add(($"Mac {name}: {region}", Crop(m, x0 - 20, y0 - 20, width + 40, height + 40)));
                    crops.Add(($"Win {name}: {region}", Crop(w, x0 - 20, y0 - 20, width + 40, height + 40)));
                }
            }
            double Level(byte[] frame)
            {
                long sum = 0;
                for (var y = Cover.Y; y < Cover.Y + Cover.Height; y++)
                {
                    for (var x = Cover.X; x < Cover.X + Cover.Width; x++)
                    {
                        var offset = (y * Width + x) * 4;
                        sum += frame[offset] + frame[offset + 1] + frame[offset + 2];
                    }
                }
                return sum / (3.0 * Cover.Width * Cover.Height);
            }
            row["coverMeanMac"] = Round(Level(m), 2);
            row["coverMeanWindows"] = Round(Level(w), 2);
            differences[name] = row;
        }
        Sheet.Write(Path.Combine(pair.Folder, "crops-effects-live.png"), crops, 4, 360);
        // A region grows while it is dragged, so a black part (capture starting, or a strip it just gained) is a small share
        // of the final rectangle: runs of frames with more than 2% of it black.
        JsonObject BlackRuns(Dictionary<string, List<double>> black)
        {
            var runs = new JsonObject();
            foreach (var (name, shares) in black)
            {
                runs[name] = Runs([.. shares.Select(share => share > 0.02)]);
            }
            return runs;
        }
        static JsonArray Heights(List<int?> heights) => new([.. heights.OfType<int>().Distinct().Order().Select(height => (JsonNode)height)]);
        Console.WriteLine($"    region differences {differences.ToJsonString()}");
        return new JsonObject
        {
            ["differenceMeanPerChannel"] = differences,
            ["blackRuns"] = new JsonObject { ["mac"] = BlackRuns(mac.Black), ["windows"] = BlackRuns(windows.Black) },
            ["toolbarHeightValues"] = new JsonObject { ["mac"] = Heights(mac.ToolbarHeights), ["windows"] = Heights(windows.ToolbarHeights) },
        };
    }

    private static readonly double[] SharingOffsets = [-0.05, 0.0, 0.05, 0.1, 0.15, 0.2, 0.25, 0.3, 0.4, 0.5, 0.6, 0.8, 1.0, 1.2];

    /// <summary>
    /// Region sharing: the desktop take's side-by-side sheets, and for the output window (sharing-output, on its own clock)
    /// the runs of covered frames (at least 92% near black under the title bar: "Updating the shared view..." and "Region
    /// sharing is off"); sheets of the hide and the show with the output and the shared region on the desktop, Mac and
    /// Windows, at the same offsets from each segment's start.
    /// </summary>
    private static JsonObject Sharing(Pair pair, string reference, string folder)
    {
        var (macKeep, windowsKeep) = SheetFrames(pair);
        var offsets = new Dictionary<string, (double Mac, double Windows)>();
        foreach (var label in new[] { "hide", "show" })
        {
            offsets[label] = (pair.Mac.Segment(label).Start, pair.Windows.Segment(label).Start);
            macKeep.UnionWith(SharingOffsets.Select(offset => Index(pair.Mac.Segment(label).Start + offset)));
            windowsKeep.UnionWith(SharingOffsets.Select(offset => Index(pair.Windows.Segment(label).Start + offset)));
        }
        var mac = ScanMotion(pair.Mac.Video, macKeep);
        var windows = ScanMotion(pair.Windows.Video, windowsKeep);
        Sheets(pair, mac.Kept, windows.Kept);
        var macOutputMeta = JsonNode.Parse(File.ReadAllText(Path.Combine(reference, "sharing-output.json")))!.AsObject();
        var windowsOutputFolder = Path.Combine(folder, "sharing-output");
        var windowsOutputMeta = JsonNode.Parse(File.ReadAllText(Path.Combine(windowsOutputFolder, "windows-sharing-output.json")))!.AsObject();
        double macShift = macOutputMeta["startsBeforeSharingLive"]!.GetValue<double>(), windowsShift = windowsOutputMeta["startsBeforeSharingLive"]!.GetValue<double>();
        (List<bool> Covered, Dictionary<int, RasterImage> Kept) ScanOutput(string video, HashSet<int> keep)
        {
            var covered = new List<bool>();
            var kept = new Dictionary<int, RasterImage>();
            using var reader = VideoReader.Open(video);
            var frame = new byte[reader.Width * reader.Height * 4];
            var index = 0;
            while (reader.Read(frame, out _))
            {
                long dark = 0, total = 0;
                for (var y = (int)(reader.Height * 0.12); y < reader.Height; y++)
                {
                    for (int x = 0, offset = y * reader.Width * 4; x < reader.Width; x++, offset += 4)
                    {
                        total++;
                        if (frame[offset] < 28 && frame[offset + 1] < 28 && frame[offset + 2] < 28)
                        {
                            dark++;
                        }
                    }
                }
                covered.Add(dark >= 0.92 * total);
                if (keep.Contains(index))
                {
                    kept[index] = new RasterImage(reader.Width, reader.Height, reader.Width * 4, (byte[])frame.Clone());
                }
                index++;
            }
            return (covered, kept);
        }
        HashSet<int> OutputKeep(Recording recording, double shift) =>
            [.. new[] { "hide", "show" }.SelectMany(label => SharingOffsets.Select(offset => Index(recording.Segment(label).Start + offset + shift)))];
        var macOutput = ScanOutput(Path.Combine(reference, macOutputMeta["video"]!.GetValue<string>()), OutputKeep(pair.Mac, macShift));
        var windowsOutput = ScanOutput(Path.Combine(windowsOutputFolder, windowsOutputMeta["video"]!.GetValue<string>()), OutputKeep(pair.Windows, windowsShift));
        static RasterImage Pick(Dictionary<int, RasterImage> frames, double time)
        {
            var index = Index(time);
            while (index > 0 && !frames.ContainsKey(index))
            {
                index--;
            }
            return frames.TryGetValue(index, out var image) ? image : frames.Values.First();
        }
        // The shared region in area points: the sample content's (12, 12) to (560, 652), the content starting at (130, 60).
        static RasterImage Region(RasterImage frame)
        {
            var pixels = frame.Pixels.ToArray();
            return Crop(pixels, 142, 72, 548, 640);
        }
        foreach (var label in new[] { "hide", "show" })
        {
            var cells = new List<(string, RasterImage)>();
            foreach (var offset in SharingOffsets)
            {
                var macTime = pair.Mac.Segment(label).Start + offset;
                var windowsTime = pair.Windows.Segment(label).Start + offset;
                var name = FormattableString.Invariant($"{offset * 1000:+0;-0} ms");
                cells.Add(($"Mac output {name}", Pick(macOutput.Kept, macTime + macShift)));
                cells.Add(($"Win output {name}", Pick(windowsOutput.Kept, windowsTime + windowsShift)));
                cells.Add(($"Mac region {name}", Region(Pick(mac.Kept, macTime))));
                cells.Add(($"Win region {name}", Region(Pick(windows.Kept, windowsTime))));
            }
            Sheet.Write(Path.Combine(pair.Folder, $"sharing-{label}.png"), cells, 4, 300);
        }
        JsonObject Clock(Recording recording, double shift)
        {
            var clock = new JsonObject();
            foreach (var (label, start, end) in recording.Segments)
            {
                clock[label] = new JsonArray(Round(start + shift, 3), Round(end + shift, 3));
            }
            return clock;
        }
        var result = new JsonObject
        {
            ["mac"] = new JsonObject { ["segmentsOnOutputClock"] = Clock(pair.Mac, macShift), ["coveredRuns"] = Runs(macOutput.Covered) },
            ["windows"] = new JsonObject { ["segmentsOnOutputClock"] = Clock(pair.Windows, windowsShift), ["coveredRuns"] = Runs(windowsOutput.Covered) },
        };
        Console.WriteLine($"    output covered: Mac {result["mac"]!["coveredRuns"]!.ToJsonString()}; Windows {result["windows"]!["coveredRuns"]!.ToJsonString()}");
        return result;
    }
}
