// Port of apps/macos/Tests/AnnotationCoreTests/LaserTrailTests.swift, with the outline LaserTrailRenderer draws.
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Core.Tests;

public class LaserTrailTests
{
    private static LaserTrail Trail((double X, double Y, double Time)[] points, double? release = null, double width = 6)
    {
        var trail = new LaserTrail();
        if (points.Length == 0)
        {
            return trail;
        }
        trail.Press(new Point(points[0].X, points[0].Y), points[0].Time, LaserTrail.DefaultColor, width);
        foreach (var (x, y, time) in points.Skip(1))
        {
            trail.Move(new Point(x, y), time);
        }
        if (release is { } end)
        {
            trail.Release(null, end);
        }
        return trail;
    }

    [Fact]
    public void KeepsTheDotUnderAHeldPointer()
    {
        LaserTrail held = Trail([(10, 10, 0)]);
        foreach (double now in new[] { 0.0, 0.5, 5 })
        {
            ValueList<LaserTrail.Shape> shapes = held.Shapes(now);
            Assert.Single(shapes);
            Assert.Equal(new[] { new LaserTrail.Dot(new Point(10, 10), 3) }, shapes[0].Dots.ToArray());
            Assert.Equal(1, shapes[0].Opacity);
        }
        Assert.True(held.IsResting);
    }

    [Fact]
    public void NarrowsTowardsItsTailAndFadesBehindThePointer()
    {
        LaserTrail moving = Trail([.. Enumerable.Range(0, 41).Select(index => (index * 5.0, 0.0, index * 0.01))]);
        LaserTrail.Dot[] dots = moving.Shapes(0.4)[0].Dots.ToArray();
        Assert.Equal(0, dots[0].Radius);
        Assert.Equal(new LaserTrail.Dot(new Point(200, 0), 3), dots[^1]);
        Assert.All(dots.Zip(dots.Skip(1)), pair => Assert.True(pair.First.Radius <= pair.Second.Radius + 1e-9));
        // 0.8 seconds after the pointer passed a point, that part is gone: the tail is where the pointer was at 0.2 s.
        LaserTrail.Dot[] later = moving.Shapes(1.0)[0].Dots.ToArray();
        Assert.True(Math.Abs(later[0].Center.X - 100) < 1e-9);
        Assert.Equal(new Point(200, 0), later[^1].Center);
    }

    [Fact]
    public void FadesOutAfterTheButtonGoesUp()
    {
        LaserTrail released = Trail([(0, 0, 0), (40, 0, 0.1)], release: 0.1);
        Assert.False(released.IsDrawing);
        Assert.True(Math.Abs(released.Shapes(0.5)[0].Opacity - 0.5) < 1e-9);
        released.Prune(0.95);
        Assert.True(released.IsEmpty);
        Assert.Empty(released.Shapes(0.95));
        Assert.Null(released.Bounds(0.95));
    }

    [Fact]
    public void RemembersARestSoAnOldSegmentStaysFaded()
    {
        LaserTrail rested = Trail([(0, 0, 0), (50, 0, 0.1)]);
        rested.Prune(2);
        Assert.Single(rested.Strokes[0].Samples);
        rested.Move(new Point(60, 0), 2);
        LaserTrail.Dot[] dots = rested.Shapes(2.05)[0].Dots.ToArray();
        Assert.All(dots, dot => Assert.True(dot.Center.X >= 50));
        Assert.Equal(new LaserTrail.Dot(new Point(60, 0), 3), dots[^1]);
    }

    [Fact]
    public void StartsANewStrokeForEachPress()
    {
        LaserTrail trail = Trail([(0, 0, 0), (30, 0, 0.05)], release: 0.05);
        trail.Press(new Point(100, 100), 0.2, new RGBA(0, 0, 1), 10);
        trail.Move(new Point(130, 100), 0.25);
        ValueList<LaserTrail.Shape> shapes = trail.Shapes(0.3);
        Assert.Equal(2, shapes.Count);
        Assert.Equal(LaserTrail.DefaultColor, shapes[0].Color);
        Assert.Equal(new RGBA(0, 0, 1), shapes[1].Color);
        Assert.Equal(5, shapes[1].Dots.ToArray()[^1].Radius);
        Assert.True(trail.IsDrawing);
    }

    [Fact]
    public void MovesTheNewestPointForTinySteps()
    {
        LaserTrail wobble = Trail([(0, 0, 0), (10, 0, 0.01), (10.3, 0.2, 0.02), (10.6, 0.1, 0.03)]);
        Assert.Equal(new[] { new Point(0, 0), new Point(10, 0), new Point(10.6, 0.1) },
            wobble.Strokes[0].Samples.Select(sample => sample.Point).ToArray());
    }

