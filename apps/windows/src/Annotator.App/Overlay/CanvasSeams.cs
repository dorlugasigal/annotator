using Annotator.Platform.Graphics;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Workspace;
using Windows.UI.Composition;

namespace Annotator.App.Overlay;

/// <summary>
/// What the app thread sends the overlay thread: an immutable workspace snapshot plus host state.
/// <paramref name="InputBlocked"/> is true while a flyout, menu or palette is open: the click that dismisses
/// it must not draw (Mac popovers consume that click).
/// </summary>
public sealed record OverlayFrame(WorkspaceSnapshot Workspace, bool InputBlocked)
{
    /// <summary>The toolbar as the visibility motion needs it; null before the toolbar has a place.</summary>
    public ToolbarState? Toolbar { get; init; }
}

/// <summary>The toolbar window's rectangle in desktop pixels and its snapshot for the current show or hide.</summary>
public sealed record ToolbarState(Windows.Graphics.RectInt32 Frame, ToolbarImage? Image);

/// <summary>
/// A snapshot of the toolbar for a run of shows and hides that started with transition <paramref name="Chain"/>:
/// premultiplied BGRA rows of <paramref name="Width"/> by <paramref name="Height"/> pixels, or null
/// <paramref name="Pixels"/> when it could not be taken (the toolbar then fades itself).
/// </summary>
public sealed record ToolbarImage(ulong Chain, byte[]? Pixels, int Width, int Height);

/// <summary>
/// Optional for an <see cref="IOverlayCanvasFactory"/>: sees each frame after every canvas presented it, in the same
/// work item, so its visuals change in the same composition commit (the visibility motion). On the overlay thread.
/// </summary>
public interface IOverlayFrameObserver
{
    void Presented(OverlayFrame frame, IReadOnlyDictionary<OverlayWindow, IOverlayCanvas> canvases, GraphicsDevices? devices);

    /// <summary>The overlays are about to be destroyed (display change or stop); release visuals under their roots.</summary>
    void OverlaysChanging();
}

/// <summary>One display's part of an <see cref="OverlayFrame"/>; <see cref="Display"/> is null until the workspace prepares that display.</summary>
public sealed record CanvasFrame(OverlayFrame Frame, DisplaySnapshot? Display)
{
    public WorkspaceSnapshot Workspace => Frame.Workspace;
}

/// <summary>Everything a canvas needs on the overlay thread. Valid until the canvas is disposed.</summary>
/// <param name="Devices">The shared Direct3D, Direct2D and DirectWrite devices, or null while they are unavailable.</param>
public sealed record OverlayCanvasContext(
    OverlayWindow Overlay,
    DisplayDescriptor Display,
    Compositor Compositor,
    GraphicsDevices? Devices,
    IWorkspaceChannel Channel);

/// <summary>
/// Shows one display's canvas. Lives on the overlay thread. Called only when something changed; draw under
/// <see cref="OverlayWindow.Root"/> and present only then (no idle work, WIN-SYS-07).
/// </summary>
public interface ICanvasPresenter
{
    void Present(CanvasFrame frame);

    /// <summary>The device bundle was lost and replaced (or is unavailable when null); recreate device resources.</summary>
    void DevicesChanged(GraphicsDevices? devices);
}

/// <summary>
/// One display's canvas input, on the overlay thread, in message order. Positions are physical pixels from
/// the display's top-left corner; divide by <see cref="DisplayDescriptor.Scale"/> for points. Never block.
/// </summary>
public interface ICanvasInput
{
    void OnPointer(in PointerInput input);

    /// <summary>Returns true when the key was handled.</summary>
    bool OnKey(in KeyInput input);

    void OnFocusChanged(bool hasFocus);
}

/// <summary>A display's canvas: presenter and input together. Disposed when its overlay is destroyed.</summary>
public interface IOverlayCanvas : ICanvasPresenter, ICanvasInput, IDisposable
{
}

/// <summary>
/// Creates a canvas for each overlay, on the overlay thread (<see cref="DrawingCanvasFactory"/>). A factory that is
/// <see cref="IDisposable"/> is disposed there when the overlay host stops.
/// </summary>
public interface IOverlayCanvasFactory
{
    IOverlayCanvas Create(OverlayCanvasContext context);
}
