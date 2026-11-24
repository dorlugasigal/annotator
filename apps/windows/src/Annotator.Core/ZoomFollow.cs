// Port of apps/macos/Sources/AnnotationCore/ZoomFollow.swift.
namespace Annotator.Core;

/// <summary>
/// Screen zoom while drawing. The zoomed part of the screen holds still while the pointer moves inside it, and slides
/// when the pointer comes near an edge of the display. Coordinates are display points with y down.
/// </summary>
public static class ZoomFollow
{
    /// <summary>Below this factor, the zoomed part doesn't slide.</summary>
    public const double MinimumSlidingFactor = 1.001;

    /// <summary>The longest step one frame may slide, so a stalled frame doesn't jump.</summary>
    public const double MaximumStep = 0.1;

    /// <summary>How deep the edge zone is along a side of <paramref name="length"/> points.</summary>
    public static double EdgeZone(double length) => SwiftMath.Min(96, SwiftMath.Max(24, length * 0.08));

    /// <summary>
    /// The zoom of <paramref name="factor"/> from the part shown before. When the factor changes, the point under the pointer
    /// stays under it; the part always stays inside the display. Without a part shown before, it starts where
    /// <see cref="Viewport.CursorAnchored"/> would.
    /// </summary>
    public static Viewport Viewport(Rect? previous, Rect display, double factor, Point pointer)
    {
        if (previous is not { } before || !before.IsValid || !(before.Width > 0) || !(before.Height > 0))
        {
            return Core.Viewport.CursorAnchored(display, factor, pointer);
        }
        if (!double.IsFinite(factor) || !(factor >= 1 && factor <= 16) || !pointer.IsFinite
            || !display.IsValid || !(display.Width > 0) || !(display.Height > 0))
        {
            throw CanvasError.InvalidGeometry;
        }
        double width = display.Width / factor, height = display.Height / factor;
        double px = SwiftMath.Min(display.MaxX, SwiftMath.Max(display.X, pointer.X));
        double py = SwiftMath.Min(display.MaxY, SwiftMath.Max(display.Y, pointer.Y));
        double x = before.X + (px - display.X) * (before.Width - width) / display.Width;
        double y = before.Y + (py - display.Y) * (before.Height - height) / display.Height;
        return new Viewport(display, new Rect(
            SwiftMath.Min(SwiftMath.Max(x, display.X), display.MaxX - width),
            SwiftMath.Min(SwiftMath.Max(y, display.Y), display.MaxY - height),
            width, height));
    }

    /// <summary>
    /// How fast the zoomed part slides, in display points per second on each axis, for the pointer at
    /// <paramref name="pointer"/>. It is zero away from the edges and off the display, and grows as the pointer goes deeper
    /// into an edge zone.
    /// </summary>
    public static Point Velocity(Rect display, double factor, Point pointer)
    {
        if (!double.IsFinite(factor) || !(factor > MinimumSlidingFactor) || !(factor <= 16) || !pointer.IsFinite
            || !display.IsValid || !(display.Width > 0) || !(display.Height > 0)
            || !(pointer.X >= display.X) || !(pointer.X <= display.MaxX)
            || !(pointer.Y >= display.Y) || !(pointer.Y <= display.MaxY))
        {
            return new Point(0, 0);
        }
        return new Point(Axis(display.X, display.Width, pointer.X, factor), Axis(display.Y, display.Height, pointer.Y, factor));
    }

    /// <summary>The zoomed part after sliding for <paramref name="elapsed"/> seconds (at most <see cref="MaximumStep"/>), kept inside the display.</summary>
    public static Viewport Slide(Viewport viewport, Point pointer, double elapsed)
    {
        Rect display = viewport.Output, source = viewport.Source;
        Point speed = Velocity(display, display.Width / source.Width, pointer);
        double step = double.IsFinite(elapsed) ? SwiftMath.Min(MaximumStep, SwiftMath.Max(0, elapsed)) : 0;
        if ((speed.X == 0 && speed.Y == 0) || !(step > 0))
        {
            return viewport;
        }
        return new Viewport(display, new Rect(
            SwiftMath.Min(SwiftMath.Max(source.X + speed.X * step, display.X), display.MaxX - source.Width),
            SwiftMath.Min(SwiftMath.Max(source.Y + speed.Y * step, display.Y), display.MaxY - source.Height),
            source.Width, source.Height));
    }

