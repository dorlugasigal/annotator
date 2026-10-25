using System.Collections.Immutable;

namespace Annotator.Core.Tutorial;

/// <summary>
/// The hands-on tutorial's steps in order (Swift <c>TutorialStep</c>). Raw values are stable identifiers shared with the Mac.
/// Start tutorial turns drawing on, so the first step picks a tool; Draw again teaches the drawing shortcut later.
/// </summary>
public enum TutorialStep
{
    Rectangle,
    Shape,
    Color,
    Arrow,
    Pen,
    Move,
    Undo,
    UseApps,
    DrawAgain,
    Hide,
}

/// <summary>
/// One thing a step waits for (Swift <c>TutorialGoal</c>). A step has one to three goals, done in order. Every goal reads the
/// workspace's state, never how the person got there, so a key and a click both count.
/// </summary>
public enum TutorialGoal
{
    /// <summary>Drawing is on.</summary>
    Drawing,

    /// <summary>The Rectangle is the tool.</summary>
    RectangleTool,

    /// <summary>A finished shape that was not there when the goal started.</summary>
    ShapeDrawn,

    /// <summary>The stroke color differs from the goal's start, with the same tool.</summary>
    ColorChanged,

    /// <summary>The Arrow is the tool.</summary>
    ArrowTool,

    /// <summary>The Arrow is the tool and draws curved.</summary>
    CurvedStyle,

    /// <summary>A new curved arrow with at least three points.</summary>
    CurvedArrowDrawn,

    /// <summary>The Pen or Highlighter is the tool.</summary>
    PenTool,

    /// <summary>A new freehand drawing, or a Smart Draw shape made while the Pen is the tool.</summary>
    PenDrawn,

    /// <summary>Select is the tool.</summary>
    SelectTool,

    /// <summary>A drawing that was there moved, resized or turned, while Select is the tool.</summary>
    Moved,

    /// <summary>Undo ran.</summary>
    Undone,

    /// <summary>Use apps: drawings stay visible and apps get the clicks.</summary>
    UsingApps,

    /// <summary>Drawings and tools are hidden (or hiding).</summary>
    Hidden,

    /// <summary>Drawings and tools are shown (or showing) again.</summary>
    Shown,
}

public static class TutorialSteps
{
    /// <summary>Every step in order.</summary>
    public static ImmutableArray<TutorialStep> All { get; } =
    [
        TutorialStep.Rectangle, TutorialStep.Shape, TutorialStep.Color, TutorialStep.Arrow,
        TutorialStep.Pen, TutorialStep.Move, TutorialStep.Undo, TutorialStep.UseApps, TutorialStep.DrawAgain, TutorialStep.Hide,
    ];

    extension(TutorialStep step)
    {
        /// <summary>The Mac's raw value.</summary>
        public string RawValue => step switch
        {
            TutorialStep.Rectangle => "rectangle",
            TutorialStep.Shape => "shape",
            TutorialStep.Color => "color",
            TutorialStep.Arrow => "arrow",
            TutorialStep.Pen => "pen",
            TutorialStep.Move => "move",
            TutorialStep.Undo => "undo",
            TutorialStep.UseApps => "useApps",
            TutorialStep.DrawAgain => "drawAgain",
            TutorialStep.Hide => "hide",
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };

        /// <summary>The goals, in the order the step asks for them.</summary>
        public ImmutableArray<TutorialGoal> Goals => step switch
        {
            TutorialStep.Rectangle => [TutorialGoal.RectangleTool],
            TutorialStep.Shape => [TutorialGoal.ShapeDrawn],
            TutorialStep.Color => [TutorialGoal.ColorChanged],
            TutorialStep.Arrow => [TutorialGoal.ArrowTool, TutorialGoal.CurvedStyle, TutorialGoal.CurvedArrowDrawn],
            TutorialStep.Pen => [TutorialGoal.PenTool, TutorialGoal.PenDrawn],
            TutorialStep.Move => [TutorialGoal.SelectTool, TutorialGoal.Moved],
            TutorialStep.Undo => [TutorialGoal.Undone],
            TutorialStep.UseApps => [TutorialGoal.UsingApps],
            TutorialStep.DrawAgain => [TutorialGoal.Drawing],
            TutorialStep.Hide => [TutorialGoal.Hidden, TutorialGoal.Shown],
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };

        /// <summary>The step's position from 0.</summary>
        public int Index => (int)step;
    }

