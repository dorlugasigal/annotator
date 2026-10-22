using System.Collections.Immutable;
using System.Numerics;
using Annotator.App.Motion;
using Annotator.App.Overlay;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform;
using Annotator.Platform.Accessibility;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Overlays;
using Annotator.Platform.Sharing;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Presentation;
using Annotator.Rendering.Sharing;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Sharing;
using Windows.UI.Composition;

namespace Annotator.App.Sharing;

/// <summary>
/// The output window of region sharing on the overlay thread (Mac SharedRegionWindow with RegionSharingController.receive):
/// an ordinary window behind the apps that shows the region of its display as the screen shows it, with the drawings,
/// covers and pixel effects of that display's canvas, letterboxed in black and drawn with the pointer, Spotlight and Cursor
/// halo. Under screen zoom it shows the region magnified as the screen shows it. It is covered
/// opaquely while it starts, while its covers, effects or the drawings' visibility change, and whenever sharing is off,
/// so a meeting app never receives pixels that the screen hides (WIN-CAP-08). What the window shows is reported to the app
/// thread as <see cref="OverlayEvent"/>s for <see cref="RegionSharing"/>.
/// </summary>
internal sealed class SharedRegionOutput : IVisibilityMirror, IDisposable
{
    private const double CoverFontSize = 18;
    private const int CoverLines = 4;
    private const double CoverBoxHeight = 100;
    private const double CoverInset = 24;

    /// <summary>The pointer's arrow is drawn this many times sharper than a point and scaled down with the region.</summary>
    private const double PointerResolution = 4;

    // The Mac's coverText font: the system font at 18 points, medium weight (DirectWrite's 500).
    private static readonly TextFont CoverFont = new(FontDesign.System, (FontWeight)500, CoverFontSize);

    private readonly Compositor _compositor;
    private readonly WindowRegistry _registry;
    private readonly IDisplayCapture _capture;
    private readonly Action<OverlayEvent> _report;
    private readonly ITextMeasurer _measurer;
    private readonly Func<ISystemColorResolver> _colors;
    private readonly PointerEffectsHost _pointerEffects;
    private readonly string _title;

    private GraphicsDevices? _devices;
    private SharedRegionWindow? _window;
    private SharedRegionPresenter? _presenter;
    private SharedToolbarLayer? _toolbar;
    private Windows.Graphics.RectInt32? _toolbarFrame;
    private SpriteVisual? _cover;
    private ContainerVisual? _coverLayer;
    private OverlayHud? _coverText;
    private ContainerVisual? _pointerLayer;
    private OverlayHud? _pointer;
    private ContainerVisual? _effectsRoot;
    private PointerEffectsLayer? _effects;
    private OverlayAccessibility? _accessibility;
    private ulong _identity;
    private RegionSharingOutput? _output;
    private DisplayInfo? _display;
    private DrawingCanvas? _source;
    private WorkspaceSnapshot? _workspace;
    private ulong _generation;
    private bool _generationSeen;

    // What the shown scene protects; a change covers the output until a frame of the new scene is on screen.
    private bool _hasContent;
    private bool _visible;
    private ImmutableArray<Element> _protection = [];
    private long _version;
    private long _uncoverAt = long.MaxValue;
    private string? _coverMessage;
    private bool _failed;
    private bool _reportedLive;
    private SharedRegionScene? _scene;
    private (int X, int Y)? _pointerAt;
    private PixelRect _fit;

    // The show and hide motion's copies (Mac SharedWorkspaceTransition) and the covers and effects drawn again above them.
    private ContainerVisual? _motionRoot;
    private ContainerVisual? _lidRoot;
    private SharedMotion? _motion;
    private readonly List<SpriteVisual> _lids = [];
    private SceneBuilder? _bounds;
    private long _presented = -1;
    private long _handOffAt = long.MaxValue;
    private bool _holdingToolbar;
    private bool _coverPending;
    private ElementList _motionDocument;
    private RGBA? _motionBoard;

    public SharedRegionOutput(Compositor compositor, WindowRegistry registry, IDisplayCapture capture, Action<OverlayEvent> report,
                              ITextMeasurer measurer, Func<ISystemColorResolver> colors, PointerEffectsHost pointerEffects, string title)
    {
        _pointerEffects = pointerEffects;
        _compositor = compositor;
        _registry = registry;
        _capture = capture;
        _report = report;
        _measurer = measurer;
        _colors = colors;
        _title = title;
    }

    /// <summary>The output window, while there is one (live checks and measurements).</summary>
    internal SharedRegionWindow? Window => _window;

    /// <summary>The region presenter, while sharing captures (measurements).</summary>
    internal SharedRegionPresenter? Presenter => _presenter;

    /// <summary>The toolbar's window, which the output captures on its own (Annotator's windows are left out of screen capture).</summary>
    public nint ToolbarWindow { get; set; }

    /// <summary>The words on the cover, or null while the region shows.</summary>
    internal string? CoverMessage => _coverMessage;

    /// <summary>
    /// Development only (live checks and measurements): shows and hides the output played as motion, the copies they made,
    /// the particle frames they drew, the covers shown while a motion played (besides the one each motion starts with) and
    /// the hand-offs to the live scene.
    /// </summary>
    internal (int Transitions, int Copies, int ParticleFrames, int Covers, int HandOffs) MotionStatistics =>
        (_motionTransitions + (_motion?.Statistics.Transitions ?? 0), _motionCopies + (_motion?.Statistics.Copies ?? 0),
         _motionParticleFrames + (_motion?.Statistics.ParticleFrames ?? 0), _motionCovers, _handOffs);

    /// <summary>Development only (measurements): the shared view's frames drawn and not drawn while it is live.</summary>
    internal Annotator.Rendering.Effects.EffectsStatistics? FrameStatistics => _presenter?.Statistics;

