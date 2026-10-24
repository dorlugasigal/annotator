using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Sharing;
using Annotator.Workspace.Shortcuts;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;
using Windows.Win32.Foundation;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Shell;

/// <summary>
/// The tray flyout (WIN-UI-01): the Mac menu-bar popover's content for the features that exist, the same
/// words and automation identifiers, plus any global shortcut that could not be registered (WIN-INP-05).
/// </summary>
internal sealed class TrayFlyout : PopoverWindow
{
    private readonly Action<TrayAction> _choose;
    private readonly Func<HWND, HWND> _focusTarget;
    private readonly bool _keyboard;
    private readonly bool _canCheckUpdates;
    private readonly string _updatesStatus;
    private readonly bool _updateAvailable;
    private Button? _primary;
    private Border? _primaryRing;
    private ParityButton? _board;
    private Border? _boardChevron;
    private FrameworkElement? _boardRows;
    private MacStack? _content;
    private bool _boardExpanded;
    private bool _saveAvailable;

    /// <param name="focusTarget">Where focus goes when the flyout closes, given the window active before it opened.</param>
    /// <param name="developerPreview">Shows the Local build badge of development builds.</param>
    /// <param name="canCheckUpdates">Check for updates… works now (the Mac's canCheckForUpdates).</param>
    /// <param name="updatesStatus">The update status, shown under Check for updates… while it does not work (the Mac's updateStatus).</param>
    /// <param name="updateAvailable">
    /// A check found a newer release: the row reads Update available… and the status shows under it (the Mac's
    /// updateReminderVersion).
    /// </param>
    public TrayFlyout(WindowRegistry registry, WorkspaceSnapshot snapshot, string? trayProblem, bool openedWithKeyboard,
                      Func<HWND, HWND> focusTarget, Action<TrayAction> choose, bool developerPreview, bool canCheckUpdates,
                      string updatesStatus, bool updateAvailable = false)
        : base(registry, "Annotator")
    {
        _choose = choose;
        _focusTarget = focusTarget;
        _keyboard = openedWithKeyboard;
        _canCheckUpdates = canCheckUpdates;
        _updatesStatus = updatesStatus;
        _updateAvailable = updateAvailable;
        // MenuBarPopoverView: a VStack with no spacing and 16 points of padding, 320 wide, inside a scroll view that
        // is the popover itself.
        var content = new MacStack { Width = 288, Margin = new Thickness(16) };
        content.Children.Add(Header());
        if (developerPreview)
        {
            content.Children.Add(LocalBuildBadge());
        }
        Build(content, snapshot, trayProblem);
        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // In the flyout's own points when it grows with the text size (WIN-A11Y-03).
            MaxHeight = Math.Max(240, (WindowChrome.MonitorAtCursor() is var monitor ? monitor.WorkArea.Height / monitor.Scale - 60 : 640) / TextScaling.Zoom),
        };
        var surface = new Grid { Width = 320 };
        surface.Children.Add(new Border { Background = AppResources.Brush("AnnotatorMenuBackgroundBrush"), CornerRadius = new CornerRadius(14) });
        surface.Children.Add(scroller);
        // WinUI rounds a border's outer edge by its radius plus its thickness: 13 keeps the stroke on the 14-point edge.
        surface.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            BorderBrush = AppResources.Brush("AnnotatorSurfaceStrokeBrush"),
            IsHitTestVisible = false,
        });
        surface.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        Surface = surface;
    }

    protected override void FocusFirst() => _primary?.Focus(_keyboard ? FocusState.Keyboard : FocusState.Programmatic);

    /// <summary>
    /// Development only (UX reference capture, which shows the flyout without focus): draws the primary button's focus
    /// ring, as the opened flyout and the Mac popover do.
    /// </summary>
    public void ShowOpeningFocusForReference()
    {
        if (_primaryRing is not null)
        {
            _primaryRing.Opacity = 1;
        }
    }

    protected override HWND FocusTarget(HWND previous) => _focusTarget(previous);

    protected override bool OnKey(VirtualKey key, bool control)
    {
        if (control && key == VirtualKey.Q)
        {
            Choose(TrayAction.Quit);
            return true;
        }
        if (control && key == (VirtualKey)0xBC)
        {
            // Ctrl+, is the Settings… row's shortcut.
            Choose(TrayAction.Settings);
            return true;
        }
        // The popover's key equivalents: Ctrl+S saves only with a drawing session, Ctrl+O always opens, whether or not the
        // Board rows show.
        if (control && key == VirtualKey.S && _saveAvailable)
        {
            Choose(TrayAction.SaveBoard);
            return true;
        }
        if (control && key == VirtualKey.O)
        {
            Choose(TrayAction.OpenBoard);
            return true;
        }
        if (!control && key == VirtualKey.Right && _board is not null && !_boardExpanded
            && ReferenceEquals(FocusManager.GetFocusedElement(_board.XamlRoot), _board))
        {
            ToggleBoard(expanded: true);
            return true;
        }
        if (!control && key == VirtualKey.Left && _boardExpanded)
        {
            ToggleBoard(expanded: false);
            _board?.Focus(FocusState.Keyboard);
            return true;
        }
        if (!control && key is VirtualKey.Down or VirtualKey.Up)
        {
            FocusManager.TryMoveFocus(key == VirtualKey.Down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous,
                new FindNextElementOptions { SearchRoot = Surface!.XamlRoot.Content });
            return true;
        }
        return false;
    }

    /// <summary>
    /// The popover's rows in its order: the primary action, the mode row, Stop zoom while zoom or the lens is on, shortcut
    /// problems, region sharing, Board, then Settings, Help, About, Check for updates, Buy me a coffee and Quit. A row whose
    /// feature is not built on Windows yet stays disabled (<see cref="TrayMenu.IsAvailable"/>).
    /// </summary>
    private void Build(MacStack content, WorkspaceSnapshot snapshot, string? trayProblem)
    {
        TrayAction primary = TrayMenu.PrimaryAction(snapshot);
        _primary = PrimaryButton(primary, TrayMenu.PrimaryTitle(snapshot),
            snapshot.ToolsPresented ? "eye.slash" : "pencil.tip",
            WindowsShortcuts.Label(GlobalShortcutAction.ShowHide, snapshot.Preferences));
        content.Children.Add(_primary);
        if (TrayMenu.ModeAction(snapshot) is { } mode)
        {
            content.Children.Add(mode == TrayAction.UseApps
                ? Row("Use apps", "cursorarrow", TrayAction.UseApps, null)
                : Row("Draw", "pencil.tip", TrayAction.Draw, WindowsShortcuts.Label(GlobalShortcutAction.ResumeDrawing, snapshot.Preferences)));
        }
        if (TrayMenu.ShowsStopZoom(snapshot))
        {
            content.Children.Add(Row("Stop zoom", "minus.magnifyingglass", TrayAction.StopZoom, snapshot.ZoomShortcutLabel));
        }
        foreach (GlobalShortcutState state in snapshot.GlobalShortcuts)
        {
            if (state.Status is GlobalShortcutStatus.AlreadyUsed or GlobalShortcutStatus.Refused && state.Message is { } message)
            {
                content.Children.Add(Problem(message));
            }
        }
        if (trayProblem is not null)
        {
            content.Children.Add(Problem(trayProblem));
        }
        bool sharing = snapshot.RegionSharing.IsCapturing;
        ParityButton toggle = Row(TrayMenu.SharingTitle(snapshot), sharing ? "stop.circle" : "rectangle.on.rectangle", TrayAction.ToggleRegionSharing, null,
            value: sharing ? "On" : "Off", tinted: sharing);
        if (sharing)
        {
            // Mac: the row sits on red at 8% (16% in dark) with a red outline at 40%, and a caption says sharing is on.
            var surface = new Grid();
            // A contrast theme outlines the row with Highlight on the window color instead.
            surface.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Background = ContrastTheme.Pick(ContrastColor.Window, new SolidColorBrush(SharingRed) { Opacity = Pickers.PickerColors.Dark ? 0.16 : 0.08 }),
                BorderBrush = ContrastTheme.Pick(ContrastColor.Highlight, new SolidColorBrush(SharingRed) { Opacity = 0.4 }),
                BorderThickness = new Thickness(ContrastTheme.IsOn ? 2 : 1),
                IsHitTestVisible = false,
            });
            surface.Children.Add(toggle);
            content.Children.Add(surface);
            var note = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 5, 10, 5) };
            note.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = ContrastTheme.Pick(ContrastColor.WindowText, new SolidColorBrush(SharingRed)), VerticalAlignment = VerticalAlignment.Center });
            note.Children.Add(Line(RegionSharingText.SharingOnCaption, 10, 13, FontWeights.Normal, "AnnotatorSecondaryText"));
            content.Children.Add(note);
        }
        else
        {
            content.Children.Add(toggle);
        }
        var halves = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 5, 0, 5) };
        halves.Children.Add(QuickRegion("Left half", TrayAction.ShareLeftHalf, TrayMenu.IsSharing(snapshot, Annotator.Core.ShareRegionPreset.LeftHalf)));
        halves.Children.Add(QuickRegion("Right half", TrayAction.ShareRightHalf, TrayMenu.IsSharing(snapshot, Annotator.Core.ShareRegionPreset.RightHalf)));
        content.Children.Add(halves);
        content.Children.Add(Row("Configure region...", "slider.horizontal.3", TrayAction.ShareRegion, null));
        AddBoard(content, snapshot);
        AddBreak(content, snapshot);
        content.Children.Add(Divider());
        content.Children.Add(Row("Settings…", "slider.horizontal.3", TrayAction.Settings, "Ctrl+,"));
        content.Children.Add(Row("Help", "questionmark.circle", TrayAction.Help, null));
        content.Children.Add(Row("About Annotator", "info.circle", TrayAction.About, null));
        content.Children.Add(Row(_updateAvailable ? Annotator.Workspace.Updates.UpdateText.UpdateAvailableRow : "Check for updates…",
            _updateAvailable ? "square.and.arrow.down" : "arrow.triangle.2.circlepath", TrayAction.CheckForUpdates, null, enabled: _canCheckUpdates));
        if (!_canCheckUpdates || _updateAvailable)
        {
            // Mac: Text(model.updateStatus).font(.caption) under the disabled row, inset like the rows' titles.
            var why = new Settings.FormText(_updatesStatus, 10, "AnnotatorSecondaryText", wrapWidth: 268);
            why.Block.Width = 268;
            content.Children.Add(new Settings.Pad(why, new Thickness(10, 0, 10, 0)) { HorizontalAlignment = HorizontalAlignment.Left });
        }
        Button support = Row("Enjoying it? Buy me a coffee…", "cup.and.saucer", TrayAction.Support, null);
        var supportSurface = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        supportSurface.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = AppResources.Brush("AnnotatorSupportFillBrush"),
            BorderBrush = AppResources.Brush("AnnotatorSupportStrokeBrush"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        });
        supportSurface.Children.Add(support);
        content.Children.Add(supportSurface);
        content.Children.Add(Divider());
        content.Children.Add(Row("Quit Annotator", "power", TrayAction.Quit, "Ctrl+Q"));
    }

    /// <summary>The Mac's systemRed in the app's theme.</summary>
    private static Windows.UI.Color SharingRed => Pickers.PickerColors.Dark
        ? Windows.UI.Color.FromArgb(255, 0xFF, 0x45, 0x3A)
        : Windows.UI.Color.FromArgb(255, 0xFF, 0x3B, 0x30);

    private static Rectangle Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 9, 0, 9),
        Style = AppResources.Style("AnnotatorDivider"),
    };

    /// <summary>The mark tile, the name and the version, as the Mac's HStack(spacing: 12).</summary>
    private static FrameworkElement Header()
    {
        var mark = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = MarkGeometry(),
            Fill = new SolidColorBrush(Color.FromArgb(255, 242, 240, 232)),
            Stretch = Stretch.Uniform,
            Width = 28,
            Height = 28,
        };
        var tile = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(255, 37, 43, 40)),
            Child = mark,
        };
        AutomationProperties.SetAccessibilityView(tile, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        // The app's mark keeps its colors in a contrast theme (WIN-UI-07).
        OwnColors.Mark(tile);
        var texts = new MacStack { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Line("Annotator", 20, 27, FontWeights.SemiBold, "AnnotatorPrimaryText"));
        texts.Children.Add(Line($"Version {BuildInfo.VersionLabel}", 11, 15, FontWeights.Normal, "AnnotatorSecondaryText"));
        var header = new MacStack { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(tile);
        header.Children.Add(texts);
        return header;
    }

    private static FrameworkElement LocalBuildBadge()
    {
        var badge = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 10, 0, 0) };
        badge.Children.Add(new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = ContrastTheme.Pick(ContrastColor.WindowText, new SolidColorBrush(Color.FromArgb(255, 232, 89, 64))),
            VerticalAlignment = VerticalAlignment.Center,
        });
        badge.Children.Add(Line("Local build", 10, 14, FontWeights.Medium, "AnnotatorSecondaryText"));
        return badge;
    }

    /// <summary>One line of text with the Mac's line height for its Avenir Next size.</summary>
    private static TextBlock Line(string text, double size, double lineHeight, Windows.UI.Text.FontWeight weight, string style) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        LineHeight = lineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Style = Text(style),
    };

    /// <summary>Text drawn inside a row; the row's name already says it, as on the Mac.</summary>
    private static TextBlock Hidden(TextBlock text)
    {
        AutomationProperties.SetAccessibilityView(text, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        text.IsHitTestVisible = false;
        return text;
    }

    /// <summary>The Mac's 46-point accent button with the icon, the title and the shortcut keycap; its value is the shortcut.</summary>
    private ParityButton PrimaryButton(TrayAction action, string title, string symbol, string shortcut)
    {
        var keycap = new Border
        {
            Style = AppResources.Style("AnnotatorKeycap"),
            Child = Hidden(new TextBlock { Text = shortcut, FontSize = 10, FontWeight = FontWeights.Medium, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Style = Text("AnnotatorMenuAccentText") }),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(keycap, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var grid = new Grid { ColumnSpacing = 9, Padding = new Thickness(12, 0, 12, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The SF Symbol's width at 16 points sets where the title starts (eye.slash is wide, pencil.tip narrow), and the
        // Lucide icon is drawn a little larger than 16 to match the symbol's size.
        (double width, double draw) = symbol == "eye.slash" ? (24.0, 20.0) : (12.5, 18.0);
        FrameworkElement icon = Toolbar.ToolbarParts.GlyphBox(symbol, width, draw, draw, "AnnotatorGlyphOnAccent");
        icon.VerticalAlignment = VerticalAlignment.Center;
        TextBlock label = Hidden(Line(title, 13, 16, FontWeights.SemiBold, "AnnotatorMenuAccentText"));
        Grid.SetColumn(label, 1);
        Grid.SetColumn(keycap, 2);
        grid.Children.Add(icon);
        grid.Children.Add(label);
        grid.Children.Add(keycap);
        var surface = new Border { Style = AppResources.Style("AnnotatorMenuPrimary"), Height = 46, Child = grid };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            Width = 288,
            Height = 46,
            Margin = new Thickness(0, 16, 0, 8),
            CornerRadius = new CornerRadius(10),
            AccessibleValue = shortcut,
        };
        button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Colors.Transparent);
        // The Mac draws a white ring 3 points inside the focused primary button, and focuses it when the popover opens.
        _primaryRing = FocusRing(button, surface, "AnnotatorMenuPrimaryFocusBrush", 1, 8, new Thickness(2.5));
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetAutomationId(button, action == TrayAction.HideTools ? "menu-hide-tools" : "menu-show-tools");
        button.Click += (_, _) => Choose(action);
        return button;
    }

    /// <summary>
    /// Mac MenuPopoverRow: a 36-point row with the icon in a 20-point column, the title, the shortcut and an optional
    /// trailing chevron. A row whose feature is not built is disabled and drawn in the disabled color.
    /// </summary>
    /// <param name="width">288 points, or 276 for the Board rows, which are indented by 12.</param>
    /// <param name="enabled">Whether the row can be used now, when that depends on the workspace (Save board... and Export PNG...).</param>
    /// <param name="trailingHost">Holds the trailing chevron, so the Board row can turn it.</param>
    private ParityButton Row(string title, string symbol, TrayAction action, string? shortcut, string? value = null, string? help = null,
                             string? trailingSymbol = null, bool tinted = false, double width = 288, bool? enabled = null,
                             Border? trailingHost = null)
    {
        bool available = enabled ?? TrayMenu.IsAvailable(action);
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 0, 10, 0), Height = 36 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // SF Symbols at 14 points draw about 14 to 18 points wide; Lucide icons fill less of their box, so they draw at 16.
        FrameworkElement icon = Glyphs.ForSymbol(symbol, 16, !available ? "AnnotatorGlyphDisabled" : tinted ? "AnnotatorGlyphError" : "AnnotatorGlyphSecondary");
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        TextBlock label = Hidden(Line(title, 13, 16, FontWeights.Medium, !available ? "AnnotatorDisabledText" : tinted ? "AnnotatorErrorText" : "AnnotatorPrimaryText"));
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (shortcut is not null)
        {
            TextBlock keys = Hidden(new TextBlock
            {
                Text = shortcut,
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Style = Text(available ? "AnnotatorSecondaryText" : "AnnotatorDisabledText"),
            });
            Grid.SetColumn(keys, 2);
            grid.Children.Add(keys);
        }
        else if (trailingSymbol is not null)
        {
            FrameworkElement chevron = Chevron(trailingSymbol, available);
            FrameworkElement trailing = chevron;
            if (trailingHost is not null)
            {
                trailingHost.Child = chevron;
                trailingHost.VerticalAlignment = VerticalAlignment.Center;
                trailing = trailingHost;
            }
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            Width = width,
            Height = 36,
            AccessibleValue = value,
            IsEnabled = available,
        };
        FocusRing(button, grid, "AnnotatorAccentBrush", 1.5, 7, new Thickness(-0.75));
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetAutomationId(button, TrayMenu.AutomationId(action));
        if (help is not null)
        {
            AutomationProperties.SetHelpText(button, help);
        }
        button.Click += (_, _) =>
        {
            if (action == TrayAction.Board)
            {
                ToggleBoard(expanded: !_boardExpanded);
            }
            else
            {
                Choose(action);
            }
        };
        return button;
    }

    /// <summary>The Mac's 10-point semibold chevron is 9 points wide: the Lucide chevron draws at 14 in a 10-point box.</summary>
    private static FrameworkElement Chevron(string symbol, bool available)
    {
        FrameworkElement chevron = Toolbar.ToolbarParts.GlyphBox(symbol, 10, 10, 14, available ? "AnnotatorGlyphSecondary" : "AnnotatorGlyphDisabled");
        chevron.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(chevron, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return chevron;
    }

    /// <summary>
    /// The Mac's Board disclosure (MenuBarPopoverView): the Board row, then, while it is expanded, Open board..., Save
    /// board... and Export PNG... one point apart, indented 12 points beside a 2-point line. Saving and exporting need a
    /// drawing session.
    /// </summary>
    private void AddBoard(MacStack content, WorkspaceSnapshot snapshot)
    {
        _content = content;
        _boardChevron = new Border();
        _board = Row("Board", "square.stack", TrayAction.Board, null, value: "Collapsed", help: "Open, save or export a board.",
            trailingSymbol: "chevron.down", trailingHost: _boardChevron);
        content.Children.Add(_board);
        var rows = new MacStack { Spacing = 1, Margin = new Thickness(12, 0, 0, 0) };
        rows.Children.Add(Row("Open board…", "folder", TrayAction.OpenBoard, "Ctrl+O", width: 276));
        rows.Children.Add(Row("Save board…", "square.and.arrow.down", TrayAction.SaveBoard, "Ctrl+S", width: 276,
            enabled: TrayMenu.IsAvailable(TrayAction.SaveBoard, snapshot)));
        rows.Children.Add(Row("Export PNG…", "square.and.arrow.up", TrayAction.ExportPng, null, width: 276,
            enabled: TrayMenu.IsAvailable(TrayAction.ExportPng, snapshot)));
        var line = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(9, 5, 0, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Pickers.PickerColors.Primary(0.12),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var group = new Grid { Visibility = Visibility.Collapsed };
        group.Children.Add(rows);
        group.Children.Add(line);
        _boardRows = group;
        _saveAvailable = TrayMenu.IsAvailable(TrayAction.SaveBoard, snapshot);
        content.Children.Add(group);
    }

    /// <summary>Opens or closes the Board rows; the flyout grows or shrinks to fit them, as the Mac popover does.</summary>
    private void ToggleBoard(bool expanded)
    {
        if (_board is null || _boardRows is null || _boardChevron is null || _boardExpanded == expanded)
        {
            return;
        }
        _boardExpanded = expanded;
        _board.AccessibleValue = expanded ? "Expanded" : "Collapsed";
        _boardChevron.Child = Chevron(expanded ? "chevron.up" : "chevron.down", available: true);
        _boardRows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _content?.InvalidateMeasure();
        Surface?.InvalidateMeasure();
        Refit();
    }

    /// <summary>Mac quickRegion: half of the row, at least 34 points high, on a faint rounded fill; its name says what it shares.</summary>
    /// <summary>
    /// The Mac popover's break section (spec 014): Start break timer with the saved length as its value and the break
    /// shortcut, the 5, 10, 15 and 30 minute buttons, and a caption that says whether the break locks the computer.
    /// </summary>
    private void AddBreak(MacStack content, WorkspaceSnapshot snapshot)
    {
        Annotator.Core.BreakTimerSettings settings = snapshot.Preferences.BreakTimer ?? new Annotator.Core.BreakTimerSettings();
        content.Children.Add(Divider());
        content.Children.Add(Row(Annotator.Workspace.Breaks.BreakCopy.StartBreak, "timer", TrayAction.StartBreak,
            WindowsShortcuts.Label(GlobalShortcutAction.BreakTimer, snapshot.Preferences), value: $"{settings.Minutes} minutes"));
        var quick = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 5, 0, 5) };
        foreach (TrayAction action in TrayMenu.QuickBreaks)
        {
            quick.Children.Add(QuickBreak(action));
        }
        content.Children.Add(quick);
        TextBlock caption = Line(Annotator.Workspace.Breaks.BreakCopy.MenuCaption(settings.LockWhenTouched), 10, 13, FontWeights.Normal, "AnnotatorSecondaryText");
        caption.TextWrapping = TextWrapping.Wrap;
        caption.MaxWidth = 268;
        caption.Margin = new Thickness(10, 0, 10, 6);
        AutomationProperties.SetAutomationId(caption, "menu-break-caption");
        content.Children.Add(caption);
    }

    /// <summary>One of the Mac's quick break buttons: "5 min" and so on, named "Start a 5-minute break".</summary>
    private ParityButton QuickBreak(TrayAction action)
    {
        int minutes = TrayMenu.BreakMinutes(action) ?? 0;
        TextBlock label = Hidden(Line(Annotator.Workspace.Breaks.BreakCopy.QuickBreak(minutes), 12, 16, FontWeights.Medium, "AnnotatorPrimaryText"));
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var surface = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = AppResources.Brush("AnnotatorQuickRegionFillBrush"),
            Height = 30,
            Child = label,
        };
        ContrastTheme.ButtonLook(surface, true);
        if (ContrastTheme.IsOn)
        {
            label.Foreground = ContrastTheme.Brush(ContrastColor.ButtonText);
        }
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            Width = 67.5,
            Height = 30,
            CornerRadius = new CornerRadius(7),
        };
        button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Colors.Transparent);
        FocusRing(button, surface, "AnnotatorAccentBrush", 1.5, 7, new Thickness(-0.75));
        AutomationProperties.SetName(button, Annotator.Workspace.Breaks.BreakCopy.QuickBreakName(minutes));
        AutomationProperties.SetAutomationId(button, TrayMenu.AutomationId(action));
        button.Click += (_, _) => Choose(action);
        return button;
    }

    private ParityButton QuickRegion(string title, TrayAction action, bool selected)
    {
        bool available = TrayMenu.IsAvailable(action);
        TextBlock label = Hidden(Line(title, 12, 16, FontWeights.Medium, available ? "AnnotatorPrimaryText" : "AnnotatorDisabledText"));
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var surface = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = AppResources.Brush("AnnotatorQuickRegionFillBrush"),
            Height = 34,
            Child = label,
        };
        ContrastTheme.ButtonLook(surface, available);
        if (ContrastTheme.IsOn && available)
        {
            label.Foreground = ContrastTheme.Brush(ContrastColor.ButtonText);
        }
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            Width = 140,
            Height = 34,
            CornerRadius = new CornerRadius(7),
            IsEnabled = available,
            AccessibleSelected = selected,
        };
        button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Colors.Transparent);
        FocusRing(button, surface, "AnnotatorAccentBrush", 1.5, 7, new Thickness(-0.75));
        AutomationProperties.SetName(button, $"Share {title.ToLowerInvariant()}");
        AutomationProperties.SetAutomationId(button, TrayMenu.AutomationId(action));
        button.Click += (_, _) => Choose(action);
        return button;
    }

    /// <summary>
    /// The popover's focus ring (MenuBarPopoverView): a stroke over the button, shown while it has focus from the keyboard
    /// or from the flyout opening, not from a click. Rows and half buttons use a 1.5-point accent ring on their edge.
    /// </summary>
    private static Border FocusRing(ParityButton button, FrameworkElement content, string brush, double thickness, double radius, Thickness margin)
    {
        var ring = new Border
        {
            BorderBrush = AppResources.Brush(brush),
            BorderThickness = new Thickness(thickness),
            CornerRadius = new CornerRadius(radius),
            Margin = margin,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(ring, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var host = new Grid();
        host.Children.Add(content);
        host.Children.Add(ring);
        button.Content = host;
        void Update(object sender, RoutedEventArgs args) =>
            ring.Opacity = button.FocusState is FocusState.Keyboard or FocusState.Programmatic ? 1 : 0;
        button.GotFocus += Update;
        button.LostFocus += Update;
        return ring;
    }
    private static FrameworkElement Problem(string message)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 6, 10, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement icon = Glyphs.ForSymbol("exclamationmark.triangle.fill", 14, "AnnotatorGlyphWarning");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);
        var text = new TextBlock { Text = message, FontSize = 12, TextWrapping = TextWrapping.Wrap, Style = Text("AnnotatorSecondaryText") };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        AutomationProperties.SetName(grid, message);
        AutomationProperties.SetLiveSetting(grid, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return grid;
    }

    private void Choose(TrayAction action)
    {
        // Close first, then act, like the Mac popover. Focus goes back before the action, so leaving Draw can return
        // it to the window that was active before Draw, and entering Draw remembers the window to return to.
        Dismiss(restoreFocus: action != TrayAction.Quit);
        _choose(action);
    }

    private static Style Text(string key) => AppResources.Style(key);

    private static PathGeometry MarkGeometry()
    {
        PathGeometry geometry = Glyphs.Geometry(MarkGlyph.PathData);
        geometry.FillRule = FillRule.EvenOdd;
        return geometry;
    }
}
