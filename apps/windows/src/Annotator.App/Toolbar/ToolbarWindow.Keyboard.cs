using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Toolbar;

/// <summary>
/// The toolbar from the keyboard (WIN-A11Y-02, WIN-A11Y-04). The toolbar never activates on a click (WIN-OVR-03), so the
/// keyboard reaches it through the canvas's Focus drawing tools (Mac focusDrawingTools, the canvas menu's last item with
/// Shift+F10 or the Menu key): the toolbar becomes the active window with the keyboard on its first control, Tab moves
/// through its controls in reading order, and Escape gives the keyboard back to the window that had it. Escape always
/// leaves the toolbar: a value field first ends its edit, and with no window to return to it releases drawing input.
/// </summary>
internal sealed partial class ToolbarWindow
{
    /// <summary>The window that had the keyboard before Focus drawing tools gave it to the toolbar.</summary>
    private HWND _keyboardReturn;

    /// <summary>
    /// Mac focusDrawingTools: the toolbar takes the keyboard with its first control focused, so a focus ring shows on it.
    /// Returns false when the toolbar is hidden.
    /// </summary>
    public bool FocusTools()
    {
        if (!_shown || _closing)
        {
            return false;
        }
        HWND foreground = PInvoke.GetForegroundWindow();
        if (foreground != Hwnd)
        {
            _keyboardReturn = foreground;
        }
        PInvoke.SetForegroundWindow(Hwnd);
        if (FocusManager.FindFirstFocusableElement(_root) is UIElement first)
        {
            first.Focus(FocusState.Keyboard);
        }
        return true;
    }

    /// <summary>Escape that no control used, and the end of a keyboard visit when the toolbar stops being active.</summary>
    private void WireKeyboard()
    {
        _root.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && !args.Handled)
            {
                args.Handled = true;
                LeaveKeyboard();
            }
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                _keyboardReturn = HWND.Null;
            }
        };
    }

    /// <summary>
    /// Gives the keyboard back to the window that had it (the canvas after Focus drawing tools, the person's app after typing
    /// in a field). Without one, Escape releases drawing input as it does on a canvas (WIN-A11Y-04).
    /// </summary>
    private void LeaveKeyboard()
    {
        HWND target = !_keyboardReturn.IsNull ? _keyboardReturn : _typingReturn;
        _keyboardReturn = HWND.Null;
        _typingReturn = HWND.Null;
        if (!target.IsNull && PInvoke.IsWindow(target) && PInvoke.SetForegroundWindow(target))
        {
            return;
        }
        _model.UseApps();
    }

    /// <summary>The toolbar hides: a keyboard visit ends with it.</summary>
    private void ReturnKeyboard()
    {
        HWND target = _keyboardReturn;
        _keyboardReturn = HWND.Null;
        if (!target.IsNull && PInvoke.GetForegroundWindow() == Hwnd && PInvoke.IsWindow(target))
        {
            PInvoke.SetForegroundWindow(target);
        }
    }
}
