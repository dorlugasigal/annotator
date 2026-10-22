using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// The scene and live layers an overlay presenter shows for a canvas (Mac DrawingCanvas.draw and SelectionOverlayView):
/// what each layer holds during gestures and which display lists stay the same.
/// </summary>
public sealed class CanvasSceneTests
{
    private static readonly Rect Bounds = MainDisplay.Bounds;

    [Fact]
    public void AStrokeGrowsOnTheLiveLayerAndMovesToTheSceneOnRelease()
    {
        using CanvasHarness h = Single();
        var scene = new CanvasScene(new FixedTextMeasurer());
        h.Choose(Tool.Pen);
        h.Down(Main, new Point(10, 10));
        Assert.Equal(CanvasLayers.Live, scene.Update(h[Main].State, Bounds));
        SceneSnapshot committed = scene.Scene;
        Assert.Same(SceneSnapshot.Empty, committed);
        Guid stroke = h[Main].State.Draft!.Value.Id;
        DisplayList previous = Assert.Single(scene.Live.Elements.ToArray());
        Assert.Equal(stroke, previous.ElementId);

        for (int step = 1; step <= 3; step++)
        {
            h.Move(Main, new Point(10 + step * 20, 10 + step * 5));
            Assert.Equal(CanvasLayers.Live, scene.Update(h[Main].State, Bounds));
            Assert.Same(committed, scene.Scene);
            DisplayList grown = Assert.Single(scene.Live.Elements.ToArray());
            Assert.Equal(stroke, grown.ElementId);
            Assert.NotSame(previous, grown);
            previous = grown;
        }

        h.Up(Main, new Point(80, 30));
        Assert.Equal(CanvasLayers.Scene | CanvasLayers.Live, scene.Update(h[Main].State, Bounds));
        Assert.Same(SceneSnapshot.Empty, scene.Live);
        Assert.Equal(stroke, Assert.Single(scene.Scene.Elements.ToArray()).ElementId);
    }

    [Fact]
    public void AnUnchangedStateChangesNothing()
    {
        using CanvasHarness h = Single();
        h.SetDocument(Main, new Element(Tool.Rectangle, new Rect(100, 100, 80, 60)), new Element(Tool.Ellipse, new Rect(300, 100, 50, 50)));
        var scene = new CanvasScene(new FixedTextMeasurer());
        Assert.Equal(CanvasLayers.Scene, scene.Update(h[Main].State, Bounds));
        SceneSnapshot committed = scene.Scene;
        Assert.Equal(2, committed.Elements.Length);

        Assert.Equal(CanvasLayers.None, scene.Update(h[Main].State, Bounds));
        h.Move(Main, new Point(700, 700), held: false);
        Assert.Equal(CanvasLayers.None, scene.Update(h[Main].State, Bounds));
        Assert.Same(committed, scene.Scene);
        Assert.Same(SceneSnapshot.Empty, scene.Live);
    }

    [Fact]
    public void CoversAndEffectsAreDrawnAgainAboveLiveDrawings()
    {
        using CanvasHarness h = Single();
        var cover = new Element(Tool.Cover, new Rect(200, 200, 100, 100));
        var drawing = new Element(Tool.Rectangle, new Rect(20, 20, 50, 50));
        h.SetDocument(Main, cover, drawing);
        var scene = new CanvasScene(new FixedTextMeasurer());
        scene.Update(h[Main].State, Bounds);
        DisplayList coverList = scene.Scene.Elements[^1];
        Assert.Equal(cover.Id, coverList.ElementId);

        h.Choose(Tool.Pen);
        h.Down(Main, new Point(150, 150));
        h.Move(Main, new Point(250, 250));
        scene.Update(h[Main].State, Bounds);
        DisplayList[] live = scene.Live.Elements.ToArray();
        Assert.Equal(2, live.Length);
        Assert.Equal(h[Main].State.Draft!.Value.Id, live[0].ElementId);
        Assert.Same(coverList, live[1]);
    }

    [Fact]
    public void LivePixelsLeaveEffectRegionsToTheEffectLayer()
    {
        using CanvasHarness h = Single();
        var blur = new Element(Tool.Blur, new Rect(200, 200, 100, 100));
        var drawing = new Element(Tool.Rectangle, new Rect(20, 20, 50, 50));
        var cover = new Element(Tool.Cover, new Rect(400, 200, 80, 80));
        h.SetDocument(Main, blur, drawing, cover);
        var scene = new CanvasScene(new FixedTextMeasurer());
        Assert.Equal(CanvasLayers.Scene, scene.Update(h[Main].State, Bounds));
        Assert.Equal([drawing.Id, blur.Id, cover.Id], scene.Scene.Elements.ToArray().Select(list => list.ElementId));
        DisplayList drawingList = scene.Scene.Elements[0];

        // The effect layer shows the region: the scene keeps the drawing and the cover and leaves the region out.
        Assert.Equal(CanvasLayers.Scene, scene.Update(h[Main].State, Bounds, pixelsAvailable: true));
        Assert.Equal([drawing.Id, cover.Id], scene.Scene.Elements.ToArray().Select(list => list.ElementId));
        Assert.Same(drawingList, scene.Scene.Elements[0]);
        Assert.Equal(CanvasLayers.None, scene.Update(h[Main].State, Bounds, pixelsAvailable: true));

        // Capture stops: the region is opaque in the scene again.
        Assert.Equal(CanvasLayers.Scene, scene.Update(h[Main].State, Bounds, pixelsAvailable: false));
        Assert.Equal([drawing.Id, blur.Id, cover.Id], scene.Scene.Elements.ToArray().Select(list => list.ElementId));
    }

