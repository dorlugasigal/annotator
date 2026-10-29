using Annotator.App.Library;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Shortcuts;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Glyphs = Annotator.App.Icons.Glyphs;
using VirtualKey = Windows.System.VirtualKey;

namespace Annotator.App.Settings;

/// <summary>
/// The Toolbar &amp; Shortcuts tab of Settings (a port of ToolbarShortcutSettings.swift, ADR 0012, spec 005): your toolbar
/// and More tools, the toolbar's size and position, the keys of the buttons that are always there, the keys that work in
/// any app and the keys that stay the same. The Settings window hosts it with
/// <c>SettingsWindow.SetPage(SettingsTab.ToolbarAndShortcuts, page)</c>. Every change goes through the workspace model, so
/// the toolbar, the canvas keys and the global shortcuts follow at once. Its colors are set in code for the app's theme, so
/// a theme change makes the page again. Use it on the app thread.
/// </summary>
internal sealed partial class ToolbarShortcutsPage : Grid
{
    private ShortcutsPageBody _body;

    public ToolbarShortcutsPage(WorkspaceModel model)
    {
        _body = new ShortcutsPageBody(model);
        Children.Add(_body);
        void Rebuild()
        {
            Children.Remove(_body);
            _body = new ShortcutsPageBody(model);
            Children.Add(_body);
        }
        ActualThemeChanged += (_, _) => Rebuild();
        // A contrast theme or text size turned on later (WIN-UI-07, WIN-A11Y-03), while the page is in Settings.
        Loaded += (_, _) => AppResources.SystemLookChanged += Rebuild;
        Unloaded += (_, _) => AppResources.SystemLookChanged -= Rebuild;
    }

    /// <summary>The scroll area (the Mac's AXScrollArea).</summary>
    public ScrollViewer Scroller => _body.Scroller;
}

/// <summary>The page's content for one theme (<see cref="ToolbarShortcutsPage"/>).</summary>
internal sealed partial class ShortcutsPageBody : Grid
{
    /// <summary>The Mac's legacy scroller beside the content while it scrolls, in points.</summary>
    private const double ScrollerGutter = 17;

