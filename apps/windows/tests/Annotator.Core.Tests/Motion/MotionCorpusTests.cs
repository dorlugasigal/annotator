using System.Security.Cryptography;
using System.Text.Json;
using Annotator.Core.Geometry;
using Annotator.Core.Motion;

namespace Annotator.Core.Tests.Motion;

/// <summary>
/// The Mac's motion corpus (<c>reference/conformance/motion</c>, WIN-MOT-01), read unchanged from the repository: MotionCore's
/// curves, springs, fades and timelines sampled every millisecond, and the durations. Each file's SHA-256 must match
/// <c>reference/conformance/manifest.json</c>; durations must be equal and samples within 1e-4, as the corpus README says.
/// </summary>
public sealed class MotionCorpusTests
{
    private const double Tolerance = 1e-4;

    private static readonly string[] MotionFiles = ["motion/curves.jsonl", "motion/durations.json", "motion/springs-and-fades.jsonl", "motion/timelines.jsonl"];

    public static TheoryData<string> Files => [.. MotionFiles];

    [Theory]
    [MemberData(nameof(Files))]
    public void FilesMatchTheManifest(string path)
    {
        JsonElement entry = ManifestEntries().Single(file => file.GetProperty("path").GetString() == path);
        var bytes = File.ReadAllBytes(MotionCorpus.Path(path));
        Assert.Equal(entry.GetProperty("bytes").GetInt64(), bytes.LongLength);
        Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void TheManifestListsOnlyTheseMotionFiles()
    {
        string[] listed = [.. ManifestEntries().Select(file => file.GetProperty("path").GetString()!).Where(path => path.StartsWith("motion/", StringComparison.Ordinal))];
        Assert.Equal(MotionFiles.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DurationsMatchExactly()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(MotionCorpus.Path("motion/durations.json")));
        JsonElement root = document.RootElement;
        var expected = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["toolbarShowDuration"] = MotionTiming.ToolbarShowDuration,
            ["toolbarHideDuration"] = MotionTiming.ToolbarHideDuration,
            ["minimumReversalShare"] = MotionTiming.MinimumReversalShare,
            ["zoomInDuration"] = MotionTiming.ZoomInDuration,
            ["zoomOutDuration"] = MotionTiming.ZoomOutDuration,
            ["spotlightFadeInDuration"] = MotionTiming.SpotlightFadeInDuration,
            ["spotlightFadeOutDuration"] = MotionTiming.SpotlightFadeOutDuration,
            ["spotlightResizeDuration"] = MotionTiming.SpotlightResizeDuration,
            ["snapMorphDuration"] = MotionTiming.SnapMorphDuration,
            ["eraseFadeDuration"] = MotionTiming.EraseFadeDuration,
            ["hintFadeInDuration"] = MotionTiming.HintFadeInDuration,
            ["hintFadeOutDuration"] = MotionTiming.HintFadeOutDuration,
            ["hintTextCrossfadeDuration"] = MotionTiming.HintTextCrossfadeDuration,
            ["hintDwellDuration"] = MotionTiming.HintDwellDuration,
            ["pointerClickDuration"] = CursorHighlightSettings.ClickDuration,
            ["toolbarResizeDuration"] = MotionTiming.ToolbarResizeDuration,
            ["toolbarCollapseDuration"] = MotionTiming.ToolbarCollapseDuration,
            ["toolbarExpandDuration"] = MotionTiming.ToolbarExpandDuration,
            ["toolbarMorphOutgoingEnd"] = ToolbarMorph.OutgoingEnd,
            ["toolbarMorphIncomingStart"] = ToolbarMorph.IncomingStart,
            ["toolbarHoldDelay"] = ToolbarHold.Delay,
            ["toolbarHoldSlop"] = ToolbarHold.Slop,
            ["laserTrailLifetime"] = LaserTrail.Lifetime,
        };
        foreach (JsonProperty property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "notes":
                    break;
                case "visibility":
                    var seen = new HashSet<VisibilityAnimationStyle>();
                    foreach (JsonProperty style in property.Value.EnumerateObject())
                    {
                        VisibilityAnimationStyle value = VisibilityAnimationStyle.FromRawValue(style.Name)
                            ?? throw new Xunit.Sdk.XunitException($"The corpus names an unknown style '{style.Name}'.");
                        seen.Add(value);
                        Assert.Equal(style.Value.GetProperty("show").GetDouble(), MotionTiming.VisibilityDuration(value, appearing: true));
                        Assert.Equal(style.Value.GetProperty("hide").GetDouble(), MotionTiming.VisibilityDuration(value, appearing: false));
                        Assert.Equal(style.Value.GetProperty("resolvedStyle").GetString(), value.Resolved.RawValue);
                    }
                    Assert.Equal(VisibilityAnimationStyle.AllCases.Count, seen.Count);
                    break;
                default:
                    Assert.True(expected.Remove(property.Name, out var duration), $"The corpus has a duration '{property.Name}' the port does not check.");
                    Assert.Equal(property.Value.GetDouble(), duration);
                    break;
            }
        }
        Assert.Empty(expected);
    }

    [Fact]
    public void CurvesMatch()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement record in MotionCorpus.Lines("motion/curves.jsonl"))
        {
            var name = record.GetProperty("name").GetString()!;
            names.Add(name);
            Func<double, double> curve;
            switch (name)
            {
                case "smoothstep":
                    curve = MotionCurves.Smooth;
                    break;
                case "dropSettle":
                    curve = MotionCurves.DropSettle;
                    break;
                case "pointerClickEase":
                    Assert.Equal(new double[] { 0.4, 0, 0.2, 1 }, Numbers(record.GetProperty("controlPoints")));
                    curve = CursorHighlightGeometry.EaseInOut;
                    break;
                default:
                    CubicBezier bezier = MotionCurves.Named.Single(named => named.Name == name).Curve;
                    Assert.Equal(Numbers(record.GetProperty("controlPoints")), new[] { bezier.X1, bezier.Y1, bezier.X2, bezier.Y2 });
                    curve = bezier.Progress;
                    break;
            }
            MotionCorpus.AssertSamples(name, Numbers(record.GetProperty("samples")), index => curve(index / 1000.0), Tolerance);
        }
        Assert.All(MotionCurves.Named, named => Assert.Contains(named.Name, names));
        Assert.Equal(MotionCurves.Named.Count + 3, names.Count);
    }

    [Fact]
    public void SpringsAndFadesMatch()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement record in MotionCorpus.Lines("motion/springs-and-fades.jsonl"))
        {
            var name = record.GetProperty("name").GetString()!;
            names.Add(name);
            if (record.TryGetProperty("response", out JsonElement response))
            {
                SpringMotion spring = MotionTiming.Springs.Single(named => named.Name == name).Spring;
                Assert.Equal(response.GetDouble(), spring.Response);
                Assert.Equal(record.GetProperty("dampingFraction").GetDouble(), spring.DampingFraction);
                Assert.Equal(record.GetProperty("settleTimeWithin0.001").GetDouble(), spring.SettleTime());
                MotionCorpus.AssertSamples(name, Numbers(record.GetProperty("samplesEveryMillisecondTo1500")), index => spring.Value(index / 1000.0), Tolerance);
            }
            else
            {
                TimedMotion motion = MotionTiming.Timed.Single(named => named.Name == name).Motion;
                Assert.Equal(record.GetProperty("duration").GetDouble(), motion.Duration);
                Assert.Equal(Numbers(record.GetProperty("curve")), new[] { motion.Curve.X1, motion.Curve.Y1, motion.Curve.X2, motion.Curve.Y2 });
                MotionCorpus.AssertSamples(name, Numbers(record.GetProperty("samplesEveryMillisecond")),
                                           index => motion.Curve.Progress(index / 1000.0 / motion.Duration), Tolerance);
            }
        }
        Assert.Equal(MotionTiming.Springs.Count + MotionTiming.Timed.Count, names.Count);
    }

    /// <summary>
    /// The timelines as the Mac's exporter builds them from MotionCore (apps/macos/Tools/Conformance/MotionExport.swift); the Windows
    /// features that play them (poses, Smart Draw, the eraser) are checked against the same file in the Workspace tests.
    /// </summary>
    public static IReadOnlyDictionary<string, (double Duration, Func<double, double> Value)> Timelines { get; } =
        new Dictionary<string, (double, Func<double, double>)>(StringComparer.Ordinal)
        {
            ["pose show, pop"] = (MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Pop, true), MotionCurves.Enter.Progress),
            ["pose hide, pop"] = (MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Pop, false), MotionCurves.Standard.Progress),
            ["pose show, drop"] = (MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Drop, true), MotionCurves.DropSettle),
            ["pose hide, drop"] = (MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Drop, false), static time => time),
            ["pose show, focus"] = (MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Focus, true), MotionCurves.Enter.Progress),
            ["pose hide, focus"] = (MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Focus, false), MotionCurves.Standard.Progress),
            ["screen zoom in"] = (MotionTiming.ZoomInDuration, MotionCurves.Enter.Progress),
            ["screen zoom out"] = (MotionTiming.ZoomOutDuration, MotionCurves.Standard.Progress),
            ["spotlight dims in"] = (MotionTiming.SpotlightFadeInDuration, MotionCurves.Fade.Progress),
            ["spotlight dims out"] = (MotionTiming.SpotlightFadeOutDuration, MotionCurves.Fade.Progress),
            ["spotlight radius"] = (MotionTiming.SpotlightResizeDuration, MotionCurves.Standard.Progress),
            ["smart draw morph"] = (MotionTiming.SnapMorphDuration, MotionCurves.Enter.Progress),
            ["smart draw shape reveal"] = (MotionTiming.SnapMorphDuration, static time => MotionCurves.Smooth((time - 0.55) / 0.45)),
            ["eraser fade alpha factor"] = (MotionTiming.EraseFadeDuration, static time => 0.22 * (1 - MotionCurves.Fade.Progress(time))),
            ["eraser fade scale"] = (MotionTiming.EraseFadeDuration, static time => 1 - 0.03 * MotionCurves.Exit.Progress(time)),
            ["pointer click pulse scale"] = (CursorHighlightSettings.ClickDuration, CursorHighlightGeometry.PulseScale),
            ["toolbar resize"] = (MotionTiming.ToolbarResizeDuration, ToolbarMorph.Shape),
            ["toolbar collapse shape"] = (MotionTiming.ToolbarCollapseDuration, ToolbarMorph.Shape),
            ["toolbar collapse outgoing face"] = (MotionTiming.ToolbarCollapseDuration, ToolbarMorph.OutgoingOpacity),
            ["toolbar collapse incoming face"] = (MotionTiming.ToolbarCollapseDuration, ToolbarMorph.IncomingOpacity),
            ["toolbar expand shape"] = (MotionTiming.ToolbarExpandDuration, ToolbarMorph.Shape),
            ["toolbar expand outgoing face"] = (MotionTiming.ToolbarExpandDuration, ToolbarMorph.OutgoingOpacity),
            ["toolbar expand incoming face"] = (MotionTiming.ToolbarExpandDuration, ToolbarMorph.IncomingOpacity),
        };

    [Fact]
    public void TimelinesMatch()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement record in MotionCorpus.Lines("motion/timelines.jsonl"))
        {
            var name = record.GetProperty("name").GetString()!;
            names.Add(name);
            Assert.True(Timelines.TryGetValue(name, out var timeline), $"The corpus has a timeline '{name}' the port does not check.");
            var duration = record.GetProperty("duration").GetDouble();
            Assert.Equal(duration, timeline.Duration);
            MotionCorpus.AssertSamples(name, Numbers(record.GetProperty("samplesEveryMillisecond")),
                                       index => MotionCorpus.TimelineValue(timeline.Value, duration, index), Tolerance);
        }
        Assert.Equal(Timelines.Count, names.Count);
    }

    private static IEnumerable<JsonElement> ManifestEntries()
    {
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(MotionCorpus.Path("manifest.json")));
        return [.. manifest.RootElement.GetProperty("files").EnumerateArray().Select(file => file.Clone())];
    }

    private static double[] Numbers(JsonElement array) => [.. array.EnumerateArray().Select(value => value.GetDouble())];
}

