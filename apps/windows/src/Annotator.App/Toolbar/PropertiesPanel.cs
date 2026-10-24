using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Workspace;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CoreStyle = Annotator.Core.Style;
using CoreTextAlignment = Annotator.Core.TextAlignment;
using Glyphs = Annotator.App.Icons.Glyphs;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Toolbar;

/// <summary>What the full properties panel needs from the window that shows it.</summary>
internal interface IPropertiesHost
{
    WorkspaceModel Model { get; }

    /// <summary>The theme's ink for property samples: the label color, opaque (the Mac's InspectorSampleView).</summary>
    Brush Ink { get; }

    /// <summary>The person used a control: transient windows such as the tray flyout close.</summary>
    void Interacted();

    /// <summary>
    /// Opens the color popover for the stroke or the fill next to <paramref name="anchor"/>, the color well that asked:
    /// the same entry point as the quick properties' color wells (<see cref="ToolbarWindow.OpenColorPicker"/>).
    /// </summary>
    void OpenColorPicker(ColorPickerRequest request, FrameworkElement anchor);

    /// <summary>Opens the arrowhead choices for the start or end below <paramref name="anchor"/>; <paramref name="closed"/> runs when they close.</summary>
    void OpenArrowHeads(bool start, FrameworkElement anchor, Action closed);

    /// <summary>A value field needs the keyboard: the window becomes active while the person types.</summary>
    void BeginTyping(TextBox field);

    void EndTyping();
}

/// <summary>
/// The full properties panel, a port of ContextualInspector.swift with NativeInspectorComponents.swift: the "Editing …"
/// line for a selection, the current tool's property blocks in the adaptive layout (or one column when
/// <c>compact</c>), with the arrow controls at the end of the layout (below it when compact). Every control applies to
/// the tool's style and to the selection through the workspace model; slider drags and field edits are one Undo step
/// each. The whole panel is unavailable while every selected object is locked.
/// </summary>
internal sealed class PropertiesPanel
{
    private const string Secondary = "AnnotatorSecondaryText";
    private readonly IPropertiesHost _host;
    private readonly WorkspaceModel _model;
    private readonly List<Action<WorkspaceSnapshot>> _updates;
    private readonly InspectorPlan _plan;
    private readonly bool _compact;
    private readonly double _width;
    private readonly SceneBuilder _samples = new(NoText.Instance);

