using System.Collections.Immutable;
using System.Numerics;
using Annotator.App.Shell;
using Annotator.Core.Tutorial;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using CorePoint = Annotator.Core.Point;

namespace Annotator.App.Tutorial;

/// <summary>
/// Port of the Mac's TutorialController: the hands-on tutorial that replaced the tour. It shows a welcome card once per launch
/// at a safe moment without taking focus, then coaches one step at a time while the person draws: the card says what to do
/// with their keys, the ring shows the control to click, and the ghost demonstrates once. Each step checks the workspace's
/// result, so a key and a click both count. The finish card ends with confetti from both bottom corners of the screen.
/// </summary>
internal sealed class TutorialController
{
    private enum Stage
    {
        Hidden,
        Welcome,
        Coaching,
        Finished,
    }

    private readonly WindowRegistry _registry;
    private readonly DispatcherQueue _queue;
    private readonly Func<WorkspaceSnapshot> _snapshot;
    private readonly Action<string> _status;
    private readonly Annotator.Workspace.Metrics.UsageMetrics _usage;
    private readonly Func<RectInt32?> _toolbar;
    private readonly Func<string, RectInt32?> _control;
    private readonly Action _openShortcuts;
    private readonly TutorialCardActions _actions;
    private TutorialCardWindow? _card;
    private TutorialOverlay? _overlay;
    private Stage _stage;
    private TutorialProgress? _progress;
    /// <summary>The step and goal the card shows; it follows the progress one done step at a time.</summary>
    private TutorialStep _shownStep;
    private int _shownGoal;
    private CoachContent? _coach;
    private DispatcherQueueTimer? _beat;
    private DispatcherQueueTimer? _ghost;
    private DispatcherQueueTimer? _hint;
    private readonly List<DispatcherQueueTimer> _keyCues = [];
    private RectInt32? _ringAnchor;
    private RectInt32? _placedNear;
    private bool _attemptedAutomaticPresentation;

    /// <param name="toolbar">The toolbar's frame in physical pixels while it shows.</param>
    /// <param name="control">A toolbar control's frame in physical pixels, by accessible name.</param>
    /// <param name="openShortcuts">Opens Settings on Toolbar &amp; Shortcuts.</param>
    public TutorialController(WindowRegistry registry, DispatcherQueue queue, AppDefaults defaults, Func<WorkspaceSnapshot> snapshot,
                              Action<string> status, Annotator.Workspace.Metrics.UsageMetrics usage, Func<RectInt32?> toolbar,
                              Func<string, RectInt32?> control, Action openShortcuts)
    {
        _registry = registry;
        _queue = queue;
        _snapshot = snapshot;
        _status = status;
        _usage = usage;
        _toolbar = toolbar;
        _control = control;
        _openShortcuts = openShortcuts;
        Preferences = new TutorialPreferences(defaults);
        _actions = new TutorialCardActions
        {
            Start = Start,
            NotNow = Close,
            End = Close,
            ShowMe = ShowMe,
            Skip = Skip,
            Done = Close,
            OpenSettings = () => _openShortcuts(),
            OpenTaskbarSettings = static () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar")),
        };
    }

    public TutorialPreferences Preferences { get; }

    /// <summary>Mac model.introductionVisible: the coffee reminder waits while a tutorial card shows.</summary>
    public bool IsVisible => _card?.IsShown == true;

    /// <summary>Raised when a card shows or hides.</summary>
    public event Action? VisibilityChanged;

    /// <summary>The card window once made (UI checks).</summary>
    public TutorialCardWindow? CardWindow => _card;

    /// <summary>The step the card shows while coaching (UI checks).</summary>
    public TutorialStep? ShownStep => _stage == Stage.Coaching ? _shownStep : null;

    public bool IsEligible => TutorialRules.IsEligible(_snapshot());

    /// <summary>Mac showAutomatically: once per launch, never at sign-in, after Show on launch was turned off or the tutorial finished, or at an unsafe moment.</summary>
    public void ShowAutomatically(bool startupLaunch)
    {
        if (_attemptedAutomaticPresentation)
        {
            return;
        }
        _attemptedAutomaticPresentation = true;
        if (TutorialRules.ShowsAutomatically(startupLaunch, Preferences.Suppressed, Preferences.Completed, IsEligible))
        {
            ShowWelcome(takeFocus: false);
        }
    }

    /// <summary>Mac showManually: Help and General open the welcome card with focus, or say when it cannot open now.</summary>
    public void ShowManually()
    {
        if (_stage is Stage.Coaching or Stage.Finished && IsVisible)
        {
            return;
        }
        if (!IsEligible)
        {
            _status(TutorialRules.NotNowMessage);
            return;
        }
        ShowWelcome(takeFocus: true);
    }

