using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Presentation;

namespace Annotator.RenderHarness;

/// <summary>
/// The real overlay on the primary display, click-through (Use apps), with a presenter, on a dedicated overlay
/// thread as the app runs it. Every call to <see cref="Run{T}(Func{T}, string)"/> executes on that thread and waits
/// at most <see cref="StepTimeout"/>; a step that takes longer ends the run with a <see cref="TimeoutException"/>.
/// </summary>
internal sealed class OverlaySession : IDisposable
{
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly DispatcherThread thread;
    private OverlayManager? manager;

    private OverlaySession(DispatcherThread thread)
    {
        this.thread = thread;
    }

    public GraphicsDevices Devices { get; private set; } = null!;

    /// <summary>Annotator's windows: the overlays register here, and a capture service leaves them out.</summary>
    public WindowRegistry Registry { get; } = new();

    public Windows.UI.Composition.Compositor Compositor => manager!.Compositor;

    public OverlayWindow Overlay { get; private set; } = null!;

    public OverlayPresenter Presenter { get; private set; } = null!;

    public DisplayInfo Display => Overlay.Display;

    public static OverlaySession Start(LayerKind scene, LayerKind live, RendererOptions? options = null)
    {
        var session = new OverlaySession(DispatcherThread.Start("Annotator overlay"));
        try
        {
            session.Run(() =>
            {
                session.Devices = GraphicsDevices.Create();
                session.manager = OverlayManager.Create(new OverlayManagerOptions
                {
                    Registry = session.Registry,
                    InputSink = new IgnoredInput(),
                    InitialMode = OverlayInputMode.UseApps,
                });
                session.Overlay = session.manager.Overlays.First(overlay => overlay.Display.IsPrimary);
                session.CreatePresenter(scene, live, options);
            }, "creating the devices, the overlay and the presenter");
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    /// <summary>Replaces the presenter with one using other layer kinds (on the overlay thread).</summary>
    public void Recreate(LayerKind scene, LayerKind live, RendererOptions? options = null) => Run(() =>
    {
        Presenter.Dispose();
        CreatePresenter(scene, live, options);
    }, "recreating the presenter");

    /// <summary>Switches the overlays to <paramref name="mode"/>; Draw activates the primary overlay (on the overlay thread).</summary>
    public void SetInputMode(OverlayInputMode mode) => Run(() =>
    {
        manager!.SetInputMode(mode, mode == OverlayInputMode.Draw ? Overlay : null);
    }, "switching the overlay's input mode");

    public T Run<T>(Func<T> work, string step = "a step") => Await(thread.RunAsync(work), step);

    public void Run(Action work, string step = "a step") => Await(thread.RunAsync(work), step);

    public void Dispose()
    {
        try
        {
            if (!thread.IsShutDown)
            {
                Run(() =>
                {
                    Presenter?.Dispose();
                    manager?.Dispose();
                    Devices?.Dispose();
                }, "releasing the presenter, the overlay and the devices");
            }
        }
        finally
        {
            if (!((IAsyncResult)thread.ShutdownAsync()).AsyncWaitHandle.WaitOne(ShutdownTimeout))
            {
                Console.Error.WriteLine($"WARNING: the overlay thread did not shut down within {ShutdownTimeout.TotalSeconds:F0} s; the process exit removes it.");
            }
        }
    }

    private static void Await(Task task, string step)
    {
        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(StepTimeout))
        {
            throw new TimeoutException($"The overlay thread did not finish {step} within {StepTimeout.TotalSeconds:F0} s.");
        }
        task.GetAwaiter().GetResult();
    }

    private static T Await<T>(Task<T> task, string step)
    {
        Await((Task)task, step);
        return task.GetAwaiter().GetResult();
    }

    private void CreatePresenter(LayerKind scene, LayerKind live, RendererOptions? options) =>
        Presenter = OverlayPresenter.Create(new OverlayPresenterOptions
        {
            Compositor = manager!.Compositor,
            Root = Overlay.Root,
            Devices = Devices,
            Scale = Display.Scale,
            PixelWidth = Display.Bounds.Width,
            PixelHeight = Display.Bounds.Height,
            Colors = new WindowsSystemColors(),
            Renderer = options,
            SceneLayer = scene,
            LiveLayer = live,
        });

    private sealed class IgnoredInput : IOverlayInputSink
    {
        public void OnPointer(OverlayWindow overlay, in PointerInput input)
        {
        }

        public bool OnKey(OverlayWindow overlay, in KeyInput input) => false;

        public void OnInputReleaseRequested(OverlayWindow overlay, InputReleaseReason reason)
        {
        }

        public void OnFocusChanged(OverlayWindow overlay, bool hasFocus)
        {
        }
    }
}

/// <summary>
/// Ends the process when a run exceeds its time limit, so a harness can never hang (for example while the display is
/// off and no composition frames arrive). Prints the reason first.
/// </summary>
internal sealed class Watchdog : IDisposable
{
    private readonly ManualResetEventSlim finished = new();

    private Watchdog(TimeSpan limit, string mode)
    {
        var thread = new Thread(() =>
        {
            if (finished.Wait(limit))
            {
                return;
            }
            Console.Out.Flush();
            Console.Error.WriteLine($"TIMEOUT: the '{mode}' run exceeded its limit of {limit.TotalSeconds:F0} s; ending the process.");
            Console.Error.Flush();
            var exit = new Thread(() => Environment.Exit(124)) { IsBackground = true };
            exit.Start();
            if (!exit.Join(TimeSpan.FromSeconds(5)))
            {
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        })
        {
            IsBackground = true,
            Name = "Harness watchdog",
        };
        thread.Start();
    }

    public static Watchdog Start(TimeSpan limit, string mode) => new(limit, mode);

    public void Dispose() => finished.Set();
}
