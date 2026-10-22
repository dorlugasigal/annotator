using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;

namespace Annotator.Rendering.Tests;

public sealed unsafe class RealizationProbe(ITestOutputHelper output)
{
    [Theory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public void CompareRealizedAndDirectPixels(bool warp)
    {
        using var devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = warp });
        using var exporter = new PngExporter(devices, Warp.Measurer, FixedSystemColors.Light);
        var renderer = exporter.Renderer;
        var path = new PathBuilder(4, 4).MoveTo(new Point(2, 2)).LineTo(new Point(30, 8)).LineTo(new Point(4, 30)).LineTo(new Point(36, 36)).ToPathData();
        var ellipse = new PathBuilder(6, 13).AddEllipse(new Rect(5, 5, 28, 20)).ToPathData();
        foreach (var item in new[]
                 {
                     DisplayItem.StrokePath(path, new StrokeStyle(3, LineCap.Round, LineJoin.Round), Paint.Solid(new RGBA(1, 0, 0))),
                     DisplayItem.FillPath(ellipse, FillRule.NonZero, Paint.Solid(new RGBA(1, 0, 0))),
                 })
        {
            var list = TestLists.Create(item);
            renderer.Enter(list);
            Annotator.Rendering.Imaging.RasterImage Render(bool live) => exporter.Canvas.Render(40, 40, context =>
            {
                renderer.Draw((nint)context, list, new DrawArea(1.25, 0, 0, new PixelRect(0, 0, 40, 40), live));
            });
            var direct = Render(true);
            _ = Render(false);
            _ = Render(false);
            var realized = Render(false);
            int any = 0, largest = 0;
            for (var index = 3; index < direct.Pixels.Length; index += 4)
            {
                var delta = Math.Abs(direct.Pixels[index] - realized.Pixels[index]);
                any += delta > 0 ? 1 : 0;
                largest = Math.Max(largest, delta);
            }
            output.WriteLine($"{(warp ? "WARP" : devices.Adapter.Description)} {item.Op}: {any} pixels differ, largest {largest}, realized draws {renderer.Counters.RealizedDraws}");
            renderer.Leave(list);
        }
    }
}
