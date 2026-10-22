using System.Diagnostics;
using Annotator.Workspace;
using CorePoint = Annotator.Core.Point;

namespace Annotator.App.Reference;

/// <summary>
/// The region sharing and screen zoom measurements on the live stage (no Mac takes), timed and read as
/// <see cref="MeasureEffectsAsync"/> does. The shared memory's phase tells the harness what to measure and when to change the
/// stage: in phase 7 under the shared region or the zoomed view, in phase 11 beside it.
/// </summary>
internal sealed partial class MotionReferenceHost
{
    /// <summary>The scripted pointer's circle in phase 10, in points; it turns once a second.</summary>
    private const double CircleRadius = 60;

    private bool _zoomPresented;
    private long _pointerMoves;

    /// <summary>
    /// In Use apps: 4 s without sharing; then sharing-live's region shared into the output beside the stage, and 6 s each over
    /// still content, while the harness changes the content in the region, while it changes the content beside the region,
    /// and while the scripted pointer circles over the region; 4 s after Stop sharing.
    /// </summary>
    private async Task MeasureSharingAsync()
    {
        WorkspaceModel model = _model!;
        model.UseApps();
        await WaitAsync(1.0);
        _takeStart = Stopwatch.GetTimestamp();
        await MeasurePhaseAsync(5, "not sharing", 4);
        await StartSharingAsync(recorded: false);
        _events.Add(("sharing live", Stopwatch.GetTimestamp()));
        await Task.Delay(1500);
        await MeasurePhaseAsync(6, "sharing over still content", 6);
        await MeasurePhaseAsync(7, "sharing over changing content", 6);
        await MeasurePhaseAsync(11, "sharing, changes beside the region", 6);
        // The middle of sharing-live's region, from (12, 12) to (560, 652) of the sample content.
        CorePoint center = MacLiveStage.Design(286, 332);
        await MeasurePhaseAsync(10, "sharing while the pointer moves", 6, end => CircleAsync(end, center, point => _overlays!.RunAsync(_ => MovePointer(point))));
        _events.Add(($"pointer moves: {_pointerMoves}", Stopwatch.GetTimestamp()));
        _sharing!.Stop();
        _events.Add(("sharing stopped", Stopwatch.GetTimestamp()));
        await Until(() => !_capture!.IsCapturing, "Capture did not stop within 5 seconds of Stop sharing.", 5000);
        _events.Add(("capture stopped", Stopwatch.GetTimestamp()));
        await Task.Delay(1500);
        await MeasurePhaseAsync(8, "sharing stopped", 4);
        _takeEnd = Stopwatch.GetTimestamp();
        Phase(0, _takeEnd);
    }

    /// <summary>
    /// In Use apps: 4 s without zoom; then screen zoom at 2x with its viewport at the stage's pointer, and 6 s each over still
    /// content, while the harness changes the content in the zoomed view, while it changes the content outside the view, and
    /// while the scripted pointer circles (the viewport follows it); 4 s after zoom stopped.
    /// </summary>
    private async Task MeasureZoomAsync()
    {
        WorkspaceModel model = _model!;
        model.UseApps();
        await WaitAsync(1.0);
        _takeStart = Stopwatch.GetTimestamp();
        await MeasurePhaseAsync(5, "no zoom", 4);
        CorePoint center = MacLiveStage.Pointer;
        model.ToggleScreenZoom();
        _events.Add(("zoom requested", Stopwatch.GetTimestamp()));
        // Zoom's hooks are not installed here: the script moves its pointer, from the first zoomed frame on.
        await Until(() =>
        {
            ZoomPointer(center);
            return _zoomPresented;
        }, "Screen zoom showed no zoomed frame within 8 seconds.", 8000);
        _events.Add(("zoom presented", Stopwatch.GetTimestamp()));
        await Task.Delay(1500);
        ZoomPointer(center);
        await MeasurePhaseAsync(6, "zoom over still content", 6);
        await MeasurePhaseAsync(7, "zoom over changing content", 6);
        await MeasurePhaseAsync(11, "zoom, changes outside the view", 6);
        await MeasurePhaseAsync(10, "zoom while the pointer moves", 6, end => CircleAsync(end, center, point =>
        {
            ZoomPointer(point);
            return Task.CompletedTask;
        }));
        _events.Add(($"pointer moves: {_pointerMoves}", Stopwatch.GetTimestamp()));
        model.StopZoom();
        _events.Add(("zoom stopped", Stopwatch.GetTimestamp()));
        await Until(() => !_capture!.IsCapturing, "Capture did not stop within 5 seconds of stopping zoom.", 5000);
        _events.Add(("capture stopped", Stopwatch.GetTimestamp()));
        await Task.Delay(1500);
        await MeasurePhaseAsync(8, "zoom stopped", 4);
        _takeEnd = Stopwatch.GetTimestamp();
        Phase(0, _takeEnd);
    }

    /// <summary>Zoom's pointer moves to <paramref name="point"/> (points on the stage display), as its hook reports a move.</summary>
    private void ZoomPointer(CorePoint point) =>
        _factory!.PointerMoved((int)Math.Round(_display.Left + point.X * _scale), (int)Math.Round(_display.Top + point.Y * _scale));

    /// <summary>
    /// Moves the scripted pointer around <paramref name="center"/> until <paramref name="end"/> (Stopwatch ticks): one turn a
    /// second, a move about every 16 ms.
    /// </summary>
    private async Task CircleAsync(long end, CorePoint center, Func<CorePoint, Task> move)
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() < end)
        {
            var angle = Stopwatch.GetElapsedTime(start).TotalSeconds * 2 * Math.PI;
            await move(new CorePoint(center.X + CircleRadius * Math.Cos(angle), center.Y + CircleRadius * Math.Sin(angle)));
            _pointerMoves++;
            await Task.Delay(16);
        }
    }
}
