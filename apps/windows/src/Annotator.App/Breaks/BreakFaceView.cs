using System.Globalization;
using System.Numerics;
using Annotator.Core;
using Annotator.Core.Motion;
using Annotator.App.Shell;
using Annotator.Workspace.Breaks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Size = Windows.Foundation.Size;
using TextAlignment = Microsoft.UI.Xaml.TextAlignment;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Annotator.App.Breaks;

/// <summary>The colors of one break palette (Mac BreakPaletteColors), the same sRGB values as the Mac.</summary>
internal readonly record struct BreakColors(Color Top, Color Bottom, Color[] Fields, Color Accent)
{
    public static readonly Color Warning = Color.FromArgb(255, 0xFF, 0xB5, 0x47);

    public static BreakColors For(BreakPalette palette) => palette switch
    {
        BreakPalette.Ocean => new(Hex(0x0D1D33), Hex(0x06101D), [Hex(0x1F5FA8), Hex(0x3FB6C9), Hex(0x4B3F9E)], Hex(0x8FD3FF)),
        BreakPalette.Sunset => new(Hex(0x2C1521), Hex(0x170A12), [Hex(0xD9604A), Hex(0xF0A458), Hex(0x8E3B7A)], Hex(0xFFC995)),
        BreakPalette.Berry => new(Hex(0x24132F), Hex(0x12091B), [Hex(0x7D3FC4), Hex(0xC9558F), Hex(0x3D4FC0)], Hex(0xF0B0FF)),
        BreakPalette.Graphite => new(Hex(0x1C1E21), Hex(0x0E0F11), [Hex(0x3C4450), Hex(0x5D6B7A), Hex(0x2B3138)], Hex(0xE8ECEF)),
        _ => new(Hex(0x16241C), Hex(0x0A110D), [Hex(0x2F6B4F), Hex(0x7FAF73), Hex(0x1F4D5C)], Hex(0x9BE3B4)),
    };

    public static Color Hex(uint value) => Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    public static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
}

/// <summary>What the break screen's buttons and pointer do; the Settings preview leaves them empty.</summary>
internal sealed class BreakFaceActions
{
    public Action RemoveMinute { get; init; } = () => { };
    public Action TogglePause { get; init; } = () => { };
    public Action AddMinute { get; init; } = () => { };
    public Action End { get; init; } = () => { };
    public Action<bool> HoverControls { get; init; } = _ => { };
}

/// <summary>
/// One display's break screen (Mac BreakScreenFace): the moving colors, the title, the countdown with its ring, the end time,
/// the lock line or the controls, and the notice. The ring empties with one composition animation and the colors drift with
/// looping ones, so a long break costs no frames on the app thread; text changes once a second. Every size follows the
/// shorter side of the screen, as on the Mac. With Animation effects off there is no drift, ripple, breathing or rolling;
/// short fades replace movement, and the ring still empties because it carries the time.
/// </summary>
internal sealed partial class BreakFaceView
{
    private const string MinusData = "M 5,12 L 19,12";
    private const string PlusData = "M 5,12 L 19,12 M 12,5 L 12,19";
    private const string CrossData = "M 6,6 L 18,18 M 18,6 L 6,18";
    private const string PauseData = "M 7,5 L 10,5 L 10,19 L 7,19 Z M 14,5 L 17,5 L 17,19 L 14,19 Z";
    private const string PlayData = "M 7,4 L 19,12 L 7,20 Z";

