using System.Diagnostics;
using System.Text.Json;
using Annotator.Workspace;
using Annotator.Workspace.Zoom;
using CorePoint = Annotator.Core.Point;

namespace Annotator.App.Reference;

/// <summary>
/// The <c>zoom-screen</c> and <c>zoom-lens</c> takes (UXLiveRecordingTests.recordZoom) and the scripted pointer that the
/// live takes share. The pointer moves as the Mac recorder's does: <see cref="DemoHandMotion"/> with the recorder's seed,
/// the same travels in the same order and a move every 16 ms. Zoom and the lens follow it as they follow the mouse hook, and
/// the take writes its path, so the recorder can draw the pointer where the Mac's pointer window showed it.
/// </summary>
internal sealed partial class MotionReferenceHost
{
    /// <summary>The Mac recorder's seed (UXLiveStage.hand).</summary>
    private const ulong HandSeed = 2026101901;

    private readonly List<(long Ticks, CorePoint Point, bool Pressed)> _pointerPath = [];
    private DemoHandMotion _hand = new(HandSeed);
    private long _scriptedDesktop;
    private bool _pointerPressed;

    /// <summary>
    /// Mac recordZoom's zoom-screen: the pointer travels to the sample's picture, Ctrl+1 (the Control-1 path) ramps zoom in
    /// to 1.5x around it, the pointer travels while zoomed, eight scrolls of 6 points zoom further, and zoom ramps out.
    /// </summary>
    private async Task PlayZoomScreenAsync()
    {
        WorkspaceModel model = _model!;
        await TravelAsync(MacLiveStage.Design(400, 96), 0.6);
        await WaitAsync(0.5);
        await EffectSegmentAsync("zoom in", async () =>
        {
            model.ToggleScreenZoomFromShortcut();
            await UntilZoomAsync(model, ramp => model.Snapshot.ZoomStart.Phase == ZoomStartPhase.Active && !ramp.Ramping,
                "Screen zoom did not become active within 6 seconds.");
        });
        _events.Add(($"magnification {model.Magnification:F2}", Stopwatch.GetTimestamp()));
        await WaitAsync(0.8);
        await EffectSegmentAsync("pointer moves while zoomed", async () =>
        {
            await TravelAsync(MacLiveStage.Design(820, 300), 1.3);
            await TravelAsync(MacLiveStage.Design(260, 470), 1.1);
        });
        await WaitAsync(0.4);
        await EffectSegmentAsync("zoom further", async () =>
        {
            for (var scroll = 0; scroll < 8; scroll++)
            {
                model.ScrollScreenZoom(6);
                await WaitAsync(0.06);
            }
            model.FinishZoomGesture();
            await WaitAsync(0.4);
        });
        _events.Add(($"magnification {model.Magnification:F2}", Stopwatch.GetTimestamp()));
        await WaitAsync(0.8);
        await EffectSegmentAsync("zoom out", async () =>
        {
            model.StopZoom();
            await UntilZoomAsync(model, ramp => !ramp.Ramping && !ramp.Exiting, "Screen zoom did not ramp out within 6 seconds.");
        });
        await WaitAsync(0.8);
    }

    /// <summary>
    /// Mac recordZoom's zoom-lens, which the Mac recorded right after zoom-screen on the same stage: the pointer travels to the
    /// picture, the lens turns on, follows three travels across the chart, the trail map and the text, and turns off.
    /// </summary>
    private async Task PlayZoomLensAsync()
    {
        WorkspaceModel model = _model!;
        await TravelAsync(MacLiveStage.Design(180, 180), 0.5);
        await WaitAsync(0.4);
        await EffectSegmentAsync("lens on", async () =>
        {
            model.ToggleMagnifier();
            await Until(() => model.Snapshot.MagnifierEnabled, "The lens did not turn on within 6 seconds.", 6000);
            await WaitAsync(0.3);
        });
        await EffectSegmentAsync("lens follows the pointer", async () =>
        {
            await TravelAsync(MacLiveStage.Design(700, 170), 1.4);
            await TravelAsync(MacLiveStage.Design(900, 520), 1.0);
            await TravelAsync(MacLiveStage.Design(300, 560), 1.0);
        });
        await WaitAsync(0.4);
        await EffectSegmentAsync("lens off", async () =>
        {
            model.ToggleMagnifier();
            await WaitAsync(0.4);
        });
        await WaitAsync(0.6);
    }