    /// <summary>Whether the zoomed part would slide now: the pointer is in an edge zone with room left in that direction.</summary>
    public static bool Slides(Viewport viewport, Point pointer)
    {
        try
        {
            return Slide(viewport, pointer, MaximumStep) != viewport;
        }
        catch (CanvasError)
        {
            return true;
        }
    }

    private static double Axis(double start, double length, double value, double factor)
    {
        double zone = EdgeZone(length);
        double low = SwiftMath.Min(1, SwiftMath.Max(0, (start + zone - value) / zone));
        double high = SwiftMath.Min(1, SwiftMath.Max(0, (value - (start + length - zone)) / zone));
        return (high * high - low * low) * length * 0.5 / Math.Sqrt(factor);
    }
}

/// <summary>Where screen zoom's map of the whole display sits.</summary>
public enum ZoomMapPosition { BottomRight, BottomLeft, TopRight, TopLeft, Off }

public static class ZoomMapPositionExtensions
{
    internal static readonly RawValueMap<ZoomMapPosition> RawValues = new("bottomRight", "bottomLeft", "topRight", "topLeft", "off");

    extension(ZoomMapPosition position)
    {
        public string RawValue => RawValues.RawValue(position);

        public string Label => position switch
        {
            ZoomMapPosition.BottomRight => "Bottom right",
            ZoomMapPosition.BottomLeft => "Bottom left",
            ZoomMapPosition.TopRight => "Top right",
            ZoomMapPosition.TopLeft => "Top left",
            _ => "Off",
        };
    }

    extension(ZoomMapPosition)
    {
        public static ValueList<ZoomMapPosition> AllCases => RawValues.AllCases;

        public static ZoomMapPosition? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

/// <summary>Screen zoom's map: a small picture of the whole display in a corner, with the zoomed part outlined.</summary>
public static class ZoomMap
{
    public const double Inset = 16;

    public const double CornerRadius = 10;

    public const double BorderWidth = 1;

    public const double BorderOpacity = 0.6;

    public const double OutlineWidth = 2;

    /// <summary>The part outside the zoomed area is darkened by black at this opacity.</summary>
    public const double DimOpacity = 0.4;

    /// <summary>While the pointer is over the map, it is this opaque so what's under it shows.</summary>
    public const double HoverOpacity = 0.2;

    /// <summary>The map fades in and out over this many seconds.</summary>
    public const double FadeDuration = 0.15;

    /// <summary>The map's frame on a display of <paramref name="display"/>, or null when the map is off or the display is too small for it.</summary>
    public static Rect? Frame(Rect display, ZoomMapPosition position)
    {
        if (position == ZoomMapPosition.Off || !display.IsValid || !(display.Width > 0) || !(display.Height > 0))
        {
            return null;
        }
        double width = SwiftMath.Min(260, SwiftMath.Max(140, display.Width * 0.16));
        double height = width * display.Height / display.Width;
        if (height > display.Height * 0.3)
        {
            height = display.Height * 0.3;
            width = height * display.Width / display.Height;
        }
        if (!(width >= 80) || !(width + Inset * 2 <= display.Width) || !(height + Inset * 2 <= display.Height))
        {
            return null;
        }
        bool left = position is ZoomMapPosition.BottomLeft or ZoomMapPosition.TopLeft;
        bool top = position is ZoomMapPosition.TopLeft or ZoomMapPosition.TopRight;
        return new Rect(left ? display.X + Inset : display.MaxX - Inset - width,
                        top ? display.Y + Inset : display.MaxY - Inset - height,
                        width, height);
    }

    /// <summary>The zoomed part of <paramref name="viewport"/>, drawn in a map of <paramref name="map"/>.</summary>
    public static Rect ZoomedArea(Viewport viewport, Rect map)
    {
        Rect display = viewport.Output, source = viewport.Source;
        double scaleX = map.Width / display.Width, scaleY = map.Height / display.Height;
        return new Rect(map.X + (source.X - display.X) * scaleX, map.Y + (source.Y - display.Y) * scaleY,
                        source.Width * scaleX, source.Height * scaleY);
    }
}