    private int _motionTransitions;
    private int _motionCopies;
    private int _motionParticleFrames;
    private int _motionCovers;
    private int _handOffs;

    /// <summary>Follows the workspace: opens, updates or closes the window for <see cref="WorkspaceSnapshot.RegionSharing"/>.</summary>
    public void Presented(OverlayFrame frame, IReadOnlyDictionary<OverlayWindow, IOverlayCanvas> canvases, GraphicsDevices? devices)
    {
        _workspace = frame.Workspace;
        _toolbarFrame = frame.Toolbar?.Frame;
        if (!ReferenceEquals(devices, _devices))
        {
            _devices = devices;
            _coverText?.ReplaceDevices(devices);
            _pointer?.ReplaceDevices(devices);
            _effects?.ReplaceDevices(devices);
            if (_motion is { } motion)
            {
                bool active = motion.IsActive;
                motion.ReplaceDevices(devices);
                if (active)
                {
                    // The copies went with the old devices: the region starts again, covered until its next frame.
                    ClearLids();
                    RestartScene();
                }
            }
        }
        RegionSharingOutput? output = frame.Workspace.RegionSharing.Output;
        if (output is null)
        {
            Close();
            return;
        }
        _source = null;
        DisplayInfo? display = null;
        foreach ((OverlayWindow overlay, IOverlayCanvas canvas) in canvases)
        {
            if (overlay.Display.DeviceName == output.Display.Id.Value)
            {
                display = overlay.Display;
                _source = canvas as DrawingCanvas;
            }
        }
        if (_window is null || output.Identity != _identity)
        {
            Close();
            if (!Open(output, display))
            {
                return;
            }
        }
        Apply(output, display);
    }

    /// <summary>A canvas drew a change; the shared view follows its source canvas at once.</summary>
    public void CanvasRendered(DrawingCanvas canvas)
    {
        if (ReferenceEquals(canvas, _source) && _output is { Capturing: true })
        {
            Refresh();
        }
    }

    /// <summary>A canvas went away with its overlay; the next frame finds the new one.</summary>
    public void CanvasRemoved(DrawingCanvas canvas)
    {
        if (ReferenceEquals(canvas, _source))
        {
            _source = null;
        }
    }

    /// <summary>Whether the output can copy the overlays' motion now: it shows a live region of a canvas's display.</summary>
    private bool Mirroring => _motion is not null && _presenter is not null && _output is { Capturing: true } && _display is not null && _source is not null
                              && !_failed;

    /// <summary>
    /// Mac beginWorkspaceAnimation with the shared images: the drawings and boards resting in the region play the overlays'
    /// plan here, cut to the region and without what covers and pixel effects hide, which stay live and are drawn again
    /// above the copies. A copy that cannot be made stops sharing with the reason (Mac workspaceAnimationFailed).
    /// </summary>
    void IVisibilityMirror.Play(ulong serial, VisibilityPlan plan, IReadOnlyList<SharedMotionSnapshot> snapshots, double started, double now)
    {
        if (_motion is not { } motion)
        {
            return;
        }
        ClearLids();
        _handOffAt = long.MaxValue;
        _holdingToolbar = false;
        bool pending = _coverPending;
        _coverPending = false;
        if (!Mirroring)
        {
            motion.Cancel();
            if (pending)
            {
                RestartScene();
            }
            return;
        }
        SharedMotionArea area;
        try
        {
            DisplayInfo display = _display!;
            PixelRect region = SharedRegionPresenter.RegionPixels(_output!.Region.Bounds, display.Scale, display.Bounds.Width, display.Bounds.Height);
            area = new SharedMotionArea(display.DeviceName, region, ProtectionPixels(viewport: null));
        }
        catch (ArgumentException)
        {
            // The region no longer fits its display; the region's own check stops sharing.
            motion.Cancel();
            if (pending)
            {
                RestartScene();
            }
            return;
        }
        try
        {
            motion.Play(plan, area, snapshots, started, now);
        }
        catch (SharedMotionFailure failure)
        {
            Fail(failure, failure.Message, capture: false);
            return;
        }
        _motionDocument = _source!.SharedContent().Source;
        _motionBoard = _source.BoardColor;
        CoverStart();
        ShowLids();
        UpdateToolbar();
    }

    /// <summary>
    /// Mac beginWorkspaceAnimation: "Updating the shared view..." covers the output from the start of every show and hide
    /// until a frame of the region under the motion is on screen; the copies play under it from the start. The scene shown
    /// gets a new version so that frame comes even when nothing else changes.
    /// </summary>
    private void CoverStart()
    {
        if (_output is not { Capturing: true } output || _failed || !_hasContent || _presenter is not { } presenter)
        {
            return;
        }
        Cover(RegionSharingText.UpdatingCover, counted: false);
        _report(new OverlayEvent.SharingWaiting(output.Generation));
        _reportedLive = false;
        if (_scene is { } scene)
        {
            _scene = scene with { Version = ++_version };
            presenter.Show(_scene);
            _uncoverAt = _version;
        }
        else
        {
            _uncoverAt = _version + 1;
        }
    }

    /// <summary>The toolbar's snapshot plays in the output too when it rests in the region (Mac sharedToolbarImage).</summary>
    void IVisibilityMirror.PlayToolbar(ToolbarPlan toolbar, ToolbarImage image, string display, PixelRect pixels, bool blurred, bool particles,
                                       double started, double shift, double now)
    {
        if (_motion is not { IsActive: true } motion || !Mirroring || display != _display!.DeviceName || image.Pixels is not { } bgra)
        {
            return;
        }
        try
        {
            motion.PlayToolbar(toolbar, bgra, image.Width, image.Height, pixels, blurred, particles, started, shift, now);
        }
        catch (SharedMotionFailure failure)
        {
            Fail(failure, failure.Message, capture: false);
            return;
        }
        UpdateToolbar();
    }

