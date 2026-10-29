using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Annotator.Core;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Canvas;

namespace Annotator.UXReference;

/// <summary>
/// Compares the cursors with reference/ux-reference/macos/cursors (cursors.json sizes and hot spots, and the 2x images). The
/// footprints, the laser pointer's dot and the rotation cursor are drawn by Annotator on both platforms (Annotator.Workspace's
/// CursorArtwork, WIN-OVR-11): size and hot spot must be equal and the pixels nearly so. The crosshair and the I-beam are each system's
/// own cursor; their kind must match, and the sheet shows the Windows cursor beside the Mac's.
/// </summary>
internal static class Cursors
{
    public static List<string> Run(string reference, string output)
    {
        string folder = Path.Combine(reference, "cursors");
        Directory.CreateDirectory(Path.Combine(output, "cursors"));
        JsonArray entries = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "cursors.json")))!.AsArray();
        var lines = new List<string>();
        var rows = new List<(string Name, BitmapSource Mac, BitmapSource Windows, string Note)>();
        foreach (JsonNode? entry in entries)
        {
            string name = entry!["name"]!.GetValue<string>();
            double[] size = [.. entry["size"]!.AsArray().Select(value => value!.GetValue<double>())];
            double[] hot = [.. entry["hotSpot"]!.AsArray().Select(value => value!.GetValue<double>())];
            BitmapSource mac = Sheets.Load(Path.Combine(folder, $"{name}-light.png"));
            (string result, BitmapSource windows) = Compare(name, size, hot, mac);
            lines.Add($"{name}: {result}");
            rows.Add((name, mac, windows, result));
        }
        Sheets.Save(Sheet(rows), Path.Combine(output, "cursors", "cursors.sheet.png"));
        File.WriteAllLines(Path.Combine(output, "cursors", "cursors.diff.txt"), lines);
        return lines;
    }

    private static (string Result, BitmapSource Windows) Compare(string name, double[] size, double[] hot, BitmapSource mac)
    {
        CursorArtwork.Image ours;
        if (name == "rotate")
        {
            ours = CursorArtwork.Rotation(2);
        }
        else
        {
            string[] parts = name.Split('-');
            Tool tool = Tool.FromRawValue(parts[0]) ?? throw new InvalidDataException($"The reference cursor {name} names no tool.");
            CanvasCursor cursor = CanvasCursor.ForTool(tool, double.Parse(parts[1], CultureInfo.InvariantCulture));
            if (cursor.Kind is not (CanvasCursorKind.Footprint or CanvasCursorKind.Laser))
            {
                string kind = cursor.Kind == CanvasCursorKind.IBeam ? "I-beam" : "crosshair";
                (BitmapSource system, int width, int height, int x, int y) = SystemCursor(cursor.Kind == CanvasCursorKind.IBeam ? IBeam : Cross);
                return ($"system {kind} on both platforms (platform): Windows {width} x {height} pixels at {Scale():0.##}x, hot spot {x}, {y}; " +
                        $"Mac {F(size[0])} x {F(size[1])}, hot spot {F(hot[0])}, {F(hot[1])}", system);
            }
            ours = cursor.Kind == CanvasCursorKind.Laser ? CursorArtwork.Laser(cursor, 2) : CursorArtwork.Footprint(cursor, 2);
        }
        BitmapSource windows = BitmapSource.Create(ours.Width, ours.Height, 96, 96, PixelFormats.Bgra32, null, ours.Bgra, ours.Width * 4);
        bool geometry = ours.Width == size[0] * 2 && ours.Height == size[1] * 2 && ours.HotspotX == hot[0] * 2 && ours.HotspotY == hot[1] * 2;
        if (mac.PixelWidth != ours.Width || mac.PixelHeight != ours.Height)
        {
            return ($"differs: Windows {ours.Width / 2.0} x {ours.Height / 2.0} points, reference {F(size[0])} x {F(size[1])}", windows);
        }
        byte[] reference = new byte[mac.PixelWidth * mac.PixelHeight * 4];
        new FormatConvertedBitmap(mac, PixelFormats.Bgra32, null, 0).CopyPixels(reference, mac.PixelWidth * 4, 0);
        double alpha = 0, color = 0;
        int edges = 0, opaque = 0;
        for (int offset = 0; offset < reference.Length; offset += 4)
        {
            int difference = Math.Abs(reference[offset + 3] - ours.Bgra[offset + 3]);
            alpha += difference;
            edges += difference > 32 ? 1 : 0;
            if (reference[offset + 3] > 128 && ours.Bgra[offset + 3] > 128)
            {
                opaque++;
                color += Math.Max(Math.Abs(reference[offset] - ours.Bgra[offset]),
                    Math.Max(Math.Abs(reference[offset + 1] - ours.Bgra[offset + 1]), Math.Abs(reference[offset + 2] - ours.Bgra[offset + 2])));
            }
        }
        int pixels = reference.Length / 4;
        bool close = alpha / pixels <= 2 && edges <= pixels / 100 && opaque > 0 && color / opaque <= 8;
        return ($"{(geometry && close ? "match" : "differs")}: {F(size[0])} x {F(size[1])} points, hot spot {F(hot[0])}, {F(hot[1])}" +
                $"{(geometry ? string.Empty : $" (Windows {ours.Width / 2.0} x {ours.Height / 2.0}, hot spot {ours.HotspotX / 2.0}, {ours.HotspotY / 2.0})")}; " +
                $"mean alpha difference {alpha / pixels:0.00}, {edges} edge pixels differ by more than 32, mean color difference {color / Math.Max(1, opaque):0.0}", windows);
    }

    private const int Cross = 32515;
    private const int IBeam = 32513;

    /// <summary>The Windows system cursor as Windows draws it for this process, with its hot spot in pixels.</summary>
    private static (BitmapSource Image, int Width, int Height, int X, int Y) SystemCursor(int id)
    {
        // System cursors load only shared, at the size Windows uses for this process's scale.
        nint cursor = LoadImage(0, id, 2, 0, 0, 0x8000 | 0x40);
        if (cursor == 0)
        {
            throw new InvalidOperationException($"The system cursor {id} could not be loaded.");
        }
        if (!GetIconInfo(cursor, out IconInfo info))
        {
            throw new InvalidOperationException($"The system cursor {id} has no icon information.");
        }
        try
        {
            BitmapSource image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(cursor, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return (image, image.PixelWidth, image.PixelHeight, info.HotspotX, info.HotspotY);
        }
        finally
        {
            DeleteObject(info.Mask);
            DeleteObject(info.Color);
        }
    }

    private static BitmapSource Sheet(List<(string Name, BitmapSource Mac, BitmapSource Windows, string Note)> rows)
    {
        const int cell = 280, label = 150, gap = 12;
        int width = label + cell * 2 + gap * 4 + 520;
        int height = 40 + rows.Sum(row => Math.Max(60, Math.Max(row.Mac.PixelHeight, row.Windows.PixelHeight)) + gap);
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)), null, new Rect(0, 0, width, height));
            Text(context, "Cursors at 2x: macOS reference, Windows (drawn by Annotator, or the Windows system cursor), result", gap, 10, 14);
            double y = 40;
            foreach ((string name, BitmapSource mac, BitmapSource windows, string note) in rows)
            {
                double rowHeight = Math.Max(60, Math.Max(mac.PixelHeight, windows.PixelHeight));
                Text(context, name, gap, y + 4, 13);
                var light = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
                context.DrawRectangle(light, null, new Rect(label, y, cell, rowHeight));
                context.DrawRectangle(light, null, new Rect(label + cell + gap, y, cell, rowHeight));
                context.DrawImage(mac, new Rect(label, y, mac.PixelWidth, mac.PixelHeight));
                context.DrawImage(windows, new Rect(label + cell + gap, y, windows.PixelWidth, windows.PixelHeight));
                Text(context, Wrap(note, 70), label + cell * 2 + gap * 3, y + 4, 11);
                y += rowHeight + gap;
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Wrap(string text, int width)
    {
        var result = new StringBuilder();
        int line = 0;
        foreach (string word in text.Split(' '))
        {
            if (line + word.Length > width)
            {
                result.Append('\n');
                line = 0;
            }
            result.Append(word).Append(' ');
            line += word.Length + 1;
        }
        return result.ToString().TrimEnd();
    }

    private static void Text(DrawingContext context, string text, double x, double y, double size) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.White, 1),
            new Point(x, y));

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static double Scale() => GetDpiForSystem() / 96.0;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Color;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadImage(nint instance, nint name, int type, int width, int height, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(nint icon, out IconInfo info);


    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
