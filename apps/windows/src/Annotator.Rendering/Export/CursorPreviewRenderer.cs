using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Export;

/// <summary>
/// Draws the halo of the Pointer settings preview (Mac CursorPreviewCanvas.draw) into pixels: the halo around a point,
/// scaled down so its footprint fits the preview, with the click animation's frame. The page draws the background and
/// the arrow. It owns a WARP device, so it never shares the overlays' device; create it while the preview shows, dispose
/// it when the preview hides, and use it on one thread.
/// </summary>
public sealed unsafe class CursorPreviewRenderer : IDisposable
{
    private readonly GraphicsDevices _devices;
    private readonly DisplayListRenderer _renderer;
    private readonly PointerEffectsRenderer _effects;
    private readonly OffscreenCanvas _canvas;

    public CursorPreviewRenderer()
    {
        _devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
        try
        {
            _renderer = new DisplayListRenderer(_devices);
            _effects = new PointerEffectsRenderer(_renderer);
            _canvas = new OffscreenCanvas(_devices);
        }
        catch
        {
            _effects?.Dispose();
            _renderer?.Dispose();
            _devices.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Mac: the halo shrinks when its footprint (1.6 diameters, both borders and four glow radii) is wider than the
    /// preview less 24 points, and never grows.
    /// </summary>
    public static double FitScale(in CursorHighlightSettings settings, double width, double height)
    {
        double footprint = settings.Diameter * 1.6 + settings.BorderWidth * 2 + settings.ResolvedGlow * 4;
        return Math.Min(1, Math.Max(1, Math.Min(width, height) - 24) / footprint);
    }

    /// <summary>
    /// The halo of a <paramref name="width"/> by <paramref name="height"/>-point preview at <paramref name="scale"/>
    /// pixels per point, centered on <paramref name="center"/> (points from the top left), with a click frame when
    /// <paramref name="clickProgress"/> is set; transparent around it. The glow keeps its size when the halo shrinks,
    /// as Core Graphics shadows do.
    /// </summary>
    public RasterImage Render(in CursorHighlightSettings settings, double width, double height, double scale, Point center,
                              double? clickProgress, Point? click, bool reduceMotion)
    {
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
        double fit = FitScale(settings, width, height);
        Point? clickCenter = click is { } at ? new Point((at.X - center.X) / fit, (at.Y - center.Y) / fit) : null;
        CursorHighlightFrame frame = CursorHighlightFrame.Create(settings, new Point(0, 0), clickProgress, clickCenter, reduceMotion);
        var area = new DrawArea(scale * fit, center.X * scale, center.Y * scale, new PixelRect(0, 0, pixelWidth, pixelHeight));
        return _canvas.Render(pixelWidth, pixelHeight, context => _effects.DrawHalo((nint)context, frame, area, glowScale: scale));
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _effects.Dispose();
        _renderer.Dispose();
        _devices.Dispose();
    }
}
