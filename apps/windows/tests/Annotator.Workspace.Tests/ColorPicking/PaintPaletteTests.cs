using Annotator.Core;
using Annotator.Workspace.ColorPicking;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.ColorPicking;

/// <summary>Ports of the Mac PaletteHexTests and the platform-neutral checks of ColorWorkflowTests.</summary>
public sealed class PaletteHexTests
{
    [Fact]
    public void RgbDefaultsToOpaqueAndAcceptsOptionalPrefixAndWhitespace()
    {
        RGBA color = PaletteHex.Parse(" \n#12abEF\t");
        Assert.Equal(new RGBA(18.0 / 255, 171.0 / 255, 239.0 / 255), color);
        Assert.Equal("12ABEFFF", PaletteHex.Format(color));
        Assert.False(PaletteHex.HasAlpha(" \n#12abEF\t"));
    }

    [Fact]
    public void RgbaKeepsColorAlphaIndependentOfObjectOpacity()
    {
        RGBA color = PaletteHex.Parse("12345680");
        Assert.Equal(128.0 / 255, color.Alpha);
        Assert.Equal("12345680", PaletteHex.Format(color));
        Assert.Equal(0, PaletteHex.Parse("00000000").Alpha);
        Assert.True(PaletteHex.HasAlpha("#12345680"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("GG1122")]
    [InlineData("##112233")]
    [InlineData("112233#")]
    [InlineData("+12345")]
    [InlineData("12 3456")]
    [InlineData("# 123456")]
    [InlineData("１２３４５６")]
    public void RejectsInvalidHexRatherThanTreatingItAsNoFill(string value)
    {
        var error = Assert.Throws<InvalidHexColorException>(() => PaletteHex.Parse(value));
        // The Mac shows CanvasError.invalidDocument's description, "The drawing could not be opened: " and then this; Windows
        // says only what to type.
        Assert.Equal("Use six RGB or eight RGBA hex digits.", error.Message);
    }

    [Fact]
    public void FormattingRoundsToNearestByte() => Assert.Equal("8000FF80", PaletteHex.Format(new RGBA(0.5, 0, 1, 0.5)));
}

public sealed class PaintPaletteTests
{
    /// <summary>The swatch labels of reference/ux-reference/macos/pickers/color-stroke, in order.</summary>
    [Fact]
    public void StrokeSwatchesHaveTheMacNamesAndHexValues()
    {
        string[] expected =
        [
            "Transparent stroke, hex #00000000", "Dark ink, hex #1F2629FF", "Slate gray, hex #545E63FF", "White, hex #FFFFFFFF",
            "Brown, hex #87665CFF", "Teal, hex #00969EFF", "Blue, hex #2B73E0FF", "Purple, hex #7D54B8FF", "Mauve, hex #B052A8FF",
            "Rose, hex #E34266FF", "Green, hex #389E59FF", "Emerald, hex #009E82FF", "Yellow, hex #F5E338FF",
            "Orange, hex #F29121FF", "Coral, hex #F04538FF",
        ];
        Assert.Equal(expected, PaintPalette.Colors(fill: false).Select((color, index) => PaintPalette.Label(color, PaintPalette.Name(index, fill: false))));
    }

    [Fact]
    public void TheFillPaletteStartsWithNoFill()
    {
        var colors = PaintPalette.Colors(fill: true);
        Assert.Equal(15, colors.Length);
        Assert.Null(colors[0]);
        Assert.Equal("None, no fill", PaintPalette.Label(colors[0], PaintPalette.Name(0, fill: true)));
        Assert.Equal("Coral, hex #F04538FF", PaintPalette.Label(colors[14], PaintPalette.Name(14, fill: true)));
    }

    [Fact]
    public void ShadesRunFromDarkerToLighterAndKeepAlpha()
    {
        var shades = PaintPalette.Shades(new RGBA(0.4, 0.6, 0.8, 0.5));
        Assert.Equal(5, shades.Length);
        Assert.Equal(new RGBA(0.2, 0.3, 0.4, 0.5), shades[0]);
        Assert.Equal(new RGBA(0.4, 0.6, 0.8, 0.5), shades[2]);
        Assert.Equal(new RGBA(0.4 + (1 - 0.4) * 0.5, 0.6 + (1 - 0.6) * 0.5, 0.8 + (1 - 0.8) * 0.5, 0.5), shades[4]);
        Assert.All(shades, shade => Assert.True(shade.IsValid));
        Assert.Equal("Darker shade of selected color", PaintPalette.ShadeName(0));
        Assert.Equal("Lighter shade of selected color", PaintPalette.ShadeName(4));
    }

    [Fact]
    public void SelectionComparesColorsAndVisibilityButNotOpacity()
    {
        RGBA coral = Palette.Coral;
        Assert.True(PaintPalette.IsSelected(coral, coral with { Alpha = 0.4 }, null));
        Assert.False(PaintPalette.IsSelected(coral, coral with { Alpha = 0 }, null));
        Assert.True(PaintPalette.IsSelected(new RGBA(0, 0, 0, 0), new RGBA(0, 0, 0, 0), null));
        Assert.False(PaintPalette.IsSelected(new RGBA(0, 0, 0, 0), new RGBA(0, 0, 0, 1), null));
        Assert.True(PaintPalette.IsSelected(null, null, null));
        Assert.False(PaintPalette.IsSelected(null, coral, null));
        Assert.False(PaintPalette.IsSelected(coral, coral, GradientPreset.Spectrum.Gradient));
    }

    /// <summary>Mac ColorWorkflowTests.testInlineWheelColorConversionAndPointerMapping.</summary>
    [Fact]
    public void InlineWheelColorConversionAndPointerMapping()
    {
        foreach (RGBA color in new[] { Palette.Ink, Palette.Coral, Palette.Blue, Palette.White, new RGBA(0, 0, 0), new RGBA(0.1, 0.75, 0.35) })
        {
            RGBA restored = new WheelColor(color).Color;
            Assert.Equal(color.Red, restored.Red, 0.00001);
            Assert.Equal(color.Green, restored.Green, 0.00001);
            Assert.Equal(color.Blue, restored.Blue, 0.00001);
        }
        var value = new WheelColor(Palette.White);
        value.Move(new Point(176, 88), 176, 176);
        Assert.Equal(new RGBA(1, 0, 0), value.Color);
        value.Move(new Point(0, 88), 176, 176);
        Assert.Equal(new RGBA(0, 1, 1), value.Color);
        value.Move(new Point(88, 88), 176, 176);
        Assert.Equal(Palette.White, value.Color);
    }

    [Fact]
    public void TheWheelKeepsItsHueForGraysAndItsSaturationForBlack()
    {
        var value = new WheelColor(new RGBA(0.1, 0.75, 0.35));
        double hue = value.Hue, saturation = value.Saturation;
        value.Follow(new RGBA(0.5, 0.5, 0.5));
        Assert.Equal(hue, value.Hue);
        Assert.Equal(0, value.Saturation);
        Assert.Equal(0.5, value.Brightness);
        value = new WheelColor(new RGBA(0.1, 0.75, 0.35));
        value.Follow(new RGBA(0, 0, 0));
        Assert.Equal(hue, value.Hue);
        Assert.Equal(saturation, value.Saturation);
        Assert.Equal(0, value.Brightness);
        value.Follow(Palette.Coral);
        Assert.Equal(new WheelColor(Palette.Coral), value);
    }
}

/// <summary>The popover's paint changes on the workspace model (Mac ColorWorkflowTests, model parts).</summary>
public sealed class PaintEditingTests
{
    /// <summary>Mac testGradientPaletteChangesOnlyTheSelectedPaintAndUndoesAsOneEdit.</summary>
    [Fact]
    public void GradientPaletteChangesOnlyTheSelectedPaintAndUndoesAsOneEdit()
    {
        WorkspaceModel model = Drawing();
        var shape = new Element(Tool.Rectangle, new Rect(50, 50, 180, 90), style: Style.Defaults(Tool.Rectangle));
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(shape);
            document[0].Select([shape.Id]);
        }));
        Element[] before = model.Elements(Primary);
        model.BeginStyleEdit();
        model.SetGradient(GradientPreset.Spectrum.Gradient, fill: true);
        model.ApplyStyle((ref Style style) => style.Opacity = 0.5);
        Assert.Equal(0.5, model.Snapshot.Style.Opacity);
        model.ResetObjectOpacity();
        Assert.Equal(1, model.Snapshot.Style.Opacity);
        model.FinishStyleEdit();
        Style styled = model.Elements(Primary)[0].Style;
        Assert.Equal(GradientPreset.Spectrum.Gradient, styled.FillGradient);
        Assert.Equal(0.4, styled.Fill?.Alpha);
        Assert.Equal(shape.Style.Color, styled.Color);
        model.Undo();
        Assert.Equal(before, model.Elements(Primary));
    }

    /// <summary>Mac testFillToggleCopiesTheStrokeGradientWithoutChangingStrokeOpacity.</summary>
    [Fact]
    public void FillToggleCopiesTheStrokeGradientWithoutChangingStrokeOpacity()
    {
        WorkspaceModel model = Create();
        model.Choose(Tool.Rectangle);
        model.ApplyStyle((ref Style style) =>
        {
            style.SetGradient(GradientPreset.Ocean.Gradient, fill: false);
            style.Color = style.Color with { Alpha = 0.8 };
        });
        Assert.Null(model.Snapshot.Style.Fill);
        model.SetFillEnabled(true);
        Assert.Equal(GradientPreset.Ocean.Gradient, model.Snapshot.Style.FillGradient);
        Assert.Equal(0.4, model.Snapshot.Style.Fill?.Alpha);
        Assert.Equal(0.8, model.Snapshot.Style.Color.Alpha);
        model.SetFillEnabled(false);
        Assert.Null(model.Snapshot.Style.Fill);
        Assert.Null(model.Snapshot.Style.FillGradient);
    }

    [Fact]
    public void MatchOutlineCopiesTheStrokeAgainAndKeepsTheFillOpacity()
    {
        WorkspaceModel model = Create();
        model.Choose(Tool.Ellipse);
        model.SetFillEnabled(true);
        model.SetPaintOpacity(0.7, fill: true);
        model.SetPaintColor(Palette.Blue, fill: false);
        model.MatchFillToStroke();
        Assert.Equal(Palette.Blue with { Alpha = 0.7 }, model.Snapshot.Style.Fill);
        Assert.Null(model.Snapshot.Style.FillGradient);
    }

    [Fact]
    public void PaintOpacityChangesOnlyItsPaint()
    {
        WorkspaceModel model = Create();
        model.Choose(Tool.Rectangle);
        model.SetPaintOpacity(0.25, fill: false);
        Assert.Equal(0.25, model.Snapshot.Style.Color.Alpha);
        Assert.Null(model.Snapshot.Style.Fill);
        // Without a fill there is no fill opacity to change, as the Mac's disabled slider shows.
        model.SetPaintOpacity(0.9, fill: true);
        Assert.Null(model.Snapshot.Style.Fill);
        Assert.Equal(1, model.Snapshot.Style.Opacity);
    }

    [Fact]
    public void GradientsSkipCensorsAndFillsSkipShapesWithoutFill()
    {
        WorkspaceModel model = Drawing();
        var line = new Element(Tool.Line, new Rect(10, 10, 100, 40), style: Style.Defaults(Tool.Line));
        var box = new Element(Tool.Rectangle, new Rect(150, 10, 100, 60), style: Style.Defaults(Tool.Rectangle));
        AssertAccepted(model.Edit(Primary, document =>
        {
            document[0].Add(line);
            document[0].Add(box);
            document[0].Select([line.Id, box.Id]);
        }));
        model.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        Assert.All(model.Elements(Primary), element => Assert.Equal(GradientPreset.Sunset.Gradient, element.Style.StrokeGradient));
        model.SetFillEnabled(true);
        Element[] elements = model.Elements(Primary);
        Assert.Null(elements.Single(element => element.Tool == Tool.Line).Style.Fill);
        Assert.NotNull(elements.Single(element => element.Tool == Tool.Rectangle).Style.Fill);
    }
}
