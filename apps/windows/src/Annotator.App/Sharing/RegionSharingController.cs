using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Sharing;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Presentation;
using Annotator.Workspace;
using Annotator.Workspace.Sharing;
using Microsoft.UI.Dispatching;
using Windows.Win32;

namespace Annotator.App.Sharing;

/// <summary>
/// Region sharing's windows on the app thread (Mac RegionSharingController with RegionSharingGuide): the Share a region
/// window, the outline of the region on the desktop and the panels that draw a custom region. The output window is drawn
/// on the overlay thread from the workspace snapshot (<see cref="SharedRegionOutput"/>).
/// </summary>
internal sealed class RegionSharingController : IRegionSharingHost, IDisposable
{
    private const string Instruction = "Drag a rectangle to choose the region. Escape cancels.";

    // The Mac's instruction: the system font at 16 points, medium weight, white, 24 points from the top-left corner.
    private static readonly TextFont InstructionFont = new(FontDesign.System, (FontWeight)500, 16);

    private readonly WindowRegistry _registry;
    private readonly DispatcherQueue _queue;
    private readonly Action<string, Exception> _report;
    private readonly List<(RegionSelectionPanel Panel, OverlayHud? Text)> _panels = [];
    private RegionSharing? _sharing;
    private RegionSharingWindow? _window;
    private RegionOutlineWindow? _outline;
    private IReadOnlyDictionary<string, string>? _names;
    private Action<RegionSelectionResult>? _completed;
    private GraphicsDevices? _devices;
    private bool _disposed;

    /// <param name="report">Shows a failure of these windows to the person.</param>
    public RegionSharingController(WindowRegistry registry, DispatcherQueue queue, Action<string, Exception> report)
    {
        _registry = registry;
        _queue = queue;
        _report = report;
    }

    /// <summary>The Share a region window, once it was opened.</summary>
    internal RegionSharingWindow? Window => _window;

    /// <summary>The outline, while it shows (reference capture and live checks).</summary>
    internal RegionOutlineWindow? Outline => _outline;

    /// <summary>The selection panels while a custom region is drawn.</summary>
    internal IReadOnlyList<RegionSelectionPanel> Panels => [.. _panels.Select(entry => entry.Panel)];

    public bool DesktopIsLocked { get; set; }

    public bool ControlsVisible => _window?.IsShown == true;

    public bool IsSelecting => _panels.Any(entry => entry.Panel.TakesInput);

    /// <summary>Gives the controller the region sharing it serves (created with this controller as its host).</summary>
    public void Attach(RegionSharing sharing) => _sharing = sharing;

    /// <summary>Follows the workspace while the window shows.</summary>
    public void Apply()
    {
        if (_window is { IsShown: true } window)
        {
            window.Refresh();
        }
    }

    public string? DisplayName(DisplayId display)
    {
        // Names are read once per display list (Mac NSScreen.localizedName), when the chooser refreshes its displays.
        _names ??= DisplayNames.Read();
        return _names.TryGetValue(display.Value, out string? name) ? name : null;
    }

    /// <summary>The display list changed: names are read again with the next refresh.</summary>
    public void DisplaysChanged() => _names = null;

    public void ShowControls()
    {
        if (_disposed || _sharing is null)
        {
            return;
        }
        _names = null;
        _window ??= new RegionSharingWindow(_registry, _sharing, () => _sharing?.ControlsClosed());
        _window.ShowAndActivate(_sharing.SelectedDisplay?.Descriptor);
    }

    public void HideControls() => _window?.HideWindow();

    public void RestoreControls(bool activate) => _window?.Restore(activate);

    public void CloseControls()
    {
        _window?.Shutdown();
        _window = null;
    }

