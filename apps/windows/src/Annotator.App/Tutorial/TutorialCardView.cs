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

/// <summary>The coach card's parts that move after it is built: keycaps, goal marks, the title's check and the hint.</summary>
internal sealed class CoachContent
{
    public required FrameworkElement Root { get; init; }
    public required List<FrameworkElement> Keys { get; init; }
    public required List<GoalMark> Goals { get; init; }
    public required TitleCheck TitleCheck { get; init; }
    public required FrameworkElement Hint { get; init; }
    public required Button ShowMe { get; init; }
    public required Border CurrentSegment { get; init; }
}

/// <summary>
/// The tutorial card's content (Mac TutorialViews): the welcome card, a coach card per step and the finish card, in the
/// Mac's sizes, places and accessibility elements. The card window swaps them with motion.
/// </summary>
internal static class TutorialCardView
{
    public const double CoachWidth = 340;
    private const double Padding = 16;
    private const double Column = CoachWidth - 2 * Padding;

    public static FrameworkElement Welcome(TutorialCardActions actions, TutorialPreferences preferences,
                                           Annotator.Workspace.Metrics.UsageMetrics usage)
    {
        var column = new MacStack { Spacing = 0, Width = Column };
        column.Children.Add(Text(TutorialText.WelcomeTitle, 20, 24, weight: FontWeights.SemiBold));
        column.Children.Add(Spaced(Text(TutorialText.WelcomeBody, 13, 16, CardParts.Secondary), 6));
        var buttons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button start = PushButton(TutorialText.Start, prominent: true);
        AutomationProperties.SetAutomationId(start, TutorialText.StartIdentifier);
        start.Click += (_, _) => actions.Start();
        Button later = PushButton(TutorialText.NotNow);
        AutomationProperties.SetAutomationId(later, TutorialText.NotNowIdentifier);
        later.Click += (_, _) => actions.NotNow();
        buttons.Children.Add(start);
        buttons.Children.Add(later);
        column.Children.Add(Spaced(buttons, 14));
        column.Children.Add(new Rectangle { Height = 1, Fill = AppResources.Brush("AnnotatorSeparatorBrush"), Margin = new Thickness(0, 12, 0, 12) });

        var launch = new CardCheckBox(TutorialText.ShowOnLaunch);
        AutomationProperties.SetAutomationId(launch, TutorialText.ShowOnLaunchIdentifier);
        launch.IsChecked = !preferences.Suppressed;
        launch.Click += (_, _) => preferences.SetSuppressed(launch.IsChecked != true);
        column.Children.Add(launch);

        var share = new CardCheckBox(TutorialText.UsageCounts);
        share.IsChecked = usage.Enabled;
        share.IsEnabled = usage.Available;
        string help = usage.Available ? TutorialText.UsageCountsHelp : TutorialText.UsageCountsUnavailableHelp;
        AutomationProperties.SetHelpText(share, help);
        ToolTipService.SetToolTip(share, help);
        share.Click += (_, _) => usage.SetEnabled(share.IsChecked == true);
        column.Children.Add(Spaced(share, 6 + share.Margin.Top));
        TextBlock note = Text(usage.Available ? TutorialText.UsageCountsNote : TutorialText.UsageCountsUnavailableNote, 10, 13, CardParts.Secondary);
        note.Margin = new Thickness(20, 2, 0, 0);
        column.Children.Add(note);
        column.Children.Add(Spaced(Text(TutorialText.FindAgain, 10, 13, CardParts.Secondary), 12));
        return Card(column, TutorialText.WelcomeIdentifier);
    }

