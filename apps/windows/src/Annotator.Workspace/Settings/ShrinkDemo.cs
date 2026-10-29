using Annotator.Core.Motion;

namespace Annotator.Workspace.Settings;

/// <summary>
/// Port of ShrinkDemoPose (ToolbarShrinkDemo.swift): one moment of the Shrink demo's loop in Settings. The pointer leaves
/// the toolbar, the toolbar turns into one button with its own morph (<see cref="ToolbarMorph"/>), the pointer comes back
/// and the tools open at once. Each loop starts with a still second.
/// </summary>
/// <param name="Shrink">How far the background has gone from the toolbar's shape (0) to the one button's (1).</param>
/// <param name="PointerX">The pointer's tip, as a share of the stage's width.</param>
/// <param name="PointerY">The pointer's tip, as a share of the stage's height.</param>
public readonly record struct ShrinkDemoPose(double Shrink, double ToolbarOpacity, double ButtonOpacity, double PointerX, double PointerY)
{
    public const double OnToolbarX = 0.53, OnToolbarY = 0.4, AwayX = 0.84, AwayY = 0.86;

    /// <summary>The still start, the pointer leaving, the wait, the shrink, the pointer coming back and the toolbar opening.</summary>
    public const double Leave = 1.0, Wait = 1.6, Collapse = 2.4, Rest = Collapse + MotionTiming.ToolbarCollapseDuration;

    public const double Back = 3.6, Expand = 4.2, Opened = Expand + MotionTiming.ToolbarExpandDuration, Period = 6.0;

    public static ShrinkDemoPose Open { get; } = new(0, 1, 0, AwayX, AwayY);

    public static ShrinkDemoPose Shrunk { get; } = new(1, 0, 1, AwayX, AwayY);

    /// <summary>The demo <paramref name="elapsed"/> seconds after it appeared.</summary>
    public static ShrinkDemoPose At(double elapsed)
    {
        double time = Math.Max(0, elapsed) % Period;
        double Share(double from, double to) => Math.Clamp((time - from) / (to - from), 0, 1);
        ShrinkDemoPose Move(ShrinkDemoPose pose, (double X, double Y) from, (double X, double Y) to, double share)
        {
            double progress = MotionCurves.Standard.Progress(share);
            return pose with { PointerX = from.X + (to.X - from.X) * progress, PointerY = from.Y + (to.Y - from.Y) * progress };
        }

        (double X, double Y) onToolbar = (OnToolbarX, OnToolbarY), away = (AwayX, AwayY);
        if (time < Leave)
        {
            return Open with { PointerX = OnToolbarX, PointerY = OnToolbarY };
        }
        if (time < Wait)
        {
            return Move(Open, onToolbar, away, Share(Leave, Wait));
        }
        if (time < Collapse)
        {
            return Open;
        }
        if (time < Rest)
        {
            double t = Share(Collapse, Rest);
            return new(ToolbarMorph.Shape(t), ToolbarMorph.OutgoingOpacity(t), ToolbarMorph.IncomingOpacity(t), AwayX, AwayY);
        }
        if (time < Back)
        {
            return Shrunk;
        }
        if (time < Expand)
        {
            return Move(Shrunk, away, onToolbar, Share(Back, Expand));
        }
        if (time < Opened)
        {
            double t = Share(Expand, Opened);
            return new(1 - ToolbarMorph.Shape(t), ToolbarMorph.IncomingOpacity(t), ToolbarMorph.OutgoingOpacity(t), OnToolbarX, OnToolbarY);
        }
        return Open with { PointerX = OnToolbarX, PointerY = OnToolbarY };
    }
}

/// <summary>The Shrink demo's words in Settings (Mac ToolbarShrinkDemo and shrinkCaption).</summary>
public static class ShrinkDemoText
{
    public const string Name = "Shrink preview";

    /// <summary>What the demo shows, its help text (Mac accessibilityHint).</summary>
    public const string Help =
        "The toolbar turns into one button after the pointer leaves it, and opens as soon as the pointer is back.";

    public const string Identifier = "toolbar-shrink-demo";

    /// <summary>The caption beside the demo, for the delay chosen in Shrink, or none.</summary>
    public static string Caption(double? delay) => delay is { } seconds
        ? $"Leave the toolbar alone for {seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} seconds and it shrinks to one button that shows Draw or Apps. Point at it and your tools are back."
        : "Pick a time in Shrink and the toolbar tucks into one button when you leave it. Point at the button and your tools are back.";
}