    public PropertiesPanel(IPropertiesHost host, InspectorPlan plan, bool compact, double width, List<Action<WorkspaceSnapshot>> updates)
    {
        _host = host;
        _model = host.Model;
        _plan = plan;
        _compact = compact;
        _width = width;
        _updates = updates;
        // SwiftUI's VStack hugs the effect controls and their caption, where the property layout takes the whole width.
        bool hugs = plan.Body == InspectorBody.Cover;
        var stack = new MacStack { Spacing = 10, Width = hugs ? double.NaN : width };
        if (plan.Context is not null)
        {
            stack.Children.Add(Left(Context()));
        }
        Body(stack);
        if (plan.Arrow && compact)
        {
            stack.Children.Add(Left(ArrowControls()));
        }
        // SwiftUI's disabled(_:) for a locked selection: the controls are unavailable, the words keep their colors.
        var enabled = new ContentControl
        {
            Content = stack,
            IsTabStop = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        _updates.Add(state => enabled.IsEnabled = !state.SelectionIsLocked);
        Root = new PropertiesRoot { Width = hugs ? double.NaN : width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Root.Children.Add(enabled);
    }

    /// <summary>The panel; UI Automation reads its controls in the Mac's order.</summary>
    public PropertiesRoot Root { get; }

    /// <summary>
    /// Port of CompactInspectorViewport: the panel in a vertical scroll area of <paramref name="width"/> points, as tall as
    /// the panel up to <paramref name="limit"/>. The panel leaves room for the scroll bar, as the Mac's legacy scroller does.
    /// </summary>
    public static ScrollViewer Viewport(IPropertiesHost host, WorkspaceSnapshot snapshot, bool compact, double width, double limit,
                                        List<Action<WorkspaceSnapshot>> updates)
    {
        var panel = new PropertiesPanel(host, InspectorPlan.For(snapshot), compact, width - ScrollerWidth, updates);
        return new ScrollViewer
        {
            Width = width,
            MaxHeight = limit,
            Content = panel.Root,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>The Mac legacy scroller's width that CompactInspectorViewport keeps free beside the panel.</summary>
    public const double ScrollerWidth = 15;

    private static FrameworkElement Left(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        return element;
    }

    /// <summary>The "Editing …" line (Mac selected-object-context): 11-point semibold with the dashed square.</summary>
    private FrameworkElement Context()
    {
        var label = new AccessibleText(_plan.Context!, "selected-object-context") { Height = 14 };
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        FrameworkElement icon = Glyphs.ForSymbol("square.dashed", 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        TextBlock text = ToolbarParts.Label(_plan.Context!, 11, "AnnotatorPrimaryText", FontWeights.SemiBold);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        label.Children.Add(row);
        _updates.Add(state =>
        {
            if (InspectorPlan.ContextText(state) is { } context && context != text.Text)
            {
                text.Text = context;
                AutomationProperties.SetName(label, context);
            }
        });
        return label;
    }

    private void Body(MacStack stack)
    {
        switch (_plan.Body)
        {
            case InspectorBody.SelectHint:
                stack.Children.Add(Left(PropertyMetrics.Text(InspectorPlan.SelectHintText, 10, Secondary)));
                break;
            case InspectorBody.Image:
                stack.Children.Add(Left(ImageOpacity()));
                break;
            case InspectorBody.Cover:
                stack.Children.Add(Left(CoverControls()));
                stack.Children.Add(Left(PropertyMetrics.Text(InspectorPlan.CoverCaption, 10, Secondary)));
                break;
            default:
                Panel layout = Layout();
                foreach (InspectorItem item in _plan.Items)
                {
                    layout.Children.Add(Left(Item(item)));
                }
                // The arrow's choices follow Roughness on the same line when the toolbar has room (Mac arrowControls).
                if (_plan.Arrow && !_compact)
                {
                    layout.Children.Add(Left(ArrowControls()));
                }
                stack.Children.Add(Left(layout));
                break;
        }
    }

    /// <summary>Mac propertyLayout: the adaptive layout, or one column with 14 points between blocks when compact.</summary>
    private Panel Layout() => _compact ? new MacStack { Spacing = PropertyLayout.Spacing } : new MacFlow { Width = _width };

    private FrameworkElement Item(InspectorItem item)
    {
        Tool tool = _plan.Tool;
        switch (item)
        {
            case InspectorItem.Colors:
                var colors = new MacStack { Orientation = Orientation.Horizontal, Spacing = 10 };
                colors.Children.Add(Left(Well(fill: false)));
                if (tool.SupportsFill)
                {
                    colors.Children.Add(Left(Well(fill: true)));
                }
                return colors;
            case InspectorItem.StrokeColor:
                return Well(fill: false);
            case InspectorItem.FillColor:
                return Well(fill: true);
            case InspectorItem.Width:
                return Block(InspectorPlan.WidthTitle(tool), "lineweight",
                    Options(InspectorPlan.WidthChoices(tool).Select(width =>
                            (width, $"Width {StyleParameter.Format(width)}", InspectorSample.Thickness(width))),
                        style => style.Width, width => _model.ApplyStyle((ref CoreStyle style) => style.Width = width)),
                    Value(StyleParameter.Width, 80, showLabel: false));
            case InspectorItem.Opacity:
                return Value(StyleParameter.Opacity, _compact ? 64 : 90, showLabel: true);
            case InspectorItem.Pattern:
                return Block("Pattern", "line.3.horizontal",
                    Options(StrokePattern.AllCases.Select(pattern =>
                            (pattern, InspectorPlan.Capitalized(pattern.RawValue), InspectorSample.ForPattern(pattern))),
                        style => style.Pattern, pattern => _model.ApplyStyle((ref CoreStyle style) => style.Pattern = pattern)));
            case InspectorItem.Roughness:
                return Block("Roughness", "scribble",
                    Options(InspectorPlan.RoughnessChoices.Select(choice => (choice.Value, choice.Name, InspectorSample.Roughness(choice.Value))),
                        style => style.Roughness, roughness => _model.ApplyStyle((ref CoreStyle style) => style.Roughness = roughness)));
            case InspectorItem.FillStyle:
                return Block("Fill style", "square.lefthalf.filled",
                    Options(FillPattern.AllCases.Select(pattern =>
                            (pattern, InspectorPlan.Capitalized(pattern.RawValue), InspectorSample.Fill(pattern))),
                        style => style.FillPattern, pattern => _model.ApplyStyle((ref CoreStyle style) => style.FillPattern = pattern)));
            case InspectorItem.Edges:
                return Block("Edges", "rectangle",
                    Options(new[] { (false, "Sharp corners", InspectorSample.Edges(false)), (true, "Rounded corners", InspectorSample.Edges(true)) },
                        style => style.CornerRadius > 0,
                        rounded => _model.ApplyStyle((ref CoreStyle style) => style.CornerRadius = rounded ? InspectorPlan.RoundedCornerRadius : 0)));
            case InspectorItem.GridSize:
                var grid = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
                grid.Children.Add(Value(StyleParameter.GridRows, 0, showLabel: true));
                grid.Children.Add(Value(StyleParameter.GridColumns, 0, showLabel: true));
                return grid;
            case InspectorItem.Axes:
                return Axes();
            case InspectorItem.Pressure:
                var pressure = new MacStack { Orientation = Orientation.Horizontal, Spacing = 16 };
                pressure.Children.Add(Left(PressureChoice(false, "Constant")));
                pressure.Children.Add(Left(PressureChoice(true, "Tablet")));
                return Block("Pressure", "pencil.tip", pressure);
            case InspectorItem.SmartDraw:
                var smart = new SmartDrawToggle(_model, 36);
                smart.Root.Click += (_, _) => _host.Interacted();
                _updates.Add(smart.Update);
                return Block("Smart Draw", "wand.and.stars", smart.Root);
            case InspectorItem.Caption:
                return PropertyMetrics.Text(_plan.Caption ?? string.Empty, 10, Secondary);
            case InspectorItem.Font:
                var fonts = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
                fonts.Children.Add(Left(FontChoice("System", "system", new FontFamily("Segoe UI"))));
                fonts.Children.Add(Left(FontChoice("Mono", "monospace", new FontFamily("Cascadia Mono, Consolas, Courier New"))));
                return Block("Font", "textformat", fonts);
            case InspectorItem.TextSize:
                var sizes = new MacStack { Orientation = Orientation.Horizontal, Spacing = 4 };
                foreach (double size in InspectorPlan.FontSizeChoices)
                {
                    TextBlock letter = Glyph("A", size / 2);
                    (ParityButton button, Action<bool> select) = Choice($"Font size {(int)size}", letter);
                    button.Click += (_, _) => Apply((ref CoreStyle style) => style.FontSize = size);
                    _updates.Add(state => select(state.Style.FontSize == size));
                    sizes.Children.Add(Left(button));
                }
                return Block("Size", "textformat.size", sizes, Value(StyleParameter.TextSize, 80, showLabel: false));
            case InspectorItem.Weight:
                (ParityButton bold, Action<bool> boldSelect) = Choice("Bold", Symbol("bold", 17));
                AutomationProperties.SetAutomationId(bold, "bold");
                bold.Click += (_, _) => Apply((ref CoreStyle style) => style.Bold = !style.Bold);
                _updates.Add(state =>
                {
                    boldSelect(state.Style.Bold);
                    bold.AccessibleValue = state.Style.Bold ? "On" : "Off";
                });
                return Block("Weight", "bold", bold);
            case InspectorItem.Alignment:
                var alignments = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
                foreach (CoreTextAlignment alignment in CoreTextAlignment.AllCases)
                {
                    string symbol = alignment switch
                    {
                        CoreTextAlignment.Left => "text.alignleft",
                        CoreTextAlignment.Center => "text.aligncenter",
                        _ => "text.alignright",
                    };
                    (ParityButton button, Action<bool> select) = Choice($"Align {alignment.RawValue}", Symbol(symbol, 16));
                    AutomationProperties.SetAutomationId(button, symbol);
                    button.Click += (_, _) => Apply((ref CoreStyle style) => style.Alignment = alignment);
                    _updates.Add(state => select(state.Style.Alignment == alignment));
                    alignments.Children.Add(Left(button));
                }
                return Block("Alignment", "text.alignleft", alignments);
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown property block.");
        }
    }

    /// <summary>Applies a style change from a button: one Undo step for the selection, and the tool's saved style.</summary>
    private void Apply(StyleMutation mutation)
    {
        _host.Interacted();
        _model.ApplyStyle(mutation);
    }

    /// <summary>Port of PropertyBlock: the title with its symbol, 6 points above the content.</summary>
    private static MacStack Block(string name, string symbol, params FrameworkElement[] content)
    {
        var block = new MacStack { Spacing = 6 };
        block.Children.Add(Left(BlockTitle(name, symbol)));
        foreach (FrameworkElement part in content)
        {
            block.Children.Add(Left(part));
        }
        return block;
    }

    /// <summary>
    /// SwiftUI's Label at 11 points medium in the secondary color. UI Automation reads it as one static text with the Mac's
    /// frame: from the symbol's drawn edge to the end of the words.
    /// </summary>
    private static FrameworkElement BlockTitle(string name, string symbol)
    {
        (double offset, double width) = PropertyMetrics.Label(name);
        TextBlock title = ToolbarParts.Label(name, 11, Secondary, FontWeights.Medium);
        title.LineHeight = 14;
        title.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement icon = Glyphs.ForSymbol(symbol, 12, "AnnotatorGlyphSecondary");
        if (width <= 0)
        {
            title.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            width = 19 + Math.Ceiling(title.DesiredSize.Width);
        }
        var label = new AccessibleText(name) { Width = offset + width, Height = 14, Bounds = new WinRect(offset, 0, width, 14) };
        Canvas.SetLeft(icon, offset - 1);
        Canvas.SetTop(icon, 1);
        var art = new Canvas { IsHitTestVisible = false };
        art.Children.Add(icon);
        label.Children.Add(art);
        title.HorizontalAlignment = HorizontalAlignment.Right;
        label.Children.Add(title);
        return label;
    }

    /// <summary>
    /// Port of InspectorControlButton: a 36-point button with a 9-point corner, a faint fill and a 1-point outline, or the
    /// accent's 2-point outline when selected. Returns the button and how to show it selected. A choice also reports its
    /// selected state to UI Automation (the Mac's isSelected trait).
    /// </summary>
    internal static (ParityButton Button, Action<bool> Select) Choice(string name, UIElement content, double width = 36, double height = 36,
                                                                     bool choice = true)
    {
        var fill = new Border { CornerRadius = new CornerRadius(9) };
        var outline = new Border { IsHitTestVisible = false };
        var frame = new Grid { Width = width, Height = height };
        frame.Children.Add(fill);
        frame.Children.Add(content);
        frame.Children.Add(outline);
        ParityButton button = ToolbarParts.Button(width, height, frame, name, 9);
        ToolbarParts.Help(button, name);
        bool? shown = null;
        void Select(bool selected)
        {
            if (choice)
            {
                button.AccessibleSelected = selected;
            }
            if (shown == selected)
            {
                return;
            }
            shown = selected;
            fill.Background = AppResources.Brush(selected ? "AnnotatorControlSelectedFillBrush" : "AnnotatorControlFillBrush");
            double thickness = selected ? 2 : 1;
            // WinUI rounds a border's outer edge by its radius plus its thickness.
            outline.CornerRadius = new CornerRadius(9 - thickness);
            outline.BorderThickness = new Thickness(thickness);
            outline.BorderBrush = AppResources.Brush(selected ? "AnnotatorAccentBrush" : "AnnotatorControlStrokeBrush");
        }
        Select(false);
        button.IsEnabledChanged += (_, _) => frame.Opacity = button.IsEnabled ? 1 : 0.5;
        return (button, Select);
    }

    /// <summary>Port of SampleOptions: a row of sample buttons 5 points apart; the current value's button is selected.</summary>
    private MacStack Options<T>(IEnumerable<(T Value, string Name, InspectorSample Sample)> values, Func<CoreStyle, T> current, Action<T> choose)
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
        var choices = new List<(T Value, Action<bool> Select)>();
        foreach ((T value, string name, InspectorSample sample) in values)
        {
            (ParityButton button, Action<bool> select) = Choice(name, Sample(sample));
            button.Click += (_, _) =>
            {
                _host.Interacted();
                choose(value);
            };
            choices.Add((value, select));
            row.Children.Add(Left(button));
        }
        _updates.Add(state =>
        {
            T now = current(state.Style);
            foreach ((T value, Action<bool> select) in choices)
            {
                select(EqualityComparer<T>.Default.Equals(value, now));
            }
        });
        return row;
    }

    /// <summary>A sample's picture: the canvas renderer's drawing of the Mac's sample element, in the theme's ink.</summary>
    private Canvas Sample(InspectorSample sample) =>
        SampleArt.Draw(sample.Draw(new RGBA(0, 0, 0), _samples), _host.Ink);

    private static TextBlock Glyph(string text, double size, FontFamily? family = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Style = AppResources.Style("AnnotatorPrimaryText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        if (family is not null)
        {
            block.FontFamily = family;
        }
        AutomationProperties.SetAccessibilityView(block, AccessibilityView.Raw);
        return block;
    }

    private static FrameworkElement Symbol(string symbol, double size)
    {
        FrameworkElement glyph = Glyphs.ForSymbol(symbol, size);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return glyph;
    }

    private FrameworkElement FontChoice(string title, string name, FontFamily family)
    {
        (ParityButton button, Action<bool> select) = Choice($"{title} font", Glyph("Aa", 17, family));
        button.Click += (_, _) => Apply((ref CoreStyle style) => style.FontName = name);
        _updates.Add(state => select(string.Equals(state.Style.FontName, name, StringComparison.Ordinal)));
        return button;
    }

    /// <summary>Mac pressureChoice: the sample button and its caption, centered under each other 4 points apart.</summary>
    private FrameworkElement PressureChoice(bool pressure, string label)
    {
        (ParityButton button, Action<bool> select) = Choice($"{label} pressure", Sample(InspectorSample.Pressure(pressure)));
        button.Click += (_, _) => Apply((ref CoreStyle style) => style.Pressure = pressure);
        _updates.Add(state => select(state.Style.Pressure == pressure));
        return Captioned(button, label);
    }

    private static MacStack Captioned(FrameworkElement control, string caption)
    {
        var stack = new MacStack { Spacing = 4 };
        control.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(control);
        TextBlock text = PropertyMetrics.Text(caption, 10, Secondary);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        // The caption's line height is its exact height: rounded up to whole pixels (13.6 points at 125%), it moved the
        // blocks below the arrow type down by more than a point.
        text.MinHeight = ToolbarParts.LineHeight(10);
        stack.Children.Add(text);
        return stack;
    }

    private FrameworkElement Well(bool fill)
    {
        var well = new ColorWell(fill, showLabel: true, _host.OpenColorPicker, compact: false);
        _updates.Add(well.Update);
        return well.Root;
    }

    private FrameworkElement Value(StyleParameter parameter, double sliderWidth, bool showLabel)
    {
        var control = new ValueControl(_model, parameter, _plan.Tool, sliderWidth, showLabel, _host.BeginTyping, _host.EndTyping, small: true);
        _updates.Add(control.Update);
        return control.Root;
    }

    /// <summary>
    /// A slider that edits a style value directly (head size, image opacity): a drag is one Undo step, as the Mac's
    /// onEditingChanged begins and finishes a style edit.
    /// </summary>
    private ParitySlider RawSlider(double width, double minimum, double maximum, Func<CoreStyle, double> read, StyleMutationOf apply,
                                   out FrameworkElement band)
    {
        ParitySlider slider = ValueControl.Slider(width, minimum, maximum, small: true, out band);
        bool updating = false, sliding = false;
        void End()
        {
            if (sliding)
            {
                sliding = false;
                _model.FinishStyleEdit();
            }
        }
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (!sliding)
            {
                sliding = true;
                _host.Interacted();
                _model.FinishStyleEdit();
                _model.BeginStyleEdit();
            }
        }), handledEventsToo: true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => End()), handledEventsToo: true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => End()), handledEventsToo: true);
        slider.ValueChanged += (_, args) =>
        {
            if (!updating)
            {
                double value = args.NewValue;
                _model.ApplyStyle((ref CoreStyle style) => apply(value, ref style));
            }
        };
        _updates.Add(state =>
        {
            updating = true;
            slider.Value = Math.Clamp(read(state.Style), minimum, maximum);
            updating = false;
        });
        return slider;
    }

