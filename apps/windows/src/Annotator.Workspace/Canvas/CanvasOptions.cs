using Annotator.Core.Geometry;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// Runs work away from the canvas thread and returns results to it. Smart Draw recognition runs here so pointer
/// input never waits for it (Mac: detached tasks).
/// </summary>
public interface ICanvasScheduler
{
    /// <summary>
    /// Runs <paramref name="work"/> on another thread, then calls <paramref name="done"/> with its result, or
    /// <paramref name="failed"/> with its exception, on the canvas thread.
    /// </summary>
    void Run<T>(Func<T> work, Action<T> done, Action<Exception> failed);
}

/// <summary>Runs work at once on the calling thread; for tests and for hosts without a background thread.</summary>
public sealed class ImmediateCanvasScheduler : ICanvasScheduler
{
    public static ImmediateCanvasScheduler Instance { get; } = new();

    public void Run<T>(Func<T> work, Action<T> done, Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(failed);
        T result;
        try
        {
            result = work();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failed(error);
            return;
        }
        done(result);
    }
}

/// <summary>Runs work on the thread pool and posts results with <paramref name="post"/> (for example the overlay thread's dispatcher queue).</summary>
public sealed class BackgroundCanvasScheduler(Func<Action, bool> post) : ICanvasScheduler
{
    public void Run<T>(Func<T> work, Action<T> done, Action<Exception> failed)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(failed);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            T result;
            try
            {
                result = work();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                post(() => failed(error));
                return;
            }
            // A queue that shuts down drops the result; the canvas is gone then.
            post(() => done(result));
        }, null);
    }
}

/// <summary>
/// Shows the text editor for a canvas (the Text Services Framework editor of spike S-7). The canvas decides where text
/// goes and what committing or cancelling does; the host owns typing, IME composition, the caret and the clipboard
/// inside the editor. The host calls <see cref="CanvasController.SetText"/> after every change and
/// <see cref="CanvasController.CommitText"/> for Escape or Ctrl+Enter (spec 009: Escape commits text).
/// </summary>
public interface ITextEditorHost
{
    /// <summary>Shows the editor for <paramref name="session"/> and gives it keyboard focus.</summary>
    void Begin(CanvasController canvas, CanvasTextSession session);

    /// <summary>The session's frame, style or text changed (for example a font change from the toolbar).</summary>
    void Update(CanvasTextSession session);

    /// <summary>The session ended; remove the editor and give keyboard focus back to the canvas.</summary>
    void End();
}

/// <summary>Settings of a <see cref="CanvasController"/>.</summary>
public sealed record CanvasOptions
{
    /// <summary>Measures text for text frames and shape labels (DirectWrite on Windows, D-4). Must be safe to use on the canvas thread.</summary>
    public required ITextMeasurer TextMeasurer { get; init; }

    public ICanvasScheduler Scheduler { get; init; } = ImmediateCanvasScheduler.Instance;

    /// <summary>
    /// The text editor host. Without one the canvas does not edit text: the Text tool and double-click editing show
    /// <see cref="WorkspaceModel.TextUnavailableMessage"/> instead of opening an editor nothing can be typed into.
    /// </summary>
    public ITextEditorHost? TextEditor { get; init; }

    /// <summary>Connects the canvases of all displays on one thread, for drags between displays.</summary>
    public CanvasCoordinator? Coordinator { get; init; }

    /// <summary>The longest time between the clicks of a double-click, in milliseconds (Windows GetDoubleClickTime).</summary>
    public double DoubleClickTime { get; init; } = 500;

    /// <summary>The area, in points, the second click of a double-click must hit (Windows SM_CXDOUBLECLK and SM_CYDOUBLECLK).</summary>
    public Size DoubleClickSize { get; init; } = new(4, 4);

    /// <summary>True when Windows Animation effects are off (the Mac's Reduce Motion): no ink-to-shape morph, no erase shrink.</summary>
    public Func<bool> ReducesMotion { get; init; } = static () => false;

    /// <summary>
    /// Seconds on the steady clock that <see cref="CanvasController.AdvanceTransitions"/> gets its frame times from (the Mac's
    /// CACurrentMediaTime): the laser pointer's trail stamps its points with it.
    /// </summary>
    public Func<double> Clock { get; init; } = static () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
}
