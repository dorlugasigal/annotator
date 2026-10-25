using Annotator.Core;
using Annotator.Platform.Threading;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Presentation;
using Annotator.Workspace.Canvas;
using Windows.UI.Composition;

namespace Annotator.Rendering.Tests;

/// <summary>
/// The on-screen tip (Mac WorkspaceHintView): its box on its own surface with the ✕ while drawing, and the crossfade between
/// two texts.
/// </summary>
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
                const string drawing = "Drawing  ·  Escape: use apps\nHold Shift for a straight highlight.";
                hint.Update(drawing, view, closable: true, shown: true, animate: false, reduceMotion: false);
                Assert.Null(hint.Failure);
                Assert.True(hint.IsVisible);
                SpriteVisual[] sprites = [.. root.Children.OfType<SpriteVisual>()];
                Assert.Equal(2, sprites.Length);
                Assert.Single(sprites, sprite => sprite.IsVisible);
                // The box starts at the view's corner (16, 50) and is as wide as the longest line plus 18 points, and 18 more
                // for the ✕.
                Assert.Equal(new Rect(16, 50, hint.Box.Width, hint.Box.Height), hint.Box);
                Assert.True(hint.Closable);
                Assert.InRange(hint.Box.Width, 200, 300);
                // Two 12-point lines of 15 points, plus 14 points of inset.
                Assert.Equal(15 * 2 + 14, hint.Box.Height, 3);
                Assert.Equal(HintBox.CloseFrame(hint.Box), hint.CloseFrame);
                Assert.True(hint.CloseFrame.MaxX > hint.Box.MaxX - 9 && hint.CloseFrame.Y < hint.Box.Y + 22);

                // A new text while shown crossfades: both surfaces show until the old one has faded. A text that changed less
                // than 0.14 s before updates in place instead.
                Thread.Sleep(160);
                hint.Update(drawing.Replace("Escape: use apps", "Escape cancels the edit", StringComparison.Ordinal), view,
                    closable: true, shown: true, animate: true, reduceMotion: false);
                Assert.True(hint.IsVisible);
                Assert.Equal(2, sprites.Count(sprite => sprite.IsVisible));

                hint.Update(drawing, view, closable: true, shown: false, animate: false, reduceMotion: false);
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
