using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.ColorPicking;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Glyphs = Annotator.App.Icons.Glyphs;
using CoreStyle = Annotator.Core.Style;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Toolbar;

/// <summary>Small parts of the toolbar, each a port of a Mac SwiftUI view with its sizes in points.</summary>
internal static class ToolbarParts
{
    /// <summary>
    /// A Mac SF Symbol's drawn bounds at the toolbar's sizes, from the UX reference frames: plain icon buttons report
    /// these bounds to accessibility on the Mac, and the Lucide icon (D-6) is centered in them at <c>draw</c> size.
    /// </summary>
    public static (double Width, double Height, double Draw) SymbolBox(string symbol, bool minimal = false) => symbol switch
    {
        "arrow.uturn.backward" or "arrow.uturn.forward" => (12, 12, 15),
        "trash" => (12.5, 14.5, 16),
        "slider.horizontal.3" => (13, 11, 15),
        "gearshape" => (14, 14, 16),
        "xmark" => (10.5, 10.5, 14),
        "plus" => (10.5, 10.5, 18),
        "minus" => (10.5, 1.5, 18),
        "link" => (10.5, 10.5, 11),
        "desktopcomputer" or "rectangle" or "rectangle.fill" => minimal ? (17.5, 15, 17) : (22.5, 17.5, 20),
        _ => (16, 16, 16),
    };

    /// <summary>
    /// A fixed-size box with a Lucide icon centered in it. The box is what accessibility reports; the icon may draw
    /// slightly past it, as SF Symbols do past their layout bounds.
    /// </summary>
    public static FrameworkElement GlyphBox(string symbol, double width, double height, double draw, string style = "AnnotatorGlyph")
    {
        FrameworkElement icon = Glyphs.ForSymbol(symbol, draw, style);
        Canvas.SetLeft(icon, (width - draw) / 2);
        Canvas.SetTop(icon, (height - draw) / 2);
        var box = new Canvas { Width = width, Height = height, IsHitTestVisible = false };
        box.Children.Add(icon);
        return box;
    }

    public static FrameworkElement SymbolGlyph(string symbol, bool minimal = false, string style = "AnnotatorGlyph")
    {
        (double width, double height, double draw) = SymbolBox(symbol, minimal);
        return GlyphBox(symbol, width, height, draw, style);
    }

    /// <summary>Text that is part of a control's look, hidden from UI Automation like the Mac's button labels.</summary>
    public static TextBlock Label(string text, double size, string style, Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock label = Text(text, size, style, weight);
        label.IsHitTestVisible = false;
        label.VerticalAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        return label;
    }

    /// <summary>
    /// Text that UI Automation reads as static text (the Mac's AXStaticText), one line of the Mac's height for its size:
    /// Segoe UI's lines are taller than SF Pro's, which would move the controls around it. The toolbar's fixed words
    /// take at least the Mac's width, so the controls after them keep the Mac's positions (D-4).
    /// </summary>
    public static TextBlock Text(string text, double size, string style, Windows.UI.Text.FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Style = AppResources.Style(style),
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
        LineHeight = LineHeight(size),
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        MinWidth = MacWidth(text),
    };

    /// <summary>
    /// The width the Mac gives the toolbar's fixed words in SF Pro (reference/ux-reference/macos/toolbar frames): tool titles
    /// at 11 points semibold, captions and units at 10 points, swatch labels at 8 points medium. Zero for other text.
    /// </summary>
    public static double MacWidth(string text) => text switch
    {
        "Arrow" => 33,
        "Database" => 52,
        "Diamond" => 48.5,
        "Ellipse" => 36,
        "Eraser" => 35.5,
        "Highlighter" => 61.5,
        "Line" => 23,
        "Paste image" => 67,
        "Pen" => 20.5,
        "Pixel ruler" => 55,
        "Rectangle" => 55,
        "Select" => 34.5,
        "Text" => 23.5,
        "Width" => 28.5,
        "Size" => 20.5,
        "Text size" => 42.5,
        "Opacity" => 37.5,
        "Blur" => 19.5,
        "Strength" => 42,
        "Pixel size" => 45,
        "pt" => 10,
        "%" => 9.5,
        "Select any object to move, edit or delete it." => 207.5,
        "Stroke" => 26.5,
        "Fill" => 12,
        _ => 0,
    };

