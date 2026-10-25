// Port of apps/macos/Tests/AnnotationCoreTests/FirstUseStyleTests.swift.
namespace Annotator.Core.Tests;

public class FirstUseStyleTests
{
    [Fact]
    public void NewProfilesStartDrawingToolsWithTheSunsetGradient()
    {
        var preferences = WorkspacePreferences.FirstUse;
        foreach (Tool tool in Tool.AllCases)
        {
            bool saved = preferences.ToolStyles.TryGetValue(tool.RawValue, out Style style);
            if (tool.StartsWithGradient)
            {
                Assert.True(saved, tool.RawValue);
                Assert.Equal(GradientPreset.Sunset.Gradient, style.StrokeGradient);
                Assert.Null(style.FillGradient);
                Assert.Equal(1.0, style.Color.Alpha);
                Assert.Equal(Style.Defaults(tool).Width, style.Width);
            }
            else
            {
                Assert.False(saved, tool.RawValue);
            }
        }
        Assert.True(Tool.Pen.StartsWithGradient && Tool.Rectangle.StartsWithGradient && Tool.Arrow.StartsWithGradient);
        Assert.True(Tool.Line.StartsWithGradient && Tool.Text.StartsWithGradient && Tool.Server.StartsWithGradient);
        Assert.False(Tool.Highlighter.StartsWithGradient || Tool.Eraser.StartsWithGradient || Tool.Blur.StartsWithGradient);
        Assert.False(Tool.Cover.StartsWithGradient || Tool.Image.StartsWithGradient || Tool.Counter.StartsWithGradient);
        Assert.Equal(0.75, Style.FirstUse(Tool.Arrow).HeadScale);

        var read = WorkspacePreferences.Read(preferences.Data());
        Assert.Equal(2, read.SchemaVersion);
        Assert.Equal(preferences.ToolStyles.Count, read.ToolStyles.Count);
        foreach (var (tool, style) in preferences.ToolStyles)
        {
            Assert.Equal(style, read.ToolStyles[tool]);
        }
    }

    [Fact]
    public void SavedProfilesWithoutStylesKeepSolidDefaults()
    {
        var read = WorkspacePreferences.Read(new WorkspacePreferences().Data());
        Assert.Empty(read.ToolStyles);
        Assert.Null(Style.Defaults(Tool.Pen).StrokeGradient);
        Assert.Equal(Palette.Coral, Style.Defaults(Tool.Pen).Color);
    }

    [Fact]
    public void GradientPresetsKeepTheirOrderAndAddSevenMore()
    {
        Assert.Equal(
            ["sunset", "lagoon", "ember", "spectrum", "dusk", "citrus", "ocean", "orchid", "aurora", "cherry",
             "forest", "copper", "neon", "tropic", "steel"],
            GradientPreset.AllCases.Select(preset => preset.RawValue).ToArray());
        Assert.Equal(
            ["Aurora", "Cherry", "Forest", "Copper", "Neon", "Tropic", "Steel"],
            new[] { GradientPreset.Aurora, GradientPreset.Cherry, GradientPreset.Forest, GradientPreset.Copper, GradientPreset.Neon, GradientPreset.Tropic, GradientPreset.Steel }.Select(preset => preset.Label).ToArray());
    }
}
