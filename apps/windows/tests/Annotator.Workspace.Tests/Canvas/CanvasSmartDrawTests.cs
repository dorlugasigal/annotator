using Annotator.Core;
using Annotator.Workspace.Canvas;
using Annotator.Core.Motion;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>Smart Draw previews, recognition and the ink-to-shape morph (SmartDrawGestureTests and DrawingFeedbackTests).</summary>
public sealed class CanvasSmartDrawTests
{
    private static CanvasHarness SmartHarness(ICanvasScheduler? scheduler = null, bool reducesMotion = false)
    {
        CanvasHarness h = Single(scheduler: scheduler, reducesMotion: reducesMotion);
        h.Model.SetSmartDraw(true);
        h.Choose(Tool.Pen);
        return h;
    }

    private static Point Circle(int index) =>
        new(250 + 80 * Math.Cos(index / 120.0 * Math.PI * 2), 200 + 60 * Math.Sin(index / 120.0 * Math.PI * 2));

    private static void DrawEllipseWithoutRelease(CanvasHarness h)
    {
        h.Down(Main, new Point(330, 200));
        for (int index = 1; index <= 120; index++)
        {
            h.Move(Main, Circle(index));
        }
    }

    [Fact]
    public void SmartDrawPreviewsBeforeReleaseAndCancelsWithoutCommitting()
    {
        // DrawingFeedbackTests.testSmartDrawPreviewsBeforeReleaseAndCancelsWithoutCommitting.
        using CanvasHarness h = SmartHarness();
        DrawEllipseWithoutRelease(h);
        Element candidate = Assert.IsType<Element>(h[Main].State.SmartPreview);
        Assert.Equal(Tool.Ellipse, candidate.Tool);
        Assert.Equal(StrokePattern.Dashed, candidate.Style.Pattern);
        Assert.Equal(0.75, candidate.Style.Opacity);
        Assert.Contains(candidate.Id, h[Main].State.FadedIds);
        Assert.Equal(Tool.Pen, h[Main].State.Draft?.Tool);
        Assert.Equal(EditKind.Gesture, h.Model.Display(Main).ActiveEdit);
        h[Main].CancelGesture();
        h.Pump();
        Assert.Null(h[Main].State.SmartPreview);
        Assert.Empty(h.Elements(Main));
        Assert.False(h.Document(Main).CanUndo);
    }

    [Fact]
    public void DiamondAndArrowPreviewCommitAndUndoThroughPenEvents()
    {
        // SmartDrawGestureTests.testDiamondAndArrowPreviewCommitAndUndoThroughPenEvents.
        using CanvasHarness h = SmartHarness();
        (Tool Tool, Point[] Vertices)[] cases =
        [
            (Tool.Diamond, [new(180, 70), new(290, 160), new(175, 250), new(70, 160), new(180, 70)]),
            (Tool.Arrow, [new(80, 170), new(330, 170), new(280, 130), new(330, 170), new(280, 210)]),
        ];
        foreach ((Tool tool, Point[] vertices) in cases)
        {
            h.Down(Main, vertices[0]);
            for (int index = 1; index < vertices.Length; index++)
            {
                for (int step = 1; step <= 24; step++)
                {
                    h.Move(Main, vertices[index - 1] + (vertices[index] - vertices[index - 1]) * (step / 24.0));
                }
            }
            Assert.Equal(tool, h[Main].State.SmartPreview?.Tool);
            h.Up(Main, vertices[^1]);
            Element recognized = Assert.Single(h.Elements(Main));
            Assert.Equal(tool, recognized.Tool);
            h.Model.Undo();
            Assert.Empty(h.Elements(Main));
            h.Model.Redo();
            Assert.Equal(tool, h.Elements(Main)[^1].Tool);
            h.Model.Undo();
            h.Pump();
        }
    }

