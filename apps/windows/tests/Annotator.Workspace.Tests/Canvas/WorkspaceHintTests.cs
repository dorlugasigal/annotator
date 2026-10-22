using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Workspace.Canvas;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// The on-screen tip (Mac WorkspaceHint and WorkspaceHintView, UX reference dialogs/canvas-hint) and the Show tips on screen
/// preference.
/// </summary>
public sealed class WorkspaceHintTests
{
    [Fact]
    public void TipsTextFollowsTheModeToolAndEditAndEndsWithTheFooter()
    {
        // Mac ToolbarShortcutSettingsTests (tips end with the footer) and LocalZoomScrollTests ("Using apps", "Drawing").
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        string drawing = WorkspaceHint.Text(model.Snapshot, hasTextEditor: false, hasActiveEdit: false);
        Assert.Equal("Drawing  ·  Ctrl+Shift+2: draw\nEscape: use apps\nTurn off these tips in Settings > General.", drawing);
        Assert.EndsWith(WorkspaceHint.Footer, drawing, StringComparison.Ordinal);
        model.Choose(Tool.Highlighter);
        Assert.Contains("Hold Shift for a straight highlight.", WorkspaceHint.Text(model.Snapshot, false, false), StringComparison.Ordinal);
        Assert.Contains("Escape finishes text.", WorkspaceHint.Text(model.Snapshot, hasTextEditor: true, hasActiveEdit: true), StringComparison.Ordinal);
        Assert.Contains("Escape cancels the current edit.", WorkspaceHint.Text(model.Snapshot, false, hasActiveEdit: true), StringComparison.Ordinal);
        model.UseApps();
        string apps = WorkspaceHint.Text(model.Snapshot, false, false);
        Assert.StartsWith("Using apps", apps, StringComparison.Ordinal);
        Assert.DoesNotContain("Escape: use apps", apps, StringComparison.Ordinal);
        // The Mac reference's zoom line appears once zoom exists on Windows; Alt+scroll needs no permission (WIN-INP-08).
        Assert.Contains("Ctrl+1: screen zoom  ·  Alt+scroll starts zoom",
            WorkspaceHint.Text(model.Snapshot, false, false, new ZoomHint(false, 1, string.Empty)), StringComparison.Ordinal);
    }

    [Fact]
    public void TipsShowOnTheEditingTargetUntilThePreferenceTurnsThemOff()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        DisplaySnapshot target = model.Snapshot.Display(model.Snapshot.ActiveDisplay!.Value)!;
        DisplaySnapshot other = model.Snapshot.Displays.Single(display => display.Id != target.Id);
        Assert.True(WorkspaceHint.IsVisible(model.Snapshot, target));
        Assert.False(WorkspaceHint.IsVisible(model.Snapshot, other));
        model.UseApps();
        Assert.True(WorkspaceHint.IsVisible(model.Snapshot, model.Snapshot.Display(target.Id)!), "The Mac shows tips in Use apps too.");

