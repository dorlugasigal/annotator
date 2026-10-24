using Annotator.App.Introduction;
using Annotator.App.Files;
using Annotator.App.Library;
using Annotator.App.Motion;
using Annotator.App.Native;
using Annotator.App.Overlay;
using Annotator.App.Pickers;
using Annotator.App.Reference;
using Annotator.App.Settings;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Platform;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Overlays;
using Annotator.Platform.Sessions;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Library;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Sharing;
using Annotator.Workspace.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using CorePoint = Annotator.Core.Point;
using DisplayId = Annotator.Workspace.DisplayId;

namespace Annotator.App;

/// <summary>
/// The composition root on the app thread (the WinUI thread). It owns the workspace model and every app
/// window, and sends the overlay thread immutable frames. Nothing mutable crosses threads: the overlay
/// thread talks back only through queued calls (<see cref="WorkspaceChannel"/> and overlay events).
/// </summary>
internal sealed partial class AppHost
{
    /// <summary>How long after a press on the tray icon closed the flyout its release is still part of that click.</summary>
    private const long IconClickMilliseconds = 500;
    private readonly DispatcherQueue _queue;
    private readonly WindowRegistry _registry = new();
    private readonly CaptureService _capture;
    private readonly SystemClipboard _clipboard;
    private readonly ScreenColorPickerHost _screenColors;
    private readonly WindowsScreenAccess? _screenAccess;
    private readonly WorkspaceModel _model;
    private readonly OverlayHost _overlays;
    private readonly DrawingCanvasFactory _canvases;
    private readonly ToolbarWindow _toolbar;
    private readonly LiveCheckRequest? _check;
    private readonly VisibilityHandoff _handoff;
    private readonly ShapeLibrary _library;
    private readonly ShapeThumbnails _thumbnails = new();
    private readonly AppDefaults _defaults;
    private readonly SupportReminder _reminder;
    private readonly IntroductionController _introduction;
    private readonly WhatsChangedTracker _whatsChangedTracker;
    /// <summary>This launch follows an update and What's changed has not shown yet.</summary>
    private ReleaseNotesEntry? _whatsChangedDue;
    /// <summary>A quiet sign-in launch: What's changed waits until the person opens Annotator.</summary>
    private bool _whatsChangedWaits;
    private WhatsChangedWindow? _whatsChanged;
    private SupportReminderPopover? _reminderPopover;
    private WorkspaceMode _lastMode;
    private readonly Annotator.Workspace.Zoom.DisplayedZoomBoard _zoomBoard = new();
    private readonly ZoomInput _zoomInput;
    private readonly FileCommands _files;
    private ErrorWindow? _errors;
    private IReadOnlyList<DisplayDescriptor> _displays;
    private TrayIcon? _tray;
    private GlobalShortcuts? _shortcuts;
    private SessionWatcher? _sessions;
    private SingleInstance.Listener? _listener;
    private TrayFlyout? _flyout;
    private ColorPopover? _colorPicker;
    private ArrowHeadPalette? _arrowHeads;
    private ToolsWindow? _tools;
    private SettingsWindow? _settings;
    private readonly LaunchAtSignIn _launch;
    private readonly AppUpdates _updates;
    private readonly SettingsActions _settingsActions;
    private AppTheme? _theme;
    private bool? _trayShown;
    private readonly Sharing.RegionSharingController _sharingWindows;
    private readonly Breaks.BreakTimerHost _breaks;
    private long? _flyoutClosedByIcon;
    private bool _checkReady;
    private bool _quitting;

