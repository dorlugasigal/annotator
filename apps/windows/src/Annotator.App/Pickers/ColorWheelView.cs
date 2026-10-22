using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Workspace.ColorPicking;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using CorePoint = Annotator.Core.Point;

namespace Annotator.App.Pickers;

/// <summary>
/// Port of InlineColorWheel: a 176-point hue and saturation wheel with 6 points around it, the brightness as a black
/// shade over it and a ring knob, then Hue, Saturation and Brightness sliders with their values and a caption. Dragging
/// on the wheel or moving a slider chooses a solid color.
/// </summary>
internal sealed class ColorWheelView
{
    private const double Diameter = 176;
    private const double Padding = 6;

    private readonly Action<RGBA> _choose;
    private readonly Image _disk = new() { Width = Diameter, Height = Diameter, Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Ellipse _shade = new() { Width = Diameter, Height = Diameter, Fill = new SolidColorBrush(Microsoft.UI.Colors.Black), IsHitTestVisible = false };
    private readonly Canvas _knob = new() { Width = 16, Height = 16, IsHitTestVisible = false };
    private readonly Grid _wheel = new() { Width = Diameter, Height = Diameter, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Channel[] _channels;
    private WheelColor _value;
    private int _diskPixels;
    private bool _dragging;
    private bool _updating;

    public ColorWheelView(RGBA color, Action<RGBA> choose)
    {
        _choose = choose;
        _value = new WheelColor(color);
        _knob.Children.Add(new Ellipse { Width = 16, Height = 16, Stroke = new SolidColorBrush(Color.FromArgb(191, 0, 0, 0)), StrokeThickness = 2 });
        var inner = new Ellipse { Width = 12, Height = 12, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 2 };
        Canvas.SetLeft(inner, 2);
        Canvas.SetTop(inner, 2);
        _knob.Children.Add(inner);
        _wheel.Children.Add(_disk);
        _wheel.Children.Add(_shade);
        var knobLayer = new Canvas { Width = Diameter, Height = Diameter, IsHitTestVisible = false };
        knobLayer.Children.Add(_knob);
        _wheel.Children.Add(knobLayer);
        _wheel.PointerPressed += OnPressed;
        _wheel.PointerMoved += OnMoved;
        _wheel.PointerReleased += OnReleased;
        _wheel.PointerCaptureLost += (_, _) => _dragging = false;
        _wheel.Loaded += (_, _) => DrawDisk();

        // The wheel shows colors, which a contrast theme leaves as they are (WIN-UI-07).
        var frame = OwnColors.Mark(new AccessibleGroup("Hue and saturation wheel")
        {
            Width = Diameter + 2 * Padding,
            Height = Diameter + 2 * Padding,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        AutomationProperties.SetHelpText(frame, "Use the hue, saturation and brightness sliders to choose a color with the keyboard.");
        // The Mac's accessibilityElement(children: .ignore): the wheel is one element; the sliders give keyboard access.
        AutomationProperties.SetAccessibilityView(_wheel, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _wheel.Margin = new Thickness(Padding);
        frame.Children.Add(_wheel);

        _channels =
        [
            new Channel(this, "Hue", 360, "°", static (in WheelColor value) => value.Hue, static (ref WheelColor value, double hue) => value.Hue = hue),
            new Channel(this, "Saturation", 100, "%", static (in WheelColor value) => value.Saturation, static (ref WheelColor value, double saturation) => value.Saturation = saturation),
            new Channel(this, "Brightness", 100, "%", static (in WheelColor value) => value.Brightness, static (ref WheelColor value, double brightness) => value.Brightness = brightness),
        ];
        Root = new MacStack { Spacing = 10 };
        Root.Children.Add(frame);
        foreach (Channel channel in _channels)
        {
            Root.Children.Add(channel.Row);
        }
        TextBlock caption = PickerText.Caption("Use the wheel for a solid color.", secondary: true);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        Root.Children.Add(caption);
        Show();
    }

    public MacStack Root { get; }

    /// <summary>Mac onChange(of: color): follows a new color from the workspace when its red, green or blue changed.</summary>
    public void Follow(RGBA previous, RGBA current)
    {
        if (previous.Red == current.Red && previous.Green == current.Green && previous.Blue == current.Blue)
        {
            return;
        }
        _value.Follow(current);
        Show();
    }

    private delegate double Read(in WheelColor value);

    private delegate void Write(ref WheelColor value, double component);

    private void Choose()
    {
        Show();
        _choose(_value.Color);
    }

    private void Show()
    {
        _shade.Opacity = 1 - _value.Brightness;
        double radius = Diameter / 2;
        double angle = _value.Hue * 2 * Math.PI;
        Canvas.SetLeft(_knob, radius + Math.Cos(angle) * _value.Saturation * radius - 8);
        Canvas.SetTop(_knob, radius + Math.Sin(angle) * _value.Saturation * radius - 8);
        _updating = true;
        try
        {
            foreach (Channel channel in _channels)
            {
                channel.Show(_value);
            }
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>
    /// The hue and saturation disk in device pixels: the Mac's angular hue gradient with a white radial gradient over it,
    /// which is exactly the color of each hue and saturation at full brightness. Drawn once per display scale.
    /// </summary>
    private void DrawDisk()
    {
        // Sharp in a popover that grows with the text size (WIN-A11Y-03).
        double scale = Shell.ZoomPanel.ScaleOf(_wheel);
        int size = Math.Max(1, (int)Math.Round(Diameter * scale));
        if (size == _diskPixels)
        {
            return;
        }
        _diskPixels = size;
        var pixels = new byte[size * size * 4];
        double center = size / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x + 0.5 - center, dy = y + 0.5 - center;
                double distance = double.Hypot(dx, dy);
                double coverage = Math.Clamp(center - distance + 0.5, 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }
                var point = new WheelColor { Hue = 0, Saturation = 0, Brightness = 1 };
                point.Move(new CorePoint(x + 0.5, y + 0.5), size, size);
                RGBA color = point.Color;
                int index = (y * size + x) * 4;
                pixels[index] = ToolbarParts.Channel(color.Blue * coverage);
                pixels[index + 1] = ToolbarParts.Channel(color.Green * coverage);
                pixels[index + 2] = ToolbarParts.Channel(color.Red * coverage);
                pixels[index + 3] = ToolbarParts.Channel(coverage);
            }
        }
        var bitmap = new WriteableBitmap(size, size);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels);
        }
        bitmap.Invalidate();
        _disk.Source = bitmap;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        Windows.Foundation.Point point = args.GetCurrentPoint(_wheel).Position;
        // The Mac's contentShape(Circle()): only the disk starts a drag.
        if (double.Hypot(point.X - Diameter / 2, point.Y - Diameter / 2) > Diameter / 2 || !_wheel.CapturePointer(args.Pointer))
        {
            return;
        }
        _dragging = true;
        args.Handled = true;
        MoveTo(point);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragging)
        {
            args.Handled = true;
            MoveTo(args.GetCurrentPoint(_wheel).Position);
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_dragging)
        {
            _dragging = false;
            _wheel.ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        }
    }

    private void MoveTo(Windows.Foundation.Point point)
    {
        _value.Move(new CorePoint(point.X, point.Y), Diameter, Diameter);
        Choose();
    }

    /// <summary>One channel row: the name in 66 points, a slider from 0 to the scale in steps of 1, and the value in 36 points.</summary>
    private sealed class Channel
    {
        private readonly ColorWheelView _owner;
        private readonly double _scale;
        private readonly string _suffix;
        private readonly Read _read;
        private readonly Write _write;
        private readonly ParitySlider _slider;
        private readonly TextBlock _number;

        public Channel(ColorWheelView owner, string name, double scale, string suffix, Read read, Write write)
        {
            _owner = owner;
            _scale = scale;
            _suffix = suffix;
            _read = read;
            _write = write;
            Row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
            TextBlock label = PickerText.Caption(name, secondary: false);
            label.Width = 66;
            Row.Children.Add(label);
            _slider = PickerText.Slider(ColorPopover.ContentWidth - 66 - 36 - 16, 0, scale, 1);
            AutomationProperties.SetName(_slider, "Color " + name.ToLowerInvariant());
            _slider.ValueChanged += (_, args) =>
            {
                if (!_owner._updating)
                {
                    _write(ref _owner._value, args.NewValue / _scale);
                    _owner.Choose();
                }
            };
            Row.Children.Add(PickerText.SliderBand(_slider));
            _number = PickerText.Caption(string.Empty, secondary: false);
            _number.Width = 36;
            _number.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right;
            Row.Children.Add(_number);
        }

        public MacStack Row { get; }

        public void Show(in WheelColor value)
        {
            double shown = _read(value) * _scale;
            _slider.Value = shown;
            _number.Text = ((int)Math.Round(shown, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + _suffix;
        }
    }
}
