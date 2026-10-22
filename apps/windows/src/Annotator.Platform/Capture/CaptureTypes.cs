using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Windowing;

namespace Annotator.Platform.Capture;

/// <summary>
/// The state of one display's capture source (constitution IV: capture has its own typed state). Only
/// <see cref="Live"/> provides pixels. In every other state a consumer shows its effect regions opaque and never
/// shows unfiltered or stale screen pixels (WIN-CAP-03).
/// </summary>
public abstract record CaptureState
{
    private CaptureState()
    {
    }

    /// <summary>Whether frames arrive and may be shown.</summary>
    public bool ProvidesPixels => this is Live;

    /// <summary>No consumer needs this display's pixels; nothing runs.</summary>
    public sealed record Stopped : CaptureState
    {
        public static readonly Stopped Instance = new();
    }

    /// <summary>Capture is starting and no frame has arrived yet (an off-screen or slow source gives none, S-1).</summary>
    public sealed record Starting : CaptureState
    {
        public static readonly Starting Instance = new();
    }

    /// <summary>Frames arrive.</summary>
    public sealed record Live : CaptureState
    {
        public static readonly Live Instance = new();
    }

    /// <summary>
    /// No frame arrived for longer than <see cref="CaptureServiceOptions.StallThreshold"/>, for example while the
    /// display is off. Capture keeps asking for frames and returns to <see cref="Live"/> by itself.
    /// </summary>
    public sealed record Stalled(TimeSpan Waited) : CaptureState;

    /// <summary>
    /// Capture is paused after a system transition or because the app asked (WIN-OVR-09). Nothing runs until
    /// <see cref="CaptureService.Resume"/>, as on the Mac, where the person resumes live effects.
    /// </summary>
    public sealed record Suspended(CaptureSuspension Reason) : CaptureState;

    /// <summary>Capture failed. <see cref="CaptureService.Resume"/> tries again.</summary>
    public sealed record Failed(CaptureFailure Failure) : CaptureState;
}

/// <summary>Why capture is paused.</summary>
public enum CaptureSuspension
{
    /// <summary>The session was locked.</summary>
    SessionLocked,

    /// <summary>The computer is going to sleep.</summary>
    SystemSleep,

    /// <summary>The person is signing out.</summary>
    SignOut,

    /// <summary>A console or Remote Desktop connection changed (fast user switching or Remote Desktop).</summary>
    SessionChanged,

    /// <summary>The app paused capture, for example after a virtual desktop switch (WIN-OVR-06).</summary>
    RequestedByApp,
}

/// <summary>Why capture failed.</summary>
public enum CaptureFailureKind
{
    /// <summary>Windows.Graphics.Capture is not available on this system.</summary>
    Unsupported,

    /// <summary>The Magnification API could not start or create its window.</summary>
    MagnifierUnavailable,

    /// <summary>The capture graphics device could not be created.</summary>
    DevicesUnavailable,

    /// <summary>The display is no longer connected.</summary>
    DisplayUnavailable,

    /// <summary>The display's position or size changed; capture it again with the new display.</summary>
    DisplayChanged,

    /// <summary>Windows requires the yellow capture border, which must never appear (WIN-CAP-04): Screenshot borders is off for Annotator.</summary>
    BorderRequired,

    /// <summary>No frame arrived within <see cref="CaptureServiceOptions.FirstFrameTimeout"/> (the Mac readiness rule).</summary>
    NoFrames,

    /// <summary>An Annotator window could not be left out of the capture in time, so its pixels could leak.</summary>
    ExclusionFailed,

    /// <summary>Windows refused screen capture, for example through a privacy setting or policy.</summary>
    AccessDenied,

    /// <summary>
    /// A packaged Annotator that Windows has not asked yet whether it may capture the screen. Capture never asks: the app
    /// asks when the person starts a pixel feature (<see cref="CaptureAccess.RequestAsync"/>), then starts capture again.
    /// </summary>
    AccessNeeded,

    /// <summary>The capture session closed unexpectedly.</summary>
    StreamClosed,

    /// <summary>Another failure while starting or running capture; see <see cref="CaptureFailure.Error"/>.</summary>
    Other,
}

/// <summary>A capture failure: its kind, the operation that failed (for support information) and the error.</summary>
public sealed record CaptureFailure(CaptureFailureKind Kind, string Operation, Exception? Error = null);

/// <summary>
/// What a <see cref="CaptureService"/> has done since it was created, over every display, for measurements: frames that
/// arrived, frames dropped (no free slot, a stale size or a paused source), frames that changed nothing in any lease's areas,
/// GPU comparisons started, frames that counted as changed everywhere without one (the first frame of a start, or no free
/// comparison), leases told again of a frame once its comparison found a change, capture sessions opened, leases that
/// reused a paused capture instead of opening one, and the comparisons read with the total and longest time from a frame's
/// arrival to reading its comparison.
/// </summary>
public readonly record struct CaptureStatistics(long Received, long Dropped, long Unchanged, long Compared, long Uncompared, long Renotified,
                                                long SessionsStarted, long SessionsReused, long ResultsRead = 0, TimeSpan ResultDelay = default,
                                                TimeSpan ResultDelayMax = default);

