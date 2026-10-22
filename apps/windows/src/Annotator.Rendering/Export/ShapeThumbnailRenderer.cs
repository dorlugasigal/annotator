using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Export;

/// <summary>
/// Port of the Mac's ShapeThumbnailView (ShapeLibraryModel.swift): a saved shape's drawings, fitted with the Mac's padding
/// into a thumbnail and centered in it, drawn by the scene renderer. Transparent where nothing is drawn. Use it on one thread.
/// </summary>
public sealed unsafe class ShapeThumbnailRenderer : IDisposable
{
    private readonly ITextMeasurer measurer;
    private readonly DisplayListRenderer renderer;
    private readonly OffscreenCanvas canvas;

    public ShapeThumbnailRenderer(GraphicsDevices devices, ITextMeasurer measurer, ISystemColorResolver? colors = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(measurer);
        this.measurer = measurer;
        renderer = new DisplayListRenderer(devices, colors);
        try
        {
            canvas = new OffscreenCanvas(devices);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>The Mac's padding around the drawings: 12 points, or more for wide strokes, large arrowheads and rough lines.</summary>
    public static double Padding(ElementList elements)
    {
        double padding = 12;
        foreach (ref readonly Element element in elements)
        {
            padding = Math.Max(padding, element.Style.Width * 8 * (element.Style.HeadScale ?? 1) + element.Style.Roughness * 3);
        }
        return padding;
    }

    /// <summary>How much the drawings shrink or grow to fit a <paramref name="width"/> by <paramref name="height"/> point box.</summary>
    public static double Fit(Rect content, double padding, double width, double height) =>
        Math.Min(width / Math.Max(1, content.Width + padding * 2), height / Math.Max(1, content.Height + padding * 2));

    /// <summary>
    /// Draws <paramref name="elements"/>, whose bounds are <paramref name="content"/> (SavedShape.Bounds), into a thumbnail of
    /// <paramref name="width"/> by <paramref name="height"/> points at <paramref name="scale"/> pixels per point.
    /// </summary>
    /// <exception cref="NativeCallException">Drawing failed, for example because the device was lost.</exception>
    public RasterImage Render(ElementList elements, Rect content, double width, double height, double scale)
    {
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height) || !(scale > 0) || !double.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A thumbnail needs a finite, positive size and scale.");
        }
        double fit = Fit(content, Padding(elements), width, height);
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
        SceneSnapshot scene = new SceneBuilder(measurer).Build(elements);
        // The Mac translates to the box's middle, scales and translates by the drawings' center, in points.
        var area = new DrawArea(scale * fit, scale * (width / 2 - fit * content.Center.X), scale * (height / 2 - fit * content.Center.Y),
                                new PixelRect(0, 0, pixelWidth, pixelHeight));
        foreach (DisplayList list in scene.Elements)
        {
            renderer.Enter(list);
        }
        try
        {
            renderer.CollectImages();
            return canvas.Render(pixelWidth, pixelHeight, context => renderer.Draw((nint)context, scene.Elements, area));
        }
        finally
        {
            foreach (DisplayList list in scene.Elements)
            {
                renderer.Leave(list);
            }
            renderer.CollectImages();
        }
    }

    public void Dispose()
    {
        canvas.Dispose();
        renderer.Dispose();
    }
}
