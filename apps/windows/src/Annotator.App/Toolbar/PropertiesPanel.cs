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
using Microsoft.UI.Xaml.Shapes;
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
    /// the toolbar's one entry point for color (<see cref="ToolbarWindow.OpenColorPicker"/>).
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
/// line for a selection and the current tool's property blocks on one row that never wraps (the toolbar grows sideways to
/// fit it), or in one column beside a vertical toolbar (<c>compact</c>), with the arrow controls at the end. Properties
/// with a few choices are one button that steps through them, or all their choices side by side (Settings > Toolbar >
/// Your properties). Every control applies to the tool's style and to the selection through the workspace model;
/// slider drags and field edits are one Undo step each. The whole panel is unavailable while every selected object is
/// locked.
/// </summary>
internal sealed class PropertiesPanel
{
    private const string Secondary = "AnnotatorSecondaryText";
    private readonly IPropertiesHost _host;
    private readonly WorkspaceModel _model;
    private readonly List<Action<WorkspaceSnapshot>> _updates;
    private readonly InspectorPlan _plan;
    private readonly bool _compact;
    private readonly SceneBuilder _samples = new(NoText.Instance);

    public PropertiesPanel(IPropertiesHost host, InspectorPlan plan, bool compact, double width, List<Action<WorkspaceSnapshot>> updates)
    {
        _host = host;
        _model = host.Model;
        _plan = plan;
        _compact = compact;
        _updates = updates;
        // SwiftUI's VStack hugs the effect controls and their caption, where the column takes the whole width.
        bool hugs = !compact || plan.Body == InspectorBody.Cover;
        // Mac rootLayout: one row aligned on the controls' middles, or a column with 10 points between its parts.
        Panel stack = compact
            ? new MacStack { Spacing = 10, Width = hugs ? double.NaN : width }
            : new PropertyRow { Spacing = PropertyLayout.Spacing };
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
        Root = new PropertiesRoot { Width = hugs ? double.NaN : width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Row = stack as PropertyRow };
        Root.Children.Add(enabled);
    }

    /// <summary>The panel; UI Automation reads its controls in the Mac's order.</summary>
    public PropertiesRoot Root { get; }

    /// <summary>
    /// Port of CompactInspectorViewport beside a vertical toolbar: the column in a vertical scroll area of
    /// <paramref name="width"/> points, as tall as the column up to <paramref name="limit"/>. It scrolls without a scroll
    /// bar, as on the Mac.
    /// </summary>
    public static ScrollViewer Viewport(IPropertiesHost host, WorkspaceSnapshot snapshot, double width, double limit,
                                        List<Action<WorkspaceSnapshot>> updates)
    {
        var panel = new PropertiesPanel(host, InspectorPlan.For(snapshot), compact: true, width, updates);
        return new ScrollViewer
        {
            Width = width,
            MaxHeight = limit,
            Content = panel.Root,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>
    /// Mac fullPropertiesRow's properties below a horizontal toolbar: the one row at its natural width. Below 1,
    /// <paramref name="scale"/> draws it that much smaller (minimal toolbar), so a tool's properties keep their proportions.
    /// </summary>
    public static FrameworkElement Row(IPropertiesHost host, WorkspaceSnapshot snapshot, List<Action<WorkspaceSnapshot>> updates,
                                       double scale = 1)
    {
        var panel = new PropertiesPanel(host, InspectorPlan.For(snapshot), compact: false, double.NaN, updates);
        if (scale >= 1)
        {
            return panel.Root;
        }
        return new ScaledContent
        {
            Factor = scale,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = panel.Root,
        };
    }

    private static FrameworkElement Left(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        return element;
    }

    /// <summary>The "Editing …" line (Mac selected-object-context): 11-point semibold with the dashed square.</summary>
    private FrameworkElement Context()
    {
        var label = new AccessibleText(_plan.Context!, "selected-object-context");
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        FrameworkElement icon = Glyphs.ForSymbol("square.dashed", 12);
        icon.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        TextBlock text = ToolbarParts.Label(_plan.Context!, 11, "AnnotatorPrimaryText", FontWeights.SemiBold);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        label.Children.Add(row);
        // The Mac's words are wider than Segoe UI's (D-4); its frame starts at the square's drawn edge, 1.5 points in.
        void Fit(string context)
        {
            text.MinWidth = PropertyMetrics.TextWidth(context, 11);
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            label.Bounds = new WinRect(1.5, 0, 12 + 6 + Math.Max(text.MinWidth, text.DesiredSize.Width) - 1.5, 14);
        }
        Fit(_plan.Context!);
        _updates.Add(state =>
        {
            if (InspectorPlan.ContextText(state) is { } context && context != text.Text)
            {
                text.Text = context;
                AutomationProperties.SetName(label, context);
                Fit(context);
            }
        });
        return label;
    }

    private void Body(Panel stack)
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
                Panel cover = Layout();
                cover.Children.Add(Left(CoverControls()));
                TextBlock caption = PropertyMetrics.Text(InspectorPlan.CoverCaption, 10, Secondary);
                if (!_compact)
                {
                    // fixedSize(horizontal: true): one line beside the effect controls.
                    caption.TextWrapping = TextWrapping.NoWrap;
                }
                cover.Children.Add(Left(caption));
                stack.Children.Add(Left(cover));
                break;
            default:
                Panel layout = Layout();
                foreach (InspectorItem item in _plan.Items)
                {
                    layout.Children.Add(Left(Item(item)));
                }
                // The arrow's choices follow Roughness on the same row; a column puts them after the others.
                if (_plan.Arrow && !_compact)
                {
                    layout.Children.Add(Left(ArrowControls()));
                }
                stack.Children.Add(Left(layout));
                break;
        }
    }

