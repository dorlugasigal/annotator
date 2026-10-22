using Annotator.Core;
using Annotator.Workspace;

namespace Annotator.App.Shell;

/// <summary>The actions of the tray flyout and menu: the Mac's MenuBarAction, in the popover's order.</summary>
internal enum TrayAction
{
    ShowTools,
    HideTools,
    Draw,
    UseApps,
    StopZoom,
    ToggleRegionSharing,
    ShareLeftHalf,
    ShareRightHalf,
    ShareRegion,

    /// <summary>The Board disclosure row; it opens and closes its rows instead of acting.</summary>
    Board,
    OpenBoard,
    SaveBoard,
    ExportPng,
    StartBreak,
    BreakFive,
    BreakTen,
    BreakFifteen,
    BreakThirty,
    Settings,
    Help,
    About,
    CheckForUpdates,
    Support,
    Quit,
}

/// <summary>The tray flyout's rows: titles, availability and automation identifiers of the Mac popover's actions.</summary>
internal static class TrayMenu
{
    /// <summary>The Mac popover's primary action: hide when drawings and tools are shown, else show.</summary>
    public static TrayAction PrimaryAction(WorkspaceSnapshot snapshot) => snapshot.ToolsPresented ? TrayAction.HideTools : TrayAction.ShowTools;

    /// <summary>The Mac popover's sharing row: Stop region sharing while it is on, else Start region sharing.</summary>
    public static string SharingTitle(WorkspaceSnapshot snapshot) =>
        snapshot.RegionSharing.IsCapturing ? "Stop region sharing" : "Start region sharing";

    /// <summary>Whether a half button shares now: sharing is on with that half (the Mac's isSelected trait).</summary>
    public static bool IsSharing(WorkspaceSnapshot snapshot, ShareRegionPreset preset) =>
        snapshot.RegionSharing.IsCapturing && snapshot.RegionSharing.SelectedPreset == preset;

    public static string PrimaryTitle(WorkspaceSnapshot snapshot) =>
        snapshot.ToolsPresented ? "Hide drawings and tools" : "Show drawings and tools";

    /// <summary>The mode row the Mac shows while the tools are shown: Use apps in Draw, otherwise Draw.</summary>
    public static TrayAction? ModeAction(WorkspaceSnapshot snapshot) =>
        !snapshot.ToolsPresented ? null : snapshot.Mode == WorkspaceMode.Draw ? TrayAction.UseApps : TrayAction.Draw;

    /// <summary>The Mac's Stop zoom row shows while screen zoom or the pointer lens is on.</summary>
    public static bool ShowsStopZoom(WorkspaceSnapshot snapshot) => snapshot.ScreenZoomEnabled || snapshot.MagnifierEnabled;

    /// <summary>
    /// Whether the action works on Windows now. The flyout and menu show every Mac row. Check for updates… depends on the
    /// copy and the check in progress, so its callers pass <see cref="Annotator.Workspace.Updates.AppUpdates.CanCheck"/>.
    /// Buy me a coffee needs the build's support link.
    /// </summary>
    public static bool IsAvailable(TrayAction action) => action switch
    {
        TrayAction.ShowTools or TrayAction.HideTools or TrayAction.Draw or TrayAction.UseApps or TrayAction.StopZoom or TrayAction.Quit => true,
        TrayAction.Settings or TrayAction.Help or TrayAction.About => true,
        TrayAction.ToggleRegionSharing or TrayAction.ShareLeftHalf or TrayAction.ShareRightHalf or TrayAction.ShareRegion => true,
        TrayAction.Board or TrayAction.OpenBoard or TrayAction.SaveBoard or TrayAction.ExportPng => true,
        TrayAction.StartBreak or TrayAction.BreakFive or TrayAction.BreakTen or TrayAction.BreakFifteen or TrayAction.BreakThirty => true,
        TrayAction.Support => BuildInfo.SupportLink is not null,
        _ => false,
    };

    /// <summary>
    /// <see cref="IsAvailable(TrayAction)"/> for the workspace's state: Save board... and Export PNG... need a drawing session,
    /// as the Mac's rows are enabled only while the model has an active session.
    /// </summary>
    public static bool IsAvailable(TrayAction action, WorkspaceSnapshot snapshot) =>
        IsAvailable(action) && (action is not (TrayAction.SaveBoard or TrayAction.ExportPng) || snapshot.ActiveDisplay is not null);

    /// <summary>The Mac's quick break buttons, in order (MenuBarAction.quickBreaks).</summary>
    public static IReadOnlyList<TrayAction> QuickBreaks { get; } = [TrayAction.BreakFive, TrayAction.BreakTen, TrayAction.BreakFifteen, TrayAction.BreakThirty];

    /// <summary>A quick break's length; null for Start break timer, which uses the saved length (Mac breakMinutes).</summary>
    public static int? BreakMinutes(TrayAction action) => action switch
    {
        TrayAction.BreakFive => 5,
        TrayAction.BreakTen => 10,
        TrayAction.BreakFifteen => 15,
        TrayAction.BreakThirty => 30,
        _ => null,
    };

    /// <summary>The Mac's accessibility identifier of a row: "menu-" and the MenuBarAction raw value.</summary>
    public static string AutomationId(TrayAction action) => action switch
    {
        TrayAction.ShowTools => "menu-show-tools",
        TrayAction.HideTools => "menu-hide-tools",
        TrayAction.Draw => "menu-draw",
        TrayAction.UseApps => "menu-useApps",
        TrayAction.StopZoom => "menu-stopZoom",
        TrayAction.ToggleRegionSharing => "menu-toggleRegionSharing",
        TrayAction.ShareLeftHalf => "menu-shareLeftHalf",
        TrayAction.ShareRightHalf => "menu-shareRightHalf",
        TrayAction.ShareRegion => "menu-shareRegion",
        TrayAction.Board => "menu-board",
        TrayAction.OpenBoard => "menu-openBoard",
        TrayAction.SaveBoard => "menu-saveBoard",
        TrayAction.ExportPng => "menu-exportPNG",
        TrayAction.StartBreak => "menu-startBreak",
        TrayAction.BreakFive => "menu-breakFive",
        TrayAction.BreakTen => "menu-breakTen",
        TrayAction.BreakFifteen => "menu-breakFifteen",
        TrayAction.BreakThirty => "menu-breakThirty",
        TrayAction.Settings => "menu-settings",
        TrayAction.Help => "menu-help",
        TrayAction.About => "menu-about",
        TrayAction.CheckForUpdates => "menu-checkForUpdates",
        TrayAction.Support => "menu-support",
        _ => "menu-quit",
    };
}
