using Annotator.Platform.Displays;
using Annotator.Rendering.Imaging;

namespace Annotator.RenderHarness;

/// <summary>
/// Mode <c>shot</c>: saves an area of the primary monitor as Windows.Graphics.Capture composes it, overlays included.
/// GDI screen copies can miss layered windows without a redirection bitmap, such as Annotator's overlays (S-1).
/// </summary>
internal static class ScreenShot
{
    public static void Run(HarnessOptions options)
    {
        var path = options.Evidence ?? throw new ArgumentException("--evidence names the PNG file to write.");
        var display = DisplayEnumerator.GetDisplays().First(candidate => candidate.IsPrimary);
        var area = options.Area ?? new PixelRect(0, 0, display.Bounds.Width, display.Bounds.Height);
        if (area.IsEmpty || area.Left < 0 || area.Top < 0 || area.Right > display.Bounds.Width || area.Bottom > display.Bounds.Height)
        {
            throw new ArgumentException($"--area must lie inside the {display.Bounds.Width} x {display.Bounds.Height} monitor.");
        }
        RasterImage? image = null;
        using var done = new ManualResetEventSlim();
        using var capture = new MonitorCapture(display, start: false);
        capture.OnFrame((texture, _) =>
        {
            if (image is null)
            {
                image = capture.Read(texture, area);
                done.Set();
            }
        });
        capture.Start();
        if (!done.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new NoFramesException("Windows.Graphics.Capture delivered no frame within two seconds.");
        }
        capture.OnFrame(null);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, RasterAsset.EncodePng(image!));
        Console.WriteLine($"Saved {path} ({area.Width} x {area.Height} at {area.Left},{area.Top}).");
    }
}
