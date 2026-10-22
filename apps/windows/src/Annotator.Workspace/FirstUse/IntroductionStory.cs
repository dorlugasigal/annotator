using Annotator.Core.Motion;

namespace Annotator.Workspace.FirstUse;

/// <summary>Port of the Mac's IntroductionEase: the small easing helpers the tour's stories use.</summary>
public static class IntroductionEase
{
    public static double Clamp(double value) => Math.Min(1, Math.Max(0, value));

    /// <summary>Progress from <paramref name="start"/> to <paramref name="end"/>, from 0 to 1.</summary>
    public static double Segment(double progress, double start, double end) => Clamp((progress - start) / (end - start));

    public static double Smooth(double t) => t * t * (3 - 2 * t);

    public static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    public static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>A damped settle that overshoots by under 3 percent.</summary>
    public static double Settle(double t) => t >= 1 ? 1 : 1 - Math.Exp(-7.5 * t) * Math.Cos(6.5 * t);

    /// <summary>One soft bump from 0 to 1 and back. Never repeats.</summary>
    public static double Pulse(double progress, double start, double width)
    {
        double t = Segment(progress, start, start + width);
        return t <= 0 || t >= 1 ? 0 : Math.Sin(Math.PI * t);
    }

    public static double Mix(double a, double b, double t) => a + (b - a) * t;
}

/// <summary>Port of the Mac's IntroductionStory: timings shared by the illustrations and the rows that play in step with them.</summary>
public static class IntroductionStory
{
    /// <summary>When each shortcut row on the first page is pressed, as story progress.</summary>
    public static IReadOnlyList<IReadOnlyList<double>> ShortcutPresses { get; } = [[0.02], [0.43], [0.735, 0.835]];

    /// <summary>
    /// Where a story starts: frozen at <paramref name="review"/> for review frames; at its end with Reduce Motion or while the
    /// tour is hidden; otherwise at the beginning.
    /// </summary>
    public static double InitialProgress(bool reduceMotion, bool presented, double? review)
    {
        if (review is { } frozen)
        {
            return IntroductionEase.Clamp(frozen);
        }
        return reduceMotion || !presented ? 1 : 0;
    }

    /// <summary>The highlighted shortcut row: a continuous row position (0 to 2) and its opacity.</summary>
    public static (double Position, double Opacity) ShortcutFocus(double progress)
    {
        double toUseApps = IntroductionEase.EaseInOut(IntroductionEase.Segment(progress, 0.40, 0.45));
        double toShowHide = IntroductionEase.EaseInOut(IntroductionEase.Segment(progress, 0.70, 0.75));
        double opacity = IntroductionEase.Smooth(IntroductionEase.Segment(progress, 0, 0.05)) *
            (1 - IntroductionEase.Smooth(IntroductionEase.Segment(progress, 0.95, 1)));
        return (toUseApps + toShowHide, opacity);
    }

    /// <summary>How far a shortcut row is pressed, from 0 to 1.</summary>
    public static double ShortcutPress(int row, double progress)
    {
        double press = 0;
        foreach (double start in ShortcutPresses[row])
        {
            press = Math.Max(press, IntroductionEase.Pulse(progress, start, 0.05));
        }
        return press;
    }

    /// <summary>The check of a ready card draws once its card has settled.</summary>
    public static double CheckDraw(int index, double progress)
    {
        double start = 0.45 + index * 0.2;
        return IntroductionEase.EaseOut(IntroductionEase.Segment(progress, start, start + 0.3));
    }

    /// <summary>Seconds after the page appears before a ready card's check starts to draw.</summary>
    public static double CheckDelay(int index, double timeScale) =>
        (IntroductionMotion.StoryDelay + IntroductionPage.Setup.StoryDuration * (0.45 + index * 0.2)) * timeScale;

    /// <summary>The setup page's cards settle in one after another: opacity, downward offset in points, and scale.</summary>
    public static (double Opacity, double Offset, double Scale) CardArrival(int index, double progress)
    {
        double start = 0.05 + index * 0.2;
        double t = IntroductionEase.Segment(progress, start, start + 0.55);
        double settle = IntroductionEase.Settle(t);
        return (0.35 + 0.65 * IntroductionEase.Smooth(Math.Min(1, t * 1.6)), (1 - settle) * 14, 0.985 + 0.015 * Math.Min(1, settle));
    }

    /// <summary>The setup page's sharing note fades from 35% to full as the cards settle.</summary>
    public static double SharingNoteOpacity(double progress) =>
        0.35 + 0.65 * IntroductionEase.Smooth(IntroductionEase.Segment(progress, 0.45, 0.9));
}

/// <summary>
/// Port of the Mac's IntroductionMotion and the step indicator's springs. SwiftUI's spring(duration:bounce: 0) is the
/// critically damped <see cref="SpringMotion"/> with that response.
/// </summary>
public static class IntroductionMotion
{
    /// <summary>Seconds between a page appearing and its story starting.</summary>
    public const double StoryDelay = 0.3;

    /// <summary>How far a new page travels in, in points; a leaving page travels half as far.</summary>
    public const double PageOffset = 28;

    /// <summary>The leaving and entering page's blur radius at the far end of their travel.</summary>
    public const double PageBlur = 2;

    /// <summary>The new page starts after the old one has started to clear.</summary>
    public const double InsertionDelay = 0.1;

    /// <summary>The old page clears with an ease-out over this many seconds.</summary>
    public const double RemovalDuration = 0.14;

    /// <summary>With Reduce Motion, pages crossfade with an ease-in-out over this many seconds.</summary>
    public const double ReducedDuration = 0.2;

    /// <summary>The step capsule's leading edge moves first; the trailing edge follows after this delay.</summary>
    public const double CapsuleFollowDelay = 0.07;

    /// <summary>The spacing between step tracks, in points.</summary>
    public const double StepSpacing = 8;

    /// <summary>The page's spring: SwiftUI spring(duration: 0.5, bounce: 0).</summary>
    public static SpringMotion Page { get; } = new(0.5, 1);

    /// <summary>The step capsule's leading edge: spring(duration: 0.32, bounce: 0).</summary>
    public static SpringMotion CapsuleLead { get; } = new(0.32, 1);

    /// <summary>The step capsule's trailing edge: spring(duration: 0.5, bounce: 0), 0.07 s later.</summary>
    public static SpringMotion CapsuleFollow { get; } = new(0.5, 1);

    /// <summary>The removal's ease-out (SwiftUI easeOut).</summary>
    public static CubicBezier RemovalCurve => MotionCurves.EaseOut;

    /// <summary>The reduced crossfade (SwiftUI easeInOut).</summary>
    public static CubicBezier ReducedCurve => MotionCurves.EaseInEaseOut;

    /// <summary>
    /// The step capsule over the step tracks (Mac IntroductionStepCapsule): from the start of the lower step to the end of
    /// the upper one, in points from the indicator's left edge.
    /// </summary>
    public static (double Start, double Width) Capsule(double lower, double upper, int count, double width, double spacing = StepSpacing)
    {
        double segment = (width - spacing * (count - 1)) / count;
        double start = Math.Min(lower, upper) * (segment + spacing);
        double end = Math.Max(lower, upper) * (segment + spacing) + segment;
        return (start, end - start);
    }

    /// <summary>The illustration's replay mark fades with the pointer over 0.15 s (SwiftUI easeOut).</summary>
    public const double ReplayHintFade = 0.15;

    /// <summary>A ready card's check draws over 0.38 s with an ease-out once its card has settled.</summary>
    public const double CheckDuration = 0.38;
}
