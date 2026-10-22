using System.Globalization;
using System.Text.Json;
using Annotator.Core;
using Annotator.Platform.Displays;
using Annotator.Workspace;

namespace Annotator.App.Reference;

/// <summary>
/// Development builds only: <c>--live-check --area x,y,width,height --out folder [--timeout seconds]</c> runs the app's
/// real toolbar, canvas, color popover and workspace on one made-up display that covers <c>area</c> (physical pixels of
/// the desktop), beside a running Annotator: no single-instance check, tray icon, global shortcut or saved setting.
/// tools/windows/live-check.ps1 drives it with real input into its own windows. The app writes <c>ready.json</c> once its
/// toolbar and canvas exist, and <c>result.json</c> and <c>canvas.png</c> when it quits: after the script sets its done
/// event, or after the timeout.
/// </summary>
internal sealed record LiveCheckRequest(PixelRect Area, string Output, int TimeoutSeconds)
{
    /// <summary>
    /// <c>--zoom-hooks</c>: the zoom hooks run during the check, so live zoom follows the pointer that the script moves (the
    /// zoom measurements). They take Alt+scroll over the made-up display and Escape while zoom shows, as in the app.
    /// </summary>
    public bool ZoomHooks { get; init; }

    public const string Switch = "--live-check";
    public const string DisplayName = "live-check";

    public string ReadyPath => Path.Combine(Output, "ready.json");

    public string ResultPath => Path.Combine(Output, "result.json");

    public string PicturePath => Path.Combine(Output, "canvas.png");

    /// <summary>Throws <see cref="FormatException"/> for a malformed request.</summary>
    public static LiveCheckRequest Parse(IReadOnlyList<string> args)
    {
        string area = Value(args, "--area") ?? throw new FormatException("--area x,y,width,height is required.");
        int[] parts = [.. area.Split(',').Select(part => int.Parse(part, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))];
        if (parts.Length != 4 || parts[2] < 200 || parts[3] < 200)
        {
            throw new FormatException("--area needs x,y,width,height in pixels, at least 200 by 200.");
        }
        string output = Value(args, "--out") ?? throw new FormatException("--out <folder> is required.");
        int timeout = Value(args, "--timeout") is { } seconds ? int.Parse(seconds, NumberStyles.None, CultureInfo.InvariantCulture) : 30;
        return new LiveCheckRequest(PixelRect.FromSize(parts[0], parts[1], parts[2], parts[3]), Path.GetFullPath(output),
                                    Math.Clamp(timeout, 5, 300))
        {
            ZoomHooks = args.Contains("--zoom-hooks"),
        };
    }

    /// <summary>The made-up display: the area, at the scale of the monitor it is on.</summary>
    public DisplayInfo Display(double scale) =>
        new(0, DisplayName, Area, Area, (uint)Math.Round(96 * scale), IsPrimary: false);

    /// <summary>The event the script sets when it is done.</summary>
    public static string DoneEventName(int processId) => $@"Local\Annotator.LiveCheck.{processId}";

    /// <summary>
    /// The live check's file dialogs remember their folder under this ID, so the person's Annotator dialogs never open in the
    /// check's output folder.
    /// </summary>
    public static readonly Guid DialogState = new("6F1C0B6E-3A55-4B8C-9E57-2D41C8B0A7E1");

