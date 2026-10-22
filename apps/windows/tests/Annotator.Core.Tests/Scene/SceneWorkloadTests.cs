// WIN-PERF-01 workloads (spec 003): the fixtures of apps/macos/Sources/PerformanceBenchmarks/Fixtures.swift, built into display lists.
// Times are written to the test output; only generous upper bounds are asserted, so slow machines do not fail.
using System.Diagnostics;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Core.Tests;

public class SceneWorkloadTests(ITestOutputHelper output)
{
    private const ulong FixtureSeed = 0xA660_2026;

    [Fact]
    public void StandardAndStressScenesBuildWithinGenerousBounds()
    {
        Run("standard: 200 mixed elements + 20,000-sample stroke", Elements(200), Stroke(20_000), coldLimitMs: 2_000);
        Run("stress: 999 mixed elements + 200,000-sample stroke", Elements(999), Stroke(200_000), coldLimitMs: 15_000);
    }

    private void Run(string name, Element[] mixed, Element stroke, double coldLimitMs)
    {
        var elements = ElementList.Create([.. mixed, stroke]);
        var pathElements = 0;
        var samples = stroke.Points.Count;
        foreach (ref readonly var element in elements)
        {
            if (element.Tool.IsShape || element.Tool.IsFreehand || element.Tool.IsConnector)
            {
                pathElements++;
            }
            if (element.Id != stroke.Id)
            {
                samples += element.Points.Count;
            }
        }
        var measurer = new FakeTextMeasurer();
        // The first build also compiles the code (the app uses Native AOT, so it never pays this); the second
        // builder measures an empty cache with compiled code.
        var clock = Stopwatch.StartNew();
        _ = new SceneBuilder(measurer).Build(elements);
        var firstMs = clock.Elapsed.TotalMilliseconds;
        var builder = new SceneBuilder(measurer);
        clock.Restart();
        var cold = builder.Build(elements);
        var coldMs = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(pathElements, builder.FullPathBuilds);
        Assert.Equal(elements.Count, cold.Elements.Length);

        var cachedTimes = new double[20];
        for (var run = 0; run < cachedTimes.Length; run++)
        {
            clock.Restart();
            var again = builder.Build(elements);
            cachedTimes[run] = clock.Elapsed.TotalMilliseconds;
            Assert.Same(cold, again);
        }
        Array.Sort(cachedTimes);

        // An in-progress stroke: every new sample rebuilds one list and extends the cached path.
        const int appended = 2_000;
        var builds = builder.FullPathBuilds;
        var sampleTimes = new double[appended];
        var last = elements.Count - 1;
        var growing = stroke;
        var allocated = 0L;
        for (var sample = 0; sample < appended; sample++)
        {
            growing.Points = growing.Points.Add(new Point(1_910 - sample * 0.5, 540 + Math.Sin(sample * 0.05) * 300));
            growing.Frame = growing.Frame.Union(new Rect(growing.Points[^1], growing.Points[^1]));
            growing.GeometryToken = Guid.NewGuid();
            elements = elements.SetItem(last, growing);
            var before = GC.GetAllocatedBytesForCurrentThread();
            clock.Restart();
            var scene = builder.Build(elements);
            sampleTimes[sample] = clock.Elapsed.TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Same(cold.Elements[0], scene.Elements[0]);
        }
        Assert.Equal(builds, builder.FullPathBuilds);
        Assert.Equal(appended, builder.IncrementalAppends);
        Assert.Equal(growing.Points.Count, builder.Path(growing).PointCount);
        Array.Sort(sampleTimes);
        var mean = sampleTimes.Average();

        output.WriteLine($"{name}: {elements.Count} elements, {pathElements} path elements, {samples:N0} stored samples");
        output.WriteLine($"  cold build (empty cache): {coldMs:F2} ms; first build in the process, including JIT: {firstMs:F2} ms");
        output.WriteLine($"  unchanged rebuild: median {cachedTimes[cachedTimes.Length / 2]:F4} ms, max {cachedTimes[^1]:F4} ms");
        output.WriteLine($"  per new stroke sample ({appended}): mean {mean:F4} ms, p95 {sampleTimes[(int)(appended * 0.95)]:F4} ms, "
                         + $"max {sampleTimes[^1]:F4} ms, {allocated / appended:N0} bytes allocated");
        Assert.True(coldMs < coldLimitMs, $"{name}: cold build took {coldMs:F1} ms.");
        Assert.True(cachedTimes[cachedTimes.Length / 2] < 50, $"{name}: an unchanged scene took {cachedTimes[cachedTimes.Length / 2]:F2} ms.");
        Assert.True(mean < 25, $"{name}: a stroke sample took {mean:F2} ms on average.");
    }

    private static Guid Id(int index)
    {
        var n = (ulong)index;
        ReadOnlySpan<byte> bytes =
        [
            0xA6, 0x60, 0x20, 0x26, 0, 0, 0x40, 0, 0x80, 0,
            (byte)(n >> 40), (byte)(n >> 32), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n,
        ];
        return new Guid(bytes, bigEndian: true);
    }

    private static Element[] Elements(int count)
    {
        Tool[] tools = [Tool.Rectangle, Tool.Ellipse, Tool.Diamond, Tool.Arrow, Tool.Pen, Tool.Highlighter, Tool.Text, Tool.Counter, Tool.Grid, Tool.Axes];
        var result = new Element[count];
        for (var index = 0; index < count; index++)
        {
            var tool = tools[index % tools.Length];
            double x = index * 73 % 1_800 + 20, y = index * 97 % 950 + 20;
            var style = new Style { Roughness = index % 3 * 2, Width = 2, FontSize = 14 };
            if (tool.SupportsFill && index % 4 == 0)
            {
                style.Fill = new RGBA(0.2, 0.4, 0.8, 0.2);
            }
            ValueList<Point> points = default;
            if (tool.IsFreehand || tool.IsConnector)
            {
                var array = new Point[24];
                for (var step = 0; step < array.Length; step++)
                {
                    array[step] = new Point(x + step * 3, y + Math.Sin(step * 0.2) * 25);
                }
                points = ValueList.Create<Point>(array);
            }
            result[index] = new Element(tool, new Rect(x, y, 75, 55), points, tool == Tool.Counter ? $"{index % 99}" : $"Owned {index}", style, Id(index))
            {
                Seed = unchecked(FixtureSeed + (ulong)index),
                GeometryToken = Id(index + 1_000_000),
                AppendEpoch = Id(index + 2_000_000),
            };
        }
        return result;
    }

    private static Element Stroke(int count)
    {
        var points = new Point[count];
        for (var index = 0; index < count; index++)
        {
            var t = (double)index / Math.Max(1, count - 1);
            points[index] = new Point(10 + t * 1_900, 540 + Math.Sin(t * Math.PI * 24) * 400);
        }
        return new Element(Tool.Pen, points: ValueList.Create<Point>(points), id: Id(900_000))
        {
            Seed = FixtureSeed,
            GeometryToken = Id(3_000_000),
            AppendEpoch = Id(4_000_000),
        };
    }
}
