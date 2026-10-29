using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.ColorPicking;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Glyphs = Annotator.App.Icons.Glyphs;
using CoreStyle = Annotator.Core.Style;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using Size = Windows.Foundation.Size;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Pickers;

/// <summary>
/// The stroke or fill color popover, a port of the Mac InspectorColorPalette (spec 001 color and gradient paint, ADR
/// 0011, WIN-UI-02): the title with Match outline, Fill shape, Solid and Gradient, the 15 palette colors with their
/// shades, the eight gradient presets, the inline color wheel, the hex field, the paint opacity, the object opacity
/// reset, Pick from screen and Done. Every change goes through the workspace's applyStyle to the current tool and the
/// selection, and the whole time it is open is one Undo step (Mac beginStyleEdit and finishStyleEdit). It is 284 points
/// wide, like the Mac's, and scrolls only when the screen is too short for it (the Mac's ViewThatFits).
/// </summary>
internal sealed class ColorPopover : PopoverWindow
{
    /// <summary>The Mac's 284-point popover less 16 points of padding on each side.</summary>
    public const double ContentWidth = 252;
    private const double SurfaceWidth = 284;

    private readonly WorkspaceModel _model;
    private readonly bool _fill;
    private readonly IScreenColorPicker? _screen;
    private bool _dismissed;
    private readonly Grid _surface = new() { Width = SurfaceWidth };
    private readonly MacStack _content = new() { Width = ContentWidth, Margin = new Thickness(16), Spacing = 8 };
    private readonly double _maximumHeight;
    private readonly ImmutableSwatches _swatches;
    private readonly ParityButton[] _shades = new ParityButton[5];
    private readonly Border[] _shadeRings = new Border[5];
    private readonly FrameworkElement[] _shadeSwatches = new FrameworkElement[5];
    private readonly List<(ParityButton Button, Border Ring, ColorGradient Gradient)> _gradients = [];
    private readonly ParityButton _matchOutline;
    private readonly ParityCheckBox? _fillToggle;
    private readonly Border? _fillBox;
    private readonly SegmentGroup _segments;
    private readonly SegmentButton _solid;
    private readonly SegmentButton _gradient;
    private readonly SwatchGrid _palette;
    private readonly MacStack _shadesSection = new() { Spacing = 0 };
    private readonly DisclosureButton _shadesDisclosure = new();
    private FrameworkElement _shadesChevron;
    private readonly MacStack _shadesRow = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly Canvas _shadesHost = new() { Width = ContentWidth, Height = 8 + SwatchGrid.Cell };
    private readonly SwatchGrid _gradientGrid = new();
    private readonly TextBlock _gradientCaption;
    private readonly ParityButton _wheelButton;
    private readonly Canvas _wheelLabel;
    private FrameworkElement _wheelChevron;
    private readonly ContentControl _wheelHost = new() { IsTabStop = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _hexLabel;
    private readonly Border _hexBox;
    private readonly TextBox _hexField;
    private readonly ParitySlider _opacity;
    private readonly TextBlock _opacityText;
    private readonly MacStack _objectOpacity = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _objectOpacityText;
    private readonly ParityButton _pick;
    private readonly TextBlock _validation;
    private ColorWheelView? _wheel;
    private ScrollViewer? _scroller;
    private RGBA _wheelColor;
    private bool _showingGradients;
    private bool _wheelExpanded;
    private string _hex = string.Empty;
    private string _committedHex = string.Empty;
    private string? _validationMessage;
    private string _structure = string.Empty;
    private bool _updating;

    /// <param name="screen">Pick from screen, or null where it cannot run.</param>
    public ColorPopover(WindowRegistry registry, WorkspaceModel model, ColorPickerRequest request, IScreenColorPicker? screen)
        : base(registry, request.Fill ? "Fill color" : "Stroke color")
    {
        _model = model;
        _fill = request.Fill;
        _screen = screen;
        _maximumHeight = MaximumHeight();
        WorkspaceSnapshot snapshot = model.Snapshot;
        _showingGradients = OffersGradients(snapshot) && request.Page switch
        {
            ColorPickerPage.Gradient => true,
            ColorPickerPage.Solid => false,
            _ => Gradient(snapshot.Style) is not null,
        };
        _wheelColor = Selected(snapshot.Style) ?? snapshot.Style.Color;

        // HStack { title; Spacer; Match outline }.frame(minHeight: 24): the fill's Match outline while there is a fill, the
        // stroke's while a shape label is edited (Mac matchLabelToStroke), unavailable once the label follows the stroke.
        var header = new Grid { Height = 24 };
        TextBlock title = PickerText.Line(_fill ? "Fill color" : "Stroke color", 13, "AnnotatorPrimaryText", FontWeights.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        header.Children.Add(title);
        _matchOutline = BorderedButton("Match outline", 91.5, 20, 11, 5);
        _matchOutline.HorizontalAlignment = HorizontalAlignment.Right;
        _matchOutline.Click += (_, _) => Act(() =>
        {
            if (_fill)
            {
                _model.MatchFillToStroke();
                _showingGradients = Gradient(_model.Snapshot.Style) is not null;
                UpdateHex(CurrentStyle);
            }
            else
            {
                _model.MatchLabelToStroke();
                UpdateHex(CurrentStyle);
                _showingGradients = Gradient(_model.Snapshot.Style) is not null;
            }
        });
        header.Children.Add(_matchOutline);
        _content.Children.Add(header);

        if (_fill)
        {
            (_fillToggle, _fillBox) = FillToggle();
            _content.Children.Add(_fillToggle);
        }

        // Picker("Color options") { Gradient; Solid }.pickerStyle(.segmented), unless the paint has no gradients.
        var segments = new SegmentGroup("Color options") { Width = 153.5, Height = 24, HorizontalAlignment = HorizontalAlignment.Left };
        _segments = segments;
        segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(6), Background = PickerColors.ControlFill, IsHitTestVisible = false };
        Grid.SetColumnSpan(track, 2);
        segments.Children.Add(track);
        _gradient = Segment(segments, "Gradient", 0, gradients: true);
        _solid = Segment(segments, "Solid", 1, gradients: false);
        _content.Children.Add(segments);

        // VStack(alignment: .leading, spacing: 12): the palette or presets, the wheel and the hex field.
        var inner = new MacStack { Spacing = 12 };
        _palette = new SwatchGrid();
        RGBA?[] colors = [.. PaintPalette.Colors(_fill)];
        var paletteButtons = new ParityButton[colors.Length];
        var paletteRings = new Border[colors.Length];
        var paletteSwatches = new FrameworkElement[colors.Length];
        for (int index = 0; index < colors.Length; index++)
        {
            RGBA? color = colors[index];
            (ParityButton button, Border ring, FrameworkElement swatch) = ColorButton(color, PaintPalette.Label(color, PaintPalette.Name(index, _fill)));
            button.Click += (_, _) => Act(() => Choose(color, useColorAlpha: color?.Alpha == 0));
            paletteButtons[index] = button;
            paletteRings[index] = ring;
            paletteSwatches[index] = swatch;
            _palette.Children.Add(button);
        }
        _swatches = new ImmutableSwatches(colors, paletteButtons, paletteRings, paletteSwatches);
        inner.Children.Add(_palette);

        // DisclosureGroup("Shades") { HStack(spacing: 12) { five shades }.padding(.top, 8) }.font(.caption)
        _shadesDisclosure.Style = AppResources.Style("AnnotatorPlainButton");
        _shadesDisclosure.Width = 47.5;
        _shadesDisclosure.Height = 21;
        _shadesDisclosure.CornerRadius = new CornerRadius(4);
        _shadesDisclosure.HorizontalAlignment = HorizontalAlignment.Left;
        _shadesDisclosure.LabelBand = new Rect(0, 4, 47.5, 13);
        AutomationProperties.SetName(_shadesDisclosure, "Shades");
        var shadesLabel = new Canvas { Width = 47.5, Height = 21, IsHitTestVisible = false };
        _shadesChevron = Chevron("chevron.right", 0.75, new Point(4, 10.5), PickerColors.Disclosure);
        shadesLabel.Children.Add(_shadesChevron);
        TextBlock shadesText = PickerText.Line("Shades", 10, "AnnotatorPrimaryText");
        Hide(shadesText);
        Canvas.SetLeft(shadesText, 11.5);
        Canvas.SetTop(shadesText, 4);
        shadesLabel.Children.Add(shadesText);
        _shadesDisclosure.Content = shadesLabel;
        _shadesDisclosure.Click += (_, _) => Act(() => _shadesDisclosure.IsExpanded = !_shadesDisclosure.IsExpanded);
        _shadesSection.Children.Add(_shadesDisclosure);
        for (int index = 0; index < _shades.Length; index++)
        {
            int shade = index;
            (ParityButton button, Border ring, FrameworkElement swatch) = ColorButton(Palette.Coral, PaintPalette.ShadeName(index));
            _shadeRings[index] = ring;
            _shadeSwatches[index] = swatch;
            button.Click += (_, _) => Act(() =>
            {
                if (Selected(_model.Snapshot.Style) is { } selected)
                {
                    RGBA value = PaintPalette.Shades(selected)[shade];
                    Choose(value, useColorAlpha: value.Alpha == 0);
                }
            });
            _shades[index] = button;
            _shadesRow.Children.Add(button);
        }
        // Five 42-point shades 12 points apart need 258 points, 6 more than the content: the row overhangs it by 3
        // points on each side, as SwiftUI centers content wider than its frame, and the stacks keep their width.
        Canvas.SetLeft(_shadesRow, -3);
        Canvas.SetTop(_shadesRow, 8);
        _shadesHost.Children.Add(_shadesRow);
        _shadesSection.Children.Add(_shadesHost);
        inner.Children.Add(_shadesSection);

        foreach (GradientPreset preset in GradientPreset.AllCases)
        {
            ColorGradient gradient = preset.Gradient;
            (ParityButton button, Border ring) = GradientButton(preset);
            button.Click += (_, _) => Act(() =>
            {
                _model.SetGradient(gradient, _fill);
                UpdateHex(CurrentStyle);
            });
            _gradients.Add((button, ring, gradient));
            _gradientGrid.Children.Add(button);
        }
        inner.Children.Add(_gradientGrid);
        _gradientCaption = PickerText.Caption("A blend across your whole shape.", secondary: true);
        _gradientCaption.HorizontalAlignment = HorizontalAlignment.Left;
        inner.Children.Add(_gradientCaption);

        // Button { Label("Color wheel", systemImage: chevron) }.font(.callout).buttonStyle(.plain)
        _wheelLabel = new Canvas { Width = 81.5, Height = 15, IsHitTestVisible = false };
        _wheelChevron = Chevron("chevron.right", 0.8, new Point(3, 7.5), null);
        _wheelLabel.Children.Add(_wheelChevron);
        TextBlock wheelText = PickerText.Line("Color wheel", 12, "AnnotatorPrimaryText");
        Hide(wheelText);
        Canvas.SetLeft(wheelText, 15);
        _wheelLabel.Children.Add(wheelText);
        var wheelContent = new Canvas { Width = ContentWidth, Height = 15, IsHitTestVisible = false };
        Canvas.SetLeft(_wheelLabel, 2);
        wheelContent.Children.Add(_wheelLabel);
        _wheelButton = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = ContentWidth,
            Height = 15,
            CornerRadius = new CornerRadius(4),
            Content = wheelContent,
            BoundsElement = _wheelLabel,
            AccessibleValue = "Collapsed",
        };
        AutomationProperties.SetName(_wheelButton, "Color wheel");
        AutomationProperties.SetAutomationId(_wheelButton, "inline-color-wheel");
        _wheelButton.Click += (_, _) => Act(() => _wheelExpanded = !_wheelExpanded);
        inner.Children.Add(_wheelButton);
        inner.Children.Add(_wheelHost);

        // Text("Hex color") and HStack { "#"; TextField }.padding(10).background(in: RoundedRectangle(cornerRadius: 7))
        _hexLabel = PickerText.Line("Hex color", 11, "AnnotatorSecondaryText");
        _hexLabel.HorizontalAlignment = HorizontalAlignment.Left;
        inner.Children.Add(_hexLabel);
        const string hexHelp = "Six RGB or eight RGBA digits. Entering a hex color switches to solid paint.";
        TextBlock hash = PickerText.Line("#", 13, "AnnotatorSecondaryText");
        hash.MinWidth = 8.5;
        AutomationProperties.SetHelpText(hash, hexHelp);
        _hexField = HexField(_fill ? "Fill hex color and alpha" : "Stroke hex color and alpha", hexHelp);
        var hexRow = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        hexRow.Children.Add(hash);
        hexRow.Children.Add(_hexField);
        _hexBox = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(7),
            Background = PickerColors.FieldFill,
            // A contrast theme outlines the field, which has no fill of its own there.
            BorderBrush = ContrastTheme.Pick(ContrastColor.WindowText, new SolidColorBrush(Microsoft.UI.Colors.Transparent)),
            BorderThickness = new Thickness(ContrastTheme.IsOn ? 1 : 0),
            Padding = new Thickness(ContrastTheme.IsOn ? 9 : 10),
            Child = hexRow,
        };
        ToolTipService.SetToolTip(_hexBox, hexHelp);
        inner.Children.Add(_hexBox);
        _content.Children.Add(inner);

