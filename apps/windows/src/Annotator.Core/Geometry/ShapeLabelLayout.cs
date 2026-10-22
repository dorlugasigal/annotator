// Port of apps/macos/Sources/PresentationRendering/ShapeLabelLayout.swift. Text is measured on the rendering platform (D-4).
namespace Annotator.Core.Geometry;

public static class ShapeLabelLayout
{
    /// <summary>
    /// The text element drawn for a shape's label: content-sized, centered on the owner, rotated with it,
    /// in the label's resolved style with the owner's opacity applied. Null for non-shapes and missing labels.
    /// </summary>
    public static Element? TextElement(in Element owner, ITextMeasurer measurer, ShapeLabel? label = null)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        var resolvedLabel = label ?? owner.Label;
        if (resolvedLabel is null || !owner.Tool.IsShape)
        {
            return null;
        }
        var size = TextLayout.ContentSize(resolvedLabel.Text, TextFont.Resolve(resolvedLabel.Style), measurer);
        var style = resolvedLabel.ResolvedStyle(owner.Style);
        style.Opacity *= owner.Style.Opacity;
        var center = owner.Frame.Center;
        var text = new Element(Tool.Text,
            frame: new Rect(center.X - size.Width / 2, center.Y - size.Height / 2, size.Width, size.Height),
            text: resolvedLabel.Text, style: style, id: owner.Id);
        text.Rotation = owner.Rotation;
        return text;
    }

    /// <summary>The owner frame grown, around its center, so the label fits with 12 points on each side.</summary>
    public static Rect FittedOwnerFrame(in Element owner, ShapeLabel label, ITextMeasurer measurer)
    {
        if (TextElement(owner, measurer, label) is not { } text)
        {
            return owner.Frame;
        }
        var center = owner.Frame.Center;
        if (owner.Tool.SystemShapeAspectRatio is not null)
        {
            var box = owner.SymbolFrame;
            var scale = SwiftMath.Max(1, SwiftMath.Max((text.Frame.Width + 24) / SwiftMath.Max(1, box.Width),
                                                       (text.Frame.Height + 24) / SwiftMath.Max(1, box.Height)));
            return new Rect(center.X - owner.Frame.Width * scale / 2, center.Y - owner.Frame.Height * scale / 2,
                            owner.Frame.Width * scale, owner.Frame.Height * scale);
        }
        var width = SwiftMath.Max(owner.Frame.Width, text.Frame.Width + 24);
        var height = SwiftMath.Max(owner.Frame.Height, text.Frame.Height + 24);
        return new Rect(center.X - width / 2, center.Y - height / 2, width, height);
    }
}
