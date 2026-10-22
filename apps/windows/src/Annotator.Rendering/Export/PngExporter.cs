using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.Rendering.Export;

/// <summary>
/// Port of Swift <c>PNGExporter</c>: renders elements offscreen in sRGB with premultiplied alpha and encodes PNGs,
/// with the Mac's sizes, scale, limits and error messages. Covers flatten to opaque black. Use it on one thread.
/// </summary>
public sealed unsafe class PngExporter : IDisposable
{
    /// <summary>Each side of an export is at most 16,384 pixels.</summary>
    public const double MaximumSide = 16_384;

    /// <summary>An export has at most 40 million pixels.</summary>
    public const double MaximumPixels = 40_000_000;

    private readonly ITextMeasurer measurer;
    private readonly OffscreenCanvas canvas;
    private readonly WicImaging wic;
    private readonly bool ownsRenderer;

    public PngExporter(GraphicsDevices devices, ITextMeasurer measurer, ISystemColorResolver? colors = null)
        : this(new DisplayListRenderer(devices, colors), measurer, ownsRenderer: true)
    {
    }

    internal PngExporter(DisplayListRenderer renderer, ITextMeasurer measurer, bool ownsRenderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(measurer);
        Renderer = renderer;
        this.measurer = measurer;
        this.ownsRenderer = ownsRenderer;
        canvas = new OffscreenCanvas(renderer.Devices);
        wic = WicImaging.Create();
    }

    public DisplayListRenderer Renderer { get; }

    /// <summary>
    /// Swift <c>PNGExporter.image(elements:size:scale:background:)</c>: <paramref name="size"/> in points times
    /// <paramref name="scale"/>, rounded up to whole pixels, with an optional background color.
    /// </summary>
    /// <exception cref="RenderFailure">The pixel size is not finite, below 1, above 16,384 on a side or above 40 million
    /// pixels (<see cref="RenderFailureKind.InvalidSize"/>).</exception>
    /// <exception cref="CanvasError">An element is not valid.</exception>
    public RasterImage Image(ElementList elements, Size size, double scale = 1, RGBA? background = null)
    {
        foreach (ref readonly var element in elements)
        {
            element.Validate();
        }
        var (width, height) = PixelSize(size, scale);
        var scene = new SceneBuilder(measurer).Build(elements);
        return Render(scene, width, height, size.Width * scale, size.Height * scale, scale, background, includeSelection: false);
    }

    /// <summary>Swift <c>PNGExporter.data(elements:size:scale:background:)</c>.</summary>
    public byte[] Data(ElementList elements, Size size, double scale = 1, RGBA? background = null) =>
        Encode(Image(elements, size, scale, background));

    /// <summary>Swift <c>PNGExporter.encode</c>: an 8-bit RGBA PNG.</summary>
    /// <exception cref="RenderFailure">WIC could not encode the image (<see cref="RenderFailureKind.EncodingFailed"/>).</exception>
    public byte[] Encode(RasterImage image) => wic.EncodePng(image);

    /// <summary>
    /// Renders a scene that was already built (element lists, then the selection when asked) at
    /// <paramref name="scale"/> pixels per point, with the same limits as <see cref="Image"/>.
    /// </summary>
    public RasterImage Render(SceneSnapshot scene, Size size, double scale = 1, RGBA? background = null, bool includeSelection = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var (width, height) = PixelSize(size, scale);
        return Render(scene, width, height, size.Width * scale, size.Height * scale, scale, background, includeSelection);
    }

    /// <summary>
    /// Renders several scenes over each other in order, each with its element lists and then its selection, at
    /// <paramref name="scale"/> pixels per point: what an overlay's layers show together, for comparisons.
    /// </summary>
    public RasterImage Render(IReadOnlyList<SceneSnapshot> layers, Size size, double scale)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var (width, height) = PixelSize(size, scale);
        var renderer = Renderer;
        foreach (var layer in layers)
        {
            foreach (var list in layer.Elements)
            {
                renderer.Enter(list);
            }
            renderer.Enter(layer.Selection);
        }
        try
        {
            renderer.CollectImages();
            var area = OffscreenCanvas.Area(width, height, scale);
            return canvas.Render(width, height, context =>
            {
                foreach (var layer in layers)
                {
                    renderer.Draw((nint)context, layer.Elements, area);
                    renderer.Draw((nint)context, layer.Selection, area);
                }
            });
        }
        finally
        {
            foreach (var layer in layers)
            {
                foreach (var list in layer.Elements)
                {
                    renderer.Leave(list);
                }
                renderer.Leave(layer.Selection);
            }
            renderer.CollectImages();
        }
    }

    public void Dispose()
    {
        wic.Dispose();
        canvas.Dispose();
        if (ownsRenderer)
        {
            Renderer.Dispose();
        }
    }

    internal OffscreenCanvas Canvas => canvas;

    internal WicImaging Wic => wic;

    /// <summary>
    /// The Mac's export size rule: finite, at least 1 and at most 16,384 on each side, at most 40 million pixels, rounded up
    /// to whole pixels. A Windows display's size in points is its pixels divided by a fractional scale, so the product can be
    /// a little above a whole number (1,600 pixels at 175% gives 1,600.0000000000002); that is the whole number, not one more.
    /// </summary>
    internal static (int Width, int Height) PixelSize(Size size, double scale)
    {
        double w = size.Width * scale, h = size.Height * scale;
        if (!double.IsFinite(w) || !double.IsFinite(h) || !(w >= 1) || !(h >= 1) || !(w <= MaximumSide) || !(h <= MaximumSide)
            || !(w * h <= MaximumPixels))
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        return (WholePixels(w), WholePixels(h));
    }

    private static int WholePixels(double pixels) => (int)Math.Ceiling(pixels - 1e-6);

    private RasterImage Render(SceneSnapshot scene, int width, int height, double fillWidth, double fillHeight, double scale, RGBA? background,
                               bool includeSelection)
    {
        var renderer = Renderer;
        var lists = scene.Elements;
        foreach (var list in lists)
        {
            renderer.Enter(list);
        }
        if (includeSelection)
        {
            renderer.Enter(scene.Selection);
        }
        try
        {
            renderer.CollectImages();
            var area = OffscreenCanvas.Area(width, height, scale);
            return canvas.Render(width, height, context =>
            {
                if (background is { } color)
                {
                    // The Mac fills the unrounded pixel size before the scale, so a fractional edge is partly covered.
                    var identity = D2D.Identity;
                    context->SetTransform(&identity);
                    context->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                    var brush = renderer.SolidBrush;
                    var fill = D2D.Color(color);
                    brush->SetColor(&fill);
                    brush->SetOpacity(1);
                    var rect = D2D.Rect(0, 0, fillWidth, fillHeight);
                    context->FillRectangle(&rect, (ID2D1Brush*)brush);
                }
                renderer.Draw((nint)context, scene.Elements, area);
                if (includeSelection)
                {
                    renderer.Draw((nint)context, scene.Selection, area);
                }
            });
        }
        finally
        {
            foreach (var list in lists)
            {
                renderer.Leave(list);
            }
            if (includeSelection)
            {
                renderer.Leave(scene.Selection);
            }
            renderer.CollectImages();
        }
    }
}