    private delegate void StyleMutationOf(double value, ref CoreStyle style);

    /// <summary>Mac arrowControls: Start, End, Arrow type and Head size, read as the group "Arrow properties".</summary>
    private FrameworkElement ArrowControls()
    {
        var group = new UnionGroup("Arrow properties");
        Panel layout = _compact ? new MacStack { Spacing = PropertyLayout.Spacing } : new MacFlow { Limit = _width };
        layout.Children.Add(Left(Block("Start", "arrow.left", ArrowEnd(start: true))));
        layout.Children.Add(Left(Block("End", "arrow.right", ArrowEnd(start: false))));
        var routes = new MacStack { Orientation = Orientation.Horizontal, Spacing = 7 };
        foreach (bool curved in new[] { false, true })
        {
            (ParityButton button, Action<bool> select) = Choice(curved ? "Curved arrow" : "Straight arrow", Sample(InspectorSample.Route(curved)));
            button.Click += (_, _) => Apply((ref CoreStyle style) => style.Curved = curved);
            _updates.Add(state => select(state.Style.Curved == curved));
            routes.Children.Add(Left(Captioned(button, curved ? "Curved" : "Straight")));
        }
        layout.Children.Add(Left(Block("Arrow type", "arrow.up.right", routes)));
        MacStack sizes = Options(InspectorPlan.HeadSizeChoices.Select(size =>
                (size, $"{(int)(size * 100)}% arrowhead", InspectorSample.HeadSize(size))),
            style => style.HeadScale ?? 1, size => _model.ApplyStyle((ref CoreStyle style) => style.HeadScale = size));
        var slider = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6 };
        ParitySlider head = RawSlider(86, 0.5, 2, style => style.HeadScale ?? 1,
            (double value, ref CoreStyle style) => style.HeadScale = value, out FrameworkElement band);
        AutomationProperties.SetName(head, "Arrowhead size");
        slider.Children.Add(band);
        TextBlock percent = ToolbarParts.Text(string.Empty, 10, "AnnotatorPrimaryText");
        percent.HorizontalAlignment = HorizontalAlignment.Right;
        percent.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right;
        var percentFrame = new Grid { Width = 32, VerticalAlignment = VerticalAlignment.Center };
        percentFrame.Children.Add(percent);
        slider.Children.Add(percentFrame);
        _updates.Add(state =>
        {
            string text = InspectorPlan.HeadSizeText(state.Style.HeadScale);
            if (percent.Text != text)
            {
                percent.Text = text;
                percent.MinWidth = PropertyMetrics.TextWidth(text, 10);
            }
        });
        layout.Children.Add(Left(Block("Head size", "arrow.up.left.and.arrow.down.right", sizes, slider)));
        group.Children.Add(layout);
        return group;
    }

    /// <summary>
    /// Port of NativeArrowHeadPicker: a 68-point button with the current head and a chevron that opens the choices. Its
    /// value names the head; it shows selected while the choices are open.
    /// </summary>
    private FrameworkElement ArrowEnd(bool start)
    {
        var sample = new ContentControl { Width = 36, Height = 36, IsTabStop = false, IsHitTestVisible = false };
        var row = new MacStack
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        row.Children.Add(sample);
        FrameworkElement chevron = ToolbarParts.GlyphBox("chevron.down", 9, 9, 11);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(chevron);
        var content = new Grid();
        content.Children.Add(row);
        (ParityButton button, Action<bool> select) = Choice(start ? "Start arrowhead" : "End arrowhead", content, width: 68, choice: false);
        bool open = false;
        button.Click += (_, _) =>
        {
            // A second click closes the open choices (Mac open.toggle()); closing them resets the flag.
            bool wasOpen = open;
            _host.Interacted();
            if (wasOpen)
            {
                return;
            }
            open = true;
            select(true);
            _host.OpenArrowHeads(start, button, () =>
            {
                open = false;
                select(false);
            });
        };
        ArrowHead? shown = null;
        _updates.Add(state =>
        {
            ArrowHead head = start ? state.Style.StartHead : state.Style.EndHead;
            button.AccessibleValue = head.Label;
            if (shown != head)
            {
                shown = head;
                sample.Content = Sample(InspectorSample.ForHead(head, start));
            }
        });
        return button;
    }

    /// <summary>
    /// The Mac's "Image opacity" block: a 180-point slider, the percentage and how to edit the image, in the adaptive layout.
    /// </summary>
    private FrameworkElement ImageOpacity()
    {
        Panel layout = Layout();
        ParitySlider slider = RawSlider(180, 0, 1, style => style.Opacity, (double value, ref CoreStyle style) => style.Opacity = value,
            out FrameworkElement band);
        AutomationProperties.SetName(slider, "Image opacity");
        layout.Children.Add(Left(band));
        TextBlock percent = ToolbarParts.Text(string.Empty, 10, "AnnotatorPrimaryText");
        _updates.Add(state => percent.Text = $"{(int)(state.Style.Opacity * 100)}%");
        layout.Children.Add(Left(percent));
        layout.Children.Add(Left(PropertyMetrics.Text(InspectorPlan.ImageHint, 10, Secondary)));
        return Block("Image opacity", "photo", layout);
    }

    /// <summary>Port of CoverPropertiesView in the panel: the effect choice and its strength, or the note for a selected solid cover.</summary>
    private FrameworkElement CoverControls() =>
        EffectControls.CoverProperties(_model, _plan.Tool, _plan.Context is not null, _compact, showsSlider: true,
            (width, showLabel) => Value(StyleParameter.Effect, width, showLabel), _updates, _host.Interacted);

    /// <summary>The axes' centered switch, tick count and explanation (Mac ContextualInspector, tool == .axes).</summary>
    private FrameworkElement Axes()
    {
        var stack = new MacStack { Spacing = 8 };
        var centered = new CheckBox { Content = "Centered axes with negative directions", FontSize = 10, MinHeight = 0, MinWidth = 0 };
        centered.Click += (_, _) =>
        {
            bool value = centered.IsChecked == true;
            Apply((ref CoreStyle style) => style.NegativeAxes = value);
        };
        _updates.Add(state => centered.IsChecked = state.Style.NegativeAxes ?? false);
        stack.Children.Add(Left(centered));
        stack.Children.Add(Left(Value(StyleParameter.AxisTickCount, 0, showLabel: true)));
        TextBlock caption = ToolbarParts.Text(InspectorPlan.AxesCaption(null), 10, Secondary);
        _updates.Add(state => caption.Text = InspectorPlan.AxesCaption(state.Style.AxisTickCount));
        stack.Children.Add(Left(caption));
        return stack;
    }

    /// <summary>Samples draw no text; the scene builder needs a measurer anyway.</summary>
    private sealed class NoText : ITextMeasurer
    {
        public static readonly NoText Instance = new();

        public Annotator.Core.Geometry.Size Measure(string text, TextFont font) =>
            throw new InvalidOperationException("Property samples draw no text.");

        public FontLineMetrics LineMetrics(TextFont font) => throw new InvalidOperationException("Property samples draw no text.");
    }
}

