using System.Collections.Immutable;
using System.Globalization;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Workspace.Toolbar;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests;

/// <summary>
/// The full properties panel's model (ContextualInspector.swift, StyleValueControl.swift and
/// NativeInspectorComponents.swift): which controls show, how the one row lines them up, what the fields
/// accept and how a change reaches the tool, the selection and history.
/// </summary>
public sealed class PropertiesPanelTests
{
    private static InspectorPlan PlanFor(Tool tool, Action<WorkspaceModel>? change = null)
    {
        WorkspaceModel model = Drawing();
        Assert.Equal(ToolChoice.Chosen, model.Choose(tool));
        change?.Invoke(model);
        return InspectorPlan.For(model.Snapshot);
    }

    [Fact]
    public void DrawingToolsShowTheMacBlocksInTheMacOrder()
    {
        InspectorItem[] stroke = [InspectorItem.Colors, InspectorItem.Width, InspectorItem.Pattern, InspectorItem.Roughness];
        Assert.Equal<InspectorItem>([.. stroke, InspectorItem.Pressure, InspectorItem.SmartDraw], PlanFor(Tool.Pen).Items);
        Assert.Equal<InspectorItem>([InspectorItem.Colors, InspectorItem.Width], PlanFor(Tool.Highlighter).Items);
        Assert.Equal<InspectorItem>(stroke, PlanFor(Tool.Line).Items);
        Assert.Equal<InspectorItem>([.. stroke, InspectorItem.Edges], PlanFor(Tool.Rectangle).Items);
        Assert.Equal<InspectorItem>([.. stroke, InspectorItem.Edges], PlanFor(Tool.Diamond).Items);
        Assert.Equal<InspectorItem>(stroke, PlanFor(Tool.Ellipse).Items);
        Assert.Equal<InspectorItem>([.. stroke, InspectorItem.GridSize], PlanFor(Tool.Grid).Items);
        Assert.Equal<InspectorItem>([.. stroke, InspectorItem.Axes], PlanFor(Tool.Axes).Items);
        Assert.Equal<InspectorItem>([InspectorItem.Width], PlanFor(Tool.Eraser).Items);
        Assert.Equal<InspectorItem>(
            [InspectorItem.StrokeColor, InspectorItem.Font, InspectorItem.TextSize, InspectorItem.Weight, InspectorItem.Alignment],
            PlanFor(Tool.Text).Items);

        InspectorPlan counter = PlanFor(Tool.Counter);
        Assert.Equal<InspectorItem>([InspectorItem.StrokeColor, InspectorItem.Opacity, InspectorItem.Caption], counter.Items);
        Assert.Equal("Click to add sequential numbered markers.", counter.Caption);
        InspectorPlan ruler = PlanFor(Tool.Ruler);
        Assert.Equal<InspectorItem>([InspectorItem.StrokeColor, InspectorItem.Width, InspectorItem.Opacity, InspectorItem.Caption], ruler.Items);
        Assert.Equal("Drag to measure the distance in source pixels.", ruler.Caption);
        InspectorPlan laser = PlanFor(Tool.Laser);
        Assert.Equal<InspectorItem>([InspectorItem.StrokeColor, InspectorItem.Width, InspectorItem.LaserFade, InspectorItem.Caption], laser.Items);
        Assert.Equal("Drag to point. The trail fades on its own.", laser.Caption);
        Assert.Equal("0.8 s", InspectorPlan.LaserFadeLabel(LaserTrail.DefaultLifetime));
        Assert.Equal("How long the trail stays before it fades: 0.5 s to 5.0 s.", InspectorPlan.LaserFadeHelp);

        InspectorPlan arrow = PlanFor(Tool.Arrow);
        Assert.Equal<InspectorItem>(stroke, arrow.Items);
        Assert.True(arrow.Arrow);
        Assert.False(PlanFor(Tool.Line).Arrow);
        Assert.Null(arrow.Context);
        Assert.Equal(InspectorBody.Layout, arrow.Body);
    }

