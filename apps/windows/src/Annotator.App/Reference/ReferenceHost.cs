using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Annotator.App.Library;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Library;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using CoreRect = Annotator.Core.Rect;

namespace Annotator.App.Reference;

/// <summary>
/// Development builds only: shows one toolbar or tray flyout state of the macOS UX reference (WIN-PAR-06) for
/// apps/windows/tools/Annotator.UXReference, which dumps its UI Automation tree. The workspace runs on one
/// made-up display with an in-memory profile; there is no tray icon, global shortcut, overlay or saved setting.
/// The window opens beyond the right edge of the desktop without activation, so it never covers the person's
/// apps or takes focus. The host renders the surface to <c>surface.bgra</c>, writes <c>ready.json</c>, waits for
/// the tool's done event (bounded by the request's timeout) and quits.
/// </summary>
internal sealed partial class ReferenceHost
{
    /// <summary>The made-up display: 2560 by 1440 pixels at 125%, with a 48-pixel taskbar.</summary>
    private static readonly DisplayDescriptor s_display =
        new(new Annotator.Workspace.DisplayId("reference"), new CoreRect(0, 0, 2560, 1440), new CoreRect(0, 0, 2560, 1392), 1.25);

    private readonly ReferenceRequest _request;
    private readonly DispatcherQueue _queue;
    private readonly WindowRegistry _registry = new();
    private ToolbarWindow? _toolbar;
    private TrayFlyout? _flyout;
    private PopoverWindow? _popover;
    private ColorPopover? _picker;
    private FrameworkElement? _surface;
    private HWND _window;
    private DispatcherQueueTimer? _deadline;
    private int _frames;
    private bool _finished;
    private TimeSpan _settle;
    private DateTime _readyAt;

    private ReferenceHost(ReferenceRequest request)
    {
        _request = request;
        _queue = DispatcherQueue.GetForCurrentThread();
    }

    public static void Start(ReferenceRequest request) => new ReferenceHost(request).Run();

    private string ReadyPath => Path.Combine(_request.Output, "ready.json");

    private string ImagePath => Path.Combine(_request.Output, "surface.bgra");

    private string LayoutPath => Path.Combine(_request.Output, "layout.txt");

