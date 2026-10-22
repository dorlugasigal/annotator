// Port of apps/macos/Tests/RenderingTests/RoughReferenceTests.swift and RoughEllipseClosureTests.swift.
using System.Text.Json;
using Annotator.Core.Geometry;

namespace Annotator.Core.Tests;

public class RoughGeometryTests
{
    [Fact]
    public void RoughShapesMatchUpstreamSeededCoordinates()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "roughjs-4.6.6.json");
        using var fixtures = JsonDocument.Parse(File.ReadAllBytes(file));
        Assert.Equal(4, fixtures.RootElement.GetArrayLength());
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var style = new Style { Roughness = fixture.GetProperty("roughness").GetDouble(), Width = 3 };
            var tool = fixture.GetProperty("shape").GetString() == "rectangle" ? Tool.Rectangle : Tool.Ellipse;
            var element = new Element(tool, new Rect(20, 30, 300, 180), style: style) { Seed = 42 };
            var path = SceneTestSupport.Builder().Path(element);
            var operations = new List<(string Op, double[] Data)>();
            foreach (var item in path)
            {
                var points = item.Points;
                switch (item.Verb)
                {
                    case PathVerb.MoveTo:
                        operations.Add(("move", [points[0].X, points[0].Y]));
                        break;
                    case PathVerb.CubicTo:
                        operations.Add(("bcurveTo", [points[0].X, points[0].Y, points[1].X, points[1].Y, points[2].X, points[2].Y]));
                        break;
                    default:
                        Assert.Fail("Unexpected rough geometry operation");
                        break;
                }
            }
            var expected = fixture.GetProperty("operations");
            Assert.Equal(expected.GetArrayLength(), operations.Count);
            var index = 0;
            foreach (var operation in expected.EnumerateArray())
            {
                Assert.Equal(operation.GetProperty("op").GetString(), operations[index].Op);
                var data = operation.GetProperty("data");
                Assert.Equal(data.GetArrayLength(), operations[index].Data.Length);
                var position = 0;
                foreach (var value in data.EnumerateArray())
                {
                    Assert.True(Math.Abs(operations[index].Data[position++] - value.GetDouble()) < 0.000_001,
                                $"{tool} roughness {style.Roughness}: operation {index} differs.");
                }
                index++;
            }
        }
    }

    [Fact]
    public void SeededRandomWrapsLikeRoughJs()
    {
        var random = new RoughGeometry.Random(42);
        Assert.Equal(42u, random.State);
        var first = random.Next();
        Assert.Equal(unchecked(42u * 48_271u), random.State);
        Assert.Equal((unchecked(42u * 48_271u) & 0x7fff_ffffu) / 2_147_483_648.0, first);
        Assert.Equal(1u, new RoughGeometry.Random(0x1_0000_0000UL).State);
        var large = new RoughGeometry.Random(uint.MaxValue);
        large.Next();
        Assert.Equal(unchecked(uint.MaxValue * 48_271u), large.State);
    }

    [Fact]
    public void RoughEllipseClosureStaysOnPerimeterAcrossSeedsAndSizes()
    {
        var builder = SceneTestSupport.Builder();
        foreach (var size in new[] { new Point(600, 550), new Point(300, 180), new Point(1_200, 1_000) })
        {
            foreach (var roughness in new[] { 0.0, 2, 6 })
            {
                for (var seed = 1UL; seed <= 256; seed++)
                {
                    var element = new Element(Tool.Ellipse, new Rect(30, 40, size.X, size.Y)) { Seed = seed };
                    element.Style.Roughness = roughness;
                    var samples = new EllipseOutlineSamples(builder.Path(element));
                    Assert.True(samples.StaysNearPerimeter(element.Frame),
                                $"Ellipse {size}, roughness {roughness}, seed {seed} crosses its interior or leaves its perimeter");
                    Assert.Equal(roughness == 0 ? 1 : 2, samples.Moves);
                    Assert.True(samples.Cubics >= 4);
                }
            }
        }
    }

    [Fact]
    public void ResizedAndUndoneRoughEllipsesKeepLocalClosingCurves()
    {
        var builder = SceneTestSupport.Builder();
        foreach (var roughness in new[] { 0.0, 2, 6 })
        {
            foreach (var seed in new ulong[] { 1, 2, 42, 93, 116, 255 })
            {
                var element = new Element(Tool.Ellipse, new Rect(30, 40, 300, 180)) { Seed = seed };
                element.Style.Roughness = roughness;
                var document = new CanvasDocument([element]);
                var original = new EllipseOutlineSamples(builder.Path(element));
                document.Update(element.Id, (ref Element changed) => changed.Resize(new Rect(60, 80, 600, 550)));
                var resized = document.Elements[0];
                var resizedSamples = new EllipseOutlineSamples(builder.Path(resized));
                Assert.True(resizedSamples.StaysNearPerimeter(resized.Frame));
                var builds = builder.FullPathBuilds;
                _ = builder.Path(resized);
                Assert.Equal(builds, builder.FullPathBuilds);
                document.Undo();
                var restored = document.Elements[0];
                var restoredSamples = new EllipseOutlineSamples(builder.Path(restored));
                Assert.Equal(original.Points, restoredSamples.Points);
                Assert.True(restoredSamples.StaysNearPerimeter(restored.Frame));
                document.Redo();
                var redone = document.Elements[0];
                Assert.Equal(resizedSamples.Points, new EllipseOutlineSamples(builder.Path(redone)).Points);
            }
        }
    }

    /// <summary>The geometric form of largeRoughEllipseExportsWithoutInteriorInk: no stroke reaches the central region.</summary>
    [Fact]
    public void LargeRoughEllipseLeavesTheInteriorUnpainted()
    {
        foreach (var roughness in new[] { 0.0, 2, 6 })
        {
            var element = new Element(Tool.Ellipse, new Rect(30, 30, 600, 550)) { Seed = 116 };
            element.Style.Roughness = roughness;
            element.Style.Width = 3;
            var interior = RectMath.Outset(new Rect(210, 200, 240, 200), element.Style.Width / 2 + 1, element.Style.Width / 2 + 1);
            foreach (var point in SceneTestSupport.Samples(SceneTestSupport.Builder().Path(element)))
            {
                Assert.False(interior.Contains(point), $"Unfilled ellipse roughness {roughness} paints inside its central region");
            }
        }
    }

    private sealed class EllipseOutlineSamples
    {
        public EllipseOutlineSamples(PathData path)
        {
            Points = SceneTestSupport.Samples(path);
            foreach (var verb in path.Verbs)
            {
                switch (verb)
                {
                    case PathVerb.MoveTo:
                        Moves++;
                        break;
                    case PathVerb.CubicTo:
                        Cubics++;
                        break;
                    case PathVerb.QuadTo:
                        Assert.Fail("Unexpected ellipse path operation");
                        break;
                }
            }
        }

        public List<Point> Points { get; }

        public int Moves { get; }

        public int Cubics { get; }

        public bool StaysNearPerimeter(Rect frame)
        {
            if (Points.Count == 0)
            {
                return false;
            }
            foreach (var point in Points)
            {
                var radius = double.Hypot((point.X - frame.Center.X) / (frame.Width / 2), (point.Y - frame.Center.Y) / (frame.Height / 2));
                if (!double.IsFinite(radius) || radius < 0.85 || radius > 1.15)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