    [Fact]
    public void BoundsIncludeTheGlow()
    {
        LaserTrail held = Trail([(100, 100, 0)]);
        Rect bounds = Assert.NotNull(held.Bounds(0));
        double reach = 3 * 2.8 + 3;
        Assert.Equal(new Rect(100 - reach, 100 - reach, reach * 2, reach * 2), bounds);
    }

    [Fact]
    public void BoundsCoverTheTailPointThatHasNoWidth()
    {
        LaserTrail fresh = Trail([(0, 0, 0), (100, 0, 0.01)]);
        LaserTrail.Shape shape = fresh.Shapes(0.02)[0];
        Assert.Equal(0, shape.Dots[0].Radius);
        Rect bounds = Assert.NotNull(fresh.Bounds(0.02));
        // The band from the tail's point is drawn, so it is redrawn too.
        Assert.True(bounds.X <= 0 && bounds.Contains(new Point(0, 0)));
    }

    [Fact]
    public void BandsWindLikeTheirCircles()
    {
        LaserTrail.Pass pass = LaserTrail.Passes[4];
        foreach (var (start, end) in new[] { (new Point(0, 0), new Point(10, 0)), (new Point(10, 0), new Point(0, 0)), (new Point(0, 0), new Point(0, -10)) })
        {
            ValueList<Point> band = Assert.NotNull(LaserTrail.Band(new LaserTrail.Dot(start, 2), new LaserTrail.Dot(end, 3), pass));
            Assert.True(LaserTrail.SignedArea(band.AsSpan()) > 0);
        }
        Assert.Null(LaserTrail.Band(new LaserTrail.Dot(Point.Zero, 2), new LaserTrail.Dot(Point.Zero, 2), pass));
    }

    [Fact]
    public void LaserPointerNeverBecomesPartOfADrawing()
    {
        Assert.Equal("Laser pointer", Tool.Laser.Label);
        Assert.True(Tool.Laser.IsLaser);
        Assert.False(Tool.Laser.SharesStyle || Tool.Laser.StartsWithGradient || Tool.Laser.IsShape);
        Assert.Throws<CanvasError>(() => new Element(Tool.Laser, points: [new Point(0, 0), new Point(5, 5)]).Validate());
        Assert.Equal(LaserTrail.DefaultColor, Style.Defaults(Tool.Laser).Color);
        Assert.Equal(LaserTrail.DefaultWidth, Style.Defaults(Tool.Laser).Width);
        Assert.False(WorkspacePreferences.FirstUse.ToolStyles.ContainsKey(Tool.Laser.RawValue));
    }

    [Fact]
    public void LaserPointerIsOnTheDefaultToolbarWithTheLKey()
    {
        Assert.Contains(Tool.Laser, WorkspacePreferences.DefaultToolbar);
        var preferences = new WorkspacePreferences();
        LocalShortcut l = Assert.NotNull(LocalShortcut.Create("l"));
        Assert.Equal(ShortcutCommand.ForTool(Tool.Laser), preferences.CommandForPressed(l));
        Assert.Equal("L", preferences.ShortcutFor(ShortcutCommand.ForTool(Tool.Laser))?.Label);
        preferences.Validate();
    }

    /// <summary>
    /// LaserTrailRenderer: each layer is one outline, a closed circle per dot and a closed band to the next dot, all with a
    /// positive signed area (counterclockwise with y up), filled with the nonzero rule in the layer's color.
    /// </summary>
    [Fact]
    public void EachLayerIsOneOutlineWoundOneWay()
    {
        LaserTrail moving = Trail([(0, 0, 0), (40, 0, 0.05), (40, 30, 0.1)]);
        LaserTrail.Shape shape = moving.Shapes(0.1)[0];
        DisplayList? list = LaserTrailRenderer.List(moving, 0.1);
        Assert.NotNull(list);
        Assert.Equal(LaserTrail.Passes.Count, list.Items.Length);
        for (int index = 0; index < LaserTrail.Passes.Count; index++)
        {
            LaserTrail.Pass pass = LaserTrail.Passes[index];
            DisplayItem item = list.Items[index];
            Assert.Equal(DisplayOp.FillPath, item.Op);
            Assert.Equal(FillRule.NonZero, item.FillRule);
            RGBA color = pass.White ? new RGBA(1, 1, 1) : LaserTrail.DefaultColor;
            Assert.Equal(new RGBA(color.Red, color.Green, color.Blue, pass.Alpha), item.Paint!.Color);
            int circles = shape.Dots.Count(dot => pass.Radius(dot) > 0);
            int bands = shape.Dots.Zip(shape.Dots.Skip(1)).Count(pair => LaserTrail.Band(pair.First, pair.Second, pass) is not null);
            var subpaths = new List<List<Point>>();
            foreach (PathElement element in item.Path!)
            {
                if (element.Verb == PathVerb.MoveTo)
                {
                    subpaths.Add([element.Points[0]]);
                }
                else if (element.Verb != PathVerb.Close)
                {
                    subpaths[^1].AddRange(element.Points.ToArray());
                }
            }
            Assert.Equal(circles + bands, subpaths.Count);
            Assert.All(subpaths, points => Assert.True(LaserTrail.SignedArea(points.ToArray()) > 0));
        }
    }

