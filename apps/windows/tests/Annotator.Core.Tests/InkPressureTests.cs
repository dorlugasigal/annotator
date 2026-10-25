// Port of apps/macos/Tests/AnnotationCoreTests/InkPressureTests.swift.
namespace Annotator.Core.Tests;

public class InkPressureTests
{
    private static double[] Pressures(double step, double interval, int count, double width = 3)
    {
        var ink = new InkPressure(width);
        var values = new double[count];
        for (int index = 0; index < count; index++)
        {
            values[index] = ink.Next(new Point(index * step, 0), index * interval);
        }
        return values;
    }

    [Fact]
    public void InkDrawsFastMovementThinnerThanSlowMovement()
    {
        var slow = Pressures(1, 0.016, 60);
        var fast = Pressures(25, 0.008, 60);
        Assert.True(slow[40] > 0.9);
        Assert.True(fast[40] < 0.5);
        Assert.All(fast, value => Assert.InRange(value, InkPressure.Thinnest * InkPressure.Tip, 1));
    }

    [Fact]
    public void InkTapersTheStartAndTheEnd()
    {
        var drawn = Pressures(1, 0.016, 60);
        Assert.Equal(InkPressure.Tip, drawn[0]);
        Assert.True(drawn[1] < drawn[5] && drawn[5] < drawn[20]);
        var points = ValueList.Create<Point>(Enumerable.Range(0, 60).Select(index => new Point(index, 0)).ToArray());
        var tapered = InkPressure.TaperEnd(points, ValueList.Create<double>(drawn), 3);
        Assert.Equal(drawn[59] * InkPressure.Tip, tapered[59]);
        Assert.True(tapered[58] < tapered[52] && tapered[52] < tapered[40]);
        Assert.Equal(drawn[..48], tapered.AsSpan()[..48].ToArray());
    }

    [Fact]
    public void InkKeepsEarlierPressuresWhileTheStrokeGrows()
    {
        Assert.Equal(Pressures(7, 0.009, 20), Pressures(7, 0.009, 40)[..20]);
    }

    [Fact]
    public void InkCopesWithSharedAndBackwardTimes()
    {
        var ink = new InkPressure(3);
        (Point Point, double Time)[] samples = [(new(0, 0), 1), (new(5, 0), 1), (new(10, 0), 0.5), (new(15, 0), double.NaN), (new(20, 0), 2)];
        foreach (var (point, time) in samples)
        {
            Assert.InRange(ink.Next(point, time), 0, 1);
        }
    }

    [Fact]
    public void InkEndTaperLeavesShortOrMismatchedStrokesAlone()
    {
        Assert.Equal([0.8], InkPressure.TaperEnd([new Point(1, 1)], [0.8], 3).AsSpan().ToArray());
        Assert.True(InkPressure.TaperEnd([new Point(1, 1), new Point(2, 2)], default, 3).IsEmpty);
        Assert.Equal(12, new InkPressure(2).TaperLength);
        Assert.Equal(40, new InkPressure(10).TaperLength);
    }
}
