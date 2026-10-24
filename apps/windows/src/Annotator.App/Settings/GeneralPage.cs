using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Updates;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Settings;

/// <summary>
/// Settings > General (the Mac's CustomizationView.general): the grouped Form with Startup, Drawing, Appearance, Updates
/// and Privacy, then the coffee box. Check for updates... follows the Store update service (WIN-SYS-03). Share usage counts
/// follows the app's usage counts (the Mac's UsageMetricsSettings), which say they are unavailable while Windows may not
/// send them (WIN-PRV-01).
/// </summary>
internal sealed partial class GeneralPage
{
    private static readonly (string Title, AppTheme Theme)[] s_themes = [("System", AppTheme.System), ("Light", AppTheme.Light), ("Dark", AppTheme.Dark)];

    private static readonly (string Title, VisibilityAnimationStyle Style)[] s_animations =
    [
        ("Dissolve", VisibilityAnimationStyle.Dissolve), ("Gather", VisibilityAnimationStyle.Slide), ("Magic", VisibilityAnimationStyle.Magic),
        ("Corners", VisibilityAnimationStyle.Corners), ("Pop", VisibilityAnimationStyle.Pop), ("Drop", VisibilityAnimationStyle.Drop),
        ("Soft focus", VisibilityAnimationStyle.Focus), ("Fade", VisibilityAnimationStyle.Fade), ("Off", VisibilityAnimationStyle.None),
    ];

    private readonly SettingsActions _actions;
    private readonly LaunchAtSignInView _launch;
    private readonly SettingsSwitch _tray = new(SettingsCopy.ShowTrayIcon);
    private readonly SettingsSwitch _share = new(SettingsCopy.ShareStyles);
    private readonly SettingsSwitch _tips = new(SettingsCopy.ShowTips);
    private readonly SettingsSwitch _usageSwitch = new(SettingsCopy.ShareUsageCounts);
    private readonly FormText _usageStatus;
    private readonly Annotator.Workspace.Metrics.UsageMetrics _usage;
    private readonly SettingsPopUp _theme;
    private readonly SettingsPopUp _animation;

