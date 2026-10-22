using Annotator.Core;
using Annotator.Platform.Threading;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Presentation;
using Annotator.Workspace.Canvas;
using Windows.UI.Composition;

namespace Annotator.Rendering.Tests;

/// <summary>The on-screen tip (Mac WorkspaceHintView): its box on its own surface, and the crossfade between two texts.</summary>
public sealed class HintHudTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ANewTextCrossfadesOnTheOtherSurfaceAndHidingHidesBoth()
    {
        var thread = DispatcherThread.Start("Annotator hint test");
        try
        {
            await thread.RunAsync(() =>
            {
                using var compositor = new Compositor();
                using var root = compositor.CreateContainerVisual();
                using var hint = new HintHud(compositor, root, Warp.Devices, FixedSystemColors.Light, 1.25, Warp.Measurer);
                Rect view = HintBox.ViewFrame(1200, useAppsShown: true);
                const string drawing = "Drawing  ·  Ctrl+Shift+2: draw\nEscape: use apps\n" + WorkspaceHint.Footer;
                hint.Update(drawing, view, shown: true, animate: false, reduceMotion: false);
                Assert.Null(hint.Failure);
                Assert.True(hint.IsVisible);
                SpriteVisual[] sprites = [.. root.Children.OfType<SpriteVisual>()];
                Assert.Equal(2, sprites.Length);
                Assert.Single(sprites, sprite => sprite.IsVisible);
                // The box starts at the view's corner (16, 50) and is as wide as the longest line plus 18 points.
                Assert.Equal(new Rect(16, 50, hint.Box.Width, hint.Box.Height), hint.Box);
                Assert.InRange(hint.Box.Width, 150, 260);
                // Three lines: two 11-point lines of 13 points and the 10-point footer of 12, plus 14 points of inset.
                Assert.Equal(13 * 2 + 12 + 14, hint.Box.Height, 3);

                // A new text while shown crossfades: both surfaces show until the old one has faded. A text that changed less
                // than 0.14 s before updates in place instead.
                Thread.Sleep(160);
                hint.Update(drawing.Replace("Escape: use apps", "Escape cancels the current edit.", StringComparison.Ordinal), view,
                    shown: true, animate: true, reduceMotion: false);
                Assert.True(hint.IsVisible);
                Assert.Equal(2, sprites.Count(sprite => sprite.IsVisible));

                hint.Update(drawing, view, shown: false, animate: false, reduceMotion: false);
                Assert.False(hint.IsVisible);
                Assert.DoesNotContain(sprites, sprite => sprite.IsVisible);
            }).WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            await thread.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }
}