    [Fact]
    public void SnapMorphIsBoundedAndLandsOnTheRecognizedShapeInOneUndoStep()
    {
        // SmartDrawGestureTests.testSnapMorphIsBoundedAndLandsOnTheRecognizedShapeInOneUndoStep.
        using CanvasHarness h = SmartHarness();
        DrawEllipseWithoutRelease(h);
        h.Up(Main, Circle(120));
        SnapMorph morph = Assert.IsType<SnapMorph>(h[Main].State.Morph);
        Assert.Equal(Tool.Ellipse, morph.Shape.Tool);
        Assert.Equal(96, morph.From.Count);
        Assert.Equal(96, morph.To?.Count);
        Assert.True(h[Main].State.HasTransitions);
        Assert.DoesNotContain(h[Main].State.Elements, element => element.Id == morph.Shape.Id);
        Element[] recognized = h.Elements(Main);
        Assert.Equal(Tool.Ellipse, recognized[0].Tool);

        Assert.True(h[Main].AdvanceTransitions(100));
        SnapMorphFrame start = morph.FrameAt(0);
        Assert.Equal(0, start.ShapeAlpha);
        Assert.NotNull(start.Line);
        Assert.True(h[Main].AdvanceTransitions(100.11));
        Assert.Equal(0.5, h[Main].State.MorphProgress, 6);
        SnapMorphFrame end = morph.FrameAt(1);
        Assert.Equal(1, end.ShapeAlpha);
        Assert.Null(end.Line);
        Assert.False(h[Main].AdvanceTransitions(100.23));
        Assert.Null(h[Main].State.Morph);
        Assert.False(h[Main].State.HasTransitions);
        Assert.Contains(h[Main].State.Elements, element => element.Id == morph.Shape.Id);
        Assert.Equal(recognized, h.Elements(Main));
        h.Model.Undo();
        Assert.Empty(h.Elements(Main));
        Assert.False(h.Document(Main).CanUndo, "Ink and recognition stay one Undo step.");
    }