    private readonly WorkspaceModel _model;
    private readonly ShortcutEditor _editor;
    private readonly DispatcherQueue _queue;
    private readonly ScrollViewer _scroller = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Disabled,
    };
    private readonly ScrollContent _content;
    private readonly ToolbarArrangerView _arranger;
    private readonly ToolbarSizePicker _sizes;
    private readonly DirectionPicker _direction;
    private readonly ChoicePopUp _startAt = new("Start at", 126);
    private readonly ChoicePopUp _properties = new("Properties", 91);
    /// <summary>Mac Picker("Shrink"): Never, or after 3, 5, 10 or 30 seconds with the pointer off the toolbar.</summary>
    private readonly ChoicePopUp _shrink = new("Shrink", 152);
    private readonly ShrinkDemo _shrinkDemo = new();
    private readonly TextBlock _shrinkCaption = SettingsText.Label(ShrinkDemoText.Caption(null), 10, "AnnotatorSecondaryText");
    private readonly PropertyChoicesEditor _choices;
    private readonly List<KeyChip> _rowChips = [];
    private readonly List<GlobalKeyButton> _globals = [];
    private readonly AccessibleGroup _fixedGroup = new("Keys that stay the same") { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly FlowPanel _fixedKeys = new() { Spacing = 8, LineSpacing = 8 };
    private readonly Grid _toastHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 20, 14), MaxWidth = 600 };
    private readonly KeyCapture _capture = new();
    private SettingsToast? _toastShown;
    private FrameworkElement? _toastView;
    private DispatcherQueueTimer? _toastTimer;
    private IDisposable? _gate;
    private RecordingTarget? _recording;
    private bool _keyboardStarted;
    private bool _returningFocus;
    private string _state = string.Empty;
    private bool _attached;

    public ShortcutsPageBody(WorkspaceModel model)
    {
        _model = model;
        _queue = DispatcherQueue.GetForCurrentThread();
        _editor = new ShortcutEditor(model, InstalledKeyboardLayouts.Instance);
        _arranger = new ToolbarArrangerView(model, _editor, _editor.Say);
        _sizes = new ToolbarSizePicker(size =>
        {
            _model.SetToolbarSize(size);
        });
        _direction = new DirectionPicker(model.SetToolbarOrientation);
        AutomationProperties.SetAutomationId(_shrink, "toolbar-collapse-delay");
        _choices = new PropertyChoicesEditor(model);
        var stack = new MacStack { Orientation = Orientation.Vertical, Spacing = 28 };
        stack.Children.Add(_arranger.Root);
        stack.Children.Add(_choices.Root);
        stack.Children.Add(LayoutSection());
        stack.Children.Add(ButtonKeysSection());
        stack.Children.Add(GlobalKeysSection());
        stack.Children.Add(FixedKeysSection());
        _content = new ScrollContent(stack, _scroller, ScrollerGutter) { Padding = new Thickness(22, 18, 22, 64) };
        _scroller.Content = _content;
        Children.Add(_scroller);
        Children.Add(_toastHost);
        Children.Add(_capture);
        _capture.PreviewKeyDown += OnCaptureKeyDown;
        _capture.PreviewKeyUp += OnCaptureKeyUp;
        _capture.LostFocus += (_, _) =>
        {
            // Mac KeyCapture.resignFirstResponder: the key stops listening when the keyboard goes elsewhere.
            if (_editor.Recording is not null && !_returningFocus)
            {
                _editor.Stop();
            }
        };
        // Mac .onTapGesture on the content: a click outside the keys stops listening.
        _content.PointerPressed += (_, _) =>
        {
            if (_editor.Recording is not null)
            {
                _editor.Stop();
            }
        };
        _editor.Changed += Render;
        _editor.Refused += Shake;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    /// <summary>The scroll area (the Mac's AXScrollArea), for hosts that place it.</summary>
    public ScrollViewer Scroller => _scroller;

    private void Attach()
    {
        if (_attached)
        {
            return;
        }
        _attached = true;
        _model.Published += Apply;
        Apply(_model.Snapshot);
    }

    /// <summary>Mac onDisappear: recording stops, the toast goes, a drag ends and nothing keeps running.</summary>
    private void Detach()
    {
        if (!_attached)
        {
            return;
        }
        _attached = false;
        _model.Published -= Apply;
        _editor.Reset();
        _arranger.Detach();
        foreach (KeyChip chip in _rowChips)
        {
            chip.Detach();
        }
        foreach (GlobalKeyButton button in _globals)
        {
            button.Detach();
        }
        _toastTimer?.Stop();
        _gate?.Dispose();
        _gate = null;
    }

    // MARK: Sections

    /// <summary>Mac sectionTitle: the title at 15 points semibold and the detail at 12 points, 3 points apart.</summary>
    private static MacStack SectionTitle(string title, string detail)
    {
        var stack = new MacStack { Orientation = Orientation.Vertical, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(SettingsText.Static(title, 15, "AnnotatorPrimaryText", FontWeights.SemiBold));
        stack.Children.Add(SettingsText.Static(detail, 12, "AnnotatorSecondaryText"));
        return stack;
    }

    private FrameworkElement LayoutSection()
    {
        var section = new MacStack { Orientation = Orientation.Vertical, Spacing = 14 };
        section.Children.Add(SectionTitle("Size and position", "Choose how big the toolbar is and where it starts."));
        section.Children.Add(_sizes.Root);
        // Mac Form rows: the label column as wide as the widest label, labels trailing, 16 points before each control.
        var form = new MacStack { Orientation = Orientation.Vertical, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left };
        (string Label, FrameworkElement Control)[] rows =
            [("Direction", _direction.Root), ("Start at", _startAt), ("Properties", _properties), ("Shrink", _shrink)];
        foreach ((string text, FrameworkElement control) in rows)
        {
            var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Left };
            TextBlock label = SettingsText.Static(text, 13);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(new Grid { Width = SettingsText.MacWidth("Properties"), Height = 24, Children = { label } });
            control.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(control);
            form.Children.Add(row);
        }
        section.Children.Add(form);
        section.Children.Add(Caption("hand.draw",
            "Drag the toolbar to a screen edge to dock it there. Top and bottom make it horizontal; left and right make it vertical."));
        section.Children.Add(ShrinkCard());
        return section;
    }

    /// <summary>Mac's Shrink card: the demo, then "Shrink" and what it does for the chosen delay, on a soft card.</summary>
    private FrameworkElement ShrinkCard()
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(_shrinkDemo.Root);
        var words = new MacStack { Orientation = Orientation.Vertical, Spacing = 4, MaxWidth = 280, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(SettingsText.Label("Shrink", 13, "AnnotatorPrimaryText", FontWeights.SemiBold));
        _shrinkCaption.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(_shrinkCaption);
        row.Children.Add(words);
        return new Border
        {
            Child = row,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = SettingsColors.Primary(0.03),
            BorderBrush = SettingsColors.Primary(0.07),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    /// <summary>Mac Label(_:systemImage:) in the caption font and the secondary color.</summary>
    private static FrameworkElement Caption(string symbol, string text)
    {
        var caption = new AccessibleText(text);
        var line = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
        FrameworkElement icon = Glyphs.ForSymbol(symbol, 12, "AnnotatorGlyphSecondary");
        icon.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(icon);
        line.Children.Add(SettingsText.Label(text, 10, "AnnotatorSecondaryText"));
        caption.Children.Add(line);
        caption.HorizontalAlignment = HorizontalAlignment.Left;
        caption.SizeChanged += (_, _) => caption.Bounds = new Windows.Foundation.Rect(1, 0, Math.Max(0, caption.ActualWidth - 1), caption.ActualHeight);
        return caption;
    }

    /// <summary>The delay choices, plus a delay from an imported profile that isn't one of them (Mac collapseDelays).</summary>
    private static IReadOnlyList<double> CollapseDelays(double? current)
    {
        ValueList<double> choices = WorkspacePreferences.ToolbarCollapseDelays;
        if (current is not { } delay || choices.Contains(delay))
        {
            return [.. choices];
        }
        return [.. choices.Append(delay).Order()];
    }

    private static string DelayTitle(double? delay) => delay is { } seconds
        ? $"After {seconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} seconds" : "Never";

    private FrameworkElement ButtonKeysSection()
    {
        var section = new MacStack { Orientation = Orientation.Vertical, Spacing = 12 };
        section.Children.Add(SectionTitle("Keys for toolbar buttons",
            "These keys work while you draw, even when you take the button off your toolbar."));
        var grid = new ColumnsPanel { Columns = 2, Spacing = 10 };
        foreach (ShortcutCommand command in ShortcutPage.AlwaysThere)
        {
            grid.Children.Add(CommandRow(command));
        }
        if (ShortcutPage.AlwaysThere.Count % 2 == 1)
        {
            grid.Children.Add(new Grid());
        }
        var group = new AccessibleGroup(string.Empty, "toolbar-button-keys") { Children = { grid } };
        section.Children.Add(group);
        return section;
    }

    /// <summary>Mac commandRow: the button's symbol, its name and purpose, and its key chip, on a light rounded fill.</summary>
    private FrameworkElement CommandRow(ShortcutCommand command)
    {
        ShortcutAction action = command.Action!.Value;
        var row = new Grid
        {
            MinHeight = 58,
            Padding = new Thickness(12, 9, 12, 9),
            CornerRadius = new CornerRadius(10),
            Background = SettingsColors.Primary(0.035),
            ColumnSpacing = 10,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FrameworkElement glyph = Glyphs.ForSymbol(action.Symbol, 16, "AnnotatorGlyphSecondary");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(glyph);
        var texts = new MacStack { Orientation = Orientation.Vertical, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(SettingsText.Static(action.Label, 13, "AnnotatorPrimaryText", FontWeights.Medium));
        texts.Children.Add(new ExactLines(new WrappedText(action.Detail, 10, "AnnotatorSecondaryText"), SettingsText.LineHeight(10)));
        // The row's exact height, so the two rows and the sections below keep the Mac's places (MacStack snaps to it).
        texts.SizeChanged += (_, _) => row.MinHeight = Math.Max(58, (texts.ExactSize?.Height ?? 0) + row.Padding.Top + row.Padding.Bottom);
        SetColumn(texts, 1);
        row.Children.Add(texts);
        var chip = new KeyChip(RecordingTarget.Local(command), action.Label, compact: false, _editor.Toggle);
        chip.Button.HorizontalAlignment = HorizontalAlignment.Right;
        SetColumn(chip.Button, 3);
        row.Children.Add(chip.Button);
        _rowChips.Add(chip);
        return row;
    }

    private FrameworkElement GlobalKeysSection()
    {
        var section = new MacStack { Orientation = Orientation.Vertical, Spacing = 12 };
        section.Children.Add(SectionTitle("Keys that work in any app", "Hold Ctrl, Alt or Win and press a letter or number."));
        var rows = new MacStack { Orientation = Orientation.Vertical, Spacing = 8 };
        WorkspacePreferences preferences = _model.Snapshot.Preferences;
        foreach (GlobalShortcutKind kind in GlobalShortcutKind.AllCases)
        {
            var row = new Grid
            {
                // The Mac row: 9 points above and below one line of 13 points and one of 10.
                MinHeight = 49,
                Padding = new Thickness(12, 9, 12, 9),
                CornerRadius = new CornerRadius(10),
                Background = SettingsColors.Primary(0.035),
                ColumnSpacing = 12,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement symbol = Glyphs.ForSymbol(kind.Symbol, 16, "AnnotatorGlyphAccent");
            symbol.HorizontalAlignment = HorizontalAlignment.Center;
            symbol.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(symbol);
            var texts = new MacStack { Orientation = Orientation.Vertical, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(SettingsText.Static(kind.Title, 13, "AnnotatorPrimaryText", FontWeights.Medium));
            texts.Children.Add(SettingsText.Static(kind.Detail, 10, "AnnotatorSecondaryText"));
            SetColumn(texts, 1);
            row.Children.Add(texts);
            var button = new GlobalKeyButton(kind, kind.Value(preferences), _editor.Toggle);
            // Spacer(minLength: 12) between the words and the button, beside the 12-point spacing.
            button.Button.Margin = new Thickness(12, 0, 0, 0);
            SetColumn(button.Button, 2);
            row.Children.Add(button.Button);
            _globals.Add(button);
            rows.Children.Add(row);
        }
        section.Children.Add(rows);
        return section;
    }

    private FrameworkElement FixedKeysSection()
    {
        var section = new MacStack { Orientation = Orientation.Vertical, Spacing = 10 };
        section.Children.Add(SettingsText.Static("Keys that stay the same", 13, "AnnotatorPrimaryText", FontWeights.SemiBold));
        _fixedGroup.Children.Add(_fixedKeys);
        section.Children.Add(_fixedGroup);
        return section;
    }

    /// <summary>Mac fixedKey: the keys in a small keycap, then what they do; UI Automation reads both as one text.</summary>
    private static FrameworkElement FixedKey(FixedKey key)
    {
        var item = new AccessibleText(key.Spoken);
        var line = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6 };
        TextBlock keys = SettingsText.Label(key.Keys, 11, "AnnotatorPrimaryText", FontWeights.SemiBold);
        // The outline is drawn over the keycap, as SwiftUI's overlay stroke, so the keycap stays 20 points high.
        var keycap = new Grid();
        keycap.Children.Add(new Border
        {
            Padding = new Thickness(6, 3, 6, 3),
            CornerRadius = new CornerRadius(5),
            Background = SettingsColors.ControlBackground,
            Child = keys,
        });
        keycap.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { RadiusX = 5, RadiusY = 5, Stroke = SettingsColors.Primary(0.14), StrokeThickness = 1, IsHitTestVisible = false });
        line.Children.Add(keycap);
        TextBlock meaning = SettingsText.Label(key.Meaning, 10, "AnnotatorSecondaryText");
        line.Children.Add(meaning);
        item.Children.Add(line);
        return item;
    }

    // MARK: State

    /// <summary>Shows the workspace's toolbar, keys and shortcuts when they change.</summary>
    private void Apply(WorkspaceSnapshot snapshot)
    {
        WorkspacePreferences preferences = snapshot.Preferences;
        string state = State(snapshot);
        if (state == _state)
        {
            return;
        }
        _state = state;
        _arranger.Apply(snapshot);
        _sizes.Show(preferences.ToolbarSize ?? ToolbarSize.Expanded);
        ToolbarOrientation orientation = preferences.ToolbarOrientation ?? ToolbarOrientation.Horizontal;
        _direction.Show(orientation);
        ToolbarDockEdge edge = preferences.ToolbarDockEdge ?? ToolbarDockEdge.Automatic;
        ToolbarDockEdge[] edges = orientation == ToolbarOrientation.Vertical
            ? [ToolbarDockEdge.Automatic, ToolbarDockEdge.Left, ToolbarDockEdge.Right]
            : [ToolbarDockEdge.Automatic, ToolbarDockEdge.Top, ToolbarDockEdge.Bottom];
        _startAt.Show(EdgeTitle(edge), [.. edges.Select(choice => (EdgeTitle(choice), (Action)(() => _model.SetToolbarDockEdge(choice))))]);
        ToolbarPropertiesMode mode = ToolbarPropertiesMode.From(preferences);
        _properties.Show(mode.RawValue, [.. Enum.GetValues<ToolbarPropertiesMode>()
            .Select(choice => (choice.RawValue, (Action)(() => _model.ApplyPropertiesMode(choice))))]);
        double? collapse = preferences.ToolbarCollapseDelay;
        _shrinkCaption.Text = ShrinkDemoText.Caption(collapse);
        _shrink.Show(DelayTitle(collapse), [(DelayTitle(null), (Action)(() => _model.SetToolbarCollapseDelay(null))),
            .. CollapseDelays(collapse).Select(delay => (DelayTitle(delay), (Action)(() => _model.SetToolbarCollapseDelay(delay))))]);
        _choices.Apply(snapshot);
        _fixedKeys.Children.Clear();
        foreach (FixedKey key in ShortcutPage.FixedKeys(preferences, _model.SupportsGlobalShortcut(GlobalShortcutAction.LiveZoom)))
        {
            _fixedKeys.Children.Add(FixedKey(key));
        }
        Render();
    }

    /// <summary>What the page shows of a snapshot; other changes, such as drawing, leave the page alone.</summary>
    private string State(WorkspaceSnapshot snapshot)
    {
        WorkspacePreferences preferences = snapshot.Preferences;
        var state = new System.Text.StringBuilder();
        foreach (ShortcutCommand item in snapshot.ToolbarItems)
        {
            state.Append(item.Id).Append(',');
        }
        foreach ((string key, string tool) in preferences.ToolShortcuts)
        {
            state.Append(key).Append('=').Append(tool).Append(',');
        }
        foreach ((string key, string id) in preferences.CommandShortcuts ?? System.Collections.Immutable.ImmutableSortedDictionary<string, string>.Empty)
        {
            state.Append(key).Append('=').Append(id).Append(',');
        }
        state.Append('|').Append(preferences.ToolbarSize).Append('|').Append(preferences.ToolbarOrientation).Append('|')
            .Append(preferences.ToolbarDockEdge).Append('|').Append(ToolbarPropertiesMode.From(preferences))
            .Append('|').Append(string.Join(',', (IEnumerable<string>?)preferences.HiddenToolbarControls ?? []))
            .Append('|').Append(preferences.ToolbarCollapseDelay)
            .Append('|').Append(string.Join(',', (IEnumerable<string>?)preferences.ExpandedPropertyChoices ?? []));
        foreach (GlobalShortcutKind kind in GlobalShortcutKind.AllCases)
        {
            state.Append('|').Append(WindowsShortcuts.Label(kind.Value(preferences)))
                .Append(_model.SupportsGlobalShortcut(kind.Action) ? '+' : '-');
        }
        return state.ToString();
    }

    private static string EdgeTitle(ToolbarDockEdge edge) => edge switch
    {
        ToolbarDockEdge.Top => "Top edge",
        ToolbarDockEdge.Bottom => "Bottom edge",
        ToolbarDockEdge.Left => "Left edge",
        ToolbarDockEdge.Right => "Right edge",
        _ => "Automatic",
    };

    /// <summary>Shows the editor's state: which key listens, the held modifiers, and the toast.</summary>
    private void Render()
    {
        if (_state.Length == 0)
        {
            return;
        }
        WorkspacePreferences preferences = _model.Snapshot.Preferences;
        RecordingTarget? recording = _editor.Recording;
        _arranger.RenderChips();
        foreach (KeyChip chip in _rowChips)
        {
            chip.Show(preferences.ShortcutFor(chip.Target.Command!.Value), recording == chip.Target);
        }
        foreach (GlobalKeyButton button in _globals)
        {
            button.Show(button.Kind.Value(preferences), recording == RecordingTarget.Global(button.Kind), _editor.LiveModifierText,
                _model.SupportsGlobalShortcut(button.Kind.Action));
        }
        if (recording != _recording)
        {
            RecordingChanged(_recording, recording);
            _recording = recording;
        }
        ShowToast(_editor.Toast);
    }

    /// <summary>
    /// Mac KeyCapture: while a key listens, the page takes the keyboard (and the global shortcuts pressed in its window), then
    /// gives the keyboard back to the chip or button that listened.
    /// </summary>
    private void RecordingChanged(RecordingTarget? before, RecordingTarget? now)
    {
        if (now is not null)
        {
            _gate ??= ShortcutRecordingGate.Listen(OfferHotkey);
            if (before is null)
            {
                _keyboardStarted = FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard };
            }
            _capture.Focus(FocusState.Programmatic);
            return;
        }
        _gate?.Dispose();
        _gate = null;
        if (before is { } target && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), _capture) && Button(target) is { } button)
        {
            _returningFocus = true;
            button.Focus(_keyboardStarted ? FocusState.Keyboard : FocusState.Programmatic);
            _returningFocus = false;
        }
    }

    private Control? Button(RecordingTarget target)
    {
        if (target.Kind is { } kind)
        {
            return _globals.FirstOrDefault(button => button.Kind == kind)?.Button;
        }
        return _rowChips.Concat(_arranger.Chips).FirstOrDefault(chip => chip.Target == target)?.Button;
    }

    private void Shake(RecordingTarget target)
    {
        if (target.Kind is { } kind)
        {
            _globals.FirstOrDefault(button => button.Kind == kind)?.Shake();
            return;
        }
        foreach (KeyChip chip in _rowChips.Concat(_arranger.Chips).Where(chip => chip.Target == target))
        {
            chip.Shake();
        }
    }

    /// <summary>A global shortcut pressed while a key of this page listens and its window is in front.</summary>
    private bool OfferHotkey(GlobalShortcutAction action)
    {
        if (_editor.Recording is null || XamlRoot is null || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), _capture))
        {
            return false;
        }
        HWND window = new(Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId));
        return PInvoke.GetForegroundWindow() == window && _editor.HandleHotkey(action);
    }

    // MARK: Keys

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_editor.Recording is null)
        {
            return;
        }
        args.Handled = true;
        int virtualKey = (int)args.Key;
        // Keys from on-screen keyboards and other tools can come without a scan code; the layout knows the key's.
        uint scan = args.KeyStatus.ScanCode == 0 ? KeyCharacters.ScanCode(virtualKey)
            : args.KeyStatus.ScanCode | (args.KeyStatus.IsExtendedKey ? 0xE000u : 0u);
        ChordModifiers modifiers = Modifiers();
        string? character = KeyCharacters.Text(virtualKey, scan, modifiers.HasFlag(ChordModifiers.Shift));
        _editor.HandleKey(new RecordedKey(virtualKey, scan, modifiers, character));
    }

    private void OnCaptureKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (_editor.Recording is null)
        {
            return;
        }
        args.Handled = true;
        _editor.UpdateModifiers(Modifiers());
    }

    private static ChordModifiers Modifiers()
    {
        static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        ChordModifiers modifiers = ChordModifiers.None;
        if (Down(VirtualKey.Control))
        {
            modifiers |= ChordModifiers.Ctrl;
        }
        if (Down(VirtualKey.Menu))
        {
            modifiers |= ChordModifiers.Alt;
        }
        if (Down(VirtualKey.Shift))
        {
            modifiers |= ChordModifiers.Shift;
        }
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
        {
            modifiers |= ChordModifiers.Win;
        }
        return modifiers;
    }

    // MARK: Toast

    /// <summary>
    /// Mac toastView: the message in a capsule at the bottom of the page, with its symbol, an Undo that says what it puts back
    /// and, while a key listens, Cancel. It rises in on the snappy spring (a fade with Reduce Motion), goes after its time,
    /// and Narrator reads it as a notification.
    /// </summary>
    private void ShowToast(SettingsToast? toast)
    {
        if (toast == _toastShown)
        {
            return;
        }
        _toastShown = toast;
        _toastTimer?.Stop();
        bool reduce = PageMotion.ReducesMotion;
        MotionSamples motion = ShortcutEditorMotion.Toast(reduce);
        if (_toastView is { } old)
        {
            _toastView = null;
            if (toast is null)
            {
                PageMotion.Fade(old, old.Opacity, 0, motion);
                var timer = _queue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(motion.Duration);
                timer.IsRepeating = false;
                timer.Tick += (sender, _) =>
                {
                    sender.Stop();
                    _toastHost.Children.Remove(old);
                };
                timer.Start();
            }
            else
            {
                _toastHost.Children.Remove(old);
            }
        }
        if (toast is null)
        {
            return;
        }
        FrameworkElement view = ToastView(toast);
        _toastView = view;
        _toastHost.Children.Add(view);
        if (_toastHost.Children.Count == 1 || reduce)
        {
            if (reduce)
            {
                PageMotion.Fade(view, 0, 1, motion);
            }
            else
            {
                view.Loaded += (_, _) =>
                {
                    PageMotion.Slide(view, new System.Numerics.Vector3(0, (float)(view.ActualHeight + 14), 0), motion);
                    PageMotion.Fade(view, 0, 1, motion);
                };
            }
        }
        Announce(toast.Message);
        if (toast.Lifetime is { } lifetime)
        {
            _toastTimer ??= _queue.CreateTimer();
            _toastTimer.Interval = lifetime;
            _toastTimer.IsRepeating = false;
            Guid id = toast.Id;
            _toastTimer.Tick -= OnToastTimer;
            _toastTimer.Tick += OnToastTimer;
            _toastId = id;
            _toastTimer.Start();
        }
    }

    private Guid _toastId;

    private void OnToastTimer(DispatcherQueueTimer timer, object args)
    {
        timer.Stop();
        _editor.Expire(_toastId);
    }

    private FrameworkElement ToastView(SettingsToast toast)
    {
        var group = new AccessibleGroup(string.Empty, "settings-toast");
        var capsule = new Border
        {
            CornerRadius = new CornerRadius(100),
            Background = SettingsColors.Window,
            BorderBrush = SettingsColors.Primary(0.08),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 10, 16, 10),
        };
        var shadow = new ThemeShadow();
        capsule.Shadow = shadow;
        capsule.Translation = new System.Numerics.Vector3(0, 0, 16);
        shadow.Receivers.Add(_scroller);
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 10 };
        FrameworkElement symbol = SettingsSymbols.Filled(toast.Symbol, 15, toast.Tint switch
        {
            ToastTint.Success => SettingsColors.Green,
            ToastTint.Warning => SettingsColors.Orange,
            ToastTint.Accent => SettingsColors.Accent,
            _ => (AppResources.Brush("AnnotatorSecondaryLabelSolidBrush") as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray,
        });
        symbol.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(symbol);
        TextBlock message = SettingsText.Wrapping(toast.Message, 12, "AnnotatorPrimaryText");
        message.VerticalAlignment = VerticalAlignment.Center;
        message.MaxWidth = 600 - 32 - 26 - (toast.Undo is null ? 0 : 160) - (toast.Persistent ? 60 : 0);
        row.Children.Add(message);
        if (toast.Undo is not null)
        {
            Button undo = Link(toast.UndoTitle);
            AutomationProperties.SetAutomationId(undo, "undo-key-change");
            undo.Click += (_, _) => _editor.Undo();
            row.Children.Add(undo);
        }
        if (toast.Persistent)
        {
            Button cancel = Link("Cancel");
            cancel.Click += (_, _) => _editor.Stop();
            row.Children.Add(cancel);
        }
        capsule.Child = row;
        group.Children.Add(capsule);
        return group;
    }

    /// <summary>The Mac's .buttonStyle(.link): the title in the link color, nothing around it.</summary>
    private static Button Link(string title)
    {
        TextBlock label = SettingsText.Label(title, 12, "AnnotatorAccentText");
        var button = new Button
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            Content = label,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = true,
        };
        button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(button, title);
        return button;
    }

    /// <summary>Mac NSAccessibility announcement: Narrator reads the toast at once.</summary>
    private void Announce(string message)
    {
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, message, "settings-toast");
    }

    /// <summary>
    /// The scroll area's content: the page's sections with the Mac's padding, leaving the Mac's legacy scroller its width while
    /// the content is taller than the scroll area, so right-aligned controls keep the Mac's places and Windows' scroll bar
    /// never covers them.
    /// </summary>
    private sealed partial class ScrollContent : Grid
    {
        private readonly FrameworkElement _child;
        private readonly ScrollViewer _scroller;
        private readonly double _gutter;

        public ScrollContent(FrameworkElement child, ScrollViewer scroller, double gutter)
        {
            _child = child;
            _scroller = scroller;
            _gutter = gutter;
            Children.Add(child);
            scroller.SizeChanged += (_, _) => InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // The scroll viewer's own width: the width it offers its content can be a pixel or two less at 125%.
            double width = _scroller.ActualWidth > 0 ? _scroller.ActualWidth : double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
            double horizontal = Padding.Left + Padding.Right, vertical = Padding.Top + Padding.Bottom;
            double viewport = _scroller.ActualHeight;
            _child.Measure(new Size(Math.Max(0, width - horizontal - _gutter), double.PositiveInfinity));
            double height = MacStack.Extent(_child, horizontal: false).Along + vertical;
            if (viewport > 0 && height <= viewport)
            {
                _child.Measure(new Size(Math.Max(0, width - horizontal), double.PositiveInfinity));
                height = MacStack.Extent(_child, horizontal: false).Along + vertical;
                _scrolls = false;
            }
            else
            {
                _scrolls = true;
            }
            return new Size(width, height);
        }

        private bool _scrolls;

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = finalSize.Width - Padding.Left - Padding.Right - (_scrolls ? _gutter : 0);
            MacStack.ArrangeExact(_child, Padding.Left, Padding.Top, Math.Max(0, width), Math.Max(0, finalSize.Height - Padding.Top - Padding.Bottom), default,
                XamlRoot?.RasterizationScale ?? 1);
            return finalSize;
        }
    }

    /// <summary>
    /// Takes the keyboard while a key listens (Mac KeyCapture). It is invisible and outside UI Automation's control view;
    /// Narrator reads the recording prompt from the toast.
    /// </summary>
    private sealed partial class KeyCapture : UserControl
    {
        public KeyCapture()
        {
            IsTabStop = true;
            Width = 1;
            Height = 1;
            Opacity = 0;
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            UseSystemFocusVisuals = false;
            AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        }
    }
}

