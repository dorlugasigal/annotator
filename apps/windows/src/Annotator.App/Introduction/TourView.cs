using System.Numerics;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core.Motion;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Annotator.App.Introduction;

/// <summary>
/// The tour's content (WIN-UI-04, a port of the Mac's IntroductionView): the step indicator, the page and the footer, 520
/// points wide. The step indicator's capsule stretches toward the next step and settles; pages fade through with a short
/// slide (forward from the right, Back from the left); each page's story plays once when it appears. The Left and Right
/// arrow keys move between pages. Nothing on any page asks for a permission (WIN-INP-08). The window
/// (<see cref="IntroductionWindow"/>) or a reference capture hosts <see cref="Root"/>.
/// </summary>
internal sealed class TourView
{
    private const double WindowWidth = 520;
    private const double StepHeight = 16;

    /// <summary>A little under 1/60 s, so a 60 Hz display's frames are never skipped by timing jitter.</summary>
    private const double FrameInterval = 1 / 60.0 - 0.002;

    private readonly IntroductionNavigator _navigator;
    private readonly IntroductionPreferences _preferences;
    private readonly Action _close;
    private readonly Func<WorkspaceSnapshot> _snapshot;
    private readonly IllustrationGraphics _graphics;
    private readonly Grid _root = new();
    private readonly Grid _pages = new();
    private readonly List<ParityButton> _steps = [];
    private readonly Border _capsule;
    private readonly Grid _indicator;
    private readonly Button _back;
    private readonly Button _next;
    private readonly CheckBox _showOnLaunch;
    private readonly CheckBox _counts = TourParts.CheckBox(IntroductionText.UsageCounts, height: 16.5);
    private readonly Annotator.Workspace.Metrics.UsageMetrics _usage;
    private TourPage? _page;
    private IntroductionPage _shownPage;
    private int _shownToken = -1;
    private bool _sharingExpanded;
    private bool _shown;
    private bool _rendering;
    private bool _capsuleMoving;
    private StoryRun? _story;
    private CapsuleEdge _lower;
    private CapsuleEdge _upper;
    private double _lastFrame;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;

    /// <param name="usage">The app's usage counts, for the footer's Share usage counts (the Mac's model.usageMetrics).</param>
    /// <param name="close">Hides the tour (Got it): the controller's Close.</param>
    public TourView(IntroductionNavigator navigator, IntroductionPreferences preferences, Func<WorkspaceSnapshot> snapshot,
                    Annotator.Workspace.Metrics.UsageMetrics usage, Action close)
    {
        _navigator = navigator;
        _preferences = preferences;
        _snapshot = snapshot;
        _usage = usage;
        _close = close;
        _graphics = new IllustrationGraphics(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()) { Suspended = true };
        _lower = CapsuleEdge.At((int)navigator.Page);
        _upper = CapsuleEdge.At((int)navigator.Page);

        _root.Width = WindowWidth;
        _root.Background = AppResources.Brush("AnnotatorWindowBackgroundBrush");
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _indicator = new Grid { Margin = new Thickness(24, 10, 24, 0), Height = StepHeight };
        var steps = new MacStack { Orientation = Orientation.Horizontal, Spacing = IntroductionMotion.StepSpacing };
        foreach (IntroductionPage step in IntroductionPageExtensions.All)
        {
            steps.Children.Add(StepButton(step));
        }
        _capsule = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = TourParts.Accent(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(_capsule, AccessibilityView.Raw);
        _indicator.Children.Add(steps);
        _indicator.Children.Add(_capsule);
        _root.Children.Add(_indicator);

        _pages.VerticalAlignment = VerticalAlignment.Stretch;
        // Mac .clipped(): a page sliding in or out never draws over the indicator or the footer.
        _pages.SizeChanged += (_, args) => _pages.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        Grid.SetRow(_pages, 1);
        _root.Children.Add(_pages);

        var divider = new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Style = AppResources.Style("AnnotatorDivider") };
        Grid.SetRow(divider, 2);
        _root.Children.Add(divider);

        _back = TourParts.PushButton(IntroductionText.Back);
        _back.Click += (_, _) => _navigator.Move(IntroductionDirection.Backward);
        _next = TourParts.PushButton(IntroductionText.Next);
        _next.Click += (_, _) => _navigator.Move(IntroductionDirection.Forward);
        _showOnLaunch = TourParts.CheckBox(IntroductionText.ShowOnLaunch);
        _showOnLaunch.IsChecked = !_preferences.Suppressed;
        _showOnLaunch.Click += (_, _) => _preferences.SetSuppressed(_showOnLaunch.IsChecked != true);
        FrameworkElement footer = Footer();
        Grid.SetRow(footer, 3);
        _root.Children.Add(footer);

        _root.PreviewKeyDown += OnPreviewKeyDown;
        _root.ActualThemeChanged += (_, _) => Rebuild();
        // The colors chosen before the content had a theme are made again once it has one.
        _root.Loaded += (_, _) => Rebuild();
        AutomationFrames.Nudge(_root);

        _navigator.Changed += OnNavigatorChanged;
        _preferences.Changed += OnPreferencesChanged;
        _usage.Changed += OnUsageChanged;
        ShowPage(animated: false);
        UpdateChrome();
        PlaceCapsule();
    }