    private void Run()
    {
        try
        {
            Directory.CreateDirectory(_request.Output);
            File.Delete(ReadyPath);
            File.Delete(ImagePath);
            ReferenceScenario scenario = ReferenceStates.Find(_request.Surface, _request.State)
                ?? throw new ArgumentException($"There is no Windows reference state named {_request.Name}.");
            if (scenario.Unavailable is { } reason)
            {
                Finish(writer => Status(writer, "unavailable", reason), wait: false);
                return;
            }
            var profile = new ReferenceProfile();
            scenario.Profile?.Invoke(profile);
            SimulateAccessibility();
            if (scenario.Surface == ReferenceSurface.Canvas)
            {
                _ = CanvasAsync(scenario, profile);
                return;
            }
            if (scenario.Surface == ReferenceSurface.RegionSharing)
            {
                ShowRegionSharing(scenario);
                return;
            }
            var clipboard = new MemoryClipboard();
            if (scenario.ClipboardHasImage)
            {
                clipboard.SetImage(ReferenceImage());
            }
            var model = new WorkspaceModel(new WorkspaceOptions
            {
                Displays = () => [s_display],
                PreferencesStore = new MemoryPreferencesStore(profile.Preferences.Data()),
                Clipboard = clipboard,
                ScreenAccess = scenario.ScreenAccess is { } access ? new FixedScreenAccess(access) : null,
            });
            scenario.Setup?.Invoke(model);
            if (scenario.Surface != ReferenceSurface.ErrorWindow && model.Snapshot.ErrorMessage is { } message)
            {
                throw new InvalidOperationException($"The workspace reported: {message}");
            }
            switch (scenario.Surface)
            {
                case ReferenceSurface.Toolbar:
                    // Docked placement shows the toolbar where the app would, on the display under the pointer, for an
                    // on-screen check of placement and fitting; the default keeps it off the desktop.
                    // Using a toolbar control closes the arrowhead choices and the color popover, as the app's transient
                    // windows close. The docked check's Stroke and Fill buttons open the color popover on screen.
                    _toolbar = new ToolbarWindow(model, _registry, CloseTransientWindows,
                        _request.Docked ? (request, anchor) => OpenPicker(model, request, anchor) : static (_, _) => { },
                        _request.Docked ? null : new ToolbarReferencePlacement(scenario.Available, OffscreenOrigin()))
                    {
                        ArrowHeads = (start, anchor, closed) => ShowArrowHeads(model, start, anchor, closed),
                        // The docked check opens the real Tools window with an in-memory My shapes; off screen the button
                        // only shows as it does in the app.
                        ToolLibrary = _request.Docked ? () => OpenTools(model) : static () => { },
                        // The gear shows as it does in the app; the reference opens no Settings window.
                        Settings = static () => { },
                    };
                    _toolbar.Apply(model.Snapshot);
                    // The toolbar follows its own buttons, as the app does, so UI Automation can check what they change; an
                    // open color popover follows them too.
                    model.Published += snapshot =>
                    {
                        _toolbar?.Apply(snapshot);
                        _picker?.Apply(snapshot);
                    };
                    Watch((FrameworkElement)_toolbar.Content, _toolbar.Hwnd);
                    break;
                case ReferenceSurface.Flyout:
                    // The Mac capture's updates-unavailable state is a build without updates; its other menu states could check.
                    // Its update-available state found 0.1.0-beta.13 while the person was busy.
                    _flyout = new TrayFlyout(_registry, model.Snapshot, trayProblem: null, openedWithKeyboard: false,
                        static previous => previous, static _ => { }, scenario.DeveloperPreview, canCheckUpdates: !scenario.UpdatesUnavailable,
                        scenario.UpdateAvailable ? Annotator.Workspace.Updates.UpdateText.PortableAvailable(ReferenceStates.WhatsChangedNotes.Version)
                            : Annotator.Workspace.Settings.SettingsCopy.UpdatesUnavailable,
                        updateAvailable: scenario.UpdateAvailable);
                    _flyout.ShowForReference(OffscreenOrigin());
                    _flyout.ShowOpeningFocusForReference();
                    Watch((FrameworkElement)_flyout.Content, _flyout.Hwnd);
                    break;
                case ReferenceSurface.TrayIcon:
                    WriteTrayIcon(scenario.SharingOn);
                    break;
                case ReferenceSurface.Properties:
                    var properties = new PropertiesReferenceWindow(_registry, model, scenario.Width);
                    _popover = properties;
                    properties.ShowForReference(OffscreenOrigin());
                    Watch((FrameworkElement)properties.Content, properties.Hwnd);
                    break;
                case ReferenceSurface.ArrowHeads:
                    var arrowHeads = new ArrowHeadPalette(_registry, model, scenario.Start);
                    _popover = arrowHeads;
                    arrowHeads.ShowForReference(OffscreenOrigin());
                    Watch((FrameworkElement)arrowHeads.Content, arrowHeads.Hwnd);
                    break;
                case ReferenceSurface.Picker:
                    _picker = new ColorPopover(_registry, model, scenario.Picker ?? ColorPickerRequest.Stroke, ReferenceScreen());
                    _picker.ShowForReference(OffscreenOrigin());
                    Watch((FrameworkElement)_picker.Content, _picker.Hwnd);
                    break;
                case ReferenceSurface.ToolsWindow:
                    _ = ToolsAsync(model, scenario);
                    break;
                case ReferenceSurface.Settings or ReferenceSurface.LaunchAtSignIn or ReferenceSurface.Feedback or ReferenceSurface.ReleaseNotes:
                    var settings = new SettingsReferenceWindow(_registry, model, scenario, ReferenceUsage());
                    _popover = settings;
                    settings.ShowForReference(OffscreenOrigin());
                    Watch((FrameworkElement)settings.Content, settings.Hwnd);
                    break;
                case ReferenceSurface.Keycaps:
                    ShowKeycaps(model);
                    break;
                case ReferenceSurface.Tutorial or ReferenceSurface.CanvasHint or ReferenceSurface.CoffeeReminder or ReferenceSurface.WhatsChanged:
                    ShowFirstUse(model, scenario);
                    break;
                case ReferenceSurface.ZoomControls:
                    var zoom = new ZoomControlsReferenceWindow(_registry, model, scenario.Compact);
                    _popover = zoom;
                    zoom.ShowForReference(OffscreenOrigin());
                    Watch((FrameworkElement)zoom.Content, zoom.Hwnd);
                    break;
                case ReferenceSurface.ErrorWindow:
                    var errors = new ErrorReferenceWindow(_registry, model.Snapshot.ErrorMessage
                        ?? throw new InvalidOperationException("The error window state has no error to show."));
                    _popover = errors;
                    errors.ShowForReference(OffscreenOrigin());
                    Watch((FrameworkElement)errors.Content, errors.Hwnd);
                    break;
                case ReferenceSurface.ScreenAccess:
                    var explanation = new ScreenAccessReferenceWindow(_registry, scenario.Asking);
                    _popover = explanation;
                    explanation.ShowForReference(OffscreenOrigin());
                    Watch((FrameworkElement)explanation.Content, explanation.Hwnd);
                    break;
                case ReferenceSurface.BreakTimer:
                    ShowBreak(model, scenario);
                    break;
            }
        }
        catch (Exception error)
        {
            Fail(error.ToString());
        }
    }

