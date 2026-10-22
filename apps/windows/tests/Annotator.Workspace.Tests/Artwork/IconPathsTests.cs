using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Artwork;

namespace Annotator.Workspace.Tests.Artwork;

public sealed class IconPathsTests
{
    [Fact]
    public void LinesQuadsCubicsAndClosesKeepTheirPoints()
    {
        PathData path = IconPaths.Parse("M 1,2 L 3,4 Q 5,6 7,8 C 9,10 11,12 13,14 Z");
        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo, PathVerb.QuadTo, PathVerb.CubicTo, PathVerb.Close], path.Verbs.ToArray());
        Assert.Equal(
            [new Point(1, 2), new Point(3, 4), new Point(5, 6), new Point(7, 8), new Point(9, 10), new Point(11, 12), new Point(13, 14)],
            path.Points.ToArray());
    }

    [Fact]
    public void AQuarterArcBecomesOneCubicOnTheCircle()
    {
        // The rounded rectangle icon's top-right corner: center (19.5, 7), radius 2.5.
        PathData path = IconPaths.Parse("M 19.5,4.5 A 2.5,2.5 0 0 1 22,7");
        Assert.Equal([PathVerb.MoveTo, PathVerb.CubicTo], path.Verbs.ToArray());
        Point[] points = path.Points.ToArray();
        Assert.Equal(new Point(22, 7), points[3]);
        double k = 4.0 / 3 * Math.Tan(Math.PI / 8) * 2.5;
        Assert.Equal(19.5 + k, points[1].X, 6);
        Assert.Equal(4.5, points[1].Y, 6);
        Assert.Equal(22, points[2].X, 6);
        Assert.Equal(7 - k, points[2].Y, 6);
    }

    [Fact]
    public void AFullCircleFromTwoHalfArcsStaysOnTheCircle()
    {
        // The text icon's lowercase "a": two half arcs of radius 3.5 around (18.5, 12.5).
        PathData path = IconPaths.Parse("M 15,12.5 A 3.5,3.5 0 1 0 22,12.5 A 3.5,3.5 0 1 0 15,12.5 Z");
        Assert.Equal(1 + 4 + 1, path.VerbCount);
        Rect bounds = path.TightBounds!.Value;
        Assert.Equal(15, bounds.X, 3);
        Assert.Equal(9, bounds.Y, 3);
        Assert.Equal(7, bounds.Width, 3);
        Assert.Equal(7, bounds.Height, 3);
    }

    [Fact]
    public void ArcsTooSmallForTheirEndsGrowToReachThem()
    {
        // SVG scales the radii up when the end point is out of reach (F.6.6).
        PathData path = IconPaths.Parse("M 0,0 A 1,1 0 0 1 10,0");
        Point end = path.Points[^1];
        Assert.Equal(new Point(10, 0), end);
        Rect bounds = path.TightBounds!.Value;
        Assert.Equal(-5, bounds.Y, 3);
    }

    [Fact]
    public void UnknownCommandsAreRejected()
    {
        Assert.Throws<FormatException>(() => IconPaths.Parse("M 0,0 H 4"));
    }
}
