using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Motion;
using Annotator.Rendering.Sharing;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;

namespace Annotator.Rendering.Tests;

/// <summary>
/// The shared region's copies of motion snapshots (Mac SharedWorkspaceTransitionTests
/// testSnapshotClipsOriginalRegionAndUnionOfOverlappingProtection): a copy keeps only what rested inside the region and
/// outside every cover and pixel effect, whether it is drawn by Direct2D or cut from the toolbar's pixels.
/// </summary>
public sealed class StageClipTests
{
    // The Mac test in pixels: a 100 by 60 snapshot, the region from 40 pixels in, and covers at 15 to 35 and 25 to 45 pixels
    // of the region, which overlap.
    private static readonly PixelRect Snapshot = new(200, 40, 300, 100);
    private static readonly StageClip Clip = new(new PixelRect(240, 0, 400, 400), [new PixelRect(255, 40, 275, 100), new PixelRect(265, 40, 285, 100)]);

    private static void AssertKept(Func<int, int, (byte Red, byte Green, byte Blue, byte Alpha)> pixel, (byte, byte, byte, byte) kept)
    {
        foreach (var x in new[] { 0, 30, 39, 55, 60, 70, 80, 84 })
        {
            Assert.Equal(0, pixel(x, 30).Alpha);
        }
        foreach (var x in new[] { 40, 45, 54, 85, 90, 99 })
        {
            Assert.Equal(kept, pixel(x, 30));
        }
    }

    [Fact]
    public void ACopyKeepsTheRegionWithoutTheUnionOfOverlappingProtection()
    {
        var bgra = new byte[100 * 60 * 4];
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            bgra[offset + 2] = 255;
            bgra[offset + 3] = 255;
        }
        Clip.Apply(bgra, 100 * 4, Snapshot);
        var image = new RasterImage(100, 60, 100 * 4, bgra);
        AssertKept((x, y) => image[x, y], (255, 0, 0, 255));
        // Every row outside the region is cleared too.
        var high = new StageClip(new PixelRect(0, 70, 400, 400), []);
        var copy = new byte[100 * 60 * 4];
        Array.Fill(copy, (byte)255);
        high.Apply(copy, 100 * 4, Snapshot);
        Assert.Equal(0, new RasterImage(100, 60, 400, copy)[50, 29].Alpha);
        Assert.Equal(255, new RasterImage(100, 60, 400, copy)[50, 30].Alpha);
    }

    [Fact]
    public void ASnapshotOutsideTheRegionOrUnderProtectionKeepsNothing()
    {
        Assert.False(Clip.Hides(Snapshot));
        Assert.True(Clip.Hides(new PixelRect(500, 500, 550, 550)), "A snapshot outside the region has no copy.");
        Assert.True(Clip.Hides(new PixelRect(256, 50, 284, 90)), "A snapshot under the overlapping covers has no copy.");
        Assert.True(new StageClip(Snapshot, [Snapshot]).Hides(Snapshot), "A fully protected object must not produce a motion trail.");
        var halves = new StageClip(new PixelRect(0, 0, 100, 60), [new PixelRect(0, 0, 60, 60), new PixelRect(40, 0, 100, 60)]);
        Assert.True(halves.Hides(new PixelRect(0, 0, 100, 60)), "Two overlapping covers protect the whole snapshot together.");
        var gap = new StageClip(new PixelRect(0, 0, 100, 60), [new PixelRect(0, 0, 60, 60), new PixelRect(40, 0, 100, 59)]);
        Assert.False(gap.Hides(new PixelRect(0, 0, 100, 60)), "One unprotected row keeps a copy.");
    }

    [Fact]
    public unsafe void DrawnCopiesKeepOnlyTheRegionOutsideProtection()
    {
        var devices = Warp.Devices;
        DisplayList list = TestLists.Create(DisplayItem.FillRect(new Rect(200, 40, 100, 60), Paint.Solid(new RGBA(1, 0, 0))));
        var bounds = new PixelRect(0, 0, 100, 60);
        RasterImage lists = Render(devices, context =>
        {
            using var renderer = new DisplayListRenderer(devices, FixedSystemColors.Light);
            renderer.Enter(list);
            try
            {
                var area = new DrawArea(1, -Snapshot.Left, -Snapshot.Top, Clip.KeepIn(bounds, Snapshot.Left, Snapshot.Top));
                renderer.Draw((nint)context, list, area);
                Clip.EraseIn(context, bounds, Snapshot.Left, Snapshot.Top);
            }
            finally
            {
                renderer.Leave(list);
            }
        });
        AssertKept((x, y) => lists[x, y], (255, 0, 0, 255));
        // A board's color, cleared inside the kept part only.
        RasterImage board = Render(devices, context =>
        {
            Clip.BeginKeep(context, bounds, Snapshot.Left, Snapshot.Top);
            var white = new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 1 };
            context->Clear(&white);
            Clip.EndKeep(context, bounds, Snapshot.Left, Snapshot.Top);
        });
        AssertKept((x, y) => board[x, y], (255, 255, 255, 255));
    }

    private unsafe delegate void Drawing(ID2D1DeviceContext* context);

    /// <summary>Draws into a cleared 100 by 60-pixel WARP target and reads it back.</summary>
    private static unsafe RasterImage Render(Platform.Graphics.GraphicsDevices devices, Drawing draw)
    {
        nint target = EffectTextures.Target(devices, 100, 60);
        ID2D1DeviceContext* context = null;
        ID2D1Bitmap1* bitmap = null;
        try
        {
            Native.Check(((ID2D1Device*)devices.D2DDevice)->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &context),
                         "CreateDeviceContext");
            bitmap = SharedRegionPresenter.Bitmap(context, (ID3D11Texture2D*)target, target: true, "clip test target");
            context->SetTarget((ID2D1Image*)bitmap);
            context->BeginDraw();
            var transparent = default(D2D1_COLOR_F);
            context->Clear(&transparent);
            draw(context);
            Native.Check(context->EndDraw(null, null), "EndDraw");
            context->SetTarget(null);
            return EffectTextures.Read(devices, target, 100, 60);
        }
        finally
        {
            if (bitmap != null)
            {
                bitmap->Release();
            }
            if (context != null)
            {
                context->Release();
            }
            Marshal.Release(target);
        }
    }
}
