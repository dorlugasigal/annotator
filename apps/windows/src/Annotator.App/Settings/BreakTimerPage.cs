using Annotator.App.Breaks;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Breaks;
using Annotator.Workspace.Shortcuts;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Annotator.App.Settings;

/// <summary>
/// Settings > Break timer (specification 014), a port of the Mac's BreakTimerSettingsView in its grouped Form: the live
/// preview beside the words, the Start button and the shortcut; Timer (length with its field and stepper, title, end time,
/// counting after zero, sound); Look (the segmented background picker, the five color swatches, every display); Locking
/// (the switch and its plain limits). Rows that come and go take their separators with them. Every change goes through
/// <see cref="WorkspaceModel.SetBreakTimerSettings"/>, which checks and saves it; the page follows the workspace while loaded.
/// </summary>
internal sealed partial class BreakTimerPage : UserControl
{
    private static readonly BreakBackground[] s_backgrounds = [BreakBackground.Moving, BreakBackground.Blur, BreakBackground.Plain];

    /// <summary>The words' column beside the preview: the 684-point row less the 320-point preview and 20 points between.</summary>
    private const double SideWidth = SettingsForm.RowWidth - 320 - 20;

    private const double SegmentWidth = 121;

    private readonly WorkspaceModel _model;
    private readonly BreakTimerHost _breaks;
    private readonly BreakFaceView _preview;
    private readonly AccessibleElement _previewFrame = new(AutomationControlType.Custom) { InSettings = true, Width = 320, Height = 200 };
    private readonly SettingsButton _start;
    private readonly AccessibleElement _shortcutRow = new(AutomationControlType.Custom) { InSettings = true, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ShortcutKeycaps _keycaps;
    private readonly PlainField _length = new() { Width = 52 };
    private readonly MacStepper _stepper = new(BreakCopy.Length, BreakTimerSettings.MinimumMinutes, BreakTimerSettings.MaximumMinutes);
    private readonly PlainField _title = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = BreakCopy.NoTitle, MaxLength = 200 };
    private readonly SettingsSwitch _endTime = new(BreakCopy.ShowEndTime);
    private readonly SettingsSwitch _countOver = new(BreakCopy.CountOver);
    private readonly SettingsSwitch _chime = new(BreakCopy.Chime);
    private readonly SegmentGroup _background = new(BreakCopy.Background) { Width = SegmentWidth * 3, Height = 24 };
    private readonly List<(BreakBackground Background, SegmentButton Segment, Border Fill)> _segments = [];
    private readonly List<Rectangle> _segmentDividers = [];
    private readonly FormText _blurNote = SettingsForm.Caption(BreakCopy.BlurNote, wrapWidth: SettingsForm.RowWidth);
    private readonly List<(BreakPalette Palette, ParityButton Button, Ellipse Fill, Ellipse Ring, FrameworkElement Check)> _swatches = [];
    private readonly SettingsSwitch _allDisplays = new(BreakCopy.AllDisplays);
    private readonly SettingsSwitch _lock = new(BreakCopy.LockWhenTouched);
    private readonly FormText _lockText = SettingsForm.Caption(BreakCopy.LockingExplanation, wrapWidth: SettingsForm.RowWidth);
    private readonly FormText _meeting;
    private readonly FormText _cannotLock;
    private readonly Dictionary<FrameworkElement, FrameworkElement> _separators = [];
    private FormRow? _blurRow;
    private FormRow? _meetingRow;
    private FormRow? _cannotLockRow;
    private bool _applying;
    private bool _following;