    /// <summary>The real toolbar plays its own motion, so the output shows it live (the Mac plays a copy of it).</summary>
    void IVisibilityMirror.UseRealToolbar()
    {
        if (_motion is { IsActive: true } motion)
        {
            motion.UseRealToolbar();
            UpdateToolbar();
        }
    }

    bool IVisibilityMirror.Frame(double now, double started)
    {
        if (_motion is not { IsActive: true } motion)
        {
            return false;
        }
        try
        {
            return motion.Frame(now, started);
        }
        catch (SharedMotionFailure failure)
        {
            Fail(failure, failure.Message, capture: false);
            return false;
        }
    }

    /// <summary>
    /// Mac finishWorkspaceAnimation and cancelWorkspaceAnimation: a motion that ran to its end holds its last pose until the
    /// scene after it is on screen; a cancelled one goes at once, and the region starts again under the cover.
    /// </summary>
    void IVisibilityMirror.End(bool completed)
    {
        if (_motion is not { IsActive: true } motion)
        {
            return;
        }
        if (completed)
        {
            motion.Finish();
            return;
        }
        motion.Cancel();
        ClearLids();
        RestartScene();
    }

    public void Dispose() => Close();

    private bool Open(RegionSharingOutput output, DisplayInfo? display)
    {
        if (display is null)
        {
            // The display went away before the window opened; the app stops sharing with the display change.
            _report(new OverlayEvent.SharingFailed(output.Generation, RegionSharingText.CaptureDisplayUnavailable, Capture: true));
            return false;
        }
        try
        {
            _window = new SharedRegionWindow(_registry, _compositor, _title);
            _identity = output.Identity;
            _cover = _compositor.CreateSpriteVisual();
            _cover.RelativeSizeAdjustment = Vector2.One;
            _cover.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            _pointerLayer = _compositor.CreateContainerVisual();
            _pointerLayer.IsVisible = false;
            _coverLayer = _compositor.CreateContainerVisual();
            _window.Root.Children.InsertAtTop(_pointerLayer);
            // Spotlight and Cursor halo go under the arrow, over the region and its toolbar (Mac SharedRegionPointerView).
            _effectsRoot = _compositor.CreateContainerVisual();
            _window.Root.Children.InsertBelow(_effectsRoot, _pointerLayer);
            // The show and hide motion goes above the region and its toolbar, with the covers and effects drawn again above
            // it, below the pointer's layers (Mac SharedWorkspaceTransition, added above the pixels).
            _motionRoot = _compositor.CreateContainerVisual();
            _window.Root.Children.InsertBelow(_motionRoot, _effectsRoot);
            _lidRoot = _compositor.CreateContainerVisual();
            _lidRoot.RelativeSizeAdjustment = Vector2.One;
            _window.Root.Children.InsertBelow(_lidRoot, _effectsRoot);
            _window.Root.Children.InsertAtTop(_cover);
            _window.Root.Children.InsertAtTop(_coverLayer);
            _coverText = new OverlayHud(_compositor, _coverLayer, _devices, _colors(), _window.Scale);
            _pointer = new OverlayHud(_compositor, _pointerLayer, _devices, _colors(), PointerResolution);
            _pointer.Show(Arrow, ArrowArea);
            _pointer.SetVisible(true, animate: false, reduceMotion: true);
            _window.CloseRequested += OnCloseRequested;
            _window.Resized += Layout;
            _window.Place(display);
            _accessibility = new OverlayAccessibility(_window.ContentHandle, _window.ClientScreenRect(), Tree());
            _window.Accessibility = _accessibility;
            _window.ShowBehind();
            _generationSeen = false;
            _hasContent = false;
            Layout();
            return true;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            Close();
            _report(new OverlayEvent.Failure("Open the shared region window", error));
            _report(new OverlayEvent.SharingFailed(output.Generation, RegionSharingText.Unavailable, Capture: false));
            return false;
        }
    }

    private void Close()
    {
        StopPresenter();
        if (_window is { } window)
        {
            window.CloseRequested -= OnCloseRequested;
            window.Resized -= Layout;
            window.Accessibility = null;
        }
        _accessibility = null;
        _coverText?.Dispose();
        _pointer?.Dispose();
        _coverText = null;
        _pointer = null;
        _cover?.Brush?.Dispose();
        _cover?.Dispose();
        _coverLayer?.Dispose();
        _pointerLayer?.Dispose();
        _effectsRoot?.Clip?.Dispose();
        _effectsRoot?.Dispose();
        _effectsRoot = null;
        _motionRoot?.Clip?.Dispose();
        _motionRoot?.Dispose();
        _motionRoot = null;
        _lidRoot?.Dispose();
        _lidRoot = null;
        _cover = null;
        _coverLayer = null;
        _pointerLayer = null;
        _window?.Dispose();
        _window = null;
        _output = null;
        _display = null;
        _coverMessage = null;
        _pointerAt = null;
        _failed = false;
    }

    private void OnCloseRequested() => _report(new OverlayEvent.SharingClosed());