    /// <summary>The Mac's one-line text height for a system font size, from the UX reference frames.</summary>
    public static double LineHeight(double size) => size switch
    {
        <= 8 => 10,
        <= 9 => 11,
        <= 10 => 13,
        <= 11 => 14,
        <= 12 => 15,
        <= 13 => 16,
        _ => Math.Ceiling(size * 1.2),
    };

    /// <summary>SwiftUI's Divider: a 1-point line in the separator color.</summary>
    public static Microsoft.UI.Xaml.Shapes.Rectangle Divider(double? width = null, double? height = null) => new()
    {
        Width = width ?? double.NaN,
        Height = height ?? double.NaN,
        Style = AppResources.Style("AnnotatorDivider"),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>A borderless toolbar button of a fixed size, with Mac-like hover and press fills.</summary>
    public static ParityButton Button(double width, double height, UIElement content, string name, double cornerRadius = 7)
    {
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = width,
            Height = height,
            Content = content,
            CornerRadius = new CornerRadius(cornerRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    public static void Help(FrameworkElement element, string? help, string? tooltip = null)
    {
        AutomationProperties.SetHelpText(element, help ?? string.Empty);
        SetToolTip(element, tooltip ?? help);
    }

    /// <summary>
    /// A tooltip that opens beside the toolbar, never over its controls: below a horizontal toolbar (above when there is no
    /// room) and beside a vertical one. WinUI shows a tooltip in a window of its own, and over the toolbar that window took
    /// the clicks meant for the controls under it, often the quick row's Stroke and Fill wells below a tool.
    /// </summary>
    public static void SetToolTip(FrameworkElement element, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            ToolTipService.SetToolTip(element, null);
            return;
        }
        if (ToolTipService.GetToolTip(element) is ToolTip existing)
        {
            existing.Content = text;
            return;
        }
        var tip = new ToolTip { Content = text };
        element.PointerEntered += (_, _) => PlaceBeside(element, tip);
        element.GotFocus += (_, _) => PlaceBeside(element, tip);
        ToolTipService.SetToolTip(element, tip);
    }

    /// <summary>Places the tooltip along the element's own band across the whole toolbar, outside the toolbar.</summary>
    private static void PlaceBeside(FrameworkElement element, ToolTip tip)
    {
        if (element.XamlRoot?.Content is not FrameworkElement root || !element.IsLoaded)
        {
            return;
        }
        Windows.Foundation.Point origin = element.TransformToVisual(root).TransformPoint(default);
        bool horizontal = root.ActualWidth >= root.ActualHeight;
        tip.Placement = horizontal ? PlacementMode.Bottom : PlacementMode.Left;
        tip.PlacementRect = horizontal
            ? new Windows.Foundation.Rect(0, -origin.Y, element.ActualWidth, root.ActualHeight)
            : new Windows.Foundation.Rect(-origin.X, 0, root.ActualWidth, element.ActualHeight);
    }

    public static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);

    public static WinColor Color(RGBA color, double opacity = 1) =>
        WinColor.FromArgb(Channel(color.Alpha * opacity), Channel(color.Red), Channel(color.Green), Channel(color.Blue));

    private static ImageBrush? s_transparencyGrid;
    private static bool s_transparencyGridDark;

    /// <summary>
    /// The Mac's TransparencyGrid: white with 6-point squares of systemGray at 40% (#D2D2D4 light, #D6D6D8 dark), the first
    /// square shaded, drawn at 4x so it stays sharp. Seven squares cover the 28- and 36-point wells, as the Mac fills the
    /// whole swatch with squares.
    /// </summary>
    public static ImageBrush TransparencyGrid()
    {
        bool dark = PickerColors.Dark;
        if (s_transparencyGrid is { } cached && s_transparencyGridDark == dark)
        {
            return cached;
        }
        const int cell = 24, cells = 7, size = cell * cells;
        WinColor gray = PickerColors.GridGray;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool shaded = ((x / cell) + (y / cell)) % 2 == 0;
                int index = (y * size + x) * 4;
                pixels[index] = shaded ? gray.B : (byte)0xFF;
                pixels[index + 1] = shaded ? gray.G : (byte)0xFF;
                pixels[index + 2] = shaded ? gray.R : (byte)0xFF;
                pixels[index + 3] = 0xFF;
            }
        }
        var bitmap = new WriteableBitmap(size, size);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels);
        }
        bitmap.Invalidate();
        s_transparencyGrid = new ImageBrush
        {
            ImageSource = bitmap,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Transform = new ScaleTransform { ScaleX = 0.25, ScaleY = 0.25 },
        };
        s_transparencyGridDark = dark;
        return s_transparencyGrid;
    }
}

