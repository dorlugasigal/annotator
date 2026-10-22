using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Annotator.UXReference;

/// <summary>
/// Pictures for side-by-side review: the Windows surface scaled to the reference's 2x (2 pixels per DIP), and a sheet
/// with the reference, the Windows surface and both blended, on a checkerboard so transparent corners show.
/// </summary>
internal static class Sheets
{
    /// <summary>Reads the app's premultiplied BGRA rows and scales them to 2 pixels per DIP.</summary>
    public static BitmapSource WindowsAt2x(string bgraPath, int width, int height, double dipWidth, double dipHeight)
    {
        byte[] pixels = File.ReadAllBytes(bgraPath);
        if (pixels.Length != width * height * 4)
        {
            throw new InvalidDataException($"{bgraPath} has {pixels.Length} bytes, not {width} x {height} x 4.");
        }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        int targetWidth = (int)Math.Round(dipWidth * 2), targetHeight = (int)Math.Round(dipHeight * 2);
        return Render(targetWidth, targetHeight, context => context.DrawImage(source, new Rect(0, 0, targetWidth, targetHeight)));
    }

    public static BitmapSource Load(string pngPath)
    {
        var decoder = new PngBitmapDecoder(new Uri(pngPath), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        return frame.Format == PixelFormats.Pbgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
    }

    public static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Reference, Windows and an even blend of both, stacked, each with a caption. A Windows-only state has no reference and
    /// shows the Windows panel alone.
    /// </summary>
    public static BitmapSource Sheet(BitmapSource? reference, BitmapSource? windows, string title, string windowsCaption)
    {
        const int margin = 16, caption = 28;
        int width = Math.Max(reference?.PixelWidth ?? 0, windows?.PixelWidth ?? 0);
        int panels = (reference is null ? 0 : 1) + (windows is null ? 0 : reference is null ? 1 : 2);
        int panelHeight = Math.Max(reference?.PixelHeight ?? 0, windows?.PixelHeight ?? 0);
        int sheetWidth = width + margin * 2;
        int sheetHeight = caption + panels * (caption + panelHeight + margin) + margin;
        return Render(sheetWidth, sheetHeight, context =>
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)), null, new Rect(0, 0, sheetWidth, sheetHeight));
            Text(context, title, margin, 6, 15, Brushes.White);
            double y = caption;
            void Panel(string label, Action<Rect> draw, double panelWidth, double height)
            {
                Text(context, label, margin, y + 6, 13, Brushes.White);
                y += caption;
                var area = new Rect(margin, y, panelWidth, height);
                Checkerboard(context, area);
                draw(area);
                y += panelHeight + margin;
            }
            if (reference is not null)
            {
                Panel("macOS reference (2x)", area => context.DrawImage(reference, area), reference.PixelWidth, reference.PixelHeight);
            }
            if (windows is not null)
            {
                Panel(windowsCaption, area => context.DrawImage(windows, area), windows.PixelWidth, windows.PixelHeight);
                if (reference is not null)
                {
                    Panel("Blend: reference and Windows at 50% each", area =>
                    {
                        context.DrawImage(reference, new Rect(area.X, area.Y, reference.PixelWidth, reference.PixelHeight));
                        context.PushOpacity(0.5);
                        context.DrawImage(windows, new Rect(area.X, area.Y, windows.PixelWidth, windows.PixelHeight));
                        context.Pop();
                    }, width, panelHeight);
                }
            }
        });
    }

    private static void Checkerboard(DrawingContext context, Rect area)
    {
        var light = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
        var dark = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
        context.DrawRectangle(light, null, area);
        const int cell = 12;
        for (double y = area.Top; y < area.Bottom; y += cell)
        {
            for (double x = area.Left + (((int)((y - area.Top) / cell)) % 2) * cell; x < area.Right; x += cell * 2)
            {
                context.DrawRectangle(dark, null, new Rect(x, y, Math.Min(cell, area.Right - x), Math.Min(cell, area.Bottom - y)));
            }
        }
    }

    private static void Text(DrawingContext context, string text, double x, double y, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, 1);
        context.DrawText(formatted, new Point(x, y));
    }

    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
        using (DrawingContext context = visual.RenderOpen())
        {
            draw(context);
        }
        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
