using Annotator.Core.Motion;
using Annotator.Workspace.Motion;

namespace Annotator.Workspace.Tests.Motion;

/// <summary>The Toolbar &amp; Shortcuts page's motion against the Mac's springs, curves and shake formula (ADR 0012).</summary>
public sealed class ShortcutEditorMotionTests
{
    [Fact]
    public void ReduceMotionDropsLiftPulseBounceAndShake()
    {
        Assert.Null(ShortcutEditorMotion.Settle(reduceMotion: true));
        Assert.Null(ShortcutEditorMotion.ChipBounce(reduceMotion: true));
        Assert.Null(ShortcutEditorMotion.ButtonBounce(reduceMotion: true));
        Assert.Null(ShortcutEditorMotion.Shake(5, reduceMotion: true));
        Assert.Null(ShortcutEditorMotion.Hover(reduceMotion: true));
        // Reorders, toasts and badges become Motion.reduced's short ease-out.
        Assert.Equal(MotionTiming.Reduced.Duration, ShortcutEditorMotion.Reorder(reduceMotion: true).Duration, 6);
        Assert.Equal(MotionTiming.Reduced.Duration, ShortcutEditorMotion.Toast(reduceMotion: true).Duration, 6);
    }

    [Fact]
    public void ShakeSwingsThreeTimesAndComesToRest()
    {
        MotionSamples shake = ShortcutEditorMotion.Shake(6, reduceMotion: false)!;
        Assert.Equal(0.42, shake.Duration, 6);
        Assert.Equal(0, shake.Values[0]);
        Assert.Equal(0, shake.Values[^1]);
        int crossings = 0;
        double previous = 0;
        foreach (double value in shake.Values.Where(value => Math.Abs(value) > 1e-6))
        {
            crossings += previous != 0 && Math.Sign(value) != Math.Sign(previous) ? 1 : 0;
            previous = value;
        }
        Assert.Equal(5, crossings);
        Assert.InRange(shake.Values.Max(), 4.5, 6);
        double time = shake.Times[4], progress = time / 0.42;
        Assert.Equal(6 * (1 - progress) * Math.Sin(6 * Math.PI * progress), shake.Values[4], 9);
        Assert.Null(ShortcutEditorMotion.Shake(5, reduceMotion: true));
    }

    [Fact]
    public void BouncesRiseAndSettleOnTheMacSprings()
    {
        MotionSamples chip = ShortcutEditorMotion.ChipBounce(reduceMotion: false)!;
        Assert.Equal(1, chip.Values[0]);
        Assert.Equal(1, chip.Values[^1]);
        double peak = chip.Values.Max();
        Assert.InRange(peak, 1.2, 1.3);
        double expected = 1 + 0.22 * new SpringMotion(0.16, 0.5).Value(0.1);
        Assert.Equal(expected, chip.At(0.1), 3);
        Assert.InRange(chip.Duration, 0.15 + new SpringMotion(0.34, 0.6).SettleTime() - 0.01, 0.15 + new SpringMotion(0.34, 0.6).SettleTime() + 0.01);

        MotionSamples button = ShortcutEditorMotion.ButtonBounce(reduceMotion: false)!;
        Assert.InRange(button.Values.Max(), 1.07, 1.12);
        Assert.Equal(1, button.Values[^1]);
        for (int index = 1; index < button.Times.Count; index++)
        {
            Assert.True(button.Times[index] > button.Times[index - 1]);
        }
    }

    [Fact]
    public void PulseAndSettleUseTheMacTimings()
    {
        Assert.Equal(1.1, ShortcutEditorMotion.Pulse.Duration, 6);
        Assert.Equal(MotionCurves.EaseOut.Progress(0.5), ShortcutEditorMotion.Pulse.At(0.55), 3);
        MotionSamples settle = ShortcutEditorMotion.Settle(reduceMotion: false)!;
        Assert.Equal(MotionTiming.Snappy.SettleTime(), settle.Duration, 6);
        Assert.Equal(MotionTiming.Snappy.Value(0.1), settle.At(0.1), 3);
        Assert.Equal(MotionTiming.Smooth.SettleTime(), ShortcutEditorMotion.Ring(reduceMotion: false).Duration, 6);
        Assert.Equal(MotionTiming.FadeIn.Duration, ShortcutEditorMotion.FadeIn.Duration, 6);
    }
}
