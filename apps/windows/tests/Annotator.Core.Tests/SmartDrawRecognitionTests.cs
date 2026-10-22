// Port of apps/macos/Tests/AnnotationCoreTests/SmartDrawRecognitionTests.swift.
namespace Annotator.Core.Tests;

public class SmartDrawRecognitionTests
{
    private static ValueList<Point> Traced(Point[] vertices, double noise = 0, Point gap = default)
    {
        var output = new List<Point>();
        for (var index = 1; index < vertices.Length; index++)
        {
            var steps = index % 2 == 0 ? 53 : 13;
            for (var step = 0; step < steps; step++)
            {
                var t = (double)step / steps;
                var point = vertices[index - 1] + (vertices[index] - vertices[index - 1]) * t;
                double wave = output.Count;
                output.Add(point + new Point(Math.Sin(wave * 0.53), Math.Cos(wave * 0.47)) * noise);
            }
        }
        if (vertices.Length > 0)
        {
            output.Add(vertices[^1] + gap);
        }
        return output.ToValueList();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    public void SmartDrawRecognizesUnevenWobblyRectanglesAndDiamonds(double noise)
    {
        (Tool Tool, Point[] Vertices)[] cases =
        [
            (Tool.Rectangle, [new(40, 50), new(260, 55), new(255, 210), new(35, 205), new(40, 50)]),
            (Tool.Diamond, [new(160, 40), new(275, 130), new(150, 220), new(40, 135), new(160, 40)]),
        ];
        foreach (var (tool, vertices) in cases)
        {
            var stroke = new Element(Tool.Pen, points: Traced(vertices, noise, new Point(14, -9)));
            var result = Assert.NotNull(ShapeRecognition.Recognize(stroke));
            Assert.Equal(tool, result.Tool);
            Assert.Equal(stroke.Id, result.Id);
            result.Validate();
        }
    }

    [Fact]
    public void SmartDrawRecognizesLooseIncompleteEllipseAndTiltedRectangle()
    {
        var ellipse = Enumerable.Range(0, 111).Select(index =>
        {
            var t = index / 120.0 * 2 * Math.PI;
            return new Point(150 + 95 * Math.Cos(t) + 7 * Math.Sin(3 * t), 150 + 65 * Math.Sin(t) + 5 * Math.Cos(4 * t));
        }).ToValueList();
        Assert.Equal(Tool.Ellipse, ShapeRecognition.Recognize(new Element(Tool.Pen, points: ellipse))?.Tool);
        Point[] box = [new(50, 50), new(280, 50), new(280, 190), new(50, 190), new(50, 50)];
        var tilted = Traced(box, noise: 4).Map(point => point.Rotated(new Point(165, 120), 0.22));
        var result = Assert.NotNull(ShapeRecognition.Recognize(new Element(Tool.Pen, points: tilted)));
        Assert.Equal(Tool.Rectangle, result.Tool);
        Assert.True(Math.Abs(result.Rotation - 0.22) < 0.12);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(Math.PI / 2)]
    [InlineData(Math.PI)]
    [InlineData(-Math.PI / 4)]
    public void SmartDrawRecognizesArrowsInBothStrokeDirections(double angle)
    {
        Point[] vertices = [new(0, 0), new(200, 0), new(160, -32), new(200, 0), new(163, 28)];
        var points = Traced(vertices, noise: 2).Map(point => point.Rotated(Point.Zero, angle) + new Point(300, 300));
        var reversed = points.ToArray();
        Array.Reverse(reversed);
        foreach (var input in new[] { points, ValueList.Create<Point>(reversed) })
        {
            var stroke = new Element(Tool.Pen, points: input);
            stroke.GroupId = Guid.NewGuid();
            stroke.Locked = true;
            stroke.Style.Color = Palette.Blue;
            stroke.Style.Width = 5;
            var arrow = Assert.NotNull(ShapeRecognition.Recognize(stroke));
            Assert.Equal(Tool.Arrow, arrow.Tool);
            Assert.Equal(2, arrow.Points.Count);
            Assert.True(arrow.Points[0].Distance(new Point(300, 300)) < 6);
            Assert.True(arrow.Points[1].Distance(new Point(200, 0).Rotated(Point.Zero, angle) + new Point(300, 300)) < 6);
            Assert.Equal(ArrowHead.Open, arrow.Style.EndHead);
            Assert.Equal(stroke.Style.Color, arrow.Style.Color);
            Assert.Equal(stroke.Style.Width, arrow.Style.Width);
            Assert.True(arrow.Id == stroke.Id && arrow.GroupId == stroke.GroupId && arrow.Seed == stroke.Seed && arrow.Locked);
            arrow.Validate();
        }
    }

    [Fact]
    public void SmartDrawRejectsOpenMarksScribblesAndRepeatedLoops()
    {
        ValueList<Point>[] marks =
        [
            Traced([new(0, 0), new(200, 0)], noise: 2),
            Traced([new(0, 0), new(200, 0), new(200, 120)]),
            Traced([new(0, 0), new(200, 0), new(200, -40), new(200, 40)]),
            Traced([new(0, 0), new(50, 80), new(200, -40)]),
            Traced([new(100, 20), new(200, 200), new(0, 200), new(100, 20)]),
            Enumerable.Range(0, 161).Select(i => new Point(150 + 80 * Math.Sin(i / 160.0 * 2 * Math.PI), 150 + 60 * Math.Sin(i / 160.0 * 4 * Math.PI))).ToValueList(),
            Enumerable.Range(0, 161).Select(i => new Point(150 + 80 * Math.Cos(i / 160.0 * 4 * Math.PI), 150 + 60 * Math.Sin(i / 160.0 * 4 * Math.PI))).ToValueList(),
        ];
        foreach (var points in marks)
        {
            Assert.Null(ShapeRecognition.Recognize(new Element(Tool.Pen, points: points)));
        }
    }

    [Theory]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(53)]
    [InlineData(97)]
    public void SmartDrawRecognizesEvenlySampledRetracedArrow(int steps)
    {
        Point[] vertices = [new(80, 170), new(330, 170), new(280, 130), new(330, 170), new(280, 210)];
        var points = new List<Point> { vertices[0] };
        for (var index = 1; index < vertices.Length; index++)
        {
            for (var step = 1; step <= steps; step++)
            {
                points.Add(vertices[index - 1] + (vertices[index] - vertices[index - 1]) * ((double)step / steps));
            }
        }
        var reversed = points.ToArray();
        Array.Reverse(reversed);
        foreach (var input in new[] { points.ToValueList(), ValueList.Create<Point>(reversed) })
        {
            var arrow = Assert.NotNull(ShapeRecognition.Recognize(new Element(Tool.Pen, points: input)));
            Assert.Equal(Tool.Arrow, arrow.Tool);
            Assert.True(arrow.Points[0].Distance(vertices[0]) < 6);
            Assert.True(arrow.Points[1].Distance(vertices[1]) < 6);
        }
    }

    [Fact]
    public void RecognizedDiamondAndArrowShareOneUndoStepAndPreviewClassification()
    {
        ValueList<Point>[] examples =
        [
            Traced([new(100, 0), new(200, 100), new(100, 200), new(0, 100), new(100, 0)], noise: 3),
            Traced([new(0, 0), new(200, 0), new(160, -32), new(200, 0), new(160, 32)], noise: 2),
        ];
        foreach (var points in examples)
        {
            var stroke = new Element(Tool.Pen, points: points);
            var recognized = Assert.NotNull(ShapeRecognition.Recognize(stroke));
            Assert.Equal(recognized.Tool, ShapeRecognition.Recognize(ShapeRecognition.PreviewInput(stroke))?.Tool);
            var document = CanvasDocument.Empty();
            document.Add(stroke);
            var generation = document.Generation;
            Assert.True(document.ApplyRecognition(recognized, generation, stroke.GeometryToken));
            document.Undo();
            Assert.Empty(document.Elements);
            document.Redo();
            Assert.Single(document.Elements);
            Assert.Equal(recognized.Tool, document.Elements[0].Tool);
        }
    }
}
