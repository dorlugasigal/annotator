using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Annotator.App.Motion;
using Annotator.App.Overlay;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using CoreRect = Annotator.Core.Rect;

namespace Annotator.App.Reference;

/// <summary>
/// A development-only motion run for measurements and contact sheets (WIN-MOT-02, tools/Annotator.RenderHarness
/// <c>motion</c>): <c>--motion-reference dissolve --area 2560,560,1280,720 --out &lt;folder&gt;</c>. The real toolbar,
/// overlay, canvas and motion show a sample board on a made-up display that covers <c>--area</c> of the screen, over an
/// opaque backdrop on the overlay, so captures contain only Annotator's pixels. It hides and shows once with the style,
/// writes <c>motion.json</c> (times on the steady clock, CPU time, snapshots and animations) and quits. It runs beside a
/// running Annotator: no single-instance check, tray icon, global shortcut or saved setting, and nothing takes focus or
/// input.
/// </summary>
internal sealed record MotionReferenceRequest(VisibilityAnimationStyle Style, PixelRect Area, string Output, int TimeoutSeconds,
                                              bool ReduceMotion, bool Dark, int Objects)
{
    public const string Switch = "--motion-reference";

    /// <summary>The shared memory where the run says which transition plays, for the capture to pick its frames.</summary>
    public const string PhaseMapName = @"Local\Annotator.MotionReference.Phase";

    /// <summary>
    /// <c>--take show-hide|reversal|controls|effects|effects-measure|sharing|sharing-measure|zoom-measure|zoom-screen|zoom-lens</c>:
    /// replays that take of the Mac's live recordings on the Mac's stage, with <c>--area</c> as the recorded 1,440 by 900-point
    /// area, instead of one hide and show of the sample board. The recording shows its own desktop under the canvas, as the
    /// Mac's owned backdrop and sample window. The <c>-measure</c> takes are no Mac takes but measurements on the same stage.
    /// </summary>
    public MacLiveTake? Take { get; init; }

    public static MotionReferenceRequest Parse(IReadOnlyList<string> args)
    {
        var style = VisibilityAnimationStyle.FromRawValue(Value(args, Switch))
            ?? throw new FormatException($"{Switch} needs a style such as dissolve, slide, magic, corners, pop, drop, focus or fade.");
        int[] area = [.. (Option(args, "--area") ?? throw new FormatException("--area x,y,width,height is required."))
            .Split(',').Select(part => int.Parse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))];
        if (area.Length != 4 || area[2] < 400 || area[3] < 300)
        {
            throw new FormatException("--area is x,y,width,height in desktop pixels, at least 400 by 300.");
        }
        var output = Path.GetFullPath(Option(args, "--out") ?? throw new FormatException("--out <folder> is required."));
        var timeout = Option(args, "--timeout") is { } seconds ? int.Parse(seconds, NumberStyles.None, CultureInfo.InvariantCulture) : 30;
        var objects = Option(args, "--objects") is { } count ? int.Parse(count, NumberStyles.None, CultureInfo.InvariantCulture) : 6;
        MacLiveTake? take = Option(args, "--take") switch
        {
            null => null,
            "show-hide" => MacLiveTake.ShowHide,
            "reversal" => MacLiveTake.Reversal,
            "controls" => MacLiveTake.Controls,
            "effects" => MacLiveTake.Effects,
            "effects-measure" => MacLiveTake.EffectsMeasure,
            "sharing" => MacLiveTake.Sharing,
            "sharing-measure" => MacLiveTake.SharingMeasure,
            "zoom-measure" => MacLiveTake.ZoomMeasure,
            "zoom-screen" => MacLiveTake.ZoomScreen,
            "zoom-lens" => MacLiveTake.ZoomLens,
            _ => throw new FormatException("--take is show-hide, reversal, controls, effects, effects-measure, sharing, sharing-measure, zoom-measure, zoom-screen or zoom-lens."),
        };
        return new MotionReferenceRequest(style, PixelRect.FromSize(area[0], area[1], area[2], area[3]), output, Math.Clamp(timeout, 5, 120),
            Array.IndexOf([.. args], "--reduce-motion") >= 0, Option(args, "--theme") == "dark", Math.Clamp(objects, 0, 64))
        {
            Take = take,
        };
    }

    private static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (args[index] == name)
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static string Value(IReadOnlyList<string> args, string name) =>
        Option(args, name) ?? throw new FormatException($"{name} needs a value.");
}

