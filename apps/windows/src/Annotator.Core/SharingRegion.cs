// Port of apps/macos/Sources/AnnotationCore/SharingRegion.swift.
namespace Annotator.Core;

public enum ShareRegionPreset
{
    LeftHalf, RightHalf, TopHalf, BottomHalf,
    LeftThird, CenterThird, RightThird,
    TopLeft, TopRight, BottomLeft, BottomRight,
}

public static class ShareRegionPresetExtensions
{
    internal static readonly RawValueMap<ShareRegionPreset> RawValues = new(
        "leftHalf", "rightHalf", "topHalf", "bottomHalf",
        "leftThird", "centerThird", "rightThird",
        "topLeft", "topRight", "bottomLeft", "bottomRight");

    extension(ShareRegionPreset)
    {
        public static ValueList<ShareRegionPreset> AllCases => RawValues.AllCases;

        public static ShareRegionPreset? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }

    extension(ShareRegionPreset preset)
    {
        public string RawValue => RawValues.RawValue(preset);

        public string Title => preset switch
        {
            ShareRegionPreset.LeftHalf => "Left half",
            ShareRegionPreset.RightHalf => "Right half",
            ShareRegionPreset.TopHalf => "Top half",
            ShareRegionPreset.BottomHalf => "Bottom half",
            ShareRegionPreset.LeftThird => "Left third",
            ShareRegionPreset.CenterThird => "Center third",
            ShareRegionPreset.RightThird => "Right third",
            ShareRegionPreset.TopLeft => "Top left",
            ShareRegionPreset.TopRight => "Top right",
            ShareRegionPreset.BottomLeft => "Bottom left",
            ShareRegionPreset.BottomRight => "Bottom right",
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };

