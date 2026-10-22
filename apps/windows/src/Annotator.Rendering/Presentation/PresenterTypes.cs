using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Windows.UI.Composition;

namespace Annotator.Rendering.Presentation;

/// <summary>Everything an <see cref="OverlayPresenter"/> needs from its host, on the overlay thread.</summary>
public sealed record OverlayPresenterOptions
{
    /// <summary>The compositor that owns <see cref="Root"/> (the overlay window's compositor).</summary>
    public required Compositor Compositor { get; init; }

    /// <summary>The overlay's root visual (<c>OverlayWindow.Root</c>); the presenter adds its visuals on top.</summary>
    public required ContainerVisual Root { get; init; }

    /// <summary>
    /// The shared device bundle, or null while there is none (creating it failed). Without devices the presenter is
    /// <see cref="PresenterState.DevicesUnavailable"/> and shows only opaque covers until
    /// <see cref="OverlayPresenter.ReplaceDevices"/> brings a bundle.
    /// </summary>
    public GraphicsDevices? Devices { get; init; }

    /// <summary>Device pixels per point, the display scale (1.25 at 125%).</summary>
    public required double Scale { get; init; }

    /// <summary>The overlay's size in device pixels.</summary>
    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    public ISystemColorResolver? Colors { get; init; }

    public RendererOptions? Renderer { get; init; }

    /// <summary>
    /// The committed scene's layer. The default, a virtual drawing surface, keeps unchanged pixels, allocates memory
    /// only for tiles that hold content and commits together with other composition changes (see the README's
    /// Renderer notes for the measurements).
    /// </summary>
    public LayerKind SceneLayer { get; init; } = LayerKind.VirtualSurface;

    /// <summary>The live gesture's layer (see the README's Renderer notes for the measurements behind the default).</summary>
    public LayerKind LiveLayer { get; init; } = LayerKind.VirtualSurface;

    /// <summary>
    /// How long a swap-chain layer may wait for a free frame before an update reports
    /// <see cref="OverlayPresenter.FrameStalled"/>. Frames stop when the display is off or the session is locked.
    /// </summary>
    public TimeSpan FrameStallThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Test hook: builds layers instead of composition surfaces (the second argument is true for the live layer).</summary>
    internal Func<OverlayPresenter, bool, PresentationLayer>? LayerFactory { get; init; }
}

/// <summary>The presenter's state. Outside <see cref="Ready"/> it shows opaque covers only (fail closed).</summary>
public abstract record PresenterState
{
    private PresenterState()
    {
    }

    /// <summary>Drawing works.</summary>
    public sealed record Ready : PresenterState;

    /// <summary>
    /// There is no device bundle (the host could not create one). Covers stay opaque through composition color visuals;
    /// <see cref="OverlayPresenter.ReplaceDevices"/> with a bundle draws everything.
    /// </summary>
    public sealed record DevicesUnavailable : PresenterState;

    /// <summary>
    /// The graphics devices were lost. Covers stay opaque through composition color visuals; call
    /// <see cref="OverlayPresenter.ReplaceDevices"/> with a new bundle to draw again.
    /// </summary>
    public sealed record DevicesLost(DeviceState.Lost Loss) : PresenterState;

    /// <summary>
    /// Drawing failed for another reason, such as running out of memory or a missing font. Covers stay opaque;
    /// replacing the devices or calling <see cref="OverlayPresenter.Retry"/> tries again.
    /// </summary>
    public sealed record Failed(Exception Error) : PresenterState;

    public sealed record Disposed : PresenterState;
}

public sealed class PresenterStateChangedEventArgs(PresenterState previous, PresenterState current) : EventArgs
{
    public PresenterState Previous { get; } = previous;

    public PresenterState Current { get; } = current;
}

/// <summary>
/// A swap-chain layer had no free frame for longer than <see cref="OverlayPresenterOptions.FrameStallThreshold"/>:
/// the display stopped taking frames (display off, session locked or a hung GPU). The newest content stays pending and
/// is drawn as soon as a frame frees; nothing waits or polls meanwhile.
/// </summary>
public sealed class FrameStallEventArgs(bool live, TimeSpan waited) : EventArgs
{
    /// <summary>True for the live layer, false for the scene layer.</summary>
    public bool Live { get; } = live;

    /// <summary>How long the oldest pending update has waited so far.</summary>
    public TimeSpan Waited { get; } = waited;
}

/// <summary>Frame counters and the last frame's cost, for measurements and tests.</summary>
public sealed class PresenterStatistics
{
    public long SceneFrames { get; internal set; }

    public long LiveFrames { get; internal set; }

    /// <summary>Updates folded into a later frame because a swap-chain layer had no free frame yet.</summary>
    public long DeferredFrames { get; internal set; }

    /// <summary>Times an update found a swap-chain layer without a free frame for longer than the stall threshold.</summary>
    public long FrameStalls { get; internal set; }

    public long FullRedraws { get; internal set; }

    /// <summary>Redraws of the lists that paint with system colors after a theme or accent change.</summary>
    public long ThemeRedraws { get; internal set; }

    /// <summary>Redraws because screen zoom's view moved or changed its zoom.</summary>
    public long ViewRedraws { get; internal set; }

    /// <summary>Pixels that changed in the last scene and live frames (a swap chain also redraws the frame before).</summary>
    public long LastScenePixels { get; internal set; }

    public long LastLivePixels { get; internal set; }

    /// <summary>CPU time of the last frame, from the first BeginDraw to the last EndDraw or Present.</summary>
    public TimeSpan LastSceneFrameTime { get; internal set; }

    public TimeSpan LastLiveFrameTime { get; internal set; }
}