/// <summary>Reads <c>reference/conformance</c> from the repository that holds this test run.</summary>
internal static class MotionCorpus
{
    private static readonly Lock ReportLock = new();

    private static readonly Lazy<string> Root = new(() =>
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "reference", "conformance");
            if (File.Exists(System.IO.Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("reference/conformance was not found above the test output; run the tests inside the repository.");
    });

    public static string Path(string relative) => System.IO.Path.Combine(Root.Value, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public static IEnumerable<JsonElement> Lines(string relative)
    {
        foreach (var line in File.ReadLines(Path(relative)))
        {
            if (line.Length == 0)
            {
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(line);
            yield return document.RootElement.Clone();
        }
    }

    /// <summary>The exporter's timeline sample: time clamped to 1, and 1 for a zero duration.</summary>
    public static double TimelineValue(Func<double, double> value, double duration, int millisecond) =>
        duration > 0 ? value(Math.Min(1, millisecond / 1000.0 / duration)) : 1;

    public static void AssertSamples(string name, IReadOnlyList<double> expected, Func<int, double> actual, double tolerance)
    {
        Assert.NotEmpty(expected);
        var largest = 0.0;
        var at = 0;
        for (var index = 0; index < expected.Count; index++)
        {
            var difference = Math.Abs(expected[index] - actual(index));
            if (!(difference <= largest))
            {
                largest = difference;
                at = index;
            }
        }
        var line = $"{name}: {expected.Count} samples, largest difference {largest:E2} at sample {at}.";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        // Measurement runs name a file in ANNOTATOR_CORPUS_REPORT to collect every comparison.
        if (Environment.GetEnvironmentVariable("ANNOTATOR_CORPUS_REPORT") is { Length: > 0 } report)
        {
            lock (ReportLock)
            {
                File.AppendAllText(report, line + Environment.NewLine);
            }
        }
        Assert.True(largest <= tolerance, $"{name}: sample {at} differs by {largest:E3} (limit {tolerance:E0}).");
    }
}
