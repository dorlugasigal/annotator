using Annotator.Core;
using Annotator.Workspace.Settings;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Settings;

/// <summary>
/// Settings > General and its profiles in the workspace (Mac WorkspaceModel: setTheme, setShareToolStyles,
/// setAnimationStyle, setShortcutHints, setMenuBarVisible, exportPreferences, importPreferences and captureVerified).
/// </summary>
public sealed class SettingsWorkspaceTests
{
    [Fact]
    public void GeneralChoicesAreSavedInTheProfile()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);

        model.SetTheme(AppTheme.Dark);
        model.SetShortcutHints(false);
        model.SetShareToolStyles(true);
        Assert.Equal("Your next style change applies to every drawing tool.", model.Snapshot.Status);
        model.SetAnimationStyle(VisibilityAnimationStyle.Magic);

        WorkspacePreferences saved = WorkspacePreferences.Read(store.Data!);
        Assert.Equal(AppTheme.Dark, saved.Theme);
        Assert.False(saved.ShowShortcutHints);
        Assert.True(saved.ShareToolStyles);
        Assert.Equal(VisibilityAnimationStyle.Magic, saved.VisibilityAnimationStyle);
        Assert.True(saved.AnimateVisibility);
        Assert.Equal(VisibilityAnimationStyle.Magic, WorkspaceModel.ShownAnimationStyle(saved));

        model.SetShareToolStyles(false);
        Assert.Null(model.Snapshot.Preferences.ShareToolStyles);
        Assert.Equal("Each tool keeps its own style.", model.Snapshot.Status);
        model.SetAnimationStyle(VisibilityAnimationStyle.None);
        Assert.False(model.Snapshot.Preferences.AnimateVisibility);
        Assert.Equal(VisibilityAnimationStyle.None, WorkspaceModel.ShownAnimationStyle(model.Snapshot.Preferences));
        model.SetAnimationStyle(VisibilityAnimationStyle.Fluid);
        Assert.Equal(VisibilityAnimationStyle.Slide, model.Snapshot.Preferences.VisibilityAnimationStyle);
        // The Mac shows Gather for a profile without a style, and Off once animation is turned off.
        Assert.Equal(VisibilityAnimationStyle.Slide, WorkspaceModel.ShownAnimationStyle(new WorkspacePreferences()));
        Assert.Equal(VisibilityAnimationStyle.None, WorkspaceModel.ShownAnimationStyle(new WorkspacePreferences { AnimateVisibility = false }));
        Assert.Null(model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void TheTrayIconHidesOnlyWithAWorkingShowHideShortcut()
    {
        WorkspaceModel model = Create();
        model.SetTrayIconVisible(false);
        Assert.Null(model.Snapshot.Preferences.ShowMenuBarIcon);
        // The Mac reports it as CanvasError.invalidDocument, so its message starts like a drawing error (Mac follow-up).
        Assert.Equal("The drawing could not be opened: Register a working activation shortcut before hiding the tray icon.", model.Snapshot.ErrorMessage);

        model.SetGlobalShortcutState(new GlobalShortcutState(GlobalShortcutAction.ShowHide, "Ctrl+2", GlobalShortcutStatus.Registered, null));
        model.DismissError();
        model.SetTrayIconVisible(false);
        Assert.False(model.Snapshot.Preferences.ShowMenuBarIcon);
        Assert.Null(model.Snapshot.ErrorMessage);
        model.SetTrayIconVisible(true);
        Assert.True(model.Snapshot.Preferences.ShowMenuBarIcon);
    }

    [Fact]
    public void ExportedProfilesImportWithShortcutsAndNoBoardChange()
    {
        WorkspaceModel source = Create();
        source.SetTheme(AppTheme.Light);
        source.SetSmartDraw(true);
        source.SetAnimationStyle(VisibilityAnimationStyle.Pop);
        byte[] profile = source.ExportPreferences()!;

        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        model.BeginDrawing();
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 40, 40));
        AssertAccepted(model.Add(Primary, shape));
        WorkspacePreferences? registered = null;
        Assert.True(model.ImportPreferences(profile, incoming =>
        {
            registered = incoming;
            return null;
        }));
        Assert.Equal(AppTheme.Light, registered?.Theme);
        Assert.Equal(AppTheme.Light, model.Snapshot.Preferences.Theme);
        Assert.True(model.Snapshot.SmartDraw);
        Assert.Equal(VisibilityAnimationStyle.Pop, WorkspacePreferences.Read(store.Data!).VisibilityAnimationStyle);
        Assert.Equal([shape], model.Elements(Primary));
        Assert.Null(model.Snapshot.ErrorMessage);
    }

    [Fact]
    public void ImportsThatCannotBeUsedKeepEverythingAndSayWhy()
    {
        var store = new MemoryPreferencesStore();
        WorkspaceModel model = Create(store: store);
        model.SetTheme(AppTheme.Dark);
        int saves = store.SaveCount;

        Assert.False(model.ImportPreferences(new byte[WorkspaceModel.MaximumProfileBytes + 1], null));
        Assert.Equal("The drawing could not be opened: The profile is too large.", model.Snapshot.ErrorMessage);

        Assert.False(model.ImportPreferences("{\"schemaVersion\":1,"u8, null));
        Assert.NotNull(model.Snapshot.ErrorMessage);
        Assert.Equal(model.Snapshot.ErrorMessage, model.Snapshot.Status);

        // A profile that hides the tray icon needs a working Show / hide shortcut first.
        byte[] hidden = new WorkspacePreferences { ShowMenuBarIcon = false }.Data();
        Assert.False(model.ImportPreferences(hidden, null));
        Assert.Equal("The drawing could not be opened: A working activation shortcut is required before the profile can hide the tray icon.", model.Snapshot.ErrorMessage);
        Assert.True(model.ImportPreferences(hidden, null, hasTrayIcon: false));
        model.SetTheme(AppTheme.Dark);
        saves = store.SaveCount;

        // Shortcuts that cannot be registered leave the profile unused; the host already put the old ones back.
        byte[] light = new WorkspacePreferences { Theme = AppTheme.Light }.Data();
        Assert.False(model.ImportPreferences(light, _ => "The Ctrl+2 shortcut is used by another app."));
        Assert.Equal("The Ctrl+2 shortcut is used by another app.", model.Snapshot.ErrorMessage);
        Assert.Equal(AppTheme.Dark, model.Snapshot.Preferences.Theme);
        Assert.Equal(saves, store.SaveCount);
    }

    [Fact]
    public void CaptureIsAllowedUntilAFrameArrivesAndAFailureResetsIt()
    {
        WorkspaceModel model = Create();
        Assert.False(model.Snapshot.CaptureVerified);
        Assert.Equal(SettingsCopy.CaptureAllowed, SettingsStatus.Capture(model.Snapshot));
        model.NoteCaptureFrame();
        Assert.True(model.Snapshot.CaptureVerified);
        Assert.Equal(SettingsCopy.CaptureWorking, SettingsStatus.Capture(model.Snapshot));
        ulong revision = model.Snapshot.Revision;
        model.NoteCaptureFrame();
        Assert.Equal(revision, model.Snapshot.Revision);
        model.ReportCaptureFailure("Screen capture stopped. Select an effect tool to try again.");
        Assert.False(model.Snapshot.CaptureVerified);
        Assert.Equal(SettingsCopy.CaptureFailed, SettingsStatus.Capture(model.Snapshot));
        model.NoteCaptureFrame();
        Assert.True(model.Snapshot.CaptureVerified);
        Assert.Null(model.Snapshot.CaptureFailureMessage);
    }
}
