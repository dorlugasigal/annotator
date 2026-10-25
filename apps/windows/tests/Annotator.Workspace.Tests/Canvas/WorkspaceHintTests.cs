using Annotator.Core;
using Annotator.Core.Motion;
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
    public void TipsTextIsTheModeAndWhatEscapeDoesOnOneLine()
    {
        // Mac ToolbarShortcutSettingsTests and LocalZoomScrollTests ("Using apps", "Drawing").
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        Assert.Equal("Drawing  ·  Escape: use apps", WorkspaceHint.Text(model.Snapshot, hasTextEditor: false, hasActiveEdit: false));
        Assert.Equal("Drawing  ·  Escape finishes text", WorkspaceHint.Text(model.Snapshot, hasTextEditor: true, hasActiveEdit: true));
        Assert.Equal("Drawing  ·  Escape cancels the edit", WorkspaceHint.Text(model.Snapshot, false, hasActiveEdit: true));
        // Zoom off adds nothing; the old screen zoom line is gone.
        Assert.Equal("Drawing  ·  Escape: use apps", WorkspaceHint.Text(model.Snapshot, false, false, new ZoomHint(false, 1, string.Empty)));
        model.Choose(Tool.Highlighter);
        Assert.Equal("Drawing  ·  Escape: use apps\nHold Shift for a straight highlight.", WorkspaceHint.Text(model.Snapshot, false, false));
        // While zooming Escape stops nothing, so the first line says only the mode.
        Assert.Equal("Drawing\nHold Shift for a straight highlight.\nZoom 2.0×  ·  Ctrl+1: stop\nScroll",
            WorkspaceHint.Text(model.Snapshot, false, false, new ZoomHint(true, 2, "Scroll")));
        model.UseApps();
        Assert.Equal("Using apps  ·  Ctrl+Shift+2: draw", WorkspaceHint.Text(model.Snapshot, false, false));
        Assert.Equal("Using apps  ·  Ctrl+Shift+2: draw  ·  Escape cancels the edit", WorkspaceHint.Text(model.Snapshot, false, true));
        Assert.DoesNotContain("Turn off", WorkspaceHint.Text(model.Snapshot, false, false), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTipWakesOnWhatItSaysAndFadesAfterItsDwell()
    {
        // Mac HintSchedule: it wakes when it can show, the mode, the highlighter, zoom or the text editor changes.
        Assert.Equal(5.0, MotionTiming.HintDwellDuration);
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        DisplaySnapshot Target() => model.Snapshot.Display(model.Snapshot.ActiveDisplay!.Value)!;
        var schedule = new HintSchedule();
        Assert.True(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), hasTextEditor: false), 10));
        Assert.Equal(15, schedule.HidesAt);
        Assert.True(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 14.9));
        Assert.False(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 15));

        // Strokes (an active edit) and zoom values don't wake it; they aren't in its wake key.
        model.ApplyInkKey("r");
        Assert.False(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 16));

        // The highlighter wakes it, and so does a text editor.
        model.Choose(Tool.Highlighter);
        Assert.True(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 20));
        Assert.False(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 25.5));
        Assert.True(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), hasTextEditor: true), 26));
        Assert.False(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), true), 31));

        // A mode change wakes it.
        model.UseApps();
        Assert.True(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 40));
        Assert.False(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 45));

        // Turning tips off hides it at once; turning them back on wakes it.
        model.SetShortcutHints(false);
        Assert.False(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 46));
        model.SetShortcutHints(true);
        Assert.True(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, Target(), false), 47));
        // Without a display it can't show.
        Assert.False(schedule.Shows(WorkspaceHint.Wake(model.Snapshot, null, false), 48));
    }

    [Fact]
    public void WakeKeysIgnoreEditsAndMagnification()
    {
        WorkspaceModel model = Create();
        model.ShowToolbar();
        model.BeginDrawing();
        DisplaySnapshot target = model.Snapshot.Display(model.Snapshot.ActiveDisplay!.Value)!;
        HintWake wake = WorkspaceHint.Wake(model.Snapshot, target, false);
        Assert.Equal(new HintWake(Available: true, Drawing: true, Highlighter: false, Zooming: false, EditingText: false), wake);
        var schedule = new HintSchedule();
        Assert.True(schedule.Shows(wake with { Zooming = true }, 0));
        // The same key at another magnification is the same key: no wake.
        Assert.False(schedule.Shows(wake with { Zooming = true }, 6));
        Assert.True(schedule.Shows(wake, 7));
        // Unavailable keeps its last wake time, and hides.
        Assert.False(schedule.Shows(wake with { Available = false }, 8));
        Assert.Equal(7, schedule.WokeAt);
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

        const string text = "Drawing  ·  Escape: use apps\nHold Shift for a straight highlight.";
        var measurer = new FixedMeasurer();
        (DisplayList list, Rect box) = HintBox.Build(text, new Rect(16, 50, 540, 130), measurer, closable: false);
        // 2 lines of the 12-point medium font, 15 points each, and 7 above and below: 44 high.
        Assert.Equal(MacTextMetrics.LineHeight(12) * 2 + 14, box.Height);
        Assert.Equal(15, MacTextMetrics.LineHeight(12));
        Assert.Equal(Math.Ceiling(measurer.Width("Hold Shift for a straight highlight.", 12)) + 18, box.Width);
        Assert.Equal(new Point(16, 50), new Point(box.X, box.Y));
        DisplayItem shape = list.Items[0];
        Assert.Equal(DisplayOp.FillPath, shape.Op);
        Assert.Equal(new RGBA(0, 0, 0, 0.82), shape.Paint!.Color);
        TextRun[] lines = [.. list.Items.ToArray().Skip(1).Select(item => item.Text!)];
        Assert.Equal(2, lines.Length);
        Assert.Equal([57.0, 72.0], lines.Select(line => line.Frame.Y));
        Assert.Equal(57 + MacTextMetrics.Ascender(12), lines[0].Baseline, 9);
        Assert.All(lines, line => Assert.Equal(new TextFont(FontDesign.System, HintBox.Medium, 12), line.Font));
        Assert.All(lines, line => Assert.Equal(new RGBA(1, 1, 1), line.Paint.Color));
        Assert.All(lines, line => Assert.Equal(25, line.Frame.X));
        Assert.All(lines, line => Assert.Equal(box.Width - 18, line.Frame.Width));
    }

    [Fact]
    public void AClosableBoxLeavesRoomForTheCloseCross()
    {
        // Mac WorkspaceHint.draw(closable: true): 18 more points for the ✕, drawn at the right of the first line.
        const string text = "Drawing  ·  Escape: use apps";
        var measurer = new FixedMeasurer();
        (DisplayList list, Rect box) = HintBox.Build(text, new Rect(16, 50, 540, 130), measurer, closable: true);
        double textWidth = Math.Ceiling(measurer.Width(text, 12));
        Assert.Equal(textWidth + 18 + 18, box.Width);
        Assert.Equal(29, box.Height);
        TextRun line = list.Items[1].Text!;
        Assert.Equal(box.Width - 18 - 18, line.Frame.Width);
        DisplayItem cross = list.Items[^1];
        Assert.Equal(DisplayOp.StrokePath, cross.Op);
        Assert.Equal(new RGBA(1, 1, 1, 0.7), cross.Paint!.Color);
        Assert.Equal(1.5, cross.Stroke!.Width);
        Assert.Equal(LineCap.Round, cross.Stroke.Cap);
        Point center = HintBox.CloseCenter(box);
        Assert.Equal(new Point(box.MaxX - 9 - 3.5, 50 + 7 + 7.5), center);
        Assert.Equal(new Rect(center.X - 3.5, center.Y - 3.5, 7, 7), cross.Path!.Bounds);
        Assert.Equal(new Rect(center.X - 12, center.Y - 12, 24, 24), HintBox.CloseFrame(box));

        // A narrow view caps the box; the text keeps the room for the ✕.
        (_, Rect narrow) = HintBox.Build(text, new Rect(16, 14, 80, 130), measurer, closable: true);
        Assert.Equal(80, narrow.Width);
    }

    [Fact]
    public void TheCloseCrossTurnsTipsOffThroughTheCanvasCommand()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        model.ShowToolbar();
        model.BeginDrawing();
        Assert.Equal("Turn off tips", WorkspaceHint.CloseLabel);
        Assert.Equal("Turn them back on in Settings > General.", WorkspaceHint.CloseHelp);
        Assert.Equal(CanvasCommandResult.Done, model.Perform(new CanvasCommand(CanvasCommandKind.TurnOffTips, model.Snapshot.ActiveDisplay!.Value)));
        Assert.False(model.Snapshot.Preferences.ShowShortcutHints);
        Assert.False(WorkspaceHint.IsVisible(model.Snapshot, model.Snapshot.Display(model.Snapshot.ActiveDisplay!.Value)!));
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.False(Create(store: store).Snapshot.Preferences.ShowShortcutHints, "Turning tips off is saved like the Settings switch.");
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