/// <summary>
/// Port of InspectorColorPicker: a swatch over the transparency grid, with the chevron, that opens the color popover.
/// Compact (quick properties): an optional 8-point "Stroke" or "Fill" label beside a 28-point swatch. Full properties: an
/// 11-point "Stroke color" or "Fill" label above a 36-point swatch. Accessibility value: "#RRGGBBAA" or "Transparent".
/// </summary>
internal sealed class ColorWell
{
    /// <summary>The chevron glyph's size in points.</summary>
    private const double ChevronSize = 10;

    private readonly bool _fill;
    private readonly ParityButton _button;
    private readonly Border _grid = new() { CornerRadius = new CornerRadius(7), Background = ToolbarParts.TransparencyGrid() };
    private readonly Border _paint = new() { CornerRadius = new CornerRadius(7) };
    private readonly ContentControl _chevron = new() { IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private string _shown = string.Empty;

    /// <param name="open">Opens the color popover for this well's paint, next to the well's own swatch button.</param>
    public ColorWell(bool fill, bool showLabel, Action<ColorPickerRequest, FrameworkElement> open, bool compact = true)
    {
        _fill = fill;
        double side = compact ? 28 : 36;
        // The swatch shows the drawing's color, which a contrast theme leaves as it is (WIN-UI-07).
        var grid = OwnColors.Mark(new Grid { Width = side, Height = side });
        grid.Children.Add(_grid);
        grid.Children.Add(_paint);
        // The Mac's chevron.down at 7 points, semibold, 4 points in from the corner: its ink is 6 by 3.5 points, centered
        // 8.5 points in from the swatch's right edge and 6.25 from its bottom, in both sizes.
        _chevron.Margin = new Thickness(0, 0, 8.5 - ChevronSize / 2, 6.25 - ChevronSize / 2);
        grid.Children.Add(_chevron);
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            BorderBrush = AppResources.Brush("AnnotatorSwatchStrokeBrush"),
            IsHitTestVisible = false,
        });
        _button = ToolbarParts.Button(side, side, grid, fill ? "Fill color" : "Stroke color");
        _button.Click += (_, _) => open(_fill ? ColorPickerRequest.FillColor : ColorPickerRequest.Stroke, _button);
        _button.IsEnabledChanged += (_, _) => grid.Opacity = _button.IsEnabled ? 1 : 0.5;
        if (!compact)
        {
            Root = new MacStack { Spacing = 6, VerticalAlignment = VerticalAlignment.Top };
            if (showLabel)
            {
                TextBlock label = PropertyMetrics.Text(fill ? "Fill" : "Stroke color", 11, "AnnotatorSecondaryText", FontWeights.Medium);
                label.HorizontalAlignment = HorizontalAlignment.Left;
                Root.Children.Add(label);
            }
            _button.HorizontalAlignment = HorizontalAlignment.Left;
            Root.Children.Add(_button);
            return;
        }
        Root = new MacStack { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        if (showLabel)
        {
            Root.Children.Add(ToolbarParts.Text(fill ? "Fill" : "Stroke", 8, "AnnotatorSecondaryText", FontWeights.Medium));
        }
        Root.Children.Add(_button);
    }

    public MacStack Root { get; }

