using System.Collections.Immutable;
using System.Text.Json;
using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.Motion;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.Workspace.Tests.Motion;

/// <summary>
/// The Windows features that play the Mac corpus timelines (<c>reference/conformance/motion/timelines.jsonl</c>, read
/// unchanged): the Pop, Drop and Soft focus poses, Smart Draw's morph and the eraser's fade, with durations equal and
/// samples within 1e-4 (WIN-MOT-01). Annotator.Core.Tests checks the file's hash and MotionCore itself.
/// </summary>
public sealed class MotionCorpusTimelineTests
{
    private const double Tolerance = 1e-4;

    [Fact]
    public void PosesFollowTheCorpus()
    {
        foreach (Style style in (Style[])[Style.Pop, Style.Drop, Style.Focus])
        {
            foreach (var appearing in (bool[])[true, false])
            {
                var name = $"pose {(appearing ? "show" : "hide")}, {style.RawValue}";
                var (duration, samples) = Timeline(name);
                Assert.Equal(duration, LayerMotion.Duration(style, appearing));
                // Hidden runs from 1 to 0 when showing; the corpus records the eased progress.
                Compare(name, samples, duration, time => appearing
                    ? 1 - VisibilityPose.Hidden(style, 1, 0, time, appearing: true, reversing: false)
                    : VisibilityPose.Hidden(style, 0, 1, time, appearing: false, reversing: false));
            }
        }
    }

    [Fact]
    public void SmartDrawMorphFollowsTheCorpus()
    {
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 100, 60));
        var ink = new Element(Tool.Pen, points: [new Point(10, 10), new Point(110, 70)]);
        ValueList<Point> from = [new Point(10, 10), new Point(110, 70)];
        var outline = new SnapMorph(shape, ink, from, null, null, 1, ImmutableHashSet<Guid>.Empty, Tool.Rectangle, new Rect(0, 0, 200, 200));
        var morphing = new SnapMorph(shape, ink, from, from, null, 1, ImmutableHashSet<Guid>.Empty, Tool.Rectangle, new Rect(0, 0, 200, 200));
        var (morphDuration, morph) = Timeline("smart draw morph");
        var (revealDuration, reveal) = Timeline("smart draw shape reveal");
        double windows = SnapMorph.Duration;
        Assert.Equal(morphDuration, windows);
        Assert.Equal(revealDuration, windows);
        Compare("smart draw morph", morph, morphDuration, time => outline.FrameAt(time).ShapeAlpha);
        Compare("smart draw shape reveal", reveal, revealDuration, time => morphing.FrameAt(time).ShapeAlpha);
    }

    [Fact]
    public void EraseFadeFollowsTheCorpus()
    {
        var fade = new EraseFade(ElementList.Empty, 1, scales: true, new Rect(0, 0, 10, 10));
        var (alphaDuration, alpha) = Timeline("eraser fade alpha factor");
        var (scaleDuration, scale) = Timeline("eraser fade scale");
        double windows = EraseFade.Duration;
        Assert.Equal(alphaDuration, windows);
        Assert.Equal(scaleDuration, windows);
        Compare("eraser fade alpha factor", alpha, alphaDuration, time => fade.FrameAt(time).Alpha);
        Compare("eraser fade scale", scale, scaleDuration, time => fade.FrameAt(time).Scale);
    }

    [Fact]
    public void LayerMotionUsesTheCorpusDurations()
    {
        Assert.Equal(MotionTiming.ToolbarShowDuration, ToolbarSlideMotion.ShowDuration);
        Assert.Equal(MotionTiming.ToolbarHideDuration, ToolbarSlideMotion.HideDuration);
        foreach (Style style in Style.AllCases)
        {
            Assert.Equal(MotionTiming.VisibilityDuration(style, true), LayerMotion.Duration(style, true));
            Assert.Equal(MotionTiming.VisibilityDuration(style, false), LayerMotion.Duration(style, false));
        }
    }

    private static void Compare(string name, double[] samples, double duration, Func<double, double> value)
    {
        var largest = 0.0;
        for (var millisecond = 0; millisecond < samples.Length; millisecond++)
        {
            var time = duration > 0 ? Math.Min(1, millisecond / 1000.0 / duration) : 1;
            largest = Math.Max(largest, Math.Abs(samples[millisecond] - value(time)));
        }
        Assert.True(largest <= Tolerance, $"{name}: largest difference {largest:E3}.");
    }

    private static (double Duration, double[] Samples) Timeline(string name)
    {
        foreach (var line in File.ReadLines(Corpus("timelines.jsonl")))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement record = document.RootElement;
            if (record.GetProperty("name").GetString() == name)
            {
                return (record.GetProperty("duration").GetDouble(),
                        [.. record.GetProperty("samplesEveryMillisecond").EnumerateArray().Select(value => value.GetDouble())]);
            }
        }
        throw new Xunit.Sdk.XunitException($"The corpus has no timeline '{name}'.");
    }

    private static string Corpus(string file)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "reference", "conformance", "motion", file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("reference/conformance/motion was not found above the test output.", file);
    }
}
