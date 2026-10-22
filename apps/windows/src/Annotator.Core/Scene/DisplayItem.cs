using System.Globalization;
using Annotator.Core.Geometry;

namespace Annotator.Core.Scene;

/// <summary>The display-list operations. Each maps to one Direct2D 1.1 call or call pair.</summary>
public enum DisplayOp : byte
{
    /// <summary><c>FillGeometry</c>: <see cref="DisplayItem.Path"/> with <see cref="DisplayItem.FillRule"/> and <see cref="DisplayItem.Paint"/>.</summary>
    FillPath,

    /// <summary><c>DrawGeometry</c>: <see cref="DisplayItem.Path"/> with <see cref="DisplayItem.Stroke"/> and <see cref="DisplayItem.Paint"/>.</summary>
    StrokePath,

    /// <summary>
    /// <c>FillRectangle</c>: <see cref="DisplayItem.Rect"/> with <see cref="DisplayItem.Paint"/>. With
    /// <see cref="RectSnap.OutwardToPixels"/>, expand the rectangle to whole device pixels and fill it without
    /// antialiasing, so a protective cover never shows a partly covered edge pixel.
    /// </summary>
    FillRect,

    /// <summary>
    /// One <c>DrawLine</c> per segment of <see cref="DisplayItem.Segments"/> (pressure ink), each with its own width
    /// and with the dash pattern restarting at every segment, as the Mac strokes each segment separately.
    /// </summary>
    StrokeSegments,

    /// <summary>DirectWrite layout and <c>DrawTextLayout</c> of <see cref="DisplayItem.Text"/>.</summary>
    DrawText,

    /// <summary>
    /// <c>DrawBitmap</c> of <see cref="DisplayItem.Image"/>. When the image cannot be decoded or drawn, draw the next
    /// <see cref="ImageDraw.FallbackItemCount"/> items instead; otherwise skip them.
    /// </summary>
    DrawImage,

    /// <summary>Multiply <see cref="DisplayItem.Transform"/> in front of the current transform until the matching pop.</summary>
    PushTransform,

    PopTransform,

    /// <summary>
    /// <c>PushLayer</c>: content until the matching pop is composited at <see cref="DisplayItem.Opacity"/>, limited to
    /// <see cref="DisplayItem.LayerBounds"/> when set and masked by <see cref="DisplayItem.Path"/> with
    /// <see cref="DisplayItem.FillRule"/> when set (antialiased geometric mask). Values are in current user space.
    /// </summary>
    PushLayer,

    PopLayer,

    /// <summary>
    /// Clip to <see cref="DisplayItem.Rect"/> in current user space until the matching pop: <c>PushAxisAlignedClip</c>
    /// when the transform keeps axes aligned, otherwise a layer with a rectangle mask.
    /// </summary>
    PushClip,

    PopClip,
}

/// <summary>Pixel snapping for <see cref="DisplayOp.FillRect"/>.</summary>
public enum RectSnap : byte
{
    None,
    OutwardToPixels,
}

