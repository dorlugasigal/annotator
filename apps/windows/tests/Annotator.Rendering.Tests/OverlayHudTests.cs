using Annotator.Core;
using Annotator.Core.Scene;
using Annotator.Platform.Threading;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Presentation;
using Windows.UI.Composition;

namespace Annotator.Rendering.Tests;

/// <summary>The canvas controls layer (the Use apps button): its surface sits on device pixels and shows only with devices.</summary>
public sealed class OverlayHudTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TheControlsDrawOnTheirOwnPixelAlignedSurfaceAndShowOnlyWithDevices()
    {
        var thread = DispatcherThread.Start("Annotator HUD test");
        try
        {
            await thread.RunAsync(() =>
            {
                using var compositor = new Compositor();
                using var root = compositor.CreateContainerVisual();
                using var hud = new OverlayHud(compositor, root, Warp.Devices, FixedSystemColors.Light, 1.25);
                var area = new Rect(16, 14, 150, 28);
                DisplayList button = new DisplayListBuilder().Add(DisplayItem.FillRect(area, Paint.Solid(new RGBA(0.98, 0.98, 0.98)))).ToList();
                hud.Show(button, area);
                Assert.Null(hud.Failure);
                SpriteVisual sprite = root.Children.OfType<SpriteVisual>().Single();
                // 16 by 14 points at 125% start inside pixel (20, 17); the far corner (207.5, 52.5) rounds out.
                Assert.Equal((20f, 17f, 188f, 36f), (sprite.Offset.X, sprite.Offset.Y, sprite.Size.X, sprite.Size.Y));
                Assert.False(sprite.IsVisible);

                hud.SetVisible(true, animate: false, reduceMotion: false);
                Assert.True(hud.IsVisible);
                Assert.True(sprite.IsVisible);
                Assert.Equal(1f, sprite.Opacity);

                hud.SetVisible(false, animate: true, reduceMotion: true);
                Assert.False(hud.IsVisible);
                hud.SetVisible(true, animate: true, reduceMotion: false);
                Assert.True(hud.IsVisible);

                hud.ReplaceDevices(null);
                Assert.Null(hud.Failure);
                Assert.False(sprite.IsVisible);
                hud.ReplaceDevices(Warp.Devices);
                Assert.True(sprite.IsVisible);
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }
}
