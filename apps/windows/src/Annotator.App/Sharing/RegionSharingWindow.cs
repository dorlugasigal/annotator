using Annotator.App.Icons;
using Annotator.App.Library;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Sharing;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Sharing;

/// <summary>
/// The Share a region window (Mac RegionSharingControls in its floating panel, spec 012): the display, a preview of the
/// region, the halves, thirds and corners, a custom area with its numbers, and Start or Stop region sharing. It opens
/// 960 by 660 points (at least 840 by 600) in the middle of the chosen display's work area, stays above the toolbar and
/// is left out of screen capture, so it never appears in the shared region. Closing it keeps sharing on.
/// </summary>
internal sealed partial class RegionSharingWindow : Window
{
    public const double DefaultWidth = 960;
    public const double DefaultHeight = 660;
    public const double MinimumWidth = 840;
    public const double MinimumHeight = 600;

    private static readonly (string Title, ShareRegionPreset[] Presets)[] Groups =
    [
        ("Halves", [ShareRegionPreset.LeftHalf, ShareRegionPreset.RightHalf, ShareRegionPreset.TopHalf, ShareRegionPreset.BottomHalf]),
        ("Thirds", [ShareRegionPreset.LeftThird, ShareRegionPreset.CenterThird, ShareRegionPreset.RightThird]),
        ("Corners", [ShareRegionPreset.TopLeft, ShareRegionPreset.TopRight, ShareRegionPreset.BottomLeft, ShareRegionPreset.BottomRight]),
    ];

    private readonly RegionSharing _sharing;
    private readonly WindowRegistration _registration;
    private readonly OverlappedPresenter _presenter;
    private readonly Action _closed;
    private readonly Grid _host = new();
    private static readonly Windows.Foundation.Size ZoomedMinimum = new(MinimumWidth, MinimumHeight);
    private ZoomPanel? _zoom;
    private readonly Dictionary<ShareRegionPreset, (ParityButton Button, Border Surface, Microsoft.UI.Xaml.Shapes.Rectangle Outline)> _presets = [];
    private readonly TextBox[] _fields = new TextBox[4];
    private string[] _applied = ["", "", "", ""];
    private Rect? _syncedBounds;
    private AccessibleImage _badge = null!;
    private Ellipse _badgeDot = null!;
    private TextBlock _badgeText = null!;
    private DisplayPopUp _displayPicker = null!;
    private Grid _preview = null!;
    private Canvas _previewCanvas = null!;
    private TextBlock _previewEmpty = null!;
    private TextBlock _selectionTitle = null!;
    private TextBlock _selectionPixels = null!;
    private ParityButton _apply = null!;
    private ParityButton _startStop = null!;
    private Border _startStopSurface = null!;
    private TextBlock _startStopText = null!;
    private AccessibleText _validation = null!;
    private TextBlock _validationText = null!;
    private TextBlock _status = null!;
    private bool _placed;
    private bool _closing;
    /// <summary>Shows the window cloaked until its content drew, and hides it by cloaking, so its first click works (WindowCloak).</summary>
    private readonly WindowCloak _cloak;

    /// <param name="closed">The person closed the window (the close button or Escape); sharing stays as it is.</param>
    public RegionSharingWindow(WindowRegistry registry, RegionSharing sharing, Action closed)
    {
        _sharing = sharing;
        _closed = closed;
        Title = "Share a region";
        _presenter = OverlappedPresenter.Create();
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsResizable = true;
        _presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(_presenter);
        Shell.AppIcon.Apply(AppWindow);
        AppWindow.IsShownInSwitchers = true;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        // Registered before the first show, so capture leaves it out (S-1) and it never shows in the shared region.
        _registration = registry.Register((nint)Hwnd, WindowLayer.Popover);
        _host.KeyDown += OnKeyDown;
        _host.ActualThemeChanged += (_, _) => Build();
        AutomationFrames.Nudge(_host);
        Content = _host;
        // A larger text size shows the window larger as a whole (WIN-A11Y-03), as far as its size allows. A contrast theme or
        // text size turned on later builds it again.
        _zoom = ZoomPanel.Host(this, _host, null, ZoomedMinimum);
        AppResources.SystemLookChanged += OnSystemLookChanged;
        Build();
        _cloak = new WindowCloak(this, Hwnd, "Share a region");
        AppWindow.Closing += (_, args) =>
        {
            if (!_closing)
            {
                // Mac windowWillClose: the window goes, the outline only if sharing is off, and sharing goes on.
                args.Cancel = true;
                HideWindow();
                _closed();
            }
        };
        Closed += (_, _) =>
        {
            AppResources.SystemLookChanged -= OnSystemLookChanged;
            _registration.Dispose();
        };
    }

