using System.Globalization;
using Annotator.App.Library;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Glyphs = Annotator.App.Icons.Glyphs;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Annotator.App.Settings;

/// <summary>
/// The Pointer page of Settings, a port of CursorHighlightControls: "Follow my lead." with the Show halo switch, the halo
/// shapes, outline, colors and custom color, the interactive preview, the size, border, opacity and glow sliders, the
/// click effect and when the highlight shows. Every change goes through <see cref="WorkspaceModel.SetCursorHighlight"/>,
/// which checks and saves it, as the Mac's bindings do. The Settings window hosts it on its Pointer tab
/// (<c>SettingsWindow.SetPage(SettingsTab.Pointer, page)</c>); it follows the workspace while it is loaded.
/// </summary>
internal sealed partial class PointerPage : UserControl
{
    /// <summary>The Mac's eight colors, in its order.</summary>
    private static readonly (string Name, RGBA Color)[] s_colors =
    [
        ("Yellow", new RGBA(1, 0.82, 0)), ("Cyan", new RGBA(0.08, 0.8, 0.84)), ("Blue", Palette.Blue), ("Purple", new RGBA(0.75, 0.36, 0.93)),
        ("Pink", new RGBA(0.98, 0.21, 0.51)), ("Coral", Palette.Coral), ("Green", new RGBA(0.27, 0.76, 0.34)), ("White", Palette.White),
    ];

    private static readonly StrokePattern[] s_patterns = [StrokePattern.Solid, StrokePattern.Dashed, StrokePattern.Dotted];

    private static readonly CursorHighlightVisibility[] s_visibilities =
        [CursorHighlightVisibility.Always, CursorHighlightVisibility.Moving, CursorHighlightVisibility.Clicks];

    private readonly WorkspaceModel _model;
    private readonly List<(CursorHighlightShape Shape, ParityButton Button, Border Surface)> _shapes = [];
    private readonly List<(CursorClickEffect Effect, SegmentButton Segment)> _effects = [];
    private readonly List<(ParitySlider Slider, TextBlock Value, Func<CursorHighlightSettings, double> Read, string Suffix)> _sliders = [];
    private ParityCheckBox _halo = null!;
    private Border _haloTrack = null!;
    private Border _haloKnob = null!;
    private PopUpChoice _outline = null!;
    private PopUpChoice _visibility = null!;
    private ParitySlider _border = null!;
    private ParityButton _custom = null!;
    private FrameworkElement _chevron = null!;
    private Grid _chevronHost = null!;
    private Border _wheelHost = null!;
    private ColorWheelView? _wheel;
    private CursorPreview _preview = null!;
    private TextBlock _problem = null!;
    private CursorHighlightSettings _shown;
    private bool _colorsExpanded;
    private bool _applying;
    private bool _following;
    private Grid _body = null!;

    /// <summary>The shapes column's height with the custom color closed: 249 points, as the Mac lays it out.</summary>
    private const double CollapsedBodyHeight = 16 + 16 + 2 * HaloShapeGrid.TileHeight + HaloShapeGrid.Spacing + 16 + 24 + 16 + 23 + 16 + 16;

    private delegate void SettingsEdit(ref CursorHighlightSettings settings);

