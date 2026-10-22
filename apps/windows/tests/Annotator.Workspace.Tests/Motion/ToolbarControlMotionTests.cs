using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.Workspace.Motion;
using static Annotator.Workspace.Tests.TestWorkspace;
using Style = Annotator.Core.VisibilityAnimationStyle;

namespace Annotator.Workspace.Tests.Motion;

/// <summary>
/// The toolbar's own control motion (Mac ToolbarView selectionMarker, modeButton and selectionAnimation): the timelines the
/// WinUI toolbar plays, against the shared springs and curves of Motion.swift.
/// </summary>
public sealed class ToolbarControlMotionTests
{
    [Fact]
    public void SelectionFollowsTheSnappySpringUntilItSettles()
    {
        MotionSamples slide = ToolbarControlMotion.Selection(reduceMotion: false);
        Assert.Equal(MotionTiming.Snappy.SettleTime(), slide.Duration, 9);
        Assert.Equal(0, slide.Values[0]);
        Assert.Equal(1, slide.Values[^1]);
        for (var step = 0; step < slide.Times.Count - 1; step++)
        {
            Assert.Equal(MotionTiming.Snappy.Value(slide.Times[step]), slide.Values[step], 12);
            Assert.True(slide.Times[step + 1] > slide.Times[step]);
            Assert.True(slide.Times[step + 1] - slide.Times[step] <= 1 / ToolbarControlMotion.SampleRate + 1e-12);
        }
        // The pen to arrow slide in reference/ux-reference/macos-live/sharing-live lands in about 0.15 s.
        Assert.InRange(slide.At(0.15), 0.9, 0.95);
        Assert.InRange(slide.Duration, 0.25, 0.4);
    }

    [Fact]
    public void ReduceMotionUsesTheReducedEaseOut()
    {
        MotionSamples slide = ToolbarControlMotion.Selection(reduceMotion: true);
        Assert.Equal(MotionTiming.Reduced.Duration, slide.Duration, 12);
        for (var step = 0; step < slide.Times.Count; step++)
        {
            Assert.Equal(MotionCurves.EaseOut.Progress(slide.Times[step] / MotionTiming.Reduced.Duration), slide.Values[step], 12);
        }
        Assert.Null(ToolbarControlMotion.DotPulse(reduceMotion: true));
        Assert.Null(ToolbarControlMotion.SymbolOut(reduceMotion: true));
        Assert.Null(ToolbarControlMotion.SymbolIn(reduceMotion: true));
    }

    [Fact]
    public void DotPulsesUpThenSpringsBack()
    {
        // phaseAnimator([1.0, 1.6, 1.0]): easeOut(duration: 0.12) towards 1.6, Motion.playful back to 1.
        MotionSamples pulse = ToolbarControlMotion.DotPulse(reduceMotion: false)!;
        Assert.Equal(1, pulse.Values[0]);
        Assert.Equal(1.6, pulse.At(0.12), 12);
        Assert.Equal(1 + 0.6 * MotionCurves.EaseOut.Progress(0.05 / 0.12), pulse.At(0.05), 12);
        Assert.Equal(1.6 - 0.6 * MotionTiming.Playful.Value(0.1), pulse.At(0.22), 3);
        Assert.Equal(0.12 + MotionTiming.Playful.SettleTime(), pulse.Duration, 9);
        Assert.Equal(1, pulse.Values[^1]);
        // The playful spring dips below 1 once (about 8%) before it settles.
        Assert.InRange(pulse.Values.Min(), 0.9, 0.99);
        Assert.Equal(1.6, pulse.Values.Max(), 12);
    }

    [Fact]
    public void SymbolIsReplacedAfterAPause()
    {
        MotionSamples leaving = ToolbarControlMotion.SymbolOut(reduceMotion: false)!;
        MotionSamples arriving = ToolbarControlMotion.SymbolIn(reduceMotion: false)!;
        var swapped = ToolbarControlMotion.SymbolHold + ToolbarControlMotion.SymbolSwap;
        Assert.Equal(1, leaving.At(ToolbarControlMotion.SymbolHold));
        Assert.Equal(0, leaving.At(swapped));
        Assert.Equal(0, arriving.At(swapped));
        Assert.Equal(MotionTiming.Snappy.Value(0.05), arriving.At(swapped + 0.05), 3);
        Assert.Equal(1, arriving.Values[^1]);
        Assert.True(arriving.Values.Max() <= 1);
        Assert.Equal(swapped + MotionTiming.Snappy.SettleTime(), arriving.Duration, 9);
    }

    [Fact]
    public void SamplesInterpolateLinearlyAndHoldOutside()
    {
        var samples = new MotionSamples([0, 0.1, 0.3], [2, 4, 0]);
        Assert.Equal(2, samples.At(-1));
        Assert.Equal(3, samples.At(0.05), 12);
        Assert.Equal(2, samples.At(0.2), 12);
        Assert.Equal(0, samples.At(1));
        Assert.Throws<ArgumentException>(() => new MotionSamples([0.1, 0.2], [0, 1]));
        Assert.Throws<ArgumentException>(() => new MotionSamples([0], [0]));
    }

    [Fact]
    public void ControlsMoveOnlyWhileTheToolbarRestsOnScreen()
    {
        // Mac selectionAnimation: nil while workspaceHidden or visibilityAnimating.
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [PrimaryDisplay],
            Pointer = () => new Point(10, 10),
            PreferencesStore = new MemoryPreferencesStore(new WorkspacePreferences { VisibilityAnimationStyle = Style.Slide }.Data()),
            AnimatesVisibility = true,
        });
        model.ResumeDrawing();
        Assert.True(ToolbarControlMotion.Animates(model.Snapshot));
        model.UseApps();
        Assert.True(ToolbarControlMotion.Animates(model.Snapshot));
        model.HideTools();
        Assert.False(ToolbarControlMotion.Animates(model.Snapshot));
        model.CompleteVisibilityTransition(model.Snapshot.Visibility!.Serial);
        Assert.False(ToolbarControlMotion.Animates(model.Snapshot));
        model.ShowToolbar();
        Assert.False(ToolbarControlMotion.Animates(model.Snapshot));
        model.CompleteVisibilityTransition(model.Snapshot.Visibility!.Serial);
        Assert.True(ToolbarControlMotion.Animates(model.Snapshot));
    }
}