    /// <summary>
    /// The Tools window's content with an in-memory My shapes library that has loaded, as the Mac capture shows "No saved
    /// shapes yet." It never reads or changes the person's shapes.
    /// </summary>
    private async Task ToolsAsync(WorkspaceModel model, ReferenceScenario scenario)
    {
        try
        {
            ShapeLibrary library = ShapeLibrary.InMemory();
            await library.ReloadAsync();
            var tools = new ToolsReferenceWindow(_registry, model, library, scenario.Section, scenario.Width, scenario.MaximumHeight);
            _popover = tools;
            tools.ShowForReference(OffscreenOrigin());
            Watch((FrameworkElement)tools.Content, tools.Hwnd);
        }
        catch (Exception error)
        {
            Fail(error.ToString());
        }
    }

    /// <summary>
    /// Pick from screen as the app has it, so the popover shows its button in the app's state. A reference state only
    /// shows the popover and never picks.
    /// </summary>
    private IScreenColorPicker ReferenceScreen() => new ScreenColorPickerHost(_registry, _queue);

    /// <summary>The image another app copied for the Paste image states: 24 by 16 blue pixels, as in the Mac's ImagePasteTests.</summary>
    private static ClipboardImage ReferenceImage()
    {
        const int Width = 24, Height = 16;
        byte[] pixels = new byte[Width * Height * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 224;
            pixels[index + 1] = 115;
            pixels[index + 2] = 43;
            pixels[index + 3] = 255;
        }
        var image = new Annotator.Rendering.Imaging.RasterImage(Width, Height, Width * 4, pixels);
        return new ClipboardImage(ValueList.Create<byte>(Annotator.Rendering.Imaging.RasterAsset.EncodePng(image)), Width, Height);
    }

    /// <summary>Closes the arrowhead choices and the color popover, as AppHost.CloseTransientWindows does.</summary>
    private void CloseTransientWindows()
    {
        _popover?.Dismiss(restoreFocus: false);
        _picker?.Dismiss(restoreFocus: false);
    }

    private ToolsWindow? _tools;

    /// <summary>The docked toolbar's Tool library button opens the Tools window on screen, as AppHost does.</summary>
    private void OpenTools(WorkspaceModel model)
    {
        CloseTransientWindows();
        if (_tools is null)
        {
            _tools = new ToolsWindow(_registry, model, ShapeLibrary.InMemory(), new ShapeThumbnails(),
                () => _toolbar?.Frame, static previous => previous);
            model.Published += snapshot => _tools?.Apply(snapshot);
        }
        _tools.Show(null);
    }

    /// <summary>The docked toolbar's Stroke and Fill buttons open the color popover on screen, as AppHost does.</summary>
    private void OpenPicker(WorkspaceModel model, ColorPickerRequest request, RectInt32 anchor)
    {
        _picker?.Dismiss(restoreFocus: false);
        var picker = new ColorPopover(_registry, model, request, ReferenceScreen());
        picker.Dismissed += () =>
        {
            if (_picker == picker)
            {
                _picker = null;
            }
        };
        _picker = picker;
        picker.Open(anchor, PopoverPlacement.BelowOrAbove);
    }

