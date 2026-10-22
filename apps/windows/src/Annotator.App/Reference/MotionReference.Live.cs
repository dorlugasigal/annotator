using System.Diagnostics;
using System.Text.Json;
using Annotator.App.Motion;
using Annotator.App.Overlay;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.Platform.Displays;
using Annotator.Workspace;
using Windows.Graphics;
using CorePoint = Annotator.Core.Point;
using CoreRect = Annotator.Core.Rect;

namespace Annotator.App.Reference;

/// <summary>A take of the Mac's live recordings (reference/ux-reference/macos-live) that the motion run can replay.</summary>
internal enum MacLiveTake
{
    /// <summary><c>show-hide-&lt;style&gt;</c>: hide tools, then show them again, with seven drawings on the canvas.</summary>
    ShowHide,

    /// <summary><c>show-hide-reversal</c>: Slide; showing again at 45% of the hide reverses from the current position.</summary>
    Reversal,

    /// <summary>
    /// The toolbar steps of <c>sharing-live</c>: in Use apps with the Pen, draw again, then choose the Arrow and Blur, then
    /// Use apps, so the selection marker, the mode button and the properties reveal play as on the Mac.
    /// </summary>
    Controls,

    /// <summary>
    /// <c>effects-live</c>: Blur, Pixelate and Distort dragged over the sample content, the blur's strength from 18 to 60 to 4,
    /// an existing solid cover over the blur, then Use apps. The effects read the recording's desktop through the app's
    /// capture service.
    /// </summary>
    Effects,

    /// <summary>
    /// Not a Mac take: the effects-live stage measured (WIN-CAP-01, WIN-SYS-07). In Use apps, the shared memory's phase
    /// tells the harness what to measure: 5 no region, 6 the take's three regions live over still content, 7 the same
    /// while the harness changes the content under them, 8 after the regions were deleted, 9 between those phases.
    /// </summary>
    EffectsMeasure,

    /// <summary>
    /// <c>sharing-live</c> with <c>sharing-output</c>: a custom region of the sample content shared into the output window,
    /// then draw again, a pen circle, an arrow and a blur in the region, Magic's hide and show, and Stop sharing. The output
    /// window moves beside the recorded area so the recorder captures it on its own, as the Mac's recorder does.
    /// </summary>
    Sharing,

    /// <summary>
    /// Not a Mac take: region sharing measured on the stage (WIN-CAP-08, WIN-SYS-07). In Use apps: 5 not sharing, then
    /// sharing-live's region shared into the output beside the stage, 6 over still content, 7 while the harness changes the
    /// content in the region, 11 while it changes the content beside the region, 10 while the scripted pointer circles over
    /// the region, 8 after Stop sharing, 9 between those phases.
    /// </summary>
    SharingMeasure,

    /// <summary>
    /// Not a Mac take: screen zoom measured on the stage (WIN-CAP-05, WIN-SYS-07). In Use apps: 5 no zoom, then screen zoom at
    /// 2x around the stage's pointer, 6 over still content, 7 while the harness changes the content in the zoomed view, 11
    /// while it changes the content outside the view, 10 while the scripted pointer circles, 8 after zoom stopped, 9 between
    /// those phases.
    /// </summary>
    ZoomMeasure,

    /// <summary>
    /// <c>zoom-screen</c>: from Draw, the Control-1 path ramps screen zoom in to 1.5x around the scripted pointer, the view
    /// follows two travels, eight scrolls zoom further to 2.67x, and zoom ramps out.
    /// </summary>
    ZoomScreen,

    /// <summary>
    /// <c>zoom-lens</c>: after zoom-screen's state (Use apps, the document's zoom at 2.67x), the pointer lens turns on, follows
    /// three travels and turns off.
    /// </summary>
    ZoomLens,
}

/// <summary>
/// The stage of the Mac's live recordings (UXLiveRecordingTests.UXLiveStage): a 2,560 by 1,440-point display with a
/// 24-point menu bar, the recorded 1,440 by 900-point area at the bottom center, the toolbar docked at the bottom and seven
/// drawings placed on the owned sample window's content (the recording shows the backdrop and sample window). Points from
/// the display's top-left corner.
/// </summary>
internal static class MacLiveStage
{
    public const double Width = 2560;
    public const double Height = 1440;
    public const double MenuBar = 24;

    /// <summary>The recorded area (the Mac's areaPoints).</summary>
    public static readonly CoreRect Area = new(560, 540, 1440, 900);

