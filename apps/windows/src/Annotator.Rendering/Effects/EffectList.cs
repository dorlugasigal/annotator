using Annotator.Core;
using Annotator.Platform.Displays;

namespace Annotator.Rendering.Effects;

/// <summary>The pixel effects of specification 003.</summary>
public enum EffectKind
{
    Blur,
    Pixelate,
    Distort,
}

/// <summary>
/// One effect region in device pixels: the Mac's <c>CaptureCompositor.pixelRect</c> of the element's frame and its
/// strength in points (the effect amount, 18 when the element has none).
/// </summary>
public readonly record struct EffectRegion(EffectKind Kind, PixelRect Rect, double Amount)
{
    /// <summary>Strength zero leaves the pixels unchanged: nothing is drawn (WIN-REND-02).</summary>
    public bool IsVisible => Amount > 0 && !Rect.IsEmpty;
}

/// <summary>
/// An image element composited into the scene the effects read, like the Mac's <c>includeImageLayers</c>: its
/// unrotated placement in device pixels (top-left origin, not rounded), its rotation around the placement's center and
/// its opacity. The image is drawn below every effect region, so a region over it shows it filtered.
/// </summary>
public sealed record EffectImageLayer(Guid Id, ValueList<byte> Png, double X, double Y, double Width, double Height, double Rotation, double Opacity)
{
    /// <summary>The device pixels the rotated placement can change, with one pixel for its antialiased edge.</summary>
    public PixelRect Bounds
    {
        get
        {
            double centerX = X + Width / 2, centerY = Y + Height / 2;
            double cos = Math.Abs(Math.Cos(Rotation)), sin = Math.Abs(Math.Sin(Rotation));
            double halfWidth = (Width * cos + Height * sin) / 2, halfHeight = (Width * sin + Height * cos) / 2;
            return new PixelRect((int)Math.Floor(centerX - halfWidth) - 1, (int)Math.Floor(centerY - halfHeight) - 1,
                                 (int)Math.Ceiling(centerX + halfWidth) + 1, (int)Math.Ceiling(centerY + halfHeight) + 1);
        }
    }
}

/// <summary>
/// What one display shows in its effect layer, built from the document on the overlay thread and read by the render
/// worker: the pixel-effect regions in document order, the solid covers and, when asked, image layers and a privacy
/// crop for the scene the effects read. Immutable; two lists with the same content, size and scale are equal, so an
/// unchanged document does no work.
/// </summary>
public sealed class EffectList : IEquatable<EffectList>
{
    /// <summary>The Mac's default effect amount.</summary>
    public const double DefaultAmount = 18;

    private readonly EffectRegion[] regions;
    private readonly PixelRect[] covers;
    private readonly EffectImageLayer[] layers;

    private EffectList(EffectRegion[] regions, PixelRect[] covers, EffectImageLayer[] layers, PixelRect? privacy, int width, int height, double scale)
    {
        this.regions = regions;
        this.covers = covers;
        this.layers = layers;
        Privacy = privacy;
        Width = width;
        Height = height;
        Scale = scale;
    }

    public static EffectList Empty { get; } = new([], [], [], null, 0, 0, 1);

    public ReadOnlySpan<EffectRegion> Regions => regions;

    /// <summary>Solid covers in device pixels. Pixels under them are black before any filter samples them, and they win over every effect.</summary>
    public ReadOnlySpan<PixelRect> Covers => covers;

    /// <summary>Image layers in document order, composited into the scene before the effects read it.</summary>
    public ReadOnlySpan<EffectImageLayer> ImageLayers => layers;

    /// <summary>
    /// The Mac's privacy crop in device pixels (rounded inward): the scene outside it is black before any effect
    /// reads it. Null for none.
    /// </summary>
    public PixelRect? Privacy { get; }

    /// <summary>The display's size in device pixels.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Device pixels per point.</summary>
    public double Scale { get; }

    /// <summary>
    /// Whether the display has any pixel-effect region, so capture must run. As on the Mac, a region of strength zero
    /// counts too: it is drawn black until capture is live and then shows the screen unchanged.
    /// </summary>
    public bool NeedsPixels => regions.Length > 0;

