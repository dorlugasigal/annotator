using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using static Annotator.Core.Tests.SceneTestSupport;

namespace Annotator.Core.Tests;

/// <summary>Display lists for representative elements of every tool: paints, stroke styles, layers, clips, text and effects.</summary>
public class DisplayListTests
{
    private static DisplayItem[] Draw(Element element, bool pixelsAvailable = false) =>
        Builder().Build([element], pixelsAvailableForBlur: pixelsAvailable).Elements[0].Items.ToArray();

    [Fact]
    public void PenStrokesUseRoundCapsAndTheMacDashPatterns()
    {
        var pen = new Element(Tool.Pen, points: [new Point(0, 0), new Point(10, 5), new Point(20, 0)]);
        pen.Style.Width = 4;
        pen.Style.Opacity = 0.8;
        var stroke = Assert.Single(Draw(pen));
        Assert.Equal(DisplayOp.StrokePath, stroke.Op);
        Assert.Equal(new StrokeStyle(4, LineCap.Round, LineJoin.Round, 10), stroke.Stroke);
        Assert.Equal(Paint.Solid(Palette.Coral, 0.8), stroke.Paint);
        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo, PathVerb.LineTo], stroke.Path!.Verbs.ToArray());
        pen.Style.Pattern = StrokePattern.Dashed;
        Assert.Equal([20.0, 12], Draw(pen)[0].Stroke!.Dashes.ToArray());
        pen.Style.Pattern = StrokePattern.Dotted;
        Assert.Equal([0.01, 12], Draw(pen)[0].Stroke!.Dashes.ToArray());
        Assert.Equal(0, Draw(pen)[0].Stroke!.DashPhase);
    }

    [Fact]
    public void HighlightersAreWideButtCappedAndTranslucent()
    {
        var highlighter = new Element(Tool.Highlighter, points: [new Point(0, 0), new Point(30, 0)], style: Style.Defaults(Tool.Highlighter));
        highlighter.Style.Pattern = StrokePattern.Dashed;
        var stroke = Assert.Single(Draw(highlighter));
        Assert.Equal(20, stroke.Stroke!.Width);
        Assert.Equal(LineCap.Butt, stroke.Stroke.Cap);
        Assert.Equal(LineJoin.Bevel, stroke.Stroke.Join);
        Assert.Equal([20.0, 12], stroke.Stroke.Dashes.ToArray());
        Assert.Equal(0.35, stroke.Paint!.Opacity);
        var dot = new Element(Tool.Highlighter, points: [new Point(10, 10)], style: Style.Defaults(Tool.Highlighter));
        var mark = Assert.Single(Draw(dot));
        Assert.Equal(DisplayOp.FillRect, mark.Op);
        Assert.Equal(new Rect(10 - 3.2, 10 - 10, 6.4, 20), mark.Rect);
        var penDot = Assert.Single(Draw(new Element(Tool.Pen, points: [new Point(10, 10)])));
        Assert.Equal(DisplayOp.FillPath, penDot.Op);
        Assert.Equal(new Rect(8.5, 8.5, 3, 3), penDot.Path!.Bounds);
    }

    [Fact]
    public void RoughInkIsJitteredDeterministicallyFromTheSeed()
    {
        var pen = new Element(Tool.Pen, points: [new Point(0, 0), new Point(10, 0), new Point(20, 0)]) { Seed = 7 };
        pen.Style.Roughness = 2;
        var path = Draw(pen)[0].Path!;
        Assert.Equal(new Point(0, 0), path.Points[0]);
        Assert.NotEqual(new Point(10, 0), path.Points[1]);
        Assert.True(path.Points[1].Distance(new Point(10, 0)) <= Math.Sqrt(2) * 0.5 * 2 + 1e-12);
        var value = unchecked(7UL + 1UL * 0x9e3779b97f4a7c15UL);
        value = unchecked((value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94d049bb133111ebUL);
        value ^= value >> 31;
        Assert.Equal(new Point(10 + ((value & 0xffff) / 65535.0 - 0.5) * 2, ((value >> 16 & 0xffff) / 65535.0 - 0.5) * 2), path.Points[1]);
    }

    [Fact]
    public void ArrowsDrawShaftsHeadsAndCurvedOrRoughConnectors()
    {
        var arrow = new Element(Tool.Arrow, points: [new Point(0, 0), new Point(100, 0)]);
        arrow.Style.Pattern = StrokePattern.Dashed;
        var items = Draw(arrow);
        Assert.Equal(2, items.Length);
        Assert.True(items[0].Stroke!.IsDashed);
        Assert.Equal([PathVerb.MoveTo, PathVerb.LineTo, PathVerb.LineTo], items[1].Path!.Verbs.ToArray());
        Assert.False(items[1].Stroke!.IsDashed);
        Assert.Equal(items[0].Paint, items[1].Paint);
        arrow.Style.EndHead = ArrowHead.Filled;
        arrow.Style.StartHead = ArrowHead.Circle;
        var filled = Draw(arrow);
        Assert.Equal(DisplayOp.FillPath, filled[1].Op);
        Assert.Equal(DisplayOp.FillPath, filled[2].Op);
        Assert.Equal(PathVerb.Close, filled[2].Path!.Verbs[^1]);
        var curved = new Element(Tool.Arrow, points: [new Point(0, 0), new Point(50, 40), new Point(100, 0)]);
        curved.Style.Curved = true;
        Assert.Equal([PathVerb.MoveTo, PathVerb.CubicTo, PathVerb.CubicTo], Draw(curved)[0].Path!.Verbs.ToArray());
        curved.Style.Roughness = 3;
        var rough = Draw(curved)[0].Path!;
        Assert.Equal(8, rough.VerbCount);
        Assert.Equal(new Point(0, 0), rough.Points[0]);
        var relationship = new Element(Tool.Arrow, points: [new Point(0, 0), new Point(100, 0)]);
        relationship.Style.EndHead = ArrowHead.CardinalityZeroOrMany;
        var parts = Draw(relationship);
        Assert.Equal(DisplayOp.PushLayer, parts[0].Op);
        Assert.Equal(1 + 3 + 1, parts.Count(item => item.Op == DisplayOp.StrokePath));
    }

    [Fact]
    public void FilledShapesClipTheirFillToTheOutline()
    {
        var rectangle = new Element(Tool.Rectangle, new Rect(10, 10, 100, 60));
        rectangle.Style.Fill = new RGBA(0, 0, 1, 0.4);
        rectangle.Style.Opacity = 0.5;
        var items = Draw(rectangle);
        Assert.Equal([DisplayOp.FillPath, DisplayOp.StrokePath], items.Select(item => item.Op).ToArray());
        Assert.Equal(Paint.Solid(new RGBA(0, 0, 1, 0.4), 0.5), items[0].Paint);
        Assert.Equal(FillRule.NonZero, items[0].FillRule);
        Assert.Same(items[0].Path, items[1].Path);
        var database = new Element(Tool.Database, new Rect(10, 10, 100, 100));
        database.Style.Fill = new RGBA(0, 0, 1, 0.4);
        var clipped = Draw(database);
        Assert.Equal([DisplayOp.PushLayer, DisplayOp.FillRect, DisplayOp.PopLayer, DisplayOp.StrokePath], clipped.Select(item => item.Op).ToArray());
        Assert.Equal(database.Frame, clipped[1].Rect);
        Assert.Equal(FillRule.NonZero, clipped[0].FillRule);
        Assert.Null(clipped[0].LayerBounds);
        var rough = rectangle;
        rough.Style.Roughness = 3;
        var sketch = Draw(rough);
        Assert.False(sketch[0].Path!.ContentEquals(sketch[1].Path));
        Assert.Equal(5, sketch[0].Path!.VerbCount);
    }

    [Fact]
    public void HatchingStrokesDiagonalsAtTheMacSpacingInsideTheShape()
    {
        var shape = new Element(Tool.Ellipse, new Rect(10, 20, 100, 40));
        shape.Style.Fill = Palette.Blue;
        shape.Style.FillPattern = FillPattern.Hatch;
        shape.Style.Width = 4;
        var items = Draw(shape);
        Assert.Equal([DisplayOp.PushLayer, DisplayOp.StrokePath, DisplayOp.PopLayer, DisplayOp.StrokePath], items.Select(item => item.Op).ToArray());
        Assert.True(items[0].Path!.ContentEquals(new PathBuilder().AddEllipse(shape.Frame).ToPathData()));
        var hatch = items[1];
        Assert.Equal(new StrokeStyle(2, LineCap.Round, LineJoin.Round), hatch.Stroke);
        var spacing = 12.0;
        double start = 10 - 40, end = 110 + 40;
        var expected = 0;
        for (var index = 0L; ; index++)
        {
            var x = index == 0 ? start : Math.FusedMultiplyAdd(index, spacing, start);
            if (x > end)
            {
                break;
            }
            Assert.Equal(new Point(x, 20), hatch.Path!.Points[expected * 2]);
            Assert.Equal(new Point(x + 40, 60), hatch.Path.Points[expected * 2 + 1]);
            expected++;
        }
        Assert.Equal(expected * 2, hatch.Path!.VerbCount);
        shape.Style.FillPattern = FillPattern.Crosshatch;
        Assert.Equal(expected * 4, Draw(shape)[1].Path!.VerbCount);
    }

    [Fact]
    public void TextAndCountersCarryEverythingDirectWriteNeeds()
    {
        var text = new Element(Tool.Text, new Rect(5, 6, 120, 40), text: "Hello\nworld");
        text.Style.FontSize = 18;
        text.Style.Bold = true;
        text.Style.FontName = "monospace";
        text.Style.Alignment = TextAlignment.Right;
        text.Style.Color = new RGBA(0.1, 0.2, 0.3, 0.5);
        text.Style.Opacity = 0.5;
        var run = Assert.Single(Draw(text)).Text!;
        Assert.Equal("Hello\nworld", run.Text);
        Assert.Equal(text.Frame, run.Frame);
        Assert.Equal(new TextFont(FontDesign.Monospace, FontWeight.Semibold, 18), run.Font);
        Assert.Equal(TextAlignment.Right, run.Alignment);
        Assert.Equal(Paint.Solid(new RGBA(0.1, 0.2, 0.3, 0.25)), run.Paint);
        Assert.Equal(text.Frame.Y + MacTextMetrics.Ascender(18), run.Baseline);
        var counter = new Element(Tool.Counter, new Rect(0, 0, 50, 50), text: "12");
        counter.Style.Color = new RGBA(1, 0, 0, 0.9);
        var items = Draw(counter);
        Assert.Equal(DisplayOp.FillPath, items[0].Op);
        Assert.Equal(Paint.Solid(new RGBA(1, 0, 0, 0.9)), items[0].Paint);
        Assert.True(items[0].Path!.ContentEquals(new PathBuilder().AddEllipse(counter.Frame).ToPathData()));
        var label = items[1].Text!;
        Assert.Equal(new Rect(0, 6, 50, 50), label.Frame);
        Assert.Equal(new TextFont(FontDesign.System, FontWeight.Semibold, counter.Frame.Height * 0.56), label.Font);
        Assert.Equal(TextAlignment.Center, label.Alignment);
        Assert.Equal(Paint.Solid(Palette.White), label.Paint);
        Assert.Equal(6 + MacTextMetrics.Ascender(counter.Frame.Height * 0.56), label.Baseline);
    }

    [Fact]
    public void RulersDrawTheirLabelOnTheWindowBackground()
    {
        var ruler = new Element(Tool.Ruler, points: [new Point(10, 50), new Point(110, 50)], text: "100 px");
        ruler.Style.Bold = true;
        var items = Draw(ruler);
        Assert.Equal(
            [DisplayOp.StrokePath, DisplayOp.StrokePath, DisplayOp.StrokePath, DisplayOp.FillRect, DisplayOp.DrawText],
            items.Select(item => item.Op).ToArray());
        Assert.Equal(new Rect(0, 25, 120, 22), items[3].Rect);
        Assert.Same(Paint.WindowBackground, items[3].Paint);
        Assert.Equal(new TextFont(FontDesign.System, FontWeight.Semibold, 13), items[4].Text!.Font);
        Assert.Equal(new Rect(0, 25, 120, 22), items[4].Text!.Frame);
        Assert.Equal(TextAlignment.Center, items[4].Text!.Alignment);
        Assert.Equal(25 + MacTextMetrics.Ascender(13), items[4].Text!.Baseline);
    }

    [Fact]
    public void PixelEffectsFailClosedUntilTheirPixelsAreAvailable()
    {
        foreach (var tool in new[] { Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            var effect = new Element(tool, new Rect(10.4, 10.6, 50.2, 20));
            var cover = Assert.Single(Draw(effect));
            Assert.Equal(DisplayOp.FillRect, cover.Op);
            Assert.Equal(new Rect(10, 10, 51, 21), cover.Rect);
            Assert.Equal(RectSnap.OutwardToPixels, cover.Snap);
            Assert.Same(Paint.Black, cover.Paint);
            Assert.Equal(0, Builder().Build([effect], pixelsAvailableForBlur: true).Elements.Length);
        }
        var solid = new Element(Tool.Cover, new Rect(0, 0, 10, 10));
        Assert.Single(Builder().Build([solid], pixelsAvailableForBlur: true).Elements.ToArray());
    }

    [Fact]
    public void StackingDrawsDrawingsThenEffectsThenCovers()
    {
        var cover = new Element(Tool.Cover, new Rect(0, 0, 5, 5));
        var blur = new Element(Tool.Blur, new Rect(0, 0, 5, 5));
        var pen = new Element(Tool.Pen, points: [new Point(0, 0), new Point(1, 1)]);
        var text = new Element(Tool.Text, new Rect(0, 0, 40, 20), text: "a");
        var scene = Builder().Build([cover, blur, pen, text]);
        Assert.Equal([pen.Id, text.Id, blur.Id, cover.Id], scene.Elements.ToArray().Select(list => list.ElementId).ToArray());
    }

    [Fact]
    public void ImagesCarryTheirBitmapAndAFailClosedReplacement()
    {
        var image = new Element(Tool.Image, new Rect(10, 20, 30, 40)) { ImagePng = ValueList.Create<byte>([137, 80, 78, 71, 13, 10, 26, 10]) };
        image.Style.Opacity = 0.7;
        var items = Draw(image);
        Assert.Equal([DisplayOp.DrawImage, DisplayOp.StrokePath, DisplayOp.StrokePath], items.Select(item => item.Op).ToArray());
        var draw = items[0].Image!;
        Assert.Equal(image.Id, draw.ElementId);
        Assert.Equal(image.Frame, draw.Destination);
        Assert.Null(draw.Source);
        Assert.Equal(0.7, draw.Opacity);
        Assert.Equal(ImageInterpolation.HighQualityCubic, draw.Interpolation);
        Assert.Equal(2, draw.FallbackItemCount);
        Assert.Equal(8, draw.Png.Count);
        Assert.Same(Paint.Red, items[1].Paint);
        Assert.Equal(new StrokeStyle(1), items[1].Stroke);
        Assert.Equal(new Rect(10, 20, 30, 40), items[1].Path!.Bounds);
        Assert.Equal([new Point(10, 20), new Point(40, 60)], items[2].Path!.Points.ToArray());
        var missing = new Element(Tool.Image, new Rect(10, 20, 30, 40));
        var list = Builder().Build([missing]).Elements[0];
        Assert.Equal(SceneIssues.MissingImageData, list.Issues);
        Assert.Equal(2, list.Count);
        Assert.Equal(DisplayOp.StrokePath, list.Items[0].Op);
    }

    [Fact]
    public void RotatedElementsPushTheirTransformAroundTheCenter()
    {
        var shape = new Element(Tool.Rectangle, new Rect(0, 0, 40, 20)) { Rotation = 0.5 };
        var items = Draw(shape);
        Assert.Equal(DisplayOp.PushTransform, items[0].Op);
        Assert.Equal(AffineTransform.RotationAbout(new Point(20, 10), 0.5), items[0].Transform);
        Assert.Equal(DisplayOp.PopTransform, items[^1].Op);
        shape.Rotation = 0;
        Assert.DoesNotContain(Draw(shape), item => item.Op == DisplayOp.PushTransform);
    }

    [Fact]
    public void LabelsLeaveAGapInTheOutlineAndDrawAtFullContextAlpha()
    {
        var shape = new Element(Tool.Rectangle, new Rect(0, 0, 200, 100)) { Label = new ShapeLabel("Hi") };
        shape.Style.Opacity = 0.5;
        var items = Draw(shape);
        Assert.Equal([DisplayOp.PushLayer, DisplayOp.StrokePath, DisplayOp.PopLayer, DisplayOp.DrawText], items.Select(item => item.Op).ToArray());
        var mask = items[0];
        Assert.Equal(FillRule.EvenOdd, mask.FillRule);
        Assert.False(mask.Path!.Contains(shape.Frame.Center, FillRule.EvenOdd));
        Assert.True(mask.Path.Contains(new Point(0, 0), FillRule.EvenOdd));
        var label = items[3].Text!;
        Assert.Equal(shape.Frame.Center, label.Frame.Center);
        Assert.Equal(0.5, label.Paint.EffectiveAlpha);
        // The label sits where the Mac draws it from the top of its own content height, centered on the shape.
        Assert.Equal(shape.Frame.Center.Y - MacTextMetrics.ContentHeight("Hi", label.Font.Size) / 2 + MacTextMetrics.Ascender(label.Font.Size),
                     label.Baseline, 9);
        var hidden = Builder().Build([shape], hiddenLabelIds: new HashSet<Guid> { shape.Id }).Elements[0].Items.ToArray();
        Assert.Equal([DisplayOp.PushLayer, DisplayOp.StrokePath, DisplayOp.PopLayer], hidden.Select(item => item.Op).ToArray());
        shape.Label = new ShapeLabel("   ");
        Assert.Single(Draw(shape));
    }

    [Fact]
    public void InvalidGradientsDrawNothingForTheirPaintAndReportIt()
    {
        var shape = new Element(Tool.Rectangle, new Rect(0, 0, 20, 20));
        shape.Style.Fill = Palette.Blue;
        shape.Style.StrokeGradient = new ColorGradient([Palette.Blue]);
        var list = Builder().Build([shape]).Elements[0];
        Assert.Equal(SceneIssues.InvalidGradient, list.Issues);
        Assert.Equal(DisplayOp.FillPath, Assert.Single(list.Items.ToArray()).Op);
        Assert.Equal(SceneIssues.InvalidGradient, Builder().Build([shape]).Issues);
        var line = new Element(Tool.Line, points: [new Point(0, 0), new Point(10, 10)]);
        line.Style.StrokeGradient = new ColorGradient([Palette.Blue]);
        var hidden = Builder().Build([line], fadedIds: new HashSet<Guid> { line.Id });
        Assert.Equal(0, hidden.Elements.Length);
        Assert.Equal(SceneIssues.InvalidGradient, hidden.Issues);
    }

    [Fact]
    public void SelectionScalesOutlinesAndHandlesAndDashesLockedObjects()
    {
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 40, 40));
        var selection = Builder().Build([shape], new HashSet<Guid> { shape.Id }, selectionScale: 2).Selection.Items.ToArray();
        var outline = selection[0];
        Assert.Equal(0.5, outline.Stroke!.Width);
        Assert.Equal(LineCap.Butt, outline.Stroke.Cap);
        Assert.Equal(new Rect(8.5, 8.5, 43, 43), outline.Path!.Bounds);
        Assert.Same(Paint.Accent, outline.Paint);
        Assert.Equal(1 + 4 * 2 + 2, selection.Length);
        Assert.Equal(new Rect(8, 8, 4, 4), selection[1].Path!.Bounds);
        Assert.Same(Paint.White, selection[1].Paint);
        Assert.Equal(new Rect(27, -5, 6, 6), selection[^1].Path!.Bounds);
        shape.Locked = true;
        var locked = Assert.Single(Builder().Build([shape], new HashSet<Guid> { shape.Id }, selectionScale: 2).Selection.Items.ToArray());
        Assert.Equal([1.5, 1.5], locked.Stroke!.Dashes.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => Builder().Build([shape], selectionScale: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Builder().Build([shape], selectionScale: double.NaN));
        var cover = new Element(Tool.Cover, new Rect(0, 0, 10, 10));
        var other = new Element(Tool.Rectangle, new Rect(20, 0, 10, 10));
        var group = Builder().Selection([cover, other], new HashSet<Guid> { cover.Id, other.Id }).Items.ToArray();
        Assert.Equal(2 + 1 + 4 * 2, group.Length);
        var faded = Builder().Build([shape], new HashSet<Guid> { shape.Id }, fadedIds: new HashSet<Guid> { shape.Id });
        Assert.True(faded.Selection.IsEmpty);
    }

    [Fact]
    public void DrawingBoundsCoverHeadsLabelsAndRotation()
    {
        var builder = Builder();
        var arrow = new Element(Tool.Arrow, points: [new Point(0, 0), new Point(100, 0)]);
        arrow.Style.EndHead = ArrowHead.CardinalityZeroOrMany;
        var bounds = builder.DrawingBounds(arrow);
        var head = ArrowHeadGeometry.Make(arrow, atStart: false)!.Value;
        Assert.True(bounds.Contains(head.Left) && bounds.Contains(head.Right));
        Assert.Equal(-9.5, bounds.X);
        var shape = new Element(Tool.Rectangle, new Rect(0, 0, 40, 20)) { Label = new ShapeLabel("A much longer label than the box") };
        var labelled = builder.DrawingBounds(shape);
        Assert.True(labelled.Width > 100);
        var rotated = shape;
        rotated.Label = null;
        rotated.Rotation = Math.PI / 2;
        var turned = builder.DrawingBounds(rotated);
        Assert.Equal(20 + 2 * 9.5, turned.Width, 9);
        Assert.Equal(40 + 2 * 9.5, turned.Height, 9);
        var counter = new Element(Tool.Counter, new Rect(0, 0, 50, 50), text: "1");
        Assert.Equal(56 + 2 * 9.5, builder.DrawingBounds(counter).Height, 9);
    }

    [Fact]
    public void ListsAreReadOnlySnapshotsSafeToCompare()
    {
        var shape = new Element(Tool.Diamond, new Rect(0, 0, 30, 30));
        shape.Style.CornerRadius = 6;
        var first = Builder().Build([shape]).Elements[0];
        var second = Builder().Build([shape]).Elements[0];
        Assert.NotSame(first, second);
        Assert.True(first.ContentEquals(second));
        Assert.Equal(shape.Id, first.ElementId);
        Assert.Contains("StrokePath", first.ToString(), StringComparison.Ordinal);
        Assert.Equal(4, first.Items[0].Path!.Verbs.ToArray().Count(verb => verb == PathVerb.QuadTo));
    }
}
