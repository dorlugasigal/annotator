// Ports of apps/macos/Tests/RenderingTests/StrokeHistoryCacheRegressionTests.swift, PressurePathElisionTests.swift, PressureClipTests.swift
// and the cache tests of RenderingTests.swift. Swift compares rendered pixels with a fresh renderer; these compare the display
// lists with a fresh builder's, which determine the pixels.
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using static Annotator.Core.Tests.SceneTestSupport;

namespace Annotator.Core.Tests;

public enum StrokeRestore
{
    Cancel,
    Undo,
    Redo,
}

public class StrokeCacheTests
{
    [Theory]
    [InlineData(Tool.Pen, StrokeRestore.Cancel)]
    [InlineData(Tool.Pen, StrokeRestore.Undo)]
    [InlineData(Tool.Pen, StrokeRestore.Redo)]
    [InlineData(Tool.Highlighter, StrokeRestore.Cancel)]
    [InlineData(Tool.Highlighter, StrokeRestore.Undo)]
    [InlineData(Tool.Highlighter, StrokeRestore.Redo)]
    public void StrokeHistoryCacheRegression(Tool tool, StrokeRestore restoration)
    {
        var builder = Builder();
        var stroke = new Element(tool, points: [new Point(20, 20), new Point(40, 20)]);
        var document = new CanvasDocument([stroke]);
        if (restoration == StrokeRestore.Redo)
        {
            document.Begin();
            document.AppendSample(stroke.Id, new Point(60, 20));
            document.AppendSample(stroke.Id, new Point(80, 20));
            document.Commit();
            document.Undo();
        }
        document.Begin();
        document.AppendSample(stroke.Id, new Point(40, 100));
        _ = builder.Build(document.Elements);
        if (restoration == StrokeRestore.Undo)
        {
            document.Commit();
            document.Undo();
        }
        else
        {
            document.Cancel();
        }
        Assert.True(document.Elements == ElementList.Create([stroke]));
        if (restoration == StrokeRestore.Redo)
        {
            document.Redo();
        }
        else
        {
            document.Begin();
            document.AppendSample(stroke.Id, new Point(60, 20));
            document.AppendSample(stroke.Id, new Point(80, 20));
            document.Commit();
        }

        AssertSameScenes(Builder().Build(document.Elements), builder.Build(document.Elements));
        Assert.Equal(document.Elements[0].Points.ToArray(), builder.Path(document.Elements[0]).Points.ToArray());
        Assert.Equal(2, builder.FullPathBuilds);

        document.AppendSample(stroke.Id, new Point(100, 20));
        AssertSameScenes(Builder().Build(document.Elements), builder.Build(document.Elements));
        var builds = builder.FullPathBuilds;
        var appends = builder.IncrementalAppends;
        document.AppendSample(stroke.Id, new Point(120, 20));
        AssertSameScenes(Builder().Build(document.Elements), builder.Build(document.Elements));
        Assert.Equal(builds, builder.FullPathBuilds);
        Assert.Equal(appends + 1, builder.IncrementalAppends);
    }

    [Fact]
    public void UnchangedPathsAreCachedAndAppended()
    {
        var builder = Builder();
        var document = CanvasDocument.Empty();
        var pen = new Element(Tool.Pen);
        document.Begin();
        document.Add(pen);
        document.AppendSample(pen.Id, new Point(10, 10));
        document.AppendSample(pen.Id, new Point(20, 20));
        var first = builder.Build(document.Elements);
        var builds = builder.FullPathBuilds;
        Assert.Same(first, builder.Build(document.Elements));
        Assert.Equal(builds, builder.FullPathBuilds);
        document.AppendSample(pen.Id, new Point(30, 30));
        builder.Build(document.Elements);
        Assert.Equal(1, builder.IncrementalAppends);
    }