    public GeneralPage(SettingsActions actions)
    {
        _actions = actions;
        WorkspacePreferences preferences = actions.Model.Snapshot.Preferences;
        _launch = new LaunchAtSignInView(actions.Launch, inForm: true);
        _theme = new SettingsPopUp(SettingsCopy.Theme, [.. s_themes.Select(item => item.Title)], ThemeIndex(preferences));
        _animation = new SettingsPopUp(SettingsCopy.WorkspaceAnimation, [.. s_animations.Select(item => item.Title)], AnimationIndex(preferences));

        _tray.Toggled += visible => Act(() => actions.Model.SetTrayIconVisible(visible));
        _share.Toggled += shared => Act(() => actions.Model.SetShareToolStyles(shared));
        _tips.Toggled += visible => Act(() => actions.Model.SetShortcutHints(visible));
        AutomationProperties.SetAutomationId(_share, "share-tool-styles");
        _theme.Chosen += index => Act(() => actions.Model.SetTheme(s_themes[index].Theme));
        _animation.Chosen += index => Act(() => actions.Model.SetAnimationStyle(s_animations[index].Style));
        SettingsHelp.Set(_tray, SettingsCopy.ShowTrayIconHelp);
        SettingsHelp.Set(_theme, SettingsCopy.ThemeHelp);
        SettingsHelp.Set(_tips, SettingsCopy.ShowTipsHelp);
        SettingsHelp.Set(_animation, SettingsCopy.WorkspaceAnimationHelp);
        var introduction = new SettingsButton(SettingsCopy.ShowIntroduction, ButtonLook.Bordered, SettingsWidths.Button(SettingsCopy.ShowIntroduction));
        introduction.Click += (_, _) => actions.ShowIntroduction?.Invoke();

        var form = new MacStack { HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(SettingsForm.Header(SettingsCopy.Startup, first: true));
        form.Children.Add(SettingsForm.Card(
        [
            new FormRow(_launch, trailingInset: 9) { TopAligned = true },
            SettingsForm.Toggle(SettingsCopy.ShowTrayIcon, _tray),
            SettingsForm.Single(introduction),
        ]));
        form.Children.Add(SettingsForm.Header(SettingsCopy.Drawing, first: false));
        form.Children.Add(SettingsForm.Card(
        [
            SettingsForm.Toggle(SettingsCopy.ShareStyles, _share),
            SettingsForm.Single(SettingsForm.Caption(SettingsCopy.ShareStylesDetail, wrapWidth: SettingsForm.RowWidth)),
        ]));
        form.Children.Add(SettingsForm.Header(SettingsCopy.Appearance, first: false));
        form.Children.Add(SettingsForm.Card(
        [
            SettingsForm.PopUp(SettingsCopy.Theme, _theme),
            SettingsForm.Toggle(SettingsCopy.ShowTips, _tips),
            SettingsForm.PopUp(SettingsCopy.WorkspaceAnimation, _animation),
            SettingsForm.Single(Indented(SettingsLabel.Make("eye.slash", SettingsCopy.HidingHidesCovers, 10, "AnnotatorPrimaryText", "AnnotatorGlyph", 11, 4))),
        ]));
        form.Children.Add(SettingsForm.Header(SettingsCopy.Updates, first: false));
        // The Microsoft Store manages a Store copy's automatic updates (WIN-SYS-03), so its switch shows them and stays
        // disabled; a portable copy checks the website itself, and its switch works as the Mac's.
        var check = new SettingsButton(SettingsCopy.CheckForUpdates, ButtonLook.Bordered, SettingsWidths.Button(SettingsCopy.CheckForUpdates));
        check.Click += (_, _) => actions.CheckForUpdates();
        bool switchable = actions.Updates.CanChangeAutomaticChecks;
        var automatic = new SettingsSwitch(SettingsCopy.AutomaticChecks) { IsEnabled = switchable };
        automatic.Toggled += on => actions.Updates.SetAutomaticChecks(on);
        FormText updateStatus = SettingsForm.Caption(actions.Updates.Status, wrapWidth: SettingsForm.RowWidth);
        // A portable copy that found a release says where to get it; Annotator downloads nothing itself.
        var downloads = new SettingsButton(UpdateText.OpenDownloads, ButtonLook.Bordered, SettingsWidths.Button(UpdateText.OpenDownloads));
        downloads.Click += (_, _) => actions.OpenUpdateDownloads();
        var winget = new SettingsButton(UpdateText.CopyWinget, ButtonLook.Bordered, SettingsWidths.Button(UpdateText.CopyWinget));
        winget.Click += (_, _) => actions.CopyWingetCommand();
        AutomationProperties.SetAutomationId(downloads, "update-open-downloads");
        AutomationProperties.SetAutomationId(winget, "update-copy-winget");
        var release = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        release.Children.Add(downloads);
        release.Children.Add(winget);
        FrameworkElement statusBlock = updateStatus;
        if (actions.Updates.ChecksFeed)
        {
            var block = new MacStack();
            block.Children.Add(updateStatus);
            block.Children.Add(release);
            statusBlock = block;
        }
        UpdatesBinding.Follow(updateStatus, actions.Updates, () =>
        {
            check.IsEnabled = actions.Updates.CanCheck;
            automatic.IsOn = actions.Updates.AutomaticChecks;
            updateStatus.Text = actions.Updates.Status;
            release.Visibility = actions.Updates.Release is not null ? Visibility.Visible : Visibility.Collapsed;
        });
        form.Children.Add(SettingsForm.Card(
        [
            SettingsForm.Single(check),
            SettingsForm.Toggle(SettingsCopy.AutomaticChecks, automatic, enabled: switchable),
            SettingsForm.Single(statusBlock),
            SettingsForm.Single(new SettingsDisclosure(SettingsCopy.WhatUpdateCheckSends, null, 13,
                () => SettingsForm.Caption(SettingsCopy.UpdateCheckSends, wrapWidth: SettingsForm.RowWidth - 12), contentTop: 6)),
        ]));
        // The Mac's UsageMetricsSettings: the switch, the status under it and what is shared.
        _usage = actions.UsageMetrics;
        _usageSwitch.IsEnabled = _usage.Available;
        _usageSwitch.Toggled += on =>
        {
            _usage.SetEnabled(on);
            ShowUsage();
        };
        _usageStatus = SettingsForm.Caption(_usage.Status, wrapWidth: SettingsForm.RowWidth);
        var privacyDetails = new SettingsButton(SettingsCopy.PrivacyDetails, ButtonLook.Link, SettingsWidths.Button(SettingsCopy.PrivacyDetails), height: 16);
        privacyDetails.Click += (_, _) => actions.OpenProductPage(ProductPage.Privacy);
        form.Children.Add(SettingsForm.Card(
        [
            SettingsForm.Single(SettingsForm.Heading(SettingsCopy.Privacy)),
            SettingsForm.Toggle(SettingsCopy.ShareUsageCounts, _usageSwitch, enabled: _usage.Available),
            SettingsForm.Single(_usageStatus),
            SettingsForm.Single(new SettingsDisclosure(SettingsCopy.WhatsShared, null, 13,
                () => SettingsForm.Caption(SettingsCopy.WhatsSharedDetail, wrapWidth: SettingsForm.RowWidth - 12), contentTop: 6)),
            SettingsForm.Single(privacyDetails),
        ], top: 10));

        ScrollViewer scroll = SettingsForm.Scroll(form, new Thickness(SettingsForm.Inset, 20, SettingsForm.Inset, 20));
        scroll.Background = SettingsLook.Window;
        Root = new Page(scroll, new Pad(CoffeeView(actions), new Thickness(16)) { HorizontalAlignment = HorizontalAlignment.Stretch });
        _usage.Changed += ShowUsage;
        ShowUsage();
        Apply(actions.Model.Snapshot);
    }

    public FrameworkElement Root { get; }

    /// <summary>Shows the saved preferences; the controls never keep a state of their own.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        WorkspacePreferences preferences = snapshot.Preferences;
        _tray.IsOn = preferences.ShowMenuBarIcon ?? true;
        _share.IsOn = preferences.ShareToolStyles == true;
        _tips.IsOn = preferences.ShowShortcutHints ?? true;
        _theme.ChosenIndex = ThemeIndex(preferences);
        _animation.ChosenIndex = AnimationIndex(preferences);
    }

