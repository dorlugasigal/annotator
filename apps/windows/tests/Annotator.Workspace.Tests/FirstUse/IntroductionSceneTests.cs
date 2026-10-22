using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Workspace.FirstUse;

namespace Annotator.Workspace.Tests.FirstUse;

/// <summary>
/// The tour's illustrations (Mac IntroductionIllustrations.swift) drawn on a recording canvas: what each page shows at the
/// moments of its story, and the arc-length trim their strokes draw on with.
/// </summary>
public sealed class IntroductionSceneTests
{
    [Fact]
    public void TrimmingFollowsArcLengthAcrossLinesAndCurves()
    {
        PathData line = new PathBuilder().MoveTo(new Point(0, 0)).LineTo(new Point(10, 0)).LineTo(new Point(10, 10)).ToPathData();
        (PathData half, Point? end) = PathTrim.Start(line, 0.5);
        Assert.Equal(new Point(10, 0), end);
        Assert.Equal(new Point(10, 0), half.CurrentPoint);
        (_, Point? quarter) = PathTrim.Start(line, 0.75);
        Assert.Equal(10, quarter!.Value.X, 9);
        Assert.Equal(5, quarter.Value.Y, 9);
        Assert.True(PathTrim.Start(line, 0).Path.IsEmpty);
        Assert.Equal(new Point(10, 10), PathTrim.Start(line, 1).End);

        // A circle of radius 10 traced from its rightmost point: half its length ends at the leftmost point.
        PathData circle = new PathBuilder().AddEllipse(new Rect(-10, -10, 20, 20)).ToPathData();
        (_, Point? opposite) = PathTrim.Start(circle, 0.5);
        Assert.Equal(-10, opposite!.Value.X, 1);
        Assert.Equal(0, opposite.Value.Y, 1);
    }

    [Fact]
    public void TheOutlineStartsAtTheTopCenterAndRunsClockwise()
    {
        var rect = new Rect(0, 0, 200, 80);
        PathData outline = IntroductionScene.Outline(rect, 12);
        Assert.Equal(new Point(100, 0), outline.Points[0]);
        (_, Point? quarter) = PathTrim.Start(outline, 0.2);
        Assert.True(quarter!.Value.X > 150, "The stroke heads right first, so it runs clockwise on screen.");
        Assert.Equal(new Rect(0, 0, 200, 80), outline.TightBounds);
    }

    [Fact]
    public void TheLoopOvershootsItsStartAroundTheBar()
    {
        PathData loop = IntroductionScene.Loop(new Point(100, 50), 17, 40);
        Assert.Equal(97, loop.PointCount);
        Rect bounds = loop.TightBounds!.Value;
        Assert.InRange(bounds.Width, 34, 46);
        Assert.InRange(bounds.Height, 80, 100);
        Assert.InRange(bounds.Center.X, 95, 105);
    }

