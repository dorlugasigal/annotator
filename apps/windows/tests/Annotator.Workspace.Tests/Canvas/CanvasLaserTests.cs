using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// The laser pointer on a canvas (Mac LaserPointerTests and DrawingCanvas's laser pointer): a trail that follows the drag,
/// fades on its own and never becomes part of the drawing.
/// </summary>
public sealed class CanvasLaserTests
{
    private static readonly Rect Bounds = MainDisplay.Bounds;

    [Fact]
    public void TheTrailFollowsTheDragAndFadesWithoutChangingTheDrawing()
    {
        using CanvasHarness h = Single();
        CanvasController canvas = h[Main];
        h.Choose(Tool.Laser);
        Assert.Equal(CanvasCursorKind.Laser, canvas.State.Cursor.Kind);
        Assert.Equal(LaserTrail.DefaultColor, canvas.State.Cursor.Color);
        Assert.Equal(LaserTrail.DefaultWidth, canvas.State.Cursor.Width);

        h.Now = 100;
        h.Down(Main, new Point(100, 100));
        Assert.True(canvas.State.Laser.IsDrawing);
        Assert.True(canvas.State.Laser.IsResting);
        Assert.False(canvas.State.HasTransitions);
        Assert.False(canvas.HasActiveEdit);
        Assert.Null(canvas.State.Draft);
        h.Now = 100.05;
        h.Move(Main, new Point(140, 100));
        Assert.True(canvas.State.HasTransitions);
        Assert.Equal(100.05, canvas.State.LaserTime);
        h.Now = 100.1;
        h.Move(Main, new Point(180, 110));
        // Frames are running, so the next frame draws this move rather than the pointer message.
        Assert.Equal(100.05, canvas.State.LaserTime);
        Assert.True(canvas.AdvanceTransitions(100.1));
        Assert.True(canvas.State.HasTransitions);
        Assert.Equal(100.1, canvas.State.LaserTime);
        LaserTrail.Stroke stroke = Assert.Single(canvas.State.Laser.Strokes);
        Assert.Equal(new[] { new Point(100, 100), new Point(140, 100), new Point(180, 110) }, stroke.Samples.Select(sample => sample.Point).ToArray());
        Assert.Equal(LaserTrail.DefaultColor, stroke.Color);
        Assert.Equal(LaserTrail.DefaultWidth, stroke.Width);

        h.Now = 100.15;
        h.Up(Main, new Point(200, 110));
        Assert.False(canvas.State.Laser.IsDrawing);
        Assert.True(canvas.State.HasTransitions);
        Assert.Empty(h.Elements(Main));
        Assert.False(h.Model.Snapshot.CanUndo);
        Assert.Equal(Tool.Laser, h.Model.Snapshot.Tool);
        Assert.Empty(h.Channel.Results);

        Assert.True(canvas.AdvanceTransitions(100.5));
        Assert.Equal(100.5, canvas.State.LaserTime);
        Assert.False(canvas.State.Laser.IsEmpty);
        // 0.8 seconds after the release every point has faded.
        Assert.False(canvas.AdvanceTransitions(101));
        Assert.True(canvas.State.Laser.IsEmpty);
        Assert.False(canvas.State.HasTransitions);
        Assert.Empty(h.Elements(Main));
    }

    [Fact]
    public void ATrailTakesTheToolsColorAndWidthAndANewPressStartsANewStroke()
    {
        Style blue = Style.Defaults(Tool.Laser);
        blue.Color = Palette.Blue;
        blue.Width = 10;
        using CanvasHarness h = Single(Store((Tool.Laser, blue)));
        CanvasController canvas = h[Main];
        h.Choose(Tool.Laser);
        Assert.Equal(new CanvasCursor(CanvasCursorKind.Laser, Tool.Laser, 10, Palette.Blue), canvas.State.Cursor);
        h.Down(Main, new Point(10, 10));
        h.Now += 0.02;
        h.Move(Main, new Point(60, 10));
        h.Up(Main, new Point(60, 10));
        h.Now += 0.1;
        h.Down(Main, new Point(300, 300));
        Assert.Equal(2, canvas.State.Laser.Strokes.Count);
        Assert.All(canvas.State.Laser.Strokes, stroke => Assert.Equal((Palette.Blue, 10.0), (stroke.Color, stroke.Width)));
        Assert.False(canvas.State.Laser.Strokes[0].Live);
        Assert.True(canvas.State.Laser.Strokes[1].Live);
    }

    [Fact]
    public void LeavingDrawOrChoosingAnotherToolLetsAHeldStrokeFade()
    {
        using CanvasHarness h = Single();
        CanvasController canvas = h[Main];
        h.Choose(Tool.Laser);
        h.Down(Main, new Point(100, 100));
        h.Now += 0.05;
        h.Move(Main, new Point(150, 100));
        h.Model.UseApps();
        h.Sync();
        Assert.False(canvas.IsDrawing);
        Assert.False(canvas.State.Laser.IsDrawing);
        Assert.False(canvas.State.Laser.IsEmpty);
        Assert.True(canvas.State.HasTransitions);

        h.Model.ResumeDrawing();
        h.Sync();
        h.Down(Main, new Point(100, 100));
        h.Now += 0.05;
        h.Move(Main, new Point(150, 100));
        Assert.True(canvas.State.Laser.IsDrawing);
        h.Choose(Tool.Pen);
        Assert.False(canvas.State.Laser.IsDrawing);
        // The pen's next stroke is the pen's; the fading trail stays out of the drawing.
        h.Drag(Main, new Point(400, 400), new Point(500, 450));
        Assert.Equal(Tool.Pen, Assert.Single(h.Elements(Main)).Tool);
    }