    public void Detach()
    {
        _launch.Detach();
        _usage.Changed -= ShowUsage;
    }

    /// <summary>The switch and the line under it follow usage counts; the switch never keeps a state of its own.</summary>
    private void ShowUsage()
    {
        _usageSwitch.IsOn = _usage.Enabled;
        if (_usageStatus.Text != _usage.Status)
        {
            _usageStatus.Text = _usage.Status;
            _usageStatus.InvalidateMeasure();
        }
    }

    private static int ThemeIndex(in WorkspacePreferences preferences)
    {
        AppTheme theme = preferences.Theme ?? AppTheme.System;
        return Array.FindIndex(s_themes, item => item.Theme == theme);
    }

    private static int AnimationIndex(in WorkspacePreferences preferences)
    {
        VisibilityAnimationStyle style = WorkspaceModel.ShownAnimationStyle(preferences);
        int index = Array.FindIndex(s_animations, item => item.Style == style);
        return index < 0 ? 1 : index;
    }

    /// <summary>SwiftUI's Label starts its icon 1.5 points in from the row's edge.</summary>
    private static FormText Indented(FormText label)
    {
        label.Margin = new Thickness(1.5, 0, 0, 0);
        return label;
    }

    private static T Disabled<T>(T control) where T : Control
    {
        control.IsEnabled = false;
        return control;
    }

