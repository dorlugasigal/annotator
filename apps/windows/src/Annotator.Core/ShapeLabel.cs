// Port of apps/macos/Sources/AnnotationCore/ShapeLabel.swift.
namespace Annotator.Core;

/// <summary>
/// Text drawn inside a shape. Immutable, like the Swift final class, so history snapshots and
/// duplicated shapes can share one instance. Equality compares values.
/// </summary>
public sealed class ShapeLabel : IEquatable<ShapeLabel>
{
    /// <summary>Swift `ShapeLabel(text:style:followsStroke:)`. Without a style, the label is centered dark ink.</summary>
    public ShapeLabel(string text = "", Style? style = null, bool? followsStroke = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        FollowsStroke = followsStroke;
        var resolved = style ?? new Style();
        if (style is null)
        {
            resolved.Alignment = TextAlignment.Center;
            resolved.Color = Palette.Ink;
        }
        Style = resolved;
    }

    public string Text { get; }

    public Style Style { get; }

    public bool? FollowsStroke { get; }

    public long EstimatedBytes =>
        384 + SwiftText.Utf8Count(Text) + SwiftText.Utf8Count(Style.FontName) + Style.PaintEstimatedBytes;

    public bool HasVisibleText => SwiftText.TrimWhitespacesAndNewlines(Text).Length != 0;

    public Style ResolvedStyle(Style owner)
    {
        var resolved = Style;
        if (FollowsStroke == true)
        {
            resolved.Color = owner.Color;
            resolved.StrokeGradient = owner.StrokeGradient;
        }
        return resolved;
    }

    public static RGBA DefaultColor(RGBA? fill)
    {
        if (fill is not { } color || !(color.Alpha > 0.6))
        {
            return new RGBA(0, 0, 0);
        }
        static double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        var luminance = Linear(color.Red) * 0.2126 + Linear(color.Green) * 0.7152 + Linear(color.Blue) * 0.0722;
        return luminance < 0.179 ? Palette.White : new RGBA(0, 0, 0);
    }

    public void Validate()
    {
        if (SwiftText.Utf8Count(Text) > 1_000_000)
        {
            throw CanvasError.InvalidDocument("The shape label exceeds the supported text limit.");
        }
        Style.Validate();
    }

    public bool Equals(ShapeLabel? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (SwiftText.Equals(Text, other.Text) && Style == other.Style && FollowsStroke == other.FollowsStroke));

    public override bool Equals(object? obj) => Equals(obj as ShapeLabel);

    public override int GetHashCode() => HashCode.Combine(SwiftText.GetHashCode(Text), Style, FollowsStroke);

    public static bool operator ==(ShapeLabel? left, ShapeLabel? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(ShapeLabel? left, ShapeLabel? right) => !(left == right);
}