        public Rect NormalizedBounds => preset switch
        {
            ShareRegionPreset.LeftHalf => new Rect(0, 0, 0.5, 1),
            ShareRegionPreset.RightHalf => new Rect(0.5, 0, 0.5, 1),
            ShareRegionPreset.TopHalf => new Rect(0, 0, 1, 0.5),
            ShareRegionPreset.BottomHalf => new Rect(0, 0.5, 1, 0.5),
            ShareRegionPreset.LeftThird => new Rect(0, 0, 1.0 / 3, 1),
            ShareRegionPreset.CenterThird => new Rect(1.0 / 3, 0, 1.0 / 3, 1),
            ShareRegionPreset.RightThird => new Rect(2.0 / 3, 0, 1.0 / 3, 1),
            ShareRegionPreset.TopLeft => new Rect(0, 0, 0.5, 0.5),
            ShareRegionPreset.TopRight => new Rect(0.5, 0, 0.5, 0.5),
            ShareRegionPreset.BottomLeft => new Rect(0, 0.5, 0.5, 0.5),
            ShareRegionPreset.BottomRight => new Rect(0.5, 0.5, 0.5, 0.5),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
    }
}

/// <summary>A part of one display shared through the region window.</summary>
public readonly record struct SharingRegion
{
    public const double MinimumDimension = 32;

    /// <summary>Throws <see cref="CanvasError.InvalidGeometry"/> unless the region is valid, inside the display and at least 32 points on each side.</summary>
    public SharingRegion(Rect bounds, Rect displayBounds)
    {
        if (!displayBounds.IsValid || !bounds.IsValid || !displayBounds.Contains(bounds)
            || !(bounds.Width >= MinimumDimension) || !(bounds.Height >= MinimumDimension))
        {
            throw CanvasError.InvalidGeometry;
        }
        Bounds = bounds;
        DisplayBounds = displayBounds;
    }

    public SharingRegion(ShareRegionPreset preset, Rect displayBounds)
        : this(Scale(preset.NormalizedBounds, displayBounds), displayBounds)
    {
    }

    public Rect Bounds { get; }

    public Rect DisplayBounds { get; }

    public Rect SourceBounds(Viewport? viewport)
    {
        if (viewport is not { } zoom)
        {
            return Bounds;
        }
        return new Rect(zoom.SourcePoint(Bounds.Origin), zoom.SourcePoint(new Point(Bounds.MaxX, Bounds.MaxY)));
    }

    public Rect AspectFit(Rect output)
    {
        if (!output.IsValid || !(output.Width > 0) || !(output.Height > 0))
        {
            throw CanvasError.InvalidGeometry;
        }
        var scale = SwiftMath.Min(output.Width / Bounds.Width, output.Height / Bounds.Height);
        var width = Bounds.Width * scale;
        var height = Bounds.Height * scale;
        return new Rect(output.X + (output.Width - width) / 2, output.Y + (output.Height - height) / 2, width, height);
    }

    /// <summary>The output window's distance from the edges of the display's usable area.</summary>
    public const double OutputMargin = 24;

    public const double OutputMinimumWidth = 320;

    public const double OutputMinimumHeight = 200;

    /// <summary>
    /// Where the output window's content goes when sharing starts, in the coordinates of <see cref="Bounds"/>. Meeting apps
    /// send the window at its own size, so the content shows the region at its own size, or as large as fits in
    /// <paramref name="available"/> (the display's usable area) with <see cref="OutputMargin"/> around the window's frame.
    /// The window takes the corner of <paramref name="available"/> that covers the least of the region, top right first, so
    /// it stays out of the shared area when it can. Throws <see cref="CanvasError.InvalidGeometry"/> for an invalid area or
    /// frame.
    /// </summary>
    public Rect OutputContentFrame(Rect available, FrameInsets frame)
    {
        if (!available.IsValid || !(available.Width > 0) || !(available.Height > 0) || !frame.IsValid)
        {
            throw CanvasError.InvalidGeometry;
        }
        const double margin = OutputMargin;
        double chromeWidth = frame.Left + frame.Right, chromeHeight = frame.Top + frame.Bottom;
        double scale = SwiftMath.Min(SwiftMath.Min(1, (available.Width - 2 * margin - chromeWidth) / Bounds.Width),
                                     (available.Height - 2 * margin - chromeHeight) / Bounds.Height);
        double width = SwiftMath.Max(1, SwiftMath.Min(SwiftMath.Max(Bounds.Width * scale, OutputMinimumWidth), available.Width - chromeWidth));
        double height = SwiftMath.Max(1, SwiftMath.Min(SwiftMath.Max(Bounds.Height * scale, OutputMinimumHeight), available.Height - chromeHeight));
        double frameWidth = width + chromeWidth, frameHeight = height + chromeHeight;
        double right = SwiftMath.Max(available.X, available.MaxX - frameWidth - margin);
        double left = SwiftMath.Max(available.X, SwiftMath.Min(available.MaxX - frameWidth, available.X + margin));
        double top = SwiftMath.Max(available.Y, SwiftMath.Min(available.MaxY - frameHeight, available.Y + margin));
        double bottom = SwiftMath.Max(available.Y, available.MaxY - frameHeight - margin);
        Rect best = default;
        double covered = double.PositiveInfinity;
        ReadOnlySpan<(double X, double Y)> corners = [(right, top), (left, top), (right, bottom), (left, bottom)];
        foreach ((double x, double y) in corners)
        {
            var candidate = new Rect(x, y, frameWidth, frameHeight);
            double overlap = SwiftMath.Max(0, SwiftMath.Min(candidate.MaxX, Bounds.MaxX) - SwiftMath.Max(candidate.X, Bounds.X)) *
                SwiftMath.Max(0, SwiftMath.Min(candidate.MaxY, Bounds.MaxY) - SwiftMath.Max(candidate.Y, Bounds.Y));
            if (overlap < covered)
            {
                best = candidate;
                covered = overlap;
            }
        }
        return new Rect(best.X + frame.Left, best.Y + frame.Top, width, height);
    }

    private static Rect Scale(Rect unit, Rect display) => new(
        display.X + unit.X * display.Width,
        display.Y + unit.Y * display.Height,
        unit.Width * display.Width, unit.Height * display.Height);
}

/// <summary>The space a window's frame adds around its content, in points.</summary>
public readonly record struct FrameInsets(double Top, double Left = 0, double Bottom = 0, double Right = 0)
{
    public bool IsValid => Valid(Top) && Valid(Left) && Valid(Bottom) && Valid(Right);

    private static bool Valid(double value) => double.IsFinite(value) && value >= 0 && value <= 10_000;
}
