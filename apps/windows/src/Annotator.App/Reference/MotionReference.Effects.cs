using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Annotator.App.Overlay;
using Annotator.Core;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Workspace;
using CorePoint = Annotator.Core.Point;
using CoreRect = Annotator.Core.Rect;
using PointerButtons = Annotator.Platform.Input.PointerButtons;

namespace Annotator.App.Reference;

/// <summary>
/// The <c>effects-live</c> take (UXLiveRecordingTests.recordEffects) and the effects measurement on its stage. The effects
/// read the recording's desktop through the app's capture service; the overlay's reports reach the workspace as they do
/// in <c>AppHost</c>.
/// </summary>
internal sealed partial class MotionReferenceHost
{
    /// <summary>The Mac's regions, in the sample content's points: Blur over the picture, Pixelate over the trail map and Distort over the chart.</summary>
    private static readonly (Tool Tool, CoreRect Rect, string Label)[] s_effectRegions =
    [
        (Tool.Blur, new CoreRect(40, 40, 488, 296), "blur"),
        (Tool.Pixelate, new CoreRect(588, 446, 552, 180), "pixelate"),
        (Tool.Distort, new CoreRect(580, 248, 290, 160), "distort"),
    ];

    private readonly List<(string State, long Ticks)> _effectReports = [];
    private readonly List<(int Phase, string Label, EffectsFacts Start, EffectsFacts End)> _measurements = [];
    private bool _effectRegions;
    private bool _effectsReady;

    /// <summary>What the app knows about itself at a phase boundary of the measurement.</summary>
    private readonly record struct EffectsFacts(long Ticks, TimeSpan Cpu, long Allocated, long WorkingSet, long PrivateBytes, int Threads,
                                                long Frames, long Skipped, long Repeated, bool Capturing, Platform.Capture.CaptureStatistics Capture);

    /// <summary>
    /// The Mac's recordEffects: each region's tool, 0.3 s, a drag over the content until the region shows live pixels,
    /// 1.0 s; the blur selected, its strength from 18 to 60 to 4 in 50 ms steps; an existing ink cover over the blur; Use apps.
    /// </summary>
    private async Task PlayEffectsAsync()
    {
        WorkspaceModel model = _model!;
        Guid? blur = null;
        foreach (var (tool, rect, label) in s_effectRegions)
        {
            Step(label + " tool", () => model.Choose(tool));
            await WaitAsync(0.3);
            Guid created = default;
            await EffectSegmentAsync(label, async () =>
            {
                created = await DragAsync(tool, MacLiveStage.Design(rect.X, rect.Y), MacLiveStage.Design(rect.MaxX, rect.MaxY));
                await Until(() => _effectsReady, $"The {label} region showed no live pixels within 6 seconds.", 6000);
            });
            blur ??= tool == Tool.Blur ? created : null;
            await WaitAsync(1.0);
        }
        Step("select the blur", () =>
        {
            model.Choose(Tool.Select);
            Edit(document =>
            {
                document.Select([blur!.Value]);
                return document;
            });
        });
        await WaitAsync(0.4);
        await EffectSegmentAsync("blur strength 18 to 60 to 4", async () =>
        {
            for (var amount = 18.0; amount <= 60; amount += 3)
            {
                await StrengthAsync(amount);
            }
            for (var amount = 60.0; amount >= 4; amount -= 4)
            {
                await StrengthAsync(amount);
            }
            await Until(() => _effectsReady, "The blur showed no live pixels within 6 seconds of its last strength.", 6000);
        });
        await WaitAsync(0.8);
        // The toolbar no longer creates solid covers, but covers in existing boards still render above effects.
        await EffectSegmentAsync("existing solid cover over the blur", async () =>
        {
            Edit(document =>
            {
                var cover = new Element(Tool.Cover, MacLiveStage.DesignRect(360, 210, 280, 120));
                cover.Style.Color = Palette.Ink;
                document.Add(cover);
                document.Select([]);
                return document;
            });
            await WaitAsync(0.4);
        });
        await WaitAsync(1.0);
        await EffectSegmentAsync("use apps", async () =>
        {
            model.UseApps();
            await WaitAsync(1.2);
        });
    }

