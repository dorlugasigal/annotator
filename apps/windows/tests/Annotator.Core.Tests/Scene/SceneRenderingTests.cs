// Geometric ports of apps/macos/Tests/RenderingTests/RenderingTests.swift, EraserFadeTests.swift, ArrowHeadSizeTests.swift,
// ExpandedArrowheadTests.swift, EditableStyleRenderingTests.swift and GradientRenderingTests.swift. Pixel probes become
// checks of what the display list paints at the same points.
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using static Annotator.Core.Tests.SceneTestSupport;

namespace Annotator.Core.Tests;

public class SceneRenderingTests
{
    [Fact]
    public void MultipleSelectionDrawsSharedHandlesInBothRenderPaths()
    {
        var first = new Element(Tool.Rectangle, new Rect(30, 70, 50, 40));
        var second = new Element(Tool.Ellipse, new Rect(170, 110, 50, 40));
        HashSet<Guid> ids = [first.Id, second.Id];
        foreach (var overlayOnly in new[] { false, true })
        {
            var builder = Builder();
            var selection = overlayOnly ? builder.Selection([first, second], ids) : builder.Build([first, second], ids).Selection;
            // These handles are on the shared bounds, away from either object's handles.
            foreach (var point in new[] { new Point(220, 70), new Point(30, 150), new Point(125, 46) })
            {
                Assert.Contains(selection.Items.ToArray(), item => Paints(item, point));
            }
        }
    }

    [Fact]
    public void SelectedArrowShowsPointHandlesWithoutABoundingBox()
    {
        var arrow = new Element(Tool.Arrow, points: [new Point(30, 40), new Point(130, 150), new Point(230, 60)]);
        var locked = arrow;
        locked.Locked = true;
        foreach (var (element, expectsBox) in new[] { (arrow, false), (locked, true) })
        {
            var items = Builder().Selection([element], new HashSet<Guid> { element.Id }).Items.ToArray();
            // The box would run along y = 37 and x = 233; selection does not draw the arrow itself.
            Assert.Equal(expectsBox, items.Any(item => Paints(item, new Point(100.5, 37.5))));
            Assert.Equal(expectsBox, items.Any(item => Paints(item, new Point(233.5, 120.5))));
            Assert.Equal(!element.Locked, items.Any(item => Paints(item, new Point(130.5, 150.5))));
        }
    }

    [Fact]
    public void CoversFlattenToOpaqueBlackWhateverTheirStyle()
    {
        var style = new Style { Opacity = 0, Color = new RGBA(1, 0, 0, 0) };
        style.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        var cover = new Element(Tool.Cover, new Rect(0.5, 0.25, 9.25, 10), style: style);
        var item = Assert.Single(Builder().Build([cover]).Elements[0].Items.ToArray());
        Assert.Equal(DisplayOp.FillRect, item.Op);
        Assert.Equal(new Rect(0, 0, 10, 11), item.Rect);
        Assert.Equal(RectSnap.OutwardToPixels, item.Snap);
        Assert.Same(Paint.Black, item.Paint);
        Assert.Equal(1, item.Paint!.EffectiveAlpha);
    }

    [Fact]
    public void SeededShapesAreStable()
    {
        var element = new Element(Tool.Rectangle, new Rect(20, 20, 100, 80));
        element.Style.Roughness = 4;
        AssertSameScenes(Builder().Build([element]), Builder().Build([element]));
    }

    [Fact]
    public void FadingPaintsOnlyTheObjectNotItsBoundingBox()
    {
        var rectangle = new Element(Tool.Rectangle, new Rect(30, 30, 60, 60));
        rectangle.Style.Width = 4;
        var list = Builder().Build([rectangle], fadedIds: new HashSet<Guid> { rectangle.Id }).Elements[0];
        var items = list.Items.ToArray();
        Assert.Equal(DisplayOp.PushLayer, items[0].Op);
        Assert.Equal(0.22, items[0].Opacity);
        Assert.True(RectMath.Contains(Assert.NotNull(items[0].LayerBounds), RectMath.Outset(rectangle.Frame, 70, 70)));
        Assert.Equal(DisplayOp.PopLayer, items[^1].Op);
        var stroke = Assert.Single(items, item => item.Op == DisplayOp.StrokePath);
        Assert.InRange(255 * 0.22 * stroke.Paint!.EffectiveAlpha, 40, 70);
        Assert.True(Paints(stroke, new Point(30, 60)));
        Assert.DoesNotContain(items, item => Paints(item, new Point(60, 60)));
        var censor = new Element(Tool.Cover, new Rect(30, 30, 60, 60));
        var cover = Assert.Single(Builder().Build([censor], fadedIds: new HashSet<Guid> { censor.Id }).Elements[0].Items.ToArray());
        Assert.Equal(DisplayOp.FillRect, cover.Op);
        Assert.Equal(1, cover.Paint!.EffectiveAlpha);
    }