    public void Update(WorkspaceSnapshot snapshot)
    {
        // The squares follow the theme, as the Mac's systemGray does.
        ImageBrush squares = ToolbarParts.TransparencyGrid();
        if (!ReferenceEquals(_grid.Background, squares))
        {
            _grid.Background = squares;
        }
        CoreStyle style = snapshot.Style;
        RGBA? color = _fill ? style.Fill : style.Color;
        ColorGradient? gradient = _fill ? style.FillGradient : style.StrokeGradient;
        string shown = $"{color}|{gradient}|{style.Opacity}";
        if (shown == _shown)
        {
            return;
        }
        _shown = shown;
        _paint.Background = color is { } paint ? Paint(paint, gradient, style.Opacity) : null;
        // Lucide's chevron at 10 points with a 1-point stroke has the SF Symbol's ink size and weight.
        _chevron.Content = Glyphs.ForSymbol("chevron.down", ChevronSize, color is null ? "AnnotatorGlyphSecondary" : "AnnotatorGlyphOnColor",
            strokeWidth: 24 / ChevronSize);
        _button.AccessibleValue = color is { } hex ? $"#{PaletteHex.Format(hex)}" : "Transparent";
        ToolbarParts.Help(_button, _fill && color is null ? "Transparent fill" : _fill ? "Fill color" : "Stroke color");
    }

    /// <summary>Port of PaintSwatch: the color, or its gradient across the swatch, at the object's opacity.</summary>
    private static Brush Paint(RGBA color, ColorGradient? gradient, double opacity)
    {
        if (gradient is not { } blend)
        {
            return new SolidColorBrush(ToolbarParts.Color(color, opacity));
        }
        double angle = blend.Angle * Math.PI / 180;
        double dx = Math.Cos(angle), dy = Math.Sin(angle);
        double length = (Math.Abs(dx) + Math.Abs(dy)) / 2;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.5 - dx * length, 0.5 - dy * length),
            EndPoint = new Windows.Foundation.Point(0.5 + dx * length, 0.5 + dy * length),
            Opacity = color.Alpha * opacity,
        };
        int count = blend.Colors.Count;
        for (int index = 0; index < count; index++)
        {
            brush.GradientStops.Add(new GradientStop { Color = ToolbarParts.Color(blend.Colors[index]), Offset = count == 1 ? 0 : index / (double)(count - 1) });
        }
        return brush;
    }
}

