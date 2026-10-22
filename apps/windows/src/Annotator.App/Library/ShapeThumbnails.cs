using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.Core;
using Annotator.Platform;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Annotator.App.Library;

/// <summary>My shapes could not draw its previews: the message says why, so it is shown, never swallowed.</summary>
internal sealed class ThumbnailFailure(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Draws My shapes thumbnails (the Mac's ShapeThumbnailView) on a worker thread with a WARP device that exists only while
/// it draws: no graphics driver thread stays behind and the overlays' device is never shared. Drawn thumbnails are kept by
/// shape and size, so showing My shapes again draws only new shapes. Use it on the app thread.
/// </summary>
internal sealed class ShapeThumbnails
{
    private readonly Dictionary<(Guid Id, int Width, int Height), ImageSource> _drawn = [];

    /// <summary>
    /// The thumbnails of <paramref name="shapes"/> at <paramref name="width"/> by <paramref name="height"/> points and
    /// <paramref name="scale"/> pixels per point. Throws <see cref="ThumbnailFailure"/> when they cannot be drawn.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ImageSource>> DrawAsync(IReadOnlyList<SavedShape> shapes, double width, double height, double scale)
    {
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));
        var result = new Dictionary<Guid, ImageSource>();
        var missing = new List<SavedShape>();
        foreach (SavedShape shape in shapes)
        {
            if (_drawn.TryGetValue((shape.Id, pixelWidth, pixelHeight), out ImageSource? known))
            {
                result[shape.Id] = known;
            }
            else
            {
                missing.Add(shape);
            }
        }
        if (missing.Count > 0)
        {
            IReadOnlyList<(Guid Id, RasterImage Image)> images = await Task.Run(() => Draw(missing, width, height, scale));
            foreach ((Guid id, RasterImage image) in images)
            {
                ImageSource source = Bitmap(image);
                _drawn[(id, pixelWidth, pixelHeight)] = source;
                result[id] = source;
            }
        }
        // Forget the thumbnails of shapes that are no longer saved.
        var listed = new HashSet<Guid>(shapes.Select(shape => shape.Id));
        foreach ((Guid Id, int Width, int Height) key in _drawn.Keys.Where(key => !listed.Contains(key.Id)).ToArray())
        {
            _drawn.Remove(key);
        }
        return result;
    }

    private static List<(Guid, RasterImage)> Draw(List<SavedShape> shapes, double width, double height, double scale)
    {
        try
        {
            using GraphicsDevices devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true });
            using var measurer = new DirectWriteTextMeasurer();
            using var renderer = new ShapeThumbnailRenderer(devices, measurer);
            var images = new List<(Guid, RasterImage)>(shapes.Count);
            foreach (SavedShape shape in shapes)
            {
                images.Add((shape.Id, renderer.Render(shape.Elements, shape.Bounds, width, height, scale)));
            }
            return images;
        }
        catch (Exception error) when (error is NativeCallException or CanvasError or ArgumentException)
        {
            throw new ThumbnailFailure($"My shapes could not draw the previews. Your shapes are still saved. Click Reload to try again. {error.Message}", error);
        }
    }

    private static WriteableBitmap Bitmap(RasterImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            for (int row = 0; row < image.Height; row++)
            {
                stream.Write(image.Pixels.Slice(row * image.Stride, image.Width * 4));
            }
        }
        bitmap.Invalidate();
        return bitmap;
    }
}