    [Fact]
    public void UndoBranchDoesNotReuseStaleGeometry()
    {
        var builder = Builder();
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 40, 40));
        var document = new CanvasDocument([shape]);
        document.Update(shape.Id, (ref Element element) => element.Frame = element.Frame with { Width = 60 });
        builder.Build(document.Elements);
        document.Undo();
        document.Update(shape.Id, (ref Element element) => element.Frame = element.Frame with { Height = 90 });
        var count = builder.FullPathBuilds;
        builder.Build(document.Elements);
        Assert.Equal(count + 1, builder.FullPathBuilds);
    }

    [Fact]
    public void RoughInkGrowsWithoutRebuildingAndMatchesAFullBuild()
    {
        var builder = Builder();
        var stroke = new Element(Tool.Pen, points: [new Point(0, 0), new Point(5, 5), new Point(10, 0)]);
        stroke.Style.Roughness = 4;
        builder.Build([stroke]);
        Assert.Equal(1, builder.FullPathBuilds);
        for (var sample = 0; sample < 50; sample++)
        {
            stroke.Points = stroke.Points.Add(new Point(12 + sample, sample % 7));
            stroke.GeometryToken = Guid.NewGuid();
            AssertSameScenes(Builder().Build([stroke]), builder.Build([stroke]));
        }
        Assert.Equal(1, builder.FullPathBuilds);
        Assert.Equal(50, builder.IncrementalAppends);
        Assert.NotEqual(stroke.Points[10], builder.Path(stroke).Points[10]);
        Assert.Equal(stroke.Points[0], builder.Path(stroke).Points[0]);
    }

    [Fact]
    public void RemovedElementsAreEvictedAndUnchangedListsAreShared()
    {
        var builder = Builder();
        var first = new Element(Tool.Rectangle, new Rect(0, 0, 10, 10));
        var second = new Element(Tool.Ellipse, new Rect(20, 0, 10, 10));
        var scene = builder.Build([first, second]);
        Assert.Equal(2, builder.FullPathBuilds);
        var moved = second;
        moved.Translate(new Point(1, 0));
        var next = builder.Build([first, moved]);
        Assert.NotSame(scene, next);
        Assert.Same(scene.Elements[0], next.Elements[0]);
        Assert.NotSame(scene.Elements[1], next.Elements[1]);
        Assert.Equal(3, builder.FullPathBuilds);
        builder.Build([moved]);
        builder.Build([first, moved]);
        Assert.Equal(4, builder.FullPathBuilds);
        builder.Reset();
        builder.Build([first, moved]);
        Assert.Equal(6, builder.FullPathBuilds);
        Assert.Same(SceneSnapshot.Empty, builder.Build(default));
    }

    [Fact]
    public void PressureRenderingDoesNotBuildAnUnusedWholeStrokePath()
    {
        var points = new Point[2_000];
        var pressures = new double[2_000];
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = new Point(20 + index * 0.1, 100 + Math.Sin(index * 0.01) * 50);
            pressures[index] = index % 101 / 100.0;
        }
        var stroke = new Element(Tool.Pen, points: ValueList.Create<Point>(points)) { Pressures = ValueList.Create<double>(pressures) };
        stroke.Style.Pressure = true;
        stroke.Style.Roughness = 6;
        var builder = Builder();
        var original = builder.Build([stroke]);
        var run = Assert.Single(Items(original.Elements[0], DisplayOp.StrokeSegments)).Segments!;
        Assert.Equal(1_999, run.Count);
        Assert.Equal(0, builder.FullPathBuilds);
        Assert.Equal(0, builder.IncrementalAppends);
        stroke.Points = stroke.Points.Add(new Point(220, 220));
        stroke.Pressures = stroke.Pressures.Add(0.8);
        stroke.GeometryToken = Guid.NewGuid();
        var appended = builder.Build([stroke]);
        Assert.False(original.Elements[0].ContentEquals(appended.Elements[0]));
        AssertSameScenes(Builder().Build([stroke]), appended);
        Assert.Equal(0, builder.FullPathBuilds);
        Assert.Equal(2_001, stroke.Points.Count);
        Assert.Equal(2_001, stroke.Pressures.Count);
    }

    [Fact]
    public void PressureModeTransitionsDoNotReuseAbandonedGeometry()
    {
        var stroke = new Element(Tool.Pen, points: [new Point(20, 30), new Point(100, 180), new Point(180, 40)])
        {
            Pressures = [0.2, 0.8, 0.4],
            Rotation = 0.2,
        };
        stroke.Style.Opacity = 0.5;
        var builder = Builder();
        builder.Build([stroke]);
        stroke.Style.Pressure = true;
        stroke.Points = stroke.Points.Add(new Point(210, 160));
        stroke.Pressures = stroke.Pressures.Add(1);
        stroke.GeometryToken = Guid.NewGuid();
        var effect = new Element(Tool.Blur, new Rect(100, 100, 40, 40));
        var cover = new Element(Tool.Cover, new Rect(60, 60, 30, 30));
        ElementList elements = [cover, stroke, effect];
        foreach (var pressure in new[] { true, false, true })
        {
            stroke.Style.Pressure = pressure;
            elements = [cover, stroke, effect];
            foreach (var scale in new[] { 1.0, 1.25, 2.0 })
            {
                foreach (var faded in new[] { new HashSet<Guid>(), [stroke.Id] })
                {
                    foreach (var effectsReady in new[] { false, true })
                    {
                        HashSet<Guid> selected = [stroke.Id];
                        var actual = builder.Build(elements, selected, effectsReady, scale, faded);
                        var expected = Builder().Build(elements, selected, effectsReady, scale, faded);
                        AssertSameScenes(expected, actual);
                    }
                }
            }
        }
        builder.Reset();
        var blank = builder.Build(ElementList.Empty);
        Assert.Equal(0, blank.Elements.Length);
        Assert.True(blank.Selection.IsEmpty);
    }

    [Fact]
    public void SinglePointInkDoesNotBuildAPathBeforeItCanBeStroked()
    {
        foreach (var tool in new[] { Tool.Pen, Tool.Highlighter })
        {
            var stroke = new Element(tool, points: [new Point(60, 60)]);
            var builder = Builder();
            builder.Build([stroke]);
            Assert.Equal(0, builder.FullPathBuilds);
            stroke.Points = stroke.Points.Add(new Point(100, 100));
            stroke.GeometryToken = Guid.NewGuid();
            AssertSameScenes(Builder().Build([stroke]), builder.Build([stroke]));
            Assert.Equal(1, builder.FullPathBuilds);
        }
    }

    /// <summary>
    /// The geometric form of clippedPressurePixelsMatchUnculledReference: culling may skip a segment only when its
    /// round-capped stroke cannot touch the clip, so clipped pixels equal the unculled reference.
    /// </summary>
    [Fact]
    public void PressureCullingNeverSkipsASegmentThatReachesTheClip()
    {
        var points = new Point[2_000];
        var pressures = new double[2_000];
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = new Point(index * 0.4 - 100, 120 + Math.Sin(index * 0.037) * 110);
            pressures[index] = index % 101 / 100.0;
        }
        var stroke = new Element(Tool.Pen, points: ValueList.Create<Point>(points)) { Pressures = ValueList.Create<double>(pressures) };
        stroke.Style.Pressure = true;
        stroke.Style.Width = 12;
        stroke.Style.Opacity = 0.45;
        stroke.Style.Color = new RGBA(0.2, 0.5, 0.8, 0.6);
        var culledTotal = 0;
        foreach (var scale in new[] { 0.02, 0.75, 1.0, 2.0 })
        {
            foreach (var rotation in new[] { 0.0, 0.37, -0.9 })
            {
                stroke.Rotation = rotation;
                foreach (var pattern in StrokePattern.AllCases)
                {
                    stroke.Style.Pattern = pattern;
                    foreach (var clip in new[] { new Rect(40, 40, 170, 170), new Rect(95.25, 100.75, 3.5, 4.5) })
                    {
                        var builder = Builder();
                        var list = builder.Build([stroke]).Elements[0];
                        Assert.Equal(0, builder.FullPathBuilds);
                        var run = Assert.Single(Items(list, DisplayOp.StrokeSegments)).Segments!;
                        var element = AffineTransform.RotationAbout(stroke.Frame.Center, rotation);
                        var device = AffineTransform.Concat(element, AffineTransform.Translation(0.25, 255.5).Scaled(scale, -scale));
                        var margin = StrokeSegments.CullMargin(device);
                        var userClip = Assert.NotNull(element.Inverted()).TransformBounds(clip);
                        if (!run.NeedsCulling(userClip, margin))
                        {
                            continue;
                        }
                        for (var segment = 0; segment < run.Count; segment++)
                        {
                            if (run.Reaches(segment, userClip, margin))
                            {
                                continue;
                            }
                            culledTotal++;
                            var distance = SegmentToRect(element.Transform(run.Start(segment)), element.Transform(run.End(segment)), clip);
                            Assert.True(distance > run.Width(segment) / 2, $"Segment {segment} was culled but reaches the clip.");
                        }
                    }
                }
            }
        }
        Assert.True(culledTotal > 10_000);
    }

    private static double SegmentToRect(Point a, Point b, Rect rect)
    {
        if (rect.Contains(a) || rect.Contains(b))
        {
            return 0;
        }
        Point[] corners = [rect.Origin, new(rect.MaxX, rect.Y), new(rect.MaxX, rect.MaxY), new(rect.X, rect.MaxY)];
        var best = double.PositiveInfinity;
        for (var index = 0; index < 4; index++)
        {
            Point c = corners[index], d = corners[(index + 1) % 4];
            if (Crosses(a, b, c, d))
            {
                return 0;
            }
            best = Math.Min(best, Math.Min(HitTesting.Distance(c, a, b), HitTesting.Distance(a, c, d)));
            best = Math.Min(best, HitTesting.Distance(b, c, d));
        }
        return best;
    }

    private static bool Crosses(Point a, Point b, Point c, Point d)
    {
        static double Cross(Point o, Point p, Point q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        return Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0;
    }
}