    [Fact]
    public void SnapMorphJumpsToTheShapeOnANewGestureOrUndo()
    {
        // SmartDrawGestureTests.testSnapMorphJumpsToTheShapeOnANewGestureOrUndo.
        using CanvasHarness h = SmartHarness();
        DrawEllipseWithoutRelease(h);
        h.Up(Main, Circle(120));
        Assert.NotNull(h[Main].State.Morph);
        Element[] recognized = h.Elements(Main);
        h.Down(Main, new Point(520, 340));
        Assert.Null(h[Main].State.Morph);
        h[Main].CancelGesture();
        h.Pump();
        Assert.Equal(recognized, h.Elements(Main));

        h.Model.Undo();
        DrawEllipseWithoutRelease(h);
        h.Up(Main, Circle(120));
        Assert.NotNull(h[Main].State.Morph);
        h.Model.Undo();
        h.Pump();
        Assert.Null(h[Main].State.Morph);
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void ReducedMotionShowsTheRecognizedShapeWithoutAMorph()
    {
        // SmartDrawGestureTests.testReduceMotionShowsTheRecognizedShapeWithoutAMorph.
        using CanvasHarness h = SmartHarness(reducesMotion: true);
        DrawEllipseWithoutRelease(h);
        h.Up(Main, Circle(120));
        Assert.Null(h[Main].State.Morph);
        Assert.False(h[Main].State.HasTransitions);
        Assert.Equal(Tool.Ellipse, h.Elements(Main)[0].Tool);
    }

    [Fact]
    public void PreviewsShowOnlyWhenNoSampleArrivedDuringRecognition()
    {
        // Mac requestSmartPreview: one recognition at a time on the newest stroke; a result is shown only when the
        // stroke did not change while it ran, so the candidate appears when the pen pauses.
        var scheduler = new ManualScheduler();
        using CanvasHarness h = SmartHarness(scheduler);
        h.Down(Main, new Point(330, 200));
        for (int index = 1; index <= 110; index++)
        {
            h.Move(Main, Circle(index));
        }
        Assert.Equal(1, scheduler.Pending);
        h.Move(Main, Circle(111));
        Assert.True(scheduler.RunNext());
        Assert.Null(h[Main].State.SmartPreview);
        Assert.Equal(1, scheduler.Pending);
        for (int index = 112; index <= 120; index++)
        {
            h.Move(Main, Circle(index));
        }
        Assert.Equal(1, scheduler.Pending);
        Assert.True(scheduler.RunNext());
        Assert.Null(h[Main].State.SmartPreview);
        Assert.True(scheduler.RunNext());
        Assert.Equal(Tool.Ellipse, h[Main].State.SmartPreview?.Tool);
        Assert.Equal(0, scheduler.Pending);
    }

    [Fact]
    public void TheReleaseCandidateStaysUntilRecognitionFinishes()
    {
        var scheduler = new ManualScheduler();
        using CanvasHarness h = SmartHarness(scheduler);
        DrawEllipseWithoutRelease(h);
        while (scheduler.RunNext())
        {
        }
        Element candidate = h[Main].State.SmartPreview!.Value;
        h.Up(Main, Circle(120));
        Assert.Equal(Tool.Pen, h.Elements(Main)[0].Tool);
        Assert.Equal(candidate.Id, h[Main].State.SmartPreview?.Id);
        Assert.Contains(candidate.Id, h[Main].State.FadedIds);
        Assert.Equal(1, scheduler.Pending);
        Assert.True(scheduler.RunNext());
        h.Pump();
        Assert.Null(h[Main].State.SmartPreview);
        Assert.Equal(Tool.Ellipse, h.Elements(Main)[0].Tool);
        Assert.Equal(candidate.Id, h[Main].State.Morph?.Preview?.Id);
    }

    [Fact]
    public void ANewGestureCancelsPendingRecognition()
    {
        var scheduler = new ManualScheduler();
        using CanvasHarness h = SmartHarness(scheduler);
        DrawEllipseWithoutRelease(h);
        h.Up(Main, Circle(120));
        h.Down(Main, new Point(700, 700));
        while (scheduler.RunNext())
        {
        }
        h.Up(Main, new Point(700, 700));
        Assert.All(h.Elements(Main), element => Assert.Equal(Tool.Pen, element.Tool));
    }

    [Fact]
    public void StrokesStayInkWithoutSmartDraw()
    {
        using CanvasHarness h = Single();
        h.Choose(Tool.Pen);
        DrawEllipseWithoutRelease(h);
        Assert.Null(h[Main].State.SmartPreview);
        h.Up(Main, Circle(120));
        Assert.Equal(Tool.Pen, h.Elements(Main)[0].Tool);
        Assert.Null(h[Main].State.Morph);
    }

    [Fact]
    public void SnapOutlinesKeepClosedShapesClosedAndArrowsOpen()
    {
        // SmartDrawGestureTests.testSnapOutlinesKeepClosedShapesClosedAndArrowsOpen.
        Point[] ink = [.. Enumerable.Range(0, 41).Select(index =>
            new Point(100 + 50 * Math.Cos(index / 40.0 * 2 * Math.PI), 100 + 30 * Math.Sin(index / 40.0 * 2 * Math.PI)))];
        ValueList<Point> from = SnapMorph.Resample(ink, 96);
        Assert.Equal(96, from.Count);
        foreach (Tool tool in new[] { Tool.Rectangle, Tool.Diamond, Tool.Ellipse })
        {
            var shape = new Element(tool, new Rect(50, 70, 100, 60)) { Rotation = 0.2 };
            ValueList<Point> outline = SnapMorph.SnapOutline(shape, 96) ?? throw new InvalidOperationException($"No outline for {tool}");
            ValueList<Point> aligned = SnapMorph.Align(outline, from, closed: true);
            Assert.Equal(96, aligned.Count);
            Assert.Equal(0, aligned[0].Distance(aligned[^1]), 3);
            double nearest = outline.Min(point => point.Distance(from[0]));
            Assert.Equal(nearest, aligned[0].Distance(from[0]), 3);
        }
        var arrow = new Element(Tool.Arrow, points: ValueList.Create<Point>([new(300, 100), new(100, 100)]));
        ValueList<Point> line = SnapMorph.Resample([new Point(100, 100), new Point(300, 100)], 96);
        ValueList<Point> path = SnapMorph.Align(SnapMorph.SnapOutline(arrow, 96)!.Value, line, closed: false);
        Assert.Equal(new Point(100, 100), path[0]);
        Assert.Equal(new Point(300, 100), path[^1]);
        Assert.Null(SnapMorph.SnapOutline(new Element(Tool.Star, new Rect(0, 0, 40, 40)), 96));
    }

    [Fact]
    public void MotionCurvesMatchTheMacTimingFunctions()
    {
        Assert.Equal(0, MotionCurves.Enter.Progress(0));
        Assert.Equal(1, MotionCurves.Enter.Progress(1));
        Assert.True(MotionCurves.Enter.Progress(0.5) > 0.8);
        Assert.True(MotionCurves.Exit.Progress(0.5) < 0.5);
        Assert.Equal(0.5, MotionCurves.Smooth(0.5));
        Assert.Equal(0, MotionCurves.Smooth(-1));
        Assert.Equal(1, MotionCurves.Smooth(2));
    }
}
