using Annotator.App.Pickers;
using Annotator.Workspace.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Annotator.App.Library;

/// <summary>
/// The Tools window's "Tool category" picker (Mac Picker with .pickerStyle(.segmented)): a 24-point track with the five
/// sections, the selected one on a darker rounded segment and 1-point separators between unselected neighbors. Segments
/// are as wide as <see cref="ToolLibraryLayout.Segments"/> says for the picker's width. Each segment is a radio button in a
/// group with the Selection pattern; Left and Right choose the neighboring section.
/// </summary>
internal sealed partial class SegmentStrip : Panel
{
    private const double SeparatorHeight = 13.5;
    private readonly Border _track = new() { CornerRadius = new CornerRadius(6), Background = PickerColors.ControlFill, IsHitTestVisible = false };
    private readonly Border _selection = new() { CornerRadius = new CornerRadius(5), Background = PickerColors.SegmentSelected, IsHitTestVisible = false };
    private readonly Rectangle[] _separators;
    private readonly SegmentButton[] _segments;
    private readonly Action<ToolLibrarySection> _choose;
    private int _selected;

    public SegmentStrip(SegmentGroup group, Action<ToolLibrarySection> choose)
    {
        _choose = choose;
        Children.Add(_track);
        Children.Add(_selection);
        ToolLibrarySection[] sections = [.. ToolLibrarySection.AllCases];
        _separators = new Rectangle[sections.Length - 1];
        for (int index = 0; index < _separators.Length; index++)
        {
            _separators[index] = new Rectangle { Width = 1, Height = SeparatorHeight, Fill = LibraryColors.SegmentSeparator, IsHitTestVisible = false };
            Children.Add(_separators[index]);
        }
        _segments = new SegmentButton[sections.Length];
        for (int index = 0; index < sections.Length; index++)
        {
            ToolLibrarySection section = sections[index];
            TextBlock label = LibraryParts.Label(section.RawValue, 13, "AnnotatorPrimaryText");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var segment = new SegmentButton
            {
                Style = AppResources.Style("AnnotatorPlainButton"),
                CornerRadius = new CornerRadius(5),
                Content = label,
                Group = group,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            AutomationProperties.SetName(segment, section.RawValue);
            segment.Chosen += () => _choose(section);
            segment.KeyDown += OnKeyDown;
            _segments[index] = segment;
            group.Segments.Add(segment);
            Children.Add(segment);
        }
        // Tab reaches the picker once; the arrow keys move between its sections, as on the Mac.
        group.TabFocusNavigation = KeyboardNavigationMode.Once;
    }

    /// <summary>Shows <paramref name="section"/> as the chosen one.</summary>
    public void Select(ToolLibrarySection section)
    {
        _selected = ToolLibrarySection.AllCases.IndexOf(section);
        for (int index = 0; index < _segments.Length; index++)
        {
            _segments[index].IsChosen = index == _selected;
        }
        InvalidateArrange();
    }

    /// <summary>Moves keyboard focus to the chosen segment.</summary>
    public void FocusSelected() => _segments[_selected].Focus(FocusState.Keyboard);

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, ToolLibraryLayout.PickerHeight));
        }
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, ToolLibraryLayout.PickerHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The strip is as wide as the segments; laid out for its own width less the bezel, they fill it.
        var segments = ToolLibraryLayout.Segments(finalSize.Width - ToolLibraryLayout.SegmentBezel * 2);
        double origin = segments[0].X;
        double height = ToolLibraryLayout.PickerHeight;
        _track.Arrange(new Rect(0, 0, finalSize.Width, height));
        for (int index = 0; index < _segments.Length; index++)
        {
            _segments[index].Arrange(new Rect(segments[index].X - origin, 0, segments[index].Width, height));
        }
        _selection.Arrange(new Rect(segments[_selected].X - origin, 0, segments[_selected].Width, height));
        for (int index = 0; index < _separators.Length; index++)
        {
            // A separator sits at the start of the segment after it, between two unselected segments.
            bool shown = index != _selected && index + 1 != _selected;
            _separators[index].Opacity = shown ? 1 : 0;
            _separators[index].Arrange(new Rect(segments[index + 1].X - origin, (height - SeparatorHeight) / 2, 1, SeparatorHeight));
        }
        return finalSize;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        int step = args.Key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }
        args.Handled = true;
        int next = Math.Clamp(_selected + step, 0, _segments.Length - 1);
        if (next != _selected)
        {
            _choose(ToolLibrarySection.AllCases[next]);
            _segments[next].Focus(FocusState.Keyboard);
        }
    }
}