    /// <summary>Mac receive: covers a new generation, keeps the cover while sharing is off and shows the region otherwise.</summary>
    private void Apply(RegionSharingOutput output, DisplayInfo? display)
    {
        bool fresh = !_generationSeen || output.Generation != _generation;
        _output = output;
        if (fresh)
        {
            _generation = output.Generation;
            _generationSeen = true;
            _failed = false;
            // Every generation starts covered with the app's words and shows the region once a frame of it is on screen.
            _hasContent = false;
            Cover(output.Cover);
            Layout();
        }
        if (!output.Capturing)
        {
            StopPresenter();
            Cover(output.Cover);
            return;
        }
        if (_failed)
        {
            return;
        }
        if (display is null || _source is null)
        {
            StopPresenter();
            _failed = true;
            _report(new OverlayEvent.SharingFailed(output.Generation, RegionSharingText.CaptureDisplayUnavailable, Capture: true));
            return;
        }
        if (_presenter is null || !ReferenceEquals(_display, display))
        {
            StopPresenter();
            _display = display;
            try
            {
                _presenter = SharedRegionPresenter.Create(new SharedRegionPresenterOptions
                {
                    Compositor = _compositor,
                    Root = _window!.Root,
                    Capture = _capture,
                    Display = display,
                    Colors = _colors(),
                });
                _presenter.Presented += OnPresented;
                _presenter.Failed += OnFailed;
                _presenter.CaptureStateChanged += OnCaptureState;
                _presenter.PointerMoved += OnPointerMoved;
                _toolbar = SharedToolbarLayer.Create(_compositor, _window.Root, _presenter.Visual);
                _toolbar.Stopped += OnToolbarStopped;
                _toolbar.Shown += OnToolbarShown;
                if (_effectsRoot is { } effectsRoot)
                {
                    // The overlays' Spotlight and Cursor halo, drawn here too in the display's pixels and scaled with the region.
                    _effects = new PointerEffectsLayer(_compositor, effectsRoot, _devices, display.Scale, display.Bounds.Width, display.Bounds.Height, _colors());
                    _pointerEffects.Attach(_effects, display.Bounds);
                }
                if (_motionRoot is { } motionRoot)
                {
                    _motion = new SharedMotion(_compositor, motionRoot, _devices, _colors(), display.Scale);
                }
                Layout();
            }
            catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
            {
                Fail(error, RegionSharingText.Unavailable, capture: false);
                return;
            }
        }
        Refresh();
        try
        {
            _presenter.Start();
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ObjectDisposedException)
        {
            Fail(error, RegionSharingText.Unavailable, capture: true);
        }
    }

    /// <summary>
    /// Mac receive: hidden drawings share nothing, drawings that show or hide with motion share only covers and effects,
    /// and visible ones share everything with the board. A change of covers, effects or visibility starts the region again:
    /// the shared motion carries a change of visibility (it covers the output as it starts, see <see cref="CoverStart"/>),
    /// and otherwise, or when a cover or effect was added, the output shows "Updating the shared view..." until a frame of
    /// the new scene is on screen. After a motion its copies hold their last pose until the scene after it is on screen.
    /// </summary>
    private void Refresh()
    {
        if (_presenter is null || _source is null || _workspace is not { } workspace || _output is not { Capturing: true } output || _display is null || _failed)
        {
            return;
        }
        SharedCanvasContent content = _source.SharedContent();
        bool visible = !workspace.WorkspaceHidden && workspace.Surfaces != SurfaceMode.Hidden;
        bool animating = workspace.Visibility is not null;
        if (animating && _motion is { IsActive: true, IsFinishing: false } motion && (content.Source != _motionDocument || _source.BoardColor != _motionBoard))
        {
            // Mac workspaceSceneChanged: the copies no longer show the drawing, so they go and the region starts again.
            _report(new OverlayEvent.Status(RegionSharingText.AnimationStopped));
            motion.Cancel();
            ClearLids();
            RestartScene();
        }
        if (_coverPending && !animating)
        {
            // The overlays' motion ended without starting here: the change shows under the cover after all.
            _coverPending = false;
            RestartScene();
        }
        // Mac receive: covers and effects stay while drawings show or hide with motion; hidden drawings share nothing.
        ElementList elements = visible || animating ? content.Elements : ElementList.Empty;
        ImmutableArray<Element> protection = Protection(elements);
        if (!_hasContent || visible != _visible || !SameProtection(protection, _protection))
        {
            if (_hasContent)
            {
                if (Added(protection, _protection) || (_motion is not { IsActive: true } && !(animating && Mirroring)))
                {
                    Cover(RegionSharingText.UpdatingCover);
                }
                else if (_motion is not { IsActive: true })
                {
                    // The overlays wait for the toolbar's snapshot before they show with motion; the region's copy of that
                    // motion carries this change once it starts, or the cover comes then.
                    _coverPending = true;
                }
                _report(new OverlayEvent.SharingWaiting(output.Generation));
            }
            _hasContent = true;
            _visible = visible;
            _protection = protection;
            _uncoverAt = _version + 1;
            _reportedLive = false;
            _scene = null;
        }
        try
        {
            Rect region = output.Region.Bounds;
            Rect? zoomed = SharedRegionPresenter.ZoomedSource(output.Region, _source.ZoomView, _display.Scale, _display.Bounds.Width, _display.Bounds.Height);
            EffectList effects = EffectList.From(elements, _display.Scale, _display.Bounds.Width, _display.Bounds.Height, privacyRegion: zoomed ?? region);
            ImmutableArray<DisplayList> drawings = visible && !animating ? content.Drawings : [];
            RGBA? background = visible && !animating ? _source.BoardColor : null;
            if (_scene is not { } shown || shown.Region != region || shown.Source != zoomed || !shown.Effects.Equals(effects) || shown.Drawings != drawings
                || shown.Background != background)
            {
                _scene = new SharedRegionScene(region, effects, drawings, background, ++_version, zoomed);
                _presenter.Show(_scene);
            }
        }
        catch (ArgumentException error)
        {
            Fail(error, RegionSharingText.RegionInvalid, capture: false);
            return;
        }
        if (_motion is { IsFinishing: true } && _handOffAt == long.MaxValue)
        {
            // Mac completingWorkspaceTransition: the copies hold until the scene after the motion is on screen.
            _handOffAt = _scene.Version;
            if (_presented >= _handOffAt)
            {
                HandOff();
            }
        }
        UpdateToolbar();
    }

