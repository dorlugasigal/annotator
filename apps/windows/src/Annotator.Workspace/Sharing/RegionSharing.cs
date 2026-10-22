using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace.Sharing;

/// <summary>
/// Port of the Mac RegionSharingController (specification 012, ADR 0013, WIN-CAP-08) without its windows: the displays and
/// presets of the Share a region window, custom regions and their checks, Start and Stop, the quick halves, and the
/// safety stops after region, display and system changes. One region on one display is shared at a time; a missing
/// display or an invalid region is never replaced silently. The host draws the chooser, the guides and the selection
/// panels (<see cref="IRegionSharingHost"/>); the output window follows <see cref="WorkspaceSnapshot.RegionSharing"/> and
/// reports back through <see cref="OutputPresented"/>, <see cref="OutputWaiting"/>, <see cref="OutputFailed"/>,
/// <see cref="OutputStopped"/> and <see cref="OutputClosed"/>. Use it on the app thread; every change publishes a
/// workspace snapshot. Nothing here starts capture or changes drawings while choosing.
/// </summary>
public sealed class RegionSharing
{
    /// <summary>The Mac's readiness wait: a start or a scene change with no frame on screen by then fails.</summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(8);

    private readonly WorkspaceModel _model;
    private readonly IRegionSharingHost _host;
    private ImmutableArray<RegionSharingDisplay> _displays = [];
    private DisplayId? _selectedDisplay;
    private ShareRegionPreset? _preset = ShareRegionPreset.LeftHalf;
    private SharingRegion? _region;
    private string? _validation;
    private RegionSharingState _state = RegionSharingState.Idle.Instance;
    private ulong _generation;
    private ulong _identity;
    private bool _outputOpen;
    private SharingRegion? _outputRegion;
    private DisplayDescriptor? _source;
    private bool _capturing;
    private string _cover = RegionSharingText.StartingCover;
    private IDisposable? _readiness;

