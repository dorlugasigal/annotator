using Annotator.Platform.Interop;
using Windows.System;

namespace Annotator.Platform.Threading;

/// <summary>
/// A dedicated thread with its own message loop, a COM single-threaded apartment and a
/// Windows.System dispatcher queue: the overlay threading model recommended by Gate 0 S-12.
/// Create the overlay's windows, devices and compositor inside <see cref="RunAsync(Action)"/>.
/// The thread waits in its message loop when it has no work, with no timers or polling.
/// </summary>
public sealed class DispatcherThread : IDisposable
{
    private readonly DispatcherQueueController _controller;
    private readonly Lock _gate = new();
    private Task? _shutdown;

    private DispatcherThread(DispatcherQueueController controller, string name)
    {
        _controller = controller;
        Queue = controller.DispatcherQueue;
        Name = name;
    }

    /// <summary>Starts the thread. <paramref name="name"/> appears in debuggers and traces.</summary>
    public static DispatcherThread Start(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var thread = new DispatcherThread(DispatcherQueueInterop.CreateOnDedicatedThread(), name);
        if (!thread.Queue.TryEnqueue(() => Thread.CurrentThread.Name = name))
        {
            thread.Dispose();
            throw new InvalidOperationException($"The {name} dispatcher thread did not accept its first work item.");
        }
        return thread;
    }

    public string Name { get; }

    public DispatcherQueue Queue { get; }

    /// <summary>True when the caller runs on this thread.</summary>
    public bool HasThreadAccess => Queue.HasThreadAccess;

    public bool IsShutDown
    {
        get
        {
            lock (_gate)
            {
                return _shutdown is not null;
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the thread. Exceptions reach the returned task.</summary>
    public Task RunAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(() =>
        {
            work();
            return true;
        });
    }

    /// <summary>Runs <paramref name="work"/> on the thread and returns its result. Exceptions reach the returned task.</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(IsShutDown, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Queue.TryEnqueue(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        if (!queued)
        {
            completion.SetException(new InvalidOperationException($"The {Name} dispatcher thread is shutting down and did not run the work."));
        }
        return completion.Task;
    }

    /// <summary>
    /// Stops the queue after the work already queued, then ends the thread. Release the
    /// thread's windows and devices with <see cref="RunAsync(Action)"/> first.
    /// </summary>
    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            _shutdown ??= _controller.ShutdownQueueAsync().AsTask();
            return _shutdown;
        }
    }

    /// <summary>Shuts down and waits for the thread to end. Call it from another thread.</summary>
    public void Dispose()
    {
        if (HasThreadAccess)
        {
            throw new InvalidOperationException($"The {Name} dispatcher thread cannot wait for its own shutdown; dispose it from another thread.");
        }
        ShutdownAsync().GetAwaiter().GetResult();
    }
}