    /// <summary>Where the sample window's content starts: 130 points in from the area's left and 60 down from its top.</summary>
    private static readonly CorePoint Content = new(Area.X + 130, Area.Y + 60);

    /// <summary>The scripted pointer rests at the middle of the 1,180 by 664-point sample content.</summary>
    public static CorePoint Pointer => Design(590, 332);

    /// <summary>The Mac's <c>design(x, y)</c>: a point in the sample content, from its top-left corner.</summary>
    public static CorePoint Design(double x, double y) => new(Content.X + x, Content.Y + y);

    /// <summary>The Mac's <c>canvasRect(x, y, width, height)</c>.</summary>
    public static CoreRect DesignRect(double x, double y, double width, double height) => new(Content.X + x, Content.Y + y, width, height);

    /// <summary>The made-up display in desktop pixels that puts the recorded area at <paramref name="area"/>.</summary>
    public static PixelRect Display(PixelRect area, double scale) => PixelRect.FromSize(
        area.Left - (int)Math.Round(Area.X * scale), area.Top - (int)Math.Round(Area.Y * scale),
        (int)Math.Round(Width * scale), (int)Math.Round(Height * scale));

    /// <summary>The display's work area (the Mac's visible frame): everything below the menu bar.</summary>
    public static PixelRect WorkArea(PixelRect display, double scale)
    {
        var menu = (int)Math.Round(MenuBar * scale);
        return new PixelRect(display.Left, display.Top + menu, display.Right, display.Bottom);
    }

    /// <summary>The Mac's addSampleDrawings, in the order it adds them.</summary>
    public static Element[] Drawings()
    {
        RGBA teal = new(0.05, 0.58, 0.6), blue = new(0.17, 0.45, 0.88), purple = new(0.42, 0.33, 0.86);
        var ellipse = new Element(Tool.Ellipse, DesignRect(338, 34, 128, 108));
        ellipse.Style.Color = teal;
        ellipse.Style.Width = 4;
        ellipse.Style.Roughness = 1.5;
        var arrow = new Element(Tool.Arrow, points: [Design(740, 150), Design(640, 96), Design(472, 92)]);
        arrow.Style.Color = blue;
        arrow.Style.Width = 4;
        arrow.Style.Curved = true;
        arrow.Style.HeadScale = 0.75;
        var box = new Element(Tool.Rectangle, DesignRect(560, 220, 324, 194));
        box.Style.Color = purple;
        box.Style.Width = 3;
        box.Style.CornerRadius = 12;
        var highlight = new Element(Tool.Highlighter, points: [Design(572, 108), Design(930, 108)], style: Style.Defaults(Tool.Highlighter));
        var wave = new CorePoint[16];
        for (var index = 0; index < wave.Length; index++)
        {
            wave[index] = Design(574 + index * 15, 62 + 4 * Math.Sin(index * 0.9));
        }
        var ink = new Element(Tool.Pen, points: [.. wave]);
        ink.Style.Color = blue;
        ink.Style.Width = 4;
        var note = new Element(Tool.Text, DesignRect(900, 178, 210, 40), text: "Book early!");
        note.Style.Color = blue;
        note.Style.FontSize = 26;
        note.Style.Bold = true;
        var counter = new Element(Tool.Counter, DesignRect(500, 446, 32, 32), text: "1");
        counter.Style.Color = teal;
        return [ellipse, arrow, box, highlight, ink, note, counter];
    }
}

/// <summary>The live take: the Mac's stage and steps, timed on the steady clock for the recording (RenderHarness motion-live).</summary>
internal sealed partial class MotionReferenceHost
{
    private readonly List<(string Label, long Start, long End, long Completed)> _segments = [];
    private readonly List<(string Label, long Ticks)> _events = [];
    private readonly List<(ulong Serial, long Started, double Duration)> _starts = [];
    private readonly List<(string Kind, long Ticks)> _toolbarReports = [];
    private long _takeStart;
    private long _takeEnd;
    private long _completed;
    private PixelRect _display;
    private PixelRect _work;
    private DisplayInfo? _stageDisplay;
    private double _scale;
    private RectInt32 _toolbarFrame;