    private async Task CanvasAsync(ReferenceScenario scenario, ReferenceProfile profile)
    {
        try
        {
            await ShowCanvasAsync(scenario, scenario.Canvas ?? throw new InvalidOperationException($"{_request.Name} has no canvas script."), profile);
        }
        catch (Exception error)
        {
            Fail(error.ToString());
        }
    }

    /// <summary>
    /// The break screen in a still state. Its end time is the Mac capture's (800,000,000 seconds after 2001) in this
    /// account's time format; nothing locks, beeps or keeps the computer awake.
    /// </summary>
    private void ShowBreak(WorkspaceModel model, ReferenceScenario scenario)
    {
        Annotator.Workspace.Breaks.BreakFace face = scenario.Break ?? throw new InvalidOperationException($"{_request.Name} has no break face.");
        _settle = scenario.Settle;
        var breaks = new Annotator.App.Breaks.BreakTimerHost(_queue, _registry, model);
        var end = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(800_000_000);
        bool showsEnd = face.Settings.ShowEndTime && face.Phase != Annotator.Workspace.Breaks.BreakPhase.TimeUp;
        var window = new BreakReferenceWindow(_registry, face with { EndTime = showsEnd ? breaks.Session.EndTimeText(end) : null },
            scenario.Width, scenario.Height);
        _popover = window;
        window.ShowForReference(OffscreenOrigin());
        Watch((FrameworkElement)window.Content, window.Hwnd);
    }

    /// <summary>Just right of the desktop's right edge, so the window is laid out and rendered but never seen.</summary>
    private static PointInt32 OffscreenOrigin()
    {
        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        return new PointInt32(left + width + 400, top + 200);
    }

    /// <summary>
    /// The toolbar's arrowhead choices, 8 pixels below the button that opened them and without activation, so a check of
    /// the full properties never takes focus from the person's app.
    /// </summary>
    private void ShowArrowHeads(WorkspaceModel model, bool start, RectInt32 anchor, Action closed)
    {
        _popover?.Dismiss(restoreFocus: false);
        var palette = new ArrowHeadPalette(_registry, model, start);
        palette.Dismissed += () =>
        {
            closed();
            if (_popover == palette)
            {
                _popover = null;
            }
        };
        _popover = palette;
        palette.ShowForReference(new PointInt32(anchor.X, anchor.Y + anchor.Height + 8));
    }

    private void Watch(FrameworkElement surface, HWND window)
    {
        _surface = surface;
        _window = window;
        _readyAt = DateTime.UtcNow + _settle;
        _deadline = _queue.CreateTimer();
        _deadline.Interval = TimeSpan.FromSeconds(15);
        _deadline.IsRepeating = false;
        _deadline.Tick += (_, _) => Fail("The surface did not render within 15 seconds.");
        _deadline.Start();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        if (_surface is not { IsLoaded: true, ActualWidth: > 0 } || ++_frames < 3 || DateTime.UtcNow < _readyAt)
        {
            return;
        }
        // The Pointer page's preview draws its halo once its window shows.
        if (_popover is SettingsReferenceWindow { Pointer.Preview.Draws: 0 })
        {
            return;
        }
        CompositionTarget.Rendering -= OnRendering;
        _ = CaptureAsync();
    }

