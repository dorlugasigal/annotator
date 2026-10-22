using Annotator.Core.Motion;

namespace Annotator.Workspace.Motion;

/// <summary>
/// The Toolbar &amp; Shortcuts page's motion (ToolbarShortcutSettings.swift, ADR 0012), as timelines the WinUI page plays
/// with composition keyframes: a lifted drag item, neighbours that slide, a settle into the drop slot, the listening pulse
/// of a recording key, the bounce of a newly set key and the damped shake of a refused one. With Reduce Motion (Windows:
/// Animation effects off) the page drops lift, tilt, pulse, bounce and shake and uses short fades.
/// </summary>
public static class ShortcutEditorMotion
{
    /// <summary>A dragged item's scale and tilt (Mac floatingItem: 1.1 and ±2 degrees, toward the toolbar while over it).</summary>
    public const double LiftScale = 1.1;

    public const double LiftTilt = 2;

    /// <summary>A dragged item's shadow: black at 25%, 14 points of blur, 8 points down.</summary>
    public const double LiftShadowOpacity = 0.25;

    public const double LiftShadowRadius = 14;

    public const double LiftShadowOffset = 8;

    /// <summary>How long the dropped item settles before it goes (Mac settle task: 260 ms).</summary>
    public const double SettleDuration = 0.26;

    /// <summary>The listening ring: from the key's outline to 1.45 times its size, from 70% opacity to nothing, every 1.1 s.</summary>
    public const double PulseScale = 1.45;

    public const double PulseOpacity = 0.7;

    /// <summary>A key chip's scale while the pointer is over it (Mac KeyChip hovering, ease-out over 0.12 s).</summary>
    public const double HoverScale = 1.06;

    /// <summary>The pulse: Mac .easeOut(duration: 1.1).repeatForever(autoreverses: false), as progress from 0 to 1.</summary>
    public static MotionSamples Pulse { get; } = ToolbarControlMotion.Timed(new TimedMotion(1.1, MotionCurves.EaseOut), 0, 1);

    /// <summary>Mac reorder (Motion.resolve(Motion.snappy, reduceMotion)): the slide of neighbours and other toolbar changes.</summary>
    public static MotionSamples Reorder(bool reduceMotion) => ToolbarControlMotion.Selection(reduceMotion);

    /// <summary>The drop's settle on the snappy spring; null with Reduce Motion, where the item goes at once.</summary>
    public static MotionSamples? Settle(bool reduceMotion) => reduceMotion ? null : ToolbarControlMotion.Spring(MotionTiming.Snappy, 0, 1);

    /// <summary>The key chip's hover: ease-out over 0.12 s; null with Reduce Motion, where the scale changes at once.</summary>
    public static MotionSamples? Hover(bool reduceMotion) =>
        reduceMotion ? null : ToolbarControlMotion.Timed(new TimedMotion(0.12, MotionCurves.EaseOut), 0, 1);

    /// <summary>
    /// A key chip's bounce when it gets a key (Mac KeyChip onChange): spring(response 0.16, damping 0.5) toward 1.22, then
    /// after 150 ms spring(response 0.34, damping 0.6) back to 1. Null with Reduce Motion.
    /// </summary>
    public static MotionSamples? ChipBounce(bool reduceMotion) =>
        reduceMotion ? null : Bounce(new SpringMotion(0.16, 0.5), 1.22, 0.15, new SpringMotion(0.34, 0.6));

    /// <summary>
    /// The global shortcut button's bounce (Mac GlobalKeyButton onChange): spring(0.18, 0.5) toward 1.08, then after 160 ms
    /// spring(0.34, 0.65) back to 1. Null with Reduce Motion.
    /// </summary>
    public static MotionSamples? ButtonBounce(bool reduceMotion) =>
        reduceMotion ? null : Bounce(new SpringMotion(0.18, 0.5), 1.08, 0.16, new SpringMotion(0.34, 0.65));

    /// <summary>
    /// Mac ShakeEffect over .linear(duration: 0.42): an offset of travel × (1 − p) × sin(6πp) as p runs from 0 to 1, three
    /// damped swings. Travel is 5 points for a key chip and 6 for a global shortcut button. Null with Reduce Motion.
    /// </summary>
    public static MotionSamples? Shake(double travel, bool reduceMotion)
    {
        if (reduceMotion)
        {
            return null;
        }
        const double duration = 0.42;
        int count = (int)Math.Ceiling(duration * ToolbarControlMotion.SampleRate) + 1;
        var times = new double[count];
        var values = new double[count];
        for (int index = 0; index < count; index++)
        {
            double time = Math.Min(duration, index / ToolbarControlMotion.SampleRate);
            double progress = time / duration;
            times[index] = time;
            values[index] = index == count - 1 ? 0 : travel * (1 - progress) * Math.Sin(progress * Math.PI * 6);
        }
        return new MotionSamples(times, values);
    }

    /// <summary>
    /// The toast's entrance and exit (Mac .animation(Motion.resolve(Motion.snappy, reduceMotion))): the snappy spring for a
    /// move from below combined with a fade, or Motion.reduced's fade alone with Reduce Motion.
    /// </summary>
    public static MotionSamples Toast(bool reduceMotion) => ToolbarControlMotion.Selection(reduceMotion);

    /// <summary>A hover badge's appearance: scale from 0.3 with a fade on the snappy spring; a fade alone with Reduce Motion.</summary>
    public static MotionSamples Badge(bool reduceMotion) => ToolbarControlMotion.Selection(reduceMotion);

    /// <summary>The size picker's ring moving to the chosen card (Motion.smooth), or Motion.reduced's fade.</summary>
    public static MotionSamples Ring(bool reduceMotion) =>
        reduceMotion ? ToolbarControlMotion.Timed(MotionTiming.Reduced, 0, 1) : ToolbarControlMotion.Spring(MotionTiming.Smooth, 0, 1);

    /// <summary>The tray's warning colors and words fading in (Motion.fadeIn).</summary>
    public static MotionSamples FadeIn { get; } = ToolbarControlMotion.Timed(MotionTiming.FadeIn, 0, 1);

    /// <summary>
    /// Two springs one after the other: the first runs from 1 toward <paramref name="peak"/> for <paramref name="hold"/>
    /// seconds, then the second returns from where the first got to. SwiftUI keeps the first spring's speed at the hand-off;
    /// these samples start the second spring from rest.
    /// </summary>
    private static MotionSamples Bounce(SpringMotion up, double peak, double hold, SpringMotion down)
    {
        int rising = (int)Math.Ceiling(hold * ToolbarControlMotion.SampleRate);
        double settle = down.SettleTime();
        int falling = Math.Max(2, (int)Math.Ceiling(settle * ToolbarControlMotion.SampleRate) + 1);
        var times = new double[rising + falling];
        var values = new double[rising + falling];
        for (int index = 0; index < rising; index++)
        {
            double time = index / ToolbarControlMotion.SampleRate;
            times[index] = time;
            values[index] = 1 + (peak - 1) * up.Value(time);
        }
        double reached = 1 + (peak - 1) * up.Value(hold);
        for (int index = 0; index < falling; index++)
        {
            double time = Math.Min(settle, index / ToolbarControlMotion.SampleRate);
            times[rising + index] = hold + time;
            values[rising + index] = index == falling - 1 ? 1 : reached + (1 - reached) * down.Value(time);
        }
        return new MotionSamples(times, values);
    }
}