    /// <summary>
    /// Runs a change, then shows the workspace's answer, so a refused change puts the control back; the workspace reports why,
    /// and the error window shows it.
    /// </summary>
    private void Act(Action change)
    {
        change();
        Apply(_actions.Model.Snapshot);
    }

    /// <summary>
    /// The Mac's CoffeeSupportView, padded 16: the cup, two lines and Buy me a coffee in the coffee color on a 10% orange
    /// box with 12-point corners. The button is disabled when the build has no support link.
    /// </summary>
    internal static FrameworkElement CoffeeView(SettingsActions actions) =>
        CoffeeView(actions.Environment.SupportLink is not null, actions.BuyCoffee);

    /// <param name="available">The build has a support link; otherwise the button is disabled.</param>
    /// <param name="buy">Opens the support link (Mac openOptionalSupport).</param>
    /// <param name="width">
    /// The box's width in a narrow window (What's changed): the second line wraps in the room the cup and button leave,
    /// as SwiftUI's Text does, and the row grows with it. Settings leaves it null: one line each, 36 points high.
    /// </param>
    internal static FrameworkElement CoffeeView(bool available, Action buy, double? width = null)
    {
        FrameworkElement cup = SettingsIcons.Make("cup.and.saucer.fill", 30, SettingsLook.Coffee);
        cup.Width = 39;
        cup.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(cup, AccessibilityView.Raw);
        double? buttonWidth = SettingsWidths.Button(SettingsCopy.BuyMeACoffee);
        // The room between the cup and the button: the box less its padding, the cup, three gaps and the button.
        double? textWidth = width is { } boxWidth ? boxWidth - 36 - 39 - 3 * 16 - (buttonWidth ?? 122) : null;
        var texts = new MacStack { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new FormText(SettingsCopy.EnjoyingAnnotator, 13, weight: FontWeights.SemiBold, wrapWidth: textWidth));
        texts.Children.Add(new FormText(SettingsCopy.CoffeeWelcome, 12, "AnnotatorSecondaryText", wrapWidth: textWidth));
        var button = new SettingsButton(SettingsCopy.BuyMeACoffee, ButtonLook.Prominent, buttonWidth, tint: SettingsLook.Coffee)
        {
            IsEnabled = available,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => buy();
        // HStack(spacing: 16) { cup; VStack(spacing: 5); Spacer; button }.padding(18), 72 points high with these lines.
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (width is null)
        {
            row.Height = 36;
        }
        row.Children.Add(cup);
        row.Children.Add(texts);
        row.Children.Add(new MacSpacer());
        row.Children.Add(button);
        var box = new Grid
        {
            CornerRadius = new CornerRadius(12),
            Background = SettingsLook.Tint(SettingsLook.Orange, 0.10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.Children.Add(new Pad(row, new Thickness(18)) { HorizontalAlignment = HorizontalAlignment.Stretch });
        return box;
    }

    /// <summary>The Mac's VStack(spacing: 0) { Form; coffee }: the Form takes the height the coffee box leaves, in exact points.</summary>
    private sealed partial class Page : Panel
    {
        private readonly FrameworkElement _form;
        private readonly FrameworkElement _footer;

        public Page(FrameworkElement form, FrameworkElement footer)
        {
            _form = form;
            _footer = footer;
            Children.Add(form);
            Children.Add(footer);
        }

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            _footer.Measure(new Windows.Foundation.Size(availableSize.Width, double.PositiveInfinity));
            double footer = MacStack.Extent(_footer, horizontal: false).Along;
            double height = double.IsInfinity(availableSize.Height) ? footer : availableSize.Height;
            _form.Measure(new Windows.Foundation.Size(availableSize.Width, Math.Max(0, height - footer)));
            return new Windows.Foundation.Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            Windows.Foundation.Point residual = MacStack.InheritedResidual(this);
            double footer = MacStack.Extent(_footer, horizontal: false).Along;
            double formHeight = Math.Max(0, finalSize.Height - footer);
            MacStack.ArrangeExact(_form, residual.X, residual.Y, finalSize.Width, formHeight, default, scale);
            MacStack.ArrangeExact(_footer, residual.X, residual.Y + formHeight, finalSize.Width, footer, default, scale);
            return finalSize;
        }
    }
}

/// <summary>
/// The Mac's LoginItemSettings for launch at sign-in (WIN-SYS-01): the switch, the state in words, an error in red,
/// Open Startup apps... and the note that Windows manages it. In the Form the switch sits at the row's trailing edge;
/// on its own (the reference's launch-at-login states) it is a check box with its title, as SwiftUI draws a Toggle
/// outside a Form.
/// </summary>
internal sealed partial class LaunchAtSignInView : Grid
{
    private readonly LaunchAtSignIn _controller;
    private readonly SettingsSwitch? _switch;
    private readonly LaunchCheckBox? _checkBox;
    private readonly FormText _state;
    private readonly FormText _error;
    private readonly SettingsButton _open;

    public LaunchAtSignInView(LaunchAtSignIn controller, bool inForm)
    {
        _controller = controller;
        var stack = new MacStack { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (inForm)
        {
            _switch = new SettingsSwitch(SettingsCopy.LaunchTitle)
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -0.75, 0, -0.75),
            };
            AutomationProperties.SetAutomationId(_switch, "launch-at-login");
            SettingsHelp.Set(_switch, SettingsCopy.LaunchHelp);
            _switch.Toggled += Toggle;
            var line = new Grid { Height = 16.5, HorizontalAlignment = HorizontalAlignment.Stretch };
            line.Children.Add(SettingsForm.Body(SettingsCopy.LaunchTitle));
            line.Children.Add(_switch);
            stack.Children.Add(line);
        }
        else
        {
            _checkBox = new LaunchCheckBox(SettingsCopy.LaunchTitle);
            AutomationProperties.SetAutomationId(_checkBox, "launch-at-login");
            SettingsHelp.Set(_checkBox, SettingsCopy.LaunchHelp);
            _checkBox.Toggled += Toggle;
            stack.Children.Add(_checkBox);
        }
        _state = SettingsForm.Caption(string.Empty, wrapWidth: SettingsForm.RowWidth);
        _error = SettingsForm.Caption(string.Empty, wrapWidth: SettingsForm.RowWidth);
        _error.Block.Foreground = SettingsLook.Red;
        _open = new SettingsButton(SettingsCopy.OpenStartupApps, ButtonLook.Bordered);
        _open.Click += (_, _) => controller.OpenSettings();
        stack.Children.Add(_state);
        stack.Children.Add(_error);
        stack.Children.Add(_open);
        stack.Children.Add(SettingsForm.Caption(SettingsCopy.LaunchManaged, wrapWidth: SettingsForm.RowWidth));
        Children.Add(stack);
        HorizontalAlignment = inForm ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        controller.Changed += Show;
        Show();
    }

    public void Detach() => _controller.Changed -= Show;

    private void Toggle(bool enabled) => _ = _controller.SetEnabledAsync(enabled);

    private void Show()
    {
        LaunchAtSignInState state = _controller.State;
        bool available = !_controller.IsChanging && state.CanChange;
        if (_switch is not null)
        {
            _switch.IsOn = state.IsRequested;
            _switch.IsEnabled = available;
        }
        if (_checkBox is not null)
        {
            _checkBox.IsOn = state.IsRequested;
            _checkBox.IsEnabled = available;
        }
        _state.Text = _controller.IsChanging ? SettingsCopy.LaunchUpdating : state.Message;
        _error.Text = _controller.ErrorMessage ?? string.Empty;
        _error.Visibility = _controller.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
        _open.IsEnabled = !_controller.IsChanging;
        InvalidateMeasure();
    }

    /// <summary>
    /// The Mac's checkbox Toggle with its title: a 14-point box and the words, read as one check box (Toggle pattern,
    /// value 1 when on) named by the title.
    /// </summary>
    private sealed partial class LaunchCheckBox : Button
    {
        private readonly Border _box = new()
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3.5),
            Margin = new Thickness(1, 1.25, 0, 1.25),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private bool _on;

        public LaunchCheckBox(string title)
        {
            Style = AppResources.Style("AnnotatorPlainButton");
            Height = 16.5;
            CornerRadius = new CornerRadius(4);
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            var text = new TextBlock
            {
                Text = title,
                FontSize = 13,
                LineHeight = 16,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(21, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = AppResources.Style("AnnotatorPrimaryText"),
                IsHitTestVisible = false,
            };
            AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
            var look = new Grid { IsHitTestVisible = false };
            look.Children.Add(_box);
            look.Children.Add(text);
            Content = look;
            Width = SettingsButton.NaturalWidth(title, ButtonLook.Link) + 21;
            AutomationProperties.SetName(this, title);
            Click += (_, _) => Toggled?.Invoke(!_on);
            IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;
            ShowCheck();
        }

        public event Action<bool>? Toggled;

        public bool IsOn
        {
            get => _on;
            set
            {
                if (_on == value)
                {
                    return;
                }
                _on = value;
                ShowCheck();
                if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
                {
                    peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                        value ? ToggleState.Off : ToggleState.On, value ? ToggleState.On : ToggleState.Off);
                }
            }
        }

        private void ShowCheck()
        {
            _box.Background = _on ? AppResources.Brush("AnnotatorAccentBrush") : AppResources.Brush("AnnotatorWindowBackgroundBrush");
            _box.BorderBrush = _on ? null : Pickers.PickerColors.Primary(0.3);
            _box.BorderThickness = new Thickness(_on ? 0 : 1);
            if (!_on)
            {
                _box.Child = null;
                return;
            }
            var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(3.6, 7.3), IsClosed = false, IsFilled = false };
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(6, 9.7) });
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(10.5, 4.4) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            _box.Child = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                // A contrast theme checks the box in Highlight with a HighlightText mark.
                Stroke = ContrastTheme.Pick(ContrastColor.HighlightText, new SolidColorBrush(Microsoft.UI.Colors.White)),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed partial class Peer(LaunchCheckBox owner) : ButtonAutomationPeer(owner), Microsoft.UI.Xaml.Automation.Provider.IToggleProvider
        {
            public ToggleState ToggleState => owner._on ? ToggleState.On : ToggleState.Off;

            public void Toggle()
            {
                if (owner.IsEnabled)
                {
                    owner.Toggled?.Invoke(!owner._on);
                }
            }

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.CheckBox;

            protected override string GetClassNameCore() => "CheckBox";

            protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

            protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface switch
            {
                PatternInterface.Toggle => this,
                PatternInterface.Invoke => null,
                _ => base.GetPatternCore(patternInterface),
            };

            protected override Windows.Foundation.Rect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
        }
    }
}
/// <summary>Help text (the Mac's .help): the tooltip and the UI Automation help text.</summary>
internal static class SettingsHelp
{
    public static void Set(FrameworkElement element, string help)
    {
        AutomationProperties.SetHelpText(element, help);
        ToolTipService.SetToolTip(element, help);
    }
}

/// <summary>Icons in Settings drawn in one color, as SwiftUI's foregroundStyle tints an SF Symbol.</summary>
internal static class SettingsIcons
{
    public static FrameworkElement Make(string symbol, double size, WinColor color, double strokeWidth = 2)
    {
        FrameworkElement icon = Icons.Glyphs.ForSymbol(symbol, size, "AnnotatorGlyph", strokeWidth);
        if (icon is Viewbox { Child: Canvas canvas } && canvas.Children.Count > 0 && canvas.Children[0] is Microsoft.UI.Xaml.Shapes.Path path)
        {
            var brush = new SolidColorBrush(color);
            path.Stroke = brush;
            if (path.Fill is not null)
            {
                path.Fill = brush;
            }
        }
        return icon;
    }
}