    /// <summary>
    /// Mac cancelWorkspaceAnimation and a lost device: covers the output until a frame of a new scene is on screen, and the
    /// region starts again (within the readiness wait).
    /// </summary>
    private void RestartScene()
    {
        if (_output is not { Capturing: true } output || _failed || !_hasContent)
        {
            return;
        }
        Cover(RegionSharingText.UpdatingCover);
        _report(new OverlayEvent.SharingWaiting(output.Generation));
        _uncoverAt = _version + 1;
        _reportedLive = false;
        _scene = null;
    }

    /// <summary>
    /// The scene after the motion is on screen (Mac presented with completingWorkspaceTransition): the copies go. The live
    /// toolbar starts again where its copy rests, so that copy stays until the toolbar's first frame, as the Mac waits for
    /// SharedToolbarCapture's frame.
    /// </summary>
    private void HandOff()
    {
        _handOffAt = long.MaxValue;
        if (_motion is not { IsActive: true } motion)
        {
            return;
        }
        _handOffs++;
        bool keepToolbar = motion.HasToolbar && _toolbar is { IsRunning: true, IsShown: false };
        motion.HandOff(keepToolbar);
        ClearLids();
        _holdingToolbar = keepToolbar;
    }

    private void ReleaseHeldToolbar()
    {
        if (_holdingToolbar)
        {
            _holdingToolbar = false;
            _motion?.ReleaseToolbar();
        }
    }

    /// <summary>
    /// Mac toolbar?.update(enabled: visible && !animating): the toolbar shows in the output while it is over the region and
    /// the drawings are visible and still. While they show or hide with motion, the live toolbar stays until a copy of its
    /// snapshot takes over: a hide shows it until its snapshot moves, and the real toolbar's own motion (Fade, or no
    /// snapshot) stays live, where the Mac plays a copy of the toolbar with the same motion.
    /// </summary>
    private void UpdateToolbar()
    {
        if (_toolbar is null)
        {
            return;
        }
        if (_output is not { Capturing: true } output || _display is not { } display || _workspace is not { } workspace || _devices is not { } devices
            || ToolbarWindow == 0 || _toolbarFrame is not { } frame || _failed || !Windows.Win32.PInvoke.IsWindowVisible(new Windows.Win32.Foundation.HWND(ToolbarWindow)))
        {
            HideToolbar();
            return;
        }
        bool still = !workspace.WorkspaceHidden && workspace.Surfaces != SurfaceMode.Hidden && workspace.Visibility is null;
        bool moving = workspace.Visibility is { } transition && (_motion?.RealToolbar == true || (!transition.Showing && _motion?.HasToolbar != true));
        if (!still && !moving)
        {
            HideToolbar();
            return;
        }
        PixelRect privacy;
        try
        {
            PixelRect local = EffectList.PrivacyRect(output.Region.Bounds, display.Scale, display.Bounds.Width, display.Bounds.Height);
            privacy = new PixelRect(local.Left + display.Bounds.Left, local.Top + display.Bounds.Top, local.Right + display.Bounds.Left, local.Bottom + display.Bounds.Top);
        }
        catch (ArgumentException)
        {
            HideToolbar();
            return;
        }
        var toolbar = PixelRect.FromSize(frame.X, frame.Y, frame.Width, frame.Height);
        if (toolbar.Right <= privacy.Left || toolbar.Left >= privacy.Right || toolbar.Bottom <= privacy.Top || toolbar.Top >= privacy.Bottom)
        {
            HideToolbar();
            return;
        }
        try
        {
            _toolbar.Show(ToolbarWindow, devices, toolbar, privacy, _fit);
        }
        catch (NativeCallException error)
        {
            // The region still shows; only its toolbar is missing (Mac: the toolbar capture failed).
            HideToolbar();
            _report(new OverlayEvent.Failure("Show the toolbar in the shared region", error));
        }
    }

    private void HideToolbar()
    {
        _toolbar?.Hide();
        ReleaseHeldToolbar();
    }

    private void OnToolbarStopped()
    {
        _toolbarFrame = null;
        ReleaseHeldToolbar();
    }

    private void OnToolbarShown() => ReleaseHeldToolbar();

    /// <summary>Mac addedProtection: a cover or effect is in <paramref name="next"/> that <paramref name="shown"/> did not have.</summary>
    private static bool Added(ImmutableArray<Element> next, ImmutableArray<Element> shown)
    {
        foreach (Element element in next)
        {
            bool known = false;
            foreach (Element old in shown)
            {
                if (old.Equals(element))
                {
                    known = true;
                    break;
                }
            }
            if (!known)
            {
                return true;
            }
        }
        return false;
    }

    private static ImmutableArray<Element> Protection(ElementList elements)
    {
        ImmutableArray<Element>.Builder protection = ImmutableArray.CreateBuilder<Element>();
        foreach (ref readonly Element element in elements)
        {
            if (element.Tool.IsProtection)
            {
                protection.Add(element);
            }
        }
        return protection.DrainToImmutable();
    }

    private static bool SameProtection(ImmutableArray<Element> next, ImmutableArray<Element> shown)
    {
        if (next.Length != shown.Length)
        {
            return false;
        }
        for (int index = 0; index < next.Length; index++)
        {
            if (!next[index].Equals(shown[index]))
            {
                return false;
            }
        }
        return true;
    }

    private void OnPresented(long version)
    {
        if (_output is not { Capturing: true } output || _failed)
        {
            return;
        }
        _presented = Math.Max(_presented, version);
        if (version >= _handOffAt)
        {
            HandOff();
        }
        if (version < _uncoverAt)
        {
            return;
        }
        if (_coverMessage is not null)
        {
            Uncover();
        }
        if (!_reportedLive)
        {
            _reportedLive = true;
            _report(new OverlayEvent.SharingPresented(output.Generation));
        }
    }