    public BreakTimerPage(WorkspaceModel model, BreakTimerHost breaks)
    {
        _model = model;
        _breaks = breaks;
        _preview = new BreakFaceView(preview: true, new BreakFaceActions(), Motion.SystemMotion.ReducesMotion());
        string start = BreakCopy.StartButton(model.BreakTimerSettings.Minutes);
        _start = new SettingsButton(start, ButtonLook.Prominent, StartWidth(start), height: 28, symbol: "timer");
        _keycaps = new ShortcutKeycaps(WindowsShortcuts.Shortcut(GlobalShortcutAction.BreakTimer, model.Snapshot.Preferences));
        // Mac Label(systemImage:) in a Form row: the symbol in its own column, 2.5 points in from the row's text.
        _meeting = SettingsLabel.Make("video", BreakCopy.MeetingNote, 10, "AnnotatorSecondaryText", "AnnotatorGlyphSecondary", 11, 13.5);
        _meeting.Margin = new Thickness(2.5, 0, 0, 0);
        _cannotLock = SettingsLabel.Make("exclamationmark.triangle.fill", BreakCopy.CannotLock, 12, "AnnotatorPrimaryText", "AnnotatorGlyph", 13, 6,
            foreground: new SolidColorBrush(SettingsLook.Orange), wrapWidth: SettingsForm.RowWidth - 19, iconColor: SettingsLook.Orange);
        Content = Build();
        Loaded += (_, _) =>
        {
            if (!_following)
            {
                _following = true;
                _model.Published += Apply;
            }
            Apply(_model.Snapshot);
        };
        Unloaded += (_, _) =>
        {
            if (_following)
            {
                _following = false;
                _model.Published -= Apply;
            }
        };
        Apply(_model.Snapshot);
    }