/// <summary>
/// A named group whose UI Automation frame is the union of its controls, as SwiftUI reports an
/// <c>accessibilityElement(children: .contain)</c> container such as "Arrow properties". Given cells and a column count it
/// is also a grid (the Grid pattern), as SwiftUI reports a LazyVGrid (AXOpaqueProviderGroup), like the color popover's
/// swatches.
/// </summary>
internal sealed partial class UnionGroup : Grid
{
    private readonly IReadOnlyList<UIElement> _cells;
    private readonly int _columns;

    public UnionGroup(string name, IReadOnlyList<UIElement>? cells = null, int columns = 0)
    {
        AutomationProperties.SetName(this, name);
        _cells = cells ?? [];
        _columns = columns;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(UnionGroup owner) : FrameworkElementAutomationPeer(owner), IGridProvider
    {
        private bool IsGrid => owner._columns > 0 && owner._cells.Count > 0;

        public int RowCount => IsGrid ? (owner._cells.Count + owner._columns - 1) / owner._columns : 0;

        public int ColumnCount => IsGrid ? Math.Min(owner._columns, owner._cells.Count) : 0;

        public IRawElementProviderSimple? GetItem(int row, int column)
        {
            int index = row * owner._columns + column;
            return IsGrid && row >= 0 && column >= 0 && column < owner._columns && index < owner._cells.Count &&
                FrameworkElementAutomationPeer.FromElement(owner._cells[index]) is { } peer ? ProviderFromPeer(peer) : null;
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => "Group";

        protected override bool IsControlElementCore() => true;

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Grid && IsGrid ? this : base.GetPatternCore(patternInterface);

        protected override WinRect GetBoundingRectangleCore()
        {
            WinRect? union = null;
            if (GetChildren() is { } children)
            {
                foreach (AutomationPeer child in children)
                {
                    WinRect frame = child.GetBoundingRectangle();
                    if (frame.Width <= 0 && frame.Height <= 0)
                    {
                        continue;
                    }
                    union = union is { } known
                        ? new WinRect(Math.Min(known.X, frame.X), Math.Min(known.Y, frame.Y),
                            Math.Max(known.Right, frame.Right) - Math.Min(known.X, frame.X),
                            Math.Max(known.Bottom, frame.Bottom) - Math.Min(known.Y, frame.Y))
                        : frame;
                }
            }
            return union ?? AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
        }
    }
}