/// <summary>The character a key gives in the active keyboard layout with Shift alone (the Mac's charactersIgnoringModifiers).</summary>
internal static class KeyCharacters
{
    /// <summary>A key's scan code in the active layout, with 0xE0 or 0xE1 in the high byte for an extended key.</summary>
    public static uint ScanCode(int virtualKey) =>
        PInvoke.MapVirtualKeyEx((uint)virtualKey, Windows.Win32.UI.Input.KeyboardAndMouse.MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX, PInvoke.GetKeyboardLayout(0));

    public static unsafe string? Text(int virtualKey, uint scanCode, bool shift)
    {
        Span<byte> state = stackalloc byte[256];
        state.Clear();
        if (shift)
        {
            state[0x10] = 0x80;
        }
        Span<char> text = stackalloc char[8];
        fixed (byte* keys = state)
        fixed (char* output = text)
        {
            // Flag 4 leaves the keyboard state (dead keys) untouched; a dead key gives its spacing character.
            int produced = PInvoke.ToUnicodeEx((uint)virtualKey, scanCode & 0xFF, keys, output, text.Length, 4, PInvoke.GetKeyboardLayout(0));
            return produced < 0 ? text[..1].ToString() : produced > 0 ? text[..produced].ToString() : null;
        }
    }
}