    /// <param name="check">Development only: the live check's made-up display and files instead of the desktop and settings.</param>
    private AppHost(LiveCheckRequest? check = null)
    {
        _check = check;
        _queue = DispatcherQueue.GetForCurrentThread();
        // An earlier Annotator saved settings here, read before anything this launch saves (What's changed after an update).
        bool usedBefore = check is null && (File.Exists(Path.Combine(BuildInfo.DataFolder, "app-defaults-v1.json"))
            || File.Exists(Path.Combine(BuildInfo.DataFolder, "workspace-preferences-v1.json")));
        // Failures inside window procedures, on either thread, are shown to the person instead of ending the app.
        CallbackErrors.Raised += error => ReportLater("A window message failed", error);
        NativeCallbackErrors.Unhandled += error => ReportLater("A window message failed", error);
        double checkScale = check is null ? 1 : WindowChrome.MonitorFor(new RectInt32(check.Area.Left, check.Area.Top, check.Area.Width, check.Area.Height)).Scale;
        DisplayInfo[]? checkDisplays = check is null ? null : [check.Display(checkScale)];
        _displays = [.. (checkDisplays ?? DisplayEnumerator.GetDisplays()).Select(OverlayHost.Describe)];
        _clipboard = new SystemClipboard();
        _screenColors = new ScreenColorPickerHost(_registry, _queue);
        // A packaged copy asks Windows for screen access through its explanation when a pixel feature starts; a development
        // build needs none and gets no host (WIN-CAP-04). The live check never asks.
        _screenAccess = check is null && Annotator.Platform.Startup.StartupTaskService.HasPackageIdentity
            ? new WindowsScreenAccess(_registry, ToolbarAnchor)
            : null;
        // My shapes live in the app's local data folder (WIN-SYS-05); the live check never reads or changes them.
        _library = check is null ? ShapeLibrary.InFolder(Path.Combine(BuildInfo.DataFolder, "My Shapes")) : ShapeLibrary.InMemory();
        _model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => _displays,
            Pointer = () => WindowChrome.TryCursor() is { } cursor ? new CorePoint(cursor.X, cursor.Y) : null,
            // The live check never reads or changes the person's settings.
            PreferencesStore = check is null ? new FilePreferencesStore(BuildInfo.DataFolder) : new MemoryPreferencesStore(),
            Clipboard = _clipboard,
            // The canvases edit text with the Text Services Framework editor of spike S-7 (WIN-INP-09).
            TextEditingAvailable = true,
            // Text size, font and weight changes resize selected text to its DirectWrite size (D-4).
            TextMeasurer = new Annotator.Rendering.Text.DirectWriteTextMeasurer(),
            // Showing and hiding play the Mac's motion on the overlays (spec 009, WIN-MOT-02).
            AnimatesVisibility = true,
            ReducesMotion = SystemMotion.ReducesMotion,
            // Zoom's scroll gesture ends 180 ms after the last scroll, on this thread (Mac zoomGesture task).
            Timer = Schedule,
            // What the overlays show of zoom, so a scroll during the zoom-out ramp starts zoom again from there.
            DisplayedZoom = _zoomBoard,
            ScreenAccess = _screenAccess,
        });
        // Devices and overlays live on their own thread (S-12). They are created after the toolbar's first frame:
        // setting up both threads' composition at the same moment sometimes left the toolbar black until it redrew.
        Action<OverlayEvent> report = overlayEvent => _queue.TryEnqueue(() => OnOverlayEvent(overlayEvent));
        // The saved theme applies before the first window is made (WIN-UI-07, the Mac's applyTheme at launch).
        ApplyAppearance(_model.Snapshot);
        // Windows that stay open follow a contrast theme or a text size the person turns on later (WIN-A11Y-03).
        AppResources.WatchSystem(_queue);
        // Launch at sign-in reads the package's startup task; the live check never touches it (WIN-SYS-01).
        _launch = new LaunchAtSignIn(check is not null ? new FixedStartupTask(LaunchAtSignInState.NotPackaged)
            : BuildInfo.IsPortable ? new RunKeyStartupTask() : new WindowsStartupTask(),
            message => _model.Report(message));
        // First-use state (the Mac's UserDefaults): Show on launch, the coffee reminder's days of use, What's changed and a
        // portable copy's update checks. The live check never reads or changes the person's.
        _defaults = check is null ? AppDefaults.InFile(Path.Combine(BuildInfo.DataFolder, "app-defaults-v1.json")) : AppDefaults.InMemory();
        _defaults.SaveFailed += message => _queue.TryEnqueue(() => _model.Report(message));
        // Updates come from the Microsoft Store (WIN-SYS-03). The live check's Store has an update that it never installs and
        // never calls Windows, so Check for updates asks its question and installs nothing. A portable copy reads the
        // website's update feed and only says where to get a newer release.
        IStoreUpdates store = check is null
            ? new WindowsStoreUpdates(() => _settings is { } settings ? (nint)settings.Hwnd : 0)
            : new FixedStoreUpdates(UpdateChannel.Store) { Answer = StoreUpdateCheck.Available, Outcome = StoreInstallOutcome.Cancelled };
        PortableUpdateSource? portable = check is null && BuildInfo.UpdateFeed is { } feed && BuildInfo.BuildNumber is { } build
            ? new PortableUpdateSource(new PortableReleaseFeed(feed, BuildInfo.NotesVersion), build, _defaults)
            : null;
        _updates = new AppUpdates(store, () => UpdateQuestion.For(_model.Snapshot), AskBeforeUpdateAsync,
            message => StartupLog.Note("store-update", message), portable: portable);
        // What's changed follows an update once (the Mac's WhatsChangedTracker); the live check never shows it.
        _whatsChangedTracker = new WhatsChangedTracker(_defaults);
        _whatsChangedDue = check is null && BuildInfo.BuildNumber is { } running
            ? _whatsChangedTracker.Due(running, usedBefore, () => ReleaseNotesCatalog.Load(
                Path.Combine(AppContext.BaseDirectory, ReleaseNotesView.NotesFile), BuildInfo.NotesVersion, running))
            : null;
        _settingsActions = new SettingsActions(_model, _launch, _updates,
            SettingsEnvironment.ForApp(CollectSupportFacts, check is null ? RegisterProfileShortcuts : null, hasTrayIcon: check is null) with
            {
                // The live check's Settings never open file dialogs, links or the clipboard.
                Live = check is null,
            });
        _settingsActions.ShowIntroduction = ShowIntroduction;
        // One capture service for every display's effect regions; it runs only while a region needs pixels (WIN-CAP-01).
        // Frames that change nothing are skipped, and a still screen is captured at 60 frames a second (WIN-PERF-03).
        _capture = CaptureService.Create(new CaptureServiceOptions { Registry = _registry, FrameComparer = new Rendering.Effects.CaptureFrameComparer() });
        var canvases = new DrawingCanvasFactory(report, (display, request) => _queue.TryEnqueue(() => OnCanvasRequest(display, request)))
        {
            Capture = _capture,
            Registry = _registry,
            DisplayedZoom = _zoomBoard,
        };
        _canvases = canvases;
        // Live zoom's Alt+scroll and Escape hooks (WIN-INP-06, WIN-INP-07). The live check hooks input only when asked.
        _zoomInput = new ZoomInput(_queue, _model, _zoomBoard, canvases, DismissShownWindow, hooks: check is null || check.ZoomHooks);
        // The break timer (specification 014): its windows and timers on this thread.
        _breaks = new Breaks.BreakTimerHost(_queue, _registry, _model);
        // Region sharing (WIN-CAP-08): its windows on this thread, its output on the overlay thread.
        _sharingWindows = new Sharing.RegionSharingController(_registry, _queue, (operation, error) => ReportLater(operation, error));
        _sharingWindows.Attach(new RegionSharing(_model, _sharingWindows));
        var channel = new WorkspaceChannel(_queue, _model);
        _overlays = checkDisplays is null
            ? OverlayHost.Start(_registry, channel, report, canvases)
            : OverlayHost.StartForLiveCheck(_registry, channel, report, canvases, () => checkDisplays);
        _toolbar = new ToolbarWindow(_model, _registry, CloseTransientWindows, OpenColorPicker, check is null ? null : new ToolbarReferencePlacement(
            new Windows.Foundation.Size(check.Area.Width / checkScale - 32, check.Area.Height / checkScale - 64),
            new PointInt32(check.Area.Left + (int)Math.Round(12 * checkScale), check.Area.Top + (int)Math.Round(12 * checkScale))))
        {
            ArrowHeads = OpenArrowHeads,
            ToolLibrary = () => OpenToolLibrary(null),
            Settings = () => OpenSettings(SettingsTab.General),
        };
        canvases.ToolbarWindow = (nint)_toolbar.Hwnd;
        // Open board, Save board, Export PNG and Insert image (WIN-SYS-04): dialogs over the toolbar, file work off the app thread.
        _files = new FileCommands(_model, _queue, () => _toolbar.Hwnd, TrayFocusTarget, PostFrame,
            check is null ? null : LiveCheckRequest.DialogState);
        _handoff = new VisibilityHandoff(_model, _toolbar, () => _overlays.IsReady, PostFrame);
        _toolbar.FirstFrame += () =>
        {
            StartupLog.Mark("toolbar-first-frame");
            if (!_quitting)
            {
                _overlays.CreateOverlays();
            }
        };
        // First-use state lives in _defaults, made with the update settings above.
        _reminder = new SupportReminder(_defaults);
        _reminder.Changed += UpdateSupportReminder;
        // Usage counts (WIN-PRV-01): the workspace reports what the Mac's model records; Settings and the tour show the switch.
        _usage = CreateUsageMetrics(live: check is null);
        _model.UsageRecorded += _usage.Record;
        _settingsActions.UsageMetrics = _usage;
        _introduction = new IntroductionController(_defaults, () => _model.Snapshot, _model.ShowStatus, _usage);
        _introduction.VisibilityChanged += UpdateSupportReminder;
        // Settings floats again once the tour that opened in front of it hides.
        _introduction.VisibilityChanged += () =>
        {
            if (!_introduction.IsVisible)
            {
                _settings?.Float();
            }
        };
        _lastMode = _model.Snapshot.Mode;
        _model.Published += OnPublished;
    }

    /// <param name="board">A board file the launch named, opened once the toolbar has drawn its first frame (WIN-SYS-04).</param>
    /// <param name="quiet">
    /// The startup task started Annotator at sign-in (WIN-SYS-01): the tray icon and global shortcuts only, without the
    /// toolbar or the tour, like the Mac's login item (finishLaunchPresentation).
    /// </param>
    public static AppHost Start(string? board = null, bool quiet = false)
    {
        var host = new AppHost();
        if (!quiet)
        {
            host._model.ShowToolbar();
            StartupLog.Mark("toolbar-shown");
        }
        host.StartServices();
        host.StartFirstUse(quiet);
        if (board is not null)
        {
            host._toolbar.FirstFrame += () =>
            {
                if (!host._quitting)
                {
                    host._files.OpenBoard(board);
                }
            };
        }
        return host;
    }

    /// <summary>
    /// Development only: the live check (<see cref="LiveCheckRequest"/>). The app's own windows and workspace on a made-up
    /// display, without the tray icon, global shortcuts or the single-instance listener; it quits after the check.
    /// </summary>
    public static AppHost StartLiveCheck(LiveCheckRequest check)
    {
        var host = new AppHost(check);
        Directory.CreateDirectory(check.Output);
        File.Delete(check.ReadyPath);
        File.Delete(check.ResultPath);
        File.Delete(check.PicturePath);
        host._model.ShowToolbar();
        host.UseLiveCheckRegistrar();
        host.WatchLiveCheckFirstUse();
        var done = new EventWaitHandle(false, EventResetMode.ManualReset, LiveCheckRequest.DoneEventName(Environment.ProcessId));
        ThreadPool.QueueUserWorkItem(_ =>
        {
            done.WaitOne(check.TimeoutSeconds * 1000);
            done.Dispose();
            host._queue.TryEnqueue(() => _ = host.FinishLiveCheckAsync(check));
        });
        return host;
    }

    /// <summary>The live check can start once the toolbar and the canvas exist.</summary>
    private void WriteLiveCheckReady(LiveCheckRequest check)
    {
        _checkReady = true;
        nint overlay = _overlays.OverlayFor(new DisplayId(LiveCheckRequest.DisplayName));
        LiveCheckRequest.WriteJson(check.ReadyPath, writer =>
        {
            writer.WriteNumber("processId", Environment.ProcessId);
            writer.WriteNumber("toolbar", (long)(nint)_toolbar.Hwnd);
            writer.WriteNumber("overlay", (long)overlay);
            writer.WriteNumber("scale", _displays[0].Scale);
        });
    }

    /// <summary>Writes what the live check drew (result.json and canvas.png), then quits; each step is bounded.</summary>
    private async Task FinishLiveCheckAsync(LiveCheckRequest check)
    {
        try
        {
            CloseTransientWindows();
            WorkspaceSnapshot snapshot = _model.Snapshot;
            Task<PointerEffectsCounters> pointer = _overlays.RunAsync(_ => _canvases.PointerEffects.Counters.Copy());
            PointerEffectsCounters? counters = await Task.WhenAny(pointer, Task.Delay(5000)) == pointer ? await pointer : null;
            Task<ZoomStatistics?> zoom = _overlays.RunAsync(shown => shown.Values.OfType<DrawingCanvas>().FirstOrDefault()?.ZoomStatistics);
            ZoomStatistics? measured = await Task.WhenAny(zoom, Task.Delay(5000)) == zoom ? await zoom : null;
            Task<(int, int, int, int, int)?> sharing = _overlays.RunAsync(_ => _canvases.SharedOutput()?.MotionStatistics);
            (int, int, int, int, int)? motion = await Task.WhenAny(sharing, Task.Delay(5000)) == sharing ? await sharing : null;
            Task<(int, int, bool, string?)?> boosting = _overlays.RunAsync(_ => _canvases.BoostCounters);
            (int, int, bool, string?)? boost = await Task.WhenAny(boosting, Task.Delay(5000)) == boosting ? await boosting : null;
            Annotator.Platform.Input.LowLevelHookStatistics? hooks = _zoomInput.Statistics;
            CaptureStatistics capture = _capture.Statistics;
            bool capturing = _capture.IsCapturing, holds = _capture.HoldsCapture;
            LiveCheckRequest.WriteJson(check.ResultPath, writer =>
            {
                LiveCheckRequest.WriteWorkspace(writer, snapshot);
                LiveCheckRequest.WritePointerEffects(writer, counters);
                LiveCheckRequest.WriteZoom(writer, measured, hooks);
                LiveCheckRequest.WriteSharingMotion(writer, motion);
                LiveCheckRequest.WriteCapture(writer, capture, capturing, holds);
                LiveCheckRequest.WriteBoost(writer, boost);
            });
            Task<byte[]?> picture = _overlays.RunAsync(shown => shown.Values.OfType<DrawingCanvas>().FirstOrDefault()?.Picture(2));
            if (await Task.WhenAny(picture, Task.Delay(5000)) == picture && await picture is { } png)
            {
                await File.WriteAllBytesAsync(check.PicturePath, png);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NativeCallException)
        {
            StartupLog.Note("live-check", error.ToString().ReplaceLineEndings(" | "));
        }
        await QuitAsync();
    }

    private void StartServices()
    {
        _tray = new TrayIcon(OnTray);
        string? reported = _tray.Problem;
        _tray.ProblemChanged += () =>
        {
            if (_tray.Problem is { } changed)
            {
                _model.Report(changed);
            }
            else if (reported is not null && _model.Snapshot.ErrorMessage == reported)
            {
                _model.DismissError();
            }
            reported = _tray.Problem;
        };
        if (reported is not null)
        {
            _model.Report(reported);
        }
        _shortcuts = new GlobalShortcuts(OnShortcut);
        foreach (GlobalShortcutState state in _shortcuts.Register(_model.Snapshot.Preferences))
        {
            StartupLog.Note("shortcut-" + state.Action, state.Status.ToString());
            _model.SetGlobalShortcutState(state);
        }
        // Settings changes global shortcuts through the hotkey service, keeping the previous one when Windows refuses (WIN-INP-04).
        _model.GlobalShortcutRegistrar = _shortcuts;
        // Mac: a hidden menu-bar icon comes back when the activation shortcut could not be registered, so the tools stay reachable.
        if (_model.Snapshot.Preferences.ShowMenuBarIcon == false && !_model.Snapshot.GlobalShortcuts.Any(state =>
                state.Action == GlobalShortcutAction.ShowHide && state.Status == GlobalShortcutStatus.Registered))
        {
            _model.SetTrayIconVisible(true);
        }
        ApplyAppearance(_model.Snapshot);
        _listener = new SingleInstance.Listener(() =>
        {
            // Opening the app again shows the toolbar, like reopening the Mac app (WIN-SYS-02).
            CloseTransientWindows();
            _model.ShowToolbar();
        }, board =>
        {
            // Opening a board file with the app opens it as Open board does: drawing starts, and hidden tools show again.
            CloseTransientWindows();
            _files.OpenBoard(board);
        });
        try
        {
            _sessions = SessionWatcher.Start(OnSessionTransition);
        }
        catch (NativeCallException error)
        {
            // Capture still pauses itself on lock and sleep while it runs; only the app's own pause is missing.
            Report("Watching for lock and sleep could not start", error);
        }
        StartupLog.Mark("services-ready");
    }

    /// <summary>
    /// WIN-OVR-09: lock, sleep, signing out and console or Remote Desktop changes run the Mac suspension; unlocking resumes
    /// live effects (the capture service's README).
    /// </summary>
    private void OnSessionTransition(SessionTransition transition)
    {
        StartupLog.Note("session", transition.ToString());
        if (_quitting)
        {
            return;
        }
        // The break keeps counting behind the lock screen and shows again after sign-in (spec 014, BRK-LOCK-05).
        if (transition == SessionTransition.Locked)
        {
            _breaks.SessionLocked();
        }
        else if (transition == SessionTransition.Unlocked)
        {
            _breaks.SessionUnlocked();
        }
        if (transition is SessionTransition.Locked or SessionTransition.Unlocked)
        {
            // Mac DisplayCapture.desktopIsLocked: region sharing does not start while the session is locked.
            _sharingWindows.DesktopIsLocked = transition == SessionTransition.Locked;
        }
        if (transition == SessionTransition.Unlocked)
        {
            _model.RetryLiveEffects();
            return;
        }
        SuspendPresentation(transition switch
        {
            SessionTransition.Locked => CaptureSuspension.SessionLocked,
            SessionTransition.SigningOut => CaptureSuspension.SignOut,
            SessionTransition.Sleeping => CaptureSuspension.SystemSleep,
            _ => CaptureSuspension.SessionChanged,
        });
    }

    /// <summary>
    /// Mac suspendPresentation with onSuspendPresentation: a toolbar drag ends, popovers and the Tools window close, capture
    /// pauses with effect regions opaque, drawing input is released and the status says so. Capture stays paused until an
    /// effect tool is chosen, the workspace shows again or the session is unlocked.
    /// </summary>
    private void SuspendPresentation(CaptureSuspension reason, bool cancelPendingRequests = true)
    {
        _toolbar.CancelDrag();
        CloseTransientWindows();
        _tools?.Hide(restoreFocus: false);
        _capture.Suspend(reason);
        _model.SuspendPresentation(cancelPendingRequests);
    }

    private bool InputBlocked => _flyout is not null || _colorPicker is not null || _arrowHeads is not null || _files.DialogOpen;

    private void OnPublished(WorkspaceSnapshot snapshot)
    {
        if (snapshot.Surfaces != SurfaceMode.Hidden || snapshot.Visibility is not null)
        {
            // Normally done after the toolbar's first frame; a mode that needs overlays must not wait for it.
            _overlays.CreateOverlays();
        }
        if (!snapshot.ToolsPresented && _colorPicker is { } picker)
        {
            // The Mac closes the color popover when the workspace hides (InspectorColorPicker onChange of workspaceHidden).
            // Closing ends its Undo step, which publishes again, so it waits until this snapshot is shown everywhere.
            _queue.TryEnqueue(() => picker.Dismiss(restoreFocus: false));
        }
        _handoff.BeforeApply(snapshot);
        _overlays.Post(Frame(snapshot));
        ApplyAppearance(snapshot);
        _settings?.Apply(snapshot);
        _zoomInput.Apply(snapshot);
        _colorPicker?.Apply(snapshot);
        if (!snapshot.ToolsPresented)
        {
            // Hiding the workspace hides Tools (Mac onDismissToolbar and onSuspendPresentation call library.hide()).
            _tools?.Hide(restoreFocus: true);
        }
        else
        {
            _tools?.Apply(snapshot);
        }
        try
        {
            _toolbar.Apply(_handoff.ToolbarView(snapshot));
        }
        catch (NativeCallException error)
        {
            // Reported after this publish; the next snapshot applies the toolbar again.
            ReportLater("The toolbar could not be updated", error);
        }
        _sharingWindows.Apply();
        _tray?.ShowSharing(snapshot.RegionSharing.IsCapturing);
        _handoff.AfterApply(snapshot);
        OnFirstUsePublished(snapshot);
        ShowError(snapshot.ErrorMessage);
    }

    /// <summary>
    /// The app-owned error window shows the workspace's current error until it is dismissed (Mac WorkspaceErrorController, spec
    /// 005). It is made with the first error and kept, hidden, for the next one.
    /// </summary>
    private void ShowError(string? message)
    {
        if (message is null && _errors is null)
        {
            return;
        }
        try
        {
            _errors ??= new ErrorWindow(_registry, _model, () => _model.Snapshot.ToolbarVisible ? _toolbar.Frame : null, TrayFocusTarget);
            _errors.Apply(message);
        }
        catch (Exception error) when (error is NativeCallException or System.Runtime.InteropServices.COMException)
        {
            // The message stays in the workspace's status; the next error tries the window again.
            StartupLog.Note("error-window", error.ToString().ReplaceLineEndings(" | "));
        }
    }

    private void PostFrame() => _overlays.Post(Frame(_model.Snapshot));

    private OverlayFrame Frame(WorkspaceSnapshot snapshot) => new(snapshot, InputBlocked) { Toolbar = _handoff.Toolbar };

    private void OnShortcut(GlobalShortcutAction action)
    {
        StartupLog.Note("hotkey", action.ToString());
        if (action == GlobalShortcutAction.BreakTimer)
        {
            CloseTransientWindows();
            _breaks.ShortcutPressed();
            return;
        }
        if (_breaks.IsRunning)
        {
            // Mac: the other global shortcuts do nothing while the break screen shows.
            return;
        }
        CloseTransientWindows();
        switch (action)
        {
            case GlobalShortcutAction.ShowHide:
                _model.ToggleTools();
                break;
            case GlobalShortcutAction.ResumeDrawing:
                _model.ResumeDrawing();
                break;
            case GlobalShortcutAction.HideOnly:
                _model.HideTools();
                break;
            case GlobalShortcutAction.LiveZoom:
                // Mac toggleScreenZoomFromShortcut: Ctrl+1 starts screen zoom at 1.5x, or stops zoom.
                _model.ToggleScreenZoomFromShortcut();
                break;
        }
    }

    /// <summary>A one-shot timer on the app thread for the workspace (<see cref="WorkspaceOptions.Timer"/>); disposing cancels it.</summary>
    private IDisposable Schedule(TimeSpan delay, Action action)
    {
        DispatcherQueueTimer timer = _queue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (sender, _) =>
        {
            sender.Stop();
            if (!_quitting)
            {
                action();
            }
        };
        timer.Start();
        return new ScheduledTimer(timer);
    }

    private sealed class ScheduledTimer(DispatcherQueueTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    /// <summary>Mac onDismissMenuBar before the zoom Escape: true when the tray flyout was open and is now closed.</summary>
    /// <summary>
    /// Mac onDismissMenuBar, for zoom's Escape: closes the error window if it shows, else the tray flyout if it is open, and
    /// says whether it closed one, so that Escape does nothing else.
    /// </summary>
    private bool DismissShownWindow()
    {
        if (_errors?.DismissIfShown() == true)
        {
            return true;
        }
        if (_flyout is not { } flyout)
        {
            return false;
        }
        flyout.Dismiss(restoreFocus: true);
        return true;
    }

    private void OnTray(TrayInvocation invocation, System.Drawing.Point anchor)
    {
        if (_quitting)
        {
            return;
        }
        switch (invocation)
        {
            case TrayInvocation.KeyboardSelect when _flyout is not null:
                // The shell can repeat a keyboard selection; the open flyout already has focus.
                break;
            case TrayInvocation.Select or TrayInvocation.ContextMenu when _flyout is not null:
                _flyout.Dismiss(restoreFocus: true);
                break;
            case TrayInvocation.Select or TrayInvocation.ContextMenu
                when _flyoutClosedByIcon is { } closed && Environment.TickCount64 - closed < IconClickMilliseconds:
                // Pressing the icon closed the open flyout; releasing it must not open the flyout again.
                _flyoutClosedByIcon = null;
                break;
            default:
                OpenFlyout(anchor, openedWithKeyboard: invocation == TrayInvocation.KeyboardSelect);
                break;
        }
    }

    private void OpenFlyout(System.Drawing.Point anchor, bool openedWithKeyboard)
    {
        CloseTransientWindows();
        RectInt32 icon = _tray!.Bounds() is { } rect
            ? new RectInt32(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top)
            : new RectInt32(anchor.X, anchor.Y, 1, 1);
        // Check for updates… works in a copy installed from the Store or a portable copy with the website's feed; otherwise it
        // says why (Mac updateStatus). A release a check found turns it into Update available….
        var flyout = new TrayFlyout(_registry, _model.Snapshot, _tray.Problem, openedWithKeyboard, TrayFocusTarget, Perform,
            BuildInfo.IsDevelopment, _updates.CanCheck, _updates.Status, updateAvailable: _updates.Release is not null);
        flyout.Dismissed += () =>
        {
            if (flyout.ClosedByDeactivation && PressedOnTrayIcon())
            {
                _flyoutClosedByIcon = Environment.TickCount64;
            }
            if (_flyout == flyout)
            {
                _flyout = null;
                PostFrame();
            }
        };
        _flyout = flyout;
        PostFrame();
        flyout.Open(icon, PopoverPlacement.AboveOrBelow);
        PersonOpenedAnnotator();
        CheckForUpdatesIfDue();
    }

    /// <summary>True when the pointer is on the tray icon, so the press that just closed the flyout was on the icon.</summary>
    private bool PressedOnTrayIcon() =>
        _tray?.Bounds() is { } icon && WindowChrome.TryCursor() is { } cursor &&
        cursor.X >= icon.left && cursor.X < icon.right && cursor.Y >= icon.top && cursor.Y < icon.bottom;

    /// <summary>
    /// Where focus goes when the tray flyout or menu closes. While drawing it goes back to the canvas, so Use apps,
    /// Hide and Escape then return it to the window that was active before Draw (WIN-OVR-03). Otherwise it goes back
    /// to the window that was active before the tray opened, usually the taskbar, which entering Draw then remembers.
    /// </summary>
    private HWND TrayFocusTarget(HWND before)
    {
        WorkspaceSnapshot snapshot = _model.Snapshot;
        if (snapshot.Surfaces == SurfaceMode.Draw && (snapshot.ActiveDisplay ?? snapshot.FocusRequest?.Display) is { } display &&
            _overlays.OverlayFor(display) is var overlay and not 0)
        {
            return new HWND(overlay);
        }
        return before;
    }

    /// <summary>Opens the color popover for the stroke or the fill next to <paramref name="anchor"/> (ToolbarWindow.OpenColorPicker).</summary>
    private void OpenColorPicker(ColorPickerRequest request, RectInt32 anchor)
    {
        CloseTransientWindows();
        var picker = new ColorPopover(_registry, _model, request, _screenColors);
        picker.Dismissed += () =>
        {
            if (_colorPicker == picker)
            {
                _colorPicker = null;
                PostFrame();
            }
        };
        _colorPicker = picker;
        PostFrame();
        picker.Open(anchor, PopoverPlacement.BelowOrAbove);
    }

    private void CloseTransientWindows()
    {
        _flyout?.Dismiss(restoreFocus: false);
        _colorPicker?.Dismiss(restoreFocus: false);
        _arrowHeads?.Dismiss(restoreFocus: false);
    }

    /// <summary>
    /// Mac openToolLibrary: shows the toolbar, then the Tools window on <paramref name="section"/>, or on the section it showed
    /// last. The window is made on first use and kept, hidden, for the next time.
    /// </summary>
    private void OpenToolLibrary(ToolLibrarySection? section)
    {
        if (_quitting)
        {
            return;
        }
        CloseTransientWindows();
        _model.ShowToolbar();
        _tools ??= new ToolsWindow(_registry, _model, _library, _thumbnails,
            () => _model.Snapshot.ToolbarVisible ? _toolbar.Frame : null, TrayFocusTarget, OpenSettings, _files);
        _tools.Show(section);
    }

    /// <summary>
    /// Mac openPreferences(tab:): Use apps, then the Settings window on <paramref name="tab"/>. The window is made on first
    /// use and kept, hidden, for the next time.
    /// </summary>
    private void OpenSettings(SettingsTab tab)
    {
        if (_quitting)
        {
            return;
        }
        CloseTransientWindows();
        _model.UseApps();
        if (_settings is null)
        {
            _settings = new SettingsWindow(_registry, _settingsActions, () => _model.Snapshot.ToolbarVisible ? _toolbar.Frame : null);
            // The coffee reminder waits while Settings shows (the Mac's settingsVisible).
            _settings.ShownChanged += UpdateSupportReminder;
            // The Toolbar & Shortcuts and Pointer pages join Settings through its seam (WIN-UI-03).
            _settings.SetPage(SettingsTab.Toolbar, new ToolbarShortcutsPage(_model));
            _settings.SetPage(SettingsTab.Pointer, new PointerPage(_model));
            _settings.SetPage(SettingsTab.BreakTimer, new BreakTimerPage(_model, _breaks));
        }
        _settings.Show(tab);
        CheckForUpdatesIfDue();
    }

    /// <summary>
    /// Check for updates… in the tray (the Mac's checkForUpdates): Settings opens on About, where the line under the button
    /// follows the check as Sparkle's window does on the Mac, and the Microsoft Store is asked (WIN-SYS-03).
    /// </summary>
    private void CheckForUpdates()
    {
        if (_updates.CanCheck)
        {
            OpenSettings(SettingsTab.About);
        }
        _settingsActions.CheckForUpdates();
    }

    /// <summary>
    /// Annotator's question before a Store install that could close it while drawings exist or a region is shared, over
    /// Settings (WIN-SYS-03). Input goes back to the apps first, as when Settings opens.
    /// </summary>
    private Task<bool> AskBeforeUpdateAsync(UpdateQuestion question)
    {
        if (_quitting)
        {
            return Task.FromResult(false);
        }
        if (_settings is null)
        {
            OpenSettings(SettingsTab.About);
        }
        else
        {
            CloseTransientWindows();
            _model.UseApps();
        }
        return _settings is { } settings ? settings.AskBeforeUpdateAsync(question) : Task.FromResult(false);
    }

    /// <summary>The saved theme for app windows and the tray icon's visibility (the Mac's onThemeChanged and onMenuBarVisibilityChanged).</summary>
    private void ApplyAppearance(WorkspaceSnapshot snapshot)
    {
        AppTheme theme = snapshot.Preferences.Theme ?? AppTheme.System;
        if (theme != _theme)
        {
            _theme = theme;
            AppResources.SetTheme(theme);
        }
        bool trayShown = snapshot.Preferences.ShowMenuBarIcon ?? true;
        if (_tray is not null && trayShown != _trayShown)
        {
            _trayShown = trayShown;
            _tray.SetVisible(trayShown);
        }
    }

    /// <summary>
    /// Mac registerProfileShortcuts: an imported profile's global shortcuts replace the current ones. When one cannot be
    /// registered, the previous ones come back and the reason is returned, so the profile is not used.
    /// </summary>
    private string? RegisterProfileShortcuts(WorkspacePreferences incoming)
    {
        if (_shortcuts is null)
        {
            return null;
        }
        WorkspacePreferences previous = _model.Snapshot.Preferences;
        _shortcuts.Dispose();
        _shortcuts = new GlobalShortcuts(OnShortcut);
        IReadOnlyList<GlobalShortcutState> states = _shortcuts.Register(incoming);
        if (states.FirstOrDefault(state => state.Status is GlobalShortcutStatus.AlreadyUsed or GlobalShortcutStatus.Refused) is { } failed)
        {
            _shortcuts.Dispose();
            _shortcuts = new GlobalShortcuts(OnShortcut);
            states = _shortcuts.Register(previous);
            foreach (GlobalShortcutState state in states)
            {
                _model.SetGlobalShortcutState(state);
            }
            return failed.Message ?? $"The {failed.Label} shortcut could not be registered, so the profile was not imported.";
        }
        foreach (GlobalShortcutState state in states)
        {
            _model.SetGlobalShortcutState(state);
        }
        return null;
    }

    /// <summary>The toolbar's rectangle in physical pixels while it shows, where windows that appear for it are centered.</summary>
    private RectInt32? ToolbarAnchor() => _model.Snapshot.ToolbarVisible ? _toolbar.Frame : null;

    /// <summary>Coarse app and system facts for support info (WIN-PRV-04): no paths, names or screen contents.</summary>
    private SupportFacts CollectSupportFacts()
    {
        Version os = Environment.OSVersion.Version;
        static string Architecture(System.Runtime.InteropServices.Architecture value) => value.ToString().ToLowerInvariant();
        return new SupportFacts
        {
            Version = BuildInfo.VersionLabel,
            BuildLabel = BuildInfo.BuildLabel,
            Installation = Annotator.Platform.Updates.StoreUpdateService.Source switch
            {
                Annotator.Platform.Updates.PackageSource.Store => "Microsoft Store package",
                Annotator.Platform.Updates.PackageSource.Sideloaded => "package not from the Microsoft Store",
                _ => BuildInfo.IsPortable ? "portable copy (not packaged)" : "development build (not packaged)",
            },
            Windows = $"{(os.Build >= 22000 ? "11" : "10")} (build {os.Build.ToString(System.Globalization.CultureInfo.InvariantCulture)})",
            Architecture = $"{Architecture(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)} on " +
                Architecture(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture),
            Screens = _displays.Count,
            UpdatesConfigured = _updates.IsConfigured,
            AutomaticUpdateChecks = _updates.AutomaticChecks,
            LaunchAtSignIn = _launch.State,
            AutomatedTest = _check is not null,
            CapturePermission = CaptureAccess.Describe(CaptureAccess.Check()),
            Capture = _capture.IsCapturing ? "running" : _capture.Suspension is { } paused ? $"paused ({paused})" : "stopped",
        };
    }

    /// <summary>The full properties panel's arrowhead choices for the start or end, below the button that opened them.</summary>
    private void OpenArrowHeads(bool start, RectInt32 anchor, Action closed)
    {
        CloseTransientWindows();
        var palette = new ArrowHeadPalette(_registry, _model, start);
        palette.Dismissed += () =>
        {
            closed();
            if (_arrowHeads == palette)
            {
                _arrowHeads = null;
                PostFrame();
            }
        };
        _arrowHeads = palette;
        PostFrame();
        palette.Open(anchor, PopoverPlacement.BelowOrAbove);
    }

    private void Perform(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.ShowTools:
                _model.OpenDrawing();
                break;
            case TrayAction.HideTools:
                _model.HideTools();
                break;
            case TrayAction.Draw:
                _model.ResumeDrawing();
                break;
            case TrayAction.UseApps:
                _model.UseApps();
                break;
            case TrayAction.Settings:
                OpenSettings(SettingsTab.General);
                break;
            case TrayAction.Help:
                OpenSettings(SettingsTab.Help);
                break;
            case TrayAction.About:
                OpenSettings(SettingsTab.About);
                break;
            case TrayAction.CheckForUpdates:
                CheckForUpdates();
                break;
            case TrayAction.StopZoom:
                _model.StopZoom();
                break;
            case TrayAction.ToggleRegionSharing:
                _model.RegionSharing?.Toggle();
                break;
            case TrayAction.ShareLeftHalf:
                _model.RegionSharing?.Share(Annotator.Core.ShareRegionPreset.LeftHalf);
                break;
            case TrayAction.ShareRightHalf:
                _model.RegionSharing?.Share(Annotator.Core.ShareRegionPreset.RightHalf);
                break;
            case TrayAction.ShareRegion:
                _model.OpenRegionSharing();
                break;
            case TrayAction.OpenBoard:
                _files.OpenBoard();
                break;
            case TrayAction.StartBreak or TrayAction.BreakFive or TrayAction.BreakTen or TrayAction.BreakFifteen or TrayAction.BreakThirty:
                _breaks.Start(TrayMenu.BreakMinutes(action));
                break;
            case TrayAction.SaveBoard:
                _files.SaveBoard();
                break;
            case TrayAction.ExportPng:
                _files.ExportPng();
                break;
            case TrayAction.Support:
                OpenSupportLink();
                break;
            case TrayAction.Quit:
                _ = QuitAsync();
                break;
        }
    }

    /// <summary>
    /// Mac openOptionalSupport: opens the support link in the default browser, or says why it could not. An opened link
    /// snoozes the coffee reminder. Returns whether the link opened.
    /// </summary>
    private bool OpenSupportLink()
    {
        if (BuildInfo.SupportLink is not { } link)
        {
            _model.Report("The optional support link is not configured yet.");
            return false;
        }
        try
        {
            using var browser = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StartupLog.Note("support-link", error.Message);
            _model.Report("The page could not open. Check your default browser and try again.");
            return false;
        }
        _reminder.Snooze();
        return true;
    }

    private void OnOverlayEvent(OverlayEvent report)
    {
        if (_quitting || _handoff.Handle(report))
        {
            return;
        }
        switch (report)
        {
            case OverlayEvent.Displays displays:
                _displays = displays.Current;
                if (_check is { } check && !_checkReady)
                {
                    WriteLiveCheckReady(check);
                }
                if (displays.Rebuilt)
                {
                    _breaks.DisplaysChanged();
                    _model.DisplaysChanged();
                    try
                    {
                        _toolbar.RecoverPlacement();
                    }
                    catch (NativeCallException error)
                    {
                        Report("The toolbar could not move back onto a display", error);
                    }
                }
                break;
            case OverlayEvent.Focus focus when focus.Change.Focus == FocusChange.ActivationRefused:
                _model.Report("Windows did not let the drawing canvas take keyboard focus. Click the canvas to use keys there.");
                break;
            case OverlayEvent.Focus:
                _model.Report("Windows did not give keyboard focus back to your app. Click the app to keep working.");
                break;
            case OverlayEvent.Devices { Error: { } error }:
                Report("The graphics device could not start", error);
                break;
            case OverlayEvent.Devices:
                break;
            case OverlayEvent.Failure failure:
                Report(failure.Operation, failure.Error);
                break;
            case OverlayEvent.Effects effects:
                OnEffects(effects);
                break;
            case OverlayEvent.DesktopSwitched:
                // WIN-OVR-06: a virtual desktop switch runs the suspension, as a Space change does on the Mac, which keeps a
                // pending zoom start.
                StartupLog.Note("session", "virtual desktop switch");
                SuspendPresentation(CaptureSuspension.RequestedByApp, cancelPendingRequests: false);
                break;
            case OverlayEvent.ZoomShown:
                _zoomInput.Apply(_model.Snapshot);
                break;
            case OverlayEvent.ZoomPresented:
                _model.NoteZoomPresented();
                break;
            case OverlayEvent.ZoomFailed failed:
                StartupLog.Note("zoom", $"{failed.Display.Value} {failed.Mode}: {failed.Message} {failed.Detail}".ReplaceLineEndings(" | "));
                _model.ReportZoomFailure(failed.Display, failed.Mode, failed.Message);
                break;
            case OverlayEvent.ZoomScroll scroll:
                System.Drawing.Point pointer = WindowChrome.TryCursor() ?? default;
                _zoomInput.LocalScroll(scroll.Display, scroll.Delta, pointer.X, pointer.Y);
                break;
            case OverlayEvent.SharingPresented presented:
                _model.RegionSharing?.OutputPresented(presented.Generation);
                break;
            case OverlayEvent.SharingWaiting waiting:
                _model.RegionSharing?.OutputWaiting(waiting.Generation);
                break;
            case OverlayEvent.SharingFailed failed:
                _model.RegionSharing?.OutputFailed(failed.Generation, failed.Message, failed.Capture);
                break;
            case OverlayEvent.SharingStopped stopped:
                _model.RegionSharing?.OutputStopped(stopped.Generation, stopped.Message);
                break;
            case OverlayEvent.SharingClosed:
                _model.RegionSharing?.OutputClosed();
                break;
        }
    }

    /// <summary>
    /// A display's effect layer changed (Mac effectsReady, noteCaptureFrame and reportCaptureFailure): the toolbar warns while
    /// the editing target's regions wait or capture failed, and the status says why.
    /// </summary>
    private void OnEffects(OverlayEvent.Effects effects)
    {
        _model.SetEffectsState(effects.Display, effects.Regions, effects.PixelsAvailable);
        if (effects.Message is { } failure)
        {
            StartupLog.Note("effects", $"{effects.Display.Value}: {effects.Detail}".ReplaceLineEndings(" | "));
            _model.ReportCaptureFailure(failure);
        }
        else if (effects.PixelsAvailable)
        {
            _model.NoteCaptureFrame();
        }
    }

    private void Report(string operation, Exception error)
    {
        StartupLog.Note("report", $"{operation}: {error}".ReplaceLineEndings(" | "));
        _model.Report($"{operation}: {error.Message}");
    }

    /// <summary>
    /// A canvas asked for a window (Ctrl+S, Ctrl+O, Ctrl+L, Ctrl+, or a menu item). Properties opens full properties in
    /// the toolbar; Ctrl+L and More shapes and tools open the Tools window, Save to My shapes... opens it on My shapes, Ctrl+,
    /// opens Settings, and Ctrl+S and Ctrl+O save and open boards. Anything else is not built on Windows yet, so the status
    /// line says so instead of doing nothing.
    /// </summary>
    private void OnCanvasRequest(DisplayId display, CanvasRequest request)
    {
        if (_quitting)
        {
            return;
        }
        switch (request)
        {
            case CanvasRequest.Properties:
                _model.ShowFullProperties();
                return;
            case CanvasRequest.ToolLibrary:
                OpenToolLibrary(null);
                return;
            case CanvasRequest.SaveToMyShapes:
                OpenToolLibrary(ToolLibrarySection.MyShapes);
                return;
            case CanvasRequest.Settings:
                OpenSettings(SettingsTab.General);
                return;
            case CanvasRequest.SaveBoard:
                _files.SaveBoard();
                return;
            case CanvasRequest.OpenBoard:
                _files.OpenBoard();
                return;
            case CanvasRequest.FocusTools:
                // Mac focusDrawingTools: the toolbar takes the keyboard (WIN-A11Y-02); a hidden toolbar leaves it on the canvas.
                CloseTransientWindows();
                if (_model.Snapshot.ToolbarVisible)
                {
                    _toolbar.FocusTools();
                }
                return;
        }
        _model.Perform(new CanvasCommand(CanvasCommandKind.Status, display) { Message = "This is not ready on Windows yet." });
    }

    /// <summary>
    /// Reports a failure from any thread on the app thread, after the current work. A message that is already shown
    /// is not reported again, so a failure that repeats on every snapshot cannot keep publishing.
    /// </summary>
    private void ReportLater(string operation, Exception error)
    {
        string message = $"{operation}: {error.Message}";
        StartupLog.Note("report", $"{operation}: {error}".ReplaceLineEndings(" | "));
        _queue.TryEnqueue(() =>
        {
            if (!_quitting && _model.Snapshot.ErrorMessage != message)
            {
                _model.Report(message);
            }
        });
    }

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }
        _quitting = true;
        try
        {
            // A file dialog closes as cancelled first; then a save or export that is being written finishes (bounded), and saves
            // are atomic either way.
            _files.CancelDialog();
            CloseTransientWindows();
            CloseFirstUse();
            // Mac quit: the break screen closes and the display may sleep again.
            _breaks.Shutdown();
            // Mac quit: sharing stops and its windows close; the output goes with the overlays.
            _model.RegionSharing?.Close();
            _sharingWindows.Dispose();
            await Task.WhenAny(_files.FinishAsync(), Task.Delay(10_000));
            _model.Published -= OnPublished;
            _errors?.Shutdown();
            _screenAccess?.Shutdown();
            _model.GlobalShortcutRegistrar = null;
            _zoomInput.Dispose();
            _shortcuts?.Dispose();
            _sessions?.Dispose();
            _listener?.Dispose();
            _tray?.Dispose();
            _tools?.Shutdown();
            _settings?.Shutdown();
            _toolbar.Shutdown();
            await _overlays.StopAsync();
            // Off the app thread: the capture thread's teardown waits are bounded (5 s each) and must not block the UI.
            Task release = Task.Run(_capture.Dispose);
            await Task.WhenAny(release, Task.Delay(12_000));
            _clipboard.Dispose();
        }
        finally
        {
            // Quit always ends the process (DispatcherShutdownMode is explicit), even when a release above failed.
            Application.Current.Exit();
        }
    }
}
