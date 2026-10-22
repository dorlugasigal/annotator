using System.Runtime.InteropServices.Marshalling;
using Annotator.Platform.Accessibility;
using Annotator.Platform.Displays;

namespace Annotator.Platform.Tests;

/// <summary>
/// The overlay's UI Automation provider (WIN-A11Y-01), called the way UI Automation calls it but without a client: the
/// canvas group with its value and help, and the Use apps button inside it.
/// </summary>
public sealed class OverlayAccessibilityTests
{
    private const int NameProperty = 30005;
    private const int ControlTypeProperty = 30003;
    private const int HelpTextProperty = 30013;
    private const int HasKeyboardFocusProperty = 30008;
    private const int IsKeyboardFocusableProperty = 30009;
    private const int ValuePattern = 10002;
    private const int InvokePattern = 10000;

    [Fact]
    public unsafe void TheCanvasIsAGroupWithItsSummaryAndAnInvokableButton()
    {
        int invoked = 0;
        var tree = new OverlayAccessibleTree(
            new OverlayAccessibleNode(OverlayControlType.Group, "Annotation canvas", new PixelRect(0, 0, 1125, 710))
            {
                Value = "7 objects, 1 selected. Drawing.",
                HelpText = "Select an object, then use arrow keys to move it.",
            },
            [new OverlayAccessibleNode(OverlayControlType.Button, "Use apps", new PixelRect(20, 18, 208, 52)) { Invoke = () => invoked++ }]);
        using var accessibility = new OverlayAccessibility(0, new PixelRect(100, 200, 1225, 910), tree);
        var root = (IRawElementProviderSimple)accessibility.RootForTesting;

        Assert.Equal("Annotation canvas", Property(root, NameProperty).As<string>());
        Assert.Equal((int)OverlayControlType.Group, Property(root, ControlTypeProperty).As<int>());
        Assert.Equal("Select an object, then use arrow keys to move it.", Property(root, HelpTextProperty).As<string>());
        Assert.Equal("7 objects, 1 selected. Drawing.", ((IValueProvider)root).get_Value());
        Assert.Equal(1, ((IValueProvider)root).get_IsReadOnly());
        Assert.Equal(new UiaRect(100, 200, 1125, 710), ((IRawElementProviderFragment)root).get_BoundingRectangle());

        IRawElementProviderFragment button = ((IRawElementProviderFragment)root).Navigate(NavigateDirection.FirstChild)!;
        var simple = (IRawElementProviderSimple)button;
        Assert.Equal("Use apps", Property(simple, NameProperty).As<string>());
        Assert.Equal((int)OverlayControlType.Button, Property(simple, ControlTypeProperty).As<int>());
        Assert.Equal(new UiaRect(120, 218, 188, 34), button.get_BoundingRectangle());
        Assert.Same(root, button.Navigate(NavigateDirection.Parent));
        Assert.Null(button.Navigate(NavigateDirection.NextSibling));
        Assert.Same(button, ((IRawElementProviderFragmentRoot)root).ElementProviderFromPoint(150, 230));
        Assert.Same(root, ((IRawElementProviderFragmentRoot)root).ElementProviderFromPoint(500, 500));
        Assert.Equal(0, simple.GetPatternProvider(ValuePattern));
        Assert.Equal(0, root.GetPatternProvider(InvokePattern));
        ((IInvokeProvider)button).Invoke();
        Assert.Equal(1, invoked);

        accessibility.Tree = tree with { Children = [] };
        Assert.Null(((IRawElementProviderFragment)root).Navigate(NavigateDirection.FirstChild));
        Assert.Throws<InvalidOperationException>(() => ((IInvokeProvider)button).Invoke());
    }

    [Fact]
    public unsafe void TheTextEditorIsAFocusedEditableTextField()
    {
        string? typed = null;
        var tree = new OverlayAccessibleTree(
            new OverlayAccessibleNode(OverlayControlType.Group, "Annotation canvas", new PixelRect(0, 0, 1125, 710)),
            [
                new OverlayAccessibleNode(OverlayControlType.Button, "Use apps", new PixelRect(20, 18, 208, 52)),
                new OverlayAccessibleNode(OverlayControlType.Edit, "Annotation text. Escape or Ctrl+Enter finishes editing.", new PixelRect(600, 412, 751, 462))
                {
                    Value = "New note",
                    IsFocused = true,
                    SetValue = value => typed = value,
                },
            ]);
        using var accessibility = new OverlayAccessibility(0, new PixelRect(100, 200, 1225, 910), tree);
        var root = (IRawElementProviderSimple)accessibility.RootForTesting;
        IRawElementProviderFragment editor = ((IRawElementProviderFragmentRoot)root).GetFocus()!;
        var simple = (IRawElementProviderSimple)editor;

        Assert.Equal((int)OverlayControlType.Edit, Property(simple, ControlTypeProperty).As<int>());
        Assert.Equal("Annotation text. Escape or Ctrl+Enter finishes editing.", Property(simple, NameProperty).As<string>());
        Assert.True(Property(simple, HasKeyboardFocusProperty).As<bool>());
        Assert.True(Property(simple, IsKeyboardFocusableProperty).As<bool>());
        Assert.Equal(new UiaRect(700, 612, 151, 50), editor.get_BoundingRectangle());
        Assert.NotEqual(0, simple.GetPatternProvider(ValuePattern));
        Assert.Equal("New note", ((IValueProvider)editor).get_Value());
        Assert.Equal(0, ((IValueProvider)editor).get_IsReadOnly());
        ((IValueProvider)editor).SetValue("Replaced");
        Assert.Equal("Replaced", typed);
        Assert.Same(editor, ((IRawElementProviderFragment)root).Navigate(NavigateDirection.LastChild));
        // Without a listening client the events do nothing.
        accessibility.RaiseFocusChanged();
        accessibility.RaiseValueChanged("New not");

        accessibility.Tree = tree with { Children = [tree.Children[0]] };
        Assert.Null(((IRawElementProviderFragmentRoot)root).GetFocus());
        Assert.Throws<InvalidOperationException>(() => ((IValueProvider)editor).SetValue("Gone"));
    }

    private static unsafe ComVariant Property(IRawElementProviderSimple provider, int property)
    {
        ComVariant value = default;
        provider.GetPropertyValue(property, &value);
        return value;
    }
}