    // Mac elementsForSharing and SharedRegionDrawingLayer: the shared region draws the canvas's own lists without covers
    // and effects, and an object being drawn joins once it is committed.
    [Fact]
    public void SharedContentReusesTheSceneListsWithoutCoversAndEffects()
    {
        using CanvasHarness h = Single();
        var blur = new Element(Tool.Blur, new Rect(200, 200, 100, 100));
        var drawing = new Element(Tool.Rectangle, new Rect(20, 20, 50, 50));
        var cover = new Element(Tool.Cover, new Rect(400, 200, 80, 80));
        h.SetDocument(Main, blur, drawing, cover);
        var scene = new CanvasScene(new FixedTextMeasurer());
        scene.Update(h[Main].State, Bounds);
        SharedCanvasContent shared = scene.Shared(h[Main].State);
        Assert.Equal([blur.Id, drawing.Id, cover.Id], shared.Elements.ToArray().Select(element => element.Id));
        Assert.Same(Assert.Single(shared.Drawings), scene.Scene.Elements[0]);
        Assert.Same(shared, scene.Shared(h[Main].State));

        h.Choose(Tool.Pen);
        h.Down(Main, new Point(150, 150));
        h.Move(Main, new Point(250, 250));
        scene.Update(h[Main].State, Bounds);
        SharedCanvasContent drawingStroke = scene.Shared(h[Main].State);
        Assert.Equal([drawing.Id], drawingStroke.Drawings.Select(list => list.ElementId));

        h.Up(Main, new Point(260, 260));
        scene.Update(h[Main].State, Bounds);
        SharedCanvasContent committed = scene.Shared(h[Main].State);
        Assert.Equal(2, committed.Drawings.Length);
        Assert.Equal(4, committed.Elements.Count);
        Assert.DoesNotContain(committed.Drawings, list => list.ElementId == blur.Id || list.ElementId == cover.Id);
    }
    [Fact]
    public void TheMarqueeIsADashedAccentOutlineWithoutCovers()
    {
        using CanvasHarness h = Single();
        h.SetDocument(Main, new Element(Tool.Cover, new Rect(200, 200, 100, 100)));
        var scene = new CanvasScene(new FixedTextMeasurer());
        h.Choose(Tool.Select);
        h.Down(Main, new Point(500, 500));
        h.Move(Main, new Point(620, 580));
        Assert.Equal(new Rect(500, 500, 120, 80), h[Main].State.Marquee);
        scene.Update(h[Main].State, Bounds);
        DisplayList marquee = Assert.Single(scene.Live.Elements.ToArray());
        DisplayItem outline = Assert.Single(marquee.Items.ToArray());
        Assert.Equal(DisplayOp.StrokePath, outline.Op);
        Assert.Same(Paint.Accent, outline.Paint);
        Assert.Equal(1, outline.Stroke!.Width);
        Assert.Equal(new[] { 4.0, 3.0 }, outline.Stroke.Dashes.ToArray());
        Assert.Equal(new Rect(500, 500, 120, 80), outline.Path!.Bounds);

        Assert.Equal(CanvasLayers.None, scene.Update(h[Main].State, Bounds));
        h.Up(Main, new Point(620, 580));
        scene.Update(h[Main].State, Bounds);
        Assert.Same(SceneSnapshot.Empty, scene.Live);
    }

    [Fact]
    public void EraserTargetsFadeInTheSceneAndShowDashedOutlines()
    {
        using CanvasHarness h = Single();
        var target = new Element(Tool.Rectangle, new Rect(100, 100, 80, 60));
        h.SetDocument(Main, target);
        var scene = new CanvasScene(new FixedTextMeasurer());
        h.Choose(Tool.Eraser);
        h.Down(Main, new Point(100, 130));
        Assert.Contains(target.Id, h[Main].State.EraserTargets);
        scene.Update(h[Main].State, Bounds);

        DisplayList faded = Assert.Single(scene.Scene.Elements.ToArray());
        Assert.Equal(DisplayOp.PushLayer, faded.Items[0].Op);
        Assert.Equal(0.22, faded.Items[0].Opacity);
        DisplayItem outline = Assert.Single(Assert.Single(scene.Live.Elements.ToArray()).Items.ToArray());
        Assert.Equal(new[] { 3.0, 3.0 }, outline.Stroke!.Dashes.ToArray());
        Assert.Equal(new Rect(96, 96, 88, 68), outline.Path!.Bounds);
    }

    [Fact]
    public void AClickedArrowBeingBuiltIsDrawnOnlyOnTheLiveLayer()
    {
        using CanvasHarness h = Single();
        var scene = new CanvasScene(new FixedTextMeasurer());
        h.Choose(Tool.Arrow);
        h.Click(Main, new Point(100, 100));
        h.Click(Main, new Point(200, 100));
        h.Move(Main, new Point(260, 160), held: false);
        CanvasLiveState state = h[Main].State;
        Guid arrow = state.Draft!.Value.Id;
        Assert.True(state.Elements.Contains(arrow));
        scene.Update(state, Bounds);
        Assert.DoesNotContain(scene.Scene.Elements.ToArray(), list => list.ElementId == arrow);
        Assert.Equal(arrow, Assert.Single(scene.Live.Elements.ToArray()).ElementId);
    }
}