    [Fact]
    public void EveryArrowheadUsesTheDoubledFootprintAtBothEnds()
    {
        foreach (var start in new[] { false, true })
        {
            foreach (var head in new[] { ArrowHead.Open, ArrowHead.Filled, ArrowHead.Circle, ArrowHead.Square, ArrowHead.Bar })
            {
                var style = new Style { Width = 3, StartHead = start ? head : ArrowHead.None, EndHead = start ? ArrowHead.None : head };
                var arrow = new Element(Tool.Arrow, points: [new Point(60, 60), new Point(220, 60)], style: style);
                var tip = start ? 60 : 220;
                var (x, y) = head switch
                {
                    ArrowHead.Open => (start ? 82 : 198, 73),
                    ArrowHead.Filled => (start ? 82 : 198, 68),
                    ArrowHead.Circle or ArrowHead.Square => (tip, 67),
                    _ => (tip, 69),
                };
                var items = Builder().Build([arrow]).Elements[0].Items.ToArray();
                Assert.True(items.Any(item => Paints(item, new Point(x + 0.5, y + 0.5))),
                            $"The expanded {head.RawValue} head must paint beyond its previous footprint.");
            }
        }
    }

    [Fact]
    public void AllExpandedHeadsRenderAtBothEndsAndRoundTrip()
    {
        Assert.Equal(16, ArrowHead.ShapeChoices.Count + ArrowHead.RelationshipChoices.Count);
        foreach (var kind in ArrowHead.AllCases)
        {
            if (kind == ArrowHead.None)
            {
                continue;
            }
            foreach (var start in new[] { true, false })
            {
                var style = new Style { Width = 3, StartHead = start ? kind : ArrowHead.None, EndHead = start ? ArrowHead.None : kind };
                var element = new Element(Tool.Arrow, points: [new Point(70, 80), new Point(250, 80)], style: style);
                var head = Assert.NotNull(ArrowHeadGeometry.Make(element, start));
                var probe = new PaintProbe(Builder().Build([element]).Elements[0].Items.ToArray());
                var tipX = start ? 70 : 250;
                var painted = 0;
                for (var x = tipX - 38; x <= tipX + 38; x++)
                {
                    for (var y = 50; y <= 110; y++)
                    {
                        if (Math.Abs(y - 80) > 3 && probe.Paints(new Point(x + 0.5, y + 0.5), 0))
                        {
                            painted++;
                        }
                    }
                }
                Assert.True(painted > 4, $"{kind} needs a visible head, not only a shaft.");
                Point point;
                if (kind is ArrowHead.Circle or ArrowHead.CircleOutline)
                {
                    point = head.Tip + head.Side * (head.Length / 3);
                }
                else if (!head.RelationshipSegments.IsEmpty)
                {
                    point = head.RelationshipSegments[0].Start;
                }
                else if (kind == ArrowHead.Square)
                {
                    point = head.Square[0];
                }
                else if (kind is ArrowHead.Diamond or ArrowHead.DiamondOutline)
                {
                    point = head.Polygon[1];
                }
                else
                {
                    point = head.Left;
                }
                Assert.True(head.Contains(point, tolerance: 0.2));
                var data = new DrawingFile(new CanvasDocument([element])).Data();
                Assert.Equal(style, DrawingFile.Read(data).Elements[0].Style);
            }
        }
    }

    [Fact]
    public void OutlinedHeadsDoNotFillOrLetTheShaftShowThroughTheirInterior()
    {
        foreach (var kind in new[] { ArrowHead.TriangleOutline, ArrowHead.CircleOutline, ArrowHead.DiamondOutline })
        {
            var style = new Style { Width = 2, EndHead = kind };
            var element = new Element(Tool.Arrow, points: [new Point(50, 60), new Point(210, 60)], style: style);
            var head = Assert.NotNull(ArrowHeadGeometry.Make(element, atStart: false));
            var interior = kind == ArrowHead.CircleOutline ? head.Tip : head.Tip - head.Direction * (head.Length * 0.5);
            var items = Builder().Build([element]).Elements[0].Items.ToArray();
            var shaft = Array.FindIndex(items, item => item.Op == DisplayOp.StrokePath);
            var mask = items[shaft - 1];
            Assert.Equal(DisplayOp.PushLayer, mask.Op);
            Assert.Equal(FillRule.EvenOdd, mask.FillRule);
            Assert.False(mask.Path!.Contains(interior, FillRule.EvenOdd));
            Assert.True(mask.Path.Contains(new Point(100, 60), FillRule.EvenOdd));
            Assert.Equal(DisplayOp.PopLayer, items[shaft + 1].Op);
            foreach (var item in items[(shaft + 2)..])
            {
                Assert.Equal(DisplayOp.StrokePath, item.Op);
                Assert.False(Paints(item, interior), $"{kind} paints its interior.");
                Assert.False(item.Stroke!.IsDashed);
            }
        }
    }