    /// <summary>
    /// Windows only: a fast drag leaves a dot every pixel or two, so the renderer leaves out the dots its neighbours' line
    /// already draws. Ends, corners, empty dots and the dots next to them stay.
    /// </summary>
    [Fact]
    public void DotsOnTheLineBetweenTheirNeighboursAreLeftOut()
    {
        var straight = new LaserTrail.Dot[41];
        for (int index = 0; index < straight.Length; index++)
        {
            straight[index] = new LaserTrail.Dot(new Point(index * 2, 10), 1 + index * 0.05);
        }
        straight[0] = straight[0] with { Radius = 0 };
        LaserTrail.Dot[] kept = LaserTrailRenderer.Simplify(straight);
        Assert.Equal(new[] { straight[0], straight[1], straight[40] }, kept);

        LaserTrail.Dot[] corner = [.. straight[1..21], .. Enumerable.Range(1, 20).Select(step => new LaserTrail.Dot(new Point(40, 10 + step * 2), 2))];
        Assert.Equal(new[] { corner[0], corner[19], corner[39] }, LaserTrailRenderer.Simplify(corner));

        LaserTrail.Dot[] widening = [new(new Point(0, 0), 2), new(new Point(10, 0), 6), new(new Point(20, 0), 2)];
        Assert.Equal(widening, LaserTrailRenderer.Simplify(widening));

        LaserTrail.Dot[] gap = [new(new Point(0, 0), 2), new(new Point(10, 0), 2), new(new Point(20, 0), 0), new(new Point(30, 0), 2), new(new Point(40, 0), 2), new(new Point(50, 0), 2)];
        Assert.Equal(new[] { gap[0], gap[1], gap[2], gap[3], gap[5] }, LaserTrailRenderer.Simplify(gap));
    }

    [Fact]
    public void ATrailThatFadedDrawsNothing()
    {
        LaserTrail released = Trail([(0, 0, 0), (40, 0, 0.1)], release: 0.1);
        Assert.Null(LaserTrailRenderer.List(released, 0.95));
        Assert.Null(LaserTrailRenderer.List(new LaserTrail(), 0));
    }

    [Fact]
    public void KeepsItsPointsForTheChosenFade()
    {
        (double, double, double)[] line = [.. Enumerable.Range(0, 41).Select(index => (index * 5.0, 0.0, index * 0.01))];
        LaserTrail slow = Trail(line, release: 0.4);
        slow.Lifetime = 2.5;
        // 1.9 seconds after the pointer passed the start, it still shows; with the default Fade it was gone after 0.8.
        Assert.Equal(new Point(0, 0), slow.Shapes(1.9)[0].Dots[0].Center);
        Assert.True(Trail(line, release: 0.4).Shapes(1.9).IsEmpty);
        // Released at 0.4, it fades out over the next 2.5 seconds.
        Assert.Equal(0.5, slow.Shapes(1.65)[0].Opacity, 9);
        slow.Prune(2.95);
        Assert.True(slow.IsEmpty);
        Assert.Equal(LaserTrail.DefaultLifetime, new LaserTrail().Lifetime);
        Assert.InRange(LaserTrail.DefaultLifetime, LaserTrail.MinimumLifetime, LaserTrail.MaximumLifetime);
    }

    [Fact]
    public void LaserFadeIsSavedWithinItsRange()
    {
        var preferences = new WorkspacePreferences();
        Assert.Null(preferences.LaserFade);
        preferences.LaserFade = 2.5;
        Assert.Equal(preferences, WorkspacePreferences.Read(preferences.Data()));
        foreach (double fade in new[] { 0.4, 5.1, double.PositiveInfinity, double.NaN })
        {
            preferences.LaserFade = fade;
            Assert.Throws<CanvasError>(() => preferences.Data());
        }
        // A Fade from a version with a wider choice keeps the nearest one this version offers.
        string json = System.Text.Encoding.UTF8.GetString(new WorkspacePreferences().Data());
        Assert.Equal(5, WorkspacePreferences.Read(System.Text.Encoding.UTF8.GetBytes(json.Insert(1, "\"laserFade\":12,"))).LaserFade);
        Assert.Equal(0.5, WorkspacePreferences.Read(System.Text.Encoding.UTF8.GetBytes(json.Insert(1, "\"laserFade\":0.1,"))).LaserFade);
    }
}