/// <summary>
/// Receives frames and state changes of one <see cref="ICaptureLease"/>. Calls come from capture threads, never
/// at the same time for one lease. They must return quickly and must not wait for the thread that owns the lease.
/// </summary>
public interface ICaptureConsumer
{
    /// <summary>A newer frame can be read with <see cref="ICaptureLease.TryAcquireFrame"/>.</summary>
    void OnFrameAvailable(ICaptureLease lease);

    /// <summary>The source's state changed. Outside <see cref="CaptureState.Live"/>, show effect regions opaque.</summary>
    void OnStateChanged(ICaptureLease lease, CaptureState state);
}

/// <summary>Screen pixels of one display without Annotator's windows, shared by every consumer of that display.</summary>
public interface ICaptureLease : IDisposable
{
    /// <summary>The display this lease captures.</summary>
    DisplayInfo Display { get; }

    /// <summary>The source's current state.</summary>
    CaptureState State { get; }

    /// <summary>
    /// The newest frame while the source is <see cref="CaptureState.Live"/>. Dispose the frame as soon as its GPU work
    /// is submitted; a held frame keeps one capture buffer busy.
    /// </summary>
    bool TryAcquireFrame(out CaptureFrame frame);

    /// <summary>
    /// The areas of the frame, in its pixels (the display's, from its top-left corner), that what this lease shows depends on.
    /// With a frame comparer, a frame that changes nothing in them keeps the lease's content version, so the consumer skips it
    /// (<see cref="CaptureFrame.ContentVersion"/>). Until it is set, the whole display counts; an empty list counts nothing.
    /// Set it whenever what the consumer shows changes; the areas are copied.
    /// </summary>
    void SetInterest(ReadOnlySpan<PixelRect> areas);
}

/// <summary>A source of capture leases (<see cref="CaptureService"/>; tests use their own).</summary>
public interface IDisplayCapture
{
    /// <summary>
    /// Starts or joins the capture of <paramref name="display"/>. Capture runs while at least one lease of the display
    /// exists; dispose the lease when its pixels are no longer needed.
    /// </summary>
    ICaptureLease Acquire(DisplayInfo display, ICaptureConsumer consumer);
}

/// <summary>
/// Finds out, on the GPU and without waiting for it, which leases' areas changed between two captured frames, so consumers
/// skip frames that change nothing they show and a still screen is captured at the still rate
/// (<see cref="CaptureServiceOptions.FrameComparer"/>). The frames are compared in square tiles; each tile carries the bits
/// of the leases whose areas reach into it.
/// </summary>
public interface ICaptureFrameComparer
{
    /// <summary>The side of a tile in pixels.</summary>
    int TileSize { get; }

    /// <summary>
    /// A tracker for one source's frames of <paramref name="width"/> x <paramref name="height"/> pixels on
    /// <paramref name="devices"/>. Throws <see cref="NativeCallException"/> when the device cannot make it.
    /// </summary>
    ICaptureChangeTracker Create(GraphicsDevices devices, int width, int height);
}

/// <summary>
/// Compares one source's frames on its capture device. Used by one thread at a time; the GPU work of different trackers on
/// one device does not interleave.
/// </summary>
public interface ICaptureChangeTracker : IDisposable
{
    /// <summary>
    /// The lease bits of every tile, row by row from the top-left one (<c>ceil(width / TileSize)</c> tiles a row,
    /// <c>ceil(height / TileSize)</c> rows), for the comparisons that start from now on. Tiles without bits are not read.
    /// </summary>
    void SetTiles(ReadOnlySpan<uint> tiles);

    /// <summary>
    /// Starts comparing <paramref name="current"/> with <paramref name="previous"/> (ID3D11Texture2D* of the tracker's device,
    /// B8G8R8A8_UNORM, the tracker's size) without waiting, and has Windows set the event <paramref name="completed"/> (a
    /// HANDLE) when the GPU has done it. The frames may be released once this returns. False when no comparison can start now,
    /// for example because too many are in flight; the frame then counts as changed everywhere.
    /// </summary>
    bool TryBegin(nint previous, nint current, nint completed, out int ticket);

    /// <summary>
    /// Reads a comparison without waiting: true with the bits of the leases whose tiles differ once the GPU has done it,
    /// false while it has not.
    /// </summary>
    bool TryRead(int ticket, out uint changed);
}

/// <summary>Returns a frame taken with <see cref="ICaptureLease.TryAcquireFrame"/>.</summary>
public interface ICaptureFrameOwner
{
    void Release(int slot, long sequence);
}

