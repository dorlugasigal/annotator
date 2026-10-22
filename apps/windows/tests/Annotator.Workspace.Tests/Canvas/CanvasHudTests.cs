using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.Canvas.CanvasHarness;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// The canvas's Use apps button (Mac DrawingCanvas interactButton and HUDFader) and its accessibility texts, compared with
/// the Mac source and the UX reference's canvas states (WIN-PAR-06).
/// </summary>
public sealed class CanvasHudTests
{
    [Fact]
    public void TheUseAppsButtonHasTheMacFrameTitleAndLabel()
    {
        Assert.Equal(new Rect(16, 14, 150, 28), CanvasHud.UseAppsFrame);
        Assert.Equal(new Rect(16, 16, 150, 24), CanvasHud.UseAppsBezel);
        // AppKit's accessibility frame of the rounded button, as the UX reference's canvas states record it.
        Assert.Equal(new Rect(15, 13, 152, 30), CanvasHud.UseAppsAccessibleFrame);
        Assert.Equal("Use apps · Esc", CanvasHud.UseAppsTitle);
        Assert.Equal("Use apps", CanvasHud.UseAppsLabel);
        Assert.Equal((0.16, 0.12, 3.0), (CanvasHud.FadeInSeconds, CanvasHud.FadeOutSeconds, CanvasHud.Rise));
    }

    [Fact]
    public void TheButtonShowsOnlyWhileDrawing()
    {
        using CanvasHarness h = Single();
        Assert.True(CanvasHud.ShowsUseApps(h.Model.Snapshot));
        h.Model.UseApps();
        Assert.False(CanvasHud.ShowsUseApps(h.Model.Snapshot));
        h.Model.ResumeDrawing();
        Assert.True(CanvasHud.ShowsUseApps(h.Model.Snapshot));
        h.Model.HideTools();
        Assert.False(CanvasHud.ShowsUseApps(h.Model.Snapshot));
    }

    [Fact]
    public void TheButtonDrawsItsBezelAndCentersItsTitleOnTheMacBaseline()
    {
        DisplayList light = CanvasHud.UseAppsButton(dark: false);
        DisplayList dark = CanvasHud.UseAppsButton(dark: true);
        Assert.Equal([DisplayOp.FillPath, DisplayOp.StrokePath, DisplayOp.DrawText], light.Items.ToArray().Select(item => item.Op));
        TextRun title = light.Items[2].Text!;
        Assert.Equal(CanvasHud.UseAppsTitle, title.Text);
        Assert.Equal(TextAlignment.Center, title.Alignment);
        Assert.Equal(CanvasHud.UseAppsBezel.X, title.Frame.X);
        Assert.Equal(CanvasHud.UseAppsBezel.Width, title.Frame.Width);
        Assert.Equal(CanvasHud.UseAppsBezel.Center.Y, title.Frame.Center.Y, 9);
        // The Mac's title sits on a baseline 33 points from the canvas top (canvas states of the UX reference at 2x).
        Assert.InRange(title.Baseline, 32.5, 33.5);
        Assert.NotEqual(light.Items[0].Paint, dark.Items[0].Paint);
        Assert.NotEqual(light.Items[2].Paint, dark.Items[2].Paint);
    }

    [Fact]
    public void TheCanvasSummaryCountsObjectsAndSelection()
    {
        // DrawingCanvas.refreshAccessibilitySummary.
        Assert.Equal("1 object, 0 selected. Drawing.", CanvasAccessibility.Summary(1, 0, drawing: true));
        Assert.Equal("7 objects, 2 selected. Using apps.", CanvasAccessibility.Summary(7, 2, drawing: false));
        Assert.Equal("Annotation canvas", CanvasAccessibility.Label);
    }

    [Fact]
    public void TheSummaryCountsAnObjectStillBeingDrawnAsTheMacDoes()
    {
        using CanvasHarness h = Single();
        h.SetDocument(Main, new Element(Tool.Rectangle, new Rect(10, 10, 50, 40)));
        CanvasController canvas = h[Main];
        Assert.Equal((1, 0), CanvasAccessibility.Counts(canvas));
        // The Mac adds a new object to its document when the gesture starts (UX reference attach-hint: "7 objects").
        h.Choose(Tool.Arrow);
        h.Down(Main, new Point(200, 200));
        h.Move(Main, new Point(260, 230));
        Assert.NotNull(canvas.State.Draft);
        Assert.Equal(2, CanvasAccessibility.ObjectCount(canvas.Document!.Value.Elements, canvas.State));
        Assert.Equal(2, CanvasAccessibility.Counts(canvas).Objects);
        // Released, the arrow is in the document as this canvas shows it, before and after the workspace confirms it.
        h.Up(Main, new Point(260, 230));
        Assert.Equal(2, CanvasAccessibility.Counts(canvas).Objects);
        h.Pump();
        Assert.Equal(2, CanvasAccessibility.Counts(canvas).Objects);
        Assert.Equal(2, h.Elements(Main).Length);
    }
}