    private async Task CaptureAsync()
    {
        try
        {
            FrameworkElement surface = _surface!;
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(surface);
            byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
            await File.WriteAllBytesAsync(ImagePath, pixels);
            await File.WriteAllTextAsync(LayoutPath, Layout(surface));
            if (!PInvoke.GetWindowRect(_window, out RECT rect))
            {
                throw new InvalidOperationException("The window rectangle is unavailable.");
            }
            double scale = surface.XamlRoot.RasterizationScale;
            string? simulation = SimulationProblem();
            if (simulation is not null)
            {
                Fail(simulation);
                return;
            }
            Finish(writer =>
            {
                Status(writer, "shown", null);
                writer.WriteNumber("hwnd", (long)(nint)_window);
                writer.WriteNumber("scale", scale);
                Pair(writer, "surface", surface.ActualWidth, surface.ActualHeight);
                writer.WriteStartArray("window");
                writer.WriteNumberValue(rect.left);
                writer.WriteNumberValue(rect.top);
                writer.WriteNumberValue(rect.right - rect.left);
                writer.WriteNumberValue(rect.bottom - rect.top);
                writer.WriteEndArray();
                writer.WriteString("image", Path.GetFileName(ImagePath));
                Pair(writer, "imageSize", bitmap.PixelWidth, bitmap.PixelHeight);
                if (_popover is SettingsReferenceWindow { Pointer: { } pointer })
                {
                    writer.WriteNumber("previewDraws", pointer.Preview.Draws);
                    writer.WriteString("previewFailure", pointer.Preview.Failure?.Message);
                    writer.WriteNumber("previewStops", pointer.Preview.Stops);
                    writer.WriteBoolean("previewShows", pointer.Preview.IsShowing);
                }
                if (_request.Accessibility)
                {
                    AccessibilityReport.Write(writer, surface, _request);
                }
            }, wait: true);
        }
        catch (Exception error)
        {
            Fail(error.ToString());
        }
    }

