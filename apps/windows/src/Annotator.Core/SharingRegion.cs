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

    private static Rect Scale(Rect unit, Rect display) => new(
        display.X + unit.X * display.Width,
        display.Y + unit.Y * display.Height,
        unit.Width * display.Width, unit.Height * display.Height);
}