    private FrameworkElement Build()
    {
        var form = new MacStack { HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(Card([SettingsForm.Single(Hero())]));

        // Timer
        _length.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                CommitLength();
            }
        };
        _length.LostFocus += (_, _) => CommitLength();
        AutomationProperties.SetName(_length, $"{BreakCopy.Length}, {BreakCopy.LengthName}");
        AutomationProperties.SetAutomationId(_length, "break-length");
        _stepper.Changed += minutes => Change((ref BreakTimerSettings settings) => settings.Minutes = minutes);
        var length = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6 };
        _length.VerticalAlignment = VerticalAlignment.Center;
        length.Children.Add(_length);
        length.Children.Add(_stepper);
        // The Mac's widths keep the field and stepper where the Mac has them although Segoe UI is narrower (D-4).
        length.Children.Add(new FormText(BreakCopy.Minutes, 13, "AnnotatorSecondaryText") { VerticalAlignment = VerticalAlignment.Center, Width = 48.5 });
        _title.TextChanged += (_, _) =>
        {
            if (_applying)
            {
                return;
            }
            string cleaned = BreakTimerSettings.CleanTitle(_title.Text);
            if (cleaned != _title.Text)
            {
                int caret = Math.Min(_title.SelectionStart, cleaned.Length);
                _applying = true;
                _title.Text = cleaned;
                _title.SelectionStart = caret;
                _applying = false;
            }
            Change((ref BreakTimerSettings settings) => settings.Title = cleaned);
        };
        AutomationProperties.SetName(_title, BreakCopy.Title);
        AutomationProperties.SetAutomationId(_title, "break-title");
        var title = new Grid { ColumnSpacing = 8 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FormText titleLabel = SettingsForm.Body(BreakCopy.Title);
        titleLabel.Width = 26.5;
        title.Children.Add(titleLabel);
        Grid.SetColumn(_title, 1);
        title.Children.Add(_title);
        _endTime.Toggled += on => Change((ref BreakTimerSettings settings) => settings.ShowEndTime = on);
        _countOver.Toggled += on => Change((ref BreakTimerSettings settings) => settings.CountOver = on);
        _chime.Toggled += on => Change((ref BreakTimerSettings settings) => settings.Chime = on);
        SettingsHelp.Set(_endTime, BreakCopy.ShowEndTimeHelp);
        SettingsHelp.Set(_countOver, BreakCopy.CountOverHelp);
        form.Children.Add(SettingsForm.Header(BreakCopy.TimerSection, first: false));
        form.Children.Add(Card(
        [
            new FormRow(SettingsForm.Body(BreakCopy.Length), length, line: 17) { FitsTrailing = true },
            SettingsForm.Single(title),
            SettingsForm.Toggle(BreakCopy.ShowEndTime, _endTime),
            SettingsForm.Toggle(BreakCopy.CountOver, _countOver),
            SettingsForm.Toggle(BreakCopy.Chime, _chime),
        ]));

        // Look
        BuildBackground();
        var swatches = new MacStack { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (BreakPalette palette in BreakPalette.AllCases)
        {
            swatches.Children.Add(Swatch(palette));
        }
        _allDisplays.Toggled += on => Change((ref BreakTimerSettings settings) => settings.AllDisplays = on);
        SettingsHelp.Set(_allDisplays, BreakCopy.AllDisplaysHelp);
        _blurRow = SettingsForm.Single(_blurNote);
        form.Children.Add(SettingsForm.Header(BreakCopy.LookSection, first: false));
        form.Children.Add(Card(
        [
            new FormRow(SettingsForm.Body(BreakCopy.Background), _background, line: 16) { FitsTrailing = true },
            _blurRow,
            new FormRow(SettingsForm.Body(BreakCopy.Colors), swatches, line: 17) { FitsTrailing = true },
            SettingsForm.Toggle(BreakCopy.AllDisplays, _allDisplays),
        ]));

        // Locking
        _lock.Toggled += on => Change((ref BreakTimerSettings settings) => settings.LockWhenTouched = on);
        AutomationProperties.SetAutomationId(_lock, "break-lock");
        AutomationProperties.SetAutomationId(_meeting, "break-meeting-note");
        _meetingRow = SettingsForm.Single(_meeting);
        _cannotLockRow = SettingsForm.Single(_cannotLock);
        form.Children.Add(SettingsForm.Header(BreakCopy.LockingSection, first: false));
        form.Children.Add(Card(
        [
            SettingsForm.Toggle(BreakCopy.LockWhenTouched, _lock),
            SettingsForm.Single(_lockText),
            _meetingRow,
            _cannotLockRow,
        ]));

        ScrollViewer scroll = SettingsForm.Scroll(form, new Thickness(SettingsForm.Inset, 20, SettingsForm.Inset, 20));
        scroll.Background = SettingsLook.Window;
        return scroll;
    }

    /// <summary>The preview beside the words, the Start button and the shortcut, 6 points from the row's padding.</summary>
    private FrameworkElement Hero()
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(31, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            Child = _preview.Root,
        };
        _previewFrame.Children.Add(frame);
        AutomationProperties.SetName(_previewFrame, BreakCopy.PreviewName);
        AutomationProperties.SetAutomationId(_previewFrame, "break-preview");
        AutomationProperties.SetAutomationId(_start, "settings-start-break");
        _start.Click += (_, _) => _breaks.Start();
        var shortcut = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        FormText caption = SettingsForm.Caption(BreakCopy.ShortcutLabel);
        caption.VerticalAlignment = VerticalAlignment.Center;
        _keycaps.VerticalAlignment = VerticalAlignment.Center;
        shortcut.Children.Add(caption);
        shortcut.Children.Add(_keycaps);
        _shortcutRow.Children.Add(shortcut);
        AutomationProperties.SetHelpText(_shortcutRow, BreakCopy.ShortcutHelp);
        ToolTipService.SetToolTip(_shortcutRow, BreakCopy.ShortcutHelp);
        var side = new MacStack { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(new FormText(BreakCopy.Introduction, 12, wrapWidth: SideWidth));
        side.Children.Add(_start);
        side.Children.Add(_shortcutRow);
        var hero = new MacStack { Orientation = Orientation.Horizontal, Spacing = 20 };
        _previewFrame.VerticalAlignment = VerticalAlignment.Center;
        hero.Children.Add(_previewFrame);
        hero.Children.Add(side);
        return new Pad(hero, new Thickness(0, 6, 0, 6));
    }

    /// <summary>Picker("Background").pickerStyle(.segmented): three 121-point segments with lines between unchosen ones.</summary>
    private void BuildBackground()
    {
        AutomationProperties.SetAutomationId(_background, "break-background");
        _background.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = PickerColors.ControlFill, IsHitTestVisible = false });
        for (var index = 0; index < s_backgrounds.Length; index++)
        {
            BreakBackground background = s_backgrounds[index];
            TextBlock title = SettingsText.Label(background.Label, 13);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            title.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAccessibilityView(title, AccessibilityView.Raw);
            var fill = new Border { CornerRadius = new CornerRadius(5), Child = title };
            var segment = new SegmentButton
            {
                Style = AppResources.Style("AnnotatorPlainButton"),
                CornerRadius = new CornerRadius(5),
                Content = fill,
                Group = _background,
                Width = SegmentWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(index * SegmentWidth, 0, 0, 0),
            };
            AutomationProperties.SetName(segment, background.Label);
            segment.Chosen += () => Change((ref BreakTimerSettings settings) => settings.Background = background);
            segment.KeyDown += (_, args) =>
            {
                if (args.Key is VirtualKey.Left or VirtualKey.Right)
                {
                    args.Handled = true;
                    int next = Math.Clamp(Array.IndexOf(s_backgrounds, _model.BreakTimerSettings.Background) + (args.Key == VirtualKey.Left ? -1 : 1),
                        0, s_backgrounds.Length - 1);
                    Change((ref BreakTimerSettings settings) => settings.Background = s_backgrounds[next]);
                    _segments[next].Segment.Focus(FocusState.Keyboard);
                }
            };
            _segments.Add((background, segment, fill));
            _background.Segments.Add(segment);
            _background.Children.Add(segment);
            if (index > 0)
            {
                var divider = new Rectangle
                {
                    Width = 1,
                    Height = 12,
                    Fill = AppResources.Brush("AnnotatorSeparatorBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(index * SegmentWidth - 0.5, 0, 0, 0),
                    IsHitTestVisible = false,
                };
                _segmentDividers.Add(divider);
                _background.Children.Add(divider);
            }
        }
        _background.TabFocusNavigation = KeyboardNavigationMode.Once;
    }

    /// <summary>
    /// Mac PaletteSwatch: the colors' gradient in a 20-point circle, or in the whole 26 points with a 2.5-point accent ring and a
    /// white check mark while chosen.
    /// </summary>
    private ParityButton Swatch(BreakPalette palette)
    {
        BreakColors colors = BreakColors.For(palette);
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        for (var index = 0; index < colors.Fields.Length; index++)
        {
            gradient.GradientStops.Add(new GradientStop { Color = colors.Fields[index], Offset = index / (double)(colors.Fields.Length - 1) });
        }
        var fill = new Ellipse { Width = 20, Height = 20, Fill = gradient };
        var ring = new Ellipse { Width = 26, Height = 26, Stroke = new SolidColorBrush(colors.Accent), StrokeThickness = 0 };
        var check = new Viewbox
        {
            Width = 10,
            Height = 10,
            Child = new Canvas
            {
                Width = 24,
                Height = 24,
                Children =
                {
                    new Microsoft.UI.Xaml.Shapes.Path
                    {
                        Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), "M 4,12.5 L 9.5,18 L 20,6"),
                        Stroke = new SolidColorBrush(Colors.White),
                        StrokeThickness = 4,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                    },
                },
            },
            Visibility = Visibility.Collapsed,
        };
        var grid = new Grid { Width = 26, Height = 26 };
        grid.Children.Add(fill);
        grid.Children.Add(ring);
        grid.Children.Add(check);
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Content = grid,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, palette.Label);
        AutomationProperties.SetName(button, palette.Label);
        AutomationProperties.SetHelpText(button, palette.Label);
        AutomationProperties.SetAutomationId(button, "break-palette-" + palette.RawValue);
        button.Click += (_, _) => Change((ref BreakTimerSettings settings) => settings.Palette = palette);
        _swatches.Add((palette, button, fill, ring, check));
        return button;
    }

    /// <summary>The Mac's width for the Start button at 10 minutes, widened or narrowed as Segoe UI measures another length.</summary>
    private static double StartWidth(string title)
    {
        string reference = BreakCopy.StartButton(10);
        static double Natural(string words) => SettingsButton.NaturalWidth(words, ButtonLook.Prominent, 13, icon: true);
        return SettingsWidths.Button(reference)!.Value + Natural(title) - Natural(reference);
    }

    /// <summary>SettingsForm.Card that remembers each row's separator, so a row that comes and goes takes its line with it.</summary>
    private FrameworkElement Card(IReadOnlyList<FrameworkElement> rows)
    {
        var stack = new MacStack();
        for (var index = 0; index < rows.Count; index++)
        {
            if (index > 0)
            {
                var separator = new Pad(new Rectangle { Height = 1, Fill = SettingsLook.RowSeparator, HorizontalAlignment = HorizontalAlignment.Stretch },
                    new Thickness(FormRow.Padding, 0, FormRow.Padding, 0));
                _separators[rows[index]] = separator;
                stack.Children.Add(separator);
            }
            stack.Children.Add(rows[index]);
        }
        var card = new Grid { Background = SettingsLook.Panel, CornerRadius = new CornerRadius(SettingsForm.CardRadius) };
        card.Children.Add(stack);
        return card;
    }

    private void Show(FrameworkElement? row, bool visible)
    {
        if (row is null)
        {
            return;
        }
        Visibility visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        row.Visibility = visibility;
        if (_separators.TryGetValue(row, out FrameworkElement? separator))
        {
            separator.Visibility = visibility;
        }
    }

    /// <summary>Mac TextField(value:format:): a typed length counts when Enter is pressed or the field loses focus.</summary>
    private void CommitLength()
    {
        if (_applying)
        {
            return;
        }
        if (long.TryParse(_length.Text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out long minutes))
        {
            minutes = Math.Clamp(minutes, BreakTimerSettings.MinimumMinutes, BreakTimerSettings.MaximumMinutes);
            Change((ref BreakTimerSettings settings) => settings.Minutes = minutes);
        }
        Apply(_model.Snapshot);
    }

    private delegate void BreakEdit(ref BreakTimerSettings settings);

    private void Change(BreakEdit edit)
    {
        if (_applying)
        {
            return;
        }
        BreakTimerSettings updated = _model.BreakTimerSettings;
        edit(ref updated);
        _model.SetBreakTimerSettings(updated);
        Apply(_model.Snapshot);
    }

    /// <summary>Shows the saved break settings; the controls never keep a state of their own.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        _applying = true;
        try
        {
            BreakTimerSettings settings = snapshot.Preferences.BreakTimer ?? new BreakTimerSettings();
            _preview.Apply(BreakFace.Preview(settings, _breaks.Session.EndTimeText(DateTimeOffset.Now.AddMinutes(settings.Minutes))));
            // The Mac's accessibilityValue: Narrator reads the item status right after the name.
            AutomationProperties.SetItemStatus(_previewFrame,
                $"{(settings.Title.Length == 0 ? BreakCopy.NoTitle : settings.Title)}, {settings.Minutes} minutes, {settings.Background.Label}, {settings.Palette.Label}");
            string start = BreakCopy.StartButton(settings.Minutes);
            _start.Retitle(start, StartWidth(start));
            bool canLock = _breaks.CanLock;
            _start.IsEnabled = !settings.LockWhenTouched || canLock;
            ActivationShortcut shortcut = WindowsShortcuts.Shortcut(GlobalShortcutAction.BreakTimer, snapshot.Preferences);
            _keycaps.Shortcut = shortcut;
            AutomationProperties.SetName(_shortcutRow, $"{BreakCopy.ShortcutLabel}, {WindowsShortcuts.Spoken(shortcut)}");
            string minutes = settings.Minutes.ToString(System.Globalization.CultureInfo.CurrentCulture);
            if (_length.Text != minutes && _length.FocusState == FocusState.Unfocused)
            {
                _length.Text = minutes;
            }
            _stepper.Value = settings.Minutes;
            if (_title.Text != settings.Title && _title.FocusState == FocusState.Unfocused)
            {
                _title.Text = settings.Title;
            }
            _endTime.IsOn = settings.ShowEndTime;
            _countOver.IsOn = settings.CountOver;
            _chime.IsOn = settings.Chime;
            int chosen = Array.IndexOf(s_backgrounds, settings.Background);
            for (var index = 0; index < _segments.Count; index++)
            {
                (_, SegmentButton segment, Border fill) = _segments[index];
                segment.IsChosen = index == chosen;
                fill.Background = index == chosen ? PickerColors.SegmentSelected : null;
            }
            for (var index = 0; index < _segmentDividers.Count; index++)
            {
                // The line between segments index and index + 1 shows only when neither is chosen.
                _segmentDividers[index].Visibility = chosen == index || chosen == index + 1 ? Visibility.Collapsed : Visibility.Visible;
            }
            Show(_blurRow, settings.Background == BreakBackground.Blur);
            foreach ((BreakPalette palette, ParityButton button, Ellipse fill, Ellipse ring, FrameworkElement check) in _swatches)
            {
                bool selected = palette == settings.Palette;
                button.AccessibleSelected = selected;
                fill.Width = fill.Height = selected ? 26 : 20;
                ring.StrokeThickness = selected ? 2.5 : 0;
                check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
            _allDisplays.IsOn = settings.AllDisplays;
            _lock.IsOn = settings.LockWhenTouched;
            _lockText.Text = settings.LockWhenTouched ? BreakCopy.LockingExplanation : BreakCopy.NoLockingExplanation;
            Show(_meetingRow, settings.LockWhenTouched);
            Show(_cannotLockRow, settings.LockWhenTouched && !canLock);
        }
        finally
        {
            _applying = false;
        }
    }
}
