using Annotator.Core;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace;

/// <summary>The outcome of <see cref="WorkspaceModel.BindShortcut"/> (Mac ShortcutBindingResult).</summary>
public abstract record ShortcutBindingResult
{
    private ShortcutBindingResult()
    {
    }

    /// <summary>The key now belongs to the command; <paramref name="Displaced"/> is the command that lost it, if any.</summary>
    public sealed record Assigned(ShortcutCommand? Displaced) : ShortcutBindingResult;

    /// <summary>The command has no key now.</summary>
    public sealed record Cleared : ShortcutBindingResult;

    /// <summary>The profile refused the key; nothing changed. <paramref name="Message"/> says why, in the Mac's words.</summary>
    public sealed record Failed(string Message) : ShortcutBindingResult;
}

/// <summary>
/// Registers the global shortcuts for the workspace, as the Mac's onActivationShortcutChanged and its siblings do
/// (WIN-INP-04). It runs on the model's thread.
/// </summary>
public interface IGlobalShortcutRegistrar
{
    /// <summary>False for actions whose feature is not built yet: their shortcut cannot be changed or used.</summary>
    bool Supports(GlobalShortcutAction action);

    /// <summary>
    /// Registers <paramref name="shortcut"/> for <paramref name="action"/> in place of its current shortcut. Unless the
    /// result is <see cref="GlobalShortcutStatus.Registered"/>, the previous registration stays as it was.
    /// </summary>
    GlobalShortcutState Replace(GlobalShortcutAction action, ActivationShortcut shortcut);
}

/// <summary>The outcome of <see cref="WorkspaceModel.SetGlobalShortcut"/>.</summary>
public abstract record GlobalShortcutChange
{
    private GlobalShortcutChange()
    {
    }

    /// <summary>The shortcut is saved, and registered unless the workspace has no registrar.</summary>
    public sealed record Changed(GlobalShortcutState? State) : GlobalShortcutChange;

    /// <summary>The profile refused the shortcut; nothing changed. <paramref name="Message"/> is the profile's reason.</summary>
    public sealed record Invalid(string Message) : GlobalShortcutChange;

    /// <summary>Windows refused the shortcut or another app owns it; the previous shortcut still works.</summary>
    public sealed record NotRegistered(GlobalShortcutState State) : GlobalShortcutChange;

    /// <summary>The action's feature is not built yet, so its shortcut cannot change.</summary>
    public sealed record Unsupported : GlobalShortcutChange;
}

public sealed partial class WorkspaceModel
{
    private IGlobalShortcutRegistrar? _registrar;

    /// <summary>
    /// The tools and buttons Settings can put in the toolbar, in the Mac's order (WorkspaceModel.toolbarCandidates): every
    /// tool but the single effects, which Effects stands for, then Blur, Pixelate and Distort, then the presenting buttons.
    /// </summary>
    public static IReadOnlyList<ShortcutCommand> ToolbarCandidates { get; } =
    [
        .. Tool.AllCases.Where(tool => !tool.IsProtection || tool == Tool.Cover).Select(ShortcutCommand.ForTool),
        .. new[] { Tool.Blur, Tool.Pixelate, Tool.Distort }.Select(ShortcutCommand.ForTool),
        .. ShortcutAction.ToolbarChoices.Select(ShortcutCommand.ForAction),
    ];

    /// <summary>
    /// Registers changed global shortcuts (the app's hotkey service). Without one, a changed shortcut is only saved, as on
    /// the Mac when no callback is set.
    /// </summary>
    public IGlobalShortcutRegistrar? GlobalShortcutRegistrar
    {
        get
        {
            VerifyAccess();
            return _registrar;
        }
        set
        {
            VerifyAccess();
            _registrar = value;
        }
    }

    /// <summary>
    /// Whether Settings can change <paramref name="action"/>'s shortcut: the registrar takes it. Without a registrar (the
    /// reference states and tests) every action can change, as every one exists in this build.
    /// </summary>
    public bool SupportsGlobalShortcut(GlobalShortcutAction action)
    {
        VerifyAccess();
        return _registrar?.Supports(action) ?? true;
    }

    /// <summary>
    /// Mac bindShortcut: gives a key to a tool or toolbar button, or clears its key with null. A key another command used
    /// moves; the result names that command so Settings can offer Undo. Failures stay with the caller and change nothing.
    /// </summary>
    public ShortcutBindingResult BindShortcut(LocalShortcut? shortcut, ShortcutCommand command)
    {
        using ChangeScope scope = Change();
        WorkspacePreferences updated = _preferences;
        ShortcutCommand? displaced;
        try
        {
            displaced = updated.Bind(shortcut, command);
        }
        catch (CanvasError error) when (error.Kind == CanvasErrorKind.InvalidDocument)
        {
            return new ShortcutBindingResult.Failed(error.Detail ?? error.Message);
        }
        catch (CanvasError error)
        {
            return new ShortcutBindingResult.Failed(error.ErrorDescription);
        }
        _preferences = updated;
        PersistPreferences();
        if (shortcut is { } key)
        {
            string label = WindowsShortcuts.Label(key);
            _status = displaced is { } owner
                ? $"{label} is now the key for {command.Label}. {owner.Label} has no key now."
                : $"{label} is now the key for {command.Label}.";
            return new ShortcutBindingResult.Assigned(displaced);
        }
        _status = $"{command.Label} has no key.";
        return new ShortcutBindingResult.Cleared();
    }

