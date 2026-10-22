using Annotator.App.Shell;

namespace Annotator.App;

internal sealed partial class AppHost
{
    /// <summary>
    /// Development only (live check): Settings changes global shortcuts through a registrar that registers nothing until the
    /// check records one, so the person's own shortcuts, and a running Annotator, keep theirs. The registrar goes at quit.
    /// </summary>
    private void UseLiveCheckRegistrar()
    {
        _shortcuts = new GlobalShortcuts(OnShortcut);
        _model.GlobalShortcutRegistrar = _shortcuts;
    }
}