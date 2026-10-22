// Port of apps/macos/Tests/RenderingTests/AxisTickGeometryTests.swift, SystemShapeAndLabelTests.swift (geometry parts) and the
// diamond assertions of EditableStyleRenderingTests.swift.
using Annotator.Core.Geometry;

namespace Annotator.Core.Tests;

public class DiagramGeometryTests
{
    [Fact]
    public void ExplicitAxisTicksAreEvenlySpacedFromTheOrigin()
    {
        var frame = new Rect(10, 20, 100, 200);
        var style = new Style { AxisTickCount = 3 };
        var positive = AxisTickGeometry.Positions(frame, style);
        Assert.Equal([44.0, 66, 88], positive.Horizontal.ToArray());
        Assert.Equal([64.0, 108, 152], positive.Vertical.ToArray());
        style.NegativeAxes = true;
        var centered = AxisTickGeometry.Positions(frame, style);
        Assert.Equal([22.5, 35, 47.5, 72.5, 85, 97.5], centered.Horizontal.ToArray());
        Assert.Equal([45.0, 70, 95, 145, 170, 195], centered.Vertical.ToArray());
        Assert.False(centered.Horizontal.Contains(frame.Center.X));
        Assert.False(centered.Vertical.Contains(frame.Center.Y));
    }

    [Fact]
    public void AxisTicksCanBeHiddenAndLegacySpacingIsPreserved()
    {
        var frame = new Rect(10, 20, 100, 200);
        var style = new Style();
        var legacy = AxisTickGeometry.Positions(frame, style);
        Assert.Equal([20.0, 30, 40, 50, 60, 70, 80, 90, 100], legacy.Horizontal.ToArray());
        Assert.Equal([40.0, 60, 80, 100, 120, 140, 160, 180, 200], legacy.Vertical.ToArray());
        style.AxisTickCount = 0;
        var hidden = AxisTickGeometry.Positions(frame, style);
        Assert.True(hidden.Horizontal.IsEmpty);
        Assert.True(hidden.Vertical.IsEmpty);
        style.AxisTickCount = 24;
        style.NegativeAxes = true;
        var maximum = AxisTickGeometry.Positions(Rect.Zero, style);
        Assert.Equal(48, maximum.Horizontal.Count);
        Assert.Equal(48, maximum.Vertical.Count);
        Assert.All(maximum.Horizontal, value => Assert.True(double.IsFinite(value)));
        Assert.All(maximum.Vertical, value => Assert.True(double.IsFinite(value)));
        style.AxisTickCount = 25;
        Assert.Throws<ArgumentOutOfRangeException>(() => AxisTickGeometry.Positions(frame, style));
    }

    [Fact]
    public void RenderedAxisPathUsesTheConfiguredTicks()
    {
        var element = new Element(Tool.Axes, new Rect(10, 20, 100, 200));
        element.Style.AxisTickCount = 0;
        var bare = DiagramGeometry.Path(element).VerbCount;
        element.Style.AxisTickCount = 3;
        Assert.Equal(bare + 12, DiagramGeometry.Path(element).VerbCount);
        element.Style.NegativeAxes = true;
        Assert.Equal(bare + 24, DiagramGeometry.Path(element).VerbCount);
    }

