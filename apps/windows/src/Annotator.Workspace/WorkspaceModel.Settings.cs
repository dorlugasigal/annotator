using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>
/// The preferences that Settings > General changes and the profiles of its footer (Mac WorkspaceModel: setMenuBarVisible,
/// setShareToolStyles, setTheme, setShortcutHints, setAnimationStyle, exportPreferences and importPreferences). The host
/// reads the results from <see cref="WorkspaceSnapshot.Preferences"/>: the theme of its windows and the tray icon.
/// </summary>
public sealed partial class WorkspaceModel
{
    /// <summary>The largest profile Import profile... reads, in bytes (the Mac's file-size check).</summary>
    public const int MaximumProfileBytes = 256 * 1_024;

    private const string TrayNeedsShortcut = "Register a working activation shortcut before hiding the tray icon.";
    private const string ProfileHidesTray = "A working activation shortcut is required before the profile can hide the tray icon.";

    /// <summary>
    /// Mac setMenuBarVisible: shows or hides the tray icon. Hiding needs a working Show / hide shortcut, so the tools can
    /// still be reached.
    /// </summary>
    public void SetTrayIconVisible(bool visible)
    {
        using ChangeScope scope = Change();
        if (!visible && !ShowHideRegistered)
        {
            Report(CanvasError.InvalidDocument(TrayNeedsShortcut));
            return;
        }
        _preferences.ShowMenuBarIcon = visible;
        PersistPreferences();
    }

    /// <summary>
    /// Mac setShareToolStyles: with sharing on, the next style change of a drawing tool reaches every drawing tool. Off is
    /// the default and is not stored. Turning it on changes nothing until the next edit.
    /// </summary>
    public void SetShareToolStyles(bool shared)
    {
        using ChangeScope scope = Change();
        _preferences.ShareToolStyles = shared ? true : null;
        PersistPreferences();
        _status = shared ? "Your next style change applies to every drawing tool." : "Each tool keeps its own style.";
    }

    /// <summary>Mac setTheme: System, Light or Dark for app windows only (WIN-UI-07); drawings keep their colors.</summary>
    public void SetTheme(AppTheme theme)
    {
        using ChangeScope scope = Change();
        _preferences.Theme = theme;
        PersistPreferences();
    }

    /// <summary>Mac setShortcutHints: Show tips on screen, the canvas tips in the top-left corner while drawing.</summary>
    public void SetShortcutHints(bool visible)
    {
        using ChangeScope scope = Change();
        _preferences.ShowShortcutHints = visible;
        PersistPreferences();
    }

    /// <summary>Mac setAnimationStyle: how drawings and tools hide and return; Off turns the motion off.</summary>
    public void SetAnimationStyle(VisibilityAnimationStyle style)
    {
        using ChangeScope scope = Change();
        _preferences.VisibilityAnimationStyle = style == VisibilityAnimationStyle.Fluid ? VisibilityAnimationStyle.Slide : style;
        _preferences.AnimateVisibility = style != VisibilityAnimationStyle.None;
        PersistPreferences();
    }

    /// <summary>The style the Workspace animation picker shows (Mac: Off when animation is off, else the resolved style).</summary>
    public static VisibilityAnimationStyle ShownAnimationStyle(in WorkspacePreferences preferences) =>
        preferences.AnimateVisibility == false ? VisibilityAnimationStyle.None
            : (preferences.VisibilityAnimationStyle ?? VisibilityAnimationStyle.Slide).Resolved;

    /// <summary>
    /// Mac exportPreferences: the profile to save, or null after reporting why it cannot be written. The host writes it
    /// atomically where the person chose.
    /// </summary>
    public byte[]? ExportPreferences()
    {
        VerifyAccess();
        try
        {
            return _preferences.Data();
        }
        catch (Exception error) when (IsReportable(error))
        {
            using ChangeScope scope = Change();
            Report(error);
            return null;
        }
    }

    /// <summary>
    /// Mac importPreferences: reads a profile of at most <see cref="MaximumProfileBytes"/>, refuses one that would hide the
    /// tray icon without a working Show / hide shortcut, asks the host to register the profile's global shortcuts
    /// (<paramref name="registerShortcuts"/> returns why they could not, after putting the previous ones back), and then
    /// replaces settings and shortcuts. Boards do not change. Returns false after reporting why the profile was not used.
    /// </summary>
    /// <param name="data">The file's bytes; a longer file is refused before it is read.</param>
    /// <param name="hasTrayIcon">False where the host has no tray icon (the live check), so nothing can be hidden.</param>
    public bool ImportPreferences(ReadOnlySpan<byte> data, Func<WorkspacePreferences, string?>? registerShortcuts, bool hasTrayIcon = true)
    {
        using ChangeScope scope = Change();
        WorkspacePreferences incoming;
        try
        {
            if (data.Length > MaximumProfileBytes)
            {
                throw CanvasError.InvalidDocument("The profile is too large.");
            }
            incoming = WorkspacePreferences.Read(data);
            if (incoming.ShowMenuBarIcon == false && hasTrayIcon && !ShowHideRegistered)
            {
                throw CanvasError.InvalidDocument(ProfileHidesTray);
            }
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
            return false;
        }
        if (registerShortcuts?.Invoke(incoming) is { } failure)
        {
            _errorMessage = failure;
            _status = failure;
            return false;
        }
        FinishStyleEditCore();
        _preferences = incoming;
        PersistPreferences();
        Synchronize();
        return true;
    }

    /// <summary>True when the Show / hide shortcut works, so the tools can be shown without the tray icon (Mac hotkeyRegistered).</summary>
    private bool ShowHideRegistered =>
        _shortcuts.Any(state => state.Action == GlobalShortcutAction.ShowHide && state.Status == GlobalShortcutStatus.Registered);
}