    [Fact]
    public void EscapeAndCancelLetAHeldStrokeGo()
    {
        using CanvasHarness h = Single();
        CanvasController canvas = h[Main];
        h.Choose(Tool.Laser);
        h.Down(Main, new Point(100, 100));
        h.Now += 0.05;
        h.Move(Main, new Point(150, 100));
        canvas.CancelGesture();
        Assert.False(canvas.State.Laser.IsDrawing);
        Assert.False(canvas.State.Laser.IsEmpty);
        // Moves after the cancel draw nothing; the next press starts again.
        h.Move(Main, new Point(300, 300));
        Assert.Equal(2, canvas.State.Laser.Strokes[0].Samples.Count);
        h.Down(Main, new Point(400, 400));
        Assert.True(canvas.State.Laser.IsDrawing);
    }

    [Fact]
    public void HidingTheDrawingsClearsTheTrailAtOnce()
    {
        using CanvasHarness h = Single();
        CanvasController canvas = h[Main];
        h.Choose(Tool.Laser);
        h.Down(Main, new Point(100, 100));
        h.Now += 0.05;
        h.Move(Main, new Point(150, 100));
        h.Model.HideTools();
        h.Sync();
        Assert.True(canvas.State.Laser.IsEmpty);
        Assert.False(canvas.State.HasTransitions);
        Assert.False(canvas.AdvanceTransitions(h.Now + 0.1));
    }

    [Fact]
    public void ATrailKeepsAboutOnePositionPerFrame()
    {
        using CanvasHarness h = Single();
        CanvasController canvas = h[Main];
        h.Choose(Tool.Laser);
        h.Down(Main, new Point(0, 100));
        long start = h.SampleTime;
        // One message with 20 positions a millisecond apart, as Windows' mouse history gives for a fast drag.
        PointerSample[] fast = [.. Enumerable.Range(1, 20).Select(step => new PointerSample(new Point(step * 5, 100), 1, start + step))];
        h.Samples(Main, fast);
        // At most one every 8 ms, and always the newest, so a long Fade keeps its whole trail.
        Assert.Equal(new[] { 0.0, 40, 80, 100 }, Assert.Single(canvas.State.Laser.Strokes).Samples.Select(sample => sample.Point.X).ToArray());
    }

    [Fact]
    public void ARestingHeldPointerNeedsNoFrames()
    {
        using CanvasHarness h = Single();
        CanvasController canvas = h[Main];
        h.Choose(Tool.Laser);
        h.Down(Main, new Point(100, 100));
        h.Now += 0.1;
        h.Move(Main, new Point(140, 100));
        Assert.True(canvas.AdvanceTransitions(h.Now + 0.5));
        // Everything but the dot under the pointer has faded: the trail rests until the pointer moves or lets go.
        Assert.False(canvas.AdvanceTransitions(h.Now + 2));
        Assert.True(canvas.State.Laser.IsResting);
        Assert.False(canvas.State.HasTransitions);
        Assert.Equal(new LaserTrail.Dot(new Point(140, 100), LaserTrail.DefaultWidth / 2),
            Assert.Single(Assert.Single(canvas.State.Laser.Shapes(canvas.State.LaserTime)).Dots.ToArray()));
    }

    [Fact]
    public void TheTrailDrawsLastOnTheLiveLayerAboveCovers()
    {
        using CanvasHarness h = Single();
        h.SetDocument(Main, new Element(Tool.Cover, new Rect(80, 80, 200, 100)));
        var scene = new CanvasScene(new FixedTextMeasurer());
        scene.Update(h[Main].State, Bounds);
        Assert.Same(SceneSnapshot.Empty, scene.Live);
        Assert.Null(scene.Laser);

        h.Choose(Tool.Laser);
        h.Down(Main, new Point(100, 100));
        h.Now += 0.05;
        h.Move(Main, new Point(150, 120));
        Assert.Equal(CanvasLayers.Live, scene.Update(h[Main].State, Bounds) & CanvasLayers.Live);
        DisplayList laser = Assert.Single(scene.Live.Elements.ToArray());
        Assert.Same(scene.Laser, laser);
        Assert.Equal(LaserTrail.Passes.Count, laser.Items.Length);
        Assert.All(laser.Items.ToArray(), item => Assert.Equal(DisplayOp.FillPath, item.Op));

        // The same state keeps the same list; the next frame draws a new one.
        Assert.Equal(CanvasLayers.None, scene.Update(h[Main].State, Bounds));
        Assert.True(h[Main].AdvanceTransitions(h.Now + 0.3));
        scene.Update(h[Main].State, Bounds);
        Assert.NotSame(laser, scene.Laser);

        Assert.False(h[Main].AdvanceTransitions(h.Now + 5));
        h.Up(Main, new Point(150, 120));
        h[Main].AdvanceTransitions(h.Now + 10);
        scene.Update(h[Main].State, Bounds);
        Assert.Null(scene.Laser);
        Assert.Same(SceneSnapshot.Empty, scene.Live);
    }
}
