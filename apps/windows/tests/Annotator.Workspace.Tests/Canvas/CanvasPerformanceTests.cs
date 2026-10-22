using System.Diagnostics;
using Annotator.Core;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// WIN-INP-01 and WIN-PERF-01: a 20,000-sample stroke keeps every sample with amortized constant work per sample and no
/// allocation per move in steady state (only the sample lists grow by doubling).
/// </summary>
public sealed class CanvasPerformanceTests(ITestOutputHelper output)
{
    private const int Samples = 20_000;

    [Fact]
    public void ATwentyThousandSampleStrokeStaysConstantTimeAndAllocationFree()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        CanvasController canvas = h[Main];
        var buffer = new PointerSample[1];
        h.Down(Main, Spiral(0), device: PointerDevice.Pen, pressure: 0.5, hasPressure: true);
        int changes = 0;
        canvas.Changed += _ => changes++;

        long[] moveTicks = new long[Samples];
        int allocatingMoves = 0;
        long allocated = 0;
        var clock = Stopwatch.StartNew();
        for (int index = 1; index <= Samples; index++)
        {
            buffer[0] = new PointerSample(Spiral(index), 0.25 + index % 50 / 100.0, index);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = clock.ElapsedTicks;
            canvas.OnPointer(new PointerEvent
            {
                Phase = PointerPhase.Move,
                Device = PointerDevice.Pen,
                PointerId = 1,
                Buttons = PointerButtons.Primary,
                HasPressure = true,
                Samples = buffer,
            });
            moveTicks[index - 1] = clock.ElapsedTicks - start;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            if (bytes > 0)
            {
                allocatingMoves++;
                allocated += bytes;
            }
        }
        clock.Stop();

        Assert.Equal(Samples, changes);
        Assert.Equal(Samples + 1, canvas.State.Draft?.Points.Count);
        double microsecondsPerSample = clock.Elapsed.TotalMilliseconds * 1000 / Samples;
        double early = Mean(moveTicks.AsSpan(1_000, 2_000));
        double late = Mean(moveTicks.AsSpan(Samples - 2_000, 2_000));
        output.WriteLine(
            $"20,000-sample pen stroke: {microsecondsPerSample:F3} µs per sample on average; samples 1,000-3,000 {Ticks(early):F3} µs, " +
            $"samples 18,000-20,000 {Ticks(late):F3} µs; {allocatingMoves} of {Samples} moves allocated ({allocated:N0} bytes, list growth).");
        Assert.True(allocatingMoves <= 64, $"{allocatingMoves} moves allocated; only list growth may allocate.");
        Assert.True(allocated < Samples * 128L, $"{allocated} bytes allocated for {Samples} samples.");
        Assert.True(microsecondsPerSample < 100, $"{microsecondsPerSample} µs per sample.");
        Assert.True(late < early * 8 + Stopwatch.Frequency / 100_000.0, $"Late samples took {Ticks(late)} µs against {Ticks(early)} µs early.");

        h.Up(Main, Spiral(Samples + 1), pressure: 0, hasPressure: true);
        Element stroke = Assert.Single(h.Elements(Main));
        Assert.Equal(Samples + 2, stroke.Points.Count);
        Assert.Equal(Samples + 2, stroke.Pressures.Count);
        Assert.Equal(Spiral(12_345), stroke.Points[12_345]);
        Assert.Equal(0.25 + 12_345 % 50 / 100.0, stroke.Pressures[12_345]);
    }

    [Fact]
    public void ShapeDragsAllocateNothingPerMove()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Rectangle);
        CanvasController canvas = h[Main];
        var buffer = new PointerSample[1];
        h.Down(Main, new Point(10, 10));
        for (int index = 0; index < 100; index++)
        {
            buffer[0] = new PointerSample(new Point(20 + index, 30 + index), 1, index);
            canvas.OnPointer(new PointerEvent { Phase = PointerPhase.Move, PointerId = 1, Buttons = PointerButtons.Primary, Samples = buffer });
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
        {
            buffer[0] = new PointerSample(new Point(200 + index % 300, 300 + index % 200), 1, 100 + index);
            canvas.OnPointer(new PointerEvent { Phase = PointerPhase.Move, PointerId = 1, Buttons = PointerButtons.Primary, Samples = buffer });
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"1,000 rectangle drag moves allocated {allocated:N0} bytes.");
        Assert.Equal(0, allocated);
        h.Up(Main, new Point(300, 300));
        Assert.Single(h.Elements(Main));
    }

    private static Point Spiral(int index)
    {
        double angle = index * 0.01;
        double radius = 50 + index * 0.02;
        return new Point(800 + radius * Math.Cos(angle), 600 + radius * Math.Sin(angle));
    }

    private static double Mean(ReadOnlySpan<long> values)
    {
        double total = 0;
        foreach (long value in values)
        {
            total += value;
        }
        return total / values.Length;
    }

    private static double Ticks(double ticks) => ticks * 1_000_000 / Stopwatch.Frequency;
}
