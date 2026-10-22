using System.Diagnostics;
using System.Text.Json;
using Annotator.App.Overlay;
using Annotator.Core;
using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Workspace;
using Annotator.Workspace.Sharing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using CorePoint = Annotator.Core.Point;
using CoreRect = Annotator.Core.Rect;

namespace Annotator.App.Reference;

/// <summary>
/// The <c>sharing-live</c> take with <c>sharing-output</c> (UXLiveRecordingTests.recordSharing): the region from the sample
/// content's (12, 12) to (560, 652) shared into the output window, then the Mac's steps with real canvas drags, Magic's hide
/// and show, and Stop sharing. The output window moves beside the recorded area, and its handle goes into the shared memory
/// so the recorder captures that window on its own, as the Mac's recorder does.
/// </summary>
internal sealed partial class MotionReferenceHost
{
    /// <summary>Where the run writes the output window's handle for the recorder (8 bytes).</summary>
    internal const int OutputWindowOffset = 40;

    /// <summary>Where the recorder writes 1 once it captures the output window (4 bytes).</summary>
    internal const int OutputRecordingOffset = 48;

    private readonly List<(string State, long Ticks)> _sharingReports = [];
    private Sharing.RegionSharingController? _sharingWindows;
    private RegionSharing? _sharing;
    private RECT _outputFrame;
    private (int Transitions, int Copies, int ParticleFrames, int Covers, int HandOffs)? _sharingMotion;

    /// <summary>Where the scripted pointer is, in points on the stage display; it rests at the content's middle.</summary>
    private CorePoint _scriptedPointer = MacLiveStage.Pointer;

    private readonly record struct OutputPlace(nint Window, RECT Frame, string? Problem);

    /// <summary>
    /// Mac recordSharing before its take: applies the custom region and starts sharing, waits until the output is live,
    /// brings the output window forward beside the recorded area, waits for the recorder to capture it, then 0.6 s. Without
    /// <paramref name="recorded"/> (the measurement) nothing records the output, so it waits for neither.
    /// </summary>
    private async Task StartSharingAsync(bool recorded = true)
    {
        _phase?.Write(OutputWindowOffset, 0L);
        _phase?.Write(OutputRecordingOffset, 0);
        WorkspaceModel model = _model!;
        var windows = new Sharing.RegionSharingController(_registry, _queue, (operation, error) => _failures.Add($"{operation}: {error.Message}"));
        _sharingWindows = windows;
        var sharing = new RegionSharing(model, windows);
        windows.Attach(sharing);
        _sharing = sharing;
        CorePoint from = MacLiveStage.Design(12, 12), to = MacLiveStage.Design(560, 652);
        sharing.RefreshDisplays();
        sharing.SelectDisplay(new DisplayId(DisplayName));
        sharing.ApplyCustom(new CoreRect(from.X, from.Y, to.X - from.X, to.Y - from.Y));
        if (sharing.ValidationMessage is { } invalid)
        {
            throw new InvalidOperationException($"The shared region was refused: {invalid}");
        }
        sharing.Start();
        await Until(() => sharing.State is RegionSharingState.Live or RegionSharingState.Failed or RegionSharingState.Stopped,
                    "Region sharing did not go live within 10 seconds.");
        if (sharing.State.Message is { } message)
        {
            throw new InvalidOperationException($"Region sharing did not go live: {message}");
        }
        // Sharing put the workspace in Use apps, as on the Mac: the take starts with that toolbar.
        _toolbarFrame = _toolbar!.Frame;
        OutputPlace place = await _overlays!.RunAsync(_ => PlaceOutput(besideArea: recorded));
        if (place.Problem is { } problem)
        {
            throw new InvalidOperationException($"The output window could not move beside the recorded area: {problem}.");
        }
        _outputFrame = place.Frame;
        await _overlays.RunAsync(_ => MovePointer(_scriptedPointer));
        if (!recorded)
        {
            return;
        }
        _phase?.Write(OutputWindowOffset, (long)place.Window);
        var waited = Stopwatch.StartNew();
        while (_phase is { } phase && phase.ReadInt32(OutputRecordingOffset) != 1 && waited.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(10);
        }
        await Task.Delay(600);
    }

