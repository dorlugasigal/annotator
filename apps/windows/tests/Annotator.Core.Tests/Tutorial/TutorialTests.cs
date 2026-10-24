// Port of apps/macos/Tests/AnnotationCoreTests/TutorialTests.swift.
using System.Collections.Immutable;
using Annotator.Core.Geometry;
using Annotator.Core.Tutorial;

namespace Annotator.Core.Tests.Tutorial;

public class TutorialTests
{
    private static TutorialObservation Look(TutorialMode mode = TutorialMode.UseApps, bool presented = true, Tool tool = Tool.Pen,
                                            RGBA? color = null, bool curved = false, ImmutableArray<TutorialElement> elements = default,
                                            ulong undo = 0, bool editing = false) =>
        new(mode, presented, tool, color ?? Palette.Coral, curved, elements.IsDefault ? [] : elements, undo, editing);

    private static TutorialElement Drawing(Tool tool, double x = 10, bool curved = false, int points = 2, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), tool, curved, points, new Rect(x, 10, 40, 30), 0);

    [Fact]
    public void StepsAndGoalsKeepTheirOrderAndRawValues()
    {
        Assert.Equal(["draw", "rectangle", "shape", "color", "arrow", "pen", "move", "undo", "useApps", "drawAgain", "hide"],
            TutorialSteps.All.Select(step => step.RawValue));
        Assert.Equal([TutorialGoal.ArrowTool, TutorialGoal.CurvedStyle, TutorialGoal.CurvedArrowDrawn], TutorialStep.Arrow.Goals);
        Assert.Equal([TutorialGoal.Hidden, TutorialGoal.Shown], TutorialStep.Hide.Goals);
        Assert.All(Enum.GetValues<TutorialGoal>(), goal => Assert.False(string.IsNullOrEmpty(goal.RawValue)));
    }

    [Fact]
    public void AWholeRunAcceptsKeysOrClicksBecauseOnlyTheResultCounts()
    {
        var progress = new TutorialProgress(Look());
        Assert.Equal(TutorialStep.Draw, progress.Step);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw)));
        Assert.Equal(TutorialStep.Rectangle, progress.Step);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Rectangle)));
        Assert.Equal(TutorialStep.Shape, progress.Step);

        TutorialElement box = Drawing(Tool.Rectangle);
        Assert.Equal(TutorialChange.None, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Rectangle, editing: true, elements: [box])));
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Rectangle, elements: [box])));
        Assert.Equal(TutorialStep.Color, progress.Step);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Rectangle, color: Palette.Blue, elements: [box])));
        Assert.Equal(TutorialStep.Arrow, progress.Step);

        Assert.Equal(TutorialChange.Goal, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Arrow, elements: [box])));
        Assert.Equal(TutorialGoal.CurvedStyle, progress.CurrentGoal);
        Assert.True(progress.IsGoalDone(0));
        Assert.False(progress.IsGoalDone(1));
        Assert.Equal(TutorialChange.Goal, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Arrow, curved: true, elements: [box])));
        TutorialElement straight = Drawing(Tool.Arrow, points: 2, curved: true);
        Assert.Equal(TutorialChange.None, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Arrow, curved: true, elements: [box, straight])));
        TutorialElement bent = Drawing(Tool.Arrow, points: 3, curved: true);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Arrow, curved: true, elements: [box, straight, bent])));
        Assert.Equal(TutorialStep.Pen, progress.Step);

        ImmutableArray<TutorialElement> drawn = [box, straight, bent];
        Assert.Equal(TutorialChange.Goal, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Pen, elements: drawn)));
        TutorialElement loop = Drawing(Tool.Pen);
        drawn = drawn.Add(loop);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Pen, elements: drawn)));
        Assert.Equal(TutorialStep.Move, progress.Step);

        Assert.Equal(TutorialChange.Goal, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Select, elements: drawn)));
        ImmutableArray<TutorialElement> moved = drawn.SetItem(0, box with { Frame = box.Frame.Translated(new Point(30, 5)) });
        Assert.Equal(TutorialChange.None, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Select, editing: true, elements: moved)));
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Select, elements: moved)));
        Assert.Equal(TutorialStep.Undo, progress.Step);

        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Select, elements: drawn, undo: 1)));
        Assert.Equal(TutorialStep.UseApps, progress.Step);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.UseApps, tool: Tool.Select, elements: drawn, undo: 1)));
        Assert.Equal(TutorialStep.DrawAgain, progress.Step);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Select, elements: drawn, undo: 1)));
        Assert.Equal(TutorialStep.Hide, progress.Step);
        Assert.Equal(TutorialChange.Goal, progress.Observe(Look(TutorialMode.UseApps, presented: false, elements: drawn, undo: 1)));
        Assert.Equal(TutorialChange.Finished, progress.Observe(Look(TutorialMode.Draw, elements: drawn, undo: 1)));
        Assert.True(progress.IsFinished);
        Assert.Null(progress.CurrentGoal);
        Assert.Equal(TutorialSteps.All, progress.Completed);
        Assert.Empty(progress.Skipped);
    }

    [Fact]
    public void AStateThatAlreadyHoldsCountsAtOnceButSomethingNewMustComeLater()
    {
        // Clicking Rectangle to start drawing finishes both steps in one look; the drawing it waits for next must be new.
        TutorialElement old = Drawing(Tool.Rectangle);
        var progress = new TutorialProgress(Look(elements: [old]));
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Rectangle, elements: [old])));
        Assert.Equal(TutorialStep.Shape, progress.Step);
        Assert.Equal([TutorialStep.Draw, TutorialStep.Rectangle], progress.Completed);
        Assert.Equal(TutorialChange.None, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Rectangle, elements: [old])));
    }

    [Fact]
    public void AnotherToolsColorIsNotAColorChange()
    {
        var progress = new TutorialProgress(Look(TutorialMode.Draw, tool: Tool.Rectangle));
        progress.Skip();
        Assert.Equal(TutorialStep.Color, progress.Step);
        Assert.Equal(TutorialChange.None, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Arrow, color: Palette.Yellow)));
        Assert.Equal(TutorialChange.None, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Arrow, color: Palette.Yellow)));
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Arrow, color: Palette.Blue)));
    }

    [Fact]
    public void SmartDrawShapesCountForThePenAndMovesNeedSelect()
    {
        var progress = new TutorialProgress(Look(TutorialMode.Draw, tool: Tool.Rectangle));
        while (progress.Step != TutorialStep.Pen)
        {
            progress.Skip();
        }
        Assert.Equal(TutorialChange.Goal, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Pen)));
        Assert.Equal(TutorialGoal.PenDrawn, progress.CurrentGoal);
        TutorialElement recognized = Drawing(Tool.Ellipse);
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Pen, elements: [recognized])));
        Assert.Equal(TutorialStep.Move, progress.Step);
        ImmutableArray<TutorialElement> erased = [recognized with { Frame = new Rect(0, 0, 5, 5) }];
        Assert.Equal(TutorialChange.None, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Eraser, elements: erased)));
        Assert.Equal(TutorialChange.Goal, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Select, elements: erased)));
        Assert.Equal(TutorialGoal.Moved, progress.CurrentGoal);
        ImmutableArray<TutorialElement> turned = [erased[0] with { Rotation = 0.4 }];
        Assert.Equal(TutorialChange.Step, progress.Observe(Look(TutorialMode.Draw, tool: Tool.Select, elements: turned)));
    }

    [Fact]
    public void HiddenToolsAreNotDrawingAndSkippingTheLastStepFinishes()
    {
        var progress = new TutorialProgress(Look(TutorialMode.Draw, presented: false));
        Assert.Equal(TutorialStep.Draw, progress.Step);
        while (progress.Step != TutorialStep.Hide)
        {
            Assert.Equal(TutorialChange.Step, progress.Skip());
        }
        // Drawings and tools are hidden already, so the step's first goal counts at once.
        Assert.Equal(TutorialGoal.Shown, progress.CurrentGoal);
        Assert.Equal(TutorialChange.None, progress.Observe(Look(presented: false)));
        Assert.Equal(TutorialChange.Finished, progress.Skip());
        Assert.True(progress.IsFinished);
        Assert.Equal(TutorialChange.Finished, progress.Observe(Look()));
        Assert.Equal(TutorialSteps.All, progress.Skipped);
    }

    [Fact]
    public void TheGhostArcsAndStaysWithinBounds()
    {
        var from = new Point(100, 100);
        var to = new Point(500, 100);
        Assert.Equal(from, TutorialMotion.TravelPoint(from, to, 0));
        Assert.Equal(to, TutorialMotion.TravelPoint(from, to, 1));
        Point middle = TutorialMotion.TravelPoint(from, to, 0.5);
        Assert.True(middle.Y > 100, "The arc bends to the right of travel.");
        Assert.InRange(TutorialMotion.TravelDuration(10), 0.45, 0.45);
        Assert.InRange(TutorialMotion.TravelDuration(5000), 1.1, 1.1);
        Assert.Equal(0.5, TutorialMotion.DragDuration(100));
        Assert.Equal(1.0, TutorialMotion.DragDuration(480));
        Assert.Equal(1.6, TutorialMotion.DragDuration(5000));
        Assert.Equal(TutorialMotion.TravelPoint(from, from, 0.3), from);
        IReadOnlyList<Point> smooth = TutorialMotion.Smooth([new Point(0, 0), new Point(50, -40), new Point(100, 0)], 10);
        Assert.Equal(21, smooth.Count);
        Assert.Equal(new Point(100, 0), smooth[^1]);
        Assert.Equal(49, TutorialMotion.Loop(new Point(0, 0), 40).Count);
    }

    [Fact]
    public void ConfettiPopsFromBothBottomCornersAndFallsAway()
    {
        var size = new Size(1440, 900);
        IReadOnlyList<ConfettiPiece> pieces = ConfettiBurst.Pieces(size);
        Assert.Equal(ConfettiBurst.PiecesPerCorner * 2, pieces.Count);
        Assert.Equal(pieces, ConfettiBurst.Pieces(size));
        Assert.All(pieces.Take(ConfettiBurst.PiecesPerCorner), piece => Assert.True(piece.Start.X < 30 && piece.Velocity.X > 0));
        Assert.All(pieces.Skip(ConfettiBurst.PiecesPerCorner), piece => Assert.True(piece.Start.X > size.Width - 30 && piece.Velocity.X < 0));
        Assert.All(pieces, piece => Assert.InRange(piece.Delay + piece.Lifetime, 0, ConfettiBurst.Duration));
        double highest = double.MaxValue;
        foreach (ConfettiPiece piece in pieces)
        {
            Assert.Null(ConfettiBurst.State(piece, piece.Delay - 0.01, size.Height));
            Assert.Null(ConfettiBurst.State(piece, piece.Delay + piece.Lifetime + 0.01, size.Height));
            for (double time = piece.Delay; time <= piece.Delay + piece.Lifetime; time += 0.05)
            {
                highest = Math.Min(highest, ConfettiBurst.State(piece, time, size.Height)!.Value.Position.Y);
            }
            ConfettiState end = ConfettiBurst.State(piece, piece.Delay + piece.Lifetime, size.Height)!.Value;
            Assert.Equal(0, end.Opacity, 9);
        }
        Assert.InRange(highest, size.Height * 0.1, size.Height * 0.6);
        ConfettiState first = ConfettiBurst.State(pieces[0], pieces[0].Delay, size.Height)!.Value;
        Assert.Equal(pieces[0].Start, first.Position);
    }

    [Fact]
    public void SplitMixMatchesItsReferenceValues()
    {
        var random = new SplitMix64(1234567);
        Assert.Equal(6457827717110365317UL, random.Next());
        Assert.Equal(3203168211198807973UL, random.Next());
        double value = new SplitMix64(0).NextDouble();
        Assert.InRange(value, 0, 1);
    }
}