/// <summary>
/// One display-list operation: a small tagged value (no boxing; read the properties that belong to
/// <see cref="Op"/>). Payload objects are immutable and may be shared between lists.
/// </summary>
public readonly struct DisplayItem
{
    private readonly double d0, d1, d2, d3, d4, d5;
    private readonly object? payload;
    private readonly Paint? paint;
    private readonly StrokeStyle? stroke;
    private readonly byte flags;

    private DisplayItem(DisplayOp op, object? payload = null, Paint? paint = null, StrokeStyle? stroke = null,
                        FillRule fillRule = FillRule.NonZero, byte flags = 0,
                        double d0 = 0, double d1 = 0, double d2 = 0, double d3 = 0, double d4 = 0, double d5 = 0)
    {
        Op = op;
        this.payload = payload;
        this.paint = paint;
        this.stroke = stroke;
        FillRule = fillRule;
        this.flags = flags;
        this.d0 = d0;
        this.d1 = d1;
        this.d2 = d2;
        this.d3 = d3;
        this.d4 = d4;
        this.d5 = d5;
    }

    private const byte HasBoundsFlag = 1;
    private const byte SnapFlag = 2;

    public DisplayOp Op { get; }

    /// <summary>The fill rule of <see cref="DisplayOp.FillPath"/> and of a layer mask.</summary>
    public FillRule FillRule { get; }

    /// <summary>The path of <see cref="DisplayOp.FillPath"/> and <see cref="DisplayOp.StrokePath"/>, or a layer's mask.</summary>
    public PathData? Path => payload as PathData;

    public Paint? Paint => paint;

    public StrokeStyle? Stroke => stroke;

    /// <summary>The rectangle of <see cref="DisplayOp.FillRect"/> and <see cref="DisplayOp.PushClip"/>.</summary>
    public Rect Rect => new(d0, d1, d2, d3);

    public RectSnap Snap => (flags & SnapFlag) != 0 ? RectSnap.OutwardToPixels : RectSnap.None;

    /// <summary>The layer bounds of <see cref="DisplayOp.PushLayer"/>; null for an unbounded layer.</summary>
    public Rect? LayerBounds => Op == DisplayOp.PushLayer && (flags & HasBoundsFlag) != 0 ? new Rect(d0, d1, d2, d3) : null;

    /// <summary>The opacity of <see cref="DisplayOp.PushLayer"/>.</summary>
    public double Opacity => d4;

    /// <summary>The transform of <see cref="DisplayOp.PushTransform"/>.</summary>
    public AffineTransform Transform => new(d0, d1, d2, d3, d4, d5);

    public TextRun? Text => payload as TextRun;

    public ImageDraw? Image => payload as ImageDraw;

    public StrokeSegments? Segments => payload as StrokeSegments;

    public static DisplayItem FillPath(PathData path, FillRule rule, Paint paint) =>
        new(DisplayOp.FillPath, path, paint, fillRule: rule);

    public static DisplayItem StrokePath(PathData path, StrokeStyle stroke, Paint paint) =>
        new(DisplayOp.StrokePath, path, paint, stroke);

    public static DisplayItem FillRect(Rect rect, Paint paint, RectSnap snap = RectSnap.None) =>
        new(DisplayOp.FillRect, paint: paint, flags: snap == RectSnap.OutwardToPixels ? SnapFlag : (byte)0,
            d0: rect.X, d1: rect.Y, d2: rect.Width, d3: rect.Height);

    public static DisplayItem StrokeSegmentRun(StrokeSegments segments) =>
        new(DisplayOp.StrokeSegments, segments, segments.Paint, segments.Style);

    public static DisplayItem DrawText(TextRun text) => new(DisplayOp.DrawText, text, text.Paint);

    public static DisplayItem DrawImage(ImageDraw image) => new(DisplayOp.DrawImage, image);

    public static DisplayItem PushTransform(in AffineTransform transform) =>
        new(DisplayOp.PushTransform, d0: transform.A, d1: transform.B, d2: transform.C, d3: transform.D, d4: transform.Tx, d5: transform.Ty);

    public static DisplayItem PopTransform() => new(DisplayOp.PopTransform);

    public static DisplayItem PushLayer(double opacity, Rect? bounds = null, PathData? mask = null, FillRule maskRule = FillRule.NonZero)
    {
        var box = bounds ?? default;
        return new DisplayItem(DisplayOp.PushLayer, mask, fillRule: maskRule, flags: bounds is null ? (byte)0 : HasBoundsFlag,
                               d0: box.X, d1: box.Y, d2: box.Width, d3: box.Height, d4: opacity);
    }

    public static DisplayItem PopLayer() => new(DisplayOp.PopLayer);

    public static DisplayItem PushClip(Rect rect) =>
        new(DisplayOp.PushClip, d0: rect.X, d1: rect.Y, d2: rect.Width, d3: rect.Height);

    public static DisplayItem PopClip() => new(DisplayOp.PopClip);

    /// <summary>Exact equality of the operation and all its values; paths compare element by element.</summary>
    public bool ContentEquals(in DisplayItem other)
    {
        if (Op != other.Op || FillRule != other.FillRule || flags != other.flags || d0 != other.d0 || d1 != other.d1
            || d2 != other.d2 || d3 != other.d3 || d4 != other.d4 || d5 != other.d5
            || !Equals(paint, other.paint) || !Equals(stroke, other.stroke))
        {
            return false;
        }
        return payload switch
        {
            null => other.payload is null,
            PathData path => path.ContentEquals(other.payload as PathData),
            _ => payload.Equals(other.payload),
        };
    }

    public override string ToString() => Op switch
    {
        DisplayOp.FillPath => $"FillPath {FillRule} {Paint} [{Path}]",
        DisplayOp.StrokePath => $"StrokePath {Stroke} {Paint} [{Path}]",
        DisplayOp.FillRect => $"FillRect {Format(Rect)} {Paint}{(Snap == RectSnap.None ? "" : " snapped")}",
        DisplayOp.StrokeSegments => $"StrokeSegments {Segments!.Count} {Stroke} {Paint}",
        DisplayOp.DrawText => $"DrawText {Text}",
        DisplayOp.DrawImage => $"DrawImage {Image}",
        DisplayOp.PushTransform => $"PushTransform {Transform}",
        DisplayOp.PushLayer => $"PushLayer {Opacity.ToString(CultureInfo.InvariantCulture)} bounds {(LayerBounds is { } box ? Format(box) : "none")}"
            + (Path is null ? "" : $" mask {FillRule} [{Path}]"),
        DisplayOp.PushClip => $"PushClip {Format(Rect)}",
        _ => Op.ToString(),
    };

    private static string Format(Rect rect) =>
        string.Create(CultureInfo.InvariantCulture, $"({rect.X}, {rect.Y}, {rect.Width}, {rect.Height})");
}