    /// <summary>Mac resetLocalShortcuts: the default keys for tools and buttons again.</summary>
    public void ResetLocalShortcuts()
    {
        using ChangeScope scope = Change();
        _preferences.ResetLocalShortcuts();
        PersistPreferences();
        _status = "Tool keys are back to their defaults.";
    }

    /// <summary>Mac restoreLocalShortcuts(from:): puts back the tool and button keys of an earlier profile, for Undo in Settings.</summary>
    public void RestoreLocalShortcuts(in WorkspacePreferences snapshot)
    {
        using ChangeScope scope = Change();
        WorkspacePreferences updated = _preferences;
        updated.ToolShortcuts = snapshot.ToolShortcuts;
        updated.CommandShortcuts = snapshot.CommandShortcuts;
        try
        {
            updated.Validate();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
            return;
        }
        _preferences = updated;
        PersistPreferences();
        _status = "Keys restored.";
    }

    /// <summary>Mac configureToolbar(_ tools:): tool-only toolbars, as older profiles store them; any effect means Effects.</summary>
    public void ConfigureToolbar(IEnumerable<Tool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ConfigureToolbar(NormalizedTools(tools).Select(ShortcutCommand.ForTool));
    }

    /// <summary>
    /// Mac configureToolbar(items:): saves the toolbar's tools and buttons in order. The tool list stays readable by older
    /// versions; the item list is saved only when the tool list cannot describe the toolbar. A refused list changes nothing.
    /// </summary>
    public void ConfigureToolbar(IEnumerable<ShortcutCommand> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ShortcutCommand[] commands = [.. items];
        using ChangeScope scope = Change();
        WorkspacePreferences updated = _preferences;
        List<Tool> tools = [.. commands.Where(item => item.Tool is not null).Select(item => item.Tool!.Value)];
        updated.ToolbarTools = tools.Select(tool => tool.RawValue).ToValueList();
        updated.ToolbarItems = NormalizedTools(tools).SequenceEqual(tools) && tools.Count == commands.Length
            ? null
            : commands.Select(item => item.Id).ToValueList();
        try
        {
            updated.Validate();
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
            return;
        }
        _preferences = updated;
        PersistPreferences();
    }

    /// <summary>Mac setToolbarSize: Expanded is the default and is not stored.</summary>
    public void SetToolbarSize(ToolbarSize size)
    {
        using ChangeScope scope = Change();
        _preferences.ToolbarSize = size == ToolbarSize.Expanded ? null : size;
        PersistPreferences();
    }

    /// <summary>
    /// Mac setActivationShortcut, setResumeShortcut, setHideShortcut and setZoomShortcut: checks the profile, registers
    /// the shortcut in place of the action's current one and saves it. A refused or failed shortcut changes nothing, and
    /// the previous shortcut keeps working (WIN-INP-04).
    /// </summary>
    public GlobalShortcutChange SetGlobalShortcut(GlobalShortcutAction action, ActivationShortcut shortcut)
    {
        using ChangeScope scope = Change();
        if (!SupportsGlobalShortcut(action))
        {
            return new GlobalShortcutChange.Unsupported();
        }
        WorkspacePreferences updated = _preferences;
        switch (action)
        {
            case GlobalShortcutAction.ShowHide:
                updated.ActivationShortcut = shortcut;
                break;
            case GlobalShortcutAction.ResumeDrawing:
                updated.ResumeShortcut = shortcut;
                break;
            case GlobalShortcutAction.HideOnly:
                updated.HideShortcut = shortcut;
                break;
            case GlobalShortcutAction.LiveZoom:
                updated.ZoomShortcut = shortcut;
                break;
            case GlobalShortcutAction.BreakTimer:
                updated.BreakShortcut = shortcut;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action));
        }
        try
        {
            updated.Validate();
        }
        catch (CanvasError error)
        {
            Report(error);
            return new GlobalShortcutChange.Invalid(error.Kind == CanvasErrorKind.InvalidDocument ? error.Detail ?? error.Message : error.ErrorDescription);
        }
        GlobalShortcutState? state = null;
        if (_registrar is { } registrar)
        {
            state = registrar.Replace(action, shortcut);
            if (state.Status != GlobalShortcutStatus.Registered)
            {
                // The Mac reports the registration error; the action keeps its previous registration and state.
                _errorMessage = state.Message ?? WindowsShortcuts.RegistrationFailure(state.Label, 0);
                _status = _errorMessage;
                return new GlobalShortcutChange.NotRegistered(state);
            }
            _shortcuts = [.. _shortcuts.Where(existing => existing.Action != action), state];
            _shortcuts = [.. _shortcuts.OrderBy(existing => existing.Action)];
        }
        else
        {
            _shortcuts = [.. _shortcuts.Select(existing => existing.Action == action
                ? existing with { Label = WindowsShortcuts.Label(action, updated) }
                : existing)];
        }
        _preferences = updated;
        PersistPreferences();
        if (action == GlobalShortcutAction.ShowHide)
        {
            _status = $"{WindowsShortcuts.Label(action, updated)} opens drawing or hides the tools. " +
                $"{WindowsShortcuts.Label(GlobalShortcutAction.HideOnly, updated)} always hides them.";
        }
        return new GlobalShortcutChange.Changed(state);
    }
}