/// <summary>
/// Port of StyleValueControl: an optional caption, an optional slider, a 48-point "Precise" field and the unit. A
/// slider drag and a field edit are one Undo step each (Mac beginStyleEdit and finishStyleEdit). The full properties
/// panel uses the small slider (<c>controlSize(.small)</c>): a 14-point band with an 18 by 14 knob. Without the field
/// (Mac <c>showsField: false</c>), the slider shows its value beside it, such as "3 pt".
/// </summary>
internal sealed class ValueControl
{
    private readonly WorkspaceModel _model;
    private readonly StyleParameter _parameter;
    private readonly ParitySlider? _slider;
    private readonly TextBox _field;
    private readonly ContentControl _warning = new() { IsTabStop = false, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    /// <summary>The value beside a slider without a field, or null with the field.</summary>
    private readonly TextBlock? _readout;
    private readonly Action<TextBox> _beginTyping;
    private readonly Action _endTyping;
    private string _original = string.Empty;
    private double _shown = double.NaN;
    private StyleEditTarget? _target;
    private bool _editing;
    private bool _sliding;
    private bool _updating;
    private bool _settingText;

    public ValueControl(WorkspaceModel model, StyleParameter parameter, Tool tool, double sliderWidth, bool showLabel,
                        Action<TextBox> beginTyping, Action endTyping, bool small = false, bool showsField = true)
    {
        _model = model;
        _parameter = parameter;
        _beginTyping = beginTyping;
        _endTyping = endTyping;
        string title = parameter.Title(tool);
        Root = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (showLabel)
        {
            Root.Children.Add(PropertyMetrics.Text(title, 10, "AnnotatorSecondaryText"));
        }
        if (sliderWidth > 0)
        {
            (double sliderLow, double sliderHigh) = parameter.SliderRange(tool);
            _slider = Slider(sliderWidth, sliderLow, sliderHigh, small, out FrameworkElement band);
            AutomationProperties.SetName(_slider, title);
            ToolbarParts.SetToolTip(_slider, parameter.SliderHelp(tool));
            _slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => BeginSlide()), handledEventsToo: true);
            _slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndSlide()), handledEventsToo: true);
            _slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndSlide()), handledEventsToo: true);
            _slider.ValueChanged += (_, args) =>
            {
                if (!_updating)
                {
                    double value = args.NewValue;
                    _model.ApplyStyle((ref CoreStyle style) => _parameter.Apply(value, ref style));
                }
            };
            Root.Children.Add(band);
        }
        _field = new TextBox
        {
            Width = 48,
            Height = 21,
            Style = AppResources.Style("AnnotatorValueField"),
            PlaceholderText = title,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_field, $"Precise {title.ToLowerInvariant()}");
        ToolbarParts.Help(_field, parameter.FieldHelp(tool));
        _field.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _beginTyping(_field)), handledEventsToo: true);
        _field.GotFocus += (_, _) => BeginEdit();
        // Typing after a discarded draft starts a new draft for what the toolbar edits now.
        _field.BeforeTextChanging += (_, _) =>
        {
            if (!_settingText && !_editing && _field.FocusState != FocusState.Unfocused)
            {
                BeginEdit();
            }
        };
        _field.LostFocus += (_, _) => Finish(commit: true);
        _field.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                if (_editing && Commit())
                {
                    Finish(commit: false);
                }
            }
            else if (args.Key == Windows.System.VirtualKey.Escape && _editing)
            {
                // Escape ends an edit; without one it reaches the toolbar, which gives the keyboard back (WIN-A11Y-04).
                args.Handled = true;
                SetText(_original);
                ShowValidation(null);
                Finish(commit: false);
            }
        };
        if (!showsField)
        {
            // Mac: the caption font's monospaced digits (Segoe UI's are tabular already) in the secondary color, at least
            // 34 points wide, hidden from UI Automation (the slider reads its value).
            _readout = ToolbarParts.Text(string.Empty, 10, "AnnotatorSecondaryText");
            _readout.MinWidth = 34;
            _readout.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAccessibilityView(_readout, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Root.Children.Add(_readout);
            return;
        }
        Root.Children.Add(_field);
        if (parameter.Unit is { Length: > 0 } unit)
        {
            Root.Children.Add(ToolbarParts.Text(unit, 10, "AnnotatorSecondaryText"));
        }
        Root.Children.Add(_warning);
    }

    public MacStack Root { get; }

    /// <summary>
    /// A standard slider in a band of the Mac slider's height: 16 points with a 20 by 16 knob, or 14 points with an 18 by
    /// 14 knob for the small size. The standard slider needs 32 points, so it overhangs the band above and below, and UI
    /// Automation reports the band.
    /// </summary>
    public static ParitySlider Slider(double width, double minimum, double maximum, bool small, out FrameworkElement band)
    {
        double height = small ? 14 : 16;
        var slider = new ParitySlider
        {
            Width = width,
            Height = 32,
            Band = height,
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = 0.01,
            SmallChange = maximum - minimum <= 2 ? 0.05 : 1,
            IsThumbToolTipEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            Style = AppResources.Style("AnnotatorSlider"),
        };
        if (small)
        {
            slider.Resources["SliderHorizontalThumbWidth"] = 18.0;
            slider.Resources["SliderHorizontalThumbHeight"] = 14.0;
        }
        Canvas.SetTop(slider, -(32 - height) / 2);
        var canvas = new Canvas { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(slider);
        band = canvas;
        return slider;
    }

    public void Update(WorkspaceSnapshot snapshot)
    {
        double current = _parameter.Value(snapshot.Style);
        if (_slider is { } slider)
        {
            _updating = true;
            slider.Value = Math.Clamp(current, slider.Minimum, slider.Maximum);
            _updating = false;
        }
        if (_target is { } target && !target.Matches(snapshot))
        {
            // Mac onChange(of: target): a draft, a refused draft or a drag never carries over to another selection,
            // tool or display.
            bool wasEditing = _editing || _sliding;
            _target = null;
            _editing = false;
            _sliding = false;
            _shown = double.NaN;
            if (wasEditing)
            {
                // After this publish, because ending the Undo step publishes again.
                _ = _field.DispatcherQueue.TryEnqueue(() =>
                {
                    _model.FinishStyleEdit();
                    _endTyping();
                });
            }
        }
        // Mac onChange(of: current): a new value replaces the draft, unless the person is typing; an unchanged value
        // leaves a refused draft in place for correction.
        if (_readout is { } readout)
        {
            string formatted = StyleParameter.Format(current);
            string text = _parameter.Unit is { Length: > 0 } unit ? $"{formatted} {unit}" : formatted;
            if (readout.Text != text)
            {
                readout.Text = text;
            }
        }
        if (!_editing && current != _shown)
        {
            _shown = current;
            SetText(StyleParameter.Format(current));
            ShowValidation(null);
            if (!_sliding)
            {
                _target = null;
            }
        }
    }

    /// <summary>Mac onEditingChanged(true): the draft belongs to what the toolbar edits now.</summary>
    private void BeginEdit()
    {
        if (_editing)
        {
            return;
        }
        _editing = true;
        _target = StyleEditTarget.Of(_model.Snapshot);
        _original = _field.Text;
        _model.BeginStyleEdit();
    }

    private bool Commit()
    {
        if (_target is { } target && !target.Matches(_model.Snapshot))
        {
            // Mac commit(): a draft typed for another selection, tool or display is never applied.
            return true;
        }
        if (_field.Text == _original)
        {
            return true;
        }
        if (_parameter.Parse(_field.Text) is not { } value)
        {
            ShowValidation(_parameter.ValidationMessage);
            return false;
        }
        if (value != _parameter.Value(_model.Snapshot.Style))
        {
            _model.ApplyStyle((ref CoreStyle style) => _parameter.Apply(value, ref style));
        }
        ShowValidation(null);
        SetText(StyleParameter.Format(value));
        _original = _field.Text;
        return true;
    }

    private void Finish(bool commit)
    {
        if (!_editing)
        {
            return;
        }
        // A refused draft stays for correction, tied to its target.
        bool refused = commit && !Commit();
        _editing = false;
        if (!refused)
        {
            _target = null;
        }
        _model.FinishStyleEdit();
        _endTyping();
    }

    private void SetText(string text)
    {
        _settingText = true;
        _field.Text = text;
        _settingText = false;
    }

    private void ShowValidation(string? message)
    {
        if (message is null)
        {
            _warning.Content = null;
            _warning.Visibility = Visibility.Collapsed;
            return;
        }
        var icon = new AccessibleImage(message, null, null);
        icon.Children.Add(ToolbarParts.GlyphBox("exclamationmark.circle.fill", 14, 14, 14, "AnnotatorGlyphError"));
        ToolbarParts.SetToolTip(icon, message);
        _warning.Content = icon;
        _warning.Visibility = Visibility.Visible;
    }

    private void BeginSlide()
    {
        if (!_sliding)
        {
            // Mac slider onEditingChanged(true): a draft for the same target is committed first and typing ends.
            Finish(commit: true);
            _sliding = true;
            _target = StyleEditTarget.Of(_model.Snapshot);
            _model.FinishStyleEdit();
            _model.BeginStyleEdit();
        }
    }

    private void EndSlide()
    {
        if (_sliding)
        {
            _sliding = false;
            _target = null;
            _model.FinishStyleEdit();
        }
    }
}