    extension(TutorialGoal goal)
    {
        /// <summary>The Mac's raw value.</summary>
        public string RawValue => goal switch
        {
            TutorialGoal.Drawing => "drawing",
            TutorialGoal.RectangleTool => "rectangleTool",
            TutorialGoal.ShapeDrawn => "shapeDrawn",
            TutorialGoal.ColorChanged => "colorChanged",
            TutorialGoal.ArrowTool => "arrowTool",
            TutorialGoal.CurvedStyle => "curvedStyle",
            TutorialGoal.CurvedArrowDrawn => "curvedArrowDrawn",
            TutorialGoal.PenTool => "penTool",
            TutorialGoal.PenDrawn => "penDrawn",
            TutorialGoal.SelectTool => "selectTool",
            TutorialGoal.Moved => "moved",
            TutorialGoal.Undone => "undone",
            TutorialGoal.UsingApps => "usingApps",
            TutorialGoal.Hidden => "hidden",
            TutorialGoal.Shown => "shown",
            _ => throw new ArgumentOutOfRangeException(nameof(goal)),
        };
    }
}

/// <summary>The workspace mode as the tutorial sees it (the Mac's Off, Interact and Draw).</summary>
public enum TutorialMode
{
    Off,
    UseApps,
    Draw,
}

/// <summary>What the tutorial needs to know about one drawing (Swift <c>TutorialElement</c>).</summary>
public readonly record struct TutorialElement(Guid Id, Tool Tool, bool Curved, int PointCount, Rect Frame, double Rotation)
{
    public static TutorialElement From(Element element) =>
        new(element.Id, element.Tool, element.Style.Curved, element.Points.Count, element.Frame, element.Rotation);

    /// <summary>Whether two looks at a drawing found it in the same place: the same frame and turn.</summary>
    public bool SamePlace(TutorialElement other) => Frame == other.Frame && Rotation == other.Rotation;
}

/// <summary>
/// One look at the workspace (Swift <c>TutorialObservation</c>): the mode, whether drawings and tools are presented, the tool
/// with its stroke color and route, every drawing on every display, how many times Undo ran and whether an edit is in progress.
/// </summary>
public sealed record TutorialObservation(
    TutorialMode Mode,
    bool Presented,
    Tool Tool,
    RGBA StrokeColor,
    bool Curved,
    ImmutableArray<TutorialElement> Elements,
    ulong UndoSerial,
    bool Editing);

/// <summary>What one observation changed.</summary>
public enum TutorialChange
{
    /// <summary>Nothing new.</summary>
    None,

    /// <summary>A goal of the current step is done; the step goes on.</summary>
    Goal,

    /// <summary>A step is done and the next one started. Several steps can finish in one observation.</summary>
    Step,

    /// <summary>The last step is done.</summary>
    Finished,
}

/// <summary>
/// The tutorial's progress (Swift <c>TutorialProgress</c>): the current step and goal, and what each goal compares with. A goal
/// that waits for something new (a drawing, a color, an Undo or a move) compares with the observation where it became
/// current; a goal about state (the mode or the tool) is done as soon as the state holds. When a goal is done, the next goal
/// is checked against the same observation, so a state that already holds counts at once, while anything new must come later.
/// </summary>
public sealed class TutorialProgress
{
    private TutorialObservation _baseline;
    private TutorialObservation _latest;
    private HashSet<Guid> _baselineIds = [];
    private Dictionary<Guid, TutorialElement> _known = [];

    public TutorialProgress(TutorialObservation start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _latest = start;
        _baseline = start;
        Rebase(start);
        Settle(start);
    }

    public TutorialStep Step { get; private set; } = TutorialSteps.All[0];

    /// <summary>The current goal's position in the step's goals; equal to the goal count once the tutorial is finished.</summary>
    public int GoalIndex { get; private set; }

    public bool IsFinished { get; private set; }

    /// <summary>Steps finished so far, in order (skipped steps excluded).</summary>
    public ImmutableArray<TutorialStep> Completed { get; private set; } = [];

    /// <summary>Steps skipped so far, in order.</summary>
    public ImmutableArray<TutorialStep> Skipped { get; private set; } = [];

    /// <summary>The goal the step waits for, or null once finished.</summary>
    public TutorialGoal? CurrentGoal => IsFinished ? null : Step.Goals[GoalIndex];

    /// <summary>Whether the current step's goal at <paramref name="index"/> is done.</summary>
    public bool IsGoalDone(int index) => IsFinished || index < GoalIndex;