/// <summary>
/// One captured frame on the GPU: the display's pixels at 1x in device pixels, top-left first, without Annotator's
/// windows. The texture belongs to <see cref="Devices"/> and stays valid until the frame is disposed; dispose it once.
/// </summary>
public readonly struct CaptureFrame : IDisposable
{
    private readonly ICaptureFrameOwner? owner;
    private readonly int slot;

    public CaptureFrame(ICaptureFrameOwner? owner, int slot, nint texture, int width, int height, long sequence, GraphicsDevices devices,
                        TimeSpan systemRelativeTime, long? contentVersion = null)
    {
        this.owner = owner;
        this.slot = slot;
        Texture = texture;
        Width = width;
        Height = height;
        Sequence = sequence;
        Devices = devices;
        SystemRelativeTime = systemRelativeTime;
        ContentVersion = contentVersion ?? sequence;
    }

    /// <summary>ID3D11Texture2D* in B8G8R8A8_UNORM, borrowed until <see cref="Dispose"/>.</summary>
    public nint Texture { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Increases with every frame of the source.</summary>
    public long Sequence { get; }

    /// <summary>
    /// The lease's version of the pixels in its areas (<see cref="ICaptureLease.SetInterest"/>): two frames that a lease hands
    /// out with the same content version show the same pixels there, so a consumer that drew one need not draw the other.
    /// With a frame comparer, a frame whose comparison has not finished gets its own version while the lease's areas were
    /// changing (it is drawn at once) and the version before it while they were still; the lease then hears of the frame
    /// again if the comparison finds a change. Without a comparer every frame has its own version.
    /// </summary>
    public long ContentVersion { get; }

    /// <summary>The capture device bundle that owns <see cref="Texture"/>; render with it.</summary>
    public GraphicsDevices Devices { get; }

    /// <summary>When Windows composed the frame (QPC time, like <see cref="System.Diagnostics.Stopwatch"/>).</summary>
    public TimeSpan SystemRelativeTime { get; }

    public void Dispose() => owner?.Release(slot, Sequence);
}

/// <summary>Settings for <see cref="CaptureService.Create"/>.</summary>
public sealed record CaptureServiceOptions
{
    /// <summary>Annotator's windows. Every registered window is left out of the capture before it first paints.</summary>
    public required WindowRegistry Registry { get; init; }

    /// <summary>Options for the capture device, which exists only while capture runs (WARP for tests).</summary>
    public GraphicsDeviceOptions? DeviceOptions { get; init; }

    /// <summary>
    /// Windows.Graphics.Capture's minimum time between frames. Its 16 ms default caps capture at about 60 frames per
    /// second; 1 ms follows the display (Gate 0 S-1).
    /// </summary>
    public TimeSpan MinUpdateInterval { get; init; } = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Frames asked for per second. Null asks on every compositor clock tick, which follows the display's refresh
    /// rate like the Mac's capture (the larger of 60 and the display's maximum).
    /// </summary>
    public double? MaximumFramesPerSecond { get; init; }

    /// <summary>
    /// Compares each frame with the one before it in the leases' areas, on the GPU without waiting
    /// (<see cref="ICaptureLease.SetInterest"/>). A frame that changes nothing in a lease's areas keeps that lease's content
    /// version, and once <see cref="StillFrames"/> frames in a row changed nothing in any lease's areas and no input arrived
    /// (the pointer stayed where it was), a source asks for at most <see cref="StillFramesPerSecond"/> frames a second until a
    /// frame changes, the pointer moves or input arrives. Null asks on every tick and gives every frame its own version.
    /// </summary>
    public ICaptureFrameComparer? FrameComparer { get; init; }

    /// <summary>How many unchanged frames in a row make a source still (with a <see cref="FrameComparer"/>).</summary>
    public int StillFrames { get; init; } = 8;

    /// <summary>
    /// Frames asked for per second while a source is still. 30 halves the capture work of a still screen on a 60 Hz display
    /// (measured: 1.3 to 1.6% of one core instead of 5.7 to 8.0% with three effect regions, 2026-10-20); a change that
    /// follows no input can show up to 1/30 s later, and input asks for the next frame at once.
    /// </summary>
    public double StillFramesPerSecond { get; init; } = 30;

    /// <summary>
    /// How long a display's capture stays open, paused, after its last lease ends, so a lease that comes back meanwhile
    /// reuses it. A paused source asks for no frames and uses no CPU or GPU time, but keeps its video memory. Windows keeps
    /// one frame-sized allocation of every capture that ends until Annotator quits (measured on Windows 11 25H2, 2026-10-20),
    /// so reusing the capture keeps that from adding up while effects, zoom or sharing are turned on and off. Zero ends
    /// capture with the last lease.
    /// </summary>
    public TimeSpan Linger { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long frames may stop before the source reports <see cref="CaptureState.Stalled"/>.</summary>
    public TimeSpan StallThreshold { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a start may wait for its first frame before it fails (the Mac's readiness timeout).</summary>
    public TimeSpan FirstFrameTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a thread that registers a window waits for the capture thread to leave it out. On timeout every source
    /// fails closed with <see cref="CaptureFailureKind.ExclusionFailed"/>.
    /// </summary>
    public TimeSpan ExclusionTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Test hook: replaces the Magnification API, Windows.Graphics.Capture and session queries.</summary>
    internal ICapturePlatform? Platform { get; init; }
}