    /// <summary>The content: 520 points wide and <see cref="ContentHeight"/> tall.</summary>
    public FrameworkElement Root => _root;

    /// <summary>Mac: min(530, max(340, the screen's visible height - 90)).</summary>
    public double ContentHeight
    {
        get => _root.Height;
        set => _root.Height = value;
    }

    /// <summary>The drawing devices, for measurements.</summary>
    public IllustrationGraphics Graphics => _graphics;

    /// <summary>The page on screen (UI checks and measurements).</summary>
    public TourPage? CurrentPage => _page;

    /// <summary>The content is on screen: illustrations may draw.</summary>
    public void Shown()
    {
        _shown = true;
        _graphics.Suspended = false;
        _page?.Illustration?.Redraw();
    }

    /// <summary>The content left the screen: the story ends and the drawing devices are released until it shows again.</summary>
    public void Hidden()
    {
        _shown = false;
        StopRendering();
        _story = null;
        _graphics.Suspended = true;
        _graphics.Release();
    }

    /// <summary>Follows the workspace: keycaps and the shortcuts card change with the shortcuts.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (_page is { } page && !page.Update(snapshot))
        {
            Rebuild();
        }
    }

    /// <summary>The content goes for good: it stops following the navigator and releases its devices.</summary>
    public void Detach()
    {
        _navigator.Changed -= OnNavigatorChanged;
        _preferences.Changed -= OnPreferencesChanged;
        _usage.Changed -= OnUsageChanged;
        StopRendering();
        _graphics.Dispose();
    }

    private void OnPreferencesChanged() => _showOnLaunch.IsChecked = !_preferences.Suppressed;

    /// <summary>The check box shows what usage counts say, also after Settings changed them.</summary>
    private void OnUsageChanged() => _counts.IsChecked = _usage.Enabled;

    /// <summary>
    /// A step's track: the label color at 10% (11% in dark). A contrast theme outlines it in the window text color, so the
    /// Highlight capsule over the current step stands out from the hollow tracks.
    /// </summary>
    private static void StyleTrack(Border track, bool dark)
    {
        track.Background = TourParts.Fill(dark, dark ? 0.11 : 0.1);
        track.BorderBrush = TourParts.Line(dark, 0);
        track.BorderThickness = new Thickness(ContrastTheme.IsOn ? 1 : 0);
    }

    private ParityButton StepButton(IntroductionPage step)
    {
        bool dark = _root.ActualTheme == ElementTheme.Dark;
        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        StyleTrack(track, dark);
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = (WindowWidth - 48 - IntroductionMotion.StepSpacing * 3) / 4,
            Height = StepHeight,
            CornerRadius = new CornerRadius(4),
            Content = track,
            AccessibleValue = step.StepValue,
            AccessibleSelected = step == _navigator.Page,
        };
        AutomationProperties.SetName(button, step.Title);
        AutomationProperties.SetHelpText(button, step.Title);
        ToolTipService.SetToolTip(button, step.Title);
        button.Click += (_, _) => _navigator.Select(step);
        _steps.Add(button);
        return button;
    }

    /// <summary>
    /// Mac footer: Share usage counts with its note, Show on launch with Back, Next and the prominent Got it, then where to find
    /// the tour again; 20 by 14 points of padding, rows 8 points apart. The check box follows the app's usage counts, and is
    /// off and unavailable, saying so, while Windows may not send them (WIN-PRV-01).
    /// </summary>
    private FrameworkElement Footer()
    {
        var footer = new MacStack { Spacing = 8, Margin = new Thickness(20, 14, 20, 14) };
        var usage = new MacStack { Orientation = Orientation.Horizontal, Spacing = 10 };
        _counts.IsChecked = _usage.Enabled;
        _counts.IsEnabled = _usage.Available;
        string help = _usage.Available ? IntroductionText.UsageCountsHelp : IntroductionText.UsageCountsUnavailableHelp;
        AutomationProperties.SetHelpText(_counts, help);
        ToolTipService.SetToolTip(_counts, help);
        _counts.Click += (_, _) =>
        {
            _usage.SetEnabled(_counts.IsChecked == true);
            OnUsageChanged();
        };
        usage.Children.Add(_counts);
        TextBlock note = TourParts.Text(_usage.Available ? IntroductionText.UsageCountsNote : IntroductionText.UsageCountsUnavailableNote,
            10, TourParts.Secondary, wrap: false);
        note.VerticalAlignment = VerticalAlignment.Center;
        usage.Children.Add(note);
        footer.Children.Add(usage);

        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(_showOnLaunch);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        Button gotIt = TourParts.PushButton(IntroductionText.GotIt, prominent: true);
        gotIt.Click += (_, _) => _close();
        buttons.Children.Add(gotIt);
        Grid.SetColumn(buttons, 2);
        actions.Children.Add(buttons);
        footer.Children.Add(actions);
        footer.Children.Add(TourParts.Text(IntroductionText.FindAgain, 10, TourParts.Secondary, wrap: false));
        return footer;
    }

    private void OnNavigatorChanged()
    {
        if (_navigator.Page != _shownPage)
        {
            bool reduce = ReducesMotion();
            int from = (int)_shownPage, to = (int)_navigator.Page;
            MoveCapsule(from, to, reduce);
            ShowPage(animated: _shown);
            UpdateChrome();
        }
        else if (_navigator.PlaybackToken != _shownToken)
        {
            StartStory();
        }
    }

    /// <summary>Back and Next follow the page; the current step reads as selected.</summary>
    private void UpdateChrome()
    {
        _back.Visibility = _navigator.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = _navigator.CanGoForward ? Visibility.Visible : Visibility.Collapsed;
        foreach (ParityButton step in _steps)
        {
            step.AccessibleSelected = _steps.IndexOf(step) == (int)_navigator.Page;
        }
    }

    private TourContext Context() => new()
    {
        Dark = _root.ActualTheme == ElementTheme.Dark,
        Snapshot = _snapshot(),
        Graphics = _graphics,
        ReducesMotion = ReducesMotion,
        Replay = _navigator.Replay,
        OpenTaskbarSettings = OpenTaskbarSettings,
        SharingExpanded = () => _sharingExpanded,
        SetSharingExpanded = expanded => _sharingExpanded = expanded,
    };

    /// <summary>
    /// Puts the navigator's page on screen. Animated: the old page clears in 0.14 s with an ease-out, moving half the
    /// distance away, while the new one slides 28 points in on the page spring after 0.1 s (Mac IntroductionPageTransition,
    /// without its 2-point blur); with Reduce Motion they crossfade over 0.2 s.
    /// </summary>
    private void ShowPage(bool animated)
    {
        TourPage? old = _page;
        _shownPage = _navigator.Page;
        TourPage page = TourPage.Create(_navigator.Page, Context());
        _page = page;
        _pages.Children.Add(page.Root);
        StartStory();
        if (old is null)
        {
            return;
        }
        if (!animated)
        {
            _pages.Children.Remove(old.Root);
            return;
        }
        old.Root.IsHitTestVisible = false;
        bool reduce = ReducesMotion();
        float sign = _navigator.Direction == IntroductionDirection.Forward ? 1 : -1;
        double removal;
        if (reduce)
        {
            MotionSamples fade = ToolbarControlMotion.Timed(new TimedMotion(IntroductionMotion.ReducedDuration, IntroductionMotion.ReducedCurve), 0, 1);
            page.Root.Opacity = 1;
            ToolbarMotion.Scalar(page.Root, "Opacity", fade, value => (float)value);
            ToolbarMotion.Scalar(old.Root, "Opacity", fade, value => (float)(1 - value));
            removal = IntroductionMotion.ReducedDuration;
        }
        else
        {
            MotionSamples spring = Delayed(ToolbarControlMotion.Spring(IntroductionMotion.Page, 0, 1), IntroductionMotion.InsertionDelay);
            page.Root.Opacity = 1;
            page.Root.Translation = Vector3.Zero;
            ToolbarMotion.Scalar(page.Root, "Opacity", spring, value => (float)value);
            ToolbarMotion.Vector(page.Root, "Translation", spring,
                value => new Vector3((float)((1 - value) * IntroductionMotion.PageOffset) * sign, 0, 0));
            MotionSamples clear = ToolbarControlMotion.Timed(new TimedMotion(IntroductionMotion.RemovalDuration, IntroductionMotion.RemovalCurve), 0, 1);
            ToolbarMotion.Scalar(old.Root, "Opacity", clear, value => (float)(1 - value));
            ToolbarMotion.Vector(old.Root, "Translation", clear,
                value => new Vector3((float)(-value * IntroductionMotion.PageOffset * 0.5) * sign, 0, 0));
            removal = IntroductionMotion.RemovalDuration;
        }
        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = _root.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(removal + 0.02);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => _pages.Children.Remove(old.Root);
        timer.Start();
    }

    /// <summary><paramref name="samples"/> after holding their first value for <paramref name="delay"/> seconds.</summary>
    private static MotionSamples Delayed(MotionSamples samples, double delay)
    {
        var times = new double[samples.Times.Count + 1];
        var values = new double[times.Length];
        values[0] = samples.Values[0];
        for (int index = 0; index < samples.Times.Count; index++)
        {
            times[index + 1] = delay + samples.Times[index];
            values[index + 1] = samples.Values[index];
        }
        return new MotionSamples(times, values);
    }

    /// <summary>
    /// Mac IntroductionStoryRun: the page starts at its end with Reduce Motion or while the tour is hidden, at a review
    /// moment when one is set, otherwise at its beginning, and then plays once after 0.3 s, linearly over its duration.
    /// </summary>
    private void StartStory()
    {
        if (_page is not { } page)
        {
            return;
        }
        _shownToken = _navigator.PlaybackToken;
        bool reduce = ReducesMotion();
        double progress = IntroductionStory.InitialProgress(reduce, _navigator.IsPresented, _navigator.ReviewProgress);
        page.Apply(progress, 0);
        _story = null;
        if (_navigator.ReviewProgress is not null || !_navigator.IsPresented)
        {
            return;
        }
        bool animated = !reduce && progress < 1;
        _navigator.NotePlayback(new IntroductionPlayback(page.Page, animated));
        if (!animated)
        {
            return;
        }
        _story = new StoryRun(page, ToolbarMotion.Now, Math.Max(0.01, _navigator.StoryTimeScale));
        StartRendering();
    }

    /// <summary>
    /// Mac IntroductionStepIndicator: moving forward, the capsule's front edge leads on a 0.32 s spring and its back edge
    /// follows on a 0.5 s spring 0.07 s later, so it stretches toward the step and settles; Back mirrors it. Reduce Motion
    /// moves it at once.
    /// </summary>
    private void MoveCapsule(int from, int to, bool reduce)
    {
        double now = ToolbarMotion.Now;
        if (reduce)
        {
            _lower = CapsuleEdge.At(to);
            _upper = CapsuleEdge.At(to);
            _capsuleMoving = false;
            PlaceCapsule();
            return;
        }
        // Forward, the upper edge leads; Back, the lower one.
        bool forward = to > from;
        CapsuleEdge Lead(CapsuleEdge edge) => new(edge.Value(now), to, now, IntroductionMotion.CapsuleLead);
        CapsuleEdge Follow(CapsuleEdge edge) => new(edge.Value(now), to, now + IntroductionMotion.CapsuleFollowDelay, IntroductionMotion.CapsuleFollow);
        (_lower, _upper) = forward ? (Follow(_lower), Lead(_upper)) : (Lead(_lower), Follow(_upper));
        _capsuleMoving = true;
        StartRendering();
    }

    private void PlaceCapsule()
    {
        double now = ToolbarMotion.Now;
        double width = WindowWidth - 48;
        (double start, double length) = IntroductionMotion.Capsule(_lower.Value(now), _upper.Value(now), IntroductionPageExtensions.All.Count, width);
        _capsule.Margin = new Thickness(start, 0, 0, 0);
        _capsule.Width = Math.Max(0, length);
    }

    /// <summary>
    /// Frames come from a 60 Hz timer on the window's thread rather than every display refresh: CompositionTarget.Rendering
    /// keeps the XAML frame loop running at the display's rate, which on a 240 Hz display cost more than drawing.
    /// </summary>
    private void StartRendering()
    {
        if (_rendering)
        {
            return;
        }
        _rendering = true;
        if (_timer is null)
        {
            _timer = _root.DispatcherQueue.CreateTimer();
            // Shorter than a frame: Windows' default timer resolution (15.6 ms) would otherwise turn 16.7 ms into 31 ms.
            _timer.Interval = TimeSpan.FromMilliseconds(8);
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => OnRendering(null, EventArgs.Empty);
        }
        _timer.Start();
        OnRendering(null, EventArgs.Empty);
    }

    private void StopRendering()
    {
        if (_rendering)
        {
            _rendering = false;
            _timer?.Stop();
        }
    }

    /// <summary>
    /// One frame while a story or the capsule moves; nothing runs once both rest. Frames come at most 60 times a second:
    /// on a faster display the stories look the same and cost a quarter.
    /// </summary>
    private void OnRendering(object? sender, object args)
    {
        double now = ToolbarMotion.Now;
        if (now - _lastFrame < FrameInterval)
        {
            return;
        }
        _lastFrame = now;
        bool busy = false;
        if (_story is { } story)
        {
            double elapsed = (now - story.Started) / story.TimeScale;
            double progress = IntroductionEase.Clamp((elapsed - IntroductionMotion.StoryDelay) / story.Page.Page.StoryDuration);
            if (ReferenceEquals(story.Page, _page))
            {
                story.Page.Apply(progress, elapsed);
            }
            if (progress >= 1)
            {
                _navigator.NoteFinished(story.Page.Page);
                _story = null;
                // Measurement runs only (ANNOTATOR_STARTUP_LOG): how many frames the story drew, and their drawing time.
                StartupLog.Note("tour-story", FormattableString.Invariant(
                    $"{story.Page.Page} {story.Page.Illustration?.Frames ?? 0} frames in {elapsed:F2} s, {story.Page.Illustration?.DrawMilliseconds ?? 0:F1} ms drawing, warp {_graphics.UsesWarp}"));
            }
            else
            {
                busy = true;
            }
        }
        if (_capsuleMoving)
        {
            _capsuleMoving = !_lower.Settled(now) || !_upper.Settled(now);
            PlaceCapsule();
            busy |= _capsuleMoving;
        }
        if (!busy)
        {
            StopRendering();
        }
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size (WIN-UI-07, WIN-A11Y-03): the pages and the indicator
    /// are made again, at the same moment.
    /// </summary>
    public void Restyle() => Rebuild();

    /// <summary>The theme changed: the pages and the indicator are made again in the new colors, at the same moment.</summary>
    private void Rebuild()
    {
        if (_page is not { } old)
        {
            return;
        }
        _pages.Children.Clear();
        TourPage page = TourPage.Create(old.Page, Context());
        _page = page;
        _pages.Children.Add(page.Root);
        if (_story is { } story && ReferenceEquals(story.Page, old))
        {
            _story = story with { Page = page };
        }
        else
        {
            page.Apply(old.Illustration?.Progress ?? 1, 0);
        }
        bool dark = _root.ActualTheme == ElementTheme.Dark;
        foreach (ParityButton step in _steps)
        {
            if (step.Content is Border track)
            {
                StyleTrack(track, dark);
            }
        }
        _capsule.Background = TourParts.Accent(1);
    }

    /// <summary>Mac IntroductionPanel.sendEvent: Left and Right without modifiers move between pages.</summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right) || AnyModifier() ||
            FocusManager.GetFocusedElement(_root.XamlRoot) is TextBox)
        {
            return;
        }
        args.Handled = true;
        _navigator.Move(args.Key == VirtualKey.Right ? IntroductionDirection.Forward : IntroductionDirection.Backward);
    }

    private static bool AnyModifier()
    {
        foreach (VirtualKey key in new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift, VirtualKey.LeftWindows, VirtualKey.RightWindows })
        {
            if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The Mac's Reduce Motion is Windows' Animation effects setting turned off (WIN-MOT-03).</summary>
    private static bool ReducesMotion() => Motion.SystemMotion.ReducesMotion();

    /// <summary>DV-4: Windows Settings > Personalization > Taskbar, where Other system tray icons keeps the icon visible.</summary>
    private static void OpenTaskbarSettings() => _ = Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar"));

    private sealed record StoryRun(TourPage Page, double Started, double TimeScale);

    /// <summary>One edge of the step capsule, in steps: a spring from one step to another that starts at a time.</summary>
    private readonly record struct CapsuleEdge(double From, double To, double Start, SpringMotion? Spring)
    {
        public static CapsuleEdge At(int step) => new(step, step, 0, null);

        public double Value(double now)
        {
            if (Spring is not { } spring || now >= Start + spring.SettleTime())
            {
                return To;
            }
            double time = now - Start;
            return time <= 0 ? From : From + (To - From) * spring.Value(time);
        }

        public bool Settled(double now) => Spring is not { } spring || now >= Start + spring.SettleTime();
    }
}