    public HWND Hwnd { get; }

    public bool IsShown { get; private set; }

    /// <summary>The window's content, for the UX reference capture.</summary>
    internal FrameworkElement Surface => _host;

    /// <summary>
    /// Development only (UX reference capture): shows the 960 by 660-point content with the window's top-left corner at
    /// <paramref name="origin"/> in physical pixels, without activating it.
    /// </summary>
    internal void ShowForReference(PointInt32 origin)
    {
        double scale = WindowChrome.MonitorAt(origin.X, origin.Y).Scale;
        // The Mac capture shows the content view alone, so the reference window has no title bar.
        _presenter.SetBorderAndTitleBar(false, false);
        _presenter.IsResizable = false;
        WindowChrome.RemoveFrame(Hwnd);
        // Without a frame the window is its client area.
        double zoom = TextScaling.Zoom;
        AppWindow.Resize(new SizeInt32((int)Math.Round(DefaultWidth * zoom * scale), (int)Math.Round(DefaultHeight * zoom * scale)));
        AppWindow.Move(origin);
        _placed = true;
        Refresh();
        IsShown = true;
        AppWindow.Show(activateWindow: false);
    }

    /// <summary>Mac makeKeyAndOrderFront: shows the window with the keyboard, the first time in the middle of the display.</summary>
    public void ShowAndActivate(DisplayDescriptor? display)
    {
        if (!_placed)
        {
            Place(display);
        }
        Refresh();
        IsShown = true;
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: true);
        PInvoke.SetForegroundWindow(Hwnd);
    }

    /// <summary>Mac orderFront: shows the window again, with the keyboard or behind the window that has it.</summary>
    public void Restore(bool activate)
    {
        Refresh();
        IsShown = true;
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: activate);
        if (activate)
        {
            PInvoke.SetForegroundWindow(Hwnd);
        }
    }

    /// <summary>
    /// Mac orderOut: hides the window and keeps its size and place. It is cloaked rather than hidden, so it takes the first click
    /// as soon as it shows again, for example after a custom region is drawn (<see cref="WindowCloak"/>).
    /// </summary>
    public void HideWindow()
    {
        if (IsShown)
        {
            IsShown = false;
            _cloak.Hide();
        }
    }

    /// <summary>Closes the window for good (quit).</summary>
    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    /// <summary>Shows the sharing state; called whenever the workspace publishes.</summary>
    public void Refresh()
    {
        RegionSharingDisplay? display = _sharing.SelectedDisplay;
        RegionSharingState state = _sharing.State;
        bool capturing = state.IsCapturing;
        _badgeDot.Fill = ContrastTheme.Pick(ContrastColor.WindowText, new SolidColorBrush(capturing ? WinColor.FromArgb(255, 0x34, 0xC7, 0x59) : SecondaryColor()));
        _badgeText.Text = state is RegionSharingState.Starting ? "Starting" : capturing ? "On" : "Off";
        AutomationProperties.SetName(_badge, state.Title);

        _displayPicker.Show([.. _sharing.Displays.Select(item => item.Name)], display is null ? -1 : _sharing.Displays.IndexOf(display));

        ShareRegionPreset? preset = _sharing.SelectedPreset;
        Rect? bounds = _sharing.Region?.Bounds;
        DrawPreview(display, bounds);
        string selection = preset?.Title ?? "Custom region";
        _selectionTitle.Text = selection;
        AutomationProperties.SetItemStatus(_preview, selection);
        _selectionPixels.Text = display is not null && bounds is { } pixels ? RegionSharingText.Pixels(pixels, display.Scale) : string.Empty;
        _selectionPixels.Visibility = _selectionPixels.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach ((ShareRegionPreset item, (ParityButton button, Border surface, Microsoft.UI.Xaml.Shapes.Rectangle outline)) in _presets)
        {
            bool selected = preset == item;
            surface.Background = selected ? new SolidColorBrush(LibraryColors.Accent) { Opacity = 0.12 } : PickerColors.Overlay(PickerColors.Dark ? 0.035 : 0.035);
            outline.Stroke = selected ? new SolidColorBrush(LibraryColors.Accent) { Opacity = 0.8 } : null;
            button.BoundsElement = selected ? outline : null;
            if (ContrastTheme.IsOn)
            {
                // A contrast theme: a button outline, and the chosen preset outlined with Highlight.
                outline.Stroke = null;
                surface.Background = ContrastTheme.Brush(selected ? ContrastColor.Window : ContrastColor.ButtonFace);
                surface.BorderBrush = ContrastTheme.Brush(selected ? ContrastColor.Highlight : ContrastColor.ButtonText);
                surface.BorderThickness = new Thickness(selected ? 2 : 1);
            }
            button.AccessibleSelected = selected;
        }
        if (bounds != _syncedBounds)
        {
            // Mac onChange(of: region.bounds): the fields show the region that is in use.
            SyncFields();
        }
        bool draftChanged = DraftChanged();
        _apply.IsEnabled = draftChanged;

        if (_sharing.ValidationMessage is { } validation)
        {
            _validationText.Text = validation;
            AutomationProperties.SetName(_validation, validation);
            _validation.Visibility = Visibility.Visible;
            _status.Visibility = Visibility.Collapsed;
        }
        else
        {
            _validation.Visibility = Visibility.Collapsed;
            _status.Text = state.Message ?? string.Empty;
            _status.Visibility = state.Message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        string title = capturing ? "Stop region sharing" : "Start region sharing";
        _startStopText.Text = title;
        AutomationProperties.SetName(_startStop, title);
        _startStop.AccessibleValue = capturing ? "On" : "Off";
        _startStop.IsEnabled = capturing || (_sharing.Region is not null && _sharing.ValidationMessage is null && !draftChanged);
        ShowStartStop();
    }

    private static WinColor SecondaryColor() =>
        PickerColors.Dark ? WinColor.FromArgb(0x8C, 255, 255, 255) : WinColor.FromArgb(0x80, 0, 0, 0);

    /// <summary>One line of text at the Mac's size and line height, as wide as its words.</summary>
    private static TextBlock Line(string text, double size, string style = "AnnotatorPrimaryText", Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock block = PickerText.Line(text, size, style, weight);
        block.HorizontalAlignment = HorizontalAlignment.Left;
        // SwiftUI's line heights for the window's text styles, as the Mac capture reports them.
        block.LineHeight = size switch
        {
            10 => 13,
            11 => 14,
            12 => 15,
            13 => 16,
            24 => 28,
            _ => block.LineHeight,
        };
        // One line exactly as high as the Mac's, so the stacks lay out in exact points.
        block.Height = block.LineHeight;
        return block;
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size: the window is made again with the new colors and
    /// zoom (WIN-UI-07, WIN-A11Y-03).
    /// </summary>
    private void OnSystemLookChanged()
    {
        _zoom = ZoomPanel.Host(this, _host, _zoom, ZoomedMinimum);
        Build();
        Refresh();
    }

    private void Build()
    {
        _presets.Clear();
        _host.Children.Clear();
        _host.Background = AppResources.Brush("AnnotatorWindowBackgroundBrush");
        // UI Automation reads the two columns row by row, as SwiftUI orders the Mac's (ReadingOrderGrid).
        var root = new ReadingOrderGrid { Padding = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 16 });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        FrameworkElement header = Header();
        header.Margin = new Thickness(0, 0, 0, 24);
        root.Children.Add(header);

        var columns = new Grid { ColumnSpacing = 28 };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement left = LeftColumn();
        FrameworkElement right = RightColumn();
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        var message = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        _validationText = Line(string.Empty, 10, "AnnotatorErrorText");
        _validationText.TextWrapping = TextWrapping.Wrap;
        _validationText.Height = double.NaN;
        // Mac Label(message, systemImage:) with its identifier: one static text whose frame covers the icon and the words.
        AutomationProperties.SetAccessibilityView(_validationText, AccessibilityView.Raw);
        var validationRow = new MacStack { Orientation = Orientation.Horizontal, Spacing = 4 };
        FrameworkElement alert = Glyphs.ForSymbol("exclamationmark.circle", 10, "AnnotatorGlyphError");
        alert.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(alert, AccessibilityView.Raw);
        validationRow.Children.Add(alert);
        validationRow.Children.Add(_validationText);
        _validation = new AccessibleText(string.Empty, "region-validation-error")
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _validation.Children.Add(validationRow);
        _status = Line(string.Empty, 10, "AnnotatorSecondaryText");
        _status.Visibility = Visibility.Collapsed;
        message.Children.Add(_validation);
        message.Children.Add(_status);
        Grid.SetRow(message, 3);
        root.Children.Add(message);

        var divider = new Rectangle { Height = 1, Fill = AppResources.Brush("AnnotatorSeparatorBrush") };
        Grid.SetRow(divider, 4);
        root.Children.Add(divider);

        FrameworkElement footer = Footer();
        footer.Margin = new Thickness(0, 18, 0, 0);
        Grid.SetRow(footer, 5);
        root.Children.Add(footer);
        _host.Children.Add(root);
        _syncedBounds = null;
        Refresh();
    }

    private FrameworkElement Header()
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement glyph = Glyphs.ForSymbol("rectangle.on.rectangle", 24, "AnnotatorGlyphAccent");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var icon = new Border
        {
            Width = 48,
            Height = 48,
            CornerRadius = new CornerRadius(12),
            Background = ContrastTheme.Pick(ContrastColor.Window, new SolidColorBrush(LibraryColors.Accent) { Opacity = 0.08 }),
            Child = glyph,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        row.Children.Add(icon);
        var titles = new MacStack { Spacing = 5 };
        titles.Children.Add(Line("Share a region", 24, weight: FontWeights.SemiBold));
        titles.Children.Add(Line("Give the call a clear view. Keep the rest of your screen to yourself.", 12, "AnnotatorSecondaryText"));
        Grid.SetColumn(titles, 1);
        row.Children.Add(titles);
        _badgeDot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        _badgeText = Line("Off", 12, weight: FontWeights.SemiBold);
        var badgeRow = new MacStack { Orientation = Orientation.Horizontal, Spacing = 7 };
        badgeRow.Children.Add(_badgeDot);
        badgeRow.Children.Add(_badgeText);
        _badge = new AccessibleImage(RegionSharingState.Idle.Instance.Title, null, null)
        {
            Padding = new Thickness(14, 8, 14, 8),
            CornerRadius = new CornerRadius(15.5),
            Background = ContrastTheme.Pick(ContrastColor.Window, PickerColors.Overlay(0.07 * (PickerColors.Dark ? 0.55 : 0.5))),
            BorderBrush = ContrastTheme.Pick(ContrastColor.WindowText, new SolidColorBrush(Colors.Transparent)),
            BorderThickness = new Thickness(ContrastTheme.IsOn ? 1 : 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        _badge.Children.Add(badgeRow);
        Grid.SetColumn(_badge, 2);
        row.Children.Add(_badge);
        return row;
    }

    private FrameworkElement LeftColumn()
    {
        var column = new MacStack { Spacing = 16 };
        var pickerRow = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        TextBlock label = Line("Display", 13);
        label.VerticalAlignment = VerticalAlignment.Center;
        // The word takes at least SF Pro's width, so the pop-up after it starts where the Mac's does (D-4).
        label.MinWidth = 44.5;
        pickerRow.Children.Add(label);
        // The Mac's pop-up button: 24 points high, after its Display label, which names it (AppKit's title element; the
        // UX comparison reads the Mac's pop-up by the text before it, as Settings' pop-ups).
        _displayPicker = new DisplayPopUp(index =>
        {
            if (index >= 0 && index < _sharing.Displays.Length)
            {
                _sharing.SelectDisplay(_sharing.Displays[index].Id);
            }
        });
        AutomationProperties.SetAutomationId(_displayPicker, "region-display");
        AutomationProperties.SetName(_displayPicker, "Display");
        pickerRow.Children.Add(_displayPicker);
        column.Children.Add(pickerRow);

        _previewCanvas = new Canvas { Margin = new Thickness(22) };
        _previewEmpty = Line("Choose a display", 13, "AnnotatorSecondaryText");
        _previewEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _previewEmpty.VerticalAlignment = VerticalAlignment.Center;
        var preview = new AccessibleImage("Region preview", null, null)
        {
            Height = 224,
            CornerRadius = new CornerRadius(14),
            Background = ContrastTheme.Pick(ContrastColor.Window, PickerColors.Overlay(0.04 * 0.85)),
            BorderBrush = ContrastTheme.Pick(ContrastColor.WindowText, new SolidColorBrush(Colors.Transparent)),
            BorderThickness = new Thickness(ContrastTheme.IsOn ? 1 : 0),
        };
        preview.Children.Add(_previewCanvas);
        preview.Children.Add(_previewEmpty);
        preview.SizeChanged += (_, _) => DrawPreview(_sharing.SelectedDisplay, _sharing.Region?.Bounds);
        _preview = preview;
        column.Children.Add(preview);

        var summary = new Grid();
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new MacStack { Spacing = 4 };
        _selectionTitle = Line("Custom region", 13, weight: FontWeights.SemiBold);
        _selectionPixels = Line(string.Empty, 10, "AnnotatorSecondaryText");
        names.Children.Add(_selectionTitle);
        names.Children.Add(_selectionPixels);
        summary.Children.Add(names);
        var outlined = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        FrameworkElement finder = Glyphs.ForSymbol("viewfinder", 10, "AnnotatorGlyphSecondary");
        finder.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(finder, AccessibilityView.Raw);
        outlined.Children.Add(finder);
        outlined.Children.Add(Line("Outlined on your screen", 10, "AnnotatorSecondaryText"));
        Grid.SetColumn(outlined, 1);
        summary.Children.Add(outlined);
        column.Children.Add(summary);

        TextBlock caveat = Line("Anything inside this area can appear, including notifications. Keep private windows outside it. Blur is not secure redaction.",
                                10, "AnnotatorSecondaryText");
        caveat.TextWrapping = TextWrapping.Wrap;
        caveat.Height = double.NaN;
        column.Children.Add(caveat);
        return column;
    }

    private FrameworkElement RightColumn()
    {
        var column = new MacStack { Spacing = 14 };
        foreach ((string title, ShareRegionPreset[] presets) in Groups)
        {
            var group = new MacStack { Spacing = 7 };
            group.Children.Add(Line(title, 10, "AnnotatorSecondaryText", FontWeights.Medium));
            var row = new Grid { ColumnSpacing = 7 };
            for (int index = 0; index < presets.Length; index++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                FrameworkElement button = PresetButton(presets[index]);
                Grid.SetColumn(button, index);
                row.Children.Add(button);
            }
            group.Children.Add(row);
            column.Children.Add(group);
        }
        column.Children.Add(new Rectangle { Height = 1, Fill = AppResources.Brush("AnnotatorSeparatorBrush") });

        var custom = new Grid();
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        custom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock own = Line("Your own area", 11, weight: FontWeights.SemiBold);
        own.VerticalAlignment = VerticalAlignment.Center;
        custom.Children.Add(own);
        ParityButton draw = SmallButton("Draw a custom region", "viewfinder");
        AutomationProperties.SetAutomationId(draw, "region-draw-custom");
        draw.Click += (_, _) => _sharing.DrawCustomRegion();
        Grid.SetColumn(draw, 1);
        custom.Children.Add(draw);
        column.Children.Add(custom);

        var fields = new Grid { ColumnSpacing = 10 };
        string[] names = ["X", "Y", "Width", "Height"];
        for (int index = 0; index < names.Length; index++)
        {
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new MacStack { Spacing = 4 };
            cell.Children.Add(Line(names[index], 10, "AnnotatorSecondaryText"));
            var field = new TextBox { PlaceholderText = names[index], FontSize = 13, MinHeight = 24, Padding = new Thickness(6, 2, 6, 2) };
            AutomationProperties.SetName(field, $"Region {names[index].ToLowerInvariant()} in points");
            field.TextChanged += (_, _) => Refresh();
            field.KeyDown += (_, args) =>
            {
                if (args.Key == VirtualKey.Enter)
                {
                    args.Handled = true;
                    ApplyCustom();
                }
            };
            _fields[index] = field;
            cell.Children.Add(field);
            Grid.SetColumn(cell, index);
            fields.Children.Add(cell);
        }
        column.Children.Add(fields);

        var hint = new Grid();
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock points = Line("Points from the display's top-left corner.", 10, "AnnotatorSecondaryText");
        points.VerticalAlignment = VerticalAlignment.Center;
        hint.Children.Add(points);
        _apply = SmallButton("Apply size", null);
        _apply.Click += (_, _) => ApplyCustom();
        Grid.SetColumn(_apply, 1);
        hint.Children.Add(_apply);
        column.Children.Add(hint);
        return column;
    }

    private FrameworkElement Footer()
    {
        var row = new Grid { ColumnSpacing = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new MacStack { Spacing = 6 };
        texts.Children.Add(Line("In your meeting, choose Share > Window.", 12, weight: FontWeights.Medium));
        texts.Children.Add(Line($"Select {RegionSharingText.OutputTitle(BuildInfo.IsDevelopment)}. Hiding drawings removes them from the call; the region stays on.",
                                10, "AnnotatorSecondaryText"));
        if (BuildInfo.IsDevelopment)
        {
            texts.Children.Add(Line("Local build", 10, "AnnotatorSecondaryText"));
        }
        row.Children.Add(texts);
        _startStopText = Line("Start region sharing", 13);
        _startStopText.HorizontalAlignment = HorizontalAlignment.Center;
        _startStopText.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(_startStopText, AccessibilityView.Raw);
        _startStopSurface = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 0, 14, 0), Child = _startStopText, MinWidth = 148, Height = 28 };
        _startStop = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Content = _startStopSurface,
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(_startStop, "region-start-stop");
        _startStop.Click += (_, _) => _sharing.Toggle();
        _startStop.IsEnabledChanged += (_, _) => ShowStartStop();
        Grid.SetColumn(_startStop, 1);
        row.Children.Add(_startStop);
        return row;
    }

    /// <summary>
    /// The Mac's prominent button: the accent, or red while sharing, with white words; dimmed when unavailable. A contrast
    /// theme shows it in Highlight colors, or as an unavailable button.
    /// </summary>
    private void ShowStartStop()
    {
        if (ContrastTheme.IsOn)
        {
            bool enabled = _startStop.IsEnabled;
            _startStopSurface.Background = ContrastTheme.Brush(enabled ? ContrastColor.Highlight : ContrastColor.ButtonFace);
            _startStopSurface.BorderBrush = ContrastTheme.Brush(enabled ? ContrastColor.Highlight : ContrastColor.GrayText);
            _startStopSurface.BorderThickness = new Thickness(1);
            _startStopText.Foreground = ContrastTheme.Brush(enabled ? ContrastColor.HighlightText : ContrastColor.GrayText);
            return;
        }
        bool capturing = _sharing.State.IsCapturing;
        WinColor fill = capturing ? (PickerColors.Dark ? WinColor.FromArgb(255, 0xFF, 0x45, 0x3A) : WinColor.FromArgb(255, 0xFF, 0x3B, 0x30)) : LibraryColors.Accent;
        _startStopSurface.Background = new SolidColorBrush(fill) { Opacity = _startStop.IsEnabled ? 1 : 0.45 };
        _startStopText.Foreground = new SolidColorBrush(Colors.White);
    }

    private FrameworkElement PresetButton(ShareRegionPreset preset)
    {
        var content = new MacStack { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var thumbnail = new Canvas { Width = 30, Height = 20, HorizontalAlignment = HorizontalAlignment.Center };
        DrawRegion(thumbnail, new Rect(0, 0, 1, 1), preset.NormalizedBounds, 30, 20, animated: false);
        content.Children.Add(thumbnail);
        TextBlock title = Line(preset.Title, 10, weight: FontWeights.Medium);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetAccessibilityView(title, AccessibilityView.Raw);
        content.Children.Add(title);
        var surface = new Border
        {
            MinHeight = 48,
            CornerRadius = new CornerRadius(8),
            Child = content,
        };
        // SwiftUI's stroke of the chosen preset is centered on its edge, half a point outside the button, and AppKit reports
        // the button with it; the outline is drawn the same way and is the chosen button's frame for UI Automation.
        var outline = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            RadiusX = 8.5,
            RadiusY = 8.5,
            StrokeThickness = 1,
            Margin = new Thickness(-0.5),
            IsHitTestVisible = false,
        };
        var face = new Grid();
        face.Children.Add(surface);
        face.Children.Add(outline);
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Content = face,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            AccessibleSelected = false,
        };
        AutomationProperties.SetName(button, preset.Title);
        button.Click += (_, _) => _sharing.ChoosePreset(preset);
        _presets[preset] = (button, surface, outline);
        return button;
    }

    private static ParityButton SmallButton(string title, string? symbol)
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (symbol is not null)
        {
            FrameworkElement glyph = Glyphs.ForSymbol(symbol, 11);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
            row.Children.Add(glyph);
        }
        TextBlock label = Line(title, 11);
        label.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        row.Children.Add(label);
        var surface = new Border { CornerRadius = new CornerRadius(5), Background = PickerColors.ControlFill, Padding = new Thickness(10, 0, 10, 0), Height = 20, Child = row };
        // The button takes at least the width SF Pro gives its words on the Mac, so it lines up with the Mac's (D-4).
        surface.MinWidth = title switch
        {
            "Draw a custom region" => 156.5,
            "Apply size" => 74,
            _ => 0,
        };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Content = surface,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, title);
        void Show()
        {
            surface.Background = button.IsEnabled ? PickerColors.ControlFill : LibraryColors.DisabledControlFill;
            label.Style = AppResources.Style(button.IsEnabled ? "AnnotatorPrimaryText" : "AnnotatorDisabledText");
            ContrastTheme.ButtonLook(surface, button.IsEnabled);
        }
        button.IsEnabledChanged += (_, _) => Show();
        button.Loaded += (_, _) => Show();
        return button;
    }

    /// <summary>The preview of the display with the region outlined in the accent (Mac RegionPreview).</summary>
    private void DrawPreview(RegionSharingDisplay? display, Rect? region)
    {
        if (_previewCanvas is null)
        {
            return;
        }
        _previewEmpty.Visibility = display is null ? Visibility.Visible : Visibility.Collapsed;
        double width = Math.Max(0, _preview.ActualWidth - 44), height = Math.Max(0, _preview.ActualHeight - 44);
        if (display is null || width <= 0 || height <= 0)
        {
            _previewCanvas.Children.Clear();
            return;
        }
        DrawRegion(_previewCanvas, display.LocalBounds, region, width, height, animated: true);
    }

    private static void DrawRegion(Canvas canvas, Rect display, Rect? region, double width, double height, bool animated)
    {
        _ = animated;
        canvas.Children.Clear();
        double scale = Math.Min(width / display.Width, height / display.Height);
        double shownWidth = display.Width * scale, shownHeight = display.Height * scale;
        double left = (width - shownWidth) / 2, top = (height - shownHeight) / 2;
        WinColor secondary = PickerColors.Dark ? WinColor.FromArgb(255, 0x98, 0x98, 0x9D) : WinColor.FromArgb(255, 0x8A, 0x8A, 0x8E);
        var screen = new Rectangle
        {
            Width = shownWidth,
            Height = shownHeight,
            RadiusX = Math.Min(5, shownHeight / 4),
            RadiusY = Math.Min(5, shownHeight / 4),
            Fill = ContrastTheme.Pick(ContrastColor.Window, new SolidColorBrush(secondary) { Opacity = 0.12 }),
            Stroke = ContrastTheme.Pick(ContrastColor.WindowText, new SolidColorBrush(secondary) { Opacity = 0.25 }),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(screen, left);
        Canvas.SetTop(screen, top);
        canvas.Children.Add(screen);
        if (region is { } bounds)
        {
            var shown = new Rectangle
            {
                Width = Math.Max(1, bounds.Width * scale),
                Height = Math.Max(1, bounds.Height * scale),
                RadiusX = Math.Min(3, bounds.Height * scale / 4),
                RadiusY = Math.Min(3, bounds.Height * scale / 4),
                // A contrast theme fills the region with Highlight, as a selection.
                Fill = ContrastTheme.Pick(ContrastColor.Highlight, new SolidColorBrush(LibraryColors.Accent) { Opacity = 0.18 }),
                Stroke = ContrastTheme.Pick(ContrastColor.Highlight, new SolidColorBrush(LibraryColors.Accent)),
                StrokeThickness = 1.5,
            };
            Canvas.SetLeft(shown, left + (bounds.X - display.X) * scale);
            Canvas.SetTop(shown, top + (bounds.Y - display.Y) * scale);
            canvas.Children.Add(shown);
        }
    }

    private bool DraftChanged()
    {
        for (int index = 0; index < _fields.Length; index++)
        {
            if (_fields[index] is null || _fields[index].Text != _applied[index])
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Mac syncFields: the fields show the region's numbers, which count as applied.</summary>
    private void SyncFields()
    {
        _syncedBounds = _sharing.Region?.Bounds;
        if (_syncedBounds is not { } bounds)
        {
            return;
        }
        string[] values = [RegionSharingText.Field(bounds.X), RegionSharingText.Field(bounds.Y), RegionSharingText.Field(bounds.Width), RegionSharingText.Field(bounds.Height)];
        _applied = values;
        for (int index = 0; index < _fields.Length; index++)
        {
            if (_fields[index].Text != values[index])
            {
                _fields[index].Text = values[index];
            }
        }
    }

    /// <summary>Mac applyCustom: four numbers apply as the custom region; anything else says which numbers are needed.</summary>
    private void ApplyCustom()
    {
        double?[] numbers = [.. _fields.Select(field => RegionSharingText.Number(field.Text))];
        if (numbers.All(number => number is not null))
        {
            _sharing.ApplyCustom(new Rect(numbers[0]!.Value, numbers[1]!.Value, numbers[2]!.Value, numbers[3]!.Value));
            if (_sharing.ValidationMessage is null)
            {
                SyncFields();
            }
        }
        else
        {
            _sharing.InvalidCustomNumbers();
        }
        Refresh();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            // Mac onExitCommand: Escape closes the window.
            args.Handled = true;
            HideWindow();
            _closed();
        }
    }

    /// <summary>
    /// Mac showControls: 960 by 660 points, at most the work area less 32 by 64, at least 840 by 600, in the middle of the
    /// chosen display's work area.
    /// </summary>
    private void Place(DisplayDescriptor? display)
    {
        MonitorArea monitor = display is { } chosen
            ? WindowChrome.MonitorAt((int)Math.Round(chosen.Bounds.X + chosen.Bounds.Width / 2), (int)Math.Round(chosen.Bounds.Y + chosen.Bounds.Height / 2))
            : WindowChrome.MonitorAtCursor();
        double scale = monitor.Scale;
        RectInt32 work = monitor.WorkArea;
        double availableWidth = work.Width / scale, availableHeight = work.Height / scale;
        // Larger at a larger text size, up to the work area (WIN-A11Y-03).
        double zoom = TextScaling.Zoom;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(Math.Min(DefaultWidth * zoom, availableWidth - 32) * scale),
                                             (int)Math.Round(Math.Min(DefaultHeight * zoom, availableHeight - 64) * scale)));
        SizeInt32 outer = AppWindow.Size;
        SizeInt32 client = AppWindow.ClientSize;
        _presenter.PreferredMinimumWidth = (int)Math.Ceiling(Math.Min(MinimumWidth, availableWidth - 32) * scale) + outer.Width - client.Width;
        _presenter.PreferredMinimumHeight = (int)Math.Ceiling(Math.Min(MinimumHeight, availableHeight - 64) * scale) + outer.Height - client.Height;
        int width = Math.Min(outer.Width, work.Width), height = Math.Min(outer.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        _placed = true;
    }
    /// <summary>
    /// The Mac's pop-up button (Picker in a form): the chosen display's name and up and down chevrons, 24 points high, with a
    /// menu of the displays. UI Automation reads it as a combo box whose value is the name shown.
    /// </summary>
    private sealed partial class DisplayPopUp : Button
    {
        private readonly Action<int> _chosen;
        private readonly TextBlock _title;
        private string[] _names = [];
        private int _selected = -1;

        public DisplayPopUp(Action<int> chosen)
        {
            _chosen = chosen;
            Style = AppResources.Style("AnnotatorPopUpButton");
            Height = 24;
            MinWidth = 147;
            VerticalAlignment = VerticalAlignment.Center;
            _title = PickerText.Line("Choose a display", 13, "AnnotatorPrimaryText");
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Margin = new Thickness(10.5, 0, 30, 0);
            FrameworkElement chevrons = Glyphs.ForSymbol("chevron.up.chevron.down", 15);
            chevrons.HorizontalAlignment = HorizontalAlignment.Right;
            chevrons.VerticalAlignment = VerticalAlignment.Center;
            chevrons.Margin = new Thickness(0, 0, 6, 0);
            var content = new Grid { IsHitTestVisible = false };
            content.Children.Add(_title);
            content.Children.Add(chevrons);
            Content = content;
            Click += (_, _) => Open();
        }

        public string Shown => _title.Text;

        /// <summary>Shows the displays' names with <paramref name="selected"/> chosen, or "Choose a display" for -1.</summary>
        public void Show(string[] names, int selected)
        {
            _names = names;
            _selected = selected;
            string before = _title.Text;
            _title.Text = selected >= 0 && selected < names.Length ? names[selected] : "Choose a display";
            if (before != _title.Text && FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(Microsoft.UI.Xaml.Automation.ValuePatternIdentifiers.ValueProperty, before, _title.Text);
            }
        }

        private void Open()
        {
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
            for (int index = 0; index < _names.Length; index++)
            {
                int chosen = index;
                var item = new ToggleMenuFlyoutItem { Text = _names[index], IsChecked = index == _selected };
                item.Click += (_, _) => _chosen(chosen);
                menu.Items.Add(item);
            }
            menu.ShowAt(this);
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

        private sealed partial class Peer(DisplayPopUp owner) : ButtonAutomationPeer(owner), Microsoft.UI.Xaml.Automation.Provider.IValueProvider
        {
            public bool IsReadOnly => true;

            public string Value => owner.Shown;

            public void SetValue(string value) => throw new InvalidOperationException("Choose the display from the pop-up button's menu.");

            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;

            protected override string GetClassNameCore() => "ComboBox";

            protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

            protected override object? GetPatternCore(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);

            protected override Windows.Foundation.Rect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
        }
    }
}