    [Fact]
    public void DiagramShapesKeepTheirMacConstruction()
    {
        var database = DiagramGeometry.Path(new Element(Tool.Database, new Rect(0, 0, 100, 100)));
        Assert.Equal(
            [
                PathVerb.MoveTo, PathVerb.LineTo, PathVerb.CubicTo, PathVerb.LineTo, PathVerb.CubicTo, PathVerb.Close,
                PathVerb.MoveTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.CubicTo, PathVerb.Close,
            ],
            database.Verbs.ToArray());
        // The bottom bulge reaches below the frame by cap / 8, so fills are clipped to the frame.
        Assert.Equal(103, Assert.NotNull(database.TightBounds).MaxY, 9);
        var grid = new Element(Tool.Grid, new Rect(0, 0, 90, 60));
        grid.Style.GridColumns = 3;
        grid.Style.GridRows = 2;
        var gridPath = DiagramGeometry.Path(grid);
        Assert.Equal(5 + 2 * 2 + 1 * 2, gridPath.VerbCount);
        Assert.Equal(new Point(30, 0), gridPath.Points[4]);
        Assert.Equal(new Point(0, 30), gridPath.Points[8]);
        var queue = DiagramGeometry.Path(new Element(Tool.Queue, new Rect(0, 0, 100, 100)));
        Assert.Equal(3, queue.Verbs.ToArray().Count(verb => verb == PathVerb.Close));
        var person = DiagramGeometry.Path(new Element(Tool.Person, new Rect(0, 0, 100, 100)));
        Assert.Equal(new Point(69, 17), person.Points[0]);
        Assert.True(DiagramGeometry.Path(new Element(Tool.Rectangle, new Rect(0, 0, 1, 1))).IsEmpty);
    }

    [Fact]
    public void DiamondRoundingUsesFourQuadraticCornersInsideItsFrame()
    {
        var frame = new Rect(30, 30, 120, 120);
        var sharp = DiamondGeometry.Path(frame, 0);
        var rounded = DiamondGeometry.Path(frame, 24);
        Assert.Equal(4, rounded.Verbs.ToArray().Count(verb => verb == PathVerb.QuadTo));
        Assert.True(sharp.Contains(new Point(90, 32)));
        Assert.False(rounded.Contains(new Point(90, 32)));
        Assert.True(rounded.Contains(frame.Center));
        Assert.True(RectMath.Contains(frame, Assert.NotNull(rounded.TightBounds)));
    }

    [Fact]
    public void DiamondRoundingIsBoundedForThinAndDegenerateFrames()
    {
        foreach (var frame in new[]
        {
            new Rect(20, 20, 200, 2), new Rect(20, 20, 2, 200), new Rect(20, 20, 0, 100),
            new Rect(20, 20, 100, 0), new Rect(20, 20, 0, 0),
        })
        {
            foreach (var point in DiamondGeometry.Path(frame, 1_000).Points)
            {
                Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y));
                Assert.True(point.X >= frame.X && point.X <= frame.MaxX);
                Assert.True(point.Y >= frame.Y && point.Y <= frame.MaxY);
            }
        }
    }

    [Fact]
    public void SystemShapesHaveDistinctGeometryInTheirAspectRatio()
    {
        var paths = new List<PathData>();
        foreach (var tool in Tool.SystemDesignShapes)
        {
            var shape = new Element(tool, new Rect(20, 20, 160, 160));
            shape.Style.Color = Palette.Ink;
            var path = SystemDiagramGeometry.Path(shape);
            Assert.False(path.IsEmpty, tool.ToString());
            var ratio = Assert.NotNull(tool.SystemShapeAspectRatio);
            var bounds = Assert.NotNull(path.TightBounds);
            Assert.True(Math.Abs(bounds.Width / bounds.Height - ratio) < 0.001, tool.ToString());
            Assert.Equal(shape.SymbolFrame.X, shape.AnchorPoint(new Point(0, 0.5)).X);
            Assert.True(RectMath.Contains(RectMath.Outset(shape.SymbolFrame, 1e-4, 1e-4), bounds), tool.ToString());
            Assert.DoesNotContain(paths, other => other.ContentEquals(path));
            paths.Add(path);
            shape.Style.Roughness = 2;
            shape.Style.Fill = new RGBA(0.4, 0.6, 0.8, 0.5);
            var reread = DrawingFile.Read(new DrawingFile(new CanvasDocument([shape])).Data());
            Assert.Equal(shape, reread.Elements[0]);
        }
        Assert.True(SystemDiagramGeometry.Path(new Element(Tool.Database, new Rect(0, 0, 10, 10))).IsEmpty);
        Assert.Equal(Tool.SystemDesignShapes.Count, paths.Count);
    }
}
