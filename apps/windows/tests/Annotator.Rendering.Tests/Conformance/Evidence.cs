using System.Text.Json;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>
/// The device the conformance comparisons draw with: WARP, which renders the same pixels on every machine. Setting
/// <c>ANNOTATOR_S13_DEVICE=hardware</c> draws on the default GPU instead, for S-13 evidence on a given machine; such
/// results depend on the GPU and driver.
/// </summary>
internal static class ConformanceDevices
{
    private static readonly Lazy<Annotator.Platform.Graphics.GraphicsDevices> Hardware =
        new(() => Annotator.Platform.Graphics.GraphicsDevices.Create(new Annotator.Platform.Graphics.GraphicsDeviceOptions { UseWarp = false }));

    public static bool UsesHardware => string.Equals(Environment.GetEnvironmentVariable("ANNOTATOR_S13_DEVICE"), "hardware", StringComparison.OrdinalIgnoreCase);

    public static Annotator.Platform.Graphics.GraphicsDevices Devices => UsesHardware ? Hardware.Value : Warp.Devices;
}

/// <summary>
/// Writes review pictures and measurements when <c>ANNOTATOR_S13_EVIDENCE</c> names a folder (S-13): for each case the
/// Mac image, the Windows image, a difference heat map, a class map and a sheet with all four, plus one JSON line in
/// <c>results.jsonl</c>. Tests never write anything otherwise.
/// </summary>
internal static class Evidence
{
    private static readonly Lock Gate = new();

    public static string? Folder => Environment.GetEnvironmentVariable("ANNOTATOR_S13_EVIDENCE") is { Length: > 0 } folder ? folder : null;

    public static void Write(string section, string name, Imaging.RasterImage reference, Imaging.RasterImage candidate, ComparisonResult result,
                             string? summary = null, IEnumerable<string>? details = null, string? status = null)
    {
        if (Folder is not { } folder)
        {
            return;
        }
        var directory = Path.Combine(folder, section);
        var magnify = reference.Width * reference.Height <= 200 * 140 ? 3 : reference.Width <= 500 ? 2 : 1;
        ComparisonImages.Save(reference, Path.Combine(directory, $"{name}-mac.png"));
        ComparisonImages.Save(candidate, Path.Combine(directory, $"{name}-windows.png"));
        ComparisonImages.Save(ComparisonImages.Heat(result), Path.Combine(directory, $"{name}-difference.png"));
        ComparisonImages.Save(ComparisonImages.Classes(result), Path.Combine(directory, $"{name}-classes.png"));
        ComparisonImages.Save(ComparisonImages.Sheet(reference, candidate, result, magnify), Path.Combine(directory, $"{name}-sheet.png"));
        var initial = Rejudge(result, ComparisonThresholds.Initial);
        var record = new Dictionary<string, object?>
        {
            ["section"] = section,
            ["name"] = name,
            ["device"] = ConformanceDevices.UsesHardware ? "hardware" : "warp",
            ["width"] = result.Width,
            ["height"] = result.Height,
            ["status"] = status ?? (result.Passed ? "pass" : "fail"),
            ["passesInitial"] = initial,
            ["passesFrozen"] = Rejudge(result, ComparisonThresholds.Frozen),
            ["flat"] = result.FlatCount,
            ["flatFailures"] = result.FlatFailures,
            ["flatLargest"] = result.FlatMaxDifference,
            ["edges"] = result.EdgeCount,
            ["edgeLargestExcess"] = result.EdgeMaxExcess,
            ["edgesBeyond"] = new Dictionary<string, int>
            {
                ["1"] = result.EdgesBeyond(1), ["8"] = result.EdgesBeyond(8), ["16"] = result.EdgesBeyond(16),
                ["32"] = result.EdgesBeyond(32), ["64"] = result.EdgesBeyond(64),
            },
            ["other"] = result.OtherCount,
            ["otherWithin"] = result.OtherShareWithin,
            ["otherDeltaE"] = Percentiles(result, PixelClass.Other),
            ["allDeltaE"] = Percentiles(result, null),
            ["ignored"] = result.IgnoredCount,
            ["differentPixels"] = result.DifferentPixels,
            ["largestDifference"] = result.MaxDifference,
            ["summary"] = summary ?? result.Summary(),
            ["details"] = details?.ToArray() ?? [],
        };
        var line = JsonSerializer.Serialize(record);
        lock (Gate)
        {
            File.AppendAllText(Path.Combine(folder, "results.jsonl"), line + "\n");
        }
    }

    /// <summary>Whether the measured result passes other thresholds with the same edge distance and contrast.</summary>
    private static bool Rejudge(ComparisonResult result, ComparisonThresholds thresholds)
    {
        var flatFailures = 0;
        var edgeFailures = 0;
        var otherFailures = 0;
        for (var index = 0; index < result.Classes.Length; index++)
        {
            switch (result.Classes[index])
            {
                case PixelClass.Flat when result.ChannelDifference[index] > thresholds.FlatTolerance:
                    flatFailures++;
                    break;
                case PixelClass.Edge when result.EdgeExcess[index] > thresholds.EdgeAllowance:
                    edgeFailures++;
                    break;
                case PixelClass.Other when result.DeltaE[index] > thresholds.DeltaE:
                    otherFailures++;
                    break;
            }
        }
        var otherWithin = result.OtherCount == 0 ? 1 : 1 - otherFailures / (double)result.OtherCount;
        return flatFailures <= Math.Floor(result.FlatCount * thresholds.OutlierShare)
            && edgeFailures <= Math.Floor(result.EdgeCount * thresholds.OutlierShare)
            && otherWithin >= thresholds.OtherShare
            && (thresholds.MaxChannelDifference is not { } limit || result.MaxDifference <= limit);
    }

    private static Dictionary<string, double> Percentiles(ComparisonResult result, PixelClass? kind) => new()
    {
        ["p50"] = Math.Round(result.Percentile(0.5, kind), 3),
        ["p95"] = Math.Round(result.Percentile(0.95, kind), 3),
        ["p99"] = Math.Round(result.Percentile(0.99, kind), 3),
        ["p99.5"] = Math.Round(result.Percentile(0.995, kind), 3),
        ["max"] = Math.Round(result.Percentile(1, kind), 3),
    };
}