        _content.Children.Add(ToolbarParts.Divider(height: 1));

        // HStack(spacing: 8) { Text(opacity name); Slider(0...1); Text("N%").frame(width: 34) }
        string opacityName = _fill ? "Fill opacity" : "Stroke opacity";
        double labelWidth = _fill ? 57 : 76;
        var opacityRow = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        TextBlock opacityLabel = PickerText.Line(opacityName, 11, "AnnotatorPrimaryText");
        opacityLabel.MinWidth = labelWidth;
        opacityRow.Children.Add(opacityLabel);
        _opacity = PickerText.Slider(ContentWidth - labelWidth - 8 - 8 - 34, 0, 1, 0.01);
        AutomationProperties.SetName(_opacity, opacityName);
        _opacity.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => ClearValidation()), handledEventsToo: true);
        _opacity.ValueChanged += (_, args) =>
        {
            if (!_updating)
            {
                Act(() =>
                {
                    _model.SetPaintOpacity(args.NewValue, _fill);
                    UpdateHex(CurrentStyle);
                });
            }
        };
        opacityRow.Children.Add(PickerText.SliderBand(_opacity));
        _opacityText = PickerText.Line(string.Empty, 11, "AnnotatorPrimaryText");
        _opacityText.HorizontalAlignment = HorizontalAlignment.Center;
        var percent = new Grid { Width = 34, VerticalAlignment = VerticalAlignment.Center };
        percent.Children.Add(_opacityText);
        opacityRow.Children.Add(percent);
        _content.Children.Add(opacityRow);

        // if model.style.opacity < 1: Text("Object opacity: N%.") and Button("Reset").buttonStyle(.link)
        _objectOpacityText = PickerText.Caption(string.Empty, secondary: true);
        _objectOpacity.Children.Add(_objectOpacityText);
        ParityButton reset = LinkButton("Reset");
        AutomationProperties.SetName(reset, "Reset object opacity");
        ToolbarParts.Help(reset, "Set whole-object opacity to 100%. Stroke and fill keep their own opacity.");
        reset.Click += (_, _) => Act(_model.ResetObjectOpacity);
        _objectOpacity.Children.Add(reset);
        _content.Children.Add(_objectOpacity);

        // Button { Label("Pick from screen", systemImage: "eyedropper") }.buttonStyle(.bordered)
        _pick = PickButton();
        _content.Children.Add(_pick);

        _validation = new TextBlock
        {
            FontSize = 10,
            LineHeight = 13,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppResources.Brush("AnnotatorErrorBrush"),
        };
        _content.Children.Add(_validation);

        // HStack { Spacer; Button("Done").controlSize(.small) }
        var doneRow = new Grid { Height = 20 };
        ParityButton done = BorderedButton("Done", 47.5, 20, 11, 5);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Click += (_, _) =>
        {
            if (_hex == _committedHex || ApplyHex())
            {
                Dismiss(restoreFocus: true);
            }
        };
        doneRow.Children.Add(done);
        _content.Children.Add(doneRow);

        _surface.Children.Add(new Border { Background = AppResources.Brush("AnnotatorWindowBackgroundBrush"), CornerRadius = new CornerRadius(14) });
        _surface.Children.Add(_content);
        // WinUI rounds a border's outer edge by its radius plus its thickness: 13 keeps the stroke on the 14-point edge.
        _surface.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            BorderBrush = AppResources.Brush("AnnotatorSurfaceStrokeBrush"),
            IsHitTestVisible = false,
        });
        _surface.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        Surface = _surface;
        // A section that opens or closes changes the content's height after WinUI's next layout pass.
        _content.SizeChanged += (_, args) =>
        {
            if (args.PreviousSize.Height != args.NewSize.Height)
            {
                Fit();
            }
        };

        UpdateHex(snapshot.Style);
        Refresh(snapshot);
        _model.BeginStyleEdit();
        Dismissed += () =>
        {
            _dismissed = true;
            if (_screen?.IsPicking == true)
            {
                _screen.Cancel();
            }
            _model.FinishStyleEdit();
        };
    }

    /// <summary>Shows a new workspace snapshot: the current paint, its opacity and the object opacity.</summary>
    public void Apply(WorkspaceSnapshot snapshot) => Refresh(snapshot);

    protected override void FocusFirst()
    {
        Control? first = _matchOutline is { Visibility: Visibility.Visible, IsEnabled: true } match ? match
            : (Control?)_fillToggle ?? (_segments.Visibility == Visibility.Visible ? (_showingGradients ? _gradient : _solid) : null);
        if (first is null)
        {
            // Without the Gradient and Solid choice the first color comes first.
            (FocusManager.FindFirstFocusableElement(_content) as Control)?.Focus(FocusState.Programmatic);
            return;
        }
        first.Focus(FocusState.Programmatic);
    }

    protected override bool OnEscape()
    {
        if (_screen?.IsPicking == true)
        {
            // Escape ends Pick from screen and keeps the popover, as the Mac's color sampler does.
            _screen.Cancel();
            return true;
        }
        return false;
    }

    private CoreStyle CurrentStyle => _model.Snapshot.Style;

    private RGBA? Selected(CoreStyle style) => _fill ? style.Fill : style.Color;

    /// <summary>Mac offersGradients: the laser pointer glows in one color, so its stroke picker has no gradients.</summary>
    private bool OffersGradients(WorkspaceSnapshot snapshot) => _fill || snapshot.EffectiveTool != Tool.Laser;

    private ColorGradient? Gradient(CoreStyle style) => _fill ? style.FillGradient : style.StrokeGradient;

    /// <summary>Runs a change and shows its result; validation messages end with any change, as on the Mac.</summary>
    private void Act(Action change)
    {
        change();
        Refresh(_model.Snapshot);
    }

    /// <summary>Mac choose(_:useColorAlpha:).</summary>
    private void Choose(RGBA? value, bool useColorAlpha)
    {
        _model.SetPaintColor(value, _fill, useColorAlpha);
        UpdateHex(CurrentStyle);
        _validationMessage = null;
    }

    private void ClearValidation()
    {
        if (_validationMessage is not null)
        {
            _validationMessage = null;
            Refresh(_model.Snapshot);
        }
    }

    /// <summary>Mac updateHex: the field shows the selected color, and that text counts as committed.</summary>
    private void UpdateHex(CoreStyle style)
    {
        _hex = Selected(style) is { } selected ? PaletteHex.Format(selected) : string.Empty;
        _committedHex = _hex;
        _updating = true;
        _hexField.Text = _hex;
        _updating = false;
    }

    /// <summary>Mac applyHex: a valid entry switches to solid paint; eight digits also set the paint opacity.</summary>
    private bool ApplyHex()
    {
        try
        {
            RGBA color = PaletteHex.Parse(_hex);
            bool alpha = PaletteHex.HasAlpha(_hex);
            Act(() => Choose(color, useColorAlpha: alpha));
            return true;
        }
        catch (InvalidHexColorException error)
        {
            _validationMessage = error.Message;
            Refresh(_model.Snapshot);
            _hexField.Focus(FocusState.Programmatic);
            return false;
        }
    }

    private void OnPicked(ScreenPickResult result)
    {
        switch (result)
        {
            case ScreenPickResult.Picked picked:
                Act(() => Choose(picked.Color, useColorAlpha: false));
                break;
            case ScreenPickResult.Failed failed:
                _validationMessage = failed.Message;
                Refresh(_model.Snapshot);
                break;
        }
    }

    /// <summary>Shows the snapshot's paint in every control and the sections the Mac's state shows; refits on a change of sections.</summary>
    private void Refresh(WorkspaceSnapshot snapshot)
    {
        CoreStyle style = snapshot.Style;
        RGBA? selected = Selected(style);
        ColorGradient? gradient = Gradient(style);
        _updating = true;
        try
        {
            for (int index = 0; index < _swatches.Colors.Length; index++)
            {
                MarkSelected(_swatches.Buttons[index], _swatches.Rings[index], _swatches.Swatches[index],
                    PaintPalette.IsSelected(_swatches.Colors[index], selected, gradient));
            }
            if (selected is { } current)
            {
                var shades = PaintPalette.Shades(current);
                for (int index = 0; index < _shades.Length; index++)
                {
                    PaintSwatch(_shadeSwatches[index], shades[index]);
                    MarkSelected(_shades[index], _shadeRings[index], _shadeSwatches[index], PaintPalette.IsSelected(shades[index], selected, gradient));
                }
            }
            foreach ((ParityButton button, Border ring, ColorGradient preset) in _gradients)
            {
                bool chosen = gradient == preset;
                ring.BorderBrush = chosen ? AppResources.Brush("AnnotatorAccentBrush") : null;
                button.BoundsElement = chosen ? null : (FrameworkElement)ring.Child;
                button.AccessibleSelected = chosen;
            }
            if (_fillToggle is { } toggle && _fillBox is { } box)
            {
                toggle.IsOn = selected is not null;
                ShowCheck(box, selected is not null);
            }
            double opacity = style.PaintOpacity(_fill);
            _opacity.Value = opacity;
            _opacity.IsEnabled = !(_fill && selected is null);
            _opacityText.Text = string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(opacity * 100, MidpointRounding.AwayFromZero)}%");
            _objectOpacityText.Text = string.Create(CultureInfo.InvariantCulture, $"Object opacity: {(int)(style.Opacity * 100)}%.");
            if (_wheel is { } wheel)
            {
                RGBA wheelColor = selected ?? style.Color;
                wheel.Follow(_wheelColor, wheelColor);
                _wheelColor = wheelColor;
            }
            _solid.IsChosen = !_showingGradients;
            _gradient.IsChosen = _showingGradients;
            ShowSegment(_gradient);
            ShowSegment(_solid);
            _pick.IsEnabled = _screen is not null;
        }
        finally
        {
            _updating = false;
        }
        if (!OffersGradients(snapshot))
        {
            _showingGradients = false;
        }
        Show(_segments, OffersGradients(snapshot));
        bool matchShown = _fill ? selected is not null : snapshot.EditingShapeLabel;
        _matchOutline.IsEnabled = _fill || !snapshot.EditingLabelFollowsStroke;
        Show(_matchOutline, matchShown);
        Show(_palette, !_wheelExpanded && !_showingGradients);
        Show(_shadesSection, !_wheelExpanded && !_showingGradients && selected is not null);
        Show(_shadesHost, _shadesDisclosure.IsExpanded);
        Show(_gradientGrid, !_wheelExpanded && _showingGradients);
        Show(_gradientCaption, !_wheelExpanded && _showingGradients);
        Show(_hexLabel, !_showingGradients || _wheelExpanded);
        Show(_hexBox, !_showingGradients || _wheelExpanded);
        Show(_objectOpacity, style.Opacity < 1);
        _validation.Text = _validationMessage ?? string.Empty;
        Show(_validation, _validationMessage is not null);
        ShowWheel(selected ?? style.Color);
        string structure = string.Create(CultureInfo.InvariantCulture,
            $"{matchShown}{_showingGradients}{_wheelExpanded}{selected is not null}{_shadesDisclosure.IsExpanded}{style.Opacity < 1}{_validationMessage}{OffersGradients(snapshot)}");
        if (structure != _structure)
        {
            _structure = structure;
            Fit();
        }
    }

    private void ShowWheel(RGBA color)
    {
        _wheelButton.AccessibleValue = _wheelExpanded ? "Expanded" : "Collapsed";
        string symbol = _wheelExpanded ? "chevron.down" : "chevron.right";
        if (_wheelChevron.Tag as string != symbol)
        {
            _wheelLabel.Children.Remove(_wheelChevron);
            _wheelChevron = Chevron(symbol, 0.8, new Point(3, 7.5), null);
            _wheelLabel.Children.Insert(0, _wheelChevron);
        }
        if (_wheelExpanded && _wheel is null)
        {
            // Mac InlineColorWheel(color: selected ?? model.style.color) { choose($0); showingGradients = false }.padding(.top, 8)
            _wheelColor = color;
            _wheel = new ColorWheelView(color, value => Act(() =>
            {
                Choose(value, useColorAlpha: false);
                _showingGradients = false;
            }));
            _wheelHost.Content = _wheel.Root;
        }
        else if (!_wheelExpanded && _wheel is not null)
        {
            _wheel = null;
            _wheelHost.Content = null;
        }
        Show(_wheelHost, _wheelExpanded);
        if (_shadesChevron.Tag as string != (_shadesDisclosure.IsExpanded ? "chevron.down" : "chevron.right"))
        {
            var label = (Canvas)_shadesDisclosure.Content;
            label.Children.Remove(_shadesChevron);
            _shadesChevron = Chevron(_shadesDisclosure.IsExpanded ? "chevron.down" : "chevron.right", 0.75, new Point(4, 10.5), PickerColors.Disclosure);
            label.Children.Insert(0, _shadesChevron);
        }
    }

    /// <summary>
    /// The Mac's ViewThatFits(in: .vertical): the contents at their own height, or in a scroll view as high as the screen
    /// allows. Then the window takes the new size.
    /// </summary>
    private void Fit()
    {
        _content.Measure(new Size(SurfaceWidth, double.PositiveInfinity));
        bool scroll = _content.DesiredSize.Height > _maximumHeight;
        if (scroll && _scroller is null)
        {
            _surface.Children.Remove(_content);
            _scroller = new ScrollViewer
            {
                Height = _maximumHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _content,
            };
            _surface.Children.Insert(1, _scroller);
        }
        else if (!scroll && _scroller is { } scroller)
        {
            scroller.Content = null;
            _surface.Children.Remove(scroller);
            _surface.Children.Insert(1, _content);
            _scroller = null;
        }
        Refit();
    }

    /// <summary>
    /// The Mac's maximumHeight: the screen's visible height less 80 points, at least 240; in the popover's own points when it
    /// grows with the text size.
    /// </summary>
    private static double MaximumHeight()
    {
        MonitorArea monitor = WindowChrome.MonitorAtCursor();
        return Math.Max(240, (monitor.WorkArea.Height / monitor.Scale - 80) / TextScaling.Zoom);
    }

    private static void Show(UIElement? element, bool visible)
    {
        Visibility value = visible ? Visibility.Visible : Visibility.Collapsed;
        if (element is null || element.Visibility == value)
        {
            return;
        }
        element.Visibility = value;
        // The stacks around it lay out in exact points and must measure again, up to the popover's surface.
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is UIElement container; parent = VisualTreeHelper.GetParent(parent))
        {
            container.InvalidateMeasure();
        }
    }

    private static void Hide(TextBlock text) => Hidden(text);

    /// <summary>Text that is part of a control's look; the control's name already says it, as on the Mac.</summary>
    private static TextBlock Hidden(TextBlock text)
    {
        text.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(text, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return text;
    }

    /// <summary>A selected swatch shows the accent ring, and the Mac then reports the ringed 42-point frame instead of the swatch.</summary>
    private static void MarkSelected(ParityButton button, Border ring, FrameworkElement swatch, bool selected)
    {
        ring.BorderBrush = selected ? AppResources.Brush("AnnotatorAccentBrush") : null;
        button.BoundsElement = selected ? null : swatch;
        button.AccessibleSelected = selected;
    }

    private void ShowSegment(SegmentButton segment)
    {
        var surface = (Border)segment.Content;
        surface.Background = segment.IsChosen ? PickerColors.SegmentSelected : null;
        ContrastTheme.Outline(surface, segment.IsChosen);
    }

    private SegmentButton Segment(SegmentGroup group, string title, int column, bool gradients)
    {
        TextBlock label = PickerText.Line(title, 13, "AnnotatorPrimaryText");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        Hide(label);
        var segment = new SegmentButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Content = new Border { CornerRadius = new CornerRadius(5), Child = label },
            Group = group,
        };
        AutomationProperties.SetName(segment, title);
        segment.Chosen += () => Act(() =>
        {
            _showingGradients = gradients;
            _wheelExpanded = false;
        });
        Grid.SetColumn(segment, column);
        group.Children.Add(segment);
        group.Segments.Add(segment);
        return segment;
    }

    private (ParityCheckBox Toggle, Border Box) FillToggle()
    {
        var box = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3.5),
            Margin = new Thickness(1, 1, 0, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock text = PickerText.Line("Fill shape", 12, "AnnotatorPrimaryText");
        text.Margin = new Thickness(21, 0, 0, 0);
        text.HorizontalAlignment = HorizontalAlignment.Left;
        Hide(text);
        var look = new Grid { IsHitTestVisible = false };
        look.Children.Add(box);
        look.Children.Add(text);
        var toggle = new ParityCheckBox
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = 74.5,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = look,
        };
        AutomationProperties.SetName(toggle, "Fill shape");
        ToolbarParts.Help(toggle, "Start with the outline's color or gradient at 40%. You can change it afterwards.");
        toggle.Toggled += enabled => Act(() =>
        {
            _model.SetFillEnabled(enabled);
            _showingGradients = Gradient(_model.Snapshot.Style) is not null;
            UpdateHex(CurrentStyle);
        });
        return (toggle, box);
    }

    /// <summary>The Mac checkbox: an accent box with a white check when on, an outlined box when off.</summary>
    private static void ShowCheck(Border box, bool on)
    {
        box.Background = on ? AppResources.Brush("AnnotatorAccentBrush") : AppResources.Brush("AnnotatorWindowBackgroundBrush");
        // A contrast theme checks the box in Highlight with a HighlightText mark.
        box.BorderBrush = on ? null : PickerColors.Primary(0.3);
        box.BorderThickness = new Thickness(on ? 0 : 1);
        if (!on)
        {
            box.Child = null;
            return;
        }
        var figure = new PathFigure { StartPoint = new Point(3.6, 7.3), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment { Point = new Point(6, 9.7) });
        figure.Segments.Add(new LineSegment { Point = new Point(10.5, 4.4) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        box.Child = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = ContrastTheme.Pick(ContrastColor.HighlightText, new SolidColorBrush(Microsoft.UI.Colors.White)),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    /// <summary>
    /// Mac colorButton: a 38-point swatch over the transparency grid, outlined, with 2 points around it for the accent
    /// ring of the selected color, and "None" on no fill.
    /// </summary>
    private static (ParityButton Button, Border Ring, FrameworkElement Swatch) ColorButton(RGBA? color, string label)
    {
        // The ring's 2-point border insets the swatch by the Mac's 2 points of padding. A swatch keeps its color in a contrast
        // theme (WIN-UI-07).
        var swatch = OwnColors.Mark(new Grid { Width = 38, Height = 38 });
        swatch.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = TransparencyGrid() });
        swatch.Children.Add(new Border { CornerRadius = new CornerRadius(6) });
        swatch.Children.Add(new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = PickerColors.SwatchStroke });
        PaintSwatch(swatch, color);
        if (color is null)
        {
            var none = new Border
            {
                Background = AppResources.Brush("AnnotatorWindowBackgroundBrush"),
                Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = Hidden(PickerText.Line("None", 10, "AnnotatorPrimaryText", FontWeights.Medium)),
            };
            AutomationProperties.SetAccessibilityView(none, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            swatch.Children.Add(none);
        }
        var ring = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Child = swatch };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = SwatchGrid.Cell,
            Height = SwatchGrid.Cell,
            CornerRadius = new CornerRadius(8),
            Content = ring,
            BoundsElement = swatch,
            AccessibleSelected = false,
        };
        AutomationProperties.SetName(button, label);
        return (button, ring, swatch);
    }

    private static void PaintSwatch(FrameworkElement swatch, RGBA? color)
    {
        var paint = (Border)((Grid)swatch).Children[1];
        paint.Background = color is { } value ? new SolidColorBrush(ToolbarParts.Color(value)) : null;
    }

    /// <summary>Mac gradient preset: PaintSwatch of the preset on 38 points, the accent ring when chosen, its name as help.</summary>
    private static (ParityButton Button, Border Ring) GradientButton(GradientPreset preset)
    {
        var swatch = OwnColors.Mark(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(6), Background = GradientBrush(preset.Gradient) });
        var ring = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2), Child = swatch };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = SwatchGrid.Cell,
            Height = SwatchGrid.Cell,
            CornerRadius = new CornerRadius(6),
            Content = ring,
            BoundsElement = swatch,
            AccessibleSelected = false,
        };
        AutomationProperties.SetName(button, $"{preset.Label} gradient");
        ToolbarParts.Help(button, preset.Label);
        return (button, ring);
    }

    /// <summary>Port of PaintSwatch: the gradient along its angle across the swatch, reaching its corners' projection.</summary>
    private static LinearGradientBrush GradientBrush(ColorGradient gradient)
    {
        double angle = gradient.Angle * Math.PI / 180;
        double dx = Math.Cos(angle), dy = Math.Sin(angle);
        double length = (Math.Abs(dx) + Math.Abs(dy)) / 2;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5 - dx * length, 0.5 - dy * length),
            EndPoint = new Point(0.5 + dx * length, 0.5 + dy * length),
        };
        int count = gradient.Colors.Count;
        for (int index = 0; index < count; index++)
        {
            brush.GradientStops.Add(new GradientStop { Color = ToolbarParts.Color(gradient.Colors[index]), Offset = count == 1 ? 0 : index / (double)(count - 1) });
        }
        return brush;
    }

    /// <summary>
    /// A bordered button (the Mac's .bordered style, small or regular) of a fixed size with centered text. A disabled one keeps
    /// half the fill and gray words, as the Tools window's bordered buttons do.
    /// </summary>
    private static ParityButton BorderedButton(string title, double width, double height, double fontSize, double radius)
    {
        TextBlock label = PickerText.Line(title, fontSize, "AnnotatorPrimaryText");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        Hide(label);
        var surface = new Border { CornerRadius = new CornerRadius(radius), Background = PickerColors.ControlFill, Child = label };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            VerticalAlignment = VerticalAlignment.Center,
            Content = Bordered(surface, label),
        };
        AutomationProperties.SetName(button, title);
        // WinUI does not raise IsEnabledChanged before the button is in the window, so it also shows its state on loading.
        button.IsEnabledChanged += (_, _) => ShowEnabled(button, surface, label);
        button.Loaded += (_, _) => ShowEnabled(button, surface, label);
        return button;
    }

    private static void ShowEnabled(ParityButton button, Border surface, TextBlock label)
    {
        surface.Background = button.IsEnabled ? PickerColors.ControlFill : Library.LibraryColors.DisabledControlFill;
        label.Style = AppResources.Style(button.IsEnabled ? "AnnotatorPrimaryText" : "AnnotatorDisabledText");
        label.ClearValue(TextBlock.ForegroundProperty);
        ContrastTheme.ButtonLook(surface, button.IsEnabled);
        if (ContrastTheme.IsOn && button.IsEnabled)
        {
            label.Foreground = ContrastTheme.Brush(ContrastColor.ButtonText);
        }
    }

    /// <summary>A bordered button's surface, with the contrast theme's button look when one is on.</summary>
    private static Border Bordered(Border surface, TextBlock label)
    {
        ContrastTheme.ButtonLook(surface);
        if (ContrastTheme.IsOn)
        {
            label.Foreground = ContrastTheme.Brush(ContrastColor.ButtonText);
        }
        return surface;
    }

    /// <summary>The Mac's .buttonStyle(.link) at caption size.</summary>
    private static ParityButton LinkButton(string title)
    {
        TextBlock label = PickerText.Line(title, 10, "AnnotatorAccentText");
        Hide(label);
        return new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Content = label,
        };
    }

    private ParityButton PickButton()
    {
        FrameworkElement icon = Glyphs.ForSymbol("eyedropper", 14, "AnnotatorGlyph");
        icon.VerticalAlignment = VerticalAlignment.Center;
        TextBlock label = PickerText.Line("Pick from screen", 13, "AnnotatorPrimaryText");
        Hide(label);
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(label);
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = ContentWidth,
            Height = 38,
            CornerRadius = new CornerRadius(6),
            Content = Bordered(new Border { CornerRadius = new CornerRadius(6), Background = PickerColors.ControlFill, Child = row }, label),
        };
        AutomationProperties.SetName(button, "Pick from screen");
        ToolbarParts.Help(button, "Choose a color anywhere on the screen. Escape cancels.");
        button.Click += (_, _) =>
        {
            if (_screen is { IsPicking: false } screen)
            {
                ClearValidation();
                // A packaged build picks only with screen access (WIN-CAP-04). While Windows has not asked yet, the explanation
                // comes to the front and asks, which closes this popover, so Pick from screen works the next time; a refusal
                // shows here with where to allow it.
                if (_model.EnsureScreenAccess(Annotator.Workspace.Capture.ScreenAccessFeature.PickColor, static () => { }, message =>
                    {
                        if (message is not null && !_dismissed)
                        {
                            OnPicked(new ScreenPickResult.Failed(message));
                        }
                    }))
                {
                    screen.Pick(OnPicked);
                }
            }
        };
        return button;
    }

    private TextBox HexField(string name, string help)
    {
        var field = new TextBox
        {
            Width = 215.5,
            Height = 15,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            PlaceholderText = "RRGGBB or RRGGBBAA",
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // The Mac's .textFieldStyle(.plain): no fill, frame or underline in any state, and no clear button.
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (string key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                 })
        {
            field.Resources[key] = clear;
        }
        field.Resources["TextControlThemePadding"] = new Thickness(0);
        field.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        field.Loaded += (_, _) => HideClearButton(field);
        AutomationProperties.SetName(field, name);
        AutomationProperties.SetHelpText(field, help);
        field.TextChanged += (_, _) =>
        {
            if (!_updating)
            {
                _hex = field.Text;
            }
        };
        field.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                ApplyHex();
            }
        };
        return field;
    }

    /// <summary>Keeps the text box's clear button at zero size; its visual states would show it while the field has text.</summary>
    private static void HideClearButton(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement { Name: "DeleteButton" } button)
            {
                button.MaxWidth = 0;
                button.Margin = new Thickness(0);
                return;
            }
            HideClearButton(child);
        }
    }

    /// <summary>A chevron glyph centered on <paramref name="center"/>, at <paramref name="scale"/> of Lucide's 24-unit grid.</summary>
    private static FrameworkElement Chevron(string symbol, double scale, Point center, Brush? tint)
    {
        double size = 24 * scale;
        FrameworkElement glyph = Glyphs.ForSymbol(symbol, size, "AnnotatorGlyph");
        if (tint is not null)
        {
            Tint(glyph, tint);
        }
        glyph.Tag = symbol;
        Canvas.SetLeft(glyph, center.X - size / 2);
        Canvas.SetTop(glyph, center.Y - size / 2);
        return glyph;
    }

    private static void Tint(DependencyObject element, Brush brush)
    {
        if (element is Microsoft.UI.Xaml.Shapes.Path path)
        {
            path.Stroke = brush;
            return;
        }
        if (element is Viewbox { Child: { } child })
        {
            Tint(child, brush);
            return;
        }
        if (element is Panel panel)
        {
            foreach (UIElement item in panel.Children)
            {
                Tint(item, brush);
            }
        }
    }

    private static ImageBrush? s_grid;
    private static bool s_gridDark;

    /// <summary>The Mac's TransparencyGrid: 6-point white and systemGray-at-40% squares, drawn at 4x so they stay sharp.</summary>
    private static ImageBrush TransparencyGrid()
    {
        bool dark = PickerColors.Dark;
        if (s_grid is { } cached && s_gridDark == dark)
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
                bool shaded = (x / cell + y / cell) % 2 == 0;
                int index = (y * size + x) * 4;
                pixels[index] = shaded ? gray.B : (byte)255;
                pixels[index + 1] = shaded ? gray.G : (byte)255;
                pixels[index + 2] = shaded ? gray.R : (byte)255;
                pixels[index + 3] = 255;
            }
        }
        var bitmap = new WriteableBitmap(size, size);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels);
        }
        bitmap.Invalidate();
        s_grid = new ImageBrush
        {
            ImageSource = bitmap,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            Transform = new ScaleTransform { ScaleX = 0.25, ScaleY = 0.25 },
        };
        s_gridDark = dark;
        return s_grid;
    }

    private sealed record ImmutableSwatches(RGBA?[] Colors, ParityButton[] Buttons, Border[] Rings, FrameworkElement[] Swatches);
}