    /// <summary>
    /// The state the Mac's zoom-screen take left for zoom-lens: Use apps (screen zoom gave clicks to the apps), the document's
    /// zoom at eight scrolls of 6 points above 1.5x, the pointer where its last travel ended, the pointer path generator
    /// after zoom-screen's three travels, and capture paused (the app keeps a stopped capture paused for 60 s, so the lens
    /// resumes the capture that zoom-screen used).
    /// </summary>
    private async Task SetUpAfterZoomScreenAsync(WorkspaceModel model)
    {
        model.UseApps();
        var zoom = 1.5;
        for (var scroll = 0; scroll < 8; scroll++)
        {
            zoom = ZoomScroll.Next(zoom, 6);
        }
        model.SetMagnification(zoom);
        for (var travel = 0; travel < 3; travel++)
        {
            // Each travel draws a duration, then a path's phase and offset.
            _hand.Next();
            _hand.Next();
            _hand.Next();
        }
        MoveScripted(MacLiveStage.Design(260, 470));
        if (_capture is { } capture && _stageDisplay is { } display)
        {
            var lease = capture.Acquire(display, new IgnoredFrames());
            try
            {
                await Until(() => lease.State is Platform.Capture.CaptureState.Live or Platform.Capture.CaptureState.Failed,
                            "Capture did not start within 10 seconds.");
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    /// <summary>A capture consumer that takes no frames, to start capture and leave it paused.</summary>
    private sealed class IgnoredFrames : Platform.Capture.ICaptureConsumer
    {
        public void OnFrameAvailable(Platform.Capture.ICaptureLease lease)
        {
        }

        public void OnStateChanged(Platform.Capture.ICaptureLease lease, Platform.Capture.CaptureState state)
        {
        }
    }

    /// <summary>
    /// The Mac's travel(to:seconds:): max(6, duration x 60) moves along a hand path with a 14-point bend, 16 ms apart. Paths
    /// are made in the Mac's coordinates, where y grows upward, so they bend to the same side.
    /// </summary>
    private async Task TravelAsync(CorePoint target, double seconds)
    {
        var frames = Math.Max(6, (int)(_hand.Duration(seconds) * 60));
        CorePoint[] path = _hand.Path(new CorePoint(_scriptedPointer.X, -_scriptedPointer.Y), new CorePoint(target.X, -target.Y), frames, 14);
        for (var index = 1; index < path.Length; index++)
        {
            var point = new CorePoint(path[index].X, -path[index].Y);
            MoveScripted(point);
            if (_sharing is not null)
            {
                await _overlays!.RunAsync(_ => MovePointer(point));
            }
            await WaitAsync(0.016);
        }
    }

    /// <summary>
    /// The Mac's move(_:): the scripted pointer is at <paramref name="point"/> (points on the stage display). Zoom and the lens
    /// follow it as they follow the mouse hook, and the take's pointer path records it.
    /// </summary>
    private void MoveScripted(CorePoint point)
    {
        _scriptedPointer = point;
        int x = (int)Math.Round(_display.Left + point.X * _scale), y = (int)Math.Round(_display.Top + point.Y * _scale);
        Interlocked.Exchange(ref _scriptedDesktop, ((long)x << 32) | (uint)y);
        NotePointer(point);
        if (_factory is not null)
        {
            ZoomPointer(point);
        }
    }

    /// <summary>Records where the scripted pointer is now and whether it is pressed, on the take's clock.</summary>
    private void NotePointer(CorePoint point, bool? pressed = null)
    {
        if (pressed is { } down)
        {
            _pointerPressed = down;
        }
        if (_takeStart != 0)
        {
            _pointerPath.Add((Stopwatch.GetTimestamp(), point, _pointerPressed));
        }
    }

    /// <summary>The scripted pointer in physical desktop pixels, for zoom and the lens on the overlay thread.</summary>
    private System.Drawing.Point ScriptedDesktopPointer()
    {
        var packed = Interlocked.Read(ref _scriptedDesktop);
        return new System.Drawing.Point((int)(packed >> 32), (int)packed);
    }

    /// <summary>Waits, checking every 10 ms as the Mac's waitUntil does, until <paramref name="done"/> holds for the display's zoom.</summary>
    private async Task UntilZoomAsync(WorkspaceModel model, Func<(bool Ramping, bool Exiting), bool> done, string failure)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            (bool Ramping, bool Exiting) ramp = await _overlays!.RunAsync(shown =>
            {
                foreach (Overlay.IOverlayCanvas canvas in shown.Values)
                {
                    if (canvas is Overlay.DrawingCanvas drawing)
                    {
                        return drawing.ZoomRamp;
                    }
                }
                return (false, false);
            });
            if (done(ramp))
            {
                return;
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(6) || model.Snapshot.ZoomStart.Phase == ZoomStartPhase.Failed)
            {
                throw new TimeoutException(failure + (model.Snapshot.ZoomStart.Message is { } message ? $" {message}" : ""));
            }
            await Task.Delay(10);
        }
    }

    private void WritePointerPath(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("pointer");
        foreach (var (ticks, point, pressed) in _pointerPath)
        {
            writer.WriteStartObject();
            writer.WriteNumber("ticks", ticks);
            writer.WriteNumber("x", Math.Round(point.X, 3));
            writer.WriteNumber("y", Math.Round(point.Y, 3));
            writer.WriteBoolean("pressed", pressed);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
