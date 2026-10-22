using Annotator.Core.Geometry;

namespace Annotator.Core.Tests;

/// <summary>Path construction, bounds and transforms with Core Graphics semantics (assumptions marked in PathBuilder).</summary>
public class PathDataTests
{
    private const double K = PathBuilder.QuarterCircleControl;

    [Fact]
    public void RectanglesFollowTheDocumentedCoreGraphicsOrder()
    {
        var path = new PathBuilder().AddRect(new Rect(10, 20, 30, 40)).ToPathData();
        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo, PathVerb.LineTo, PathVerb.LineTo, PathVerb.Close], path.Verbs.ToArray());
        Assert.Equal([new Point(10, 20), new Point(40, 20), new Point(40, 60), new Point(10, 60)], path.Points.ToArray());
        Assert.Equal(new Point(10, 20), path.CurrentPoint);
    }

    [Fact]
    public void EllipsesStartAtTheRightAndTurnTowardIncreasingAngles()
    {
        var path = new PathBuilder().AddEllipse(new Rect(0, 0, 100, 60)).ToPathData();
        Assert.Equal([PathVerb.MoveTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.Close],
                     path.Verbs.ToArray());
        var points = path.Points.ToArray();
        Assert.Equal(new Point(100, 30), points[0]);
        Assert.Equal(new Point(100, 30 + 30 * K), points[1]);
        Assert.Equal(new Point(50 + 50 * K, 60), points[2]);
        Assert.Equal(new Point(50, 60), points[3]);
        Assert.Equal(new Point(0, 30), points[6]);
        Assert.Equal(new Point(50, 0), points[9]);
        Assert.Equal(new Point(100, 30), points[12]);
        Assert.Equal(new Rect(0, 0, 100, 60), path.Bounds);
        Assert.True(path.Contains(new Point(50, 30)));
        Assert.False(path.Contains(new Point(3, 3)));
    }

    [Fact]
    public void ZeroRadiusRoundedRectanglesAreRectangles()
    {
        var rect = new Rect(20, 30, 300, 180);
        Assert.True(new PathBuilder().AddRect(rect).ToPathData().ContentEquals(new PathBuilder().AddRoundedRect(rect, 0, 0).ToPathData()));
    }

    [Fact]
    public void RoundedRectanglesAddTheLineBeforeEveryCornerEvenWhenEmpty()
    {
        var path = new PathBuilder().AddRoundedRect(new Rect(0, 0, 100, 50), 10, 10).ToPathData();
        Assert.Equal(
            [
                PathVerb.MoveTo, PathVerb.LineTo, PathVerb.CubicTo, PathVerb.LineTo, PathVerb.CubicTo,
                PathVerb.LineTo, PathVerb.CubicTo, PathVerb.LineTo, PathVerb.CubicTo, PathVerb.Close,
            ],
            path.Verbs.ToArray());
        var points = path.Points.ToArray();
        Assert.Equal(new Point(100, 25), points[0]);
        Assert.Equal(new Point(100, 40), points[1]);
        Assert.Equal(new Point(100, 40 + 10 * K), points[2]);
        Assert.Equal(new Point(90 + 10 * K, 50), points[3]);
        Assert.Equal(new Point(90, 50), points[4]);
        Assert.Equal(new Point(100, 10), points[^1]);
        // A pill has no straight vertical edges, but Core Graphics still adds the empty lines there.
        var pill = new PathBuilder().AddRoundedRect(new Rect(0, 0, 100, 50), 25, 25).ToPathData();
        Assert.Equal(
            [
                PathVerb.MoveTo, PathVerb.LineTo, PathVerb.CubicTo, PathVerb.LineTo, PathVerb.CubicTo,
                PathVerb.LineTo, PathVerb.CubicTo, PathVerb.LineTo, PathVerb.CubicTo, PathVerb.Close,
            ],
            pill.Verbs.ToArray());
        Assert.Equal(pill.Points[0], pill.Points[1]);
        // Core Graphics asserts on oversized corners; the port clamps them to half the rectangle.
        Assert.True(new PathBuilder().AddRoundedRect(new Rect(0, 0, 100, 50), 25, 99).ToPathData().ContentEquals(pill));
    }

    [Fact]
    public void ArcsSplitIntoQuarterTurnsAndWrapInTheChosenDirection()
    {
        var circle = new PathBuilder().AddArc(new Point(10, 10), 5, 0, 2 * Math.PI, clockwise: false).ToPathData();
        Assert.Equal([PathVerb.MoveTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.CubicTo], circle.Verbs.ToArray());
        foreach (var point in SceneTestSupport.Samples(circle))
        {
            Assert.True(Math.Abs(point.Distance(new Point(10, 10)) - 5) < 0.002);
        }
        var quarter = new PathBuilder().MoveTo(new Point(15, 10)).AddArc(new Point(10, 10), 5, 0, Math.PI / 2, false).ToPathData();
        // Core Graphics adds the line to the arc start even when the pen is already there.
        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo, PathVerb.CubicTo], quarter.Verbs.ToArray());
        Assert.True(quarter.CurrentPoint!.Value.Distance(new Point(10, 15)) < 1e-12);
        var wrapped = new PathBuilder().AddArc(new Point(0, 0), 1, 0, -Math.PI / 2, clockwise: false).ToPathData();
        Assert.Equal(4, wrapped.VerbCount);
        Assert.True(wrapped.CurrentPoint!.Value.Distance(new Point(0, -1)) < 1e-12);
        var connected = new PathBuilder().MoveTo(new Point(0, 0)).AddArc(new Point(10, 0), 2, Math.PI, Math.PI / 2, clockwise: true).ToPathData();
        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo, PathVerb.CubicTo], connected.Verbs.ToArray());
    }

    [Fact]
    public void BoundsIncludeControlPointsButTightBoundsDoNot()
    {
        var path = new PathBuilder().MoveTo(new Point(0, 0)).CubicTo(new Point(0, 100), new Point(100, 100), new Point(100, 0)).ToPathData();
        Assert.Equal(new Rect(0, 0, 100, 100), path.Bounds);
        var tight = Assert.NotNull(path.TightBounds);
        Assert.Equal(75, tight.Height, 9);
        Assert.Equal(100, tight.Width, 9);
        Assert.Null(PathData.Empty.Bounds);
        Assert.Null(PathData.Empty.TightBounds);
        var quad = new PathBuilder().MoveTo(new Point(0, 0)).QuadTo(new Point(50, 100), new Point(100, 0)).ToPathData();
        Assert.Equal(50, Assert.NotNull(quad.TightBounds).Height, 9);
    }

    [Fact]
    public void ContainmentFollowsTheFillRule()
    {
        var nested = new PathBuilder().AddRect(new Rect(0, 0, 100, 100)).AddRect(new Rect(25, 25, 50, 50)).ToPathData();
        Assert.True(nested.Contains(new Point(50, 50), FillRule.NonZero));
        Assert.False(nested.Contains(new Point(50, 50), FillRule.EvenOdd));
        Assert.True(nested.Contains(new Point(10, 10), FillRule.EvenOdd));
        Assert.False(nested.Contains(new Point(150, 50), FillRule.NonZero));
        var open = new PathBuilder().MoveTo(new Point(0, 0)).LineTo(new Point(10, 0)).LineTo(new Point(10, 10)).ToPathData();
        Assert.True(open.Contains(new Point(8, 2)), "Fills close open subpaths.");
    }

    [Fact]
    public void AppendedLinesShareStorageWithoutChangingEarlierPaths()
    {
        var first = new PathBuilder().MoveTo(new Point(0, 0)).LineTo(new Point(10, 0)).ToPathData(reserveForAppends: true);
        var second = first.AppendLines([new Point(10, 10)]);
        var third = second.AppendLines([new Point(20, 10), new Point(20, 30)]);
        Assert.Equal(2, first.PointCount);
        Assert.Equal([new Point(0, 0), new Point(10, 0)], first.Points.ToArray());
        Assert.Equal(new Rect(0, 0, 10, 0), first.Bounds);
        Assert.Equal(5, third.PointCount);
        Assert.Equal(new Rect(0, 0, 20, 30), third.Bounds);
        Assert.Equal(new Point(20, 30), third.CurrentPoint);
        // A branch from an older view copies instead of overwriting the newer one.
        var branch = second.AppendLines([new Point(-5, -5)]);
        Assert.Equal(new Point(20, 10), third.Points[3]);
        Assert.Equal(new Point(-5, -5), branch.Points[3]);
        Assert.Equal(new Rect(-5, -5, 15, 15), branch.Bounds);
    }

    [Fact]
    public void ClosingWithoutAnOpenSubpathAndSegmentsWithoutAPointAreIgnored()
    {
        Assert.True(new PathBuilder().Close().ToPathData().IsEmpty);
        var closedTwice = new PathBuilder().AddRect(new Rect(0, 0, 1, 1)).Close().ToPathData();
        Assert.Equal(5, closedTwice.VerbCount);
        Assert.True(new PathBuilder().LineTo(new Point(1, 1)).ToPathData().IsEmpty);
        Assert.True(new PathBuilder().CubicTo(default, default, default).ToPathData().IsEmpty);
        var replaced = new PathBuilder().MoveTo(new Point(0, 0)).MoveTo(new Point(10, 10)).LineTo(new Point(20, 10)).ToPathData();
        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo], replaced.Verbs.ToArray());
        Assert.Equal(new Rect(10, 10, 10, 0), replaced.Bounds);
    }

    [Fact]
    public void TransformsFollowCoreGraphicsConcatenationOrder()
    {
        var rotation = AffineTransform.RotationAbout(new Point(10, 20), Math.PI / 2);
        var turned = rotation.Transform(new Point(11, 20));
        Assert.True(turned.Distance(new Point(10, 21)) < 1e-12);
        Assert.Equal(new Point(10, 20), rotation.Transform(new Point(10, 20)));
        Assert.True(AffineTransform.RotationAbout(new Point(3, 4), 0).IsIdentity);
        var scaled = AffineTransform.Translation(5, 6).Scaled(2, 3).Translated(-1, -1);
        Assert.Equal(new Point(5, 6), scaled.Transform(new Point(1, 1)));
        Assert.Equal(new Point(7, 9), scaled.Transform(new Point(2, 2)));
        var box = AffineTransform.RotationAbout(new Point(0, 0), Math.PI / 4).TransformBounds(new Rect(-1, -1, 2, 2));
        Assert.Equal(-Math.Sqrt(2), box.X, 12);
        Assert.Equal(2 * Math.Sqrt(2), box.Width, 12);
        var inverse = Assert.NotNull(scaled.Inverted());
        Assert.True(inverse.Transform(scaled.Transform(new Point(3, -7))).Distance(new Point(3, -7)) < 1e-12);
        Assert.Null(AffineTransform.Scale(0, 1).Inverted());
        var path = new PathBuilder().AddRect(new Rect(0, 0, 2, 1)).ToPathData().Transformed(AffineTransform.Translation(1, 1));
        Assert.Equal(new Rect(1, 1, 2, 1), path.Bounds);
    }

    [Fact]
    public void RectMathFollowsCoreGraphicsNullRules()
    {
        var rect = new Rect(10, 10, 20, 10);
        Assert.Null(RectMath.Inset(rect, 6, 6));
        Assert.Equal(new Rect(12, 12, 16, 6), RectMath.Inset(rect, 2, 2));
        Assert.Equal(new Rect(7, 8, 26, 14), RectMath.Outset(rect, 3, 2));
        Assert.Equal(rect, RectMath.Union(rect, (Rect?)null));
        Assert.Null(RectMath.Union(null, null));
        Assert.Null(RectMath.Intersection(rect, new Rect(40, 40, 1, 1)));
        Assert.True(RectMath.IsEmpty(RectMath.Intersection(rect, new Rect(30, 10, 5, 5))));
        Assert.Equal(new Rect(1, -2, 3, 4), RectMath.Integral(new Rect(1.5, -1.5, 2.2, 3.1)));
        Assert.True(RectMath.Contains(rect, new Rect(10, 10, 20, 10)));
        Assert.False(RectMath.Contains(rect, new Rect(9.9, 10, 1, 1)));
    }
}
