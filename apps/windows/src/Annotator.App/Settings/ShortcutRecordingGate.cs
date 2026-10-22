using Annotator.Workspace;
using Annotator.Workspace.Settings;

namespace Annotator.App.Settings;

/// <summary>
/// Lets the Toolbar &amp; Shortcuts page take a global shortcut that is pressed while one of its keys records. Windows
/// delivers a registered chord as WM_HOTKEY and never as key presses, so without this, pressing Ctrl+2 to record it would
/// show or hide the tools instead. Use it on the app thread.
/// </summary>
internal static class ShortcutRecordingGate
{
    private static Func<GlobalShortcutAction, bool>? s_listener;

    /// <summary>Offers each pressed global shortcut to <paramref name="listener"/> until the returned handle is disposed.</summary>
    public static IDisposable Listen(Func<GlobalShortcutAction, bool> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        s_listener = listener;
        return new Subscription(listener);
    }

    /// <summary>True when a recording page took the shortcut, so its action must not run.</summary>
    public static bool Offer(GlobalShortcutAction action) => s_listener?.Invoke(action) ?? false;

    private sealed class Subscription(Func<GlobalShortcutAction, bool> listener) : IDisposable
    {
        public void Dispose()
        {
            if (s_listener == listener)
            {
                s_listener = null;
            }
        }
    }
}

/// <summary>The keyboard layouts installed for this account, for the recording rule of WIN-INP-04.</summary>
internal sealed class InstalledKeyboardLayouts : IKeyboardLayouts
{
    public static InstalledKeyboardLayouts Instance { get; } = new();

    public string? TypedCharacter(Annotator.Workspace.Shortcuts.ShortcutChord chord) => Shell.GlobalShortcuts.TypesCharacter(chord);
}