    /// <summary>Writes a file in one step, so the script never reads half of it.</summary>
    public static void WriteJson(string path, Action<Utf8JsonWriter> write)
    {
        string temporary = path + ".tmp";
        using (FileStream stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The workspace as the script checks it: mode, tool and every object of the made-up display.</summary>
    public static void WriteWorkspace(Utf8JsonWriter writer, WorkspaceSnapshot snapshot)
    {
        writer.WriteString("mode", snapshot.Mode.ToString());
        writer.WriteString("tool", snapshot.Tool.RawValue);
        writer.WriteString("status", snapshot.Status);
        writer.WriteString("error", snapshot.ErrorMessage);
        writer.WriteStartArray("elements");
        if (snapshot.Display(new Annotator.Workspace.DisplayId(DisplayName))?.Document is { } document)
        {
            foreach (ref readonly Element element in document.Elements)
            {
                writer.WriteStartObject();
                writer.WriteString("tool", element.Tool.RawValue);
                writer.WriteStartArray("frame");
                writer.WriteNumberValue(element.Frame.X);
                writer.WriteNumberValue(element.Frame.Y);
                writer.WriteNumberValue(element.Frame.Width);
                writer.WriteNumberValue(element.Frame.Height);
                writer.WriteEndArray();
                writer.WriteStartArray("points");
                foreach (Annotator.Core.Point point in element.Points)
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(point.X);
                    writer.WriteNumberValue(point.Y);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteBoolean("selected", document.Selection.Contains(element.Id));
                if (element.Tool == Tool.Text)
                {
                    writer.WriteString("text", element.Text);
                }
                if (element.Label is { } label)
                {
                    writer.WriteString("label", label.Text);
                    writer.WriteBoolean("labelFollowsStroke", label.FollowsStroke == true);
                }
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
    }

    /// <summary>What Spotlight and Cursor halo did on the overlays: pointer activity, shows, clicks and the last pointer.</summary>
    public static void WritePointerEffects(Utf8JsonWriter writer, Overlay.PointerEffectsCounters? counters)
    {
        if (counters is null)
        {
            return;
        }
        writer.WriteStartObject("pointerEffects");
        writer.WriteNumber("activity", counters.Activity);
        writer.WriteNumber("applies", counters.Applies);
        writer.WriteNumber("clicks", counters.Clicks);
        writer.WriteNumber("watcherStarts", counters.WatcherStarts);
        writer.WriteNumber("watcherFailures", counters.WatcherFailures);
        writer.WriteNumber("mostLayersShown", counters.MostLayersShown);
        writer.WriteStartArray("lastPointer");
        writer.WriteNumberValue(counters.LastPointer.X);
        writer.WriteNumberValue(counters.LastPointer.Y);
        writer.WriteEndArray();
        writer.WriteBoolean("spotlightShown", counters.SpotlightShown);
        writer.WriteBoolean("haloShown", counters.HaloShown);
        writer.WriteEndObject();
    }

    /// <summary>
    /// What the shared region's show and hide motion did (Mac SharedWorkspaceTransition): motions copied, copies made,
    /// particle frames drawn, covers shown while a motion played (besides the one each starts with) and hand-offs to the
    /// live scene.
    /// </summary>
    public static void WriteSharingMotion(Utf8JsonWriter writer, (int Transitions, int Copies, int ParticleFrames, int Covers, int HandOffs)? motion)
    {
        if (motion is not { } stats)
        {
            return;
        }
        writer.WriteStartObject("sharingMotion");
        writer.WriteNumber("transitions", stats.Transitions);
        writer.WriteNumber("copies", stats.Copies);
        writer.WriteNumber("particleFrames", stats.ParticleFrames);
        writer.WriteNumber("coversDuringMotion", stats.Covers);
        writer.WriteNumber("handOffs", stats.HandOffs);
        writer.WriteEndObject();
    }

    /// <summary>
    /// What the capture service did over the check: capture sessions opened and reused (a paused capture serves the next
    /// lease of its display), frames received, compared and unchanged, and whether a source takes frames or lingers now.
    /// </summary>
    public static void WriteCapture(Utf8JsonWriter writer, Annotator.Platform.Capture.CaptureStatistics statistics, bool capturing, bool holds)
    {
        writer.WriteStartObject("capture");
        writer.WriteNumber("sessionsOpened", statistics.SessionsStarted);
        writer.WriteNumber("sessionsReused", statistics.SessionsReused);
        writer.WriteNumber("framesReceived", statistics.Received);
        writer.WriteNumber("framesCompared", statistics.Compared);
        writer.WriteNumber("framesUnchanged", statistics.Unchanged);
        writer.WriteBoolean("capturing", capturing);
        writer.WriteBoolean("paused", holds && !capturing);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The compositor boost while drawing (WIN-PERF-01): requests made, holds and whether a boost is still on at the end,
    /// and the last refusal.
    /// </summary>
    public static void WriteBoost(Utf8JsonWriter writer, (int Requests, int Holds, bool Boosted, string? Failure)? boost)
    {
        if (boost is not { } stats)
        {
            return;
        }
        writer.WriteStartObject("boost");
        writer.WriteNumber("requests", stats.Requests);
        writer.WriteNumber("holds", stats.Holds);
        writer.WriteBoolean("boosted", stats.Boosted);
        writer.WriteString("failure", stats.Failure);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Live zoom's measurements (spike S-3): pointer to zoomed frame, the captured views' frames and the hooks' timings.
    /// </summary>
    public static void WriteZoom(Utf8JsonWriter writer, Overlay.ZoomStatistics? zoom, Annotator.Platform.Input.LowLevelHookStatistics? hooks)
    {
        writer.WriteStartObject("zoom");
        if (zoom is not null)
        {
            writer.WriteNumber("pointerMoves", zoom.Moves);
            writer.WriteNumber("pointerToFrameP50Ms", zoom.Percentile(0.5));
            writer.WriteNumber("pointerToFrameP99Ms", zoom.Percentile(0.99));
            writer.WriteNumber("pointerToFrameMaxMs", zoom.Percentile(1));
            writer.WriteNumber("screenFramesDrawn", zoom.ScreenFrames);
            writer.WriteNumber("screenFramesSkipped", zoom.ScreenSkipped);
            writer.WriteNumber("screenLastFrameMs", zoom.ScreenLastFrame.TotalMilliseconds);
            writer.WriteNumber("lensFramesDrawn", zoom.LensFrames);
            writer.WriteNumber("drawingViewRedraws", zoom.ViewRedraws);
            writer.WriteNumber("rampFrames", zoom.RampFrames);
        }
        if (hooks is not null)
        {
            writer.WriteNumber("hookCalls", hooks.Calls);
            writer.WriteNumber("hookConsumed", hooks.Consumed);
            writer.WriteNumber("hookP50Us", hooks.Percentile(0.5).TotalMicroseconds);
            writer.WriteNumber("hookP99Us", hooks.Percentile(0.99).TotalMicroseconds);
            writer.WriteNumber("hookMaxUs", hooks.Percentile(1).TotalMicroseconds);
            writer.WriteNumber("hookRepairs", hooks.Reinstalls);
        }
        writer.WriteEndObject();
    }

    private static string? Value(IReadOnlyList<string> args, string option)
    {
        for (int index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal) && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}
