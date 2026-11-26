using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Annotator.App.Toolbar;

/// <summary>What a property's menu shows: its name, each choice's name and picture, which one is current, and what a choice does.</summary>
internal sealed record ChoicesRequest(string Name, IReadOnlyList<(string Label, Func<UIElement> Face)> Choices, int Selected,
                                      Action<int> Choose);

/// <summary>
/// A property's menu (Mac ChoicePalette in NativeInspectorComponents.swift): a column of every choice drawn as it looks,
/// 36-point buttons 5 points apart with 8 points around them; the current one has the accent outline. A choice applies
/// and closes the menu. Up and Down move between the choices, wrapping at the ends; Escape closes without a change.
/// </summary>
internal sealed class ChoicePalette : PopoverWindow
{
    private readonly List<ParityButton> _choices = [];
    private readonly int _selected;

    public ChoicePalette(WindowRegistry registry, ChoicesRequest request)
        : base(registry, request.Name)
    {
        _selected = request.Selected;
        var column = new MacStack { Spacing = 5 };
        AutomationProperties.SetName(column, request.Name);
        for (int position = 0; position < request.Choices.Count; position++)
        {
            int target = position;
            (ParityButton button, Action<bool> select) = PropertiesPanel.Choice(request.Choices[position].Label,
                request.Choices[position].Face());
            select(position == request.Selected);
            button.Click += (_, _) =>
            {
                Dismiss(restoreFocus: true);
                request.Choose(target);
            };
            _choices.Add(button);
            column.Children.Add(button);
        }
        // The 1-point outline is part of the Mac's 8-point padding.
        Surface = new Border
        {
            Style = AppResources.Style("AnnotatorToolbarSurface"),
            Padding = new Thickness(7),
            Child = column,
        };
    }

    protected override void FocusFirst() =>
        (_selected >= 0 && _selected < _choices.Count ? _choices[_selected] : _choices.FirstOrDefault())?.Focus(FocusState.Programmatic);

    /// <summary>Mac onMoveCommand: up and down step by one, wrapping around the choices.</summary>
    protected override bool OnKey(VirtualKey key, bool control)
    {
        int delta = key switch
        {
            VirtualKey.Up => -1,
            VirtualKey.Down => 1,
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
            current = Math.Max(0, _selected);
        }
        _choices[(current + delta + _choices.Count) % _choices.Count].Focus(FocusState.Keyboard);
        return true;
    }
}