/// <summary>Runs one <see cref="MotionReferenceRequest"/> on the app thread.</summary>
internal sealed partial class MotionReferenceHost
{
    private const string DisplayName = "motion-reference";
    private readonly MotionReferenceRequest _request;
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
    private readonly WindowRegistry _registry = new();
    private readonly List<string> _failures = [];
    private readonly List<Dictionary<string, object>> _runs = [];
    private WorkspaceModel? _model;
    private OverlayHost? _overlays;
    private ToolbarWindow? _toolbar;
    private VisibilityHandoff? _handoff;
    private DrawingCanvasFactory? _factory;
    private Platform.Capture.CaptureService? _capture;
    private MemoryMappedFile? _phaseMap;
    private MemoryMappedViewAccessor? _phase;
    private bool _firstFrame;
    private bool _stopping;

    /// <summary>The effects take's own drags reach the canvas; other pointer input stays blocked during a live take.</summary>
    private bool _scriptedInput;

    private MotionReferenceHost(MotionReferenceRequest request) => _request = request;

    public static void Start(MotionReferenceRequest request) => _ = new MotionReferenceHost(request).RunAsync();

    private async Task RunAsync()
    {
        var watchdog = Stopwatch.StartNew();
        Native.CallbackErrors.Raised += error => _failures.Add("Window message: " + error.Message);
        Platform.NativeCallbackErrors.Unhandled += error => _failures.Add("Overlay message: " + error.Message);
        var deadline = _queue.CreateTimer();
        deadline.Interval = TimeSpan.FromSeconds(_request.TimeoutSeconds);
        deadline.IsRepeating = false;
        deadline.Tick += (_, _) => Environment.Exit(124);
        deadline.Start();
        try
        {
            Directory.CreateDirectory(_request.Output);
            File.Delete(Path.Combine(_request.Output, "motion.json"));
            _phaseMap = MemoryMappedFile.CreateOrOpen(MotionReferenceRequest.PhaseMapName, 64);
            _phase = _phaseMap.CreateViewAccessor();
            Phase(0, 0);
            if (_request.Take is { } take)
            {
                await SetUpLiveAsync();
                await PlayTakeAsync(take);
                return;
            }
            await SetUpAsync();
            Phase(3, Stopwatch.GetTimestamp());
            await Task.Delay(600);
            await PlayAsync(showing: false);
            await Task.Delay(500);
            await PlayAsync(showing: true);
            await Task.Delay(500);
        }
        catch (Exception error)
        {
            _failures.Add(error.ToString());
        }
        finally
        {
            Write(watchdog.Elapsed);
            await StopAsync();
        }
    }

    private async Task SetUpAsync()
    {
        PixelRect area = _request.Area;
        // The made-up display is the area itself, at the scale of the monitor it is on.
        Shell.MonitorArea monitor = Shell.WindowChrome.MonitorAt(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var scale = monitor.Scale;
        var bounds = new CoreRect(area.Left, area.Top, area.Width, area.Height);
        var display = new DisplayDescriptor(new Annotator.Workspace.DisplayId(DisplayName), bounds, bounds, scale);
        var preferences = new WorkspacePreferences { VisibilityAnimationStyle = _request.Style, AnimateVisibility = true };
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [display],
            PreferencesStore = new MemoryPreferencesStore(preferences.Data()),
            AnimatesVisibility = true,
            ReducesMotion = () => _request.ReduceMotion,
            TextEditingAvailable = false,
        });
        _model = model;
        model.ShowToolbar();
        model.BeginDrawing();
        SubmitSample(model, display);
        model.UseApps();