    /// <summary>Whether any region is drawn: a region of strength zero or outside the display is not.</summary>
    public bool HasVisibleRegions => Array.Exists(regions, region => region.IsVisible);

    /// <summary>
    /// The effect regions and covers of <paramref name="elements"/> on a display of <paramref name="pixelWidth"/> by
    /// <paramref name="pixelHeight"/> device pixels at <paramref name="scale"/>, like the Mac's
    /// <c>CaptureCompositor.image</c>. With <paramref name="includeImageLayers"/> the image elements become layers of
    /// the scene; <paramref name="privacyRegion"/> (in points) blackens the scene outside it. Invalid frames are
    /// refused, like the Mac's renderer, which fails the whole frame.
    /// </summary>
    /// <exception cref="ArgumentException">An effect, cover or image frame is not valid, an effect amount is negative, an
    /// image has no data, or the privacy region is not a rectangle of at least one pixel inside the display.</exception>
    public static EffectList From(ElementList elements, double scale, int pixelWidth, int pixelHeight, bool includeImageLayers = false,
                                  Rect? privacyRegion = null)
    {
        if (!double.IsFinite(scale) || !(scale > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The display scale must be finite and positive.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        List<EffectRegion>? regions = null;
        List<PixelRect>? covers = null;
        List<EffectImageLayer>? layers = null;
        for (var index = 0; index < elements.Count; index++)
        {
            ref readonly var element = ref elements[index];
            if (element.Tool == Tool.Cover)
            {
                var rect = PixelRect(element.Frame, scale, pixelWidth, pixelHeight);
                if (!rect.IsEmpty)
                {
                    (covers ??= []).Add(rect);
                }
            }
            else if (element.Tool.IsPixelEffect)
            {
                var kind = element.Tool switch
                {
                    Tool.Blur => EffectKind.Blur,
                    Tool.Pixelate => EffectKind.Pixelate,
                    _ => EffectKind.Distort,
                };
                var amount = element.Style.EffectAmount ?? DefaultAmount;
                if (!double.IsFinite(amount) || amount < 0)
                {
                    throw new ArgumentException("An effect amount must be finite and not negative.", nameof(elements));
                }
                (regions ??= []).Add(new EffectRegion(kind, PixelRect(element.Frame, scale, pixelWidth, pixelHeight), amount));
            }
            else if (includeImageLayers && element.Tool == Tool.Image)
            {
                if (element.ImagePng is not { } png)
                {
                    throw new ArgumentException("An image element has no image data.", nameof(elements));
                }
                var frame = element.Frame;
                if (!frame.IsValid || !double.IsFinite(element.Rotation) || !double.IsFinite(element.Style.Opacity))
                {
                    throw new ArgumentException("An image frame is not a valid rectangle.", nameof(elements));
                }
                (layers ??= []).Add(new EffectImageLayer(element.Id, png, frame.X * scale, frame.Y * scale, frame.Width * scale,
                                                         frame.Height * scale, element.Rotation, Math.Clamp(element.Style.Opacity, 0, 1)));
            }
        }
        var privacy = privacyRegion is { } crop ? PrivacyRect(crop, scale, pixelWidth, pixelHeight) : (PixelRect?)null;
        return new EffectList(regions?.ToArray() ?? [], covers?.ToArray() ?? [], layers?.ToArray() ?? [], privacy, pixelWidth, pixelHeight, scale);
    }

    /// <summary>
    /// The Mac's <c>CaptureCompositor.pixelRect</c>: the frame times the scale, rounded outward to whole pixels and
    /// clipped to the display. Core Image's upward y axis gives the same rows for a whole-pixel display height.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="frame"/> is not a valid rectangle.</exception>
    public static PixelRect PixelRect(Rect frame, double scale, int pixelWidth, int pixelHeight)
    {
        if (!frame.IsValid)
        {
            throw new ArgumentException("An effect or cover frame is not a valid rectangle.", nameof(frame));
        }
        var left = Math.Floor(frame.X * scale);
        var top = Math.Floor(frame.Y * scale);
        var right = Math.Ceiling(frame.MaxX * scale);
        var bottom = Math.Ceiling(frame.MaxY * scale);
        var clipped = new PixelRect(
            (int)Math.Clamp(left, 0, pixelWidth),
            (int)Math.Clamp(top, 0, pixelHeight),
            (int)Math.Clamp(right, 0, pixelWidth),
            (int)Math.Clamp(bottom, 0, pixelHeight));
        return clipped.IsEmpty ? default : clipped;
    }

    /// <summary>
    /// The Mac's privacy crop: the region in points must lie inside the display and is rounded inward to whole pixels.
    /// </summary>
    /// <exception cref="ArgumentException">The region is not valid, not inside the display, or smaller than one pixel after rounding.</exception>
    public static PixelRect PrivacyRect(Rect region, double scale, int pixelWidth, int pixelHeight)
    {
        var full = new Rect(0, 0, pixelWidth / scale, pixelHeight / scale);
        if (!region.IsValid || !(region.Width > 0) || !(region.Height > 0) || !(region.X >= full.X && region.Y >= full.Y
            && region.MaxX <= full.MaxX && region.MaxY <= full.MaxY))
        {
            throw new ArgumentException("The privacy region must be a rectangle inside the display.", nameof(region));
        }
        var left = Math.Ceiling(region.X * scale);
        var top = Math.Ceiling(region.Y * scale);
        var right = Math.Floor(region.MaxX * scale);
        var bottom = Math.Floor(region.MaxY * scale);
        if (!(right > left) || !(bottom > top))
        {
            throw new ArgumentException("The privacy region is smaller than one pixel.", nameof(region));
        }
        return new PixelRect((int)left, (int)top, (int)right, (int)bottom);
    }

    public bool Equals(EffectList? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (Width == other.Width && Height == other.Height && Scale == other.Scale && Privacy == other.Privacy
                && regions.AsSpan().SequenceEqual(other.regions) && covers.AsSpan().SequenceEqual(other.covers)
                && layers.AsSpan().SequenceEqual(other.layers)));

    /// <summary>
    /// What the pixels of <paramref name="area"/> need from this list, in <paramref name="area"/>'s own pixels: the regions
    /// and covers that reach into it, moved by its origin and not clipped again, so a pixelate grid or a twirl keeps its
    /// center; image layers and the privacy crop moved too; and <paramref name="area"/>'s size. Filtering a copy of
    /// <paramref name="area"/> with it gives the same pixels as filtering the whole display, provided the privacy crop lies
    /// inside <paramref name="area"/> with at least one pixel of black around it wherever the area does not end at the
    /// display's edge (the shared region, WIN-CAP-08).
    /// </summary>
    internal EffectList Within(PixelRect area)
    {
        if (area.IsEmpty || area.Left < 0 || area.Top < 0 || area.Right > Width || area.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(nameof(area), area, "The area must be a rectangle inside the display.");
        }
        static bool Touches(PixelRect rect, PixelRect area) =>
            rect.Left < area.Right && rect.Right > area.Left && rect.Top < area.Bottom && rect.Bottom > area.Top;
        static PixelRect Move(PixelRect rect, PixelRect area) =>
            new(rect.Left - area.Left, rect.Top - area.Top, rect.Right - area.Left, rect.Bottom - area.Top);
        var movedRegions = new List<EffectRegion>(regions.Length);
        foreach (var region in regions)
        {
            if (Touches(region.Rect, area))
            {
                movedRegions.Add(region with { Rect = Move(region.Rect, area) });
            }
        }
        var movedCovers = new List<PixelRect>(covers.Length);
        foreach (var cover in covers)
        {
            if (Touches(cover, area))
            {
                movedCovers.Add(Move(cover, area));
            }
        }
        var movedLayers = new EffectImageLayer[layers.Length];
        for (var index = 0; index < layers.Length; index++)
        {
            movedLayers[index] = layers[index] with { X = layers[index].X - area.Left, Y = layers[index].Y - area.Top };
        }
        PixelRect? privacy = Privacy is { } crop ? Move(crop, area) : null;
        return new EffectList([.. movedRegions], [.. movedCovers], movedLayers, privacy, area.Width, area.Height, Scale);
    }

    public override bool Equals(object? obj) => obj is EffectList other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Width, Height, Scale, regions.Length, covers.Length, layers.Length);
}
