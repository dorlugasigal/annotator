// Port of apps/macos/Tests/AnnotationCoreTests/ArrowHeadGeometryTests.swift.
namespace Annotator.Core.Tests;

public class ArrowHeadGeometryTests
{
    [Fact]
    public void CurvedArrowheadsStaySymmetricAndSelectableAtEveryAngle()
    {
        var center = new Point(300, 300);
        Point[] original = [new(80, 60), new(250, 200), new(270, 370)];
        for (var step = 0; step < 8; step++)
        {
            var angle = step * Math.PI / 4;
            foreach (var start in new[] { false, true })
            {
                var element = new Element(Tool.Arrow, points: original.Select(point => point.Rotated(center, angle)).ToValueList());
                element.Style.Curved = true;
                element.Style.Width = 6;
                element.Style.StartHead = start ? ArrowHead.Open : ArrowHead.None;
                element.Style.EndHead = start ? ArrowHead.None : ArrowHead.Open;
                var head = Assert.NotNull(ArrowHeadGeometry.Make(element, start));
                Assert.True(Math.Abs(head.Tip.Distance(head.Left) - head.Tip.Distance(head.Right)) < 0.0001);
                var midpoint = (head.Left + head.Right) * 0.5;
                Assert.True(midpoint.Distance(head.Base) < 0.0001);
                Assert.True(HitTesting.Contains(head.Left, element, tolerance: 0));
                Assert.True(HitTesting.Contains(head.Right, element, tolerance: 0));
                var chord = start ? element.Points[1] : element.Points[element.Points.Count - 2];
                Assert.True(head.Length <= head.Tip.Distance(chord) * 0.7 + 0.0001);
            }
        }
    }

    [Fact]
    public void ShortArrowheadsDoNotFoldPastTheirShaftAndSquareCapsRotate()
    {
        var arrow = new Element(Tool.Arrow, points: [new(20, 20), new(40, 40)]);
        arrow.Style.Width = 10;
        arrow.Style.EndHead = ArrowHead.Square;
        var head = Assert.NotNull(ArrowHeadGeometry.Make(arrow, atStart: false));
        Assert.True(head.Length <= new Point(20, 20).Distance(new Point(40, 40)) * 0.7);
        Assert.True(Math.Abs(head.Square[0].X - head.Tip.X) < 0.001);
        Assert.True(head.Square[0].Y > head.Tip.Y);
        Assert.True(head.Contains(head.Square[0], tolerance: 0.01));
    }

    [Fact]
    public void ATightCurveNeverTurnsItsHeadBackwards()
    {
        var arrow = new Element(Tool.Arrow, points: [new(0, 100), new(200, 100), new(210, 100)]);
        arrow.Style.Curved = true;
        var head = Assert.NotNull(ArrowHeadGeometry.Make(arrow, atStart: false));
        Assert.True(head.Direction.X > 0.99);
        Assert.True(Math.Abs(head.Direction.Y) < 0.01);
    }
}
