using Annotator.Platform.Graphics;

namespace Annotator.Platform.Tests;

public sealed class CompositorBoostTests
{
    [Fact]
    public void TheFirstHoldBoostsAndTheLastReleaseEndsIt()
    {
        var calls = new List<bool>();
        var boost = new CompositorBoost(enable =>
        {
            calls.Add(enable);
            return 0;
        });

        boost.Hold();
        boost.Hold();
        Assert.Equal([true], calls);
        Assert.True(boost.IsBoosted);
        Assert.Equal(2, boost.Holds);

        boost.Release();
        Assert.Equal([true], calls);
        Assert.True(boost.IsBoosted);

        boost.Release();
        Assert.Equal([true, false], calls);
        Assert.False(boost.IsBoosted);
        Assert.Equal(0, boost.Holds);
        Assert.Equal(1, boost.Requests);
        Assert.Null(boost.Failure);
    }

    [Fact]
    public void AReleaseWithoutAHoldAsksNothing()
    {
        var calls = new List<bool>();
        var boost = new CompositorBoost(enable =>
        {
            calls.Add(enable);
            return 0;
        });

        boost.Release();
        boost.ReleaseAll();

        Assert.Empty(calls);
        Assert.Equal(0, boost.Holds);
    }

    [Fact]
    public void ReleaseAllEndsEveryHoldWithOneRequest()
    {
        var calls = new List<bool>();
        var boost = new CompositorBoost(enable =>
        {
            calls.Add(enable);
            return 0;
        });

        boost.Hold();
        boost.Hold();
        boost.Hold();
        boost.ReleaseAll();
        boost.Release();

        Assert.Equal([true, false], calls);
        Assert.Equal(0, boost.Holds);
        Assert.False(boost.IsBoosted);
    }

    [Fact]
    public void ARefusedBoostIsReportedAndNeverEndedTwice()
    {
        const int Refused = unchecked((int)0x80004005);
        var calls = new List<bool>();
        var boost = new CompositorBoost(enable =>
        {
            calls.Add(enable);
            return enable ? Refused : 0;
        });
        NativeCallException? reported = null;
        boost.Failed += error => reported = error;

        boost.Hold();
        Assert.False(boost.IsBoosted);
        Assert.NotNull(reported);
        Assert.Equal(Refused, reported.HResult);
        Assert.Same(reported, boost.Failure);

        // Windows refuses an end without a boost, so nothing is ended for a refused request.
        boost.Release();
        Assert.Equal([true], calls);

        // The next press asks again.
        boost.Hold();
        Assert.Equal([true, true], calls);
        Assert.Equal(2, boost.Requests);
    }

    [Fact]
    public void WindowsAcceptsABalancedBoostAndRelease()
    {
        // DCompositionBoostCompositorClock itself (Windows 11): a boost and its end succeed whether or not the display has
        // Dynamic refresh rate, and an end without a boost is refused, which is why the holds are counted.
        var boost = new CompositorBoost();
        boost.Hold();
        Assert.True(boost.IsBoosted, boost.Failure?.Message);
        boost.Release();
        Assert.False(boost.IsBoosted);
        Assert.Null(boost.Failure);
    }
}