    public static CoachContent Coach(ProgressView progress, WorkspaceSnapshot snapshot, TutorialCardActions actions, bool dark)
    {
        TutorialStep step = progress.Step;
        TutorialStepText text = TutorialText.Step(step, snapshot);
        var column = new MacStack { Spacing = 0, Width = Column };

        var header = new Grid { ColumnSpacing = 10, Height = 20 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        (FrameworkElement track, Border segment) = Track(step, dark);
        header.Children.Add(track);
        TextBlock count = Text(TutorialText.StepValue(step), 10, 13, CardParts.Secondary);
        count.TextWrapping = TextWrapping.NoWrap;
        count.Width = CardWidths.StepCount(step.Index + 1);
        count.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        Button end = CloseButton(actions.End);
        Grid.SetColumn(end, 2);
        header.Children.Add(end);
        column.Children.Add(header);

        // The title's check has its room from the start, so a finished step never changes the card's size.
        var titleRow = new Grid { ColumnSpacing = 8, Height = 22, Margin = new Thickness(0, 12, 0, 0) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock title = Text(text.Title, 15, 19, weight: FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        titleRow.Children.Add(title);
        var titleCheck = new TitleCheck();
        Grid.SetColumn(titleCheck.Root, 1);
        titleRow.Children.Add(titleCheck.Root);
        column.Children.Add(titleRow);

        int current = progress.GoalIndex;
        var keys = new List<FrameworkElement>();
        if (text.Detail is { } detail)
        {
            column.Children.Add(Spaced(KeyLine(detail, 13, CardParts.Secondary, dark, keys), 6));
        }

        var goals = new List<GoalMark>();
        if (!text.Goals.IsEmpty)
        {
            var rows = new MacStack { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
            for (int index = 0; index < text.Goals.Length; index++)
            {
                bool done = progress.IsGoalDone(index);
                bool now = index == current;
                var mark = new GoalMark(done, dark);
                FrameworkElement line = KeyLine(text.Goals[index], 13, now ? CardParts.Primary : CardParts.Secondary, dark, now ? keys : null, accessible: false);
                line.VerticalAlignment = VerticalAlignment.Center;
                // The row reads as one element named by its words, framed from the mark's circle to the line's end (Mac
                // accessibilityElement(children: .combine)); its state is the item status.
                var row = new CardElement(TutorialText.Plain(text.Goals[index])) { ColumnSpacing = 8 };
                row.Bounds = () => CardElement.Union(row, [mark.Check.Root.Visibility == Visibility.Visible ? mark.Check.Root : mark.Visible, line]);
                AutomationProperties.SetItemStatus(row, done ? TutorialText.Done : now ? "Current" : "Next");
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(mark.Root);
                Grid.SetColumn(line, 1);
                row.Children.Add(line);
                rows.Children.Add(row);
                goals.Add(mark);
            }
            column.Children.Add(rows);
        }

        // Shortcuts with Ctrl, Shift or Alt, and Esc, also show on a keyboard with their keys lit; single keys show in the line.
        if (progress.CurrentGoal is { } goal && TutorialText.ShowsKeyboard(goal, snapshot))
        {
            column.Children.Add(Spaced(Keyboard(TutorialText.Keys(goal, snapshot), dark, keys), 12));
        }

        FrameworkElement hint = KeyLine(text.Hint, 11, CardParts.Secondary, dark, null);
        hint.Visibility = Visibility.Collapsed;
        column.Children.Add(Spaced(hint, 10));

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Button showMe = PushButton(TutorialText.ShowMe);
        AutomationProperties.SetAutomationId(showMe, TutorialText.ShowMeIdentifier);
        showMe.Click += (_, _) => actions.ShowMe();
        footer.Children.Add(showMe);
        Button skip = PushButton(TutorialText.SkipStep);
        AutomationProperties.SetAutomationId(skip, TutorialText.SkipIdentifier);
        skip.Click += (_, _) => actions.Skip();
        Grid.SetColumn(skip, 2);
        footer.Children.Add(skip);
        column.Children.Add(footer);

        return new CoachContent
        {
            Root = Card(column, TutorialText.CoachIdentifier),
            Keys = keys,
            Goals = goals,
            TitleCheck = titleCheck,
            Hint = hint,
            ShowMe = showMe,
            CurrentSegment = segment,
        };
    }

    /// <summary>
    /// The finish card (Mac TutorialViews.finish): the keys to keep, where to change them, Before you share, and, on Windows
    /// only, how to keep the tray icon visible (DV-4) and whether the shortcuts work (DV-10).
    /// </summary>
    public static FrameworkElement Finish(WorkspaceSnapshot snapshot, TutorialCardActions actions, bool dark)
    {
        var column = new MacStack { Spacing = 0, Width = Column };
        column.Children.Add(Text(TutorialText.FinishTitle, 20, 24, weight: FontWeights.SemiBold));
        column.Children.Add(Spaced(Text(TutorialText.FinishLead, 13, 16, CardParts.Secondary), 4));
        var rows = new MacStack { Spacing = 6, Margin = new Thickness(0, 12, 0, 0) };
        foreach (TutorialKeyRow row in TutorialText.FinishRows(snapshot))
        {
            // One element per row, named by its words and keys and framed with the keycaps' outer half point (Mac
            // accessibilityElement(children: .combine)).
            var line = new CardElement($"{row.Label}, {TutorialText.Spoken(row.Keys)}");
            line.Bounds = () => new Windows.Foundation.Rect(-0.5, -0.5, line.ActualWidth + 1, line.ActualHeight + 1);
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = Text(row.Label, 13, 16, weight: FontWeights.SemiBold);
            label.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(label);
            FrameworkElement caps = Keycaps(row.Keys, dark);
            Grid.SetColumn(caps, 1);
            line.Children.Add(caps);
            rows.Children.Add(line);
        }
        column.Children.Add(rows);

        TextBlock change = Text(TutorialText.ChangeShortcuts, 12, 15, CardParts.Secondary);
        // It wraps where the Mac's wider font wraps it, so the controls below keep the Mac's places (D-4).
        change.MaxWidth = CardWidths.ChangeShortcuts;
        column.Children.Add(Spaced(change, 12));
        Button open = PushButton(TutorialText.OpenSettings);
        AutomationProperties.SetAutomationId(open, TutorialText.OpenSettingsIdentifier);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += (_, _) => actions.OpenSettings();
        column.Children.Add(Spaced(open, 6));

        TextBlock sharing = Text(TutorialText.SharingBodyFor(snapshot.ScreenAccess), 12, 15, CardParts.Secondary);
        sharing.Margin = new Thickness(0, 8, 0, 0);
        var disclosure = new CardDisclosure(TutorialText.SharingTitle, TutorialText.SharingIdentifier, sharing, expanded: false)
        {
            Height = 24,
            Margin = new Thickness(0, 12, 0, 0),
        };
        column.Children.Add(disclosure);
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

        var last = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        last.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        last.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock again = Text(TutorialText.PracticeAgain, 10, 13, CardParts.Secondary);
        again.VerticalAlignment = VerticalAlignment.Center;
        last.Children.Add(again);
        Button done = PushButton(TutorialText.Done, prominent: true);
        AutomationProperties.SetAutomationId(done, TutorialText.DoneIdentifier);
        done.Click += (_, _) => actions.Done();
        Grid.SetColumn(done, 1);
        last.Children.Add(done);
        column.Children.Add(last);
        return Card(column, TutorialText.FinishIdentifier, framedByChildren: true);
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

    /// <summary>
    /// The progress track (Mac TutorialProgressTrack): a segment per step three points apart, done steps and the current one in
    /// the accent and the rest faint, read as one element.
    /// </summary>
    private static (FrameworkElement Track, Border Current) Track(TutorialStep step, bool dark)
    {
        var track = new CardElement("Tutorial progress") { ColumnSpacing = 3, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetItemStatus(track, TutorialText.StepValue(step));
        Border current = new();
        for (int index = 0; index < TutorialSteps.All.Length; index++)
        {
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lane = new Grid();
            lane.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = CardParts.Line(dark, 0.12) });
            if (index <= step.Index)
            {
                var fill = new Border { CornerRadius = new CornerRadius(2), Background = CardParts.Accent(1) };
                lane.Children.Add(fill);
                if (index == step.Index)
                {
                    current = fill;
                }
            }
            Grid.SetColumn(lane, index);
            track.Children.Add(lane);
        }
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
        AutomationProperties.SetHelpText(button, TutorialText.EndTutorial);
        ToolTipService.SetToolTip(button, TutorialText.EndTutorial);
        button.Click += (_, _) => end();
        return button;
    }

    /// <summary>Keycaps five points apart for the finish card's rows; their row reads them.</summary>
    private static FrameworkElement Keycaps(ImmutableArray<string> caps, bool dark)
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        foreach (string cap in caps)
        {
            FrameworkElement key = CardParts.Keycap(cap, dark, hidden: true);
            ToolTipService.SetToolTip(key, cap);
            row.Children.Add(key);
        }
        return row;
    }

    /// <summary>
    /// A line whose keys, marked in braces, show as small keycaps inside the words (Mac TutorialKeyLine), laid out like the Mac's
    /// TutorialLineFlow. Screen readers read the plain words as one element, unless <paramref name="accessible"/> is false and a
    /// row reads them. The keycaps go into <paramref name="keys"/> so the ghost's key presses move them.
    /// </summary>
    private static FrameworkElement KeyLine(string line, double size, string style, bool dark, List<FrameworkElement>? keys, bool accessible = true)
    {
        var flow = new LineFlow { Space = size * 0.28, LineSpacing = 2 };
        foreach (TutorialLineToken token in TutorialText.Tokens(line))
        {
            if (!token.IsKey)
            {
                TextBlock word = Text(token.Text, size, size <= 11 ? 14 : 16, style);
                word.TextWrapping = TextWrapping.NoWrap;
                flow.Add(word, token.SpaceBefore);
                continue;
            }
            // One keycap per shortcut, as the Mac's ⌃⇧2: "Ctrl+Shift+2".
            Border key = InlineKey(token.Text, dark);
            keys?.Add(key);
            flow.Add(key, token.SpaceBefore);
        }
        AutomationProperties.SetAccessibilityView(flow, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (!accessible)
        {
            flow.HorizontalAlignment = HorizontalAlignment.Left;
            return flow;
        }
        var element = new CardElement(TutorialText.Plain(line)) { HorizontalAlignment = HorizontalAlignment.Left };
        element.Children.Add(flow);
        return element;
    }

    /// <summary>
    /// A small keycap inside a line: monospaced semibold on a faint fill with a fine outline and 4-point corners, a point apart
    /// from its neighbors.
    /// </summary>
    private static Border InlineKey(string cap, bool dark)
    {
        var label = new TextBlock
        {
            Text = cap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Style = AppResources.Style(CardParts.Primary),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        return new Border
        {
            MinWidth = 18,
            Height = 18,
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(4),
            Background = CardParts.Fill(dark, 0.06),
            BorderBrush = CardParts.Line(dark, 0.22),
            BorderThickness = new Thickness(1),
            Child = label,
        };
    }

    /// <summary>
    /// The left part of a keyboard with the shortcut's keys lit in the accent (Mac TutorialKeyboardView): the Esc row, the
    /// number row, three letter rows and the bottom row, cut at its right edge, which fades into the card. It reads as one
    /// element framed around every key it draws, cut or not, as SwiftUI reports it.
    /// </summary>
    private static FrameworkElement Keyboard(ImmutableArray<string> caps, bool dark, List<FrameworkElement> keys)
    {
        IReadOnlyList<string> lit = TutorialKeyboard.Ids(caps, mac: false) ?? [];
        var canvas = new Canvas { Width = TutorialKeyboard.Width, Height = TutorialKeyboard.Height };
        var litKeys = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        Windows.Foundation.Rect? drawn = null;
        foreach (TutorialKey key in TutorialKeyboard.Keys(mac: false))
        {
            Annotator.Core.Rect frame = TutorialKeyboard.Frame(key);
            if (frame.X >= TutorialKeyboard.Width)
            {
                continue;
            }
            var box = new Windows.Foundation.Rect(frame.X, frame.Y, frame.Width, frame.Height);
            if (drawn is { } union)
            {
                union.Union(box);
                drawn = union;
            }
            else
            {
                drawn = box;
            }
            bool on = lit.Contains(key.Id);
            var label = new TextBlock
            {
                Text = KeyLabel(key.Id),
                FontSize = key.Id.Length > 1 ? 8.5 : 10,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = on ? new SolidColorBrush(Microsoft.UI.Colors.White) : AppResources.Brush("AnnotatorSecondaryLabelBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = key.Width > 1 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                Margin = new Thickness(key.Width > 1 ? 5 : 0, 0, 0, 0),
            };
            var cap = new Border
            {
                Width = frame.Width,
                Height = frame.Height,
                CornerRadius = new CornerRadius(4),
                Background = on ? CardParts.Accent(1) : CardParts.Fill(dark, 0.05),
                BorderBrush = on ? CardParts.Accent(1) : CardParts.Line(dark, 0.14),
                BorderThickness = new Thickness(1),
                Child = label,
            };
            Canvas.SetLeft(cap, frame.X);
            Canvas.SetTop(cap, frame.Y);
            canvas.Children.Add(cap);
            if (on)
            {
                litKeys[key.Id] = cap;
            }
        }
        foreach (string id in lit)
        {
            if (litKeys.TryGetValue(id, out FrameworkElement? cap))
            {
                keys.Add(cap);
            }
        }
        Windows.UI.Color background = AppResources.Brush("AnnotatorWindowBackgroundBrush") is SolidColorBrush { Color: var color }
            ? color : Windows.UI.Color.FromArgb(255, 30, 30, 30);
        var fade = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
        fade.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, background.R, background.G, background.B), Offset = 0 });
        fade.GradientStops.Add(new GradientStop { Color = background, Offset = 1 });
        var edge = new Rectangle { Width = TutorialKeyboard.FadeWidth, Height = TutorialKeyboard.Height, Fill = fade, IsHitTestVisible = false };
        Canvas.SetLeft(edge, TutorialKeyboard.Width - TutorialKeyboard.FadeWidth);
        canvas.Children.Add(edge);
        canvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, TutorialKeyboard.Width, TutorialKeyboard.Height) };
        var element = new CardElement($"Keyboard: {TutorialText.Spoken(caps)}") { HorizontalAlignment = HorizontalAlignment.Left };
        Windows.Foundation.Rect bounds = drawn ?? new Windows.Foundation.Rect(0, 0, TutorialKeyboard.Width, TutorialKeyboard.Height);
        element.Bounds = () => bounds;
        element.Children.Add(canvas);
        return element;
    }

    /// <summary>Windows keycap words for the tutorial keyboard's keys.</summary>
    private static string KeyLabel(string id) => id switch
    {
        "esc" => "Esc",
        "tab" => "Tab",
        "caps" => "Caps",
        "shift" => "Shift",
        "ctrl" => "Ctrl",
        "win" => "Win",
        "alt" => "Alt",
        "space" => string.Empty,
        _ => id.ToUpperInvariant(),
    };

    /// <summary>Text at <paramref name="size"/> points in the Mac's line height for it, wrapping across the card.</summary>
    private static TextBlock Text(string text, double size, double lineHeight, string style = CardParts.Primary, Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock block = CardParts.Text(text, size, style, weight);
        block.LineHeight = lineHeight;
        return block;
    }

    /// <summary>A card push button at the Mac's width for its title.</summary>
    private static Button PushButton(string title, bool prominent = false)
    {
        Button button = CardParts.PushButton(title, prominent);
        if (CardWidths.Of(title) is { } width)
        {
            button.Width = width;
            button.Padding = new Thickness(0, 0, 0, 1);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
        }
        return button;
    }

    private static T Spaced<T>(T element, double top) where T : FrameworkElement
    {
        Thickness margin = element.Margin;
        element.Margin = new Thickness(margin.Left, top, margin.Right, margin.Bottom);
        return element;
    }

    /// <summary>
    /// A card: the content in 16 points of padding, read as a group with the card's identifier and framed around the
    /// content, as SwiftUI reports the padded stack, or around its elements when <paramref name="framedByChildren"/> (the finish
    /// card, whose disclosure reaches past the content). The card covers its surface's 1-point outline, so the surface is as
    /// wide as the card.
    /// </summary>
    private static FrameworkElement Card(FrameworkElement content, string identifier, bool framedByChildren = false)
    {
        var group = new AccessibleGroup(string.Empty, identifier) { BoundsFromChildren = framedByChildren };
        group.Children.Add(content);
        return new Grid { Padding = new Thickness(Padding), Margin = new Thickness(-1), Children = { group } };
    }
}