    [Fact]
    public void FillStyleAppearsOnlyWithAVisibleFill()
    {
        InspectorPlan filled = PlanFor(Tool.Rectangle, model => model.ApplyStyle((ref Style style) => style.SetFillEnabled(true)));
        Assert.Equal<InspectorItem>(
            [InspectorItem.Colors, InspectorItem.Width, InspectorItem.Pattern, InspectorItem.Roughness, InspectorItem.FillStyle, InspectorItem.Edges],
            filled.Items);
        InspectorPlan clear = PlanFor(Tool.Rectangle, model =>
        {
            model.ApplyStyle((ref Style style) => style.SetFillEnabled(true));
            model.ApplyStyle((ref Style style) => style.Fill = style.Fill!.Value with { Alpha = 0 });
        });
        Assert.DoesNotContain(InspectorItem.FillStyle, clear.Items);
        Assert.NotEqual(filled.Key, clear.Key);
    }

    [Fact]
    public void SelectionsShowTheirContextAndUniformControls()
    {
        WorkspaceModel model = Drawing();
        var box = new Element(Tool.Rectangle, new Rect(10, 10, 100, 80));
        var oval = new Element(Tool.Ellipse, new Rect(200, 10, 100, 80));
        var line = new Element(Tool.Line, points: [new Point(10, 200), new Point(120, 220)]);
        AssertAccepted(model.Add(Primary, box, oval, line));
        model.Choose(Tool.Select);
        Assert.Equal(InspectorBody.SelectHint, InspectorPlan.For(model.Snapshot).Body);

        Select(model, box.Id);
        InspectorPlan single = InspectorPlan.For(model.Snapshot);
        Assert.Equal("Editing Rectangle", single.Context);
        Assert.Equal(Tool.Rectangle, single.Tool);
        Assert.Contains(InspectorItem.Edges, single.Items);

        Select(model, box.Id, oval.Id);
        Assert.Equal("Editing 2 selected objects", InspectorPlan.For(model.Snapshot).Context);

        Select(model, box.Id, line.Id);
        InspectorPlan mixed = InspectorPlan.For(model.Snapshot);
        Assert.Equal(Tool.Select, mixed.Tool);
        Assert.Equal<InspectorItem>([InspectorItem.StrokeColor, InspectorItem.FillColor, InspectorItem.Width, InspectorItem.Opacity], mixed.Items);

        AssertAccepted(model.Add(Left, new Element(Tool.Rectangle, new Rect(30, 40, 100, 80))));
        model.SelectAll();
        Assert.Equal("Editing 4 objects across displays", InspectorPlan.For(model.Snapshot).Context);
    }