        // An opaque backdrop under the overlay's content, so captures of the area hold only Annotator's pixels.
        var level = _request.Dark ? 0x2B / 255.0 : 0xE8 / 255.0;
        _factory = new DrawingCanvasFactory(Report, static (_, _) => { }) { ReferenceDark = _request.Dark, ReferenceBackdrop = new RGBA(level, level, level), ShowsHints = false };
        OverlayHost overlays = OverlayHost.StartForReference(_registry, new WorkspaceChannel(_queue, model), Report, _factory,
            () => [new DisplayInfo(0, DisplayName, area, area, (uint)Math.Round(96 * scale), IsPrimary: false)]);
        _overlays = overlays;
        var available = new Windows.Foundation.Size(area.Width / scale - 32, area.Height / scale - 64);
        var toolbar = new ToolbarWindow(model, _registry, static () => { }, static (_, _) => { },
            new ToolbarReferencePlacement(available, new PointInt32(area.Left + (int)(16 * scale), area.Top + (int)(12 * scale))));
        _toolbar = toolbar;
        toolbar.FirstFrame += () => _firstFrame = true;
        _handoff = new VisibilityHandoff(model, toolbar, () => overlays.IsReady, Post);
        model.Published += OnPublished;
        OnPublished(model.Snapshot);
        overlays.CreateOverlays();
        await Until(() => overlays.IsReady && _firstFrame, "The overlay or the toolbar did not start within 10 seconds.");
        await Task.Delay(300);
    }

    private void OnPublished(WorkspaceSnapshot snapshot)
    {
        _handoff!.BeforeApply(snapshot);
        Post();
        _toolbar!.Apply(_handoff.ToolbarView(snapshot));
        _handoff.AfterApply(snapshot);
    }

    /// <summary>The overlay's reports, on the app thread: the motion's start for the capture, then the hand-off, then failures.</summary>
    private void Report(OverlayEvent report) => _queue.TryEnqueue(() =>
    {
        if (report is OverlayEvent.VisibilityStarted started)
        {
            _phase?.Write(24, started.Started);
            _phase?.Write(32, started.Duration);
            NoteStart(started);
        }
        if (report is OverlayEvent.ToolbarCovered or OverlayEvent.ToolbarFallback)
        {
            NoteToolbar(report);
        }
        if (_handoff?.Handle(report) == true)
        {
            return;
        }
        switch (report)
        {
            case OverlayEvent.Failure failure:
                _failures.Add($"{failure.Operation}: {failure.Error.Message}");
                break;
            case OverlayEvent.Devices { Error: { } error }:
                _failures.Add($"Graphics devices: {error.Message}");
                break;
            case OverlayEvent.Effects effects when !_stopping:
                NoteEffects(effects);
                break;
            case OverlayEvent.SharingPresented or OverlayEvent.SharingWaiting or OverlayEvent.SharingFailed or OverlayEvent.SharingStopped
                or OverlayEvent.SharingClosed:
                NoteSharing(report);
                break;
            case OverlayEvent.ZoomPresented:
                _model?.NoteZoomPresented();
                _zoomPresented = true;
                break;
            case OverlayEvent.ZoomFailed failed:
                _model?.ReportZoomFailure(failed.Display, failed.Mode, failed.Message);
                _failures.Add($"Live zoom: {failed.Message} {failed.Detail}".ReplaceLineEndings(" | "));
                break;
        }
    });

    private void Post()
    {
        if (_model is { } model)
        {
            // The live take shows Draw as the Mac's stage does; a stray click must not draw on it.
            _overlays?.Post(new OverlayFrame(model.Snapshot, InputBlocked: _request.Take is not null && !_scriptedInput) { Toolbar = _handoff?.Toolbar });
        }
    }

    private async Task PlayAsync(bool showing)
    {
        WorkspaceModel model = _model!;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime;
        var (animations, keyframes) = _factory!.Visibility?.Created ?? (0, 0);
        var start = Stopwatch.GetTimestamp();
        Phase(showing ? 2 : 1, start);
        if (showing)
        {
            model.ShowToolbar();
        }
        else
        {
            model.HideTools();
        }
        VisibilityTransition? transition = model.Snapshot.Visibility;
        await Until(() => model.Snapshot.Visibility is null, "The transition did not end within 5 seconds.", 5000);
        var end = Stopwatch.GetTimestamp();
        Phase(0, end);
        process.Refresh();
        var (animationsAfter, keyframesAfter) = _factory.Visibility?.Created ?? (0, 0);
        _runs.Add(new Dictionary<string, object>
        {
            ["direction"] = showing ? "show" : "hide",
            ["style"] = transition?.Style.RawValue ?? "none",
            ["startTicks"] = start,
            ["endTicks"] = end,
            ["seconds"] = Stopwatch.GetElapsedTime(start, end).TotalSeconds,
            ["motionStartTicks"] = _phase?.ReadInt64(24) ?? 0L,
            ["plannedSeconds"] = _phase?.ReadDouble(32) ?? 0.0,
            ["cpuMilliseconds"] = (process.TotalProcessorTime - cpu).TotalMilliseconds,
            ["animations"] = animationsAfter - animations,
            ["keyframes"] = keyframesAfter - keyframes,
            ["hidden"] = model.Snapshot.WorkspaceHidden,
            ["toolbarVisible"] = model.Snapshot.ToolbarVisible,
        });
    }

    /// <summary>A sample board over the area: shapes, a labeled box, an arrow, a pen wave and a highlighter mark.</summary>
    private void SubmitSample(WorkspaceModel model, DisplayDescriptor display)
    {
        WorkspaceSnapshot snapshot = model.Snapshot;
        DisplaySnapshot canvas = snapshot.Display(display.Id) ?? throw new InvalidOperationException("The sample display has no canvas.");
        CanvasDocument document = canvas.Document;
        double width = display.Bounds.Width, height = display.Bounds.Height;
        var elements = new List<Element>();
        Element Shape(Tool tool, double x, double y, double w, double h, RGBA? color = null)
        {
            var element = new Element(tool, new CoreRect(width * x, height * y, w, h), style: Style.Defaults(tool));
            if (color is { } value)
            {
                element.Style.Color = value;
            }
            return element;
        }
        var box = Shape(Tool.Rectangle, 0.08, 0.32, 200, 110);
        box.Label = new ShapeLabel("API");
        elements.Add(box);
        elements.Add(Shape(Tool.Ellipse, 0.42, 0.30, 170, 100, new RGBA(0.20, 0.55, 0.95)));
        elements.Add(new Element(Tool.Arrow, points: [new Annotator.Core.Point(width * 0.08 + 205, height * 0.32 + 55),
            new Annotator.Core.Point(width * 0.42 - 5, height * 0.30 + 50)], style: Style.Defaults(Tool.Arrow)));
        var wave = new List<Annotator.Core.Point>();
        for (var step = 0; step <= 40; step++)
        {
            var t = step / 40.0;
            wave.Add(new Annotator.Core.Point(width * 0.10 + t * width * 0.35, height * 0.75 + Math.Sin(t * Math.PI * 2) * 26));
        }
        elements.Add(new Element(Tool.Pen, points: [.. wave], style: Style.Defaults(Tool.Pen)));
        elements.Add(Shape(Tool.Diamond, 0.70, 0.58, 120, 110, new RGBA(0.95, 0.45, 0.25)));
        var marker = Style.Defaults(Tool.Highlighter);
        elements.Add(new Element(Tool.Highlighter, points: [new Annotator.Core.Point(width * 0.55, height * 0.86),
            new Annotator.Core.Point(width * 0.88, height * 0.86)], style: marker));
        var random = new Random(7);
        while (elements.Count < _request.Objects)
        {
            elements.Add(Shape(elements.Count % 2 == 0 ? Tool.Rectangle : Tool.Ellipse, 0.05 + random.NextDouble() * 0.85,
                0.25 + random.NextDouble() * 0.65, 40 + random.Next(60), 30 + random.Next(50),
                new RGBA(random.NextDouble(), random.NextDouble() * 0.6, random.NextDouble())));
        }
        foreach (Element element in elements.Take(_request.Objects))
        {
            document.Add(element);
        }
        document.Select([]);
        if (model.Submit(new EditSubmission(new DisplayEdit(canvas.Id, canvas.Version, document))) is EditResult.Rejected rejected)
        {
            throw new InvalidOperationException($"The sample objects were refused: {rejected.Reason}.");
        }
    }

    /// <summary>
    /// Publishes the run's state for the capture: the phase (0 setting up or done, 1 hiding, 2 showing, 3 idle before the
    /// hide, 4 a live take running, 5 to 11 the measurements' phases) at 0 and when it began at 8, in
    /// <see cref="Stopwatch"/> ticks at the frequency at 16; the overlay writes when the motion started at 24 and its length
    /// in seconds at 32.
    /// </summary>
    private void Phase(int phase, long ticks)
    {
        if (phase is 1 or 2)
        {
            _phase?.Write(24, 0L);
            _phase?.Write(32, 0.0);
        }
        _phase?.Write(8, ticks);
        _phase?.Write(16, Stopwatch.Frequency);
        _phase?.Write(0, phase);
    }

    private void Write(TimeSpan elapsed)
    {
        try
        {
            using FileStream stream = File.Create(Path.Combine(_request.Output, "motion.json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("style", _request.Style.RawValue);
            writer.WriteBoolean("reduceMotion", _request.ReduceMotion);
            writer.WriteNumber("objects", _request.Objects);
            writer.WriteNumber("frequency", Stopwatch.Frequency);
            writer.WriteStartArray("area");
            writer.WriteNumberValue(_request.Area.Left);
            writer.WriteNumberValue(_request.Area.Top);
            writer.WriteNumberValue(_request.Area.Width);
            writer.WriteNumberValue(_request.Area.Height);
            writer.WriteEndArray();
            writer.WriteNumber("seconds", elapsed.TotalSeconds);
            writer.WriteStartArray("runs");
            foreach (Dictionary<string, object> run in _runs)
            {
                writer.WriteStartObject();
                foreach ((string key, object value) in run)
                {
                    switch (value)
                    {
                        case string text:
                            writer.WriteString(key, text);
                            break;
                        case bool flag:
                            writer.WriteBoolean(key, flag);
                            break;
                        case long number:
                            writer.WriteNumber(key, number);
                            break;
                        case int number:
                            writer.WriteNumber(key, number);
                            break;
                        case double number:
                            writer.WriteNumber(key, number);
                            break;
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("failures");
            foreach (var failure in _failures)
            {
                writer.WriteStringValue(failure);
            }
            writer.WriteEndArray();
            if (_request.Take is not null)
            {
                WriteTake(writer);
            }
            writer.WriteEndObject();
        }
        catch (IOException)
        {
        }
    }

    private async Task StopAsync()
    {
        // The canvases report once more as they close (their effect layers end); the toolbar must not hear of it.
        _stopping = true;
        if (_model is { } model)
        {
            model.Published -= OnPublished;
        }
        _phase?.Dispose();
        _phaseMap?.Dispose();
        _sharingWindows?.Dispose();
        _toolbar?.Shutdown();
        if (_overlays is { } overlays)
        {
            await Task.WhenAny(overlays.StopAsync(), Task.Delay(5000));
        }
        if (_capture is { } capture)
        {
            await Task.WhenAny(Task.Run(capture.Dispose), Task.Delay(12_000));
        }
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private static async Task Until(Func<bool> condition, string failure, int milliseconds = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds)
            {
                throw new TimeoutException(failure);
            }
            await Task.Delay(10);
        }
    }
}