    /// <summary>
    /// On the overlay thread: moves the output window to the top-left or else the top-right corner of the recorded monitor's
    /// work area, wherever it covers nothing of the recorded area (with <paramref name="besideArea"/>; otherwise the top-left
    /// corner), and in front of other apps without taking focus (the Mac's orderFrontRegardless).
    /// </summary>
    private OutputPlace PlaceOutput(bool besideArea = true)
    {
        if (_factory?.SharedOutput()?.Window is not { Handle: not 0 } window)
        {
            return new OutputPlace(0, default, "there is no output window");
        }
        var hwnd = (HWND)window.Handle;
        if (!PInvoke.GetWindowRect(hwnd, out RECT frame))
        {
            return new OutputPlace(0, default, "GetWindowRect failed");
        }
        int width = frame.right - frame.left, height = frame.bottom - frame.top;
        PixelRect area = _request.Area;
        Shell.MonitorArea monitor = Shell.WindowChrome.MonitorAt(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var work = monitor.WorkArea;
        var margin = (int)Math.Round(16 * monitor.Scale);
        foreach (var left in new[] { work.X + margin, work.X + work.Width - width - margin })
        {
            var place = PixelRect.FromSize(left, work.Y + margin, width, height);
            if (besideArea && place.Right > area.Left && place.Left < area.Right && place.Bottom > area.Top && place.Top < area.Bottom)
            {
                continue;
            }
            // HWND_TOP is the null handle.
            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
            if (!PInvoke.SetWindowPos(hwnd, HWND.Null, place.Left, place.Top, 0, 0, flags) || !PInvoke.GetWindowRect(hwnd, out frame))
            {
                return new OutputPlace(0, default, "SetWindowPos failed");
            }
            return new OutputPlace(window.Handle, frame, null);
        }
        return new OutputPlace(0, default, $"a {width} by {height}-pixel window fits in neither top corner of the work area beside the area");
    }

    /// <summary>
    /// Mac recordSharing's take: 1.0 s, draw again, the Pen's circle around the sun, the Arrow, a Blur over the packing list,
    /// Magic's hide and show, and Stop sharing, with the Mac's waits.
    /// </summary>
    private async Task PlaySharingAsync()
    {
        WorkspaceModel model = _model!;
        RegionSharing sharing = _sharing!;
        // PlayTakeAsync waited 0.6 s of the Mac's 1.0 s.
        await WaitAsync(0.4);
        await EffectSegmentAsync("draw again", async () =>
        {
            model.ResumeDrawing();
            await WaitAsync(0.5);
        });
        model.Choose(Tool.Pen);
        var circle = new CorePoint[41];
        for (var index = 0; index < circle.Length; index++)
        {
            var angle = index / 40.0 * 2 * Math.PI;
            circle[index] = MacLiveStage.Design(400 + 62 * Math.Cos(angle), 96 + 52 * Math.Sin(angle));
        }
        await EffectSegmentAsync("pen stroke in the region", () => DragPathAsync(Tool.Pen, circle, 1.2));
        await WaitAsync(0.8);
        model.Choose(Tool.Arrow);
        await EffectSegmentAsync("arrow in the region", () =>
            DragPathAsync(Tool.Arrow, [MacLiveStage.Design(150, 520), MacLiveStage.Design(330, 440)], 0.7));
        await WaitAsync(0.8);
        model.Choose(Tool.Blur);
        await EffectSegmentAsync("blur in the region", async () =>
        {
            await DragPathAsync(Tool.Blur, [MacLiveStage.Design(36, 380), MacLiveStage.Design(300, 470)], 0.8);
            await Until(() => _effectsReady, "The blur showed no live pixels within 6 seconds.", 6000);
        });
        await WaitAsync(1.0);
        model.SetAnimationStyle(VisibilityAnimationStyle.Magic);
        await SegmentAsync("hide", () => model.HideTools());
        await WaitAsync(0.6);
        await SegmentAsync("show", () => model.ResumeDrawing());
        await WaitAsync(0.8);
        await EffectSegmentAsync("stop sharing", async () =>
        {
            sharing.Stop();
            await WaitAsync(1.2);
        });
        _sharingMotion = await _overlays!.RunAsync(_ => _factory?.SharedOutput()?.MotionStatistics);
    }

    /// <summary>
    /// The Mac's drag(points, seconds): the scripted pointer travels to the start (travel(to:seconds:) with 0.4 s), then a real
    /// canvas drag of seconds × 60 moves 16 ms apart along the points, evenly by distance. The shared view's arrow follows it.
    /// </summary>
    private async Task DragPathAsync(Tool tool, IReadOnlyList<CorePoint> points, double seconds)
    {
        OverlayHost overlays = _overlays!;
        HashSet<Guid> before = [.. Elements().Select(element => element.Id)];
        _scriptedInput = true;
        Post();
        try
        {
            CorePoint first = points[0], last = points[^1];
            await TravelAsync(first, 0.4);
            NotePointer(first, pressed: true);
            await overlays.RunAsync(shown => Pointer(shown, PointerInputKind.Down, first) && MovePointer(first));
            var lengths = new double[points.Count - 1];
            for (var index = 0; index < lengths.Length; index++)
            {
                lengths[index] = Math.Sqrt(Math.Pow(points[index + 1].X - points[index].X, 2) + Math.Pow(points[index + 1].Y - points[index].Y, 2));
            }
            var total = Math.Max(1, lengths.Sum());
            var frames = Math.Max(2, (int)(seconds * 60));
            for (var frame = 1; frame <= frames; frame++)
            {
                var distance = total * frame / frames;
                CorePoint location = last;
                for (var index = 0; index < lengths.Length; index++)
                {
                    if (distance <= lengths[index] || index == lengths.Length - 1)
                    {
                        var t = lengths[index] > 0 ? Math.Min(1, distance / lengths[index]) : 1;
                        location = new CorePoint(points[index].X + (points[index + 1].X - points[index].X) * t,
                                                 points[index].Y + (points[index + 1].Y - points[index].Y) * t);
                        break;
                    }
                    distance -= lengths[index];
                }
                MoveScripted(location);
                await overlays.RunAsync(shown => Pointer(shown, PointerInputKind.Move, location) && MovePointer(location));
                await WaitAsync(0.016);
            }
            NotePointer(last, pressed: false);
            await overlays.RunAsync(shown => Pointer(shown, PointerInputKind.Up, last));
            await Until(() => Elements().Any(element => element.Tool == tool && !before.Contains(element.Id)),
                        $"The {tool} did not reach the workspace within 2 seconds.", 2000);
        }
        finally
        {
            _scriptedInput = false;
            Post();
        }
    }

    /// <summary>On the overlay thread: the shared view's arrow goes where the scripted pointer is (Mac ReelPointerHook).</summary>
    private bool MovePointer(CorePoint point)
    {
        _factory?.SharedOutput()?.ScriptPointer((int)Math.Round(_display.Left + point.X * _scale), (int)Math.Round(_display.Top + point.Y * _scale));
        return true;
    }

    /// <summary>The output's reports, as AppHost forwards them to region sharing, and when they came on the take's clock.</summary>
    private void NoteSharing(OverlayEvent report)
    {
        RegionSharing? sharing = _model?.RegionSharing;
        switch (report)
        {
            case OverlayEvent.SharingPresented presented:
                sharing?.OutputPresented(presented.Generation);
                _sharingReports.Add(("live", Stopwatch.GetTimestamp()));
                break;
            case OverlayEvent.SharingWaiting waiting:
                sharing?.OutputWaiting(waiting.Generation);
                _sharingReports.Add(("waiting", Stopwatch.GetTimestamp()));
                break;
            case OverlayEvent.SharingFailed failed:
                sharing?.OutputFailed(failed.Generation, failed.Message, failed.Capture);
                _failures.Add($"Region sharing failed: {failed.Message}");
                _sharingReports.Add(("failed", Stopwatch.GetTimestamp()));
                break;
            case OverlayEvent.SharingStopped stopped:
                sharing?.OutputStopped(stopped.Generation, stopped.Message);
                _sharingReports.Add(("stopped", Stopwatch.GetTimestamp()));
                break;
            case OverlayEvent.SharingClosed:
                sharing?.OutputClosed();
                _sharingReports.Add(("closed", Stopwatch.GetTimestamp()));
                break;
        }
    }

    private void WriteSharing(Utf8JsonWriter writer)
    {
        if (_sharing is not { } sharing)
        {
            return;
        }
        writer.WriteStartObject("sharing");
        WriteRect(writer, "outputWindow", _outputFrame.left, _outputFrame.top, _outputFrame.right - _outputFrame.left, _outputFrame.bottom - _outputFrame.top);
        if (sharing.Region is { } region)
        {
            writer.WriteStartArray("regionPoints");
            writer.WriteNumberValue(region.Bounds.X);
            writer.WriteNumberValue(region.Bounds.Y);
            writer.WriteNumberValue(region.Bounds.Width);
            writer.WriteNumberValue(region.Bounds.Height);
            writer.WriteEndArray();
        }
        writer.WriteString("state", sharing.State.Title);
        writer.WriteStartArray("reports");
        foreach (var (state, ticks) in _sharingReports)
        {
            writer.WriteStartObject();
            writer.WriteString("state", state);
            writer.WriteNumber("ticks", ticks);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        LiveCheckRequest.WriteSharingMotion(writer, _sharingMotion);
        writer.WriteEndObject();
    }
}
