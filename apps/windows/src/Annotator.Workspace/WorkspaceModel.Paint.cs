using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>The paint changes of the Mac's color popover (InspectorColorPalette), each through applyStyle.</summary>
public sealed partial class WorkspaceModel
{
    /// <summary>
    /// A gradient preset for the stroke or the fill. Censor and pixel-effect regions keep their paint, and the fill
    /// changes only on shapes that have one.
    /// </summary>
    public void SetGradient(ColorGradient gradient, bool fill)
    {
        using ChangeScope scope = Change();
        ApplyStyle(element => !element.Tool.IsProtection && (!fill || element.Tool.SupportsFill),
            (ref Style style) => style.SetGradient(gradient, fill));
    }

    /// <summary>
    /// Fill shape: on copies the stroke's color or gradient at the fill opacity (40% for a new fill); off removes the fill.
    /// </summary>
    public void SetFillEnabled(bool enabled)
    {
        using ChangeScope scope = Change();
        ApplyStyle(static element => element.Tool.SupportsFill, (ref Style style) => style.SetFillEnabled(enabled));
    }

    /// <summary>Match outline: the fill takes the stroke's color or gradient again and keeps its own opacity.</summary>
    public void MatchFillToStroke()
    {
        using ChangeScope scope = Change();
        ApplyStyle(static element => element.Tool.SupportsFill, static (ref Style style) => style.MatchFillToStroke());
    }

    /// <summary>
    /// Mac matchLabelToStroke, the stroke popover's Match outline while a shape label is edited: the label follows its shape's
    /// stroke color and gradient again (as a new label does) and keeps its own font, size, weight and alignment. The toolbar
    /// shows the stroke's paint at once; the canvas changes the label. Nothing changes without such a label, or when it follows
    /// the stroke already.
    /// </summary>
    public void MatchLabelToStroke()
    {
        using ChangeScope scope = Change();
        if (_active is not { Edit: { Kind: EditKind.Text, TextStyle: { } style, Label: { FollowsStroke: false } label } edit } editing)
        {
            return;
        }
        editing.Edit = edit with { TextStyle = label.Following(style), Label = label with { FollowsStroke = true } };
        Synchronize();
    }

    /// <summary>The stroke or fill opacity from 0 to 1; the object opacity stays.</summary>
    public void SetPaintOpacity(double opacity, bool fill)
    {
        using ChangeScope scope = Change();
        ApplyStyle(element => !fill || element.Tool.SupportsFill, (ref Style style) => style.SetPaintOpacity(opacity, fill));
    }

    /// <summary>Reset object opacity: the whole object at 100%; stroke and fill keep their own opacity.</summary>
    public void ResetObjectOpacity()
    {
        using ChangeScope scope = Change();
        ApplyStyle(static _ => true, static (ref Style style) => style.Opacity = 1);
    }
}
