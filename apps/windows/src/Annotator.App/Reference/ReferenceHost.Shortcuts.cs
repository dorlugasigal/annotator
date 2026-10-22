using Annotator.App.Settings;
using Annotator.Workspace;
using Annotator.Workspace.Shortcuts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Annotator.App.Reference;

internal sealed partial class ReferenceHost
{
    /// <summary>The keycaps of one global shortcut with 8 points of padding, off the desktop and without activation (<see cref="ShortcutReferenceStates"/>).</summary>
    private void ShowKeycaps(WorkspaceModel model)
    {
        GlobalShortcutAction action = ShortcutReferenceStates.Action(_request.State)
            ?? throw new ArgumentException($"There is no shortcut for {_request.Name}.");
        var root = new Grid
        {
            Background = SettingsColors.Window,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        root.Children.Add(new ShortcutKeycaps(WindowsShortcuts.Shortcut(action, model.Snapshot.Preferences)) { Margin = new Thickness(8) });
        var window = new ShortcutsReferenceWindow(_registry, "Keycaps", root);
        _popover = window;
        window.ShowForReference(OffscreenOrigin());
        Watch((FrameworkElement)window.Content, window.Hwnd);
    }
}