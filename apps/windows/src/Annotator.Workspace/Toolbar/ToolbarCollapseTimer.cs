namespace Annotator.Workspace.Toolbar;

/// <summary>
/// Port of ToolbarCollapseController (CollapsedToolbar.swift): decides when the toolbar shrinks to one button and when it
/// opens again. It shrinks once the pointer has been off it for the chosen delay, unless something holds it open
/// (<see cref="IsHeldOpen"/>: a menu or flyout, a drag, typing, the tutorial or the pointer on the toolbar), and opens as
/// soon as the pointer is on it. Use it on the app thread; its waits resume there.
/// </summary>
public sealed class ToolbarCollapseTimer
{
    private readonly Func<double?> _delay;
    private readonly Func<bool> _collapsed;
    private CancellationTokenSource? _collapseWait;

    /// <param name="delay">The saved delay in seconds (<c>WorkspacePreferences.ToolbarCollapseDelay</c>); null never shrinks.</param>
    /// <param name="collapsed">Whether the toolbar shows as one button now (<see cref="WorkspaceSnapshot.ToolbarCollapsed"/>).</param>
    public ToolbarCollapseTimer(Func<double?> delay, Func<bool> collapsed)
    {
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(collapsed);
        _delay = delay;
        _collapsed = collapsed;
    }

    public bool PointerInside { get; private set; }

    public bool IsWaitingToCollapse => _collapseWait is not null;

    /// <summary>Things that keep the toolbar open, checked when the wait ends.</summary>
    public Func<bool> IsHeldOpen { get; set; } = static () => false;

    /// <summary>Shrinks the toolbar with its morph (the host).</summary>
    public Action Collapse { get; set; } = static () => { };

    /// <summary>Opens the toolbar with its morph (the host).</summary>
    public Action Expand { get; set; } = static () => { };

    /// <summary>Waits the given seconds; tests shorten it.</summary>
    public Func<double, CancellationToken, Task> Wait { get; set; } =
        static (seconds, token) => Task.Delay(TimeSpan.FromSeconds(seconds), token);

    public void PointerChanged(bool inside)
    {
        PointerInside = inside;
        if (inside)
        {
            CancelCollapse();
            if (_collapsed())
            {
                Expand();
            }
        }
        else
        {
            Restart();
        }
    }

    /// <summary>Starts the wait again, for example when the toolbar appears or opens, or the setting changes.</summary>
    public void Restart()
    {
        CancelCollapse();
        if (_delay() is not { } delay || _collapsed())
        {
            return;
        }
        var wait = new CancellationTokenSource();
        _collapseWait = wait;
        _ = WaitToCollapse(delay, wait);
    }

    public void Cancel() => CancelCollapse();

    private async Task WaitToCollapse(double delay, CancellationTokenSource wait)
    {
        try
        {
            await Wait(delay, wait.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (wait.IsCancellationRequested || !ReferenceEquals(_collapseWait, wait))
        {
            return;
        }
        _collapseWait = null;
        wait.Dispose();
        if (PointerInside || IsHeldOpen())
        {
            Restart();
            return;
        }
        Collapse();
    }

    private void CancelCollapse()
    {
        _collapseWait?.Cancel();
        _collapseWait?.Dispose();
        _collapseWait = null;
    }
}