    private readonly bool _preview;
    private readonly bool _reduceMotion;
    private readonly BreakFaceActions _actions;
    private readonly Grid _backdrop = new() { IsHitTestVisible = false };
    private readonly Grid _content = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new()
    {
        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly Grid _ring = new();
    private readonly Border _ringHost = new();
    private readonly TextBlock _label = new() { TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _subline = new() { TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _pausedText = new() { Text = BreakCopy.Paused, FontWeight = FontWeights.SemiBold };
    private readonly Border _paused = new() { HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _badge = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
    private readonly Ellipse[] _ripples = [new(), new(), new()];
    private readonly AccessibleElement _countdown = new(AutomationControlType.Custom) { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly StackPanel _bottom = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Spacing = 8 };
    private readonly AccessibleElement _chip = new(AutomationControlType.Text) { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _chipRow = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _chipText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _controls = new() { HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Border _controlsStroke = new() { IsHitTestVisible = false };
    private readonly Rectangle _controlsDivider = new() { Width = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _controlRow = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly AccessibleElement _notice = new(AutomationControlType.Text)
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
    };
    private readonly Grid _noticeRow = new();
    private readonly TextBlock _noticeText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _remove;
    private readonly Button _pause;
    private readonly TextBlock _pauseText = new() { Text = BreakCopy.Pause, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private bool? _pauseShowsPlay;
    private string? _chipSymbol;
    private Size _size;
    private BreakFace? _face;
    private BreakTimerSettings? _look;
    private Color _ringColor;
    private int _ringGeneration = -1;
    private int _rippleGeneration = -1;
    private int? _badgeId;
    private bool _breathing;
    private bool _entered;
    private string _shownLabel = string.Empty;

    // Ring (composition)
    private ShapeVisual? _shapes;
    private CompositionEllipseGeometry? _arc;
    private CompositionEllipseGeometry? _glowArc;
    private CompositionColorBrush? _arcBrush;
    private CompositionColorBrush? _glowBrush;
    private double _drainFrom;
    private double _drainSeconds;
    private DateTimeOffset _drainStart;
    private double _shownFraction;
    private bool _ringShown;

    public BreakFaceView(bool preview, BreakFaceActions actions, bool reduceMotion)
    {
        _preview = preview;
        _actions = actions;
        _reduceMotion = reduceMotion;
        Root = new Grid { RequestedTheme = ElementTheme.Dark, Background = new SolidColorBrush(Colors.Transparent) };
        Root.Children.Add(_backdrop);
        // The notice comes first, as UI Automation and the Mac read the screen from the top.
        Root.Children.Add(_notice);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(_title);
        foreach (Ellipse ripple in _ripples)
        {
            ripple.Stroke = new SolidColorBrush(BreakColors.Warning);
            ripple.Opacity = 0;
            ripple.IsHitTestVisible = false;
            ripple.HorizontalAlignment = HorizontalAlignment.Center;
            ripple.VerticalAlignment = VerticalAlignment.Center;
            _ring.Children.Add(ripple);
        }
        _ring.Children.Add(_ringHost);
        var inner = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        inner.Children.Add(_label);
        inner.Children.Add(_subline);
        _paused.Child = _pausedText;
        inner.Children.Add(_paused);
        _ring.Children.Add(inner);
        _ring.Children.Add(_badge);
        _countdown.Children.Add(_ring);
        center.Children.Add(_countdown);
        _content.Children.Add(center);
        Root.Children.Add(_content);
        _chip.Children.Add(_chipRow);
        _bottom.Children.Add(_chip);
        _bottom.Children.Add(_controls);
        Root.Children.Add(_bottom);
        _noticeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _noticeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_noticeText, 1);
        _noticeRow.Children.Add(_noticeText);
        _notice.Children.Add(_noticeRow);
        var display = new FontFamily("Segoe UI Variable Display");
        _label.FontFamily = display;
        _label.FontWeight = FontWeights.SemiBold;
        _label.Foreground = new SolidColorBrush(Colors.White);
        _title.FontFamily = display;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = new SolidColorBrush(BreakColors.WithAlpha(Colors.White, 0.88));
        _subline.Foreground = new SolidColorBrush(BreakColors.WithAlpha(Colors.White, 0.7));
        _subline.FontWeight = FontWeights.Medium;
        _badge.FontWeight = FontWeights.SemiBold;
        _pausedText.Foreground = new SolidColorBrush(Colors.White);
        _paused.Background = new SolidColorBrush(BreakColors.WithAlpha(Colors.White, 0.16));
        _chip.Background = new SolidColorBrush(BreakColors.WithAlpha(Colors.White, 0.1));
        _chipText.Foreground = new SolidColorBrush(BreakColors.WithAlpha(Colors.White, 0.85));
        _chipText.FontWeight = FontWeights.Medium;
        _notice.Background = new SolidColorBrush(Color.FromArgb(82, 0xFF, 0x95, 0x00));
        _noticeText.Foreground = new SolidColorBrush(Colors.White);
        _noticeText.FontWeight = FontWeights.Medium;
        AutomationProperties.SetAutomationId(_chip, "break-lock-status");
        AutomationProperties.SetAutomationId(_notice, "break-notice");
        AutomationProperties.SetName(_countdown, BreakCopy.CountdownName);
        AutomationProperties.SetAutomationId(_countdown, "break-countdown");
        AutomationProperties.SetAccessibilityView(_ring, AccessibilityView.Raw);
        _remove = ControlButton(MinusData, false, BreakCopy.OneMinute, BreakCopy.RemoveMinute, "break-remove-minute", false, () => _actions.RemoveMinute());
        _pause = ControlButton(PauseData, true, _pauseText, BreakCopy.Pause, "break-pause", false, () => _actions.TogglePause());
        Button add = ControlButton(PlusData, false, BreakCopy.OneMinute, BreakCopy.AddMinute, "break-add-minute", false, () => _actions.AddMinute());
        Button end = ControlButton(CrossData, false, BreakCopy.EndBreak, BreakCopy.EndBreak, "break-end", true, () => _actions.End());
        _controlRow.Children.Add(_remove);
        _controlRow.Children.Add(_pause);
        _controlRow.Children.Add(add);
        _controlsDivider.Fill = new SolidColorBrush(BreakColors.WithAlpha(Colors.White, 0.18));
        _controlRow.Children.Add(_controlsDivider);
        _controlRow.Children.Add(end);
        // Mac Capsule().strokeBorder: the outline is drawn inside the capsule, so it adds no size.
        _controlsStroke.BorderBrush = new SolidColorBrush(BreakColors.WithAlpha(Colors.White, 0.12));
        _controlsStroke.BorderThickness = new Thickness(1);
        var controls = new Grid();
        controls.Children.Add(_controlRow);
        controls.Children.Add(_controlsStroke);
        _controls.Child = controls;
        _controls.Background = new SolidColorBrush(Color.FromArgb(150, 20, 22, 24));
        _controls.PointerEntered += (_, _) => _actions.HoverControls(true);
        _controls.PointerExited += (_, _) => _actions.HoverControls(false);
        AutomationProperties.SetName(_controls, BreakCopy.ControlsName);
        Root.SizeChanged += (_, args) => Layout(args.NewSize);
        if (!preview)
        {
            ElementCompositionPreview.GetElementVisual(_content).Opacity = 0;
        }
    }

    /// <summary>The whole screen: put it in a window, or in the Settings preview.</summary>
    public Grid Root { get; }

    /// <summary>The countdown element; Narrator reads its name and help text, and announcements are raised on it.</summary>
    public FrameworkElement Countdown => _countdown;

    // Applying a face

    public void Apply(BreakFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        BreakFace? previous = _face;
        _face = face;
        if (_look is not { } look || look.Background != face.Settings.Background || look.Palette != face.Settings.Palette)
        {
            _look = face.Settings;
            BuildBackdrop();
        }
        _title.Text = face.Title;
        _title.Visibility = face.Title.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetLabel(face.Label, previous is not null && face.Phase != BreakPhase.TimeUp);
        _label.Opacity = face.Phase == BreakPhase.Paused ? 0.45 : 1;
        _paused.Visibility = face.Phase == BreakPhase.Paused ? Visibility.Visible : Visibility.Collapsed;
        if (face.Phase == BreakPhase.Paused && previous is not null && previous.Phase != BreakPhase.Paused && !_preview)
        {
            Pop(_paused);
        }
        string? subline = face.Phase switch
        {
            BreakPhase.Paused => null,
            BreakPhase.TimeUp => face.Settings.CountOver && face.Label != "0:00" ? BreakCopy.OverTime : null,
            _ => face.EndTime,
        };
        _subline.Text = subline ?? string.Empty;
        _subline.Visibility = subline is null ? Visibility.Collapsed : Visibility.Visible;
        // The Mac's accessibilityValue: Narrator reads the item status right after the name.
        AutomationProperties.SetItemStatus(_countdown, face.SpokenValue);
        ApplyBottom(face);
        ApplyNotice(face);
        ApplyBadge(face.Badge);
        ApplyRing(face);
        if (face.RippleGeneration != _rippleGeneration)
        {
            bool first = _rippleGeneration < 0;
            _rippleGeneration = face.RippleGeneration;
            if (!first && face.Phase == BreakPhase.TimeUp)
            {
                Ripple();
            }
        }
        if (face.Closing && previous?.Closing != true)
        {
            Exit();
        }
    }

    /// <summary>Mac onAppear: the content rises 24 points and grows from 94% into place while the window fades in.</summary>
    public void Enter(bool animated)
    {
        if (_entered || _preview)
        {
            return;
        }
        _entered = true;
        Visual content = ElementCompositionPreview.GetElementVisual(_content);
        Compositor compositor = content.Compositor;
        if (!animated || _reduceMotion)
        {
            content.StartAnimation("Opacity", Scalar(compositor, 0, 1, TimeSpan.FromSeconds(animated ? MotionTiming.Reduced.Duration : 0.01), MotionCurves.EaseOut));
            return;
        }
        ElementCompositionPreview.SetIsTranslationEnabled(_content, true);
        CenterOn(content, _content);
        TimeSpan duration = TimeSpan.FromSeconds(0.6);
        content.StartAnimation("Opacity", Scalar(compositor, 0, 1, TimeSpan.FromSeconds(0.45), MotionCurves.Fade));
        content.StartAnimation("Translation", Vector(compositor, new Vector3(0, (float)(24 * Unit / 900), 0), Vector3.Zero, duration, MotionCurves.Enter));
        content.StartAnimation("Scale", Vector(compositor, new Vector3(0.94f, 0.94f, 1), Vector3.One, duration, MotionCurves.Enter));
    }

    /// <summary>Mac closing: the content shrinks to 96% and fades with the whole screen.</summary>
    private void Exit()
    {
        if (_preview)
        {
            return;
        }
        Visual root = ElementCompositionPreview.GetElementVisual(Root);
        Compositor compositor = root.Compositor;
        TimeSpan duration = TimeSpan.FromSeconds(_reduceMotion ? MotionTiming.Reduced.Duration : 0.28);
        root.StartAnimation("Opacity", Scalar(compositor, 1, 0, duration, MotionCurves.EaseIn));
        if (!_reduceMotion)
        {
            Visual content = ElementCompositionPreview.GetElementVisual(_content);
            CenterOn(content, _content);
            content.StartAnimation("Scale", Vector(compositor, Vector3.One, new Vector3(0.96f, 0.96f, 1), duration, MotionCurves.Exit));
        }
    }

    // Layout

    private double Unit => Math.Max(Math.Min(_size.Width, _size.Height), 1);

    private void Layout(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0 || size == _size)
        {
            return;
        }
        _size = size;
        // The drifting color fields reach past the screen's edges; the Settings preview must not show them over the page.
        Root.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, size.Width, size.Height) };
        double unit = Unit;
        double ring = unit * 0.6, line = Math.Max(1.5, unit * 0.016), text = unit * 0.021;
        _title.FontSize = unit * 0.052;
        _title.MaxWidth = unit * 1.3;
        _title.Margin = new Thickness(0, 0, 0, unit * 0.04);
        _ring.Width = ring;
        _ring.Height = ring;
        _ringHost.Width = ring;
        _ringHost.Height = ring;
        _label.FontSize = unit * 0.17;
        _label.MaxWidth = ring * 0.82;
        _subline.FontSize = unit * 0.032;
        _subline.Margin = new Thickness(0, unit * 0.014, 0, 0);
        _pausedText.FontSize = unit * 0.03;
        _paused.Padding = new Thickness(unit * 0.022, unit * 0.008, unit * 0.022, unit * 0.008);
        _paused.CornerRadius = new CornerRadius(unit);
        _paused.Margin = new Thickness(0, unit * 0.014, 0, 0);
        _badge.FontSize = unit * 0.034;
        _badge.Margin = new Thickness(ring * 0.72, 0, 0, ring * 0.76);
        foreach (Ellipse ripple in _ripples)
        {
            ripple.StrokeThickness = line * 0.6;
        }
        // SwiftUI's frame of the countdown includes the half of the ripples' stroke that lies outside the ring.
        _countdown.Overhang = line * 0.3;
        _bottom.Margin = new Thickness(0, 0, 0, unit * 0.06);
        _chip.Padding = new Thickness(text * 1.1, text * 0.6, text * 1.1, text * 0.6);
        _chip.CornerRadius = new CornerRadius(unit);
        _chipText.FontSize = text;
        SetLine(_chipText, text, ChipLine);
        _chipSymbol = null;
        _notice.Margin = new Thickness(0, unit * 0.05, 0, 0);
        _notice.Padding = new Thickness(text * 1.1, text * 0.7, text * 1.1, text * 0.7);
        _notice.CornerRadius = new CornerRadius(text * 1.2);
        _notice.MaxWidth = unit * 1.1;
        _noticeText.FontSize = text;
        _noticeRow.ColumnSpacing = text * 0.6;
        SetNoticeIcon(text);
        LayoutControls(text);
        BuildRing(ring, line);
        BuildBackdrop();
        if (_face is { } face)
        {
            ApplyBottom(face);
        }
    }

    // Countdown text

    private void SetLabel(string label, bool roll)
    {
        if (label == _shownLabel)
        {
            return;
        }
        _shownLabel = label;
        _label.Text = label;
        if (!roll || _preview)
        {
            return;
        }
        // Mac numericText(countsDown: true): the new value comes down from above and settles.
        Visual visual = ElementCompositionPreview.GetElementVisual(_label);
        Compositor compositor = visual.Compositor;
        if (_reduceMotion)
        {
            visual.StartAnimation("Opacity", Scalar(compositor, 0.6f, 1, TimeSpan.FromSeconds(MotionTiming.Reduced.Duration), MotionCurves.EaseOut));
            return;
        }
        ElementCompositionPreview.SetIsTranslationEnabled(_label, true);
        float drop = (float)(_label.FontSize * 0.12);
        visual.StartAnimation("Translation", Vector(compositor, new Vector3(0, -drop, 0), Vector3.Zero, TimeSpan.FromSeconds(0.42), MotionCurves.Enter));
        visual.StartAnimation("Opacity", Scalar(compositor, 0.35f, 1, TimeSpan.FromSeconds(0.3), MotionCurves.Fade));
    }

    // Lock line and controls

    private void ApplyBottom(BreakFace face)
    {
        double text = Unit * 0.021;
        switch (face.Lock)
        {
            case BreakLockState.Arming:
                SetChip("timer", BreakCopy.Arming(face.ArmingSeconds), text);
                _chip.Visibility = Visibility.Visible;
                _controls.Visibility = Visibility.Collapsed;
                break;
            case BreakLockState.Armed or BreakLockState.Locking:
                SetChip("lock.fill", BreakCopy.LocksWhenTouched, text);
                _chip.Visibility = Visibility.Visible;
                _controls.Visibility = Visibility.Collapsed;
                break;
            default:
                _chip.Visibility = Visibility.Collapsed;
                bool show = face.ControlsVisible && !_preview;
                if (show && _controls.Visibility != Visibility.Visible)
                {
                    _controls.Visibility = Visibility.Visible;
                    Slide(_controls, true);
                }
                else if (!show)
                {
                    _controls.Visibility = Visibility.Collapsed;
                }
                break;
        }
        bool paused = face.Phase == BreakPhase.Paused;
        _pauseText.Text = paused ? BreakCopy.Resume : BreakCopy.Pause;
        _pauseText.MinWidth = s_macWords[_pauseText.Text] * text;
        AutomationProperties.SetName(_pause, paused ? BreakCopy.Resume : BreakCopy.Pause);
        _pause.IsEnabled = face.Phase != BreakPhase.TimeUp;
        _remove.IsEnabled = face.Phase != BreakPhase.TimeUp;
        SetPauseIcon(paused, text);
    }

    /// <summary>SF Rounded's line height per point size in the chip (medium) and the controls (semibold), from the Mac capture.</summary>
    private const double ChipLine = 1.26;

    private const double ControlLine = 1.28;

    /// <summary>The controls' words' widths in the Mac capture, in em of their size.</summary>
    private static readonly Dictionary<string, double> s_macWords = new(StringComparer.Ordinal)
    {
        [BreakCopy.OneMinute] = 2.8,
        [BreakCopy.Pause] = 2.99,
        [BreakCopy.Resume] = 4.1,
        [BreakCopy.EndBreak] = 5.18,
    };

    private static void SetLine(TextBlock block, double size, double ratio)
    {
        block.LineHeight = size * ratio;
        block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    /// <summary>Mac Label(notice, systemImage: "exclamationmark.triangle.fill"): the symbol before the first line.</summary>
    private void SetNoticeIcon(double size)
    {
        foreach (UIElement old in _noticeRow.Children.Where(child => child != _noticeText).ToList())
        {
            _noticeRow.Children.Remove(old);
        }
        FrameworkElement icon = Icons.Glyphs.ForSymbol("exclamationmark.triangle.fill", size, "AnnotatorGlyphOnColor");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, size * 0.15, 0, 0);
        _noticeRow.Children.Add(icon);
    }

    private void SetChip(string symbol, string text, double size)
    {
        _chipText.Text = text;
        AutomationProperties.SetName(_chip, text);
        if (_chipSymbol == symbol)
        {
            return;
        }
        _chipSymbol = symbol;
        _chipRow.Children.Clear();
        FrameworkElement icon = Icons.Glyphs.ForSymbol(symbol, size * 0.9, "AnnotatorGlyphOnColor");
        icon.VerticalAlignment = VerticalAlignment.Center;
        _chipRow.Children.Add(icon);
        _chipRow.Children.Add(_chipText);
    }

    private void LayoutControls(double text)
    {
        _controls.Padding = new Thickness(text * 0.45);
        _controls.CornerRadius = new CornerRadius(text * 4);
        // SF Rounded is wider than Segoe UI: the words keep the Mac's widths (em) so the buttons keep their places (D-4).
        _controlsStroke.Margin = new Thickness(-text * 0.45);
        _controlsStroke.CornerRadius = new CornerRadius(text * 4);
        _controlRow.Spacing = text * 0.5;
        _controlsDivider.Height = text * 1.6;
        foreach (UIElement child in _controlRow.Children)
        {
            if (child is not Button { Content: StackPanel content } button)
            {
                continue;
            }
            button.Padding = new Thickness(text * 0.95, text * 0.55, text * 0.95, text * 0.55);
            button.Height = text * (1.1 + ControlLine);
            button.CornerRadius = new CornerRadius(text * 2);
            content.Spacing = text * 0.35;
            foreach (UIElement part in content.Children)
            {
                if (part is Viewbox icon)
                {
                    icon.Width = text * 0.85;
                    icon.Height = text * 0.85;
                }
                else if (part is TextBlock label)
                {
                    label.FontSize = text;
                    SetLine(label, text, ControlLine);
                    label.MinWidth = s_macWords.TryGetValue(label.Text, out double em) ? em * text : 0;
                    label.TextAlignment = TextAlignment.Center;
                }
            }
        }
    }

    private Button ControlButton(string iconData, bool filled, object label, string name, string identifier, bool prominent, Action action)
    {
        Color fill = prominent ? BreakColors.WithAlpha(Colors.White, 0.88) : BreakColors.WithAlpha(Colors.White, 0.08);
        Color hover = prominent ? Colors.White : BreakColors.WithAlpha(Colors.White, 0.18);
        Color foreground = prominent ? Color.FromArgb(217, 0, 0, 0) : Colors.White;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(Glyph(iconData, filled, foreground, 12));
        TextBlock text = label as TextBlock ?? new TextBlock { Text = (string)label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        // The button's name is its label; its words are not read again.
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        content.Children.Add(text);
        var button = new Button { Content = content, BorderThickness = new Thickness(0) };
        button.Resources["ButtonBackground"] = new SolidColorBrush(fill);
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(hover);
        button.Resources["ButtonBackgroundDisabled"] = new SolidColorBrush(BreakColors.WithAlpha(fill, fill.A / 255.0 * 0.4));
        button.Resources["ButtonForeground"] = new SolidColorBrush(foreground);
        button.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(foreground);
        button.Resources["ButtonForegroundPressed"] = new SolidColorBrush(foreground);
        button.Resources["ButtonForegroundDisabled"] = new SolidColorBrush(BreakColors.WithAlpha(foreground, 0.4));
        foreach (string key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled" })
        {
            button.Resources[key] = new SolidColorBrush(Colors.Transparent);
        }
        AutomationProperties.SetName(button, name);
        AutomationProperties.SetAutomationId(button, identifier);
        button.Click += (_, _) => action();
        return button;
    }

    private void SetPauseIcon(bool paused, double text)
    {
        if (_pauseShowsPlay == paused || _pause.Content is not StackPanel content || content.Children.Count == 0)
        {
            return;
        }
        _pauseShowsPlay = paused;
        content.Children.RemoveAt(0);
        Viewbox icon = Glyph(paused ? PlayData : PauseData, true, Colors.White, text * 0.85);
        content.Children.Insert(0, icon);
    }

    /// <summary>A 24-unit glyph in a Viewbox, as the app's Glyphs draws Lucide icons, for the controls' simple shapes.</summary>
    private static Viewbox Glyph(string data, bool filled, Color color, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new XamlPath
        {
            Data = Figures(data),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stroke = new SolidColorBrush(color),
            Fill = filled ? new SolidColorBrush(color) : null,
        });
        var box = new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(box, AccessibilityView.Raw);
        return box;
    }

    /// <summary>Builds "M x,y L x,y ... Z" figures, the subset the controls' glyphs use.</summary>
    private static PathGeometry Figures(string data)
    {
        var geometry = new PathGeometry();
        PathFigure? figure = null;
        string[] parts = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            switch (parts[index])
            {
                case "M":
                    figure = new PathFigure { StartPoint = Pair(parts[++index]), IsClosed = false, IsFilled = false };
                    geometry.Figures.Add(figure);
                    break;
                case "L":
                    figure?.Segments.Add(new LineSegment { Point = Pair(parts[++index]) });
                    break;
                case "Z":
                    if (figure is not null)
                    {
                        figure.IsClosed = true;
                        figure.IsFilled = true;
                    }
                    break;
                default:
                    figure?.Segments.Add(new LineSegment { Point = Pair(parts[index]) });
                    break;
            }
        }
        return geometry;
    }

    private static Windows.Foundation.Point Pair(string pair)
    {
        string[] values = pair.Split(',');
        return new Windows.Foundation.Point(double.Parse(values[0], CultureInfo.InvariantCulture), double.Parse(values[1], CultureInfo.InvariantCulture));
    }

    private void ApplyNotice(BreakFace face)
    {
        if (face.Notice is { } notice)
        {
            _noticeText.Text = notice;
            AutomationProperties.SetName(_notice, notice);
            if (_notice.Visibility != Visibility.Visible)
            {
                _notice.Visibility = Visibility.Visible;
                Slide(_notice, false);
            }
        }
        else
        {
            _notice.Visibility = Visibility.Collapsed;
        }
    }

    // Badge, pop and ripples

    private void ApplyBadge(BreakBadge? badge)
    {
        if (badge is null || badge.Id == _badgeId || _preview)
        {
            return;
        }
        _badgeId = badge.Id;
        _badge.Text = badge.Text;
        _badge.Foreground = new SolidColorBrush(BreakColors.For(_look?.Palette ?? BreakPalette.Forest).Accent);
        Visual visual = ElementCompositionPreview.GetElementVisual(_badge);
        Compositor compositor = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(_badge, true);
        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.15f, 1);
        fade.InsertKeyFrame(0.7f, 1);
        fade.InsertKeyFrame(1, 0);
        fade.Duration = TimeSpan.FromSeconds(1.2);
        visual.StartAnimation("Opacity", fade);
        if (!_reduceMotion)
        {
            visual.StartAnimation("Translation", Vector(compositor, Vector3.Zero, new Vector3(0, -(float)(Unit * 0.05), 0), TimeSpan.FromSeconds(1.2), MotionCurves.EaseOut));
        }
    }

    private void Pop(FrameworkElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        if (_reduceMotion)
        {
            visual.StartAnimation("Opacity", Scalar(compositor, 0, 1, TimeSpan.FromSeconds(MotionTiming.Reduced.Duration), MotionCurves.EaseOut));
            return;
        }
        CenterOn(visual, element);
        visual.StartAnimation("Scale", Spring(compositor, new Vector3(0.6f, 0.6f, 1), Vector3.One, MotionTiming.Playful));
    }

    private void Slide(FrameworkElement element, bool fromBelow)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        TimeSpan duration = TimeSpan.FromSeconds(_reduceMotion ? MotionTiming.Reduced.Duration : 0.32);
        visual.StartAnimation("Opacity", Scalar(compositor, 0, 1, duration, MotionCurves.Fade));
        if (!_reduceMotion)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            float distance = (float)(Unit * 0.03) * (fromBelow ? 1 : -1);
            visual.StartAnimation("Translation", Vector(compositor, new Vector3(0, distance, 0), Vector3.Zero, duration, MotionCurves.Enter));
        }
    }

