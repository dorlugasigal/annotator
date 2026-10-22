using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Annotator.App.Overlay;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using Annotator.Workspace;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using CoreRect = Annotator.Core.Rect;
using CoreSize = Annotator.Core.Geometry.Size;
using PointerButtons = Annotator.Platform.Input.PointerButtons;

namespace Annotator.App.Reference;

/// <summary>
/// The canvas states (UXReferenceCaptureTests.test07CanvasChrome): a real overlay with its drawing canvas, presenter and
/// Use apps button for one made-up display beyond the desktop's right edge, which never takes focus. The sample scene
/// is submitted like a canvas edit, gestures go through the canvas's pointer input, and the canvas renders what it shows
/// with the presenter's renderer at 2x over the Mac capture's fictional backdrop.
/// </summary>
internal sealed partial class ReferenceHost
{
    private const string CanvasDisplayName = "reference-canvas";
    private const double CanvasScale = 1.25;

    /// <summary>The Mac capture's canvas: its 900 by 600 test panel less the 32-point title bar.</summary>
    private static readonly CoreSize s_canvasSize = new(900, 568);

    private OverlayHost? _overlays;

    private async Task ShowCanvasAsync(ReferenceScenario scenario, CanvasScript script, ReferenceProfile profile)
    {
        PointInt32 origin = OffscreenOrigin();
        int width = (int)Math.Round(s_canvasSize.Width * CanvasScale), height = (int)Math.Round(s_canvasSize.Height * CanvasScale);
        PixelRect pixels = PixelRect.FromSize(origin.X, origin.Y, width, height);
        var bounds = new CoreRect(pixels.Left, pixels.Top, width, height);
        var display = new DisplayDescriptor(new Annotator.Workspace.DisplayId(CanvasDisplayName), bounds, bounds, CanvasScale);
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [display],
            PreferencesStore = new MemoryPreferencesStore(profile.Preferences.Data()),
        });
        model.ShowToolbar();
        model.Choose(script.Tool);
        CanvasSample sample = SubmitSampleScene(model, script);

        if (sample.Board != Board.Desktop)
        {
            model.SetBoard(sample.Board);
        }
        if (model.Snapshot.Surfaces != SurfaceMode.Draw || model.Snapshot.ErrorMessage is { } message && message.Length > 0)
        {
            throw new InvalidOperationException($"The reference canvas is not drawing: {model.Snapshot.Surfaces}, {model.Snapshot.ErrorMessage}.");
        }

        var failures = new List<string>();
        void Report(OverlayEvent report) => _queue.TryEnqueue(() =>
        {
            switch (report)
            {
                case OverlayEvent.Failure failure:
                    failures.Add($"{failure.Operation}: {failure.Error.Message}");
                    break;
                case OverlayEvent.Devices { Error: { } error }:
                    failures.Add($"Graphics devices: {error.Message}");
                    break;
            }
        });
        var canvases = new DrawingCanvasFactory(Report, static (_, _) => { }) { ReferenceDark = _request.Theme == ReferenceTheme.Dark, ShowsHints = false };
        OverlayHost overlays = OverlayHost.StartForReference(_registry, new WorkspaceChannel(_queue, model), Report, canvases,
            () => [new DisplayInfo(0, CanvasDisplayName, pixels, pixels, (uint)Math.Round(96 * CanvasScale), IsPrimary: false)]);
        _overlays = overlays;
        model.Published += snapshot => overlays.Post(new OverlayFrame(snapshot, InputBlocked: false));
        overlays.Post(new OverlayFrame(model.Snapshot, InputBlocked: false));
        overlays.CreateOverlays();
        await Until(() => overlays.RunAsync(shown => shown.Count == 1), "The reference canvas did not start within 10 seconds.");

        if (script.Gesture.Length > 0)
        {
            await overlays.RunAsync(shown =>
            {
                IOverlayCanvas canvas = shown.Values.Single();
                uint time = 1000;
                for (int index = 0; index < script.Gesture.Length; index++)
                {
                    Pointer(canvas, index == 0 ? PointerInputKind.Down : PointerInputKind.Move, script.Gesture[index], time);
                    time += 120;
                }
                if (script.Typing is { } typing)
                {
                    Pointer(canvas, PointerInputKind.Up, script.Gesture[^1], time);
                    foreach (char character in typing)
                    {
                        canvas.OnKey(new KeyInput(KeyInputKind.Character, 0, 0, false, false, false, ModifierKeys.None, character));
                    }
                }
                return true;
            });
        }
        // Edits reach the workspace on this thread and come back as frames; let them settle before the picture.
        for (int pass = 0; pass < 4; pass++)
        {
            await Task.Delay(60);
            await overlays.RunAsync(static _ => true);
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" | ", failures));
        }
        (CanvasReference picture, nint window) = await overlays.RunAsync(shown =>
        {
            var canvas = (DrawingCanvas)shown.Values.Single();
            return (canvas.Reference(Backdrop(), 2), shown.Keys.Single().Handle);
        });
        RasterImage image = picture.Image;
        File.WriteAllBytes(ImagePath, image.Pixels);
        if (!PInvoke.GetWindowRect(new HWND(window), out RECT rect))
        {
            throw new InvalidOperationException("The overlay's rectangle is unavailable.");
        }
        Finish(writer =>
        {
            Status(writer, "shown", null);
            writer.WriteNumber("hwnd", (long)window);
            writer.WriteNumber("scale", CanvasScale);
            writer.WriteBoolean("canvas", true);
            Pair(writer, "surface", picture.Size.Width, picture.Size.Height);
            writer.WriteStartArray("window");
            writer.WriteNumberValue(rect.left);
            writer.WriteNumberValue(rect.top);
            writer.WriteNumberValue(rect.right - rect.left);
            writer.WriteNumberValue(rect.bottom - rect.top);
            writer.WriteEndArray();
            writer.WriteString("image", Path.GetFileName(ImagePath));
            Pair(writer, "imageSize", image.Width, image.Height);
            WriteElements(writer, picture);

        }, wait: true);
    }

    /// <summary>
    /// UXReferenceCaptureTests.addSampleScene, submitted the way a canvas submits an edit, then the state's changes: an
    /// "API" box, a blue oval, an arrow, "Checkout flow", a pen wave and a locked diamond.
    /// </summary>
    private static CanvasSample SubmitSampleScene(WorkspaceModel model, CanvasScript script)
    {
        WorkspaceSnapshot snapshot = model.Snapshot;
        DisplaySnapshot display = snapshot.Display(snapshot.ActiveDisplay ?? throw new InvalidOperationException("No display is drawing."))!;
        CanvasDocument document = display.Document;
        var box = new Element(Tool.Rectangle, new CoreRect(120, 110, 220, 130), style: Style.Defaults(Tool.Rectangle))
        {
            Label = new ShapeLabel("API", followsStroke: true),
        };
        var oval = new Element(Tool.Ellipse, new CoreRect(420, 120, 190, 110), style: Style.Defaults(Tool.Ellipse));
        oval.Style.Color = new RGBA(0.20, 0.55, 0.95);
        var arrow = new Element(Tool.Arrow, points: [new Annotator.Core.Point(345, 175), new Annotator.Core.Point(415, 175)], style: Style.Defaults(Tool.Arrow));
        var text = new Element(Tool.Text, new CoreRect(120, 300, 220, 40), text: "Checkout flow", style: Style.Defaults(Tool.Text));
        var wave = new List<Annotator.Core.Point>();
        for (int step = 0; step <= 20; step++)
        {
            // Swift's stride(from: 0, through: 1, by: 0.05) with its fused multiply-add.
            double t = Math.FusedMultiplyAdd(step, 0.05, 0);
            wave.Add(new Annotator.Core.Point(130 + t * 240, 400 + Math.Sin(t * Math.PI * 2) * 22));
        }
        var pen = new Element(Tool.Pen, points: [.. wave], style: Style.Defaults(Tool.Pen));
        var locked = new Element(Tool.Diamond, new CoreRect(650, 300, 120, 110), style: Style.Defaults(Tool.Diamond)) { Locked = true };
        foreach (Element element in new[] { box, oval, arrow, text, pen, locked })
        {
            document.Add(element);
        }
        document.Select([]);
        var sample = new CanvasSample { Box = box, Oval = oval, Arrow = arrow, Locked = locked };
        script.Prepare(ref document, sample);
        if (sample.AddsNewNote)
        {
            // The text the Mac's text-editing state typed at (480, 330), sized as a canvas sizes committed text.
            Style style = Style.Defaults(Tool.Text);
            using var measurer = new DirectWriteTextMeasurer();
            CoreSize size = TextLayout.ContentSize("New note", TextFont.Resolve(style), measurer);
            document.Add(new Element(Tool.Text, new CoreRect(480, 330, size.Width, size.Height), text: "New note", style: style));
            // Adding selects nothing; keep the state's selection.
        }
        if (model.Submit(new EditSubmission(new DisplayEdit(display.Id, display.Version, document))) is EditResult.Rejected rejected)
        {
            throw new InvalidOperationException($"The reference objects were refused: {rejected.Reason}.");
        }
        return sample;
    }

    /// <summary>One mouse sample, in points from the canvas's top-left corner, through the canvas's pointer input.</summary>
    private static void Pointer(IOverlayCanvas canvas, PointerInputKind kind, Annotator.Core.Point point, uint time)
    {
        Span<PointerSample> samples = [new PointerSample(new Vector2((float)(point.X * CanvasScale), (float)(point.Y * CanvasScale)), 1, time)];
        canvas.OnPointer(new PointerInput
        {
            Kind = kind,
            Device = PointerDeviceKind.Mouse,
            PointerId = 1,
            Buttons = kind == PointerInputKind.Up ? PointerButtons.None : PointerButtons.Left,
            ChangedButton = kind is PointerInputKind.Down or PointerInputKind.Up ? PointerButtons.Left : PointerButtons.None,
            Samples = samples,
        });
    }

    /// <summary>
    /// UXReferenceCaptureTests.backdrop: a light desk, a white card with gray text bars and four colored blocks, drawn at
    /// 900 by 560 points and stretched to the 900 by 568 canvas, as the Mac draws its reference image over the canvas.
    /// </summary>
    private static SceneSnapshot Backdrop()
    {
        var builder = new DisplayListBuilder();
        builder.Add(DisplayItem.PushTransform(AffineTransform.Scale(1, s_canvasSize.Height / 560)));
        builder.Add(DisplayItem.FillRect(new CoreRect(0, 0, 900, 560), Paint.Solid(new RGBA(0.91, 0.93, 0.96))));
        var card = new CoreRect(40, 36, 820, 488);
        builder.Add(DisplayItem.FillPath(new PathBuilder(10, 21).AddRoundedRect(card, 12, 12).ToPathData(), FillRule.NonZero, Paint.White));
        Paint bars = Paint.Solid(new RGBA(0.80, 0.83, 0.88));
        double[] lengths = [0.42, 0.66, 0.55, 0.61, 0.38];
        for (int index = 0; index < lengths.Length; index++)
        {
            builder.Add(DisplayItem.FillRect(new CoreRect(card.X + 32, card.Y + 40 + index * 30, (card.Width * 0.5 - 64) * lengths[index] * 1.6, 10), bars));
        }
        RGBA[] colors = [new(0.98, 0.45, 0.35), new(0.98, 0.78, 0.25), new(0.32, 0.72, 0.55), new(0.32, 0.52, 0.95)];
        for (int index = 0; index < colors.Length; index++)
        {
            builder.Add(DisplayItem.FillRect(new CoreRect(card.MaxX - 300 + index * 64, card.Y + 40, 52, 150), Paint.Solid(colors[index])));
        }
        builder.Add(DisplayItem.PopTransform());
        return SceneSnapshot.Create([builder.ToList()]);
    }

    /// <summary>The objects the canvas shows, in the reference JSON's shape; the object being drawn is marked.</summary>
    private static void WriteElements(Utf8JsonWriter writer, CanvasReference picture)
    {
        writer.WriteStartArray("elements");
        foreach (ref readonly Element element in picture.Elements)
        {
            writer.WriteStartObject();
            writer.WriteString("tool", element.Tool.RawValue);
            writer.WriteStartArray("frame");
            writer.WriteNumberValue(element.Frame.X);
            writer.WriteNumberValue(element.Frame.Y);
            writer.WriteNumberValue(element.Frame.Width);
            writer.WriteNumberValue(element.Frame.Height);
            writer.WriteEndArray();
            writer.WriteNumber("rotation", element.Rotation);
            writer.WriteBoolean("locked", element.Locked);
            writer.WriteBoolean("selected", picture.Selection.Contains(element.Id));
            if (element.Id == picture.Draft)
            {
                writer.WriteBoolean("draft", true);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static async Task Until(Func<Task<bool>> condition, string failure, int milliseconds = 10_000)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds)
            {
                throw new TimeoutException(failure);
            }
            await Task.Delay(50);
        }
    }

    /// <summary>Stops the reference overlay (at most 5 seconds), then quits.</summary>
    private static async Task StopThenExitAsync(OverlayHost overlays)
    {
        await Task.WhenAny(overlays.StopAsync(), Task.Delay(5000));
        Microsoft.UI.Xaml.Application.Current.Exit();
    }
}