    public PointerPage(WorkspaceModel model)
    {
        _model = model;
        _shown = model.Snapshot.CursorHighlight;
        Build();
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
            _preview.Stop();
        };
        // The colors come from the theme when the page is made, as the Tools window's do.
        void Rebuild()
        {
            _preview.Stop();
            Build();
            Apply(_model.Snapshot);
        }
        ActualThemeChanged += (_, _) => Rebuild();
        // A contrast theme or text size turned on later (WIN-UI-07, WIN-A11Y-03), while the page is in Settings.
        Loaded += (_, _) => AppResources.SystemLookChanged += Rebuild;
        Unloaded += (_, _) => AppResources.SystemLookChanged -= Rebuild;
    }

    /// <summary>The preview, for the reference check.</summary>
    internal CursorPreview Preview => _preview;

    /// <summary>Shows the workspace's halo settings and whether the halo is on.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        _applying = true;
        try
        {
            CursorHighlightSettings settings = snapshot.CursorHighlight;
            CursorHighlightSettings previous = _shown;
            _shown = settings;
            bool on = snapshot.CursorHalo;
            _halo.IsOn = on;
            _haloTrack.Background = on ? new SolidColorBrush(LibraryColors.Accent) : PickerColors.Primary(0.11);
            if (ContrastTheme.IsOn)
            {
                // As Settings' switches in a contrast theme: an outlined track with a window-text knob, Highlight while on.
                _haloTrack.Background = ContrastTheme.Brush(on ? ContrastColor.Highlight : ContrastColor.Window);
                _haloTrack.BorderBrush = ContrastTheme.Brush(on ? ContrastColor.Highlight : ContrastColor.WindowText);
                _haloTrack.BorderThickness = new Thickness(1);
                _haloKnob.Background = ContrastTheme.Brush(on ? ContrastColor.HighlightText : ContrastColor.WindowText);
                _haloKnob.BorderThickness = new Thickness(0);
                _haloKnob.Width = 28;
                _haloKnob.Height = 18;
                _haloKnob.Margin = new Thickness(2);
            }
            _haloKnob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            bool disc = settings.Shape == CursorHighlightShape.Disc;
            foreach ((CursorHighlightShape shape, ParityButton button, Border surface) in _shapes)
            {
                bool selected = shape == settings.Shape;
                button.AccessibleSelected = selected;
                surface.Background = selected ? new SolidColorBrush(LibraryColors.Accent) { Opacity = 0.18 } : PickerColors.Primary(0.045);
                if (ContrastTheme.IsOn)
                {
                    // A contrast theme: a button outline, and the chosen shape outlined with Highlight.
                    surface.Background = ContrastTheme.Brush(selected ? ContrastColor.Window : ContrastColor.ButtonFace);
                    surface.BorderBrush = ContrastTheme.Brush(selected ? ContrastColor.Highlight : ContrastColor.ButtonText);
                    surface.BorderThickness = new Thickness(selected ? 2 : 1);
                }
            }
            _outline.Index = Array.IndexOf(s_patterns, settings.ResolvedBorder);
            _outline.IsEnabled = !disc;
            _border.IsEnabled = !disc;
            foreach ((ParitySlider slider, TextBlock value, Func<CursorHighlightSettings, double> read, string suffix) in _sliders)
            {
                double current = read(settings);
                slider.Value = current;
                value.Text = Math.Round(current, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + suffix;
            }
            foreach ((CursorClickEffect effect, SegmentButton segment) in _effects)
            {
                segment.IsChosen = effect == settings.ResolvedClickEffect;
                ((Border)segment.Content).Background = segment.IsChosen ? PickerColors.SegmentSelected : null;
            }
            _visibility.Index = Array.IndexOf(s_visibilities, settings.Visibility);
            _wheel?.Follow(previous.Color, settings.Color);
            _preview.Update(settings);
        }
        finally
        {
            _applying = false;
        }
    }

    private void Change(SettingsEdit edit)
    {
        if (_applying)
        {
            return;
        }
        CursorHighlightSettings settings = _model.Snapshot.CursorHighlight;
        edit(ref settings);
        _model.SetCursorHighlight(settings);
    }

    /// <summary>A swatch or the wheel: the color changes and the opacity stays (Mac chooseColor).</summary>
    private void ChooseColor(RGBA color) => Change((ref CursorHighlightSettings settings) =>
        settings.Color = new RGBA(color.Red, color.Green, color.Blue, settings.Color.Alpha));

    private void Build()
    {
        _shapes.Clear();
        _effects.Clear();
        _sliders.Clear();
        _wheel = null;
        var content = new MacStack { Spacing = 14, Margin = new Thickness(24) };
        content.Children.Add(Header());
        content.Children.Add(Body());
        content.Children.Add(Divider());
        content.Children.Add(Sliders());
        content.Children.Add(Divider());
        content.Children.Add(ClickRow());
        content.Children.Add(VisibilityRow());
        _problem = Line(string.Empty, 12, "AnnotatorErrorText");
        _problem.TextWrapping = TextWrapping.Wrap;
        _problem.Visibility = Visibility.Collapsed;
        content.Children.Add(_problem);
        Background = AppResources.Brush("AnnotatorWindowBackgroundBrush");
        Content = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <summary>HStack(alignment: .firstTextBaseline) { the title and subtitle; Spacer(); Toggle("Show halo").toggleStyle(.switch) }.</summary>
    private Grid Header()
    {
        var header = new Grid { Height = 43 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // The title, the switch, then the subtitle: the Mac reads them in that order.
        TextBlock title = Line("Follow my lead.", 17, "AnnotatorPrimaryText", FontWeights.Bold);
        // SwiftUI's .title2: a 20.5-point line.
        title.LineHeight = 20.5;
        title.MinHeight = 20.5;
        title.Margin = new Thickness(0, 0.5, 0, 0);
        title.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(title);
        TextBlock subtitle = Line("Easy to spot. Easy on the eyes.", 13, "AnnotatorSecondaryText");
        subtitle.VerticalAlignment = VerticalAlignment.Top;
        subtitle.Margin = new Thickness(0, 27, 0, 0);
        var toggle = new MacStack { Orientation = Orientation.Horizontal, Spacing = 7.5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, 0, 0) };
        TextBlock label = Line("Show halo", 13, "AnnotatorPrimaryText", macWidth: 62.5);
        label.VerticalAlignment = VerticalAlignment.Top;
        label.Margin = new Thickness(0, 5, 0, 0);
        toggle.Children.Add(label);
        _haloKnob = new Border
        {
            Width = 32,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(1),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = PickerColors.Primary(0.12),
            BorderThickness = new Thickness(0.5),
        };
        _haloTrack = new Border { Width = 52, Height = 24, CornerRadius = new CornerRadius(12), Child = _haloKnob };
        _halo = new ParityCheckBox
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = 56,
            Height = 26,
            Padding = new Thickness(0),
            Content = _haloTrack,
        };
        AutomationProperties.SetName(_halo, "Show halo");
        AutomationProperties.SetHelpText(_halo, "Show or hide the highlight around your real pointer.");
        ToolTipService.SetToolTip(_halo, "Show or hide the highlight around your real pointer.");
        _halo.Toggled += on =>
        {
            if (!_applying)
            {
                _model.SetCursorHalo(on);
            }
        };
        toggle.Children.Add(_halo);
        Grid.SetColumn(toggle, 1);
        header.Children.Add(toggle);
        header.Children.Add(subtitle);
        return header;
    }

    /// <summary>
    /// HStack(alignment: .top, spacing: 24) { the shape, outline and colors; the preview and its captions }. UI Automation
    /// reads the two columns row by row, as SwiftUI does: the preview after Outline, its first caption before Custom color.
    /// </summary>
    private Grid Body()
    {
        var body = new ReadingOrderGrid { ColumnSpacing = 24, Height = _colorsExpanded ? double.NaN : CollapsedBodyHeight };
        _body = body;
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CursorPreview.Side) });

        var left = new MacStack { Spacing = 16, VerticalAlignment = VerticalAlignment.Top };
        TextBlock heading = Line("Shape", 13, "AnnotatorPrimaryText", FontWeights.SemiBold);
        heading.HorizontalAlignment = HorizontalAlignment.Left;
        left.Children.Add(heading);
        var grid = new HaloShapeGrid { Height = 2 * HaloShapeGrid.TileHeight + HaloShapeGrid.Spacing };
        foreach (CursorHighlightShape shape in CursorHighlightShape.AllCases)
        {
            grid.Children.Add(ShapeTile(shape));
        }
        left.Children.Add(grid);

        var outline = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        outline.Children.Add(Line("Outline", 13, "AnnotatorPrimaryText", macWidth: 43.5));
        _outline = new PopUpChoice("Outline", ["Solid", "Dashed", "Dotted"], 94);
        _outline.Chosen += index => Change((ref CursorHighlightSettings settings) => settings.BorderStyle = s_patterns[index]);
        outline.Children.Add(_outline);
        left.Children.Add(outline);

        var swatches = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach ((string name, RGBA color) in s_colors)
        {
            swatches.Children.Add(Swatch(name, color));
        }
        left.Children.Add(swatches);
        left.Children.Add(CustomColor());
        _wheelHost = new Border { MaxWidth = 270, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        left.Children.Add(_wheelHost);
        if (_colorsExpanded)
        {
            ShowWheel(true);
        }
        body.Children.Add(left);

        var right = new MacStack { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        _preview = new CursorPreview(_shown);
        _preview.Failed += error =>
        {
            _problem.Text = $"The preview could not draw the halo. Your settings still work on the screen. {error.Message}";
            _problem.Visibility = Visibility.Visible;
        };
        right.Children.Add(_preview);
        TextBlock move = Line("Move here. Give it a click.", 12, "AnnotatorPrimaryText", FontWeights.Medium);
        move.HorizontalAlignment = HorizontalAlignment.Center;
        right.Children.Add(move);
        TextBlock note = Line("This preview won't draw on your screen.", 10, "AnnotatorSecondaryText");
        note.HorizontalAlignment = HorizontalAlignment.Center;
        right.Children.Add(note);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        return body;
    }

    /// <summary>A shape button: its icon at 25 points with 12 points around it, on a rounded tile that shows the choice.</summary>
    private ParityButton ShapeTile(CursorHighlightShape shape)
    {
        bool disc = shape == CursorHighlightShape.Disc;
        // HaloShapeIcon: the renderer's path in the icon's frame, stroked inside it 2 points wide, or filled for the disc.
        PathData path = Annotator.Core.Geometry.CursorHighlightGeometry.Path(shape, disc ? new Rect(0, 0, 25, 25) : new Rect(1, 1, 23, 23));
        var icon = new XamlPath
        {
            Data = SampleArt.PathGeometryOf(path, Annotator.Core.Geometry.FillRule.NonZero),
            Width = 25,
            Height = 25,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (disc)
        {
            icon.Fill = PickerColors.Primary(1);
        }
        else
        {
            icon.Stroke = PickerColors.Primary(1);
            icon.StrokeThickness = 2;
            icon.StrokeStartLineCap = PenLineCap.Round;
            icon.StrokeEndLineCap = PenLineCap.Round;
            icon.StrokeLineJoin = PenLineJoin.Round;
        }
        var surface = new Border { CornerRadius = new CornerRadius(8), Child = icon, IsHitTestVisible = false };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            Content = surface,
            AccessibleSelected = false,
        };
        AutomationProperties.SetName(button, shape.Label);
        AutomationProperties.SetHelpText(button, shape.Label);
        ToolTipService.SetToolTip(button, shape.Label);
        button.Click += (_, _) => Change((ref CursorHighlightSettings settings) => settings.Shape = shape);
        _shapes.Add((shape, button, surface));
        return button;
    }

    private ParityButton Swatch(string name, RGBA color)
    {
        var circle = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 23,
            Height = 23,
            Fill = new SolidColorBrush(ToolbarParts.Color(color)),
            Stroke = PickerColors.Primary(0.2),
            StrokeThickness = 1,
        };
        // The halo's colors stay as chosen in a contrast theme (WIN-UI-07).
        OwnColors.Mark(circle);
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = 23,
            Height = 23,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(11.5),
            Content = circle,
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => ChooseColor(color);
        return button;
    }

    /// <summary>Button { colorsExpanded.toggle() } label: Label("Custom color", systemImage: chevron), with the state as its value.</summary>
    private ParityButton CustomColor()
    {
        _chevronHost = new Grid { Width = 13.5, Height = 16, IsHitTestVisible = false };
        _chevron = Glyphs.ForSymbol(_colorsExpanded ? "chevron.down" : "chevron.right", 16);
        _chevronHost.Children.Add(_chevron);
        TextBlock text = LibraryParts.Label("Custom color", 13, "AnnotatorPrimaryText");
        var label = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, IsHitTestVisible = false };
        label.Children.Add(_chevronHost);
        label.Children.Add(text);
        _custom = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Height = 16,
            Padding = new Thickness(0),
            Margin = new Thickness(2.5, 0, 0, 0),
            Content = label,
            AccessibleValue = _colorsExpanded ? "Expanded" : "Collapsed",
        };
        AutomationProperties.SetName(_custom, "Custom color");
        _custom.Click += (_, _) => ShowWheel(!_colorsExpanded);
        return _custom;
    }

    private void ShowWheel(bool expanded)
    {
        _colorsExpanded = expanded;
        _body.Height = expanded ? double.NaN : CollapsedBodyHeight;
        _custom.AccessibleValue = expanded ? "Expanded" : "Collapsed";
        _chevronHost.Children.Clear();
        _chevron = Glyphs.ForSymbol(expanded ? "chevron.down" : "chevron.right", 16);
        _chevronHost.Children.Add(_chevron);
        if (expanded)
        {
            _wheel = new ColorWheelView(_shown.Color, ChooseColor);
            _wheelHost.Child = _wheel.Root;
            _wheelHost.Visibility = Visibility.Visible;
        }
        else
        {
            _wheel = null;
            _wheelHost.Child = null;
            _wheelHost.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>VStack(spacing: 12) { Size, Border, Opacity and Glow }: label 65 points, slider, value 58 points.</summary>
    private MacStack Sliders()
    {
        var rows = new MacStack { Spacing = 12 };
        rows.Children.Add(SliderRow("Size", 16, 160, " pt", static settings => settings.Diameter,
            static (ref CursorHighlightSettings settings, double value) => settings.Diameter = value, out _));
        rows.Children.Add(SliderRow("Border", 1, 12, " pt", static settings => settings.BorderWidth,
            static (ref CursorHighlightSettings settings, double value) => settings.BorderWidth = value, out _border));
        rows.Children.Add(SliderRow("Opacity", 0, 100, "%", static settings => settings.Color.Alpha * 100,
            static (ref CursorHighlightSettings settings, double value) => settings.Color = settings.Color with { Alpha = value / 100 }, out _));
        rows.Children.Add(SliderRow("Glow", 0, 24, " pt", static settings => settings.ResolvedGlow,
            static (ref CursorHighlightSettings settings, double value) => settings.GlowRadius = value, out _));
        return rows;
    }

    private delegate void SliderWrite(ref CursorHighlightSettings settings, double value);

    private Grid SliderRow(string label, double minimum, double maximum, string suffix, Func<CursorHighlightSettings, double> read,
                           SliderWrite write, out ParitySlider slider)
    {
        var row = new Grid { Height = 16, ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        TextBlock name = Line(label, 12, "AnnotatorPrimaryText");
        name.HorizontalAlignment = HorizontalAlignment.Left;
        row.Children.Add(name);
        ParitySlider control = PickerText.Slider(double.NaN, minimum, maximum, 1);
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.Margin = new Thickness(0, -8, 0, -8);
        AutomationProperties.SetName(control, $"Cursor {label.ToLowerInvariant()}");
        control.ValueChanged += (_, args) => Change((ref CursorHighlightSettings settings) => write(ref settings, args.NewValue));
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        TextBlock value = Line(string.Empty, 12, "AnnotatorPrimaryText");
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        _sliders.Add((control, value, read, suffix));
        slider = control;
        return row;
    }

    /// <summary>Picker("On click").pickerStyle(.segmented): Off, Pulse, Ripple and Turn, 62 points each.</summary>
    private MacStack ClickRow()
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Line("On click", 13, "AnnotatorPrimaryText", macWidth: 49));
        var segments = new SegmentGroup("On click") { Width = 248, Height = 24 };
        var track = new Border { CornerRadius = new CornerRadius(6), Background = PickerColors.ControlFill, IsHitTestVisible = false };
        Grid.SetColumnSpan(track, CursorClickEffect.AllCases.Count);
        segments.Children.Add(track);
        int column = 0;
        foreach (CursorClickEffect effect in CursorClickEffect.AllCases)
        {
            segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock title = Line(effect.Label, 13, "AnnotatorPrimaryText");
            title.HorizontalAlignment = HorizontalAlignment.Center;
            AutomationProperties.SetAccessibilityView(title, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var segment = new SegmentButton
            {
                Style = AppResources.Style("AnnotatorPlainButton"),
                CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Content = new Border { CornerRadius = new CornerRadius(5), Child = title },
                Group = segments,
            };
            AutomationProperties.SetName(segment, effect.Label);
            CursorClickEffect chosen = effect;
            segment.Chosen += () => Change((ref CursorHighlightSettings settings) =>
            {
                settings.ClickEffect = chosen;
                settings.ShowClicks = chosen != CursorClickEffect.Off;
            });
            Grid.SetColumn(segment, column++);
            segments.Children.Add(segment);
            segments.Segments.Add(segment);
            _effects.Add((effect, segment));
        }
        row.Children.Add(segments);
        return row;
    }

    /// <summary>Picker("Show highlight") { While turned on; When I move; When I click } with its help.</summary>
    private MacStack VisibilityRow()
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Line("Show highlight", 13, "AnnotatorPrimaryText", macWidth: 89.5));
        _visibility = new PopUpChoice("Show highlight", ["While turned on", "When I move", "When I click"], 143.5);
        const string help = "Clicks animate once, then settle. Reduce Motion uses a quiet, static emphasis.";
        AutomationProperties.SetHelpText(_visibility, help);
        ToolTipService.SetToolTip(_visibility, help);
        _visibility.Chosen += index => Change((ref CursorHighlightSettings settings) => settings.Visibility = s_visibilities[index]);
        row.Children.Add(_visibility);
        return row;
    }

    /// <summary>One line of text at the Mac's line height, which it keeps as its minimum height; aligned left.</summary>
    private static TextBlock Line(string text, double size, string style, Windows.UI.Text.FontWeight? weight = null, double macWidth = 0)
    {
        TextBlock block = LibraryParts.Text(text, size, style, weight, macWidth);
        block.MinHeight = block.LineHeight;
        block.HorizontalAlignment = HorizontalAlignment.Left;
        return block;
    }

    private static Rectangle Divider() => new() { Height = 1, Fill = AppResources.Brush("AnnotatorSeparatorBrush"), HorizontalAlignment = HorizontalAlignment.Stretch };
}