    /// <summary>Mac propertyLayout: one row aligned on the controls' middles, or one column with 14 points between blocks.</summary>
    private Panel Layout() => _compact ? new MacStack { Spacing = PropertyLayout.Spacing } : new PropertyRow { Spacing = PropertyLayout.Spacing };

    private FrameworkElement Item(InspectorItem item)
    {
        Tool tool = _plan.Tool;
        switch (item)
        {
            case InspectorItem.Colors:
                var colors = new PropertyRow { Spacing = 10 };
                colors.Children.Add(Left(Titled(Well(fill: false))));
                if (tool.SupportsFill)
                {
                    colors.Children.Add(Left(Titled(Well(fill: true))));
                }
                return colors;
            case InspectorItem.StrokeColor:
                return Titled(Well(fill: false));
            case InspectorItem.FillColor:
                return Titled(Well(fill: true));
            case InspectorItem.Width:
                return Block(InspectorPlan.WidthTitle(tool), SliderOnly(StyleParameter.Width));
            case InspectorItem.Opacity:
                return Value(StyleParameter.Opacity, _compact ? 64 : 90, showLabel: true);
            case InspectorItem.Pattern:
                return Block("Pattern",
                    Choices(PropertyChoice.Pattern, "Pattern",
                        InspectorPlan.PatternChoices.Select(choice => (choice.Value, choice.Name, InspectorSample.ForPattern(choice.Value))),
                        style => style.Pattern, pattern => _model.ApplyStyle((ref CoreStyle style) => style.Pattern = pattern)));
            case InspectorItem.Roughness:
                return Block("Roughness",
                    Choices(PropertyChoice.Roughness, "Roughness",
                        InspectorPlan.RoughnessChoices.Select(choice => (choice.Value, choice.Name, InspectorSample.Roughness(choice.Value))),
                        style => style.Roughness, roughness => _model.ApplyStyle((ref CoreStyle style) => style.Roughness = roughness)));
            case InspectorItem.FillStyle:
                return Block("Fill style",
                    Choices(PropertyChoice.FillStyle, "Fill style",
                        InspectorPlan.FillStyleChoices.Select(choice => (choice.Value, choice.Name, InspectorSample.Fill(choice.Value))),
                        style => style.FillPattern, pattern => _model.ApplyStyle((ref CoreStyle style) => style.FillPattern = pattern)));
            case InspectorItem.Edges:
                return Block("Edges", _plan.ShowsAllChoices(PropertyChoice.Edges)
                    ? Options(new[] { (false, "Sharp corners", InspectorSample.Edges(false)), (true, "Rounded corners", InspectorSample.Edges(true)) },
                        style => style.CornerRadius > 0,
                        rounded => _model.ApplyStyle((ref CoreStyle style) => style.CornerRadius = rounded ? InspectorPlan.RoundedCornerRadius : 0))
                    : Toggle("Rounded corners", InspectorSample.Edges(true), style => style.CornerRadius > 0,
                        "Rounded corners are on. Click for sharp corners.", "Sharp corners. Click to round them.",
                        rounded => (ref CoreStyle style) => style.CornerRadius = rounded ? 0 : InspectorPlan.RoundedCornerRadius));
            case InspectorItem.GridSize:
                var grid = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
                grid.Children.Add(Value(StyleParameter.GridRows, 0, showLabel: true));
                grid.Children.Add(Value(StyleParameter.GridColumns, 0, showLabel: true));
                return grid;
            case InspectorItem.Axes:
                return Axes();
            case InspectorItem.Pressure:
                return Block("Pressure", _plan.ShowsAllChoices(PropertyChoice.Pressure)
                    ? Options(new[] { (false, "Constant pressure", InspectorSample.Pressure(false)), (true, "Ink pressure", InspectorSample.Pressure(true)) },
                        style => style.Pressure, pressure => _model.ApplyStyle((ref CoreStyle style) => style.Pressure = pressure))
                    : Toggle("Ink pressure", InspectorSample.Pressure(true), style => style.Pressure,
                        "Ink pressure is on: lines get thinner and thicker. Click for one width.",
                        "One width all along. Click for ink pressure.",
                        pressure => (ref CoreStyle style) => style.Pressure = !pressure));
            case InspectorItem.SmartDraw:
                var smart = new SmartDrawToggle(_model, 36);
                smart.Root.Click += (_, _) => _host.Interacted();
                _updates.Add(smart.Update);
                return Block("Smart Draw", smart.Root);
            case InspectorItem.Caption:
                TextBlock caption = PropertyMetrics.Text(_plan.Caption ?? string.Empty, 10, Secondary);
                if (_compact && tool == Tool.Laser)
                {
                    // fixedSize(horizontal: !compact, vertical: true): one line on the row, wrapped in the column.
                    caption.TextWrapping = TextWrapping.Wrap;
                    caption.MinWidth = 0;
                }
                return caption;
            case InspectorItem.Font:
                if (_plan.ShowsAllChoices(PropertyChoice.Font))
                {
                    var fonts = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
                    fonts.Children.Add(Left(FontChoice("System", "system", SystemFont)));
                    fonts.Children.Add(Left(FontChoice("Mono", "monospace", MonoFont)));
                    return Block("Font", fonts);
                }
                return Block("Font", Cycle("Font",
                    [
                        ("system", "System", () => Glyph("Aa", 16, SystemFont)),
                        ("monospace", "Mono", () => Glyph("Aa", 16, MonoFont)),
                    ],
                    style => string.Equals(style.FontName, "monospace", StringComparison.Ordinal) ? "monospace" : "system",
                    name => _model.ApplyStyle((ref CoreStyle style) => style.FontName = name)));
            case InspectorItem.TextSize:
                return Block("Size", SliderOnly(StyleParameter.TextSize));
            case InspectorItem.Weight:
                (ParityButton bold, Action<bool> boldSelect) = Choice("Bold", Symbol("bold", 17));
                AutomationProperties.SetAutomationId(bold, "bold");
                bold.Click += (_, _) => Apply((ref CoreStyle style) => style.Bold = !style.Bold);
                _updates.Add(state =>
                {
                    boldSelect(state.Style.Bold);
                    bold.AccessibleValue = state.Style.Bold ? "On" : "Off";
                });
                return Block("Weight", bold);
            case InspectorItem.Alignment:
                if (_plan.ShowsAllChoices(PropertyChoice.Alignment))
                {
                    var alignments = new MacStack { Orientation = Orientation.Horizontal, Spacing = 5 };
                    foreach (CoreTextAlignment alignment in CoreTextAlignment.AllCases)
                    {
                        string symbol = AlignmentSymbol(alignment);
                        (ParityButton button, Action<bool> select) = Choice($"Align {alignment.RawValue}", Symbol(symbol, 16));
                        AutomationProperties.SetAutomationId(button, symbol);
                        button.Click += (_, _) => Apply((ref CoreStyle style) => style.Alignment = alignment);
                        _updates.Add(state => select(state.Style.Alignment == alignment));
                        alignments.Children.Add(Left(button));
                    }
                    return Block("Alignment", alignments);
                }
                return Block("Alignment", Cycle("Alignment",
                    [.. InspectorPlan.AlignmentChoices.Select(choice =>
                        (choice.Value, choice.Name, (Func<UIElement>)(() => Symbol(AlignmentSymbol(choice.Value), 15))))],
                    style => style.Alignment, alignment => _model.ApplyStyle((ref CoreStyle style) => style.Alignment = alignment)));
            default:
                throw new ArgumentOutOfRangeException(nameof(item), item, "Unknown property block.");
        }
    }