    [Fact]
    public void EveryPageDrawsBalancedAndClippedToTheDesktop()
    {
        foreach (IntroductionPage page in new[] { IntroductionPage.Draw, IntroductionPage.Color, IntroductionPage.Focus })
        {
            foreach (double progress in new[] { 0, 0.1, 0.25, 0.4, 0.5, 0.64, 0.75, 0.85, 1 })
            {
                foreach (bool dark in new[] { false, true })
                {
                    var canvas = new RecordingCanvas();
                    new IntroductionScene(IntroductionScene.StandardSize, progress, dark).Draw(page, canvas);
                    Assert.Equal(0, canvas.Depth);
                    Assert.Equal("PushClip", canvas.Operations[0].Kind);
                    Assert.True(canvas.Operations.Count > 10, $"{page} at {progress} drew {canvas.Operations.Count} operations.");
                }
            }
        }
        var setup = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 1, false).Draw(IntroductionPage.Setup, setup);
        Assert.Empty(setup.Operations);
    }

    [Fact]
    public void TheDrawPageEndsWithTheCircleShownTheToolbarGoneAndThePointerOnSend()
    {
        var canvas = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 1, dark: false).Draw(IntroductionPage.Draw, canvas);
        Assert.Contains(canvas.Operations, operation => operation is { Kind: "Text", Text: "Send" });
        Assert.DoesNotContain(canvas.Operations, operation => operation.Kind == "Symbol");
        // The ink loop: a round-capped 2.6-point stroke at full opacity.
        Recorded loop = Assert.Single(canvas.Operations, operation => operation is { Kind: "Stroke", Width: 2.6 });
        Assert.Equal(1, loop.Opacity, 9);
        // The pointer rests on the button's right side.
        Recorded pointer = canvas.Operations.Last(operation => operation is { Kind: "Fill", Paint.Color: { Red: 0, Green: 0, Blue: 0, Alpha: 1 } });
        double width = IntroductionScene.StandardSize.Width;
        Assert.Equal(width * 0.07 + width * 0.56 - 52 + 40 - 7, pointer.Transform.Tx, 6);
    }

    [Fact]
    public void TheDrawPageShowsTheToolbarWhileDrawingAndHidesTheInkForShowHide()
    {
        var drawing = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 0.3, dark: false).Draw(IntroductionPage.Draw, drawing);
        Assert.Equal(["pencil.tip", "highlighter", "rectangle", "arrow.up.right", "textformat"],
            drawing.Operations.Where(operation => operation.Kind == "Symbol").Select(operation => operation.Text));

        var hidden = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 0.8, dark: false).Draw(IntroductionPage.Draw, hidden);
        Recorded loop = Assert.Single(hidden.Operations, operation => operation is { Kind: "Stroke", Width: 2.6 });
        Assert.Equal(0, loop.Opacity, 9);
    }

    [Fact]
    public void TheColorPageTypesTheLabelWithTheGradient()
    {
        var typing = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 0.78, dark: false).Draw(IntroductionPage.Color, typing);
        Recorded partial = Assert.Single(typing.Operations, operation => operation.Kind == "Text");
        Assert.True(partial.Text!.Length is > 0 and < 15);
        Assert.StartsWith(partial.Text, IntroductionScene.Label, StringComparison.Ordinal);
        Assert.Equal(IllustrationPaintKind.Linear, partial.Paint!.Kind);
        Assert.Contains(typing.Operations, operation => operation is { Kind: "Fill", Paint.Kind: IllustrationPaintKind.Conic });
        Assert.Contains(typing.Operations, operation => operation.Kind == "Fill" && operation.Paint!.Color == IntroductionScene.Ink with { Alpha = 1 });

        var done = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 1, dark: true).Draw(IntroductionPage.Color, done);
        Assert.Equal(IntroductionScene.Label, Assert.Single(done.Operations, operation => operation.Kind == "Text").Text);
        Assert.DoesNotContain(done.Operations, operation => operation.Kind == "Blur");
    }

    [Fact]
    public void TheFocusPageEndsWithTheSpotlightAndTheLens()
    {
        static bool Spotlight(Recorded operation) =>
            operation is { Kind: "Fill", Paint: { Kind: IllustrationPaintKind.Radial } paint } && paint.Stops[^1].Color is { Red: 0, Alpha: > 0 };
        var canvas = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 1, dark: false).Draw(IntroductionPage.Focus, canvas);
        Recorded dim = Assert.Single(canvas.Operations, Spotlight);
        Assert.Equal(0.32, dim.Paint!.Stops[^1].Color.Alpha, 9);
        Assert.Equal(52, dim.Paint.Radius, 9);
        // The small print is drawn twice: in the window and magnified 1.8 times in the lens.
        Assert.Equal(2, canvas.Operations.Count(operation => operation is { Kind: "Text", Text: "$1,284.00" }));
        Assert.Contains(canvas.Operations, operation => operation is { Kind: "Text", Text: "Total", Transform.A: 1.8 });
        Assert.Contains(canvas.Operations, operation => operation is { Kind: "Stroke", Paint.Color: var color } && color == IntroductionScene.Halo);

        var early = new RecordingCanvas();
        new IntroductionScene(IntroductionScene.StandardSize, 0.1, dark: false).Draw(IntroductionPage.Focus, early);
        Assert.DoesNotContain(early.Operations, Spotlight);
        Assert.Single(early.Operations, operation => operation is { Kind: "Text", Text: "$1,284.00" });
    }

    private sealed record Recorded(string Kind, IllustrationPaint? Paint, double Width, string? Text, double Opacity, AffineTransform Transform);

    /// <summary>Records each drawing operation with the opacity and transform it would be drawn with.</summary>
    private sealed class RecordingCanvas : IIllustrationCanvas
    {
        private readonly Stack<(double Opacity, AffineTransform Transform)> _states = new();
        private double _opacity = 1;
        private AffineTransform _transform = AffineTransform.Identity;

        public List<Recorded> Operations { get; } = [];

        public int Depth => _states.Count;

        public void PushOpacity(double opacity)
        {
            Save("Opacity");
            _opacity *= opacity;
        }

        public void PushTransform(in AffineTransform transform)
        {
            Save("Transform");
            _transform = AffineTransform.Concat(transform, _transform);
        }

        public void PushClip(PathData path) => Save("PushClip");

        public void PushShadow(RGBA color, double radius, double offsetX, double offsetY) => Save("Shadow");

        public void PushBlur(double radius) => Save("Blur");

        public void Pop() => (_opacity, _transform) = _states.Pop();

        public void Fill(PathData path, IllustrationPaint paint, FillRule rule = FillRule.NonZero) => Add("Fill", paint, 0, null);

        public void Stroke(PathData path, IllustrationPaint paint, double width, LineCap cap = LineCap.Butt, LineJoin join = LineJoin.Miter) =>
            Add("Stroke", paint, width, null);

        public void DrawText(string text, TextFont font, IllustrationPaint paint, Point point, IllustrationAnchor anchor) => Add("Text", paint, 0, text);

        public double MeasureText(string text, TextFont font) => text.Length * font.Size * 0.55;

        public void DrawSymbol(string symbol, Point center, double size, RGBA color) => Add("Symbol", IllustrationPaint.Solid(color), size, symbol);

        private void Save(string kind)
        {
            _states.Push((_opacity, _transform));
            Operations.Add(new Recorded(kind, null, 0, null, _opacity, _transform));
        }

        private void Add(string kind, IllustrationPaint paint, double width, string? text) =>
            Operations.Add(new Recorded(kind, paint, width, text, _opacity, _transform));
    }
}