    private void OnFailed(Exception error) => Fail(error, RegionSharingText.Unavailable, capture: false);

    /// <summary>Mac fail and captureStopped: the cover stays and the app stops or fails sharing with the reason.</summary>
    private void OnCaptureState(CaptureState state)
    {
        if (_output is not { Capturing: true } output || _failed)
        {
            return;
        }
        switch (state)
        {
            case CaptureState.Suspended { Reason: CaptureSuspension.SessionLocked }:
                Stop(output, RegionSharingText.DesktopLocked, failed: true);
                break;
            case CaptureState.Suspended:
                Stop(output, RegionSharingText.SystemPaused, failed: false);
                break;
            case CaptureState.Failed { Failure.Kind: CaptureFailureKind.StreamClosed }:
                Stop(output, RegionSharingText.CaptureStopped, failed: false);
                break;
            case CaptureState.Failed failed:
                Stop(output, CaptureMessage(failed.Failure), failed: true);
                break;
        }
    }

    private static string CaptureMessage(CaptureFailure failure) => failure.Kind switch
    {
        CaptureFailureKind.DisplayUnavailable or CaptureFailureKind.DisplayChanged => RegionSharingText.CaptureDisplayUnavailable,
        CaptureFailureKind.NoFrames => RegionSharingText.NoFrame,
        CaptureFailureKind.AccessNeeded => Workspace.Capture.ScreenAccessText.NotAsked(Workspace.Capture.ScreenAccessFeature.RegionSharing),
        CaptureFailureKind.BorderRequired or CaptureFailureKind.AccessDenied =>
            Workspace.Capture.ScreenAccessText.Refused(Overlay.EffectsMessages.Refusal(failure.Kind), Workspace.Capture.ScreenAccessFeature.RegionSharing),
        CaptureFailureKind.ExclusionFailed =>
            "Annotator could not keep its own windows out of the shared view. Stop sharing, then start it again.",
        _ => RegionSharingText.Unavailable,
    };

    private void Stop(RegionSharingOutput output, string message, bool failed)
    {
        _failed = true;
        StopPresenter();
        Cover(message);
        _report(failed ? new OverlayEvent.SharingFailed(output.Generation, message, Capture: true) : new OverlayEvent.SharingStopped(output.Generation, message));
    }

    private void Fail(Exception error, string message, bool capture)
    {
        _failed = true;
        StopPresenter();
        Cover(message);
        _report(new OverlayEvent.Failure("Show the shared region", error));
        if (_output is { } output)
        {
            _report(new OverlayEvent.SharingFailed(output.Generation, message, capture));
        }
    }

    private void StopPresenter()
    {
        if (_motion is { } motion)
        {
            // Mac cover: whatever the motion copied goes with the region.
            (int transitions, int copies, int particleFrames) = motion.Statistics;
            _motionTransitions += transitions;
            _motionCopies += copies;
            _motionParticleFrames += particleFrames;
            motion.Dispose();
            _motion = null;
        }
        ClearLids();
        _handOffAt = long.MaxValue;
        _holdingToolbar = false;
        _coverPending = false;
        _presented = -1;
        _bounds = null;
        if (_toolbar is { } toolbar)
        {
            toolbar.Stopped -= OnToolbarStopped;
            toolbar.Shown -= OnToolbarShown;
            toolbar.Dispose();
            _toolbar = null;
        }
        if (_effects is { } effects)
        {
            _pointerEffects.Detach(effects);
            effects.Dispose();
            _effects = null;
        }
        if (_presenter is { } presenter)
        {
            presenter.Presented -= OnPresented;
            presenter.Failed -= OnFailed;
            presenter.CaptureStateChanged -= OnCaptureState;
            presenter.PointerMoved -= OnPointerMoved;
            presenter.Dispose();
            _presenter = null;
        }
        _display = null;
        _hasContent = false;
        _uncoverAt = long.MaxValue;
        _reportedLive = false;
        _scene = null;
        if (_pointerLayer is not null)
        {
            _pointerLayer.IsVisible = false;
        }
        _pointerAt = null;
    }

    private void Cover(string message, bool counted = true)
    {
        if (_window is null || _cover is null)
        {
            return;
        }
        if (counted && !_cover.IsVisible && _motion is { IsActive: true })
        {
            _motionCovers++;
        }
        _cover.IsVisible = true;
        _coverLayer!.IsVisible = true;
        if (message != _coverMessage)
        {
            _coverMessage = message;
            ShowCoverText();
            UpdateAccessibility();
        }
    }

    private void Uncover()
    {
        if (_cover is null)
        {
            return;
        }
        _cover.IsVisible = false;
        _coverLayer!.IsVisible = false;
        _coverMessage = null;
        UpdateAccessibility();
    }

    /// <summary>The cover's words: white, centered and wrapped to at most four lines in the Mac's text box.</summary>
    private void ShowCoverText()
    {
        if (_window is null || _coverText is null || _coverMessage is null)
        {
            return;
        }
        double width = _window.ClientSize.Width / _window.Scale, height = _window.ClientSize.Height / _window.Scale;
        _coverText.Show(CoverText(_coverMessage, width, height, _measurer), new Rect(0, 0, width, height));
        _coverText.SetVisible(true, animate: false, reduceMotion: true);
    }

    /// <summary>The Mac's coverText frame: 24 points from the sides, 100 high and centered vertically.</summary>
    internal static Rect CoverBox(double width, double height) =>
        new(CoverInset, (height - CoverBoxHeight) / 2, Math.Max(1, width - 2 * CoverInset), CoverBoxHeight);

