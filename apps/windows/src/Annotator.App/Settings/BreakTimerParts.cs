using Annotator.App.Shell;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Settings;

/// <summary>
/// A Form's text field (SwiftUI TextField in a grouped Form): no box, the text in the row's 13-point type, the value at
/// the trailing edge. UI Automation reports the Mac's 16-point line, centered in the field; focus shows the accent line.
/// </summary>
internal sealed partial class PlainField : TextBox
{
    public const double Line = 16;

    public PlainField()
    {
        Style = AppResources.Style("AnnotatorValueField");
        FontSize = 13;
        Padding = new Thickness(0);
        Height = Line;
        BorderThickness = new Thickness(0);
        VerticalContentAlignment = VerticalAlignment.Center;
        TextAlignment = TextAlignment.Right;
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (string key in new[]
        {
            "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
            "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushDisabled",
        })
        {
            Resources[key] = clear;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(PlainField owner) : TextBoxAutomationPeer(owner)
    {
        protected override WinRect GetBoundingRectangleCore()
        {
            double top = (owner.ActualHeight - Line) / 2;
            return SettingsFrames.Of(owner, new WinRect(0, top, owner.ActualWidth, Line), base.GetBoundingRectangleCore());
        }
    }
}

/// <summary>
/// The Mac's Stepper with .labelsHidden(): a 20 by 24-point rounded control with an up and a down half. It is one tab
/// stop; Up and Down change the value. UI Automation reads a spinner (the Mac's AXIncrementor) with its name, value and
/// range, and its two halves as Increase and Decrease buttons.
/// </summary>
internal sealed partial class MacStepper : ContentControl
{
    private readonly long _minimum;
    private readonly long _maximum;
    private long _value;

    public MacStepper(string name, long minimum, long maximum)
    {
        _minimum = minimum;
        _maximum = maximum;
        Width = 20;
        Height = 24;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetName(this, name);
        var body = new Grid
        {
            CornerRadius = new CornerRadius(5),
            Background = Pickers.PickerColors.ControlFill,
        };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Button up = Half("chevron.up", "Increase", 1);
        Button down = Half("chevron.down", "Decrease", -1);
        Grid.SetRow(down, 1);
        body.Children.Add(up);
        body.Children.Add(down);
        Content = body;
        KeyDown += (_, args) =>
        {
            if (args.Key is VirtualKey.Up or VirtualKey.Down)
            {
                args.Handled = true;
                Step(args.Key == VirtualKey.Up ? 1 : -1);
            }
        };
    }

    /// <summary>A step the person asked for, with the new value already inside the range.</summary>
    public event Action<long>? Changed;

    public long Value
    {
        get => _value;
        set
        {
            long clamped = Math.Clamp(value, _minimum, _maximum);
            if (clamped == _value)
            {
                return;
            }
            long before = _value;
            _value = clamped;
            if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, (double)before, (double)clamped);
            }
        }
    }

    private void Step(long delta) => Request(_value + delta);

    private void Request(long value)
    {
        long next = Math.Clamp(value, _minimum, _maximum);
        if (next != _value)
        {
            Value = next;
            Changed?.Invoke(next);
        }
    }

    private Button Half(string symbol, string name, long delta)
    {
        FrameworkElement glyph = Icons.Glyphs.ForSymbol(symbol, 9, "AnnotatorGlyph");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var half = new Button
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            CornerRadius = new CornerRadius(5),
            Content = glyph,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetName(half, name);
        half.Click += (_, _) =>
        {
            Step(delta);
            Focus(FocusState.Pointer);
        };
        return half;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(MacStepper owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        public bool IsReadOnly => false;

        public double LargeChange => 1;

        public double Maximum => owner._maximum;

        public double Minimum => owner._minimum;

        public double SmallChange => 1;

        public double Value => owner._value;

        public void SetValue(double value) => owner.Request((long)Math.Round(value));

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Spinner;

        protected override string GetClassNameCore() => "Stepper";

        protected override bool IsControlElementCore() => true;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore() => SettingsFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
