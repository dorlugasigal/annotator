// Port of apps/macos/Tests/AnnotationCoreTests/ColorPaintTests.swift.
using System.Text;

namespace Annotator.Core.Tests;

public class ColorPaintTests
{
    [Fact]
    public void ColorChannelsKeepIndependentOpacityAndNoFill()
    {
        var style = new Style();
        Assert.Equal(1.0, style.Color.Alpha);
        Assert.Null(style.Fill);
        style.SetPaintColor(Palette.Blue, fill: true);
        Assert.Equal(0.4, style.Fill?.Alpha);
        style.SetPaintOpacity(0.2, fill: true);
        style.SetPaintColor(Palette.Coral, fill: true);
        Assert.Equal(0.2, style.Fill?.Alpha);
        Assert.Equal(1.0, style.Color.Alpha);
        style.SetPaintOpacity(0.7, fill: false);
        style.SetPaintColor(Palette.Blue, fill: false);
        Assert.Equal(0.7, style.Color.Alpha);
        style.SetGradient(GradientPreset.Spectrum.Gradient, fill: true);
        Assert.Equal(0.2, style.Fill?.Alpha);
        style.SetPaintColor(null, fill: true);
        Assert.Null(style.Fill);
        Assert.Null(style.FillGradient);
        Assert.Equal(0.7, style.Color.Alpha);
        style.Validate();
    }

    [Fact]
    public void EnablingFillCopiesEachStrokePaintAtFortyPercentWithoutLinkingIt()
    {
        foreach (var gradient in new ColorGradient?[] { null, GradientPreset.Spectrum.Gradient })
        {
            var style = new Style();
            style.Color = new RGBA(0.2, 0.4, 0.8, 0.8);
            if (gradient is { } stroke)
            {
                style.SetGradient(stroke, fill: false);
            }
            style.SetFillEnabled(true);
            Assert.Equal(style.StrokeGradient, style.FillGradient);
            Assert.Equal(style.Color.Red, style.Fill?.Red);
            Assert.Equal(0.4, style.Fill?.Alpha);
            style.SetPaintOpacity(0.25, fill: true);
            style.SetPaintColor(Palette.White, fill: true);
            var customFill = style.Fill;
            style.SetFillEnabled(true);
            Assert.Equal(customFill, style.Fill);
            style.MatchFillToStroke();
            Assert.Equal(0.25, style.Fill?.Alpha);
            Assert.Equal(style.StrokeGradient, style.FillGradient);
            style.SetFillEnabled(false);
            Assert.True(style.Fill is null && style.FillGradient is null);
            Assert.Equal(0.8, style.Color.Alpha);
            style.Validate();
        }
    }

    [Fact]
    public void GradientsValidateAndRequireTheNewBoardFormat()
    {
        Assert.Equal(10, GradientPreset.AllCases.Count);
        foreach (var preset in GradientPreset.AllCases)
        {
            Assert.Equal(25.0, preset.Gradient.Angle);
            Assert.Equal(preset == GradientPreset.Spectrum ? 6 : 3, preset.Gradient.Colors.Count);
        }
        foreach (var preset in GradientPreset.AllCases)
        {
            preset.Gradient.Validate();
        }
        ColorGradient[] invalidGradients =
        [
            new([]), new([Palette.Blue]),
            new(ValueList.Repeat(Palette.Blue, 9)),
            new([Palette.Blue, new RGBA(1, 0, 0, 0.4)]),
            new([Palette.Blue, Palette.Coral], double.NaN),
        ];
        foreach (var gradient in invalidGradients)
        {
            Assert.Throws<CanvasError>(() => gradient.Validate());
        }
        var element = new Element(Tool.Rectangle, new Rect(0, 0, 100, 60));
        element.Style.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        element.Label = new ShapeLabel("Follow me", followsStroke: true);
        var document = new CanvasDocument([element]);
        var file = new DrawingFile(document);
        Assert.Equal(3, file.SchemaVersion);
        var data = file.Data();
        Assert.Equal([element], DrawingFile.Read(data).Elements);
        var downgraded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("\"schemaVersion\":3", "\"schemaVersion\":2", StringComparison.Ordinal));
        Assert.Throws<CanvasError>(() => DrawingFile.Read(downgraded));
        var selected = document;
        selected.Select([element.Id]);
        var saved = new SavedShape("Gradient label", selected);
        Assert.Equal([element], SavedShape.Read(saved.Data()).Elements);
    }

    [Fact]
    public void LegacyLabelsStayIndependentAndNewLabelsFollowStrokePaint()
    {
        var owner = new Style();
        owner.SetGradient(GradientPreset.Sunset.Gradient, fill: false);
        owner.SetPaintOpacity(0.8, fill: false);
        var legacy = new ShapeLabel("Legacy");
        Assert.Equal(Palette.Ink, legacy.ResolvedStyle(owner).Color);
        var linked = new ShapeLabel("Linked", followsStroke: true);
        Assert.Equal(0.8, linked.ResolvedStyle(owner).Color.Alpha);
        Assert.Equal(owner.StrokeGradient, linked.ResolvedStyle(owner).StrokeGradient);
        var preferences = new WorkspacePreferences();
        preferences.ToolStyles = preferences.ToolStyles.SetItem("rectangle", owner);
        var restored = WorkspacePreferences.Read(preferences.Data());
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal(owner, restored.ToolStyles["rectangle"]);
        var rectangle = new Element(Tool.Rectangle, new Rect(0, 0, 100, 60));
        rectangle.Label = linked;
        rectangle.Resize(new Rect(0, 0, 200, 120));
        Assert.True(rectangle.Label?.FollowsStroke == true);
    }
}