    private async Task StrengthAsync(double amount)
    {
        _model!.ApplyStyle((ref Style style) => style.EffectAmount = amount);
        await WaitAsync(0.05);
    }

    /// <summary>
    /// Not a Mac take: in Use apps, 4 s with no region, then the take's three regions live over still content for 6 s and
    /// over content the harness changes for 6 s, then 4 s after deleting them. The harness reads the phases.
    /// </summary>
    private async Task MeasureEffectsAsync()
    {
        WorkspaceModel model = _model!;
        model.UseApps();
        await WaitAsync(1.0);
        _takeStart = Stopwatch.GetTimestamp();
        await MeasurePhaseAsync(5, "no region", 4);
        // Canvas edits need Draw; the measured phases run in Use apps.
        model.ResumeDrawing();
        Edit(document =>
        {
            foreach (var (tool, rect, _) in s_effectRegions)
            {
                document.Add(new Element(tool, MacLiveStage.DesignRect(rect.X, rect.Y, rect.Width, rect.Height)));
            }
            document.Select([]);
            return document;
        });
        model.UseApps();
        _events.Add(("regions added", Stopwatch.GetTimestamp()));
        await Until(() => _effectsReady, "The regions showed no live pixels within 8 seconds.", 8000);
        _events.Add(("regions live", Stopwatch.GetTimestamp()));
        await WaitAsync(1.5);
        await MeasurePhaseAsync(6, "regions over still content", 6);
        await MeasurePhaseAsync(7, "regions over changing content", 6);
        model.ResumeDrawing();
        Edit(document =>
        {
            document.ClearProtection();
            return document;
        });
        model.UseApps();
        _events.Add(("regions deleted", Stopwatch.GetTimestamp()));
        await Until(() => !_effectRegions && !_capture!.IsCapturing, "Capture did not stop within 5 seconds of deleting the regions.", 5000);
        _events.Add(("capture stopped", Stopwatch.GetTimestamp()));
        await WaitAsync(1.5);
        await MeasurePhaseAsync(8, "regions deleted", 4);
        _takeEnd = Stopwatch.GetTimestamp();
        Phase(0, _takeEnd);
    }

    private async Task MeasurePhaseAsync(int phase, string label, double seconds, Func<long, Task>? activity = null)
    {
        EffectsFacts start = await FactsAsync(opening: true);
        Phase(phase, start.Ticks);
        // One timer, so the measured window holds only the app's own work; an activity runs until the window ends instead.
        await (activity?.Invoke(start.Ticks + Seconds(seconds)) ?? Task.Delay(TimeSpan.FromSeconds(seconds)));
        EffectsFacts end = await FactsAsync(opening: false);
        Phase(9, end.Ticks);
        _measurements.Add((phase, label, start, end));
    }

    /// <summary>
    /// The app's counters now. Managed allocations are read last when a window opens and first when it closes, so the
    /// window does not count what reading the counters allocates. Frames are those of the effect layer, screen zoom's view
    /// and the shared region together.
    /// </summary>
    private async Task<EffectsFacts> FactsAsync(bool opening)
    {
        var allocated = opening ? 0 : GC.GetTotalAllocatedBytes(precise: true);
        (long frames, long skipped, long repeated) = await _overlays!.RunAsync(shown =>
        {
            long drawn = 0, missed = 0, same = 0;
            void Add(Annotator.Rendering.Effects.EffectsStatistics? statistics)
            {
                if (statistics is not null)
                {
                    drawn += statistics.Frames;
                    missed += statistics.Skipped;
                    same += statistics.Repeated;
                }
            }
            foreach (IOverlayCanvas canvas in shown.Values)
            {
                if (canvas is DrawingCanvas drawing)
                {
                    Add(drawing.EffectsStatistics);
                    Add(drawing.ZoomScreenStatistics);
                }
            }
            Add(_factory?.SharedOutput()?.FrameStatistics);
            return (drawn, missed, same);
        });
        using var process = Process.GetCurrentProcess();
        var facts = new EffectsFacts(Stopwatch.GetTimestamp(), process.TotalProcessorTime, allocated, process.WorkingSet64, process.PrivateMemorySize64,
            process.Threads.Count, frames, skipped, repeated, _capture?.IsCapturing ?? false, _capture?.Statistics ?? default);
        return opening ? facts with { Allocated = GC.GetTotalAllocatedBytes(precise: true) } : facts;
    }

