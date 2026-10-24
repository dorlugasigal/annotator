using System.Collections.Immutable;
using System.Numerics;
using Annotator.App.Icons;
using Annotator.App.Shell;
using Annotator.Core.Tutorial;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Annotator.App.Tutorial;

/// <summary>What the card's buttons do (Mac TutorialViews' closures).</summary>
internal sealed class TutorialCardActions
{
    public Action Start { get; init; } = static () => { };
    public Action NotNow { get; init; } = static () => { };
    public Action End { get; init; } = static () => { };
    public Action ShowMe { get; init; } = static () => { };
    public Action Skip { get; init; } = static () => { };
    public Action Done { get; init; } = static () => { };
    public Action OpenSettings { get; init; } = static () => { };
    public Action OpenTaskbarSettings { get; init; } = static () => { };
}

/// <summary>The coach card's parts that move after it is built: keycaps, goal checks, the title's check and the hint.</summary>
internal sealed class CoachContent
{
    public required FrameworkElement Root { get; init; }
    public required List<FrameworkElement> Keys { get; init; }
    public required List<(Grid Slot, CheckBadge Check)> Goals { get; init; }
    public required List<TextBlock> GoalTexts { get; init; }
    public required CheckBadge TitleCheck { get; init; }
    public required TextBlock Hint { get; init; }
    public required Button ShowMe { get; init; }
    public required Border CurrentSegment { get; init; }
}

/// <summary>
/// The tutorial card's content (Mac TutorialViews): the welcome card, a coach card per step and the finish card, in the
/// Mac's sizes. The card window swaps them with motion.
/// </summary>
internal static class TutorialCardView
{
    public const double CoachWidth = 340;
    public const double FinishWidth = 400;
    private const double Padding = 16;

    public static FrameworkElement Welcome(TutorialCardActions actions, TutorialPreferences preferences,
                                           Annotator.Workspace.Metrics.UsageMetrics usage)
    {
        var column = new MacStack { Spacing = 0, Width = CoachWidth - 2 * Padding };
        column.Children.Add(CardParts.Text(TutorialText.WelcomeTitle, 20, weight: FontWeights.SemiBold));
        column.Children.Add(Spaced(CardParts.Text(TutorialText.WelcomeBody, 13, CardParts.Secondary), 6));
        var buttons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button start = CardParts.PushButton(TutorialText.Start, prominent: true);
        AutomationProperties.SetAutomationId(start, TutorialText.StartIdentifier);
        start.Click += (_, _) => actions.Start();
        Button later = CardParts.PushButton(TutorialText.NotNow);
        AutomationProperties.SetAutomationId(later, TutorialText.NotNowIdentifier);
        later.Click += (_, _) => actions.NotNow();
        buttons.Children.Add(start);
        buttons.Children.Add(later);
        column.Children.Add(Spaced(buttons, 14));

        CheckBox launch = CardParts.CheckBox(TutorialText.ShowOnLaunch);
        AutomationProperties.SetAutomationId(launch, TutorialText.ShowOnLaunchIdentifier);
        launch.IsChecked = !preferences.Suppressed;
        launch.Click += (_, _) => preferences.SetSuppressed(launch.IsChecked != true);
        column.Children.Add(Spaced(launch, 14));

        var counts = new MacStack { Orientation = Orientation.Horizontal, Spacing = 10 };
        CheckBox share = CardParts.CheckBox(TutorialText.UsageCounts, height: 16.5);
        share.IsChecked = usage.Enabled;
        share.IsEnabled = usage.Available;
        string help = usage.Available ? TutorialText.UsageCountsHelp : TutorialText.UsageCountsUnavailableHelp;
        AutomationProperties.SetHelpText(share, help);
        ToolTipService.SetToolTip(share, help);
        share.Click += (_, _) => usage.SetEnabled(share.IsChecked == true);
        counts.Children.Add(share);
        TextBlock note = CardParts.Text(usage.Available ? TutorialText.UsageCountsNote : TutorialText.UsageCountsUnavailableNote,
            10, CardParts.Secondary, wrap: false);
        note.VerticalAlignment = VerticalAlignment.Center;
        counts.Children.Add(note);
        column.Children.Add(Spaced(counts, 6));
        column.Children.Add(Spaced(CardParts.Text(TutorialText.FindAgain, 11, CardParts.Secondary), 12));
        return Group(column, TutorialText.WelcomeTitle, TutorialText.WelcomeIdentifier);
    }

