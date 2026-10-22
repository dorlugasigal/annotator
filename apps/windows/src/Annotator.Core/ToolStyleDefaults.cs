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
}
