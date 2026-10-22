using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Annotator.Platform.Tests;

public sealed class PointerModifierTests
{
    /// <summary>
    /// Explicit: real input, like <see cref="StackingTests.ClickingTheCanvasLeavesTheToolbarClickable"/>. Routes the mouse
    /// through pointer messages as the app does and drags with Shift held on a 120-pixel canvas tile in the primary
    /// display's top-left corner, for about a second; keys go only to the test's own foreground windows. The canvas must
    /// see Shift on the press and on every move, which the Mac's Shift constraints need (snappedLineEnd).
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ShiftHeldWhileDraggingReachesTheCanvas()
    {
        try
        {
            MouseInPointer.Enable();
        }
        catch (NativeCallException)
        {
            Assert.Skip("Windows refused to route the mouse through pointer messages in the test process.");
        }
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Draw, () => TestDisplays.Tiles(1, size: 120));
        PixelRect tile = TestDisplays.Tiles(1, size: 120)[0].Bounds;
        await using StandInWindow app = await StandInWindow.StartAsync(registry: null, WindowLayer.Canvas,
            PixelRect.FromSize(tile.Left + 4, tile.Top + 4, 16, 16), activates: true);
        nint overlay = await harness.Run(() => harness.Manager.Overlays[0].Handle);

        HWND before = PInvoke.GetForegroundWindow();
        Assert.True(PInvoke.GetCursorPos(out System.Drawing.Point pointer));
        bool shiftDown = false;
        try
        {
            // Another window of this process has the keyboard when Shift goes down; the press then activates the canvas.
            RealInput.Click(tile.Left + 100, tile.Top + 100, overlay);
            Assert.True(await ZOrder.Until(() => (nint)PInvoke.GetForegroundWindow() == overlay), "The click did not activate the canvas.");
            Assert.True(PInvoke.SetForegroundWindow((HWND)app.Window));
            Assert.True(await ZOrder.Until(() => (nint)PInvoke.GetForegroundWindow() == app.Window), "The other window did not take the foreground.");
            await harness.Run(() => harness.Sink.Pointers.Clear());
            RealInput.Key(VIRTUAL_KEY.VK_SHIFT, up: false);
            shiftDown = true;
            await Drag(harness, overlay, tile);
            RecordedPointer[] activating = await Pressed(harness);

            // The canvas has the keyboard when Shift goes down.
            await harness.Run(() => harness.Sink.Pointers.Clear());
            await Drag(harness, overlay, tile);
            RecordedPointer[] active = await Pressed(harness);
            RealInput.Key(VIRTUAL_KEY.VK_SHIFT, up: true);
            shiftDown = false;

            Assert.NotEmpty(activating);
            Assert.All(activating, input => Assert.True(input.Modifiers.HasFlag(ModifierKeys.Shift), $"{input.Kind} without Shift after activation"));
            Assert.NotEmpty(active);
            Assert.All(active, input => Assert.True(input.Modifiers.HasFlag(ModifierKeys.Shift), $"{input.Kind} without Shift"));
        }
        finally
        {
            if (shiftDown)
            {
                RealInput.Key(VIRTUAL_KEY.VK_SHIFT, up: true);
            }
            await harness.Run(() => harness.Manager.SetInputMode(OverlayInputMode.Hidden));
            PInvoke.SetCursorPos(pointer.X, pointer.Y);
            if (PInvoke.IsWindow(before))
            {
                PInvoke.SetForegroundWindow(before);
            }
        }
    }

    private static async Task Drag(OverlayHarness harness, nint overlay, PixelRect tile)
    {
        RealInput.Press(tile.Left + 20, tile.Top + 60, overlay);
        for (int step = 1; step <= 6; step++)
        {
            RealInput.DragTo(tile.Left + 20 + step * 12, tile.Top + 60 - step * 5, overlay);
        }
        RealInput.Release();
        for (int attempt = 0; attempt < 100 && !await harness.Run(() => harness.Sink.Pointers.Any(input => input.Kind == PointerInputKind.Up)); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The press and the moves with the button held.</summary>
    private static Task<RecordedPointer[]> Pressed(OverlayHarness harness) => harness.Run(() => harness.Sink.Pointers
        .Where(input => input.Kind == PointerInputKind.Down || input.Kind == PointerInputKind.Move && input.Buttons.HasFlag(PointerButtons.Left))
        .ToArray());
}
