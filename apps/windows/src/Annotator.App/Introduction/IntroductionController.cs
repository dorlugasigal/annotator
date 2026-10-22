using Annotator.Workspace;
using Annotator.Workspace.FirstUse;

namespace Annotator.App.Introduction;

/// <summary>
/// Port of the Mac's IntroductionController (WIN-UI-04, spec 005): shows the tour once per launch at a safe moment without
/// taking focus, on request with focus, and closes it as soon as the workspace starts drawing, zooming, animating, reports
/// an error or has an edit in progress. The window is made when the tour shows and released when it closes.
/// </summary>
internal sealed class IntroductionController
{
    private readonly Func<WorkspaceSnapshot> _snapshot;
    private readonly Action<string> _status;
    private readonly Annotator.Workspace.Metrics.UsageMetrics _usage;
    private IntroductionWindow? _window;
    private bool _attemptedAutomaticPresentation;

    /// <param name="status">Shows a status message (Mac model.status), for the tour that cannot open now.</param>
    /// <param name="usage">The app's usage counts, for the footer's Share usage counts.</param>
    public IntroductionController(AppDefaults defaults, Func<WorkspaceSnapshot> snapshot, Action<string> status,
                                  Annotator.Workspace.Metrics.UsageMetrics usage)
    {
        Preferences = new IntroductionPreferences(defaults);
        _snapshot = snapshot;
        _status = status;
        _usage = usage;
    }

    public IntroductionPreferences Preferences { get; }

    public IntroductionNavigator Navigator { get; } = new();

    /// <summary>Mac model.introductionVisible: the coffee reminder waits while the tour shows.</summary>
    public bool IsVisible => _window?.IsShown == true;

    /// <summary>Raised when the tour shows or hides.</summary>
    public event Action? VisibilityChanged;

    /// <summary>The window once made (UI checks and measurements).</summary>
    public IntroductionWindow? Window => _window;

    public bool IsEligible => IntroductionRules.IsEligible(_snapshot());

    /// <summary>Mac showAutomatically: once per launch, never for a quiet start at sign-in, after Show on launch was turned off or at an unsafe moment.</summary>
    public void ShowAutomatically(bool startupLaunch)
    {
        if (_attemptedAutomaticPresentation)
        {
            return;
        }
        _attemptedAutomaticPresentation = true;
        if (IntroductionRules.ShowsAutomatically(startupLaunch, Preferences.Suppressed, IsEligible))
        {
            Show(takeFocus: false);
        }
    }

    /// <summary>Mac showManually: Help and Settings open it with focus, or say when it cannot open now.</summary>
    public void ShowManually()
    {
        if (!IsEligible)
        {
            _status(IntroductionRules.NotNowMessage);
            return;
        }
        Show(takeFocus: true);
    }

    /// <summary>
    /// Mac close(): the story ends in its final state. Windows releases the window and its devices (about 30 MB) instead
    /// of keeping it hidden; the navigator keeps the page, so the tour opens where it was.
    /// </summary>
    public void Close()
    {
        bool wasVisible = IsVisible;
        IntroductionWindow? window = _window;
        _window = null;
        window?.Conceal();
        window?.Shutdown();
        Navigator.SetPresented(false);
        if (wasVisible)
        {
            VisibilityChanged?.Invoke();
        }
    }

    /// <summary>Mac's observation of the model: an open tour closes once it is no longer eligible, and follows shortcut changes.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (_window is not { IsShown: true } window)
        {
            return;
        }
        if (!IntroductionRules.IsEligible(snapshot))
        {
            Close();
            return;
        }
        window.Apply(snapshot);
    }

    /// <summary>Closes the window for good (Quit).</summary>
    public void Shutdown()
    {
        _window?.Shutdown();
        _window = null;
    }

    private void Show(bool takeFocus)
    {
        bool wasVisible = IsVisible;
        Navigator.SetPresented(true);
        _window ??= new IntroductionWindow(Navigator, Preferences, _snapshot, _usage, Close);
        _window.Present(takeFocus);
        if (!wasVisible)
        {
            VisibilityChanged?.Invoke();
        }
    }
}