    private static readonly FontFamily SystemFont = new("Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    private static string AlignmentSymbol(CoreTextAlignment alignment) => alignment switch
    {
        CoreTextAlignment.Left => "text.alignleft",
        CoreTextAlignment.Center => "text.aligncenter",
        _ => "text.alignright",
    };

    /// <summary>Marks an item whose control sits below its name, so the row lines it up by its control.</summary>
    private static FrameworkElement Titled(FrameworkElement element)
    {
        PropertyRow.SetTitled(element, true);
        return element;
    }

    /// <summary>
    /// A property with a few choices (Mac showsAllChoices): all of them side by side (SampleOptions), or one button that
    /// steps through them (CycleOptions).
    /// </summary>
    private FrameworkElement Choices<T>(PropertyChoice choice, string name, IEnumerable<(T Value, string Name, InspectorSample Sample)> values,
                                        Func<CoreStyle, T> current, Action<T> choose)
    {
        if (_plan.ShowsAllChoices(choice))
        {
            return Options(values, current, choose);
        }
        return Cycle(name, [.. values.Select(value => (value.Value, value.Name, (Func<UIElement>)(() => Sample(value.Sample))))], current, choose);
    }

    /// <summary>
    /// Port of CycleOptions: one 36-point button for a few choices, such as Solid, Dashed and Dotted. It shows the current
    /// choice with dots for where it is, and moves to the next choice on each click; its context menu reaches every
    /// choice. UI Automation reads the current choice as its value.
    /// </summary>
    private FrameworkElement Cycle<T>(string name, IReadOnlyList<(T Value, string Label, Func<UIElement> Face)> values,
                                      Func<CoreStyle, T> current, Action<T> choose)
    {
        var face = new ContentControl
        {
            IsTabStop = false,
            IsHitTestVisible = false,
            Width = 36,
            Height = 36,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var dots = new MacStack
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(dots, AccessibilityView.Raw);
        var marks = new List<Ellipse>();
        foreach ((T _, string _, Func<UIElement> _) in values)
        {
            var dot = new Ellipse { Width = 3.5, Height = 3.5 };
            marks.Add(dot);
            dots.Children.Add(dot);
        }
        var content = new Grid();
        content.Children.Add(face);
        content.Children.Add(dots);
        (ParityButton button, Action<bool> _) = Choice(name, content, choice: false);
        // The faces slide inside the button (Mac .clipped()).
        content.Clip = new RectangleGeometry { Rect = new WinRect(0, 0, 36, 36) };
        int index = 0;
        button.Click += (_, _) =>
        {
            _host.Interacted();
            choose(values[(index + 1) % values.Count].Value);
        };
        // Mac accessibilityAdjustableAction: Up goes to the next choice and Down to the one before, for screen readers and
        // the keyboard.
        button.KeyDown += (_, args) =>
        {
            int step = args.Key switch
            {
                Windows.System.VirtualKey.Up => 1,
                Windows.System.VirtualKey.Down => values.Count - 1,
                _ => 0,
            };
            if (step == 0)
            {
                return;
            }
            args.Handled = true;
            _host.Interacted();
            choose(values[(index + step) % values.Count].Value);
        };
        var menu = new MenuFlyout();
        var items = new List<ToggleMenuFlyoutItem>();
        for (int position = 0; position < values.Count; position++)
        {
            int target = position;
            var entry = new ToggleMenuFlyoutItem { Text = values[position].Label };
            entry.Click += (_, _) =>
            {
                _host.Interacted();
                if (target != index)
                {
                    choose(values[target].Value);
                }
            };
            menu.Items.Add(entry);
            items.Add(entry);
        }
        button.ContextFlyout = menu;
        int shown = -1;
        _updates.Add(state =>
        {
            T now = current(state.Style);
            index = 0;
            for (int position = 0; position < values.Count; position++)
            {
                if (EqualityComparer<T>.Default.Equals(values[position].Value, now))
                {
                    index = position;
                    break;
                }
            }
            for (int position = 0; position < items.Count; position++)
            {
                items[position].IsChecked = position == index;
            }
            if (shown == index)
            {
                return;
            }
            bool changed = shown != -1;
            shown = index;
            face.Content = values[index].Face();
            if (changed && face.Content is UIElement arrived)
            {
                CycleSlide.Start(arrived);
            }
            for (int position = 0; position < marks.Count; position++)
            {
                marks[position].Fill = position == index ? AppResources.Brush("AnnotatorAccentBrush")
                    : AppResources.Brush("AnnotatorControlStrokeBrush");
            }
            string next = values[(index + 1) % values.Count].Label;
            button.AccessibleValue = values[index].Label;
            ToolbarParts.Help(button, InspectorPlan.CycleHint(next), InspectorPlan.CycleHelp(name, values[index].Label, next));
        });
        return button;
    }

    /// <summary>
    /// Port of InspectorToggle: an on and off button for one feature, such as a curved arrow. The accent outline shows
    /// it's on; UI Automation reads On or Off. <paramref name="toggle"/> makes the change from the shown value, so a mixed
    /// selection takes one value.
    /// </summary>
    private FrameworkElement Toggle(string name, InspectorSample sample, Func<CoreStyle, bool> isOn, string onHelp, string offHelp,
                                    Func<bool, StyleMutation> toggle)
    {
        (ParityButton button, Action<bool> select) = Choice(name, Sample(sample));
        button.Click += (_, _) => Apply(toggle(isOn(_model.Snapshot.Style)));
        _updates.Add(state =>
        {
            bool on = isOn(state.Style);
            select(on);
            button.AccessibleValue = on ? "On" : "Off";
            ToolbarParts.Help(button, on ? onHelp : offHelp);
        });
        return button;
    }

    /// <summary>Mac widthControls and the text size: just the slider with its value beside it, in a 36-point frame.</summary>
    private FrameworkElement SliderOnly(StyleParameter parameter)
    {
        var control = new ValueControl(_model, parameter, _plan.Tool, 96, showLabel: false, _host.BeginTyping, _host.EndTyping,
            small: true, showsField: false);
        _updates.Add(control.Update);
        var frame = new Grid { Height = PropertyRow.ControlHeight };
        control.Root.VerticalAlignment = VerticalAlignment.Center;
        frame.Children.Add(control.Root);
        return frame;
    }

    /// <summary>Applies a style change from a button: one Undo step for the selection, and the tool's saved style.</summary>
    private void Apply(StyleMutation mutation)
    {
        _host.Interacted();
        _model.ApplyStyle(mutation);
    }

    /// <summary>
    /// Port of PropertyBlock: the property's name, 6 points above the content. The name is plain text, 11-point medium in
    /// the secondary color like the color pickers' names, so every name on the row starts at the block's leading edge.
    /// </summary>
    private static MacStack Block(string name, params FrameworkElement[] content)
    {
        var block = new MacStack { Spacing = 6 };
        PropertyRow.SetTitled(block, true);
        block.Children.Add(Left(PropertyMetrics.Text(name, 11, Secondary, FontWeights.Medium)));
        foreach (FrameworkElement part in content)
        {
            block.Children.Add(Left(part));
        }
        return block;
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

    private static readonly SceneBuilder s_previewSamples = new(NoText.Instance);

    /// <summary>A sample's picture for previews outside the toolbar (Settings > Toolbar > Your properties).</summary>
    internal static Canvas SamplePicture(InspectorSample sample, Brush ink) =>
        SampleArt.Draw(sample.Draw(new RGBA(0, 0, 0), s_previewSamples), ink);

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

    /// <summary>
    /// Mac arrowControls: Start, End, the Curved arrow switch (or both arrow types) and the Head size slider, read as the
    /// group "Arrow properties".
    /// </summary>
    private FrameworkElement ArrowControls()
    {
        var group = new UnionGroup("Arrow properties");
        Panel layout = Layout();
        layout.Children.Add(Left(Block("Start", ArrowEnd(start: true))));
        layout.Children.Add(Left(Block("End", ArrowEnd(start: false))));
        FrameworkElement type = _plan.ShowsAllChoices(PropertyChoice.ArrowType)
            ? Options(new[] { (false, "Straight arrow", InspectorSample.Route(false)), (true, "Curved arrow", InspectorSample.Route(true)) },
                style => style.Curved, curved => _model.ApplyStyle((ref CoreStyle style) => style.Curved = curved))
            : Toggle("Curved arrow", InspectorSample.Route(true), style => style.Curved,
                "Curved arrow is on. Click for a straight arrow.", "Straight arrow. Click to curve it.",
                curved => (ref CoreStyle style) => style.Curved = !curved);
        layout.Children.Add(Left(Block(_plan.ArrowTypeTitle, type)));
        var slider = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ParitySlider head = RawSlider(86, 0.5, 2, style => style.HeadScale ?? 1,
            (double value, ref CoreStyle style) => style.HeadScale = value, out FrameworkElement band);
        AutomationProperties.SetName(head, "Arrowhead size");
        slider.Children.Add(band);
        TextBlock percent = PropertyMetrics.Text(string.Empty, 10, Secondary);
        percent.VerticalAlignment = VerticalAlignment.Center;
        percent.HorizontalAlignment = HorizontalAlignment.Left;
        // frame(minWidth: 34, alignment: .leading): UI Automation reads the words' own frame, not the 34 points.
        var percentFrame = new Grid { MinWidth = 34, VerticalAlignment = VerticalAlignment.Center };
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
        var frame = new Grid { Height = PropertyRow.ControlHeight };
        frame.Children.Add(slider);
        layout.Children.Add(Left(Block("Head size", frame)));
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
    /// The Mac's "Image opacity" block: a 180-point slider with the percentage beside it, then how to edit the image.
    /// </summary>
    private FrameworkElement ImageOpacity()
    {
        Panel layout = Layout();
        var slider = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6 };
        ParitySlider opacity = RawSlider(180, 0, 1, style => style.Opacity, (double value, ref CoreStyle style) => style.Opacity = value,
            out FrameworkElement band);
        AutomationProperties.SetName(opacity, "Image opacity");
        slider.Children.Add(band);
        TextBlock percent = ToolbarParts.Text(string.Empty, 10, "AnnotatorPrimaryText");
        percent.VerticalAlignment = VerticalAlignment.Center;
        _updates.Add(state => percent.Text = $"{(int)(state.Style.Opacity * 100)}%");
        slider.Children.Add(percent);
        layout.Children.Add(Left(slider));
        layout.Children.Add(Left(PropertyMetrics.Text(InspectorPlan.ImageHint, 10, Secondary)));
        return Block("Image opacity", layout);
    }

    /// <summary>Port of CoverPropertiesView in the panel: the effect choice and its strength, or the note for a selected solid cover.</summary>
    private FrameworkElement CoverControls() =>
        EffectControls.CoverProperties(_model, _plan.Tool, _plan.Context is not null, _compact, showsSlider: true,
            (width, showLabel) => Value(StyleParameter.Effect, width, showLabel), _updates, _host.Interacted);

    /// <summary>
    /// The axes' "Centered axes" switch and tick count side by side, 12 points apart; the tick count's help explains the
    /// spacing (Mac ContextualInspector, tool == .axes).
    /// </summary>
    private FrameworkElement Axes()
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 12 };
        var centered = new CheckBox { Content = "Centered axes", FontSize = 10, MinHeight = 0, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(centered, "Centered axes with negative directions");
        ToolbarParts.Help(centered, "Centered axes also go in the negative directions.");
        centered.Click += (_, _) =>
        {
            bool value = centered.IsChecked == true;
            Apply((ref CoreStyle style) => style.NegativeAxes = value);
        };
        _updates.Add(state => centered.IsChecked = state.Style.NegativeAxes ?? false);
        row.Children.Add(centered);
        FrameworkElement ticks = Value(StyleParameter.AxisTickCount, 0, showLabel: true);
        _updates.Add(state => ToolbarParts.Help(ticks, InspectorPlan.AxesCaption(state.Style.AxisTickCount)));
        row.Children.Add(ticks);
        return row;
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