    public static CoachContent Coach(ProgressView progress, WorkspaceSnapshot snapshot, TutorialCardActions actions, bool dark)
    {
        TutorialStep step = progress.Step;
        TutorialStepText text = TutorialText.Step(step, snapshot);
        var column = new MacStack { Spacing = 0, Width = CoachWidth - 2 * Padding };

        var header = new Grid { ColumnSpacing = 10, Height = 20 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        (FrameworkElement track, Border current) = Track(step, dark);
        header.Children.Add(track);
        TextBlock count = CardParts.Text(TutorialText.StepValue(step), 11, CardParts.Secondary, wrap: false);
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        Button end = CloseButton(actions.End);
        Grid.SetColumn(end, 2);
        header.Children.Add(end);
        column.Children.Add(header);

        var titleRow = new Grid { ColumnSpacing = 8 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock title = CardParts.Text(text.Title, 15, weight: FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(title);
        var titleCheck = new CheckBadge(TutorialText.Done, TutorialText.Done, dark);
        titleCheck.Root.Visibility = Visibility.Collapsed;
        Grid.SetColumn(titleCheck.Root, 1);
        titleRow.Children.Add(titleCheck.Root);
        column.Children.Add(Spaced(titleRow, 12));
        column.Children.Add(Spaced(CardParts.Text(text.Detail, 13, CardParts.Secondary), 4));

        var goals = new List<(Grid, CheckBadge)>();
        var goalTexts = new List<TextBlock>();
        if (!text.Goals.IsEmpty)
        {
            var rows = new MacStack { Spacing = 6 };
            for (int index = 0; index < text.Goals.Length; index++)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var slot = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top };
                var pending = new Ellipse { Width = 14, Height = 14, Stroke = CardParts.Line(dark, 0.3), StrokeThickness = 1.5 };
                slot.Children.Add(pending);
                var check = new CheckBadge(string.Empty, $"Done: {text.Goals[index]}", dark);
                slot.Children.Add(check.Root);
                bool done = progress.IsGoalDone(index);
                pending.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
                check.Root.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
                row.Children.Add(slot);
                TextBlock line = CardParts.Text(text.Goals[index], 13, index == progress.GoalIndex ? CardParts.Primary : CardParts.Secondary);
                line.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(line, 1);
                row.Children.Add(line);
                rows.Children.Add(row);
                goals.Add((slot, check));
                goalTexts.Add(line);
            }
            column.Children.Add(Spaced(rows, 10));
        }

        var keys = new List<FrameworkElement>();
        if (progress.CurrentGoal is { } goal && TutorialText.Keys(goal, snapshot) is { IsEmpty: false } caps)
        {
            column.Children.Add(Spaced(Keycaps(caps, dark, keys), 12));
        }

        TextBlock hint = CardParts.Text(text.Hint, 11, CardParts.Secondary);
        hint.Visibility = Visibility.Collapsed;
        column.Children.Add(Spaced(hint, 10));

        var footer = new Grid { ColumnSpacing = 8 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Button showMe = CardParts.PushButton(TutorialText.ShowMe);
        AutomationProperties.SetAutomationId(showMe, TutorialText.ShowMeIdentifier);
        showMe.Click += (_, _) => actions.ShowMe();
        footer.Children.Add(showMe);
        Button skip = CardParts.PushButton(TutorialText.SkipStep);
        AutomationProperties.SetAutomationId(skip, TutorialText.SkipIdentifier);
        skip.Click += (_, _) => actions.Skip();
        Grid.SetColumn(skip, 2);
        footer.Children.Add(skip);
        column.Children.Add(Spaced(footer, 14));

        return new CoachContent
        {
            Root = Group(column, text.Title, TutorialText.CoachIdentifier),
            Keys = keys,
            Goals = goals,
            GoalTexts = goalTexts,
            TitleCheck = titleCheck,
            Hint = hint,
            ShowMe = showMe,
            CurrentSegment = current,
        };
    }

    public static FrameworkElement Finish(WorkspaceSnapshot snapshot, TutorialCardActions actions, bool dark)
    {
        var column = new MacStack { Spacing = 0, Width = FinishWidth - 2 * Padding };
        column.Children.Add(CardParts.Text(TutorialText.FinishTitle, 20, weight: FontWeights.SemiBold));
        column.Children.Add(Spaced(CardParts.Text(TutorialText.FinishLead, 13, CardParts.Secondary), 4));
        var rows = new MacStack { Spacing = 6 };
        foreach (TutorialKeyRow row in TutorialText.FinishRows(snapshot))
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = CardParts.Text(row.Label, 13, weight: FontWeights.SemiBold, wrap: false);
            label.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(label);
            FrameworkElement caps = Keycaps(row.Keys, dark, []);
            Grid.SetColumn(caps, 1);
            line.Children.Add(caps);
            rows.Children.Add(line);
        }
        column.Children.Add(Spaced(rows, 12));

        var change = new Grid { ColumnSpacing = 8 };
        change.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        change.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock changeText = CardParts.Text(TutorialText.ChangeShortcuts, 12, CardParts.Secondary);
        changeText.VerticalAlignment = VerticalAlignment.Center;
        change.Children.Add(changeText);
        Button open = CardParts.PushButton(TutorialText.OpenSettings);
        AutomationProperties.SetAutomationId(open, TutorialText.OpenSettingsIdentifier);
        open.Click += (_, _) => actions.OpenSettings();
        Grid.SetColumn(open, 1);
        change.Children.Add(open);
        column.Children.Add(Spaced(change, 12));

        TextBlock sharing = CardParts.Text(TutorialText.SharingBodyFor(snapshot.ScreenAccess), 12, CardParts.Secondary);
        sharing.Margin = new Thickness(0, 8, 0, 0);
        var disclosure = new CardDisclosure(TutorialText.SharingTitle, TutorialText.SharingIdentifier, sharing, expanded: false);
        column.Children.Add(Spaced(disclosure, 12));
        column.Children.Add(sharing);

        Button taskbar = CardParts.PushButton(TutorialText.TrayAction);
        AutomationProperties.SetHelpText(taskbar, TutorialText.TrayActionHelp);
        ToolTipService.SetToolTip(taskbar, TutorialText.TrayActionHelp);
        taskbar.Click += (_, _) => actions.OpenTaskbarSettings();
        var tray = new InfoCard("chevron.up", "Chevron Up", TutorialText.TrayTitle, TutorialText.TrayDetail, [], ready: false, dark, taskbar);
        column.Children.Add(Spaced(tray.Root, 12));
        IReadOnlyList<GlobalShortcutState> unavailable = TutorialText.UnavailableShortcuts(snapshot);
        bool working = unavailable.Count == 0;
        FrameworkElement trailing = working ? new CheckBadge(TutorialText.ShortcutsWorking, TutorialText.ShortcutsWorkingLabel, dark).Root : new Grid();
        var shortcuts = new InfoCard("keyboard", "Keyboard", TutorialText.ShortcutsTitle, TutorialText.ShortcutsDetail(snapshot),
            [.. unavailable.Select(TutorialText.ShortcutUnavailable)], working, dark, trailing);
        column.Children.Add(Spaced(shortcuts.Root, 8));

        column.Children.Add(Spaced(CardParts.Text(TutorialText.PracticeAgain, 11, CardParts.Secondary), 12));
        Button done = CardParts.PushButton(TutorialText.Done, prominent: true);
        AutomationProperties.SetAutomationId(done, TutorialText.DoneIdentifier);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Click += (_, _) => actions.Done();
        column.Children.Add(Spaced(done, 12));
        return Group(column, TutorialText.FinishTitle, TutorialText.FinishIdentifier);
    }

    /// <summary>
    /// Plays a keycap press on <paramref name="keys"/>, one after another: down two points, then back up (Mac
    /// TutorialKeycaps.press).
    /// </summary>
    public static void Press(IReadOnlyList<FrameworkElement> keys)
    {
        for (int index = 0; index < keys.Count; index++)
        {
            FrameworkElement key = keys[index];
            Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
            double down = TutorialMotion.KeyDownDuration, up = TutorialMotion.KeyUpDuration;
            Vector3KeyFrameAnimation press = compositor.CreateVector3KeyFrameAnimation();
            press.Target = "Translation";
            press.InsertKeyFrame(0, Vector3.Zero);
            press.InsertKeyFrame((float)(down / (down + up)), new Vector3(0, (float)TutorialMotion.KeyTravel, 0), Easing(compositor, Annotator.Core.Motion.MotionCurves.Exit));
            press.InsertKeyFrame(1, Vector3.Zero, Easing(compositor, Annotator.Core.Motion.MotionCurves.Enter));
            press.Duration = TimeSpan.FromSeconds(down + up);
            press.DelayTime = TimeSpan.FromSeconds(index * TutorialMotion.KeyStagger);
            key.StartAnimation(press);
        }
    }

    /// <summary>A gentle one-time pulse that points at an element (Show me after the hint appears).</summary>
    public static void Pulse(FrameworkElement element)
    {
        Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        element.CenterPoint = new Vector3((float)(element.ActualWidth / 2), (float)(element.ActualHeight / 2), 0);
        Vector3KeyFrameAnimation pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.Target = "Scale";
        pulse.InsertKeyFrame(0, Vector3.One);
        pulse.InsertKeyFrame(0.4f, new Vector3(1.06f, 1.06f, 1), Easing(compositor, Annotator.Core.Motion.MotionCurves.Standard));
        pulse.InsertKeyFrame(1, Vector3.One, Easing(compositor, TutorialMotion.Pop));
        pulse.Duration = TimeSpan.FromSeconds(0.5);
        element.StartAnimation(pulse);
    }

    /// <summary>The current step's segment of the progress track fills from its leading edge (Mac TutorialProgressTrack).</summary>
    public static void Grow(Border segment, bool reduceMotion)
    {
        if (reduceMotion)
        {
            return;
        }
        Compositor compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        segment.CenterPoint = new Vector3(0, 2, 0);
        Vector3KeyFrameAnimation grow = compositor.CreateVector3KeyFrameAnimation();
        grow.Target = "Scale";
        grow.InsertKeyFrame(0, new Vector3(0, 1, 1));
        grow.InsertKeyFrame(1, Vector3.One, Easing(compositor, Annotator.Core.Motion.MotionCurves.Standard));
        grow.Duration = TimeSpan.FromSeconds(TutorialMotion.TrackTrailDuration);
        grow.DelayTime = TimeSpan.FromSeconds(TutorialMotion.TrackTrailDelay);
        grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        segment.StartAnimation(grow);
    }

    /// <summary>Asks the screen reader to read <paramref name="text"/> (Mac TutorialViews announcement).</summary>
    public static void Announce(FrameworkElement element, string text)
    {
        Microsoft.UI.Xaml.Automation.Peers.AutomationPeer peer =
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(element) ??
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other,
            Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent, text, "tutorial-step");
    }