    /// <summary>Mac zero: three rings ripple outward and fade over 1.6 s.</summary>
    private void Ripple()
    {
        if (_reduceMotion || _preview)
        {
            return;
        }
        for (var index = 0; index < _ripples.Length; index++)
        {
            Ellipse ripple = _ripples[index];
            Visual visual = ElementCompositionPreview.GetElementVisual(ripple);
            Compositor compositor = visual.Compositor;
            CenterOn(visual, ripple);
            TimeSpan delay = TimeSpan.FromSeconds(0.22 * index);
            Vector3KeyFrameAnimation scale = Vector(compositor, Vector3.One, new Vector3(1.85f, 1.85f, 1), TimeSpan.FromSeconds(1.6), MotionCurves.EaseOut);
            scale.DelayTime = delay;
            scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0.75f);
            fade.InsertKeyFrame(1, 0, Easing(compositor, MotionCurves.EaseOut));
            fade.Duration = TimeSpan.FromSeconds(1.6);
            fade.DelayTime = delay;
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", fade);
        }
    }

    // Ring

    private void BuildRing(double side, double line)
    {
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(_ringHost);
        Compositor compositor = hostVisual.Compositor;
        _shapes ??= compositor.CreateShapeVisual();
        _shapes.Size = new Vector2((float)side, (float)side);
        _shapes.Shapes.Clear();
        var center = new Vector2((float)side / 2);
        float radius = (float)Math.Max(1, side / 2 - line * 1.4);
        CompositionEllipseGeometry track = compositor.CreateEllipseGeometry();
        track.Center = center;
        track.Radius = new Vector2(radius);
        CompositionSpriteShape trackShape = compositor.CreateSpriteShape(track);
        trackShape.StrokeBrush = compositor.CreateColorBrush(BreakColors.WithAlpha(Colors.White, 0.1));
        trackShape.StrokeThickness = (float)line;
        _glowArc = compositor.CreateEllipseGeometry();
        _glowArc.Center = center;
        _glowArc.Radius = new Vector2(radius);
        _glowBrush = compositor.CreateColorBrush(BreakColors.WithAlpha(_ringColor, 0.22));
        CompositionSpriteShape glowShape = compositor.CreateSpriteShape(_glowArc);
        glowShape.StrokeBrush = _glowBrush;
        glowShape.StrokeThickness = (float)(line * 2.6);
        _arc = compositor.CreateEllipseGeometry();
        _arc.Center = center;
        _arc.Radius = new Vector2(radius);
        _arcBrush = compositor.CreateColorBrush(_ringColor);
        CompositionSpriteShape arcShape = compositor.CreateSpriteShape(_arc);
        arcShape.StrokeBrush = _arcBrush;
        arcShape.StrokeThickness = (float)line;
        foreach (CompositionSpriteShape shape in new[] { glowShape, arcShape })
        {
            shape.StrokeStartCap = CompositionStrokeCap.Round;
            shape.StrokeEndCap = CompositionStrokeCap.Round;
            // A composition ellipse's outline starts at the top and runs clockwise, as the Mac's ring does (UX reference).
        }
        _shapes.Shapes.Add(trackShape);
        _shapes.Shapes.Add(glowShape);
        _shapes.Shapes.Add(arcShape);
        _shapes.CenterPoint = new Vector3(center, 0);
        ElementCompositionPreview.SetElementChildVisual(_ringHost, _shapes);
        foreach (Ellipse ripple in _ripples)
        {
            ripple.Width = radius * 2;
            ripple.Height = radius * 2;
        }
        _ringGeneration = -1;
        _breathing = false;
        if (_face is { } face)
        {
            ApplyRing(face);
        }
    }

    private void ApplyRing(BreakFace face)
    {
        if (_arc is null || _glowArc is null || _arcBrush is null || _glowBrush is null || _shapes is null)
        {
            return;
        }
        Compositor compositor = _arc.Compositor;
        Color target = face.Phase switch
        {
            BreakPhase.LastMinute or BreakPhase.TimeUp => BreakColors.Warning,
            BreakPhase.Paused => BreakColors.WithAlpha(BreakColors.For(face.Settings.Palette).Accent, 0.55),
            _ => BreakColors.For(face.Settings.Palette).Accent,
        };
        if (target != _arcBrush.Color)
        {
            bool first = !_ringShown;
            _ringColor = target;
            TimeSpan change = TimeSpan.FromSeconds(first || _preview ? 0.01 : _reduceMotion ? 0.2 : 1);
            _arcBrush.StartAnimation("Color", ColorTo(compositor, _arcBrush.Color, target, change));
            _glowBrush.StartAnimation("Color", ColorTo(compositor, _glowBrush.Color, BreakColors.WithAlpha(target, 0.22), change));
        }
        if (face.RingGeneration != _ringGeneration)
        {
            _ringGeneration = face.RingGeneration;
            Restart(face, _preview ? 0 : _reduceMotion ? 0.2 : 0.6);
            _ringShown = true;
        }
        bool breathe = !_reduceMotion && !_preview && face.Phase is BreakPhase.LastMinute or BreakPhase.TimeUp;
        if (breathe != _breathing)
        {
            _breathing = breathe;
            if (breathe)
            {
                Vector3KeyFrameAnimation pulse = Vector(compositor, Vector3.One, new Vector3(1.018f, 1.018f, 1), TimeSpan.FromSeconds(1.1), MotionCurves.EaseInEaseOut);
                pulse.IterationBehavior = AnimationIterationBehavior.Forever;
                pulse.Direction = AnimationDirection.Alternate;
                _shapes.StartAnimation("Scale", pulse);
            }
            else
            {
                _shapes.StopAnimation("Scale");
                _shapes.Scale = Vector3.One;
            }
        }
    }

    /// <summary>The ring's fill now, worked out on the wall clock from the drain or settle that started last.</summary>
    private double DisplayedFraction(DateTimeOffset now)
    {
        if (_drainSeconds <= 0)
        {
            return _shownFraction;
        }
        double elapsed = (now - _drainStart).TotalSeconds;
        return Math.Clamp(_drainFrom * (1 - elapsed / _drainSeconds), 0, 1);
    }

    /// <summary>
    /// Mac restart(_:settle:): from where the ring is now it settles to the face's fill, then, while the break runs, empties
    /// linearly with the time left, all in one key-frame animation of the arc's trim.
    /// </summary>
    private void Restart(BreakFace face, double settle)
    {
        if (_arc is null || _glowArc is null)
        {
            return;
        }
        Compositor compositor = _arc.Compositor;
        DateTimeOffset now = DateTimeOffset.Now;
        double start = _ringShown ? DisplayedFraction(now) : 0;
        double target = Math.Clamp(face.Fraction, 0, 1);
        _arc.StopAnimation("TrimEnd");
        _glowArc.StopAnimation("TrimEnd");
        if (face.SecondsToEmpty is { } seconds && double.IsFinite(seconds) && seconds > settle && target > 0)
        {
            ScalarKeyFrameAnimation drain = compositor.CreateScalarKeyFrameAnimation();
            drain.InsertKeyFrame(0, (float)start);
            if (settle > 0)
            {
                drain.InsertKeyFrame((float)(settle / seconds), (float)(target * (seconds - settle) / seconds), Easing(compositor, MotionCurves.Enter));
            }
            drain.InsertKeyFrame(1, 0, compositor.CreateLinearEasingFunction());
            drain.Duration = TimeSpan.FromSeconds(seconds);
            _arc.StartAnimation("TrimEnd", drain);
            _glowArc.StartAnimation("TrimEnd", drain);
            _drainFrom = target;
            _drainSeconds = seconds;
            _drainStart = now;
            _shownFraction = target;
            return;
        }
        _drainSeconds = 0;
        _shownFraction = target;
        if (settle <= 0)
        {
            _arc.TrimEnd = (float)target;
            _glowArc.TrimEnd = (float)target;
            return;
        }
        ScalarKeyFrameAnimation move = Scalar(compositor, (float)start, (float)target, TimeSpan.FromSeconds(settle), MotionCurves.Enter);
        _arc.StartAnimation("TrimEnd", move);
        _glowArc.StartAnimation("TrimEnd", move);
    }

    // Background

    private void BuildBackdrop()
    {
        if (_look is not { } look || _size.Width <= 0)
        {
            return;
        }
        BreakColors colors = BreakColors.For(look.Palette);
        _backdrop.Children.Clear();
        if (look.Background == BreakBackground.Blur && !_preview)
        {
            // Over the system blur: the palette's dark color at 55%, as the Mac's tint layer.
            _backdrop.Children.Add(new Rectangle { Fill = new SolidColorBrush(BreakColors.WithAlpha(colors.Bottom, 0.55)) });
        }
        else
        {
            var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
            gradient.GradientStops.Add(new GradientStop { Color = colors.Top, Offset = 0 });
            gradient.GradientStops.Add(new GradientStop { Color = colors.Bottom, Offset = 1 });
            _backdrop.Children.Add(new Rectangle { Fill = gradient });
        }
        if (look.Background == BreakBackground.Moving)
        {
            _backdrop.Children.Add(Fields(colors));
        }
        var vignette = new RadialGradientBrush { RadiusX = 0.75, RadiusY = 0.75 };
        vignette.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.45 });
        vignette.GradientStops.Add(new GradientStop { Color = Color.FromArgb(107, 0, 0, 0), Offset = 1 });
        _backdrop.Children.Add(new Rectangle { Fill = vignette });
    }

    /// <summary>Mac Moving colors: three soft color fields that drift and swell on 22 to 37 second loops.</summary>
    private Canvas Fields(BreakColors colors)
    {
        double side = Math.Max(_size.Width, _size.Height) * 0.9;
        (double X, double Y)[] anchors = [(0.18, 0.22), (0.82, 0.72), (0.55, 0.5)];
        (double X, double Y)[] travel = [(0.2, 0.14), (-0.18, -0.16), (-0.12, 0.1)];
        double[] periods = [22, 29, 37];
        var canvas = new Canvas { IsHitTestVisible = false };
        for (var index = 0; index < colors.Fields.Length; index++)
        {
            var fill = new RadialGradientBrush();
            fill.GradientStops.Add(new GradientStop { Color = BreakColors.WithAlpha(colors.Fields[index], 0.62), Offset = 0 });
            fill.GradientStops.Add(new GradientStop { Color = BreakColors.WithAlpha(colors.Fields[index], 0), Offset = 1 });
            var field = new Ellipse { Width = side, Height = side, Fill = fill };
            Canvas.SetLeft(field, _size.Width * anchors[index].X - side / 2);
            Canvas.SetTop(field, _size.Height * anchors[index].Y - side / 2);
            canvas.Children.Add(field);
            if (_reduceMotion)
            {
                continue;
            }
            ElementCompositionPreview.SetIsTranslationEnabled(field, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(field);
            Compositor compositor = visual.Compositor;
            Vector3KeyFrameAnimation drift = Vector(compositor, Vector3.Zero,
                new Vector3((float)(_size.Width * travel[index].X), (float)(_size.Height * travel[index].Y), 0),
                TimeSpan.FromSeconds(periods[index]), MotionCurves.EaseInEaseOut);
            drift.IterationBehavior = AnimationIterationBehavior.Forever;
            drift.Direction = AnimationDirection.Alternate;
            visual.StartAnimation("Translation", drift);
            visual.CenterPoint = new Vector3((float)side / 2, (float)side / 2, 0);
            Vector3KeyFrameAnimation swell = Vector(compositor, Vector3.One, new Vector3(1.16f, 1.16f, 1),
                TimeSpan.FromSeconds(periods[(index + 1) % periods.Length] * 0.8), MotionCurves.EaseInEaseOut);
            swell.IterationBehavior = AnimationIterationBehavior.Forever;
            swell.Direction = AnimationDirection.Alternate;
            visual.StartAnimation("Scale", swell);
        }
        return canvas;
    }

    // Composition helpers

    private static void CenterOn(Visual visual, FrameworkElement element) =>
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);

    private static CubicBezierEasingFunction Easing(Compositor compositor, CubicBezier curve) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2((float)curve.X1, (float)curve.Y1), new Vector2((float)curve.X2, (float)curve.Y2));

    private static ScalarKeyFrameAnimation Scalar(Compositor compositor, float from, float to, TimeSpan duration, CubicBezier curve)
    {
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, to, Easing(compositor, curve));
        animation.Duration = duration;
        return animation;
    }

    private static Vector3KeyFrameAnimation Vector(Compositor compositor, Vector3 from, Vector3 to, TimeSpan duration, CubicBezier curve)
    {
        Vector3KeyFrameAnimation animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, to, Easing(compositor, curve));
        animation.Duration = duration;
        return animation;
    }

    private static SpringVector3NaturalMotionAnimation Spring(Compositor compositor, Vector3 from, Vector3 to, SpringMotion spring)
    {
        SpringVector3NaturalMotionAnimation animation = compositor.CreateSpringVector3Animation();
        animation.InitialValue = from;
        animation.FinalValue = to;
        animation.DampingRatio = (float)spring.DampingFraction;
        animation.Period = TimeSpan.FromSeconds(spring.Response / (2 * Math.PI));
        return animation;
    }

    private static ColorKeyFrameAnimation ColorTo(Compositor compositor, Color from, Color to, TimeSpan duration)
    {
        ColorKeyFrameAnimation animation = compositor.CreateColorKeyFrameAnimation();
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, to, Easing(compositor, MotionCurves.Standard));
        animation.Duration = duration;
        return animation;
    }
}