    [Fact]
    public void NewArrowDefaultsAreSmallerButLegacyGeometryIsUnchanged()
    {
        Assert.Equal(0.75, Style.Defaults(Tool.Arrow).HeadScale);
        Assert.Null(new Style().HeadScale);
    }

    [Fact]
    public void TextRenderingMultipliesColorAlphaByObjectOpacity()
    {
        var text = new Element(Tool.Text, new Rect(10, 10, 180, 70), text: "MMMM");
        text.Style.FontSize = 40;
        text.Style.Color = Palette.Blue;
        foreach (var (colorAlpha, opacity) in new[] { (0.0, 1.0), (0.5, 1.0), (0.5, 0.5), (1.0, 0.5) })
        {
            text.Style.Color = text.Style.Color with { Alpha = colorAlpha };
            text.Style.Opacity = opacity;
            var run = Assert.Single(Builder().Build([text]).Elements[0].Items.ToArray()).Text!;
            Assert.Equal(colorAlpha * opacity, run.Paint.EffectiveAlpha);
            Assert.Equal(new TextFont(FontDesign.System, FontWeight.Regular, 40), run.Font);
            Assert.Equal("MMMM", run.Text);
            Assert.Equal(text.Frame, run.Frame);
            Assert.False(run.WordWrap);
        }
    }

    [Fact]
    public void MeasurementOpacityCompositesEveryPaintedPartExactlyOnce()
    {
        var counter = new Element(Tool.Counter, new Rect(60, 40, 80, 80), text: "8");
        var ruler = new Element(Tool.Ruler, points: [new Point(30, 100), new Point(190, 100)], text: "160 px");
        foreach (var original in new[] { counter, ruler })
        {
            var opaque = Builder().Build([original]).Elements[0].Items.ToArray();
            Assert.DoesNotContain(opaque, item => item.Op == DisplayOp.PushLayer);
            var translucent = original;
            translucent.Style.Opacity = 0.5;
            var layered = Builder().Build([translucent]).Elements[0].Items.ToArray();
            Assert.Equal(DisplayOp.PushLayer, layered[0].Op);
            Assert.Equal(0.5, layered[0].Opacity);
            Assert.Equal(DisplayOp.PopLayer, layered[^1].Op);
            Assert.Equal(opaque.Length, layered.Length - 2);
            for (var index = 0; index < opaque.Length; index++)
            {
                Assert.True(opaque[index].ContentEquals(layered[index + 1]), $"{original.Tool} item {index} changed inside the layer.");
            }
            foreach (var part in opaque)
            {
                var bounds = Assert.NotNull(layered[0].LayerBounds);
                var area = part.Op switch
                {
                    DisplayOp.DrawText => part.Text!.Frame,
                    DisplayOp.FillRect => part.Rect,
                    _ => RectMath.Outset(part.Path!.Bounds!.Value, original.Style.Width / 2, original.Style.Width / 2),
                };
                Assert.True(RectMath.Contains(bounds, area), $"{original.Tool} {part.Op} leaves the layer.");
            }
            translucent.Style.Opacity = 0;
            Assert.Equal(0, Builder().Build([translucent]).Elements.Length);
        }
    }

    [Fact]
    public void RulerWidthChangesBothShaftAndEndpointMarks()
    {
        var ruler = new Element(Tool.Ruler, points: [new Point(30, 100), new Point(190, 100)], text: "160 px");
        foreach (var width in new[] { 1.0, 9 })
        {
            ruler.Style.Width = width;
            var strokes = Items(Builder().Build([ruler]).Elements[0], DisplayOp.StrokePath);
            Assert.Equal(3, strokes.Length);
            Assert.All(strokes, item => Assert.Equal(width, item.Stroke!.Width));
            Assert.All(strokes, item => Assert.Equal(LineCap.Butt, item.Stroke!.Cap));
            Assert.All(strokes, item => Assert.Equal(LineJoin.Miter, item.Stroke!.Join));
            Assert.Equal(new Rect(27, 97, 6, 6), strokes[1].Path!.Bounds);
            Assert.Equal(new Rect(187, 97, 6, 6), strokes[2].Path!.Bounds);
        }
    }