/// <summary>Text and sliders of the color popover at the Mac's sizes.</summary>
internal static class PickerText
{
    /// <summary>
    /// One line of text with the Mac's line height for its size (Segoe UI lines are taller than SF Pro's). The line height
    /// is also its minimum height, so stacks add the exact height and not the one WinUI rounds up to whole pixels.
    /// </summary>
    public static TextBlock Line(string text, double size, string style, Windows.UI.Text.FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Style = AppResources.Style(style),
        TextWrapping = TextWrapping.NoWrap,
        LineHeight = ToolbarParts.LineHeight(size),
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        MinHeight = ToolbarParts.LineHeight(size),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>SwiftUI's .caption: 10 points.</summary>
    public static TextBlock Caption(string text, bool secondary) => Line(text, 10, secondary ? "AnnotatorSecondaryText" : "AnnotatorPrimaryText");

    /// <summary>A slider that UI Automation reports as the Mac's 16-point band, with its knob as a child.</summary>
    public static ParitySlider Slider(double width, double minimum, double maximum, double step) => new()
    {
        Width = width,
        Height = 32,
        Band = 16,
        Minimum = minimum,
        Maximum = maximum,
        StepFrequency = step,
        SmallChange = step,
        LargeChange = (maximum - minimum) / 10,
        IsThumbToolTipEnabled = false,
        VerticalAlignment = VerticalAlignment.Center,
        Style = AppResources.Style("AnnotatorSlider"),
    };

    /// <summary>
    /// The Windows slider needs 32 points; it sits on a canvas of the Mac slider's 16 points and overhangs it by 8 points
    /// above and below, as in the toolbar.
    /// </summary>
    public static Canvas SliderBand(ParitySlider slider)
    {
        Canvas.SetTop(slider, -8);
        var band = new Canvas { Width = slider.Width, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        band.Children.Add(slider);
        return band;
    }
}
