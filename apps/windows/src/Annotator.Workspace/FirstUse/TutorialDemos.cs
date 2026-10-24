using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Tutorial;

namespace Annotator.Workspace.FirstUse;

/// <summary>Where the ghost goes: a toolbar control by its accessible name, a place in the work area, or a point on the display.</summary>
public enum TutorialSpotKind
{
    /// <summary>The center of the toolbar control named <see cref="TutorialSpot.Anchor"/>.</summary>
    Anchor,

    /// <summary><see cref="TutorialSpot.Value"/> is a fraction of the display's work area, from its top-left corner.</summary>
    WorkArea,

    /// <summary><see cref="TutorialSpot.Value"/> is in points from the display's top-left corner.</summary>
    Display,
}

public readonly record struct TutorialSpot(TutorialSpotKind Kind, string? Anchor, Point Value)
{
    public static TutorialSpot Control(string name) => new(TutorialSpotKind.Anchor, name, default);

    public static TutorialSpot Work(double x, double y) => new(TutorialSpotKind.WorkArea, null, new Point(x, y));

    public static TutorialSpot OnDisplay(Point point) => new(TutorialSpotKind.Display, null, point);
}

/// <summary>What the ghost's ink shows while it demonstrates.</summary>
public enum TutorialInk
{
    None,

    /// <summary>A rectangle outline from where the drag started to the ghost.</summary>
    Box,

    /// <summary>The line the ghost has drawn so far.</summary>
    Trail,

    /// <summary>A smooth curve through the points the ghost clicked.</summary>
    Curve,
}

/// <summary>One action of a demonstration (Swift <c>TutorialDemoAction</c>).</summary>
public abstract record TutorialDemoAction;

/// <summary>The card's keycaps go down one after another.</summary>
public sealed record TutorialDemoKeys(ImmutableArray<string> Keys) : TutorialDemoAction;

/// <summary>The ghost travels to <paramref name="Target"/> and clicks; with <see cref="TutorialInk.Curve"/> the point joins the curve.</summary>
public sealed record TutorialDemoClick(TutorialSpot Target, TutorialInk Ink = TutorialInk.None) : TutorialDemoAction;

/// <summary>The ghost travels to <paramref name="From"/>, presses, drags in a straight line to <paramref name="To"/> and lets go.</summary>
public sealed record TutorialDemoDrag(TutorialSpot From, TutorialSpot To, TutorialInk Ink) : TutorialDemoAction;

/// <summary>The ghost draws a loop around <paramref name="Center"/> with the pen (<see cref="TutorialMotion.Loop"/>).</summary>
public sealed record TutorialDemoLoop(TutorialSpot Center, double Radius) : TutorialDemoAction;

/// <summary>
/// The ghost's demonstrations, one per goal (Mac TutorialDemos). Each shows the key first, then the click, so both ways are
/// visible. The toolbar names are the controls' accessible names.
/// </summary>
public static class TutorialDemos
{
    public const string PenControl = "Pen";
    public const string RectangleControl = "Rectangle";
    public const string ArrowControl = "Arrow";
    public const string SelectControl = "Select";
    public const string StrokeColorControl = "Stroke color";
    public const string CurvedControl = "Curved arrow";
    public const string UndoControl = "Undo";
    public const string UseAppsControl = "Use apps";
    public const string HideControl = "Hide drawings and tools";

    /// <summary>The toolbar control that the target ring shows for <paramref name="goal"/>, or null.</summary>
    public static string? Target(TutorialGoal goal) => goal switch
    {
        TutorialGoal.Drawing => PenControl,
        TutorialGoal.RectangleTool => RectangleControl,
        TutorialGoal.ColorChanged => StrokeColorControl,
        TutorialGoal.ArrowTool => ArrowControl,
        TutorialGoal.CurvedStyle => CurvedControl,
        TutorialGoal.PenTool => PenControl,
        TutorialGoal.SelectTool => SelectControl,
        TutorialGoal.Undone => UndoControl,
        TutorialGoal.UsingApps => UseAppsControl,
        TutorialGoal.Hidden => HideControl,
        _ => null,
    };

    /// <summary>
    /// The demonstration for <paramref name="goal"/>. <paramref name="newest"/> is where the move demo presses on the newest
    /// drawing on the ghost's display, in points (<see cref="TutorialObserver.NewestGrip"/>).
    /// </summary>
    public static ImmutableArray<TutorialDemoAction> For(TutorialGoal goal, WorkspaceSnapshot snapshot, Point? newest)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<string> keys = TutorialText.Keys(goal, snapshot);
        ImmutableArray<TutorialDemoAction>.Builder actions = ImmutableArray.CreateBuilder<TutorialDemoAction>();
        if (!keys.IsEmpty && goal != TutorialGoal.CurvedArrowDrawn)
        {
            actions.Add(new TutorialDemoKeys(keys));
        }
        switch (goal)
        {
            case TutorialGoal.ShapeDrawn:
                actions.Add(new TutorialDemoDrag(TutorialSpot.Work(0.34, 0.42), TutorialSpot.Work(0.48, 0.58), TutorialInk.Box));
                break;
            case TutorialGoal.CurvedArrowDrawn:
                actions.Add(new TutorialDemoClick(TutorialSpot.Work(0.56, 0.62), TutorialInk.Curve));
                actions.Add(new TutorialDemoClick(TutorialSpot.Work(0.64, 0.46), TutorialInk.Curve));
                actions.Add(new TutorialDemoClick(TutorialSpot.Work(0.74, 0.54), TutorialInk.Curve));
                actions.Add(new TutorialDemoKeys(keys));
                break;
            case TutorialGoal.PenDrawn:
                actions.Add(new TutorialDemoLoop(TutorialSpot.Work(0.30, 0.62), 56));
                break;
            case TutorialGoal.Moved:
                if (newest is { } grip)
                {
                    actions.Add(new TutorialDemoDrag(TutorialSpot.OnDisplay(grip), TutorialSpot.OnDisplay(grip + new Point(120, 40)), TutorialInk.None));
                }
                else
                {
                    actions.Add(new TutorialDemoDrag(TutorialSpot.Work(0.41, 0.50), TutorialSpot.Work(0.49, 0.55), TutorialInk.None));
                }
                break;
            case TutorialGoal.Shown:
                break;
            default:
                if (Target(goal) is { } control)
                {
                    actions.Add(new TutorialDemoClick(TutorialSpot.Control(control)));
                }
                break;
        }
        return actions.ToImmutable();
    }
}
