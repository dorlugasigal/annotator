using System.Diagnostics;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Interop;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Capture;

/// <summary>
/// Screen pixels without Annotator's windows (WIN-CAP-01, Gate 0 S-1 method A), with one source per display shared
/// by every consumer (WIN-PERF-05). Capture runs only while a lease of that display exists. After the last lease the
/// source pauses for <see cref="CaptureServiceOptions.Linger"/> (it asks for no frames and holds none) and then stops;
/// with no source the service releases its graphics device, the Magnification API, the pacer thread and its session
/// notifications, so an idle service does no work (WIN-SYS-07).
/// </summary>
/// <remarks>
/// <para>Every window in <see cref="CaptureServiceOptions.Registry"/> is left out. A newly registered window is added
/// to the exclude list before <see cref="WindowRegistry.Register"/> returns: the registering thread waits, at most
/// <see cref="CaptureServiceOptions.ExclusionTimeout"/>, for the capture thread to apply it (S-1: register before the
/// first paint). If that fails, every source fails closed.</para>
/// <para>States are typed and fail closed (<see cref="CaptureState"/>): consumers show effect regions only while a
/// source is <see cref="CaptureState.Live"/>. Locking the session, sleep, signing out and console or Remote Desktop
/// changes suspend capture (WIN-OVR-09); it stays suspended until <see cref="Resume"/>, as on the Mac. A display
/// change fails the affected sources, so the app captures the new displays with new leases. A lost device is
/// replaced and the sources start again. A paused source that a lease takes again applies the exclude list again and
/// hands out only frames taken after that; a suspension, display change, failure or lost device ends it.</para>
/// <para>Threads: a capture thread with a message loop owns the magnifier windows; the pacer thread asks for frames and
/// reads the frame comparisons that the GPU has finished (<see cref="CaptureServiceOptions.FrameComparer"/>); frames arrive
/// on Windows.Graphics.Capture worker threads. Nothing waits for the GPU. All public members are thread-safe.</para>
/// </remarks>
public sealed class CaptureService : IDisplayCapture, IDisposable
{
    private const uint ApplyExclusionsMessage = PInvoke.WM_APP + 1;
    private const uint CheckDisplaysMessage = PInvoke.WM_APP + 2;
    private const nuint LingerTimer = 1;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly CaptureServiceOptions options;
    private readonly ICapturePlatform platform;
    private readonly EventHandler<WindowRegistryChangedEventArgs> registryChanged;
    private readonly EventHandler<DeviceLostEventArgs> deviceLost;
    private readonly Lock gate = new();
    private readonly List<CaptureSource> sources = [];
    private DispatcherThread? thread;
    private CaptureSuspension? suspension;
    private int running;
    private nint windowHandle;
    private bool disposed;
    private long sessionsStarted;
    private long sessionsReused;
    private long retiredReceived;
    private long retiredDropped;
    private long retiredUnchanged;
    private long retiredCompared;
    private long retiredUncompared;
    private long retiredRenotified;
    private long retiredRead;
    private long retiredDelay;
    private long retiredDelayMax;

    // Capture thread only.
    private readonly HashSet<RegisteredWindow> seenVisible = new(ReferenceEqualityComparer.Instance);
    private ServiceWindow? window;
    private GraphicsDevices? devices;
    private CompositorPacer? pacer;
    private ShowWatcher? showWatcher;
    private AutoResetEvent? completion;
    private bool platformReady;
    private bool sessionNotifications;
    private bool displayCheckPending;
    private bool lingerTimer;

    private CaptureService(CaptureServiceOptions options, ICapturePlatform platform)
    {
        this.options = options;
        this.platform = platform;
        registryChanged = OnRegistryChanged;
        deviceLost = OnDeviceLost;
        options.Registry.Changed += registryChanged;
    }

