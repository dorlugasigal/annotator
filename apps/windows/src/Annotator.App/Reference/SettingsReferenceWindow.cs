using Annotator.App.Settings;
using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference capture): Settings and its dialogs on their own, as the Mac capture renders them
/// (UXReferenceCaptureTests.test05Settings and test06WindowsAndDialogs): CustomizationView at 840 points wide and 700,
/// 1500 or 2400 high with the Toolbar &amp; Shortcuts, Pointer and Break timer pages the app sets, LoginItemSettings().padding(16) 620 wide for a
/// startup task state, FeedbackView at 460 by 680 and ReleaseNotesView().padding(20) at 560 by 640. Launch at sign-in reads a
/// fixed state; nothing opens a link, a dialog, the clipboard or Windows Settings.
/// </summary>
internal sealed class SettingsReferenceWindow : PopoverWindow
{
    private readonly SettingsView? _view;

    public SettingsReferenceWindow(WindowRegistry registry, WorkspaceModel model, ReferenceScenario scenario,
                                   Annotator.Workspace.Metrics.UsageMetrics usage)
        : base(registry, "Annotator Settings")
    {
        var launch = new LaunchAtSignIn(new FixedStartupTask(scenario.Launch), static _ => { });
        // A fixed task answers at once, so the state shows in the first frame.
        _ = launch.RefreshAsync();
        // The Mac capture's Settings come from a build without updates, as a development build is.
        var updates = new AppUpdates(new FixedStoreUpdates(UpdateChannel.Development), static () => null, static _ => Task.FromResult(false),
            static _ => { });
        var actions = new SettingsActions(model, launch, updates, SettingsEnvironment.ForApp(Facts, registerShortcuts: null, hasTrayIcon: true) with
        {
            Live = false,
        })
        {
            UsageMetrics = usage,
        };
        switch (scenario.Surface)
        {
            case ReferenceSurface.Settings:
                // The pages the app sets through SettingsWindow.SetPage.
                var pointer = new PointerPage(model);
                var pages = new Dictionary<SettingsTab, FrameworkElement>
                {
                    [SettingsTab.Toolbar] = new ToolbarShortcutsPage(model),
                    [SettingsTab.Pointer] = pointer,
                    // Its Start button is never pressed here, so no break starts.
                    [SettingsTab.BreakTimer] = new BreakTimerPage(model, new Breaks.BreakTimerHost(DispatcherQueue, registry, model)),
                };
                Pointer = scenario.Tab == SettingsTab.Pointer ? pointer : null;
                _view = new SettingsView(actions, scenario.Tab, pages);
                _view.Root.Width = SettingsWindow.DefaultWidth;
                _view.Root.Height = scenario.Height;
                // The full states are 1500 or 2400 points high, taller than most screens.
                WindowChrome.AllowAnySize(Hwnd);
                Surface = _view.Root;
                model.Published += Apply;
                break;
            case ReferenceSurface.LaunchAtSignIn:
                var root = new Grid { Width = 620, Background = SettingsLook.Window };
                root.Children.Add(new LaunchAtSignInView(launch, inForm: false) { Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center });
                Surface = root;
                break;
            case ReferenceSurface.Feedback:
                var feedback = new FeedbackView(actions, scenario.Feedback, () => (nint)Hwnd, static () => { });
                feedback.Root.Height = FeedbackView.Height;
                Surface = feedback.Root;
                break;
            case ReferenceSurface.ReleaseNotes:
                var notes = new Grid { Width = 560, Height = 640, Background = SettingsLook.Window };
                SettingsDisclosure disclosure = ReleaseNotesView.Make(actions.Environment, 560 - 40 - 12);
                disclosure.Margin = new Thickness(20);
                disclosure.VerticalAlignment = VerticalAlignment.Center;
                notes.Children.Add(disclosure);
                Surface = notes;
                break;
            default:
                throw new ArgumentException($"{scenario.Surface} is not a Settings surface.", nameof(scenario));
        }
        Closed += (_, _) =>
        {
            model.Published -= Apply;
            _view?.Detach();
        };
    }

    /// <summary>The Pointer page while the Pointer tab shows: its preview draws its halo once the window shows.</summary>
    public PointerPage? Pointer { get; }

    protected override void FocusFirst()
    {
    }

    private void Apply(WorkspaceSnapshot snapshot) => _view?.Apply(snapshot);

    private static SupportFacts Facts() => new()
    {
        Version = BuildInfo.Version,
        BuildLabel = BuildInfo.BuildLabel,
        Installation = "development build (not packaged)",
        Windows = "reference",
        Architecture = "reference",
        Capture = "stopped",
    };
}