    /// <summary>
    /// The cover's words in a window of <paramref name="width"/> by <paramref name="height"/> points: white, 18 points,
    /// medium weight, centered and wrapped to at most four lines in the Mac's text box.
    /// </summary>
    internal static DisplayList CoverText(string message, double width, double height, ITextMeasurer measurer)
    {
        Rect box = CoverBox(width, height);
        string text = Wrap(message, box.Width, measurer);
        return new DisplayListBuilder()
            .Add(DisplayItem.DrawText(new TextRun(text, box, CoverFont, TextAlignment.Center, Paint.Solid(new RGBA(1, 1, 1)))))
            .ToList();
    }

    /// <summary>Breaks <paramref name="message"/> into lines no wider than <paramref name="width"/> points, at most four.</summary>
    internal static string Wrap(string message, double width, ITextMeasurer measurer)
    {
        var lines = new List<string>();
        string current = string.Empty;
        foreach (string word in message.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && measurer.Measure(candidate, CoverFont).Width > width)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
        {
            lines.Add(current);
        }
        return string.Join('\n', lines.Take(CoverLines));
    }

    /// <summary>Fits the region into the window with black bars (the Mac's aspect-fit viewport) and lays out the cover.</summary>
    private void Layout()
    {
        if (_window is null)
        {
            return;
        }
        (int width, int height) = _window.ClientSize;
        if (_output is { } output && width > 0 && height > 0)
        {
            Rect region = output.Region.Bounds;
            double aspect = region.Width / region.Height;
            double fitWidth = width, fitHeight = height;
            if (width / (double)height > aspect)
            {
                fitWidth = height * aspect;
            }
            else
            {
                fitHeight = width / aspect;
            }
            int left = (int)Math.Round((width - fitWidth) / 2), top = (int)Math.Round((height - fitHeight) / 2);
            _fit = new PixelRect(left, top, left + (int)Math.Round(fitWidth), top + (int)Math.Round(fitHeight));
            if (_presenter is { } presenter)
            {
                presenter.Visual.Offset = new Vector3(_fit.Left, _fit.Top, 0);
                presenter.Visual.Size = new Vector2(_fit.Width, _fit.Height);
            }
            PlaceEffects();
        }
        ShowCoverText();
        UpdatePointer();
        UpdateToolbar();
        if (_accessibility is not null)
        {
            try
            {
                _accessibility.Bounds = _window.ClientScreenRect();
            }
            catch (NativeCallException)
            {
                // The window is closing; its tree goes with it.
            }
        }
        UpdateAccessibility();
    }

    /// <summary>
    /// The pointer effects' layer and the motion's copies map the display's pixels onto the letterboxed region, as the
    /// presenter's image does, and show only the region (Mac SharedRegionPointerView draws in the region's points, not
    /// through zoom; Mac SharedWorkspaceTransition's bounds are the region on the screen). The covers and effects drawn again
    /// above the copies follow the region's picture.
    /// </summary>
    private void PlaceEffects()
    {
        if (_output is not { } output || _display is not { } display || _fit.IsEmpty)
        {
            return;
        }
        PixelRect region;
        try
        {
            region = SharedRegionPresenter.RegionPixels(output.Region.Bounds, display.Scale, display.Bounds.Width, display.Bounds.Height);
        }
        catch (ArgumentException)
        {
            return;
        }
        foreach (ContainerVisual? root in (ReadOnlySpan<ContainerVisual?>)[_effectsRoot, _motionRoot])
        {
            if (root is null)
            {
                continue;
            }
            float x = (float)_fit.Width / region.Width, y = (float)_fit.Height / region.Height;
            root.Size = new Vector2(display.Bounds.Width, display.Bounds.Height);
            root.Scale = new Vector3(x, y, 1);
            root.Offset = new Vector3(_fit.Left - region.Left * x, _fit.Top - region.Top * y, 0);
            if (root.Clip is not InsetClip clip)
            {
                clip = _compositor.CreateInsetClip();
                root.Clip = clip;
            }
            clip.LeftInset = region.Left;
            clip.TopInset = region.Top;
            clip.RightInset = display.Bounds.Width - region.Right;
            clip.BottomInset = display.Bounds.Height - region.Bottom;
        }
        if (_lids.Count > 0)
        {
            ShowLids();
        }
    }

    /// <summary>
    /// Mac SharedWorkspaceTransition's protection mask: the moving copies pass behind covers and pixel effects. Above the
    /// copies, a view of the region's picture is drawn again where each cover and effect can paint, so the output shows the
    /// live protection there and never a copy over it.
    /// </summary>
    private void ShowLids()
    {
        ClearLids();
        if (_presenter is not { } presenter || _lidRoot is not { } root || _motion is not { IsActive: true, Area: { } area } || _fit.IsEmpty)
        {
            return;
        }
        // Under screen zoom the region's picture shows the protection where zoom puts it on the screen.
        ImmutableArray<PixelRect> protection = _source?.ZoomView is { } zoom ? ProtectionPixels(zoom) : area.Protection;
        foreach (PixelRect rect in protection)
        {
            if (SharedMotion.LidInsets(rect, area.Region, _fit) is not { } insets)
            {
                continue;
            }
            SpriteVisual lid = presenter.AddView();
            lid.Offset = new Vector3(_fit.Left, _fit.Top, 0);
            lid.Size = new Vector2(_fit.Width, _fit.Height);
            lid.Clip = _compositor.CreateInsetClip(insets.Left, insets.Top, insets.Right, insets.Bottom);
            root.Children.InsertAtTop(lid);
            _lids.Add(lid);
        }
    }

    private void ClearLids()
    {
        foreach (SpriteVisual lid in _lids)
        {
            _presenter?.RemoveView(lid);
            _lidRoot?.Children.Remove(lid);
            lid.Clip?.Dispose();
            lid.Dispose();
        }
        _lids.Clear();
    }