/// <summary>Port of SmartDrawButton: a toggle with the wand, On or Off; 32 points in quick properties, 36 in full properties.</summary>
internal sealed class SmartDrawToggle
{
    private readonly ToggleButton _button;
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(9) };
    private bool? _shown;

    public SmartDrawToggle(WorkspaceModel model, double size = 32)
    {
        _frame.Child = ToolbarParts.GlyphBox("wand.and.stars", size, size, 18);
        _button = new ToggleButton
        {
            Style = AppResources.Style("AnnotatorToolButton"),
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(9),
            Content = _frame,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(_button, "Smart Draw");
        AutomationProperties.SetAutomationId(_button, "wand.and.stars");
        _button.Click += (_, _) => model.SetSmartDraw(!model.Snapshot.SmartDraw);
    }

    public ToggleButton Root => _button;

    public void Update(WorkspaceSnapshot snapshot)
    {
        bool on = snapshot.SmartDraw;
        _button.IsChecked = on;
        if (_shown == on)
        {
            return;
        }
        _shown = on;
        _frame.Background = AppResources.Brush(on ? "AnnotatorControlSelectedFillBrush" : "AnnotatorControlFillBrush");
        _frame.BorderBrush = AppResources.Brush(on ? "AnnotatorAccentBrush" : "AnnotatorControlStrokeBrush");
        _frame.BorderThickness = new Thickness(on ? 2 : 1);
        ToolbarParts.Help(_button, on ? "Smart Draw is on. Click to turn it off." : "Turn on Smart Draw to turn pen strokes into rectangles and ellipses.");
    }
}