/// <summary>
/// Text to lay out with DirectWrite and draw: the Mac's <c>NSString.draw(in:withAttributes:)</c> with a font,
/// a color and a paragraph alignment. Lines start at the top of <see cref="Frame"/>, with the first baseline at
/// <see cref="Baseline"/>; the alignment is absolute (left stays left in right-to-left text) and the base direction is
/// natural (first strong character).
/// </summary>
public sealed class TextRun : IEquatable<TextRun>
{
    /// <summary>Text drawn from the top of <paramref name="frame"/>, with its first baseline where the Mac's is (<see cref="MacTextMetrics.FirstBaseline"/>).</summary>
    public TextRun(string text, Rect frame, TextFont font, TextAlignment alignment, Paint paint)
        : this(text, frame, font, alignment, paint, MacTextMetrics.FirstBaseline(frame, font.Size))
    {
    }

    public TextRun(string text, Rect frame, TextFont font, TextAlignment alignment, Paint paint, double baseline)
    {
        Text = text;
        Frame = frame;
        Font = font;
        Alignment = alignment;
        Paint = paint;
        Baseline = baseline;
    }

    public string Text { get; }

    /// <summary>The layout box; alignment applies across its width.</summary>
    public Rect Frame { get; }

    public TextFont Font { get; }

    public TextAlignment Alignment { get; }

    /// <summary>A solid color whose alpha already includes the object's opacity, or the text's gradient.</summary>
    public Paint Paint { get; }

    /// <summary>
    /// Where the first line's baseline goes, in the space of <see cref="Frame"/>: where the Mac draws it (see
    /// <see cref="MacTextMetrics"/>). The other lines follow at the platform font's line spacing.
    /// </summary>
    public double Baseline { get; }

    /// <summary>
    /// Always false: lines break only at explicit line breaks (WIN-REND-03). The Mac's string drawing would also
    /// wrap at the frame width, which matters only when a frame is narrower than its content.
    /// </summary>
    public bool WordWrap => false;

    public bool Equals(TextRun? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (string.Equals(Text, other.Text, StringComparison.Ordinal) && Frame == other.Frame && Font == other.Font
                && Alignment == other.Alignment && Paint.Equals(other.Paint) && Baseline == other.Baseline));

    public override bool Equals(object? obj) => Equals(obj as TextRun);

    public override int GetHashCode() => HashCode.Combine(Text, Frame, Font, Alignment);

    public override string ToString() =>
        $"\"{Text}\" in {Frame} {Font} {Alignment} {Paint} baseline {Baseline.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>An image element's bitmap: decode <see cref="Png"/> (cached by the renderer) and draw it.</summary>
public sealed class ImageDraw : IEquatable<ImageDraw>
{
    public ImageDraw(Guid elementId, ValueList<byte> png, Rect destination, double opacity, ImageInterpolation interpolation,
                     int fallbackItemCount, Rect? source = null)
    {
        ElementId = elementId;
        Png = png;
        Destination = destination;
        Opacity = opacity;
        Interpolation = interpolation;
        FallbackItemCount = fallbackItemCount;
        Source = source;
    }

    public Guid ElementId { get; }

    public ValueList<byte> Png { get; }

    /// <summary>Where the whole image (or <see cref="Source"/>) is drawn, upright, in user space.</summary>
    public Rect Destination { get; }

    /// <summary>The part of the image to draw in image pixels; null for the whole image.</summary>
    public Rect? Source { get; }

    public double Opacity { get; }

    public ImageInterpolation Interpolation { get; }

    /// <summary>How many of the following items draw the replacement when the image cannot be shown.</summary>
    public int FallbackItemCount { get; }

    public bool Equals(ImageDraw? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (ElementId == other.ElementId && Png == other.Png && Destination == other.Destination
                && Source == other.Source && Opacity == other.Opacity && Interpolation == other.Interpolation
                && FallbackItemCount == other.FallbackItemCount));

    public override bool Equals(object? obj) => Equals(obj as ImageDraw);

    public override int GetHashCode() => HashCode.Combine(ElementId, Destination, Opacity);

    public override string ToString() => $"{ElementId} {Png.Count} bytes in {Destination} x {Opacity} {Interpolation}";
}
