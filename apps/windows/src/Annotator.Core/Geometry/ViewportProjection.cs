// Port of apps/macos/Sources/PresentationRendering/ViewportProjection.swift as geometry. Core Image works with a
// bottom-left origin; these results use the top-left origin of Windows images and Direct2D.
namespace Annotator.Core.Geometry;

/// <summary>How an image is resampled when drawn.</summary>
public enum ImageInterpolation : byte
{
    /// <summary>Direct2D <c>D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR</c> (Core Image <c>samplingNearest()</c>).</summary>
    NearestNeighbor,

    /// <summary>Direct2D <c>D2D1_INTERPOLATION_MODE_LINEAR</c>.</summary>
    Linear,

    /// <summary>
    /// Direct2D <c>D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC</c>, used wherever the Mac scales an image with Core Graphics
    /// (image objects, exported snapshots). No Direct2D mode reproduces Core Graphics' resampling; this one comes closest,
    /// and the owner approved it on 2026-10-19 (S-13). The corpus image scene stays within its measured distance.
    /// </summary>
    HighQualityCubic,
}

/// <summary>Draw <see cref="Source"/> (image pixels) into <see cref="Destination"/> (target pixels).</summary>
public readonly record struct ImageProjection(Rect Source, Rect Destination, ImageInterpolation Interpolation);

/// <summary>Swift <c>PixelScreenOverlay</c> without the image: its pixel bounds stand in for <c>image.extent</c>.</summary>
public readonly record struct PixelScreenOverlay(Rect ImageBounds, Rect Bounds, Rect Region, double Opacity = 1);

/// <summary>
/// Draw the whole overlay image (<see cref="Source"/>) into <see cref="Destination"/>, clipped to
/// <see cref="Clip"/>, with <see cref="Opacity"/>. All values are target pixels with a top-left origin.
/// </summary>
public readonly record struct OverlayProjection(Rect Source, Rect Destination, Rect Clip, double Opacity);

public static class ViewportProjection
{
    /// <summary>
    /// Swift <c>imageForMetal</c>: the part of a captured display image that the zoom viewport shows, scaled to the
    /// target with nearest-neighbor sampling. <paramref name="imageBounds"/> is the image's pixel rectangle and
    /// <paramref name="sourceSize"/> the display size in points.
    /// </summary>
    /// <exception cref="RenderFailure">A size is not finite and positive (<see cref="RenderFailureKind.InvalidSize"/>).</exception>
    public static ImageProjection ZoomedImage(Rect imageBounds, Size sourceSize, Viewport viewport, Size targetSize)
    {
        if (!(sourceSize.Width > 0) || !(sourceSize.Height > 0) || !double.IsFinite(sourceSize.Width)
            || !double.IsFinite(sourceSize.Height) || !(targetSize.Width > 0) || !(targetSize.Height > 0)
            || !double.IsFinite(targetSize.Width) || !double.IsFinite(targetSize.Height))
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        double sx = imageBounds.Width / sourceSize.Width, sy = imageBounds.Height / sourceSize.Height;
        var source = viewport.Source;
        var crop = new Rect(imageBounds.X + source.X * sx, imageBounds.Y + source.Y * sy, source.Width * sx, source.Height * sy);
        return new ImageProjection(crop, new Rect(0, 0, targetSize.Width, targetSize.Height), ImageInterpolation.NearestNeighbor);
    }

    /// <summary>
    /// Swift <c>overlayForMetal</c>: places an image of <see cref="PixelScreenOverlay.Bounds"/> (display points) into a
    /// target that shows <see cref="PixelScreenOverlay.Region"/>, clipped to the target.
    /// </summary>
    /// <exception cref="RenderFailure">Invalid geometry, opacity or image bounds (<see cref="RenderFailureKind.InvalidSize"/>).</exception>
    public static OverlayProjection Overlay(PixelScreenOverlay overlay, Size targetSize)
    {
        Rect frame = overlay.Bounds, region = overlay.Region, extent = overlay.ImageBounds;
        if (!frame.IsValid || !region.IsValid || !(frame.Width > 0) || !(frame.Height > 0)
            || !(region.Width > 0) || !(region.Height > 0)
            || !double.IsFinite(targetSize.Width) || !double.IsFinite(targetSize.Height)
            || !(targetSize.Width > 0) || !(targetSize.Height > 0)
            || !double.IsFinite(extent.X) || !double.IsFinite(extent.Y) || !double.IsFinite(extent.Width) || !double.IsFinite(extent.Height)
            || !(extent.Width > 0) || !(extent.Height > 0) || !(extent.Width <= 16_384) || !(extent.Height <= 16_384)
            || !(extent.Width * extent.Height <= 8_000_000)
            || !double.IsFinite(overlay.Opacity) || !(overlay.Opacity >= 0 && overlay.Opacity <= 1))
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        double sx = targetSize.Width / region.Width, sy = targetSize.Height / region.Height;
        var destination = new Rect((frame.X - region.X) * sx, (frame.Y - region.Y) * sy, frame.Width * sx, frame.Height * sy);
        return new OverlayProjection(extent, destination, new Rect(0, 0, targetSize.Width, targetSize.Height), overlay.Opacity);
    }
}