    private async Task EffectSegmentAsync(string label, Func<Task> body)
    {
        var start = Stopwatch.GetTimestamp();
        await body();
        var end = Stopwatch.GetTimestamp();
        _segments.Add((label, start, end, end));
    }

    /// <summary>
    /// The Mac's drag: the scripted pointer travels to the start (travel(to:seconds:) with 0.4 s), then a real canvas drag of
    /// 54 moves 16 ms apart (0.9 s at 60 per second). Returns the new region once the workspace has it.
    /// </summary>
    private async Task<Guid> DragAsync(Tool tool, CorePoint from, CorePoint to)
    {
        OverlayHost overlays = _overlays!;
        HashSet<Guid> before = [.. Elements().Select(element => element.Id)];
        _scriptedInput = true;
        Post();
        try
        {
            await TravelAsync(from, 0.4);
            NotePointer(from, pressed: true);
            await overlays.RunAsync(shown => Pointer(shown, PointerInputKind.Down, from));
            const int Frames = 54;
            for (var frame = 1; frame <= Frames; frame++)
            {
                var t = frame / (double)Frames;
                var point = new CorePoint(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
                MoveScripted(point);
                await overlays.RunAsync(shown => Pointer(shown, PointerInputKind.Move, point));
                await WaitAsync(0.016);
            }
            NotePointer(to, pressed: false);
            await overlays.RunAsync(shown => Pointer(shown, PointerInputKind.Up, to));
            var created = Guid.Empty;
            await Until(() =>
            {
                foreach (Element element in Elements())
                {
                    if (element.Tool == tool && !before.Contains(element.Id))
                    {
                        created = element.Id;
                    }
                }
                return created != Guid.Empty;
            }, $"The dragged {tool} region did not reach the workspace within 2 seconds.", 2000);
            return created;
        }
        finally
        {
            _scriptedInput = false;
            Post();
        }
    }

    /// <summary>One mouse sample at <paramref name="point"/> (points on the stage display) through the canvas's pointer input.</summary>
    private bool Pointer(IReadOnlyDictionary<OverlayWindow, IOverlayCanvas> shown, PointerInputKind kind, CorePoint point)
    {
        Span<PointerSample> samples = [new PointerSample(new Vector2((float)(point.X * _scale), (float)(point.Y * _scale)), 1, (uint)Environment.TickCount)];
        ((DrawingCanvas)shown.Values.Single()).ScriptPointer(new PointerInput
        {
            Kind = kind,
            Device = PointerDeviceKind.Mouse,
            PointerId = 1,
            Buttons = kind == PointerInputKind.Up ? PointerButtons.None : PointerButtons.Left,
            ChangedButton = kind == PointerInputKind.Move ? PointerButtons.None : PointerButtons.Left,
            Samples = samples,
        });
        return true;
    }

    private ElementList Elements() => _model!.Snapshot.Display(new DisplayId(DisplayName))?.Document.Elements ?? default;

    /// <summary>Submits a change to the stage display's document, as a canvas submits an edit.</summary>
    private void Edit(Func<CanvasDocument, CanvasDocument> change)
    {
        WorkspaceModel model = _model!;
        DisplaySnapshot canvas = model.Snapshot.Display(new DisplayId(DisplayName)) ?? throw new InvalidOperationException("The stage display has no canvas.");
        if (model.Submit(new EditSubmission(new DisplayEdit(canvas.Id, canvas.Version, change(canvas.Document)))) is EditResult.Rejected rejected)
        {
            throw new InvalidOperationException($"The edit was refused: {rejected.Reason}.");
        }
    }

    /// <summary>The overlay's effect report, as AppHost.OnEffects handles it, and its state on the take's clock.</summary>
    private void NoteEffects(OverlayEvent.Effects effects)
    {
        WorkspaceModel? model = _model;
        model?.SetEffectsState(effects.Display, effects.Regions, effects.PixelsAvailable);
        if (effects.Message is { } failure)
        {
            _failures.Add($"Live effects: {failure} {effects.Detail}".ReplaceLineEndings(" | "));
            model?.ReportCaptureFailure(failure);
        }
        else if (effects.PixelsAvailable)
        {
            model?.NoteCaptureFrame();
        }
        _effectRegions = effects.Regions;
        _effectsReady = effects.Regions && effects.PixelsAvailable;
        _effectReports.Add((effects.Message is not null ? "failed" : !effects.Regions ? "no regions" : effects.PixelsAvailable ? "live" : "waiting",
            Stopwatch.GetTimestamp()));
    }

    private void WriteEffects(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("effectReports");
        foreach (var (state, ticks) in _effectReports)
        {
            writer.WriteStartObject();
            writer.WriteString("state", state);
            writer.WriteNumber("ticks", ticks);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("measurements");
        foreach (var (phase, label, start, end) in _measurements)
        {
            writer.WriteStartObject();
            writer.WriteNumber("phase", phase);
            writer.WriteString("label", label);
            writer.WriteNumber("startTicks", start.Ticks);
            writer.WriteNumber("endTicks", end.Ticks);
            var seconds = (end.Ticks - start.Ticks) / (double)Stopwatch.Frequency;
            writer.WriteNumber("seconds", seconds);
            writer.WriteNumber("cpuMilliseconds", (end.Cpu - start.Cpu).TotalMilliseconds);
            writer.WriteNumber("allocatedBytes", end.Allocated - start.Allocated);
            writer.WriteNumber("effectFrames", end.Frames - start.Frames);
            writer.WriteNumber("skippedFrames", end.Skipped - start.Skipped);
            writer.WriteNumber("repeatedFrames", end.Repeated - start.Repeated);
            writer.WriteNumber("capturedFrames", end.Capture.Received - start.Capture.Received);
            writer.WriteNumber("unchangedFrames", end.Capture.Unchanged - start.Capture.Unchanged);
            writer.WriteNumber("comparedFrames", end.Capture.Compared - start.Capture.Compared);
            writer.WriteNumber("renotified", end.Capture.Renotified - start.Capture.Renotified);
            writer.WriteNumber("comparisonsRead", end.Capture.ResultsRead - start.Capture.ResultsRead);
            writer.WriteNumber("comparisonWaitMilliseconds", (end.Capture.ResultDelay - start.Capture.ResultDelay).TotalMilliseconds);
            writer.WriteNumber("longestComparisonWaitMilliseconds", end.Capture.ResultDelayMax.TotalMilliseconds);
            writer.WriteNumber("captureSessions", end.Capture.SessionsStarted);
            writer.WriteNumber("captureSessionsReused", end.Capture.SessionsReused);
            writer.WriteNumber("workingSet", end.WorkingSet);
            writer.WriteNumber("privateBytes", end.PrivateBytes);
            writer.WriteNumber("threads", end.Threads);
            writer.WriteBoolean("capturingAtStart", start.Capturing);
            writer.WriteBoolean("capturingAtEnd", end.Capturing);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