    public static CubicBezierEasingFunction Easing(Compositor compositor, Annotator.Core.Motion.CubicBezier curve) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2((float)curve.X1, (float)curve.Y1), new Vector2((float)curve.X2, (float)curve.Y2));

    /// <summary>Eleven segments: done steps and the current one in the accent, the rest faint.</summary>
    private static (FrameworkElement Track, Border Current) Track(TutorialStep step, bool dark)
    {
        var track = new Grid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Border current = new();
        for (int index = 0; index < TutorialSteps.All.Length; index++)
        {
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lane = new Grid { Height = 4 };
            lane.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = CardParts.Line(dark, 0.12) });
            if (index <= step.Index)
            {
                var fill = new Border { CornerRadius = new CornerRadius(2), Background = CardParts.Accent(index == step.Index ? 1 : 0.55) };
                lane.Children.Add(fill);
                if (index == step.Index)
                {
                    current = fill;
                }
            }
            Grid.SetColumn(lane, index);
            track.Children.Add(lane);
        }
        AutomationProperties.SetName(track, TutorialText.StepValue(step));
        return (track, current);
    }

    private static Button CloseButton(Action end)
    {
        FrameworkElement glyph = Glyphs.ForSymbol("xmark", 10, "AnnotatorGlyphSecondary", 2);
        var button = new Button
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Content = glyph,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, TutorialText.EndTutorial);
        AutomationProperties.SetAutomationId(button, TutorialText.EndIdentifier);
        ToolTipService.SetToolTip(button, TutorialText.EndTutorial);
        button.Click += (_, _) => end();
        return button;
    }

    /// <summary>Keycaps five points apart, read as one element named like "Ctrl + Shift + 2".</summary>
    private static FrameworkElement Keycaps(ImmutableArray<string> caps, bool dark, List<FrameworkElement> keys)
    {
        string spoken = string.Join(" + ", caps);
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
        foreach (string cap in caps)
        {
            FrameworkElement key = CardParts.Keycap(cap, dark, hidden: true);
            ToolTipService.SetToolTip(key, cap);
            row.Children.Add(key);
            keys.Add(key);
        }
        var group = new CardShortcut { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(group, spoken);
        AutomationProperties.SetHelpText(group, spoken);
        group.Children.Add(row);
        return group;
    }

    private static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        Thickness margin = element.Margin;
        element.Margin = new Thickness(margin.Left, top, margin.Right, margin.Bottom);
        return element;
    }

    private static FrameworkElement Group(FrameworkElement content, string name, string identifier)
    {
        var group = new AccessibleGroup(name, identifier) { Padding = new Thickness(Padding) };
        group.Children.Add(content);
        return group;
    }
}