    [Fact]
    public void GradientStrokeAndFillHaveIndependentAlphaAndSpatialColors()
    {
        var shape = new Element(Tool.Rectangle, new Rect(20, 20, 160, 80));
        shape.Style.Width = 8;
        shape.Style.SetGradient(new ColorGradient([new RGBA(1, 0, 0), new RGBA(0, 0, 1)]), fill: false);
        shape.Style.SetGradient(GradientPreset.Lagoon.Gradient, fill: true);
        var items = Builder().Build([shape]).Elements[0].Items.ToArray();
        var fill = Assert.Single(items, item => item.Op == DisplayOp.FillPath);
        var stroke = Assert.Single(items, item => item.Op == DisplayOp.StrokePath);
        var gradient = stroke.Paint!.Gradient!;
        Assert.Equal(new GradientStop(0, new RGBA(1, 0, 0)), gradient.Stops[0]);
        Assert.Equal(new GradientStop(1, new RGBA(0, 0, 1)), gradient.Stops[1]);
        Assert.True(gradient.Start.X < gradient.End.X, "Red starts on the left.");
        Assert.Equal(gradient.Start.Y, gradient.End.Y, 12);
        Assert.Equal(1, stroke.Paint.EffectiveAlpha);
        Assert.InRange(fill.Paint!.EffectiveAlpha * 255, 100, 104);
        Assert.Equal(3, fill.Paint.Gradient!.Stops.Count);
        Assert.Equal(DisplayOp.PushClip, items[0].Op);
        Assert.Equal(new Rect(18, 18, 164, 84), items[0].Rect);
        shape.Style.Opacity = 0.5;
        var faded = Builder().Build([shape]).Elements[0].Items.ToArray();
        Assert.InRange(Assert.Single(faded, item => item.Op == DisplayOp.StrokePath).Paint!.EffectiveAlpha * 255, 126, 130);
        Assert.InRange(Assert.Single(faded, item => item.Op == DisplayOp.FillPath).Paint!.EffectiveAlpha * 255, 49, 53);
    }

    [Fact]
    public void GradientLabelsUseTheOwnerPaintAcrossTheOwnerFrame()
    {
        var shape = new Element(Tool.Rectangle, new Rect(20, 20, 440, 100));
        shape.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        shape.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: true);
        shape.Style.Width = 6;
        shape.Style.CornerRadius = 10;
        shape.Label = new ShapeLabel("Make your point", followsStroke: true);
        var items = Builder().Build([shape]).Elements[0].Items.ToArray();
        var text = Assert.Single(items, item => item.Op == DisplayOp.DrawText).Text!;
        var stroke = Assert.Single(items, item => item.Op == DisplayOp.StrokePath);
        Assert.Equal(stroke.Paint!.Gradient, text.Paint.Gradient);
        Assert.Equal(LinearGradientPaint.Create(GradientPreset.Sunset.Gradient, shape.Frame), text.Paint.Gradient);
    }

    [Fact]
    public void GradientsPreserveTheAlphaMaskOfEveryDrawingPath()
    {
        foreach (var tool in new[] { Tool.Rectangle, Tool.Pen, Tool.Highlighter, Tool.Arrow, Tool.Text, Tool.Counter, Tool.Ruler })
        {
            var element = new Element(tool, new Rect(20, 20, 150, 80), text: "8");
            if (tool.IsFreehand || tool.IsConnector || tool == Tool.Ruler)
            {
                element = new Element(tool, points: [new Point(30, 30), new Point(80, 60), new Point(140, 30)], text: "8");
            }
            element.Style.Opacity = 0.6;
            element.Style.Color = element.Style.Color with { Alpha = 0.5 };
            element.Style.Width = 8;
            if (tool.SupportsFill)
            {
                element.Style.SetPaintColor(Palette.Blue, fill: true);
            }
            if (tool == Tool.Pen)
            {
                element.Style.Pressure = true;
                element.Pressures = [0.5, 0.8, 1];
            }
            var solid = Drawing(Builder().Build([element]).Elements[0]);
            element.Style.SetGradient(GradientPreset.Spectrum.Gradient, fill: false);
            if (tool.SupportsFill)
            {
                element.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: true);
            }
            var gradient = Drawing(Builder().Build([element]).Elements[0]);
            Assert.Equal(solid.Length, gradient.Length);
            for (var index = 0; index < solid.Length; index++)
            {
                Assert.Equal(solid[index].Op, gradient[index].Op);
                if (solid[index].Paint is not { } before)
                {
                    Assert.True(solid[index].ContentEquals(gradient[index]));
                    continue;
                }
                var after = gradient[index].Paint!;
                if (after.Kind == PaintKind.LinearGradient)
                {
                    Assert.True(Math.Abs(before.EffectiveAlpha - after.EffectiveAlpha) < 1e-12, $"{tool.RawValue} changed opacity with its gradient");
                }
                else
                {
                    Assert.Equal(before, after);
                }
            }
        }
    }

    private static DisplayItem[] Drawing(DisplayList list) =>
        list.Items.ToArray().Where(item => item.Op is not (DisplayOp.PushClip or DisplayOp.PopClip)).ToArray();
}
