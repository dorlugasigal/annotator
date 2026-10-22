using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Text;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>live-compare</c>, nothing on screen: compares Windows takes of <c>motion-live</c> (under <c>--evidence</c>) with the
/// Mac recordings they replay (<c>--reference</c>), frame by frame, with Media Foundation decoding both. Every frame is read at
/// the Mac's 1,440 x 900 area points (area averaging) and frame n is at n/60 s. Per take it writes into the take's folder:
/// <c>windows-&lt;take&gt;.png</c> (a contact sheet at the Mac's offsets, in the Mac's format), <c>compare-&lt;take&gt;.png</c>
/// (the Mac's and Windows' frames side by side at the same offsets from each segment's start) and
/// <c>compare-&lt;take&gt;.json</c> (the take's measurements, see <see cref="ShowHide"/>, <see cref="Effects"/>,
/// <see cref="Sharing"/>, <see cref="ZoomScreen"/> and <see cref="ZoomLens"/>).
/// </summary>
internal static partial class LiveCompare
{
    internal const int Width = 1440, Height = 900, Rate = 60;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Run(HarnessOptions options)
    {
        var reference = Path.GetFullPath(options.Reference ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "..",
                                                                             "reference", "ux-reference", "macos-live"));
        var folder = Path.GetFullPath(options.Evidence ?? throw new ArgumentException("--evidence names the folder of the motion-live takes."));
        if (File.Exists(folder) && options.Takes is { } wanted)
        {
            // video-frames: frames of one video by number (n/60 s), as PNG files beside it.
            var numbers = wanted.Select(text => int.Parse(text, CultureInfo.InvariantCulture)).ToHashSet();
            var saved = 0;
            using var reader = VideoReader.Open(folder);
            var frame = new byte[reader.Width * reader.Height * 4];
            for (var index = 0; reader.Read(frame, out _) && saved < numbers.Count; index++)
            {
                if (numbers.Contains(index))
                {
                    var path = Path.ChangeExtension(folder, null) + $"-frame{index:D4}.png";
                    File.WriteAllBytes(path, RasterAsset.EncodePng(new RasterImage(reader.Width, reader.Height, reader.Width * 4, frame)));
                    Console.WriteLine($"  {path}");
                    saved++;
                }
            }
            return;
        }
        var takes = options.Takes ?? [.. Directory.EnumerateDirectories(folder).Select(Path.GetFileName).OfType<string>()
                                          .Where(take => take != "sharing-output"
                                                         && File.Exists(Path.Combine(folder, take, $"windows-{take}.json"))
                                                         && File.Exists(Path.Combine(reference, $"{take}.json")))
                                          .Order(StringComparer.Ordinal)];
        if (takes.Length == 0)
        {
            throw new ArgumentException($"No Windows takes with a Mac recording were found under {folder}.");
        }
        foreach (var take in takes)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var pair = Pair.Load(reference, folder, take);
            JsonObject result = take switch
            {
                "effects-live" => Effects(pair),
                "sharing-live" => Sharing(pair, reference, folder),
                "zoom-screen" => ZoomScreen(pair),
                "zoom-lens" => ZoomLens(pair),
                _ when take.StartsWith("show-hide-", StringComparison.Ordinal) => ShowHide(pair),
                _ => throw new ArgumentException($"live-compare does not know the take '{take}'."),
            };
            result["take"] = take;
            result["macDuration"] = pair.Mac.Duration;
            result["windowsDuration"] = pair.Windows.Duration;
            File.WriteAllText(Path.Combine(pair.Folder, $"compare-{take}.json"), result.ToJsonString(Indented));
            Console.WriteLine($"  {take}: compared in {clock.Elapsed.TotalSeconds:F1} s; {Path.Combine(pair.Folder, $"compare-{take}.json")}");
        }
    }

    /// <summary>A Mac recording and its Windows take: their JSON files and videos.</summary>
    internal sealed record Recording(JsonObject Meta, string Video)
    {
        public double Duration => Meta["duration"]!.GetValue<double>();

        public IReadOnlyList<(string Label, double Start, double End)> Segments =>
            [.. Meta["segments"]!.AsArray().Select(segment => (segment!["label"]!.GetValue<string>(), segment["start"]!.GetValue<double>(),
                                                                 segment["end"]!.GetValue<double>()))];

        public (string Label, double Start, double End) Segment(string label) =>
            Segments.FirstOrDefault(segment => segment.Label == label) is { Label: not null } found
                ? found
                : throw new InvalidOperationException($"The recording has no '{label}' segment.");

        /// <summary>The take's scripted pointer (Windows takes): seconds from the start and area points.</summary>
        public (double Time, double X, double Y)[] Pointer => Meta["pointer"] is JsonArray path
            ? [.. path.Select(point => (point!["time"]!.GetValue<double>(), point["x"]!.GetValue<double>(), point["y"]!.GetValue<double>()))]
            : [];
    }

    internal sealed record Pair(string Take, string Folder, Recording Mac, Recording Windows)
    {
        public static Pair Load(string reference, string folder, string take)
        {
            var mac = JsonNode.Parse(File.ReadAllText(Path.Combine(reference, $"{take}.json")))!.AsObject();
            var windowsFolder = Path.Combine(folder, take);
            var windows = JsonNode.Parse(File.ReadAllText(Path.Combine(windowsFolder, $"windows-{take}.json")))!.AsObject();
            return new Pair(take, windowsFolder, new Recording(mac, Path.Combine(reference, mac["video"]!.GetValue<string>())),
                            new Recording(windows, Path.Combine(windowsFolder, windows["video"]!.GetValue<string>())));
        }

        /// <summary>
        /// The Mac's contact-sheet frames as (label, Mac time, Windows time): each offset from its segment's start on the Mac
        /// is put at the same offset from the Windows segment's start.
        /// </summary>
        public List<(string Label, double Mac, double Windows)> SheetTimes()
        {
            var times = new List<(string, double, double)>();
            foreach (var item in Mac.Meta["contactSheetFrames"]!.AsArray())
            {
                var label = item!["label"]!.GetValue<string>();
                var macTime = item["time"]!.GetValue<double>();
                double windowsTime;
                if (label == "start")
                {
                    windowsTime = 0;
                }
                else if (label == "end")
                {
                    windowsTime = Math.Max(0, Windows.Duration - 2.0 / Rate);
                }
                else
                {
                    var plus = label.LastIndexOf(" +", StringComparison.Ordinal);
                    var segment = label[..plus];
                    var offset = double.Parse(label[(plus + 2)..].Split(' ')[0], CultureInfo.InvariantCulture) / 1000;
                    windowsTime = Windows.Segment(segment).Start + offset;
                }
                times.Add((label, macTime, windowsTime));
            }
            return times;
        }
    }

    /// <summary>
    /// Decodes a video and hands each frame to <paramref name="frame"/> at 1,440 x 900 (BGRA, the buffer is reused), with
    /// its index; frame n is at n/60 s.
    /// </summary>
    internal static int Scan(string path, Action<int, byte[]> frame)
    {
        using var reader = VideoReader.Open(path);
        var decoded = new byte[reader.Width * reader.Height * 4];
        var scaled = new byte[Width * Height * 4];
        var scaler = new AreaScaler(reader.Width, reader.Height, Width, Height);
        var index = 0;
        while (reader.Read(decoded, out _))
        {
            scaler.Scale(decoded, reader.Width * 4, scaled);
            frame(index++, scaled);
        }
        return index;
    }

    internal static int Index(double time) => Math.Max(0, (int)Math.Round(time * Rate, MidpointRounding.AwayFromZero));

    /// <summary>A copy of a 1,440 x 900 frame as an image.</summary>
    internal static RasterImage Image(byte[] frame) => new(Width, Height, Width * 4, (byte[])frame.Clone());

    /// <summary>A copy of part of a 1,440 x 900 frame as an image.</summary>
    internal static RasterImage Crop(byte[] frame, int left, int top, int width, int height)
    {
        left = Math.Clamp(left, 0, Width - 1);
        top = Math.Clamp(top, 0, Height - 1);
        width = Math.Min(width, Width - left);
        height = Math.Min(height, Height - top);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(frame, ((top + y) * Width + left) * 4, pixels, y * width * 4, width * 4);
        }
        return new RasterImage(width, height, width * 4, pixels);
    }

    /// <summary>
    /// The contact sheet of the Windows take at the Mac's offsets and the side-by-side sheet (Mac, then Windows, at each
    /// offset). Returns the frames kept for the sheets, by index, for further checks.
    /// </summary>
    internal static void Sheets(Pair pair, Dictionary<int, RasterImage> mac, Dictionary<int, RasterImage> windows)
    {
        var times = pair.SheetTimes();
        RasterImage Frame(Dictionary<int, RasterImage> frames, double time)
        {
            var index = Index(time);
            while (index > 0 && !frames.ContainsKey(index))
            {
                index--;
            }
            return frames.TryGetValue(index, out var image) ? image : frames.Values.First();
        }
        Sheet.Write(Path.Combine(pair.Folder, $"windows-{pair.Take}.png"),
            [.. times.Select(time => (FormattableString.Invariant($"{time.Windows,6:F3} s  {time.Label}"), Frame(windows, time.Windows)))], 4, 360);
        var cells = new List<(string, RasterImage)>();
        foreach (var (label, macTime, windowsTime) in times)
        {
            cells.Add((FormattableString.Invariant($"Mac {macTime,6:F3} s  {label}"), Frame(mac, macTime)));
            cells.Add((FormattableString.Invariant($"Win {windowsTime,6:F3} s  {label}"), Frame(windows, windowsTime)));
        }
        Sheet.Write(Path.Combine(pair.Folder, $"compare-{pair.Take}.png"), cells, 4, 360);
    }

    /// <summary>The frame indexes the sheets need, for each recording.</summary>
    internal static (HashSet<int> Mac, HashSet<int> Windows) SheetFrames(Pair pair)
    {
        var times = pair.SheetTimes();
        return ([.. times.Select(time => Index(time.Mac))], [.. times.Select(time => Index(time.Windows))]);
    }

    internal static JsonArray Runs(IReadOnlyList<bool> flags)
    {
        var runs = new JsonArray();
        int? start = null;
        for (var index = 0; index <= flags.Count; index++)
        {
            var on = index < flags.Count && flags[index];
            if (on && start is null)
            {
                start = index;
            }
            else if (!on && start is { } first)
            {
                runs.Add(new JsonArray(Math.Round(first / (double)Rate, 3), Math.Round((index - 1) / (double)Rate, 3), index - first));
                start = null;
            }
        }
        return runs;
    }

    internal static double Round(double value, int digits = 4) => double.IsFinite(value) ? Math.Round(value, digits) : double.NaN;

    internal static JsonNode? Number(double? value, int digits = 4) => value is { } number && double.IsFinite(number) ? JsonValue.Create(Math.Round(number, digits)) : null;
}

