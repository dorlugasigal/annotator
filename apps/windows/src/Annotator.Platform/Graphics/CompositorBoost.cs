using System.Runtime.InteropServices;

namespace Annotator.Platform.Graphics;

/// <summary>
/// Asks Windows for the display's higher refresh rate while the person draws (WIN-PERF-01): on a display with Dynamic
/// refresh rate (for example 60 Hz that boosts to 120 Hz), <c>DCompositionBoostCompositorClock</c> raises the rate for
/// latency-sensitive content such as inking, as Microsoft's compositor clock guidance asks inking apps to do; at a fixed
/// refresh rate it changes nothing (measured on the reference machine at 60 Hz). Holds are counted, so overlapping
/// presses on several overlays make one request, and the last release ends it: nothing stays boosted while nothing is
/// drawn. Windows ends an app's requests when the app exits. Use it on one thread (the overlay thread).
/// </summary>
public sealed partial class CompositorBoost
{
    private readonly Func<bool, int> _request;
    private int _holds;
    private bool _boosted;

    public CompositorBoost()
        : this(static enable => DCompositionBoostCompositorClock(enable ? 1 : 0))
    {
    }

    /// <summary>For tests: <paramref name="request"/> stands in for <c>DCompositionBoostCompositorClock</c> and returns its HRESULT.</summary>
    internal CompositorBoost(Func<bool, int> request)
    {
        _request = request;
    }

    /// <summary>Presses holding the boost now.</summary>
    public int Holds => _holds;

    /// <summary>Whether Windows accepted the request that the current holds made.</summary>
    public bool IsBoosted => _boosted;

    /// <summary>Requests made to Windows since the start, for measurements and the live check.</summary>
    public int Requests { get; private set; }

    /// <summary>
    /// The last request Windows refused, or null. A refused boost changes nothing the person sees (the display keeps its
    /// rate), so it is kept here for diagnostics instead of interrupting the drawing.
    /// </summary>
    public NativeCallException? Failure { get; private set; }

    /// <summary>Raised when Windows refuses a request.</summary>
    public event Action<NativeCallException>? Failed;

    /// <summary>A press began: the first hold asks for the higher refresh rate.</summary>
    public void Hold()
    {
        if (_holds++ > 0)
        {
            return;
        }
        Requests++;
        int result = _request(true);
        if (result < 0)
        {
            Report(new NativeCallException("DCompositionBoostCompositorClock(TRUE)", result));
            return;
        }
        _boosted = true;
    }

    /// <summary>A press ended: the last release gives the refresh rate back. A release without a hold does nothing.</summary>
    public void Release()
    {
        if (_holds == 0 || --_holds > 0 || !_boosted)
        {
            return;
        }
        End();
    }

    /// <summary>Ends every hold at once (the overlays are going), giving the refresh rate back.</summary>
    public void ReleaseAll()
    {
        _holds = 0;
        if (_boosted)
        {
            End();
        }
    }

    private void End()
    {
        _boosted = false;
        int result = _request(false);
        if (result < 0)
        {
            Report(new NativeCallException("DCompositionBoostCompositorClock(FALSE)", result));
        }
    }

    private void Report(NativeCallException error)
    {
        Failure = error;
        Failed?.Invoke(error);
    }

    [LibraryImport("dcomp.dll")]
    private static partial int DCompositionBoostCompositorClock(int enable);
}