    public void ShowOutline(SharingRegion region, DisplayDescriptor display, bool selectionFill)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            _outline ??= new RegionOutlineWindow(_registry, Shell.WindowChrome.SystemCompositor(), Accent);
            _outline.Show(ScreenFrame(region.Bounds, display), display.Scale, selectionFill);
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            _report("Show the region outline", error);
        }
    }

    public void HideOutline() => _outline?.Hide();

    /// <summary>Mac RegionSharingGuide.screenFrame: the region in points on its display, in desktop pixels.</summary>
    internal static PixelRect ScreenFrame(Rect region, DisplayDescriptor display)
    {
        Rect desktop = display.DesktopFrame;
        double scale = display.Scale;
        return new PixelRect((int)Math.Round(desktop.X + region.X * scale), (int)Math.Round(desktop.Y + region.Y * scale),
                             (int)Math.Round(desktop.X + region.MaxX * scale), (int)Math.Round(desktop.Y + region.MaxY * scale));
    }

    public void ChooseRegion(IReadOnlyList<RegionSharingDisplay> displays, DisplayId preferred, Action<RegionSelectionResult> completed)
    {
        CancelSelection();
        _completed = completed;
        try
        {
            _devices ??= GraphicsDevices.Create();
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException)
        {
            _report("Prepare the region selection", error);
            Finish(RegionSelectionResult.Cancelled.Instance);
            return;
        }
        RegionSelectionPanel? keyboard = null;
        foreach (RegionSharingDisplay display in displays)
        {
            // The display as the workspace knows it: the panel covers its desktop frame at its scale.
            Rect frame = display.Descriptor.DesktopFrame, work = display.Descriptor.DesktopWorkArea;
            var info = new DisplayInfo(0, display.Id.Value,
                new PixelRect((int)Math.Round(frame.X), (int)Math.Round(frame.Y), (int)Math.Round(frame.MaxX), (int)Math.Round(frame.MaxY)),
                new PixelRect((int)Math.Round(work.X), (int)Math.Round(work.Y), (int)Math.Round(work.MaxX), (int)Math.Round(work.MaxY)),
                (uint)Math.Round(96 * display.Scale), IsPrimary: false);
            try
            {
                var panel = new RegionSelectionPanel(_registry, Shell.WindowChrome.SystemCompositor(), info, Accent);
                OverlayHud? text = null;
                try
                {
                    text = new OverlayHud(panel.Overlay.Compositor, panel.Overlay, _devices, FixedSystemColors.Light, display.Scale);
                    var line = new Rect(24, 24, Math.Max(1, display.LocalBounds.Width - 48), 24);
                    DisplayList list = new DisplayListBuilder()
                        .Add(DisplayItem.DrawText(new TextRun(Instruction, line, InstructionFont, TextAlignment.Left, Paint.Solid(new RGBA(1, 1, 1)))))
                        .ToList();
                    text.Show(list, new Rect(0, 0, display.LocalBounds.Width, 64));
                    text.SetVisible(true, animate: false, reduceMotion: true);
                }
                catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
                {
                    // The panel still works without its words.
                    text?.Dispose();
                    text = null;
                    _report("Show the region selection's instruction", error);
                }
                panel.Completed += OnCompleted;
                panel.Cancelled += OnCancelled;
                panel.Deactivated += OnDeactivated;
                _panels.Add((panel, text));
                panel.Show();
                if (display.Id == preferred)
                {
                    keyboard = panel;
                }
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
            {
                _report("Show the region selection", error);
            }
        }
        if (_panels.Count == 0)
        {
            Finish(RegionSelectionResult.Cancelled.Instance);
            return;
        }
        (keyboard ?? _panels[0].Panel).Activate();
    }

    public void CancelSelection()
    {
        _completed = null;
        ReleasePanels();
    }

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        DispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        var scheduled = new ScheduledAction(timer);
        timer.Tick += (_, _) =>
        {
            if (scheduled.Fire())
            {
                action();
            }
        };
        timer.Start();
        return scheduled;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CancelSelection();
        CloseControls();
        _outline?.Dispose();
        _outline = null;
        _devices?.Dispose();
        _devices = null;
    }

    private static Windows.UI.Color Accent => Windows.UI.Color.FromArgb(255, 0, 0x7A, 0xFF);

    private void OnCompleted(RegionSelectionPanel panel, SelectionRect? selection)
    {
        RegionSelectionResult result = selection is { } rect
            ? new RegionSelectionResult.Selected(new DisplayId(panel.Display.DeviceName), new Rect(rect.X, rect.Y, rect.Width, rect.Height))
            : RegionSelectionResult.Cancelled.Instance;
        Finish(result);
    }

    private void OnCancelled(RegionSelectionPanel panel) => Finish(RegionSelectionResult.Cancelled.Instance);

    /// <summary>
    /// Mac selectionFocusChanged: once a panel loses the keyboard, the selection ends unless another panel took it. The check
    /// waits for the activation to settle.
    /// </summary>
    private void OnDeactivated(RegionSelectionPanel panel) => _queue.TryEnqueue(() =>
    {
        if (_completed is null || _panels.Count == 0)
        {
            return;
        }
        nint foreground = (nint)PInvoke.GetForegroundWindow();
        if (_panels.Any(entry => entry.Panel.Handle == foreground))
        {
            return;
        }
        Finish(RegionSelectionResult.FocusLost.Instance);
    });

    private void Finish(RegionSelectionResult result)
    {
        Action<RegionSelectionResult>? completed = _completed;
        _completed = null;
        ReleasePanels();
        completed?.Invoke(result);
    }

    private void ReleasePanels()
    {
        foreach ((RegionSelectionPanel panel, OverlayHud? text) in _panels)
        {
            panel.Completed -= OnCompleted;
            panel.Cancelled -= OnCancelled;
            panel.Deactivated -= OnDeactivated;
            text?.Dispose();
            panel.Dispose();
        }
        _panels.Clear();
        // The devices only serve the instruction's words; they go with the panels.
        _devices?.Dispose();
        _devices = null;
    }

    private sealed class ScheduledAction(DispatcherQueueTimer timer) : IDisposable
    {
        private bool _done;

        public bool Fire()
        {
            if (_done)
            {
                return false;
            }
            _done = true;
            timer.Stop();
            return true;
        }

        public void Dispose()
        {
            _done = true;
            timer.Stop();
        }
    }
}