    /// <summary>Takes a new look at the workspace and reports the furthest change it caused.</summary>
    public TutorialChange Observe(TutorialObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (IsFinished)
        {
            return TutorialChange.Finished;
        }
        _latest = observation;
        TutorialStep before = Step;
        int goalBefore = GoalIndex;
        if (Step.Goals[GoalIndex] == TutorialGoal.ColorChanged && observation.Tool != _baseline.Tool)
        {
            // Another tool shows its own color: compare with that one from now on.
            Rebase(observation);
        }
        Settle(observation);
        foreach (TutorialElement element in observation.Elements)
        {
            _known.TryAdd(element.Id, element);
        }
        return IsFinished ? TutorialChange.Finished
            : Step != before ? TutorialChange.Step
            : GoalIndex != goalBefore ? TutorialChange.Goal
            : TutorialChange.None;
    }

    /// <summary>Skips the current step; the next one starts from the latest observation.</summary>
    public TutorialChange Skip()
    {
        if (IsFinished)
        {
            return TutorialChange.Finished;
        }
        Skipped = Skipped.Add(Step);
        Advance(_latest);
        Settle(_latest);
        return IsFinished ? TutorialChange.Finished : TutorialChange.Step;
    }

    /// <summary>Checks goals against <paramref name="observation"/> until one is not done yet.</summary>
    private void Settle(TutorialObservation observation)
    {
        while (!IsFinished && IsMet(Step.Goals[GoalIndex], observation))
        {
            GoalIndex++;
            if (GoalIndex < Step.Goals.Length)
            {
                Rebase(observation);
                continue;
            }
            Completed = Completed.Add(Step);
            Advance(observation);
        }
    }

    private void Advance(TutorialObservation observation)
    {
        int next = Step.Index + 1;
        if (next >= TutorialSteps.All.Length)
        {
            IsFinished = true;
            GoalIndex = Step.Goals.Length;
            return;
        }
        Step = TutorialSteps.All[next];
        GoalIndex = 0;
        Rebase(observation);
    }

    private void Rebase(TutorialObservation observation)
    {
        _baseline = observation;
        _baselineIds = [.. observation.Elements.Select(element => element.Id)];
        _known = [];
        foreach (TutorialElement element in observation.Elements)
        {
            _known[element.Id] = element;
        }
    }

    private bool IsMet(TutorialGoal goal, TutorialObservation now) => goal switch
    {
        TutorialGoal.Drawing => now.Mode == TutorialMode.Draw && now.Presented,
        TutorialGoal.RectangleTool => now.Tool == Tool.Rectangle,
        TutorialGoal.ShapeDrawn => IsNew(now, element => element.Tool.IsShape),
        TutorialGoal.ColorChanged => now.Tool == _baseline.Tool && !SameColor(now.StrokeColor, _baseline.StrokeColor),
        TutorialGoal.ArrowTool => now.Tool == Tool.Arrow,
        TutorialGoal.CurvedStyle => now.Tool == Tool.Arrow && now.Curved,
        TutorialGoal.CurvedArrowDrawn => IsNew(now, element => element.Tool == Tool.Arrow && element.Curved && element.PointCount >= 3),
        TutorialGoal.PenTool => now.Tool.IsFreehand,
        TutorialGoal.PenDrawn => IsNew(now, element => element.Tool.IsFreehand || (now.Tool.IsFreehand && element.Tool.IsShape)),
        TutorialGoal.SelectTool => now.Tool == Tool.Select,
        TutorialGoal.Moved => now.Tool == Tool.Select && !now.Editing && now.Elements.Any(element =>
            _known.TryGetValue(element.Id, out TutorialElement before) && !element.SamePlace(before)),
        TutorialGoal.Undone => now.UndoSerial > _baseline.UndoSerial,
        TutorialGoal.UsingApps => now.Mode == TutorialMode.UseApps && now.Presented,
        TutorialGoal.Hidden => !now.Presented,
        TutorialGoal.Shown => now.Presented,
        _ => false,
    };

    /// <summary>A finished drawing that the goal's start did not have.</summary>
    private bool IsNew(TutorialObservation now, Func<TutorialElement, bool> matches) =>
        !now.Editing && now.Elements.Any(element => !_baselineIds.Contains(element.Id) && matches(element));

    private static bool SameColor(RGBA left, RGBA right) =>
        Math.Abs(left.Red - right.Red) < 0.002 && Math.Abs(left.Green - right.Green) < 0.002 &&
        Math.Abs(left.Blue - right.Blue) < 0.002 && Math.Abs(left.Alpha - right.Alpha) < 0.002;
}