        var store = new MemoryPreferencesStore();
        WorkspaceModel saved = Create(store: store);
        saved.ShowToolbar();
        saved.BeginDrawing();
        saved.SetShortcutHints(false);
        Assert.False(saved.Snapshot.Preferences.ShowShortcutHints);
        Assert.False(WorkspaceHint.IsVisible(saved.Snapshot, saved.Snapshot.Display(saved.Snapshot.ActiveDisplay!.Value)!));
        Assert.False(Create(store: store).Snapshot.Preferences.ShowShortcutHints, "Show tips on screen is saved in the profile.");
        saved.SetShortcutHints(true);
        Assert.True(saved.Snapshot.Preferences.ShowShortcutHints);
        saved.HideTools();
        Assert.False(WorkspaceHint.IsVisible(saved.Snapshot, saved.Snapshot.Displays[0]));
    }

    [Fact]
    public void TheBoxIsTheMacsFrameWithItsLineHeightsAndBaselines()
    {
        // WorkspaceHintView at (16, 50, 540, 130) below the Use apps button, (16, 14) without it; narrower canvases shrink it.
        Assert.Equal(new Rect(16, 50, 540, 130), HintBox.ViewFrame(1440, useAppsShown: true));
        Assert.Equal(new Rect(16, 14, 540, 130), HintBox.ViewFrame(1440, useAppsShown: false));
        Assert.Equal(new Rect(16, 14, 368, 130), HintBox.ViewFrame(400, useAppsShown: false));
        Assert.Equal(new Rect(16, 14, 80, 130), HintBox.ViewFrame(60, useAppsShown: false));

        const string text = "Drawing  ·  Ctrl+Shift+2: draw\nCtrl+1: screen zoom  ·  Alt+scroll starts zoom\nEscape: use apps\n" + WorkspaceHint.Footer;
        var measurer = new FixedMeasurer();
        (DisplayList list, Rect box) = HintBox.Build(text, new Rect(16, 50, 540, 130), measurer);
        // The Mac reference: 3 lines of 13 points and a 12-point footer, 7 above and below: 65 high.
        Assert.Equal(65, box.Height);
        Assert.Equal(Math.Ceiling(measurer.Width("Ctrl+1: screen zoom  ·  Alt+scroll starts zoom", 11)) + 18, box.Width);
        Assert.Equal(new Point(16, 50), new Point(box.X, box.Y));
        DisplayItem shape = list.Items[0];
        Assert.Equal(DisplayOp.FillPath, shape.Op);
        Assert.Equal(new RGBA(0, 0, 0, 0.78), shape.Paint!.Color);
        TextRun[] lines = [.. list.Items.ToArray().Skip(1).Select(item => item.Text!)];
        Assert.Equal(4, lines.Length);
        Assert.Equal([57.0, 70.0, 83.0, 96.0], lines.Select(line => line.Frame.Y));
        Assert.Equal(57 + MacTextMetrics.Ascender(11), lines[0].Baseline, 9);
        Assert.Equal(96 + MacTextMetrics.Ascender(10), lines[3].Baseline, 9);
        Assert.Equal(new TextFont(FontDesign.System, HintBox.Medium, 11), lines[0].Font);
        Assert.Equal(new TextFont(FontDesign.System, FontWeight.Regular, 10), lines[3].Font);
        Assert.Equal(new RGBA(1, 1, 1, 0.62), lines[3].Paint.Color);
        Assert.Equal(new RGBA(1, 1, 1), lines[1].Paint.Color);
        Assert.All(lines, line => Assert.Equal(25, line.Frame.X));
    }

    [Fact]
    public void ActionMessagesStayInTheWorkspaceAndOutOfTheTip()
    {
        // The Mac shows model.status nowhere since its 2026-09-24 interface pass; Windows keeps the message in the workspace
        // too, and errors go to the error window.
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        model.ApplyInkKey("r");
        Assert.Equal("Red ink", model.Snapshot.Status);
        Assert.DoesNotContain("Red ink", WorkspaceHint.Text(model.Snapshot, false, false, model.Snapshot.ZoomHint), StringComparison.Ordinal);
        model.Report("The file could not be read.");
        Assert.Equal("The file could not be read.", model.Snapshot.ErrorMessage);
        Assert.DoesNotContain("could not be read", WorkspaceHint.Text(model.Snapshot, false, false, model.Snapshot.ZoomHint), StringComparison.Ordinal);
    }
    /// <summary>A measurer with a fixed width per character, so the box's arithmetic is exact.</summary>
    private sealed class FixedMeasurer : ITextMeasurer
    {
        public double Width(string text, double size) => text.Length * size * 0.5;

        public Size Measure(string text, TextFont font) => new(Width(text, font.Size), font.Size * 1.33);

        public FontLineMetrics LineMetrics(TextFont font) => new(font.Size, font.Size * 0.25, 0);
    }
}
