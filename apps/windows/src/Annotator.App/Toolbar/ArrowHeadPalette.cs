using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using CoreStyle = Annotator.Core.Style;

namespace Annotator.App.Toolbar;

/// <summary>
/// The arrowhead choices for the start or the end of an arrow (Mac ArrowHeadPalette in NativeArrowHeadPicker.swift): the
/// title, the ten shapes and the six relationships in rows of four, and the current choice's name. A choice applies to
/// the arrow tool and the selected arrows and closes the choices. The arrow keys move between the choices, wrapping at the
/// ends; Escape closes without a change.
/// </summary>
internal sealed class ArrowHeadPalette : PopoverWindow
{
    private const double Gap = 7;
    private readonly WorkspaceModel _model;
    private readonly bool _start;
    private readonly List<ParityButton> _choices = [];
    private ParityButton? _selected;

    public ArrowHeadPalette(WindowRegistry registry, WorkspaceModel model, bool start)
        : base(registry, start ? "Start arrowhead" : "End arrowhead")
    {
        _model = model;
        _start = start;
        CoreStyle style = model.Snapshot.Style;
        ArrowHead selected = start ? style.StartHead : style.EndHead;
        var ink = new SolidColorBrush(PropertiesInk.Color());
        var samples = new SceneBuilder(new SampleMeasurer());
        var content = new MacStack { Spacing = 12 };
        content.Children.Add(Left(PropertyMetrics.Text(start ? "Start arrowhead" : "End arrowhead", 13, "AnnotatorPrimaryText", FontWeights.SemiBold)));
        content.Children.Add(Left(Choices(ArrowHead.ShapeChoices, selected, ink, samples)));
        content.Children.Add(Left(PropertyMetrics.Text("Relationships", 10, "AnnotatorSecondaryText")));
        content.Children.Add(Left(Choices(ArrowHead.RelationshipChoices, selected, ink, samples)));
        content.Children.Add(Left(PropertyMetrics.Text(selected.Label, 10, "AnnotatorSecondaryText")));
        // The 1-point outline is part of the Mac's 14-point margin.
        Surface = new Border
        {
            Style = AppResources.Style("AnnotatorToolbarSurface"),
            Padding = new Thickness(13),
            Child = content,
        };
    }

    protected override void FocusFirst() => (_selected ?? _choices.FirstOrDefault())?.Focus(FocusState.Programmatic);

    /// <summary>Mac onMoveCommand: left and right step by one, up and down by a row of four, wrapping around all choices.</summary>
    protected override bool OnKey(VirtualKey key, bool control)
    {
        int delta = key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            VirtualKey.Up => -4,
            VirtualKey.Down => 4,
            _ => 0,
        };
        if (delta == 0 || control || _choices.Count == 0)
        {
            return false;
        }
        int current = Content?.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is ParityButton focused
            ? _choices.IndexOf(focused) : -1;
        if (current < 0)
        {
            current = _selected is { } selected ? _choices.IndexOf(selected) : 0;
        }
        _choices[(current + delta + _choices.Count) % _choices.Count].Focus(FocusState.Keyboard);
        return true;
    }

    private static FrameworkElement Left(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        return element;
    }

    /// <summary>A grid of four 36-point columns, 7 points apart in both directions (Mac LazyVGrid, a grid in UI Automation).</summary>
    private UnionGroup Choices(ValueList<ArrowHead> heads, ArrowHead selected, Brush ink, SceneBuilder samples)
    {
        var rows = new MacStack { Spacing = Gap };
        var cells = new List<UIElement>();
        MacStack? row = null;
        for (int index = 0; index < heads.Count; index++)
        {
            if (index % 4 == 0)
            {
                row = new MacStack { Orientation = Orientation.Horizontal, Spacing = Gap, HorizontalAlignment = HorizontalAlignment.Left };
                rows.Children.Add(row);
            }
            ArrowHead head = heads[index];
            Canvas picture = SampleArt.Draw(InspectorSample.ForHead(head, _start).Draw(new RGBA(0, 0, 0), samples), ink);
            (ParityButton button, Action<bool> select) = PropertiesPanel.Choice($"{(_start ? "Start" : "End")}: {head.Label}", picture);
            select(head == selected);
            if (head == selected)
            {
                _selected = button;
            }
            button.Click += (_, _) => Choose(head);
            _choices.Add(button);
            cells.Add(button);
            row!.Children.Add(button);
        }
        var group = new UnionGroup(string.Empty, cells, columns: 4);
        group.Children.Add(rows);
        return group;
    }

    private void Choose(ArrowHead head)
    {
        bool start = _start;
        _model.ApplyStyle((ref CoreStyle style) =>
        {
            if (start)
            {
                style.StartHead = head;
            }
            else
            {
                style.EndHead = head;
            }
        });
        Dismiss(restoreFocus: true);
    }

    /// <summary>Arrowhead samples draw no text.</summary>
    private sealed class SampleMeasurer : ITextMeasurer
    {
        public Annotator.Core.Geometry.Size Measure(string text, TextFont font) =>
            throw new InvalidOperationException("Arrowhead samples draw no text.");

        public FontLineMetrics LineMetrics(TextFont font) => throw new InvalidOperationException("Arrowhead samples draw no text.");
    }
}