    /// <summary>Ends the tutorial (Not now, End tutorial, Done): the card sinks away and the ghost and ring go.</summary>
    public void Close()
    {
        bool wasVisible = IsVisible;
        StopTimers();
        _overlay?.Clear();
        _stage = Stage.Hidden;
        _progress = null;
        _coach = null;
        _card?.Conceal(SystemMotionReduced());
        if (wasVisible)
        {
            VisibilityChanged?.Invoke();
        }
    }

    /// <summary>
    /// Each published snapshot: the coach checks the step and follows the toolbar. The welcome card stays when the person starts
    /// drawing first; the steps that are done by then count once they start.
    /// </summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (_stage == Stage.Coaching && _progress is { } progress)
        {
            Observe(progress, snapshot);
            FollowToolbar();
        }
    }

    /// <summary>Closes the windows for good (Quit).</summary>
    public void Shutdown()
    {
        StopTimers();
        _overlay?.Dispose();
        _overlay = null;
        _card?.Shutdown();
        _card = null;
    }

    private void ShowWelcome(bool takeFocus)
    {
        bool wasVisible = IsVisible;
        StopTimers();
        _overlay?.Clear();
        _stage = Stage.Welcome;
        TutorialCardWindow card = Card();
        card.Show(TutorialCardView.Welcome(_actions, Preferences, _usage), Centered, takeFocus, SystemMotionReduced());
        if (!wasVisible)
        {
            VisibilityChanged?.Invoke();
        }
    }

    /// <summary>Start tutorial: the first step shows in the card, now placed next to the toolbar.</summary>
    private void Start()
    {
        _progress = new TutorialProgress(TutorialObserver.Observe(_snapshot()));
        _stage = Stage.Coaching;
        _shownStep = TutorialStep.Draw;
        _shownGoal = 0;
        ShowShownStep();
        // Steps that were done already (drawing that was on) count now, one beat at a time.
        if (_progress.Step != _shownStep || _progress.IsFinished)
        {
            StepDone();
        }
    }

    private void Observe(TutorialProgress progress, WorkspaceSnapshot snapshot)
    {
        TutorialChange change = progress.Observe(TutorialObserver.Observe(snapshot));
        if (snapshot.Displays.Any(display => display.ActiveEdit is not null) && _overlay?.IsPlaying == true)
        {
            // The person started drawing: the ghost steps aside.
            _overlay.StopDemo();
        }
        if (_beat is not null)
        {
            return;
        }
        switch (change)
        {
            case TutorialChange.Goal when progress.Step == _shownStep:
                GoalDone();
                break;
            case TutorialChange.Step or TutorialChange.Finished:
                StepDone();
                break;
        }
    }

    /// <summary>A goal of the shown step is done: its check draws, the keys and ring move to the next goal and the ghost shows it.</summary>
    private void GoalDone()
    {
        int done = _shownGoal;
        _shownGoal = _progress!.GoalIndex;
        ShowShownStep(animated: false);
        if (_coach is { } coach)
        {
            for (int index = done; index < _shownGoal && index < coach.Goals.Count; index++)
            {
                Check(coach.Goals[index].Check);
            }
        }
    }

    /// <summary>The shown step is done: its title check draws, then after a beat the next step shows.</summary>
    private void StepDone()
    {
        StopGoal();
        if (_coach is { } coach)
        {
            coach.TitleCheck.Root.Visibility = Visibility.Visible;
            Check(coach.TitleCheck);
            for (int index = _shownGoal; index < coach.Goals.Count; index++)
            {
                coach.Goals[index].Slot.Children[0].Visibility = Visibility.Collapsed;
                coach.Goals[index].Check.Root.Visibility = Visibility.Visible;
                Check(coach.Goals[index].Check);
            }
        }
        TutorialProgress progress = _progress!;
        bool last = _shownStep.Index >= TutorialSteps.All.Length - 1;
        bool nextDone = !last && (progress.IsFinished || progress.Step.Index > _shownStep.Index + 1);
        _beat = Timer(nextDone ? TutorialMotion.QuickDoneBeat : TutorialMotion.DoneBeat, () =>
        {
            _beat = null;
            if (_stage != Stage.Coaching || _progress is not { } current)
            {
                return;
            }
            if (last)
            {
                Finish();
                return;
            }
            _shownStep = TutorialSteps.All[_shownStep.Index + 1];
            _shownGoal = current.Step == _shownStep ? current.GoalIndex : _shownStep.Goals.Length;
            ShowShownStep();
            if (current.IsFinished || current.Step.Index > _shownStep.Index)
            {
                StepDone();
            }
        });
    }

    /// <summary>The finish card replaces the coach, the tutorial counts as done, and confetti pops from both bottom corners.</summary>
    private void Finish()
    {
        StopTimers();
        _overlay?.Clear();
        _stage = Stage.Finished;
        _coach = null;
        Preferences.SetCompleted(true);
        TutorialCardWindow card = Card();
        card.Show(TutorialCardView.Finish(_snapshot(), _actions, card.Dark), NextToToolbar, takeFocus: false, SystemMotionReduced());
        if (!SystemMotionReduced() && Overlay() is { } overlay)
        {
            overlay.Celebrate();
        }
    }

    /// <summary>Shows the shown step in the card, with the step motion unless <paramref name="animated"/> is false, and starts its goal.</summary>
    private void ShowShownStep(bool animated = true)
    {
        if (_progress is null)
        {
            return;
        }
        TutorialCardWindow card = Card();
        CoachContent coach = TutorialCardView.Coach(View(), _snapshot(), _actions, card.Dark);
        _coach = coach;
        bool reduce = SystemMotionReduced();
        if (animated)
        {
            card.Show(coach.Root, NextToToolbar, takeFocus: false, reduce);
            coach.CurrentSegment.Loaded += (_, _) => TutorialCardView.Grow(coach.CurrentSegment, reduce);
            // Narrator reads the new step, as VoiceOver announces it on the Mac.
            coach.Root.Loaded += (_, _) => TutorialCardView.Announce(coach.Root, TutorialText.StepValue(_shownStep) + ". " +
                TutorialText.Step(_shownStep, _snapshot()).Title + ". " + TutorialText.Step(_shownStep, _snapshot()).Detail);
        }
        else
        {
            card.Replace(coach.Root, NextToToolbar);
        }
        _placedNear = _toolbar();
        StartGoal();
    }

    /// <summary>
    /// The progress as the card shows it: the shown step with its shown goal. While the card catches up with steps the person
    /// finished already, the view shows them complete.
    /// </summary>
    private ProgressView View() => new(_shownStep, _shownGoal);

    /// <summary>The current goal starts: its ring shows, the ghost demonstrates once after a pause, and the hint waits.</summary>
    private void StartGoal()
    {
        StopGoal();
        if (_progress is not { } progress || progress.Step != _shownStep || progress.CurrentGoal is not { } goal)
        {
            return;
        }
        bool reduce = SystemMotionReduced();
        if (Overlay() is { } overlay)
        {
            _ringAnchor = TutorialDemos.Target(goal) is { } name ? _control(name) : null;
            overlay.Ring(_ringAnchor, reduce);
        }
        if (!reduce)
        {
            _ghost = Timer(TutorialMotion.GhostStartDelay, () =>
            {
                _ghost = null;
                PlayDemo();
            });
        }
        _hint = Timer(TutorialMotion.HintDelay, () =>
        {
            _hint = null;
            if (_coach is { } coach && _card is { } card)
            {
                coach.Hint.Visibility = Visibility.Visible;
                card.Refit(NextToToolbar);
                TutorialCardView.Pulse(coach.ShowMe);
            }
        });
    }

    private void StopGoal()
    {
        Stop(ref _ghost);
        Stop(ref _hint);
        foreach (DispatcherQueueTimer cue in _keyCues)
        {
            cue.Stop();
        }
        _keyCues.Clear();
        _overlay?.StopDemo();
        _overlay?.Ring(null, SystemMotionReduced());
        _ringAnchor = null;
    }

    /// <summary>Show me: the ghost plays the current goal again (with Reduce Motion only the keycaps press and the ring shows).</summary>
    private void ShowMe()
    {
        if (_stage != Stage.Coaching)
        {
            return;
        }
        Stop(ref _ghost);
        if (SystemMotionReduced())
        {
            if (_coach is { } coach)
            {
                TutorialCardView.Press(coach.Keys);
            }
            return;
        }
        PlayDemo();
    }

    private void Skip()
    {
        if (_progress is not { } progress || _beat is not null)
        {
            return;
        }
        TutorialChange change = progress.Skip();
        if (change is TutorialChange.Step or TutorialChange.Finished)
        {
            StopGoal();
            _beat = Timer(0.01, () =>
            {
                _beat = null;
                if (_stage != Stage.Coaching || _progress is not { } current)
                {
                    return;
                }
                if (_shownStep.Index >= TutorialSteps.All.Length - 1)
                {
                    Finish();
                    return;
                }
                _shownStep = TutorialSteps.All[_shownStep.Index + 1];
                _shownGoal = current.Step == _shownStep ? current.GoalIndex : _shownStep.Goals.Length;
                ShowShownStep();
                if (current.IsFinished || current.Step.Index > _shownStep.Index)
                {
                    StepDone();
                }
            });
        }
    }

    private void PlayDemo()
    {
        if (_progress is not { } progress || progress.Step != _shownStep || progress.CurrentGoal is not { } goal ||
            Overlay() is not { } overlay)
        {
            return;
        }
        WorkspaceSnapshot snapshot = _snapshot();
        MonitorArea monitor = Monitor();
        DisplayDescriptor? display = snapshot.Displays.Select(item => item.Display)
            .FirstOrDefault(item => item.ContainsDesktopPoint(new CorePoint(monitor.Bounds.X + monitor.Bounds.Width / 2.0, monitor.Bounds.Y + monitor.Bounds.Height / 2.0)));
        CorePoint? newest = display is null ? null : TutorialObserver.NewestGrip(snapshot, display.Id);
        ImmutableArray<TutorialDemoAction> actions = TutorialDemos.For(goal, snapshot, newest);
        RectInt32 card = _card?.SurfaceFrame ?? new RectInt32(monitor.WorkArea.X + monitor.WorkArea.Width / 2, monitor.WorkArea.Y + monitor.WorkArea.Height / 2, 1, 1);
        var origin = new Vector2(card.X + card.Width / 2f - monitor.Bounds.X, card.Y + card.Height / 2f - monitor.Bounds.Y);
        if (origin.X < 0 || origin.Y < 0 || origin.X > monitor.Bounds.Width || origin.Y > monitor.Bounds.Height)
        {
            origin = new Vector2(monitor.Bounds.Width / 2f, monitor.Bounds.Height / 2f);
        }
        DemoTimeline timeline = TutorialDemoCompiler.Compile(actions, _control, monitor,
            point => display?.ToDesktop(point), origin);
        overlay.Play(timeline);
        foreach (DemoKeyCue cue in timeline.Keys)
        {
            DispatcherQueueTimer? timer = null;
            timer = Timer(Math.Max(0.01, cue.Time), () =>
            {
                if (timer is not null)
                {
                    _keyCues.Remove(timer);
                }
                if (_coach is { } coach)
                {
                    TutorialCardView.Press(coach.Keys);
                }
            });
            _keyCues.Add(timer);
        }
    }

    /// <summary>The ring follows the toolbar when it moves, and the card stays next to it.</summary>
    private void FollowToolbar()
    {
        if (_progress?.CurrentGoal is { } goal && _beat is null && _overlay is { } overlay)
        {
            RectInt32? anchor = TutorialDemos.Target(goal) is { } name ? _control(name) : null;
            if (!Same(anchor, _ringAnchor))
            {
                _ringAnchor = anchor;
                overlay.Ring(anchor, reduceMotion: true);
            }
        }
        // While the toolbar hides (the Hide step), the card stays where it is.
        RectInt32? toolbar = _toolbar();
        if (toolbar is not null && !Same(toolbar, _placedNear) && _card is { IsShown: true } card)
        {
            _placedNear = toolbar;
            card.Refit(NextToToolbar);
        }
    }

    private TutorialCardWindow Card()
    {
        if (_card is null)
        {
            _card = new TutorialCardWindow(_registry);
            _card.ThemeChanged += () =>
            {
                switch (_stage)
                {
                    case Stage.Welcome:
                        _card.Replace(TutorialCardView.Welcome(_actions, Preferences, _usage), Centered);
                        break;
                    case Stage.Coaching:
                        ShowShownStep(animated: false);
                        break;
                    case Stage.Finished:
                        _card.Replace(TutorialCardView.Finish(_snapshot(), _actions, _card.Dark), NextToToolbar);
                        break;
                }
            };
        }
        return _card;
    }

    private TutorialOverlay? Overlay()
    {
        MonitorArea monitor = Monitor();
        _overlay ??= new TutorialOverlay(_registry, WindowChrome.SystemCompositor(), CardParts.Accent(1).Color);
        _overlay.Place(new PixelRect(monitor.Bounds.X, monitor.Bounds.Y, monitor.Bounds.X + monitor.Bounds.Width,
            monitor.Bounds.Y + monitor.Bounds.Height), monitor.Scale);
        return _overlay;
    }

    /// <summary>The display the tutorial uses: the toolbar's, else the one under the pointer.</summary>
    private MonitorArea Monitor() => _toolbar() is { } toolbar ? WindowChrome.MonitorFor(toolbar) : WindowChrome.MonitorAtCursor();

    /// <summary>The welcome card: centered on the display under the pointer, its top at 28% of the work area.</summary>
    private PointInt32 Centered(Windows.Foundation.Size size)
    {
        MonitorArea monitor = _toolbar() is { } toolbar ? WindowChrome.MonitorFor(toolbar) : WindowChrome.MonitorAtCursor();
        RectInt32 work = monitor.WorkArea;
        int x = work.X + (work.Width - (int)size.Width) / 2;
        int y = work.Y + (int)Math.Round(work.Height * 0.28);
        return Clamp(new PointInt32(x, y), size, monitor);
    }

    /// <summary>
    /// Mac TutorialCardPlacement: below a horizontal toolbar with right edges aligned (above when there is no room), beside a
    /// vertical one with tops aligned, or bottom-center without a toolbar; always inside the work area.
    /// </summary>
    private PointInt32 NextToToolbar(Windows.Foundation.Size size)
    {
        if (_toolbar() is not { } toolbar)
        {
            MonitorArea pointer = WindowChrome.MonitorAtCursor();
            RectInt32 area = pointer.WorkArea;
            int bottom = (int)Math.Round(24 * pointer.Scale);
            return Clamp(new PointInt32(area.X + (area.Width - (int)size.Width) / 2, area.Y + area.Height - bottom - (int)size.Height), size, pointer);
        }
        MonitorArea monitor = WindowChrome.MonitorFor(toolbar);
        RectInt32 work = monitor.WorkArea;
        int gap = (int)Math.Round(12 * monitor.Scale);
        PointInt32 corner;
        if (toolbar.Width >= toolbar.Height)
        {
            int x = toolbar.X + toolbar.Width - (int)size.Width;
            bool below = toolbar.Y + toolbar.Height + gap + size.Height <= work.Y + work.Height;
            int y = below ? toolbar.Y + toolbar.Height + gap : toolbar.Y - gap - (int)size.Height;
            corner = new PointInt32(x, y);
        }
        else
        {
            bool right = toolbar.X + toolbar.Width + gap + size.Width <= work.X + work.Width;
            int x = right ? toolbar.X + toolbar.Width + gap : toolbar.X - gap - (int)size.Width;
            corner = new PointInt32(x, toolbar.Y);
        }
        return Clamp(corner, size, monitor);
    }

    private static PointInt32 Clamp(PointInt32 corner, Windows.Foundation.Size size, MonitorArea monitor)
    {
        RectInt32 work = monitor.WorkArea;
        int inset = (int)Math.Round(8 * monitor.Scale);
        int x = Math.Clamp(corner.X, work.X + inset, Math.Max(work.X + inset, work.X + work.Width - (int)size.Width - inset));
        int y = Math.Clamp(corner.Y, work.Y + inset, Math.Max(work.Y + inset, work.Y + work.Height - (int)size.Height - inset));
        return new PointInt32(x, y);
    }

    /// <summary>A goal's check draws itself over <see cref="TutorialMotion.CheckDuration"/>, or at once with Reduce Motion.</summary>
    private void Check(CheckBadge badge)
    {
        if (SystemMotionReduced())
        {
            badge.Draw(1);
            return;
        }
        badge.Draw(0);
        DateTime start = DateTime.UtcNow;
        DispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            double t = (DateTime.UtcNow - start).TotalSeconds / TutorialMotion.CheckDuration;
            badge.Draw(Annotator.Core.Motion.MotionCurves.Enter.Progress(Math.Min(1, t)));
            if (t >= 1)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }

    private DispatcherQueueTimer Timer(double seconds, Action action)
    {
        DispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return timer;
    }

    private static void Stop(ref DispatcherQueueTimer? timer)
    {
        timer?.Stop();
        timer = null;
    }

    private void StopTimers()
    {
        StopGoal();
        Stop(ref _beat);
    }

    private static bool Same(RectInt32? left, RectInt32? right) =>
        left is { } a && right is { } b ? a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height : left is null && right is null;

    private static bool SystemMotionReduced() => Motion.SystemMotion.ReducesMotion();
}

/// <summary>The progress as the coach card shows it, which may lag one step behind while it catches up.</summary>
internal sealed record ProgressView(TutorialStep Step, int GoalIndex)
{
    /// <summary>The goal the card asks for, or null when the shown step is done.</summary>
    public TutorialGoal? CurrentGoal => GoalIndex < Step.Goals.Length ? Step.Goals[GoalIndex] : null;

    public bool IsGoalDone(int index) => index < GoalIndex;
}