    public RegionSharing(WorkspaceModel model, IRegionSharingHost host)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(host);
        _model = model;
        _host = host;
        model.Attach(this);
    }

    /// <summary>The connected displays the chooser lists, in the system's order.</summary>
    public ImmutableArray<RegionSharingDisplay> Displays => _displays;

    public DisplayId? SelectedDisplayId => _selectedDisplay;

    public RegionSharingDisplay? SelectedDisplay => _selectedDisplay is { } id ? Find(id) : null;

    /// <summary>The preset of the region, or null for a custom region.</summary>
    public ShareRegionPreset? SelectedPreset => _preset;

    public SharingRegion? Region => _region;

    /// <summary>Why the region cannot be shared, shown in red under the controls, or null.</summary>
    public string? ValidationMessage => _validation;

    public RegionSharingState State => _state;

    /// <summary>True once an output window exists; it keeps its identity until the person closes it or Annotator quits.</summary>
    public bool HasOutput => _outputOpen;

    /// <summary>The output's generation (tests and the overlay thread's reports).</summary>
    public ulong Generation => _generation;

    /// <summary>What <see cref="WorkspaceSnapshot.RegionSharing"/> shows.</summary>
    internal RegionSharingSnapshot Snapshot => new(_state, _preset,
        _outputOpen && _outputRegion is { } region && (_source ?? SelectedDisplay?.Descriptor) is { } display
            ? new RegionSharingOutput(_identity, display, region, _generation, _cover, _capturing)
            : null);

    /// <summary>Mac showControls: reads the displays, chooses one and shows the window with the outline.</summary>
    public void ShowControls()
    {
        using var scope = _model.SharingChange();
        RefreshDisplays();
        ChooseInitialDisplay();
        _host.ShowControls();
        UpdateOutline();
    }

    /// <summary>
    /// Mac refreshDisplays: reads the connected displays again. Sharing stops when the selected display disconnected or
    /// changed size, and the region is not replaced with another.
    /// </summary>
    public void RefreshDisplays()
    {
        using var scope = _model.SharingChange();
        try
        {
            IReadOnlyList<DisplayDescriptor> candidates = _model.DisplaysForRegionSharing();
            _displays = [.. candidates.Select((display, index) =>
                new RegionSharingDisplay(display, _host.DisplayName(display.Id) is { Length: > 0 } name ? name : $"Display {index + 1}"))];
            if (_selectedDisplay is not null && SelectedDisplay is null)
            {
                StopForSafety(RegionSharingText.DisplayDisconnected);
                _region = null;
                _validation = RegionSharingText.DisplayUnavailable;
            }
            else if (_region is { } region && SelectedDisplay is { } selected && region.DisplayBounds != selected.LocalBounds)
            {
                StopForSafety(RegionSharingText.DisplaySizeChanged);
                _region = null;
                _validation = RegionSharingText.RegionInvalid;
            }
            UpdateOutline();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _displays = [];
            _region = null;
            StopForSafety(RegionSharingText.DisplayListUnreadable);
            _validation = error.Message;
            _model.Report(error.Message);
        }
    }

    /// <summary>Mac selectDisplay: the preset on the new display, or a request to draw or enter a custom region there.</summary>
    public void SelectDisplay(DisplayId id)
    {
        using var scope = _model.SharingChange();
        if (Find(id) is not { } display)
        {
            _validation = RegionSharingText.DisplayUnavailable;
            return;
        }
        if (_selectedDisplay == id && _region is not null)
        {
            return;
        }
        StopForRegionChange();
        _selectedDisplay = id;
        if (_preset is { } preset)
        {
            try
            {
                _region = new SharingRegion(preset, display.LocalBounds);
                _validation = null;
            }
            catch (CanvasError error)
            {
                _region = null;
                _validation = error.Message;
            }
        }
        else
        {
            _region = null;
            _validation = RegionSharingText.CustomNeeded;
        }
        UpdateOutline();
    }

    /// <summary>Mac choosePreset: a different region stops a running share first; the output keeps its identity.</summary>
    public void ChoosePreset(ShareRegionPreset preset)
    {
        using var scope = _model.SharingChange();
        if (SelectedDisplay is not { } display)
        {
            _validation = RegionSharingText.DisplayUnavailable;
            return;
        }
        try
        {
            var selected = new SharingRegion(preset, display.LocalBounds);
            if (selected != _region)
            {
                StopForRegionChange();
            }
            _preset = preset;
            _region = selected;
            _outputRegion = selected;
            _validation = null;
            UpdateOutline();
        }
        catch (CanvasError error)
        {
            _validation = error.Message;
        }
    }

    /// <summary>Mac applyCustom on the selected display: refuses nonfinite, out-of-display and small regions with a message.</summary>
    public void ApplyCustom(Rect bounds)
    {
        using var scope = _model.SharingChange();
        if (SelectedDisplay is not { } display)
        {
            _validation = RegionSharingText.DisplayUnavailable;
            return;
        }
        ApplyCustom(bounds, display);
    }

    /// <summary>Mac invalidCustomNumbers: a field does not hold a number.</summary>
    public void InvalidCustomNumbers()
    {
        using var scope = _model.SharingChange();
        _validation = RegionSharingText.NumbersNeeded;
    }

    /// <summary>
    /// Mac drawCustomRegion: hides the chooser and the outline and prepares a selection panel on every display. A completed
    /// drag sets the display and the region together; Escape and focus loss keep the previous ones.
    /// </summary>
    public void DrawCustomRegion()
    {
        using var scope = _model.SharingChange();
        if (SelectedDisplay is not { } display)
        {
            _validation = RegionSharingText.DisplayUnavailable;
            return;
        }
        StopForRegionChange();
        _host.HideControls();
        _host.HideOutline();
        _host.ChooseRegion(_displays, display.Id, result =>
        {
            using var completion = _model.SharingChange();
            if (result is RegionSelectionResult.Selected selected)
            {
                if (Find(selected.Display) is { } target)
                {
                    ApplyCustom(selected.Bounds, target);
                }
                else
                {
                    _validation = RegionSharingText.DisplayUnavailable;
                }
            }
            _host.RestoreControls(activate: result is not RegionSelectionResult.FocusLost);
            UpdateOutline();
        });
    }

    /// <summary>
    /// Mac start: checks the motion, the display, the region and the session lock, prepares the display's canvas, gives the
    /// apps their input back (Use apps), and opens the output behind other apps, covered until its first frame.
    /// </summary>
    public void Start()
    {
        if (_state.IsCapturing)
        {
            return;
        }
        using var scope = _model.SharingChange();
        RefreshDisplays();
        ChooseInitialDisplay();
        try
        {
            if (_model.VisibilityAnimating)
            {
                throw new RegionSharingFailure(RegionSharingText.Transition);
            }
            _model.FinishStyleEdit();
            RegionSharingDisplay display = SelectedDisplay ?? throw new RegionSharingFailure(RegionSharingText.DisplayUnavailable);
            if (_validation is not null || _region is not { } region || region.DisplayBounds != display.LocalBounds)
            {
                throw new RegionSharingFailure(RegionSharingText.RegionInvalid);
            }
            // Mac start: screen access first (requestCapturePermission). Sharing starts once Windows allows it; a refusal or
            // no answer fails it with where to allow it, and Not now leaves the chooser as it was.
            if (!_model.EnsureScreenAccess(Capture.ScreenAccessFeature.RegionSharing, Start, message =>
                {
                    if (message is not null)
                    {
                        using var failure = _model.SharingChange();
                        Fail(message, captureFailure: true);
                    }
                }))
            {
                return;
            }
            if (_host.DesktopIsLocked)
            {
                throw new RegionSharingFailure(RegionSharingText.DesktopLocked, capture: true);
            }
            _model.PrepareRegionSharingSession(display.Id);
            _model.UseApps();
            if (!_outputOpen)
            {
                _outputOpen = true;
                _identity++;
            }
            _outputRegion = region;
            _generation++;
            _cover = RegionSharingText.StartingCover;
            _source = display.Descriptor;
            _capturing = true;
            _state = RegionSharingState.Starting.Instance;
            _validation = null;
            UpdateOutline();
            WatchReadiness();
        }
        catch (RegionSharingFailure failure)
        {
            Fail(failure.Message, failure.Capture);
        }
    }

    /// <summary>Mac toggle: Start region sharing, or Stop region sharing while it runs.</summary>
    public void Toggle()
    {
        if (_state.IsCapturing)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    /// <summary>Mac share(preset:): the menu's quick halves share that part of the selected display without the chooser.</summary>
    public void Share(ShareRegionPreset preset)
    {
        using var scope = _model.SharingChange();
        RefreshDisplays();
        ChooseInitialDisplay();
        ChoosePreset(preset);
        if (_validation is null)
        {
            Start();
        }
    }

    /// <summary>
    /// Mac stopForSafety: turns sharing off with <paramref name="message"/> on the opaque output, and releases capture.
    /// Does nothing before an output or capture exists.
    /// </summary>
    public void StopForSafety(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        using var scope = _model.SharingChange();
        _host.CancelSelection();
        if (!_outputOpen && !_capturing)
        {
            return;
        }
        _generation++;
        _state = new RegionSharingState.Stopped(message);
        _cover = message;
        StopTasks();
        _capturing = false;
        UpdateOutline();
    }

    /// <summary>Mac stop: Stop region sharing, or closing the output with <paramref name="closeOutput"/>.</summary>
    public void Stop(bool closeOutput = false)
    {
        using var scope = _model.SharingChange();
        _generation++;
        _state = RegionSharingState.Idle.Instance;
        _cover = RegionSharingText.OffCover;
        StopTasks();
        _capturing = false;
        if (closeOutput)
        {
            _outputOpen = false;
            _source = null;
        }
        _host.CancelSelection();
        UpdateOutline();
    }

    /// <summary>Mac close: quitting stops sharing, closes the output, the chooser and the guides.</summary>
    public void Close()
    {
        using var scope = _model.SharingChange();
        Stop(closeOutput: true);
        _host.CloseControls();
        _host.CancelSelection();
        _host.HideOutline();
    }

    /// <summary>Mac windowWillClose of the chooser: a selection ends, and the outline goes unless sharing runs.</summary>
    public void ControlsClosed()
    {
        using var scope = _model.SharingChange();
        _host.CancelSelection();
        if (!_state.IsCapturing)
        {
            _host.HideOutline();
        }
    }

    /// <summary>
    /// Mac presented: the output shows a frame of <paramref name="generation"/> with the current scene, so sharing is live.
    /// A stale generation does nothing.
    /// </summary>
    public void OutputPresented(ulong generation)
    {
        if (generation != _generation || !_state.IsCapturing || (_state is RegionSharingState.Live && _readiness is null))
        {
            return;
        }
        using var scope = _model.SharingChange();
        StopTasks();
        if (_state is not RegionSharingState.Live)
        {
            _state = RegionSharingState.Live.Instance;
        }
    }

    /// <summary>
    /// The output covered itself while the scene changed (Mac receive and the workspace motion): sharing is starting again
    /// until the new scene is on screen, within the readiness wait.
    /// </summary>
    public void OutputWaiting(ulong generation)
    {
        if (generation != _generation || !_state.IsCapturing)
        {
            return;
        }
        using var scope = _model.SharingChange();
        _state = RegionSharingState.Starting.Instance;
        WatchReadiness();
    }

    /// <summary>Mac fail from the output or the capture: sharing fails opaquely with <paramref name="message"/>.</summary>
    public void OutputFailed(ulong generation, string message, bool captureFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (generation != _generation || !_state.IsCapturing)
        {
            return;
        }
        using var scope = _model.SharingChange();
        Fail(message, captureFailure);
    }

    /// <summary>Mac captureStopped: the live view under the region stopped, so sharing stops for safety.</summary>
    public void OutputStopped(ulong generation, string message)
    {
        if (generation != _generation || !_state.IsCapturing)
        {
            return;
        }
        StopForSafety(message);
    }

    /// <summary>Mac onClosed: closing the output stops sharing; the chooser stays.</summary>
    public void OutputClosed() => Stop(closeOutput: true);

    private RegionSharingDisplay? Find(DisplayId id)
    {
        foreach (RegionSharingDisplay display in _displays)
        {
            if (display.Id == id)
            {
                return display;
            }
        }
        return null;
    }

    /// <summary>Mac chooseInitialDisplay: the editing target's display, else the one under the pointer, else the first.</summary>
    private void ChooseInitialDisplay()
    {
        if (_selectedDisplay is not null || _displays.IsEmpty)
        {
            return;
        }
        DisplayId? active = _model.Snapshot.ActiveDisplay;
        Point? pointer = _model.PointerPosition();
        RegionSharingDisplay chosen = _displays.FirstOrDefault(display => display.Id == active)
            ?? (pointer is { } position ? _displays.FirstOrDefault(display => display.Descriptor.ContainsDesktopPoint(position)) : null)
            ?? _displays[0];
        SelectDisplay(chosen.Id);
    }

    private void ApplyCustom(Rect bounds, RegionSharingDisplay display)
    {
        try
        {
            var selected = new SharingRegion(bounds, display.LocalBounds);
            if (selected != _region || _selectedDisplay != display.Id)
            {
                StopForRegionChange();
            }
            _selectedDisplay = display.Id;
            _region = selected;
            _outputRegion = selected;
            _preset = null;
            _validation = null;
            UpdateOutline();
        }
        catch (CanvasError)
        {
            _validation = RegionSharingText.RegionInvalid;
        }
    }

    private void StopForRegionChange()
    {
        _host.CancelSelection();
        if (_outputOpen)
        {
            StopForSafety(RegionSharingText.RegionChanged);
        }
    }

    private void Fail(string message, bool captureFailure)
    {
        _generation++;
        _state = new RegionSharingState.Failed(message);
        _cover = message;
        StopTasks();
        _capturing = false;
        UpdateOutline();
        _model.Report(message);
        if (captureFailure)
        {
            _model.ReportCaptureFailure(message);
        }
    }

    /// <summary>Mac updateOutline: the outline shows while the chooser is open or sharing runs; its fill only while choosing.</summary>
    private void UpdateOutline()
    {
        if (SelectedDisplay is not { } display || _region is not { } region || !(_host.ControlsVisible || _state.IsCapturing))
        {
            _host.HideOutline();
            return;
        }
        _host.ShowOutline(region, display.Descriptor, selectionFill: !_state.IsCapturing);
    }

    private void StopTasks()
    {
        _readiness?.Dispose();
        _readiness = null;
    }

    /// <summary>Mac watchReadiness: a start or scene change that shows no frame within eight seconds fails.</summary>
    private void WatchReadiness()
    {
        _readiness?.Dispose();
        ulong requested = _generation;
        IDisposable? timer = null;
        timer = _host.Schedule(ReadinessTimeout, () =>
        {
            if (!ReferenceEquals(_readiness, timer) || _generation != requested || _state is not RegionSharingState.Starting)
            {
                return;
            }
            using var scope = _model.SharingChange();
            _readiness = null;
            Fail(RegionSharingText.NoFrame, captureFailure: true);
        });
        _readiness = timer;
    }

}

/// <summary>A reason region sharing cannot start (Mac RegionSharingFailure and CaptureFailure), with its words.</summary>
internal sealed class RegionSharingFailure(string message, bool capture = false) : Exception(message)
{
    /// <summary>A capture failure, which the toolbar's capture warning also shows.</summary>
    public bool Capture { get; } = capture;
}