    [Fact]
    public void ImagesEffectsAndCoversUseTheirOwnBodies()
    {
        WorkspaceModel model = Drawing();
        ValueList<byte> png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0];
        var image = new Element(Tool.Image, new Rect(10, 10, 100, 80)) { ImagePng = png };
        var blur = new Element(Tool.Blur, new Rect(200, 10, 100, 80));
        var cover = new Element(Tool.Cover, new Rect(400, 10, 100, 80));
        AssertAccepted(model.Add(Primary, image, blur, cover));
        model.Choose(Tool.Select);
        Select(model, image.Id);
        Assert.Equal(InspectorBody.Image, InspectorPlan.For(model.Snapshot).Body);
        Select(model, blur.Id);
        Assert.Equal(InspectorBody.Cover, InspectorPlan.For(model.Snapshot).Body);
        Select(model, cover.Id);
        Assert.Equal(InspectorBody.Cover, InspectorPlan.For(model.Snapshot).Body);
    }

    [Fact]
    public void PropertiesStayOnOneRowWithTheirControlsLinedUp()
    {
        // Stroke color (its swatch's middle 38 points down), Width (a named slider), Pattern and the axes' unnamed switch:
        // one row, 14 points apart, never wrapping, with every control's middle on one line.
        (double, double, double)[] items = [(65.5, 56, 38), (150, 56, 38), (60, 56, 38), (90, 20, 10)];
        var (frames, width, height) = PropertyLayout.Row(items);
        Assert.Equal([0, 79.5, 243.5, 317.5], frames.Select(frame => frame.X));
        Assert.Equal([0, 0, 0, 28], frames.Select(frame => frame.Y));
        Assert.Equal(407.5, width);
        Assert.Equal(56, height);
        Assert.All(frames.Zip(items), pair => Assert.Equal(38, pair.First.Y + pair.Second.Item3));
        // A short item above the line makes the row taller only as much as it reaches past the others.
        var (_, _, taller) = PropertyLayout.Row([(40, 56, 38), (40, 80, 60)]);
        Assert.Equal(60 + 20, taller);
    }

    [Fact]
    public void ChoicesAreSingleButtonsThatStepToTheNextOne()
    {
        Assert.Equal(["Solid", "Dashed", "Dotted"], InspectorPlan.PatternChoices.Select(choice => choice.Name));
        Assert.Equal(1, InspectorPlan.NextChoice(InspectorPlan.PatternChoices, StrokePattern.Solid));
        Assert.Equal(0, InspectorPlan.NextChoice(InspectorPlan.PatternChoices, StrokePattern.Dotted));
        Assert.Equal(1, InspectorPlan.NextChoice(InspectorPlan.RoughnessChoices, 0.0));
        // An unknown value shows as the first choice.
        Assert.Equal(1, InspectorPlan.NextChoice(InspectorPlan.RoughnessChoices, 1.5));
        Assert.Equal(["System", "Mono"], InspectorPlan.FontChoices.Select(choice => choice.Name));
        Assert.Equal(["Left", "Center", "Right"], InspectorPlan.AlignmentChoices.Select(choice => choice.Name));
        Assert.Equal(["Solid", "Hatch", "Crosshatch"], InspectorPlan.FillStyleChoices.Select(choice => choice.Name));
        Assert.Equal("Pattern: Solid. Click for dashed.", InspectorPlan.CycleHelp("Pattern", "Solid", "Dashed"));
        // Mac CycleOptions' accessibilityHint, which screen readers read.
        Assert.Equal("Changes to dashed", InspectorPlan.CycleHint("Dashed"));
        Assert.Equal("Blurred details can still be readable. Remove private info before you share.", InspectorPlan.CoverCaption);
    }

    [Fact]
    public void PreciseFieldsAcceptTheMacRangesAndWholeCounts()
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        Assert.Equal(3.5, StyleParameter.Width.Parse(" 3.5 ", invariant));
        Assert.Null(StyleParameter.Width.Parse("0.5", invariant));
        Assert.Null(StyleParameter.Width.Parse("41", invariant));
        Assert.Null(StyleParameter.Width.Parse("three", invariant));
        var comma = (CultureInfo)invariant.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        Assert.Equal(3.5, StyleParameter.Width.Parse("3,5", comma));
        Assert.Equal(0, StyleParameter.AxisTickCount.Parse("0", invariant));
        Assert.Null(StyleParameter.AxisTickCount.Parse("2.5", invariant));
        Assert.Null(StyleParameter.GridRows.Parse("0", invariant));
        Assert.Equal("Enter a whole number from 1 to 24.", StyleParameter.GridColumns.ValidationMessage);
        Assert.Equal("Enter 1 to 40.", StyleParameter.Width.ValidationMessage);
        Assert.Equal("Width: 1–40 pt", StyleParameter.Width.FieldHelp(Tool.Pen));
        Assert.Equal("Size: 1–40 pt", StyleParameter.Width.FieldHelp(Tool.Eraser));
        Assert.Equal("Blur: 0–100 ", StyleParameter.Effect.FieldHelp(Tool.Blur));
        Assert.Equal("Opacity: 0–100 %", StyleParameter.Opacity.FieldHelp(Tool.Counter));
        Assert.Equal("Slider: 0–40. Enter up to 100 in the number field for a stronger effect.", StyleParameter.Effect.SliderHelp(Tool.Pixelate));
        Assert.Equal((2.0, 100.0), StyleParameter.Effect.SliderRange(Tool.Distort));
        Assert.Equal("Ticks per direction", StyleParameter.AxisTickCount.Title(Tool.Axes));
        Assert.Equal(9, StyleParameter.AxisTickCount.Value(new Style()));
        Assert.Equal("28", StyleParameter.Format(28));
        Assert.Equal("3.1", StyleParameter.Format(3.125));
        var style = new Style();
        StyleParameter.GridRows.Apply(7, ref style);
        StyleParameter.Opacity.Apply(40, ref style);
        Assert.Equal(7, style.GridRows);
        Assert.Equal(0.4, style.Opacity, 12);
    }

    [Fact]
    public void ThePropertiesKeyTogglesLikeTheButtonAndSaysWhatItShows()
    {
        WorkspaceModel model = Drawing();
        model.ApplyPropertiesMode(ToolbarPropertiesMode.Shown);
        Assert.True(model.PerformShortcutAction(ShortcutAction.Properties));
        Assert.Equal(ToolbarPropertiesMode.Hidden, ToolbarPropertiesMode.From(model.Snapshot.Preferences));
        Assert.Equal("Properties: hidden.", model.Snapshot.Status);
        Assert.Equal(CanvasCommandResult.Done,
            model.Perform(new CanvasCommand(CanvasCommandKind.ShortcutAction, Primary) { Action = ShortcutAction.Properties }));
        Assert.Equal(ToolbarPropertiesMode.Shown, ToolbarPropertiesMode.From(model.Snapshot.Preferences));
        Assert.Equal("Properties: shown.", model.Snapshot.Status);
        model.CyclePropertiesMode();
        Assert.Equal(ToolbarPropertiesMode.Hidden, ToolbarPropertiesMode.From(model.Snapshot.Preferences));
        Assert.Equal("Properties: hidden.", model.Snapshot.Status);
    }

    [Fact]
    public void TheCanvasMenuOpensFullPropertiesInTheShownToolbar()
    {
        WorkspaceModel model = Drawing();
        model.ApplyPropertiesMode(ToolbarPropertiesMode.Hidden);
        model.HideTools();
        Assert.False(model.Snapshot.ToolsPresented);
        model.ShowFullProperties();
        Assert.True(model.Snapshot.ToolsPresented);
        Assert.Equal(ToolbarPropertiesMode.Shown, ToolbarPropertiesMode.From(model.Snapshot.Preferences));
    }

    [Fact]
    public void ArrowChoicesChangeTheToolAndEverySelectedArrowAsOneUndoStep()
    {
        WorkspaceModel model = Drawing();
        var first = new Element(Tool.Arrow, points: [new Point(10, 10), new Point(60, 40), new Point(120, 10)], style: Style.Defaults(Tool.Arrow));
        var second = new Element(Tool.Arrow, points: [new Point(10, 10), new Point(120, 90)], style: Style.Defaults(Tool.Arrow));
        AssertAccepted(model.Add(Primary, first));
        AssertAccepted(model.Add(Left, second));
        model.SelectAll();
        Assert.True(model.Snapshot.SelectionIsWorkspaceWide);
        Assert.Equal(Tool.Arrow, model.Snapshot.EffectiveTool);

        model.ApplyStyle((ref Style style) => style.Curved = true);
        model.ApplyStyle((ref Style style) => style.StartHead = ArrowHead.CircleOutline);
        model.ApplyStyle((ref Style style) => style.HeadScale = 0.5);
        Assert.True(model.Elements(Primary)[0].Style.Curved);
        Assert.Equal(ArrowHead.CircleOutline, model.Elements(Left)[0].Style.StartHead);
        Assert.Equal(0.5, model.Elements(Left)[0].Style.HeadScale);
        Assert.True(model.Snapshot.Preferences.ToolStyles["arrow"].Curved, "The arrow tool keeps the choice for new arrows.");

        model.Undo();
        Assert.Equal(0.75, model.Elements(Left)[0].Style.HeadScale);
        Assert.Equal(0.75, model.Elements(Primary)[0].Style.HeadScale);
        model.Undo();
        model.Undo();
        Assert.False(model.Elements(Primary)[0].Style.Curved);
        Assert.Equal(ArrowHead.None, model.Elements(Left)[0].Style.StartHead);
    }

    [Fact]
    public void SliderDragsAreOneUndoStep()
    {
        WorkspaceModel model = Drawing();
        var box = new Element(Tool.Rectangle, new Rect(10, 10, 100, 80));
        AssertAccepted(model.Add(Primary, box));
        model.Choose(Tool.Select);
        Select(model, box.Id);
        model.BeginStyleEdit();
        foreach (double width in new[] { 4.0, 6, 9 })
        {
            model.ApplyStyle((ref Style style) => StyleParameter.Width.Apply(width, ref style));
        }
        model.FinishStyleEdit();
        Assert.Equal(9, model.Elements(Primary)[0].Style.Width);
        model.Undo();
        Assert.Equal(box.Style.Width, model.Elements(Primary)[0].Style.Width);
    }

    [Fact]
    public void ADraftBelongsToOneDisplaySelectionAndTool()
    {
        // testDraftCannotLeakAcrossSelectionOrToolTransitions: a value control keeps a StyleEditTarget while a draft or
        // a drag lasts, applies the draft only while it matches and drops it when it no longer does.
        WorkspaceModel model = Drawing();
        var first = new Element(Tool.Rectangle, new Rect(10, 10, 100, 80));
        var second = new Element(Tool.Rectangle, new Rect(200, 10, 100, 80));
        var line = new Element(Tool.Line, points: [new Point(10, 200), new Point(120, 220)]);
        foreach (Element element in new[] { first, second, line })
        {
            AssertAccepted(model.Add(Primary, element));
        }
        model.Choose(Tool.Select);
        Select(model, first.Id);
        StyleEditTarget target = StyleEditTarget.Of(model.Snapshot);
        model.ApplyStyle((ref Style style) => style.Width = 6);
        Select(model, first.Id);
        Assert.True(target.Matches(model.Snapshot), "A new value, or the same selection again, keeps the target.");
        foreach (Guid[] other in new Guid[][] { [second.Id], [first.Id, second.Id], [line.Id], [] })
        {
            Select(model, other);
            Assert.False(target.Matches(model.Snapshot));
        }

        StyleEditTarget nothing = StyleEditTarget.Of(model.Snapshot);
        model.Perform(new CanvasCommand(CanvasCommandKind.SelectAll, Left));
        Assert.Equal(Left, model.Snapshot.ActiveDisplay);
        Assert.False(nothing.Matches(model.Snapshot), "Another display is another target.");

        StyleEditTarget select = StyleEditTarget.Of(model.Snapshot);
        Assert.Equal(ToolChoice.Chosen, model.Choose(Tool.Pen));
        Assert.False(select.Matches(model.Snapshot), "Another tool is another target.");
    }

    [Fact]
    public void TextLayoutChangesResizeSelectedTextToItsMeasuredSize()
    {
        // testCommittedTextRemeasuresWhenFontChangesAndUndoRestoresBounds (SelectedContentEditingTests).
        var measurer = new FixedMeasurer();
        WorkspaceModel model = new(new WorkspaceOptions
        {
            Displays = () => [PrimaryDisplay],
            Pointer = () => new Point(10, 10),
            TextMeasurer = measurer,
        });
        model.BeginDrawing();
        var note = new Element(Tool.Text, new Rect(100, 120, 80, 30), text: "Note", style: Style.Defaults(Tool.Text));
        AssertAccepted(model.Add(Primary, note));
        model.Choose(Tool.Select);
        Select(model, note.Id);

        model.ApplyStyle((ref Style style) => style.Color = Palette.Blue);
        Assert.Equal(note.Frame, model.Elements(Primary)[0].Frame);

        model.ApplyStyle((ref Style style) => style.FontSize = 44);
        Size expected = TextLayout.ContentSize("Note", TextFont.Resolve(model.Elements(Primary)[0].Style), measurer);
        Assert.Equal(new Rect(100, 120, expected.Width, expected.Height), model.Elements(Primary)[0].Frame);

        model.ApplyStyle((ref Style style) => style.FontName = "monospace");
        Size mono = TextLayout.ContentSize("Note", TextFont.Resolve(model.Elements(Primary)[0].Style), measurer);
        Assert.NotEqual(expected, mono);
        Assert.Equal(new Rect(100, 120, mono.Width, mono.Height), model.Elements(Primary)[0].Frame);
        model.Undo();
        Assert.Equal(new Rect(100, 120, expected.Width, expected.Height), model.Elements(Primary)[0].Frame);
    }

    [Fact]
    public void SamplesBuildTheMacElementsAndDrawThem()
    {
        RGBA ink = new(0, 0, 0, 0.85);
        Element thick = InspectorSample.Thickness(7).Element(ink);
        Assert.Equal(7 / 1.5, thick.Style.Width, 12);
        Assert.Equal(1, thick.Style.Color.Alpha);
        Assert.Equal(InspectorSample.Seed, thick.Seed);
        Assert.Throws<InvalidOperationException>(() => InspectorSample.Roughness(6).Element(ink));
        Assert.Throws<InvalidOperationException>(() => InspectorSample.Edges(rounded: true).Element(ink));
        Element solid = InspectorSample.Fill(FillPattern.Solid).Element(ink);
        Assert.Equal(0.5, solid.Style.Fill!.Value.Alpha);
        Element start = InspectorSample.ForHead(ArrowHead.Filled, start: true).Element(ink);
        Assert.Equal((Tool.Arrow, ArrowHead.Filled, ArrowHead.None, 0.35), (start.Tool, start.Style.StartHead, start.Style.EndHead, start.Style.HeadScale!.Value));
        Assert.Equal(0.75 * 0.35, InspectorSample.HeadSize(0.75).Element(ink).Style.HeadScale!.Value, 12);
        Element pressure = InspectorSample.Pressure(variable: true).Element(ink);
        Assert.Equal(5, pressure.Style.Width);
        Assert.Equal<double>([0.15, 0.4, 1, 0.6, 0.2], pressure.Pressures);

        var builder = new SceneBuilder(new FixedMeasurer());
        InspectorSample[] samples =
        [
            InspectorSample.Thickness(2), InspectorSample.ForPattern(StrokePattern.Dotted), InspectorSample.Roughness(6),
            InspectorSample.Edges(rounded: false), InspectorSample.Fill(FillPattern.Crosshatch), InspectorSample.Route(curved: true),
            InspectorSample.ForHead(ArrowHead.CardinalityZeroOrMany, start: false), InspectorSample.HeadSize(1), InspectorSample.Pressure(variable: true),
        ];
        foreach (InspectorSample sample in samples)
        {
            DisplayItem[] items = sample.Draw(ink, builder);
            Assert.Contains(items, item => item.Op is DisplayOp.StrokePath or DisplayOp.FillPath or DisplayOp.StrokeSegments);
        }
        Assert.Equal(2, InspectorSample.Route(curved: false).Draw(ink, builder).Length);
        Assert.Contains(InspectorSample.ForHead(ArrowHead.TriangleOutline, start: false).Draw(ink, builder),
            item => item.Op == DisplayOp.PushLayer && item.Path is not null);
        Assert.Contains(InspectorSample.Pressure(variable: true).Draw(ink, builder), item => item.Op == DisplayOp.StrokeSegments);
    }

    [Fact]
    public void RoughnessAndEdgesSamplesAreTheMacHandDrawnStrokes()
    {
        RGBA ink = new(0, 0, 0, 0.85);
        var builder = new SceneBuilder(new FixedMeasurer());
        GlyphStroke clean = Assert.Single(InspectorGlyph.Roughness(0));
        Assert.Equal(2, InspectorGlyph.Roughness(2).Length);
        Assert.Equal(clean.Start, InspectorGlyph.Roughness(2)[0].Start);
        Assert.Equal(clean.Segments, InspectorGlyph.Roughness(2)[0].Segments);
        GlyphStroke[] wild = InspectorGlyph.Roughness(6);
        Assert.Equal(2, wild.Length);
        Assert.DoesNotContain(clean.Start, wild.Select(stroke => stroke.Start));
        Assert.All(InspectorGlyph.Roughness(2).Concat(wild).Append(clean), stroke => Assert.Equal(3, stroke.Segments.Length));
        Assert.NotEqual(InspectorGlyph.Roughness(2)[0].Start, wild[0].Start);
        Assert.Equal(2, InspectorSample.Roughness(6).Draw(ink, builder).Length);
        Assert.Equal(2, InspectorSample.Roughness(2).Draw(ink, builder).Length);
        foreach (bool rounded in new[] { false, true })
        {
            GlyphStroke[] edges = InspectorGlyph.Edges(rounded);
            Assert.Equal(new[] { true, false }, edges.Select(stroke => stroke.Dotted));
            Assert.Equal(rounded, edges[1].Segments.Any(segment => segment.IsCurve));
            DisplayItem[] items = InspectorSample.Edges(rounded).Draw(ink, builder);
            Assert.Equal(2, items.Length);
            Assert.True(items[0].Stroke!.IsDashed);
            Assert.False(items[1].Stroke!.IsDashed);
            Assert.All(items, item => Assert.Equal(1, item.Paint!.Color.Alpha));
        }
    }

    [Fact]
    public void TheCurvedChoiceMakesTheCanvasDrawClickedArrowsAsCurves()
    {
        // The panel's Curved arrow button applies curved = true to the arrow tool; the canvas then builds the clicked
        // arrow from that style, and the scene draws its shaft as cubic curves through the points.
        using Canvas.CanvasHarness h = Canvas.CanvasHarness.Single();
        h.Choose(Tool.Arrow);
        h.Model.ApplyStyle((ref Style style) => style.Curved = true);
        h.Model.ApplyStyle((ref Style style) => style.EndHead = ArrowHead.Filled);
        foreach (Point point in new[] { new Point(100, 300), new Point(250, 120), new Point(420, 300) })
        {
            h.Click(Canvas.CanvasHarness.Main, point);
        }
        h[Canvas.CanvasHarness.Main].FinishLinearPath();
        h.Pump();
        Element arrow = Assert.Single(h.Elements(Canvas.CanvasHarness.Main));
        Assert.True(arrow.Style.Curved);
        Assert.Equal(ArrowHead.Filled, arrow.Style.EndHead);
        Assert.True(new SceneBuilder(new FixedMeasurer()).Path(arrow).HasCurves);
    }

    private static void Select(WorkspaceModel model, params Guid[] ids) =>
        AssertAccepted(model.Edit(Primary, document => document[0].Select(ids)));

    /// <summary>Text 0.6 em (monospaced 0.7 em) wide per character, 1.2 em high, with an ascent of 0.8 em and a descent of 0.2 em.</summary>
    private sealed class FixedMeasurer : ITextMeasurer
    {
        public Size Measure(string text, TextFont font) =>
            new(text.Length * font.Size * (font.Design == FontDesign.Monospace ? 0.7 : 0.6), font.Size * 1.2);

        public FontLineMetrics LineMetrics(TextFont font) => new(font.Size * 0.8, font.Size * 0.2, 0);
    }
}