    /// <summary>
    /// Every visible element's frame in DIPs from the surface's top-left, as laid out, with its desired size: the tool
    /// keeps it next to the dump for finding where a frame difference starts.
    /// </summary>
    private static string Layout(FrameworkElement surface)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"XamlRoot {surface.XamlRoot.Size.Width:0.###} x {surface.XamlRoot.Size.Height:0.###} at {surface.XamlRoot.RasterizationScale:0.###}\n");
        for (DependencyObject? parent = VisualTreeHelper.GetParent(surface); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            text.Append(CultureInfo.InvariantCulture, $"in {parent.GetType().Name}");
            if (parent is FrameworkElement container)
            {
                text.Append(CultureInfo.InvariantCulture, $" {container.ActualWidth:0.###} x {container.ActualHeight:0.###} desired {container.DesiredSize.Width:0.###} x {container.DesiredSize.Height:0.###}");
            }
            text.Append('\n');
        }
        void Visit(DependencyObject node, int depth)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed })
            {
                return;
            }
            if (node is FrameworkElement element)
            {
                Windows.Foundation.Rect frame = element.TransformToVisual(surface)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
                text.Append(' ', depth * 2).Append(element.GetType().Name);
                string name = AutomationProperties.GetName(element);
                if (name.Length > 0)
                {
                    text.Append(" \"").Append(name).Append('"');
                }
                if (element is MacStack { ExactSize: { } exact })
                {
                    text.Append(CultureInfo.InvariantCulture, $" exact {exact.Width:0.###} x {exact.Height:0.###}");
                }
                if (element is TextBlock block)
                {
                    text.Append(" text \"").Append(block.Text).Append('"');
                }
                text.Append(CultureInfo.InvariantCulture,
                    $" [{frame.X:0.###}, {frame.Y:0.###}, {frame.Width:0.###}, {frame.Height:0.###}] desired {element.DesiredSize.Width:0.###} x {element.DesiredSize.Height:0.###}\n");
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
            {
                Visit(VisualTreeHelper.GetChild(node, index), depth + 1);
            }
        }
        Visit(surface, 0);
        return text.ToString();
    }

    /// <summary>
    /// The tray glyph as Windows draws it: the taskbar's small-icon size, black on light and white on dark, with the red dot
    /// while region sharing is on.
    /// </summary>
    private void WriteTrayIcon(bool sharing)
    {
        int size = TrayIcon.IconSize();
        RGBA color = TrayIcon.GlyphColor(_request.Theme == ReferenceTheme.Light ? TrayTheme.Light : TrayTheme.Dark);
        byte[] pixels = MarkGlyph.RenderBgra(size, color, premultiplied: !sharing);
        if (sharing)
        {
            TrayIcon.AddDot(pixels, size);
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                int alpha = pixels[offset + 3];
                for (int channel = 0; channel < 3; channel++)
                {
                    pixels[offset + channel] = (byte)((pixels[offset + channel] * alpha + 127) / 255);
                }
            }
        }
        File.WriteAllBytes(ImagePath, pixels);
        Finish(writer =>
        {
            Status(writer, "shown", null);
            writer.WriteNumber("scale", size / 16.0);
            Pair(writer, "surface", 16, 16);
            writer.WriteString("image", Path.GetFileName(ImagePath));
            Pair(writer, "imageSize", size, size);
        }, wait: false);
    }

    private void Fail(string reason) => Finish(writer => Status(writer, "failed", reason), wait: false);

    private void Status(Utf8JsonWriter writer, string status, string? reason)
    {
        writer.WriteString("state", _request.Name);
        writer.WriteString("theme", _request.Theme == ReferenceTheme.Light ? "light" : "dark");
        writer.WriteString("status", status);
        if (reason is not null)
        {
            writer.WriteString("reason", reason);
        }
        writer.WriteNumber("processId", Environment.ProcessId);
    }

    private static void Pair(Utf8JsonWriter writer, string name, double first, double second)
    {
        writer.WriteStartArray(name);
        writer.WriteNumberValue(first);
        writer.WriteNumberValue(second);
        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes ready.json in one step, then quits: at once, or after the tool's done event, waiting at most the
    /// request's timeout.
    /// </summary>
    private void Finish(Action<Utf8JsonWriter> write, bool wait)
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        _deadline?.Stop();
        CompositionTarget.Rendering -= OnRendering;
        EventWaitHandle? done = wait ? new EventWaitHandle(false, EventResetMode.ManualReset, DoneEventName(Environment.ProcessId)) : null;
        try
        {
            string temporary = ReadyPath + ".tmp";
            using (FileStream stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }
            File.Move(temporary, ReadyPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupLog.Note("ux-reference", error.Message);
        }
        if (done is null)
        {
            Quit();
            return;
        }
        int timeout = _request.TimeoutSeconds * 1000;
        var capture = new EventWaitHandle(false, EventResetMode.AutoReset, CaptureEventName(Environment.ProcessId));
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int captures = 0;
            for (int remaining = timeout; remaining > 0; remaining = timeout - (int)clock.ElapsedMilliseconds)
            {
                if (WaitHandle.WaitAny([done, capture], remaining) != 1)
                {
                    break;
                }
                int index = ++captures;
                _queue.TryEnqueue(() => _ = CaptureAgainAsync(index));
            }
            done.Dispose();
            capture.Dispose();
            _queue.TryEnqueue(Quit);
        });
    }

    /// <summary>The event the tool sets when it has read the surface.</summary>
    public static string DoneEventName(int processId) => $@"Local\Annotator.UXReference.{processId}";

    /// <summary>
    /// The event a development check sets to have the surface rendered again after it changed the state through UI
    /// Automation: each signal writes <c>surface-N.bgra</c> and then <c>capture-N.json</c>.
    /// </summary>
    public static string CaptureEventName(int processId) => $@"Local\Annotator.UXReference.{processId}.capture";

    private async Task CaptureAgainAsync(int index)
    {
        string image = Path.Combine(_request.Output, $"surface-{index}.bgra");
        string report = Path.Combine(_request.Output, $"capture-{index}.json");
        string? reason = null;
        int width = 0, height = 0;
        try
        {
            FrameworkElement surface = _surface ?? throw new InvalidOperationException("There is no surface.");
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(surface);
            await File.WriteAllBytesAsync(image, (await bitmap.GetPixelsAsync()).ToArray());
            (width, height) = (bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (Exception error)
        {
            reason = error.Message;
        }
        string temporary = report + ".tmp";
        using (FileStream stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("status", reason is null ? "shown" : "failed");
            if (reason is not null)
            {
                writer.WriteString("reason", reason);
            }
            writer.WriteString("image", Path.GetFileName(image));
            Pair(writer, "imageSize", width, height);
            writer.WriteEndObject();
        }
        File.Move(temporary, report, overwrite: true);
    }

    private void Quit()
    {
        _tools?.Shutdown();
        _toolbar?.Shutdown();
        _flyout?.Dismiss(restoreFocus: false);
        _popover?.Dismiss(restoreFocus: false);
        _picker?.Dismiss(restoreFocus: false);
        if (_overlays is { } overlays)
        {
            _overlays = null;
            _ = StopThenExitAsync(overlays);
            return;
        }
        Application.Current.Exit();
    }
}