/// <summary>Contact sheets drawn with Direct2D: cells in rows with a caption under each, on a light background.</summary>
internal static unsafe class Sheet
{
    public static void Write(string path, IReadOnlyList<(string Label, RasterImage Image)> cells, int columns, int cellWidth)
    {
        const int margin = 10, caption = 22;
        var first = cells[0].Image;
        var cellHeight = (int)Math.Round(cellWidth * first.Height / (double)first.Width);
        var rows = (cells.Count + columns - 1) / columns;
        int width = margin + columns * (cellWidth + margin), height = margin + rows * (cellHeight + caption + margin);
        using var devices = GraphicsDevices.Create();
        using var canvas = new OffscreenCanvas(devices);
        using var text = TextSystem.Create();
        var font = new TextFont(FontDesign.Monospace, FontWeight.Regular, 13);
        RasterImage sheet = canvas.Render(width, height, context =>
        {
            ID2D1SolidColorBrush* background = null;
            ID2D1SolidColorBrush* ink = null;
            try
            {
                var backgroundColor = new D2D1_COLOR_F { r = 0.925f, g = 0.925f, b = 0.925f, a = 1 };
                var inkColor = new D2D1_COLOR_F { r = 0.16f, g = 0.16f, b = 0.16f, a = 1 };
                Native.Check(context->CreateSolidColorBrush(&backgroundColor, null, &background), "ID2D1DeviceContext::CreateSolidColorBrush");
                Native.Check(context->CreateSolidColorBrush(&inkColor, null, &ink), "ID2D1DeviceContext::CreateSolidColorBrush");
                var all = D2D.Rect(0, 0, width, height);
                context->FillRectangle(&all, (ID2D1Brush*)background);
                for (var index = 0; index < cells.Count; index++)
                {
                    var (label, image) = cells[index];
                    double x = margin + index % columns * (cellWidth + margin), y = margin + index / columns * (cellHeight + caption + margin);
                    // Each cell keeps its image's aspect inside the cell.
                    var fit = Math.Min(cellWidth / (double)image.Width, cellHeight / (double)image.Height);
                    ID2D1Bitmap1* bitmap = canvas.Upload(image);
                    try
                    {
                        var destination = D2D.Rect(x, y, x + image.Width * fit, y + image.Height * fit);
                        context->DrawBitmap((ID2D1Bitmap*)bitmap, &destination, 1, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC, null, null);
                    }
                    finally
                    {
                        Native.Release(ref bitmap);
                    }
                    IDWriteTextLayout* layout = text.CreateLayout(label, font, TextAlignment.Left, cellWidth, caption);
                    try
                    {
                        context->DrawTextLayout(D2D.Point(x, y + cellHeight + 3), layout, (ID2D1Brush*)ink, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_CLIP);
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
}