    /// <summary>Creates the service. Nothing runs until the first <see cref="Acquire"/>.</summary>
    public static CaptureService Create(CaptureServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Registry);
        if (options.MinUpdateInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MinUpdateInterval, "The minimum update interval must not be negative.");
        }
        if (options.StallThreshold <= TimeSpan.Zero || options.FirstFrameTimeout <= TimeSpan.Zero || options.ExclusionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Capture timeouts must be positive.");
        }
        if (options.MaximumFramesPerSecond is { } rate && (!double.IsFinite(rate) || rate <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), rate, "The frame rate limit must be finite and positive.");
        }
        if (options.StillFrames < 1 || !double.IsFinite(options.StillFramesPerSecond) || options.StillFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A still source needs at least one unchanged frame and a positive frame rate.");
        }
        if (options.Linger < TimeSpan.Zero || options.Linger > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Linger, "The linger time must be between zero and a day.");
        }
        if (options.FrameComparer is { TileSize: < 1 })
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The frame comparer's tiles must be at least one pixel.");
        }
        return new CaptureService(options, options.Platform ?? WindowsCapturePlatform.Instance);
    }

    /// <summary>Why capture is paused, or null.</summary>
    public CaptureSuspension? Suspension
    {
        get
        {
            using (NonPumpingLock.Enter(gate))
            {
                return suspension;
            }
        }
    }

    /// <summary>
    /// True while a display's source takes frames for a lease. A paused source (<see cref="CaptureServiceOptions.Linger"/>)
    /// does not count; once the last source stops, the capture thread releases the pacer, the magnifier and the capture
    /// device in the same step (WIN-SYS-07). For measurements and diagnostics.
    /// </summary>
    public bool IsCapturing
    {
        get
        {
            using (NonPumpingLock.Enter(gate))
            {
                return AnyPaced();
            }
        }
    }

    /// <summary>True while any source holds a capture, paused ones included. For measurements and diagnostics.</summary>
    public bool HoldsCapture
    {
        get
        {
            using (NonPumpingLock.Enter(gate))
            {
                return running > 0;
            }
        }
    }

    /// <summary>What the service has done since it was created, over every display, for measurements.</summary>
    public CaptureStatistics Statistics
    {
        get
        {
            long received, dropped, unchanged, compared, uncompared, told, read, delay, longest;
            CaptureSource[] current;
            using (NonPumpingLock.Enter(gate))
            {
                (received, dropped, unchanged, compared, uncompared, told) =
                    (retiredReceived, retiredDropped, retiredUnchanged, retiredCompared, retiredUncompared, retiredRenotified);
                (read, delay, longest) = (retiredRead, retiredDelay, retiredDelayMax);
                current = [.. sources];
            }
            foreach (var source in current)
            {
                var (sourceReceived, sourceDropped, sourceUnchanged) = source.Counters;
                var (sourceCompared, sourceUncompared, sourceTold) = source.Comparisons;
                var (sourceRead, sourceDelay, sourceLongest) = source.ResultDelays;
                received += sourceReceived;
                dropped += sourceDropped;
                unchanged += sourceUnchanged;
                compared += sourceCompared;
                uncompared += sourceUncompared;
                told += sourceTold;
                read += sourceRead;
                delay += sourceDelay;
                longest = Math.Max(longest, sourceLongest);
            }
            return new CaptureStatistics(received, dropped, unchanged, compared, uncompared, told, Interlocked.Read(ref sessionsStarted),
                                         Interlocked.Read(ref sessionsReused), read, Stopwatch.GetElapsedTime(0, delay), Stopwatch.GetElapsedTime(0, longest));
        }
    }

    /// <summary>The capture device while a source runs, for measurements and tests.</summary>
    internal GraphicsDevices? CurrentDevices => devices;

    /// <summary>Compares frames in the leases' areas (sources call it), or null.</summary>
    internal ICaptureFrameComparer? FrameComparer => options.FrameComparer;

    /// <summary>The pacer's counters, for measurements.</summary>
    internal (long Ticks, long Requests) PacerCounters => pacer?.Counters ?? default;

    internal CaptureSource[] Sources
    {
        get
        {
            using (NonPumpingLock.Enter(gate))
            {
                return [.. sources];
            }
        }
    }

    public ICaptureLease Acquire(DisplayInfo display, ICaptureConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(consumer);
        if (display.Bounds.IsEmpty)
        {
            throw new ArgumentException($"Display {display.DeviceName} has empty bounds.", nameof(display));
        }
        CaptureSource? starting = null;
        Lease lease;
        using (NonPumpingLock.Enter(gate))
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (thread is null)
            {
                thread = DispatcherThread.Start("Annotator capture");
                if (!thread.Queue.TryEnqueue(() => Run(InitializeThread)))
                {
                    throw new InvalidOperationException("The capture thread did not accept its first work item.");
                }
            }
            var source = sources.Find(candidate => candidate.Matches(display));
            if (source is null)
            {
                source = new CaptureSource(this, display, suspension is { } reason ? new CaptureState.Suspended(reason) : CaptureState.Starting.Instance);
                sources.Add(source);
                if (suspension is null)
                {
                    starting = source;
                }
            }
            else if (source.IsPaused)
            {
                // The paused capture of this display serves the new lease once its exclude list is current again.
                starting = source;
            }
            lease = new Lease(this, source, consumer);
            source.AddLease(lease);
        }
        if (starting is not null)
        {
            Post(() => StartSource(starting));
        }
        return lease;
    }

    /// <summary>Pauses capture on every display until <see cref="Resume"/> (WIN-OVR-06, WIN-OVR-09).</summary>
    public void Suspend(CaptureSuspension reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown suspension reason.");
        }
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed)
            {
                return;
            }
            suspension = reason;
        }
        Post(() => SuspendAll(reason));
    }

    /// <summary>
    /// Ends a suspension and retries failed sources, for example when the person selects an effect tool. While the
    /// session is still locked, capture stays suspended.
    /// </summary>
    public void Resume() => Post(ResumeAll);

    public void Dispose()
    {
        DispatcherThread? captureThread;
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            captureThread = thread;
        }
        options.Registry.Changed -= registryChanged;
        if (captureThread is null)
        {
            return;
        }
        if (captureThread.HasThreadAccess)
        {
            throw new InvalidOperationException("Dispose the capture service from another thread than its capture thread.");
        }
        var teardown = captureThread.RunAsync(TearDown);
        if (((IAsyncResult)teardown).AsyncWaitHandle.WaitOne(ShutdownTimeout))
        {
            teardown.GetAwaiter().GetResult();
        }
        // A capture thread that does not stop in time ends with the process; nothing else waits for it.
        ((IAsyncResult)captureThread.ShutdownAsync()).AsyncWaitHandle.WaitOne(ShutdownTimeout);
    }

    /// <summary>Stops a source's pixel source on the capture thread and keeps its leases and state.</summary>
    internal void StopPixelsLater(CaptureSource source) => Post(() =>
    {
        Detach(source);
        ForgetIfUnleased(source);
        ReleaseIfIdle();
    });

    internal void Release(Lease lease)
    {
        var source = lease.Source;
        var stop = false;
        var pause = false;
        using (NonPumpingLock.Enter(gate))
        {
            if (!source.RemoveLease(lease))
            {
                return;
            }
            if (source.LeaseCount == 0)
            {
                if (options.Linger > TimeSpan.Zero && source.IsRunning && !disposed && suspension is null
                    && (source.IsPaused || source.State is CaptureState.Live or CaptureState.Starting or CaptureState.Stalled))
                {
                    pause = true;
                }
                else
                {
                    sources.Remove(source);
                    stop = true;
                }
            }
        }
        if (stop)
        {
            Post(() =>
            {
                Detach(source);
                source.Transition(CaptureState.Stopped.Instance);
                Retire(source);
                ReleaseIfIdle();
            });
        }
        else if (pause)
        {
            Post(() => PauseSource(source));
        }
    }

    private void Post(Action work)
    {
        DispatcherThread? captureThread;
        using (NonPumpingLock.Enter(gate))
        {
            captureThread = disposed ? null : thread;
        }
        captureThread?.Queue.TryEnqueue(() => Run(work));
    }

    /// <summary>Runs capture-thread work. Expected failures are typed states; anything else is a bug that must not vanish.</summary>
    private static void Run(Action work)
    {
        try
        {
            work();
        }
        catch (Exception error)
        {
            NativeCallbackErrors.Report(error);
        }
    }

    private unsafe void InitializeThread()
    {
        // Physical pixels for the magnifier source and its window, whatever the process manifest says (WIN-REND-06).
        PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        window = new ServiceWindow(this);
        using (NonPumpingLock.Enter(gate))
        {
            windowHandle = (nint)window.Handle;
        }
    }

    private void StartSource(CaptureSource source)
    {
        bool resume;
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed || suspension is not null || !sources.Contains(source) || (source.IsRunning && !source.IsPaused))
            {
                return;
            }
            resume = source.IsPaused;
        }
        if (resume)
        {
            ResumeSource(source);
            return;
        }
        if (!platform.IsSupported())
        {
            source.Fail(new CaptureFailure(CaptureFailureKind.Unsupported, "GraphicsCaptureSession.IsSupported"));
            return;
        }
        if (platform.IsSessionLocked())
        {
            SuspendAll(CaptureSuspension.SessionLocked);
            return;
        }
        GraphicsDevices bundle;
        try
        {
            bundle = EnsureServices();
        }
        catch (CaptureStartException error)
        {
            source.Fail(error.Failure);
            ReleaseIfIdle();
            return;
        }
        using (NonPumpingLock.Enter(gate))
        {
            source.IsRunning = true;
            running++;
        }
        source.Prepare(bundle, completion!.SafeWaitHandle.DangerousGetHandle());
        // Read after the source counts as running: a window registered from now on is applied by a message instead.
        var exclusions = Exclusions();
        try
        {
            source.Attach(platform.Start(source.Display, bundle, exclusions, source, options));
        }
        catch (CaptureStartException error)
        {
            Detach(source);
            source.Fail(error.Failure);
            ReleaseIfIdle();
            return;
        }
        Interlocked.Increment(ref sessionsStarted);
        UpdatePacer();
        // Applies the list again with the windows' visibility known, and watches windows that were never shown.
        ApplyExclusionsNow();
    }

    /// <summary>
    /// A lease came back to a paused source (capture thread): the exclude list is applied again with the windows' visibility
    /// known before the source takes frames, so the frames it hands out are all taken after that.
    /// </summary>
    private void ResumeSource(CaptureSource source)
    {
        using (NonPumpingLock.Enter(gate))
        {
            if (!source.IsPaused || source.LeaseCount == 0)
            {
                return;
            }
        }
        if (platform.IsSessionLocked())
        {
            SuspendAll(CaptureSuspension.SessionLocked);
            return;
        }
        using (NonPumpingLock.Enter(gate))
        {
            if (!source.IsPaused || !source.IsRunning || source.LeaseCount == 0)
            {
                return;
            }
            // Counted as taking frames from here, so windows that were never shown get their watcher again.
            source.IsPaused = false;
        }
        // A source that fails here fails closed and stops; its leases see the failure. Until Resume it takes no frames.
        ApplyExclusionsNow();
        using (NonPumpingLock.Enter(gate))
        {
            if (!source.IsRunning)
            {
                return;
            }
        }
        Interlocked.Increment(ref sessionsReused);
        source.Resume();
        UpdatePacer();
        ArmLingerTimer();
    }

    /// <summary>
    /// The last lease ended (capture thread): the source stops taking frames and holds none, but keeps its capture for
    /// <see cref="CaptureServiceOptions.Linger"/>, so a lease that comes back meanwhile reuses it.
    /// </summary>
    private void PauseSource(CaptureSource source)
    {
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed || source.LeaseCount > 0 || !source.IsRunning || !sources.Contains(source))
            {
                return;
            }
            source.IsPaused = true;
            source.LingerUntil = Stopwatch.GetTimestamp() + (long)(options.Linger.TotalSeconds * Stopwatch.Frequency);
        }
        source.Pause();
        UpdatePacer();
        bool paced;
        using (NonPumpingLock.Enter(gate))
        {
            paced = AnyPaced();
        }
        if (!paced)
        {
            // Paused sources need no watcher (see Watch).
            showWatcher?.Update([]);
        }
        ArmLingerTimer();
    }

    /// <summary>Ends the paused sources whose time is up and sets the timer for the next one (capture thread).</summary>
    private void EndLingering()
    {
        var now = Stopwatch.GetTimestamp();
        List<CaptureSource>? ended = null;
        using (NonPumpingLock.Enter(gate))
        {
            foreach (var source in sources)
            {
                if (source.IsPaused && source.LeaseCount == 0 && now >= source.LingerUntil)
                {
                    (ended ??= []).Add(source);
                }
            }
            if (ended is not null)
            {
                foreach (var source in ended)
                {
                    sources.Remove(source);
                }
            }
        }
        if (ended is not null)
        {
            foreach (var source in ended)
            {
                Detach(source);
                Retire(source);
            }
            ReleaseIfIdle();
        }
        ArmLingerTimer();
    }

    /// <summary>A one-shot timer for the paused source that ends first, or none (capture thread).</summary>
    private unsafe void ArmLingerTimer()
    {
        long? next = null;
        using (NonPumpingLock.Enter(gate))
        {
            foreach (var source in sources)
            {
                if (source.IsPaused && (next is null || source.LingerUntil < next))
                {
                    next = source.LingerUntil;
                }
            }
        }
        if (window is null)
        {
            return;
        }
        if (next is not { } until)
        {
            if (lingerTimer)
            {
                PInvoke.KillTimer(window.Handle, LingerTimer);
                lingerTimer = false;
            }
            return;
        }
        var milliseconds = Math.Clamp((until - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency + 1, PInvoke.USER_TIMER_MINIMUM, int.MaxValue);
        lingerTimer = PInvoke.SetTimer(window.Handle, LingerTimer, (uint)milliseconds, null) != 0;
    }

    /// <summary>Removes a source without leases from the list once it has stopped (capture thread).</summary>
    private void ForgetIfUnleased(CaptureSource source)
    {
        var forget = false;
        using (NonPumpingLock.Enter(gate))
        {
            if (source.LeaseCount == 0 && !source.IsRunning && sources.Remove(source))
            {
                forget = true;
            }
        }
        if (forget)
        {
            Retire(source);
        }
    }

    /// <summary>Keeps a stopped source's counters in the service's statistics.</summary>
    private void Retire(CaptureSource source)
    {
        var (received, dropped, unchanged) = source.Counters;
        var (compared, uncompared, told) = source.Comparisons;
        var (read, delay, longest) = source.ResultDelays;
        using (NonPumpingLock.Enter(gate))
        {
            retiredReceived += received;
            retiredDropped += dropped;
            retiredUnchanged += unchanged;
            retiredCompared += compared;
            retiredUncompared += uncompared;
            retiredRenotified += told;
            retiredRead += read;
            retiredDelay += delay;
            retiredDelayMax = Math.Max(retiredDelayMax, longest);
        }
    }

    /// <summary>The magnifier, the device and the session notifications that running sources need (capture thread).</summary>
    private GraphicsDevices EnsureServices()
    {
        if (!platformReady)
        {
            platform.Initialize();
            platformReady = true;
        }
        // Windows sets it when the GPU has compared a frame; it lives as long as the capture thread, so a comparison that
        // finishes after its device went never sets a closed handle.
        completion ??= new AutoResetEvent(false);
        if (devices is null)
        {
            try
            {
                devices = platform.CreateDevices(options.DeviceOptions);
            }
            catch (NativeCallException error)
            {
                throw new CaptureStartException(new CaptureFailure(CaptureFailureKind.DevicesUnavailable, error.Operation, error));
            }
            devices.DeviceLost += deviceLost;
        }
        if (!sessionNotifications && window is not null)
        {
            // Without them the stall check still turns regions opaque when frames stop.
            sessionNotifications = PInvoke.WTSRegisterSessionNotification(window.Handle, PInvoke.NOTIFY_FOR_THIS_SESSION);
        }
        return devices;
    }

    /// <summary>Stops a source's pixel source (capture thread); frames held by consumers stay valid.</summary>
    private void Detach(CaptureSource source)
    {
        var pixels = source.Detach();
        using (NonPumpingLock.Enter(gate))
        {
            source.IsPaused = false;
            if (source.IsRunning)
            {
                source.IsRunning = false;
                running--;
            }
        }
        UpdatePacer();
        pixels?.Dispose();
    }

    /// <summary>With no running source, releases the pacer, notifications, device and magnifier (capture thread).</summary>
    private void ReleaseIfIdle()
    {
        using (NonPumpingLock.Enter(gate))
        {
            if (running > 0)
            {
                return;
            }
        }
        UpdatePacer();
        showWatcher?.Dispose();
        showWatcher = null;
        if (sessionNotifications && window is not null)
        {
            PInvoke.WTSUnRegisterSessionNotification(window.Handle);
            sessionNotifications = false;
        }
        if (devices is not null)
        {
            devices.DeviceLost -= deviceLost;
            devices.Dispose();
            devices = null;
        }
        if (platformReady)
        {
            platform.Uninitialize();
            platformReady = false;
        }
        ArmLingerTimer();
    }

    /// <summary>The pacer asks the sources that take frames; a paused source asks for none (capture thread).</summary>
    private void UpdatePacer()
    {
        CaptureSource[] paced;
        using (NonPumpingLock.Enter(gate))
        {
            paced = [.. sources.Where(source => source.IsRunning && !source.IsPaused)];
        }
        if (paced.Length == 0)
        {
            pacer?.Dispose();
            pacer = null;
            return;
        }
        pacer ??= new CompositorPacer(options, completion!);
        pacer.Update(paced);
    }

    private nint[] Exclusions()
    {
        var windows = options.Registry.Windows;
        var handles = new nint[windows.Count];
        for (var index = 0; index < handles.Length; index++)
        {
            handles[index] = windows[index].Handle;
        }
        return handles;
    }

    /// <summary>
    /// Applies the registry's windows to every running source and watches the windows capture has never seen visible
    /// (capture thread). The Magnification API leaves out only windows that have been shown at least once, so a window
    /// counts as seen only if it was visible before the list was applied; a window shown meanwhile is applied again.
    /// Returns false when a source or the watcher failed; those sources fail closed.
    /// </summary>
    private bool ApplyExclusionsNow()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var windows = options.Registry.Windows;
            var visibleBefore = windows.Where(registered => PInvoke.IsWindowVisible((HWND)registered.Handle)).ToArray();
            var handles = new nint[windows.Count];
            for (var index = 0; index < handles.Length; index++)
            {
                handles[index] = windows[index].Handle;
            }
            if (!ApplyToRunningSources(handles))
            {
                return false;
            }
            seenVisible.IntersectWith(windows);
            seenVisible.UnionWith(visibleBefore);
            var pending = windows.Where(registered => !seenVisible.Contains(registered)).ToArray();
            if (!Watch(pending))
            {
                return false;
            }
            if (!Array.Exists(pending, registered => PInvoke.IsWindowVisible((HWND)registered.Handle)))
            {
                return true;
            }
        }
        return true;
    }

    private bool ApplyToRunningSources(nint[] handles)
    {
        var applied = true;
        foreach (var source in Sources)
        {
            if (!source.IsRunning)
            {
                continue;
            }
            try
            {
                source.ApplyExclusions(handles);
            }
            catch (NativeCallException error)
            {
                applied = false;
                Detach(source);
                source.Fail(new CaptureFailure(CaptureFailureKind.ExclusionFailed, error.Operation, error));
                ForgetIfUnleased(source);
            }
        }
        if (!applied)
        {
            ReleaseIfIdle();
        }
        return applied;
    }

    /// <summary>
    /// Watches windows that were never seen visible while a source takes frames (capture thread). A paused source needs no
    /// watcher: its frames are dropped, and resuming applies the list again before it takes any.
    /// </summary>
    private bool Watch(RegisteredWindow[] pending)
    {
        using (NonPumpingLock.Enter(gate))
        {
            if (!AnyPaced())
            {
                pending = [];
            }
        }
        if (pending.Length == 0)
        {
            showWatcher?.Update([]);
            return true;
        }
        showWatcher ??= new ShowWatcher(OnPendingWindowShown);
        try
        {
            showWatcher.Update(pending);
            return true;
        }
        catch (NativeCallException error)
        {
            FailRunning(new CaptureFailure(CaptureFailureKind.ExclusionFailed, error.Operation, error));
            return false;
        }
    }

    /// <summary>Fails every running source closed and stops it later (any thread).</summary>
    private void FailRunning(CaptureFailure failure)
    {
        foreach (var source in Sources)
        {
            if (source.IsRunning)
            {
                source.Fail(failure);
                StopPixelsLater(source);
            }
        }
    }

    private void OnRegistryChanged(object? sender, WindowRegistryChangedEventArgs change)
    {
        if (change.Change == WindowRegistryChange.Removed)
        {
            // Not urgent: a destroyed window paints nothing. Applying it keeps a reused handle from hiding another app's window.
            PostToServiceWindow(ApplyExclusionsMessage);
            return;
        }
        // A window that is already visible must be left out before it paints; a hidden one gets its watcher now.
        ApplyFromAnotherThread($"Leave window 0x{change.Window.Handle:X} out of the capture in time");
    }

    /// <summary>A watched window is being shown for the first time; runs inside that call on the window's thread.</summary>
    private void OnPendingWindowShown(nint shown) => ApplyFromAnotherThread($"Leave window 0x{shown:X} out of the capture when it first shows");

    /// <summary>
    /// Has the capture thread apply the exclusions while the calling thread waits, at most
    /// <see cref="CaptureServiceOptions.ExclusionTimeout"/>; on failure every running source fails closed. Nothing waits while
    /// every source is paused: resuming applies the list before frames are taken.
    /// </summary>
    private unsafe void ApplyFromAnotherThread(string operation)
    {
        DispatcherThread? captureThread;
        nint target;
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed || !AnyPaced())
            {
                return;
            }
            captureThread = thread;
            target = windowHandle;
        }
        if (captureThread is null || target == 0)
        {
            return;
        }
        if (captureThread.HasThreadAccess)
        {
            ApplyExclusionsNow();
            return;
        }
        nuint result = 0;
        var sent = PInvoke.SendMessageTimeout((HWND)target, ApplyExclusionsMessage, default, default,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_NORMAL | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
            (uint)Math.Ceiling(options.ExclusionTimeout.TotalMilliseconds), &result);
        if (sent.Value == 0 || result != 1)
        {
            FailRunning(new CaptureFailure(CaptureFailureKind.ExclusionFailed, operation));
        }
    }

    private void PostToServiceWindow(uint message)
    {
        nint target;
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed || !AnyPaced())
            {
                return;
            }
            target = windowHandle;
        }
        if (target != 0)
        {
            PInvoke.PostMessage((HWND)target, message, default, default);
        }
    }

    /// <summary>Whether a source takes frames now (under the lock).</summary>
    private bool AnyPaced() => sources.Exists(source => source.IsRunning && !source.IsPaused);

    private void OnDeviceLost(object? sender, DeviceLostEventArgs args) => Post(() =>
    {
        if (devices is not { } lost || !ReferenceEquals(sender, lost))
        {
            return;
        }
        // Fail closed at once, then start every leased source again on a new device; paused ones end.
        var restart = Sources.Where(source => source.IsRunning).ToArray();
        foreach (var source in restart)
        {
            Detach(source);
            source.Transition(CaptureState.Starting.Instance);
            ForgetIfUnleased(source);
        }
        lost.DeviceLost -= deviceLost;
        lost.Dispose();
        devices = null;
        foreach (var source in restart)
        {
            if (source.LeaseCount > 0)
            {
                StartSource(source);
            }
        }
        ReleaseIfIdle();
    });

    private void SuspendAll(CaptureSuspension reason)
    {
        CaptureSource[] all;
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed)
            {
                return;
            }
            suspension = reason;
            all = [.. sources];
        }
        var state = new CaptureState.Suspended(reason);
        foreach (var source in all)
        {
            Detach(source);
            source.Transition(state);
            ForgetIfUnleased(source);
        }
        ReleaseIfIdle();
    }

    private void ResumeAll()
    {
        if (platform.IsSessionLocked())
        {
            SuspendAll(CaptureSuspension.SessionLocked);
            return;
        }
        CaptureSource[] all;
        using (NonPumpingLock.Enter(gate))
        {
            if (disposed)
            {
                return;
            }
            suspension = null;
            all = [.. sources];
        }
        foreach (var source in all)
        {
            if (!source.IsRunning)
            {
                source.Transition(CaptureState.Starting.Instance);
                StartSource(source);
            }
        }
    }

    private void OnSessionChange(uint change)
    {
        CaptureSuspension? reason = change switch
        {
            PInvoke.WTS_SESSION_LOCK => CaptureSuspension.SessionLocked,
            PInvoke.WTS_SESSION_LOGOFF => CaptureSuspension.SignOut,
            PInvoke.WTS_CONSOLE_CONNECT or PInvoke.WTS_CONSOLE_DISCONNECT or PInvoke.WTS_REMOTE_CONNECT or PInvoke.WTS_REMOTE_DISCONNECT =>
                CaptureSuspension.SessionChanged,
            _ => null,
        };
        if (reason is { } suspended)
        {
            SuspendAll(suspended);
        }
    }

    private void ScheduleDisplayCheck()
    {
        if (!displayCheckPending && window is not null && PInvoke.PostMessage(window.Handle, CheckDisplaysMessage, default, default))
        {
            displayCheckPending = true;
        }
    }

    /// <summary>Fails the sources whose display moved, resized or disconnected (capture thread).</summary>
    private void CheckDisplays()
    {
        displayCheckPending = false;
        var runningSources = Sources.Where(source => source.IsRunning).ToArray();
        if (runningSources.Length == 0)
        {
            return;
        }
        IReadOnlyList<DisplayInfo>? current = null;
        NativeCallException? failure = null;
        try
        {
            current = platform.GetDisplays();
        }
        catch (NativeCallException error)
        {
            failure = error;
        }
        foreach (var source in runningSources)
        {
            var match = current?.FirstOrDefault(display => display.DeviceName == source.Display.DeviceName);
            if (match is not null && match.Bounds == source.Display.Bounds)
            {
                continue;
            }
            Detach(source);
            source.Fail(match is null && failure is null
                ? new CaptureFailure(CaptureFailureKind.DisplayUnavailable, $"Find display {source.Display.DeviceName}")
                : new CaptureFailure(CaptureFailureKind.DisplayChanged, $"Check display {source.Display.DeviceName}", failure));
            ForgetIfUnleased(source);
        }
        ReleaseIfIdle();
    }

    private void TearDown()
    {
        CaptureSource[] all;
        using (NonPumpingLock.Enter(gate))
        {
            all = [.. sources];
            sources.Clear();
        }
        foreach (var source in all)
        {
            Detach(source);
            source.Transition(CaptureState.Stopped.Instance);
            Retire(source);
        }
        ReleaseIfIdle();
        window?.Destroy();
        window = null;
        lingerTimer = false;
        using (NonPumpingLock.Enter(gate))
        {
            windowHandle = 0;
        }
        // Every device is gone, so no comparison can still set it.
        completion?.Dispose();
        completion = null;
    }

    /// <summary>One consumer's hold on a display's capture.</summary>
    internal sealed class Lease(CaptureService service, CaptureSource source, ICaptureConsumer consumer) : ICaptureLease
    {
        private int disposed;

        public CaptureSource Source { get; } = source;

        public ICaptureConsumer Consumer { get; } = consumer;

        public DisplayInfo Display => Source.Display;

        public CaptureState State => Volatile.Read(ref disposed) != 0 ? CaptureState.Stopped.Instance : Source.State;

        // The source's state for this lease, under the source's state lock.

        /// <summary>The lease's bit in the comparisons' tiles.</summary>
        internal int Bit;

        /// <summary>The newest compared frame whose pixels in the lease's areas the lease's content version stands for.</summary>
        internal long Version;

        /// <summary>Whether the lease's areas changed in the newest compared frame.</summary>
        internal bool Hot;

        /// <summary>The newest frame handed out under its own version before its comparison was read.</summary>
        internal long LastProvisional;

        /// <summary>The first frame compared with the lease's current areas; earlier results count as changes for it.</summary>
        internal long CountedFrom;

        internal PixelRect[] Interest = [];

        internal int InterestCount;

        internal bool WholeDisplay = true;

        public bool TryAcquireFrame(out CaptureFrame frame)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                frame = default;
                return false;
            }
            return Source.TryAcquire(this, out frame);
        }

        public void SetInterest(ReadOnlySpan<PixelRect> areas)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                Source.SetInterest(this, areas);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                service.Release(this);
            }
        }
    }

    /// <summary>
    /// A hidden top-level window on the capture thread: exclusion requests from other threads, session, power and
    /// display messages. It is never shown, so it is not registered.
    /// </summary>
    private sealed class ServiceWindow : NativeWindow
    {
        private readonly CaptureService owner;

        public ServiceWindow(CaptureService owner)
        {
            this.owner = owner;
            CreateHandle("Annotator.Platform.CaptureService", WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE, WINDOW_STYLE.WS_POPUP,
                         0, 0, 0, 0, HWND.Null, "CreateWindowEx (capture service)");
        }

        public void Destroy() => DestroyHandle("DestroyWindow (capture service)");

        protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
                case ApplyExclusionsMessage:
                    return (LRESULT)(owner.ApplyExclusionsNow() ? 1 : 0);
                case CheckDisplaysMessage:
                    owner.CheckDisplays();
                    return default;
                case PInvoke.WM_TIMER when wParam.Value == LingerTimer:
                    PInvoke.KillTimer(Handle, LingerTimer);
                    owner.lingerTimer = false;
                    owner.EndLingering();
                    return default;
                case PInvoke.WM_DISPLAYCHANGE:
                case PInvoke.WM_DPICHANGED:
                    owner.ScheduleDisplayCheck();
                    return default;
                case PInvoke.WM_WTSSESSION_CHANGE:
                    owner.OnSessionChange((uint)wParam.Value);
                    return default;
                case PInvoke.WM_POWERBROADCAST:
                    if (wParam.Value == PInvoke.PBT_APMSUSPEND)
                    {
                        owner.SuspendAll(CaptureSuspension.SystemSleep);
                    }
                    return (LRESULT)1;
                default:
                    return base.WndProc(message, wParam, lParam);
            }
        }
    }
}
