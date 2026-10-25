// Port of apps/macos/Sources/AnnotationCore/ToolStyleDefaults.swift.
namespace Annotator.Core;

public partial struct Style
{
    /// <summary>Swift `Style.defaults(for:)`.</summary>
    public static Style Defaults(Tool tool)
    {
        var style = new Style();
        if (tool == Tool.Arrow)
        {
            style.HeadScale = 0.75;
        }
        if (tool == Tool.Highlighter)
        {
            style.Color = new RGBA(0.93, 1, 0.05);
            style.Width = 4;
        }
        return style;
    }

    /// <summary>Swift `Style.firstUseGradient`: the gradient a brand-new profile starts with.</summary>
    public static GradientPreset FirstUseGradient => GradientPreset.Sunset;

    /// <summary>Swift `Style.firstUse(for:)`: the defaults, with the first-use gradient on drawing tools.</summary>
    public static Style FirstUse(Tool tool)
    {
        var style = Defaults(tool);
        if (tool.StartsWithGradient)
        {
            style.SetGradient(FirstUseGradient.Gradient, fill: false);
        }
        return style;
    }
}

public static class ToolFirstUseExtensions
{
    extension(Tool tool)
    {
        /// <summary>
        /// Swift `Tool.startsWithGradient`: shapes, lines, arrows, pen and text start with a gradient in a new profile. The
        /// highlighter keeps its yellow, and tools whose color is not ink keep their defaults.
        /// </summary>
        public bool StartsWithGradient => tool switch
        {
            Tool.Select or Tool.Highlighter or Tool.Eraser or Tool.Cover or Tool.Blur or Tool.Pixelate or Tool.Distort
                or Tool.Ruler or Tool.Counter or Tool.Image => false,
            _ => true,
        };
    }
}

public partial struct WorkspacePreferences
{
    /// <summary>Swift `WorkspacePreferences.firstUse`: preferences for someone with no saved profile.</summary>
    public static WorkspacePreferences FirstUse
    {
        get
        {
            var styles = EmptyStyles.ToBuilder();
            foreach (Tool tool in Tool.AllCases)
            {
                if (tool.StartsWithGradient)
                {
                    styles[tool.RawValue] = Style.FirstUse(tool);
                }
            }
            return new WorkspacePreferences { ToolStyles = styles.ToImmutable() };
        }
    }
}