    /// <summary>The Mac's UXLiveStage: its display, backdrop, sample window, docked toolbar and drawings, in Draw with nothing selected.</summary>
    private async Task SetUpLiveAsync()
    {
        PixelRect area = _request.Area;
        Shell.MonitorArea monitor = Shell.WindowChrome.MonitorAt(area.Left + area.Width / 2, area.Top + area.Height / 2);
        _scale = monitor.Scale;
        _display = MacLiveStage.Display(area, _scale);
        _work = MacLiveStage.WorkArea(_display, _scale);
        var display = new DisplayDescriptor(new Annotator.Workspace.DisplayId(DisplayName),
            new CoreRect(_display.Left, _display.Top, _display.Width, _display.Height), new CoreRect(_work.Left, _work.Top, _work.Width, _work.Height), _scale);
        var preferences = new WorkspacePreferences
        {
            VisibilityAnimationStyle = _request.Style,
            AnimateVisibility = true,
            ToolbarDockEdge = ToolbarDockEdge.Bottom,
        };
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [display],
            // The scripted pointer (Mac ReelPointerHook.location): zoom starts around it.
            Pointer = () => display.ToDesktop(_scriptedPointer),
            PreferencesStore = new MemoryPreferencesStore(preferences.Data()),
            AnimatesVisibility = true,
            ReducesMotion = () => _request.ReduceMotion,
            TextEditingAvailable = false,
            TextMeasurer = new Annotator.Rendering.Text.DirectWriteTextMeasurer(),
        });
        _model = model;
        // Mac: app.resumeDrawing(), then addSampleDrawings() with nothing selected; effects-live and sharing-live start on
        // the bare content.
        model.ResumeDrawing();
        var effects = _request.Take is MacLiveTake.Effects or MacLiveTake.EffectsMeasure or MacLiveTake.ZoomMeasure;
        var sharing = _request.Take is MacLiveTake.Sharing or MacLiveTake.SharingMeasure;
        var zoom = _request.Take is MacLiveTake.ZoomScreen or MacLiveTake.ZoomLens;
        MoveScripted(MacLiveStage.Pointer);
        if (!effects && !sharing && !zoom)
        {
            DisplaySnapshot canvas = model.Snapshot.Display(display.Id) ?? throw new InvalidOperationException("The stage display has no canvas.");
            CanvasDocument document = canvas.Document;
            foreach (Element element in MacLiveStage.Drawings())
            {
                document.Add(element);
            }
            document.Select([]);
            if (model.Submit(new EditSubmission(new DisplayEdit(canvas.Id, canvas.Version, document))) is EditResult.Rejected rejected)
            {
                throw new InvalidOperationException($"The drawings were refused: {rejected.Reason}.");
            }
        }
        if (_request.Take == MacLiveTake.Controls)
        {
            // sharing-live starts in Use apps with the Pen chosen.
            model.Choose(Tool.Pen);
            model.UseApps();
        }
        if (effects || sharing || zoom)
        {
            // The app's capture service, as AppHost creates it: the effects, zoom, the lens and the shared region read the stage
            // without Annotator's windows.
            _capture = Platform.Capture.CaptureService.Create(new Platform.Capture.CaptureServiceOptions { Registry = _registry, FrameComparer = new Rendering.Effects.CaptureFrameComparer() });
        }

        // No backdrop: the recording's own desktop shows through the canvas, as the Mac's backdrop and sample window do, and
        // stays when the workspace hides. The shared region's output window and the lens register with the app's registry.
        _factory = new DrawingCanvasFactory(Report, static (_, _) => { })
        {
            ReferenceDark = _request.Dark,
            Capture = _capture,
            Registry = sharing || zoom ? _registry : null,
            ReferenceScriptedInput = true,
            ReferencePointer = ScriptedDesktopPointer,
            ShowsHints = false,
        };
        var info = new DisplayInfo(0, DisplayName, _display, _work, (uint)Math.Round(96 * _scale), IsPrimary: false);
        _stageDisplay = info;
        OverlayHost overlays = OverlayHost.StartForReference(_registry, new WorkspaceChannel(_queue, model), Report, _factory, () => [info]);
        _overlays = overlays;
        var available = new Windows.Foundation.Size(_work.Width / _scale - 32, _work.Height / _scale - 64);
        var toolbar = new ToolbarWindow(model, _registry, static () => { }, static (_, _) => { },
            new ToolbarReferencePlacement(available, default)
            {
                DockArea = new RectInt32(_work.Left, _work.Top, _work.Width, _work.Height),
                ControlMotion = _request.Take is MacLiveTake.Controls || effects || sharing || zoom,
            });
        _toolbar = toolbar;
        // The shared region captures the toolbar's window on its own, as in the app.
        _factory.ToolbarWindow = (nint)toolbar.Hwnd;
        toolbar.FirstFrame += () => _firstFrame = true;
        _handoff = new VisibilityHandoff(model, toolbar, () => overlays.IsReady, Post);
        model.Published += OnPublished;
        model.Published += snapshot =>
        {
            if (snapshot.Visibility is null && _completed == 0)
            {
                _completed = Stopwatch.GetTimestamp();
            }
        };
        OnPublished(model.Snapshot);
        overlays.CreateOverlays();
        await Until(() => overlays.IsReady && _firstFrame, "The overlay or the toolbar did not start within 10 seconds.");
        if (_request.Take == MacLiveTake.ZoomLens)
        {
            await SetUpAfterZoomScreenAsync(model);
        }
        await Task.Delay(500);
        _toolbarFrame = toolbar.Frame;
        if (_request.Take == MacLiveTake.Sharing)
        {
            await StartSharingAsync();
        }
    }

    /// <summary>
    /// The Mac's recordShowAndHide steps: wait 0.6 s, hide (Control-Option-2 path), wait 0.6 s, show (Control-Shift-2 path),
    /// wait 0.8 s; or for the reversal, show again 45% of the hide after the motion started. A segment ends as the Mac's
    /// does: 50 ms after the request, at the first 10 ms check that finds no transition.
    /// </summary>
    private async Task PlayTakeAsync(MacLiveTake take)
    {
        WorkspaceModel model = _model!;
        if (take is MacLiveTake.EffectsMeasure or MacLiveTake.SharingMeasure or MacLiveTake.ZoomMeasure)
        {
            await (take switch
            {
                MacLiveTake.EffectsMeasure => MeasureEffectsAsync(),
                MacLiveTake.SharingMeasure => MeasureSharingAsync(),
                _ => MeasureZoomAsync(),
            });
            return;
        }
        _takeStart = Stopwatch.GetTimestamp();
        Phase(4, _takeStart);
        NotePointer(_scriptedPointer);
        if (take is MacLiveTake.ZoomScreen or MacLiveTake.ZoomLens)
        {
            // The Mac's zoom takes start with the pointer's travel.
            await (take == MacLiveTake.ZoomScreen ? PlayZoomScreenAsync() : PlayZoomLensAsync());
            _takeEnd = Stopwatch.GetTimestamp();
            Phase(0, _takeEnd);
            return;
        }
        await WaitAsync(0.6);
        if (take is MacLiveTake.Effects or MacLiveTake.Sharing)
        {
            // The take ends with its last segment, as the Mac's does.
            await (take == MacLiveTake.Effects ? PlayEffectsAsync() : PlaySharingAsync());
            _takeEnd = Stopwatch.GetTimestamp();
            Phase(0, _takeEnd);
            return;
        }
        if (take == MacLiveTake.ShowHide)
        {
            await SegmentAsync("hide", () => model.HideTools());
            await WaitAsync(0.6);
            await SegmentAsync("show", () => model.ResumeDrawing());
        }
        else if (take == MacLiveTake.Controls)
        {
            // sharing-live: draw again, then the Pen, the Arrow and Blur.
            Step("draw again", () => model.ResumeDrawing());
            await WaitAsync(0.8);
            Step("arrow", () => model.Choose(Tool.Arrow));
            await WaitAsync(0.8);
            Step("blur", () => model.Choose(Tool.Blur));
            await WaitAsync(0.8);
            Step("use apps", () => model.UseApps());
        }
        else
        {
            var reverseAfter = MotionTiming.VisibilityDuration(VisibilityAnimationStyle.Slide, appearing: false) * 0.45;
            await SegmentAsync("hide, reversed mid-way", () => model.HideTools(), async () =>
            {
                // The Mac's hideTools() returns once its motion started; it waits from there.
                await Until(() => _starts.Count > 0, "The hide did not start within 2 seconds.", 2000);
                await UntilTicks(_starts[0].Started + Seconds(reverseAfter));
                _events.Add(("show while hiding", Stopwatch.GetTimestamp()));
                model.ResumeDrawing();
            });
        }
        await WaitAsync(0.8);
        _takeEnd = Stopwatch.GetTimestamp();
        Phase(0, _takeEnd);
    }

    private async Task SegmentAsync(string label, Action request, Func<Task>? then = null)
    {
        WorkspaceModel model = _model!;
        var start = Stopwatch.GetTimestamp();
        _completed = 0;
        request();
        if (then is not null)
        {
            await then();
            _completed = 0;
        }
        await Task.Delay(50);
        var clock = Stopwatch.StartNew();
        while (model.Snapshot.Visibility is not null)
        {
            if (clock.ElapsedMilliseconds > 5000)
            {
                throw new TimeoutException($"The '{label}' segment did not end within 5 seconds.");
            }
            await Task.Delay(10);
        }
        var end = Stopwatch.GetTimestamp();
        _segments.Add((label, start, end, _completed == 0 ? start : _completed));
    }

    private void NoteStart(OverlayEvent.VisibilityStarted started) => _starts.Add((started.Serial, started.Started, started.Duration));

    /// <summary>A step without show and hide motion, at the time it was requested.</summary>
    private void Step(string label, Action request)
    {
        _events.Add((label, Stopwatch.GetTimestamp()));
        request();
    }

    /// <summary>When the toolbar's snapshot took over from the real toolbar, or the real toolbar animated instead.</summary>
    private void NoteToolbar(OverlayEvent report) =>
        _toolbarReports.Add((report is OverlayEvent.ToolbarCovered ? "covered" : "fallback", Stopwatch.GetTimestamp()));

    private static long Seconds(double seconds) => (long)Math.Round(seconds * Stopwatch.Frequency);

    private Task WaitAsync(double seconds) => UntilTicks(Stopwatch.GetTimestamp() + Seconds(seconds));

    /// <summary>
    /// Waits on the app thread until the steady clock reaches <paramref name="ticks"/>: delays while far, then a short spin
    /// (Windows' timer steps are about 15.6 ms).
    /// </summary>
    private static async Task UntilTicks(long ticks)
    {
        var margin = Seconds(0.02);
        while (Stopwatch.GetTimestamp() < ticks - margin)
        {
            await Task.Delay(1);
        }
        while (Stopwatch.GetTimestamp() < ticks)
        {
            Thread.SpinWait(64);
        }
    }

    private void WriteTake(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("take");
        writer.WriteString("kind", _request.Take switch
        {
            MacLiveTake.Reversal => "reversal",
            MacLiveTake.Controls => "controls",
            MacLiveTake.Effects => "effects",
            MacLiveTake.EffectsMeasure => "effects-measure",
            MacLiveTake.Sharing => "sharing",
            MacLiveTake.SharingMeasure => "sharing-measure",
            MacLiveTake.ZoomMeasure => "zoom-measure",
            MacLiveTake.ZoomScreen => "zoom-screen",
            MacLiveTake.ZoomLens => "zoom-lens",
            _ => "show-hide",
        });
        writer.WriteNumber("startTicks", _takeStart);
        writer.WriteNumber("endTicks", _takeEnd);
        writer.WriteNumber("scale", _scale);
        WriteRect(writer, "display", _display.Left, _display.Top, _display.Width, _display.Height);
        WriteRect(writer, "workArea", _work.Left, _work.Top, _work.Width, _work.Height);
        WriteRect(writer, "area", _request.Area.Left, _request.Area.Top, _request.Area.Width, _request.Area.Height);
        WriteRect(writer, "toolbar", _toolbarFrame.X, _toolbarFrame.Y, _toolbarFrame.Width, _toolbarFrame.Height);
        writer.WriteStartArray("segments");
        foreach (var (label, start, end, completed) in _segments)
        {
            writer.WriteStartObject();
            writer.WriteString("label", label);
            writer.WriteNumber("startTicks", start);
            writer.WriteNumber("endTicks", end);
            writer.WriteNumber("completedTicks", completed);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("events");
        foreach (var (label, ticks) in _events)
        {
            writer.WriteStartObject();
            writer.WriteString("label", label);
            writer.WriteNumber("ticks", ticks);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("motionStarts");
        foreach (var (serial, started, duration) in _starts)
        {
            writer.WriteStartObject();
            writer.WriteNumber("serial", serial);
            writer.WriteNumber("ticks", started);
            writer.WriteNumber("plannedSeconds", duration);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("toolbarReports");
        foreach (var (kind, ticks) in _toolbarReports)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteNumber("ticks", ticks);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteEffects(writer);
        WriteSharing(writer);
        WritePointerPath(writer);
        writer.WriteEndObject();
    }

    private static void WriteRect(Utf8JsonWriter writer, string name, int x, int y, int width, int height)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(x);
        writer.WriteNumberValue(y);
        writer.WriteNumberValue(width);
        writer.WriteNumberValue(height);
        writer.WriteEndArray();
    }
}