    /// <summary>
    /// The area each cover and pixel effect of the shared canvas can paint, rounded out to whole pixels of its display (Mac
    /// workspaceProtectionFrames); through screen zoom's <paramref name="viewport"/> when given.
    /// </summary>
    private ImmutableArray<PixelRect> ProtectionPixels(Viewport? viewport)
    {
        ImmutableArray<PixelRect>.Builder rects = ImmutableArray.CreateBuilder<PixelRect>();
        if (_source is not { } source || _display is not { } display)
        {
            return rects.DrainToImmutable();
        }
        _bounds ??= new SceneBuilder(_measurer);
        double scale = display.Scale;
        foreach (ref readonly Element element in source.SharedContent().Elements)
        {
            if (!element.Tool.IsProtection)
            {
                continue;
            }
            Rect bounds = _bounds.DrawingBounds(element);
            if (viewport is { } view)
            {
                Point from = view.OutputPoint(new Point(bounds.X, bounds.Y)), to = view.OutputPoint(new Point(bounds.MaxX, bounds.MaxY));
                bounds = new Rect(from.X, from.Y, to.X - from.X, to.Y - from.Y);
            }
            rects.Add(new PixelRect((int)Math.Floor(bounds.X * scale), (int)Math.Floor(bounds.Y * scale),
                                    (int)Math.Ceiling(bounds.MaxX * scale), (int)Math.Ceiling(bounds.MaxY * scale)));
        }
        return rects.DrainToImmutable();
    }

    private void OnPointerMoved(int x, int y)
    {
        _pointerAt = (x, y);
        UpdatePointer();
    }

    /// <summary>
    /// Development motion runs only: a replayed take's scripted pointer in desktop pixels, as the Mac's ReelPointerHook moves
    /// the shared view's arrow. The real pointer takes over again when it moves.
    /// </summary>
    internal void ScriptPointer(int x, int y) => OnPointerMoved(x, y);

    /// <summary>
    /// Mac SharedRegionPointerView: the arrow where the pointer is while it is inside the region, scaled with the region
    /// into the window. The capture leaves the system pointer out, so the shared view draws its own.
    /// </summary>
    private void UpdatePointer()
    {
        if (_pointerLayer is null || _output is not { } output || _display is not { } display || _pointerAt is not { } at || _fit.IsEmpty)
        {
            if (_pointerLayer is not null)
            {
                _pointerLayer.IsVisible = false;
            }
            return;
        }
        Rect region = output.Region.Bounds;
        double x = (at.X - display.Bounds.Left) / display.Scale, y = (at.Y - display.Bounds.Top) / display.Scale;
        bool inside = x >= region.X && y >= region.Y && x < region.MaxX && y < region.MaxY;
        _pointerLayer.IsVisible = inside && _coverMessage is null;
        if (!inside)
        {
            return;
        }
        double perPoint = _fit.Width / region.Width;
        _pointerLayer.Offset = new Vector3((float)(_fit.Left + (x - region.X) * perPoint), (float)(_fit.Top + (y - region.Y) * perPoint), 0);
        float scale = (float)(perPoint / PointerResolution);
        _pointerLayer.Scale = new Vector3(scale, scale, 1);
    }

    /// <summary>The arrow pointer in points with its tip at the origin: white with a black outline, as Windows draws it.</summary>
    internal static DisplayList Arrow { get; } = CreateArrow();

    private static Rect ArrowArea => new(-1, -1, 14, 21);

    private static DisplayList CreateArrow()
    {
        PathData outline = new PathBuilder(0, 0)
            .MoveTo(new Point(0.5, 0.5))
            .LineTo(new Point(0.5, 16.5))
            .LineTo(new Point(4.5, 12.5))
            .LineTo(new Point(7.2, 18.5))
            .LineTo(new Point(9.4, 17.5))
            .LineTo(new Point(6.8, 11.6))
            .LineTo(new Point(11.8, 11.6))
            .Close()
            .ToPathData();
        return new DisplayListBuilder()
            .Add(DisplayItem.FillPath(outline, FillRule.NonZero, Paint.Solid(new RGBA(1, 1, 1))))
            .Add(DisplayItem.StrokePath(outline, new StrokeStyle(1), Paint.Solid(new RGBA(0, 0, 0))))
            .ToList();
    }

    /// <summary>
    /// The Mac's accessibility: an image "Shared region. View only." whose value is the cover's words with the words as
    /// text inside it while covered, and the live value once the region shows.
    /// </summary>
    private OverlayAccessibleTree Tree()
    {
        (int width, int height) = _window?.ClientSize ?? (0, 0);
        return CoverTree(_coverMessage, _coverMessage ?? (_output is { Capturing: true } ? RegionSharingText.LiveValue : null), width, height,
            _window?.Scale ?? 1);
    }

    /// <summary>The output's accessibility for a client area of <paramref name="width"/> by <paramref name="height"/> pixels.</summary>
    internal static OverlayAccessibleTree CoverTree(string? cover, string? value, int width, int height, double scale)
    {
        var root = new OverlayAccessibleNode(OverlayControlType.Image, RegionSharingText.OutputLabel, PixelRect.FromSize(0, 0, width, height))
        {
            Value = value,
        };
        if (cover is not { } message)
        {
            return new OverlayAccessibleTree(root, []);
        }
        Rect box = CoverBox(width / scale, height / scale);
        // AppKit reports a label's frame one point outside its text box on the sides and the height.
        var frame = PixelRect.FromSize((int)Math.Round((box.X + 1) * scale), (int)Math.Round((box.Y - 1) * scale),
                                       (int)Math.Round((box.Width - 2) * scale), (int)Math.Round((box.Height + 2) * scale));
        return new OverlayAccessibleTree(root, [new OverlayAccessibleNode(OverlayControlType.Text, message, frame)]);
    }

    private void UpdateAccessibility()
    {
        if (_accessibility is not null)
        {
            _accessibility.Tree = Tree();
        }
    }
}
