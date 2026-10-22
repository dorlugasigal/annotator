namespace Annotator.Workspace.Settings;

/// <summary>
/// The words of Settings, Help, About and the feedback windows: the Mac copy (CustomizationView.swift, SettingsView.swift,
/// LoginItemSettings.swift, FeedbackView.swift, ReleaseNotesView.swift, SupportReminder.swift) with the Windows terms of
/// specification 013 (WIN-PAR-05). Lines whose feature is Windows-only or not built yet say so plainly.
/// </summary>
public static class SettingsCopy
{
    // General: Startup.
    public const string Startup = "Startup";
    public const string LaunchTitle = "Launch at sign-in";
    public const string LaunchHelp = "Start Annotator for this Windows account without opening the drawing toolbar.";
    public const string LaunchChecking = "Checking Windows Startup apps...";
    public const string LaunchUpdating = "Updating Windows Startup apps...";
    public const string OpenStartupApps = "Open Startup apps...";
    public const string LaunchManaged = "Managed by Windows for this account. Not included in exported profiles.";
    public const string ShowTrayIcon = "Show tray icon";
    public const string ShowTrayIconHelp =
        "Windows may keep the icon in the hidden icons menu. You can still show the tools with your Show / hide shortcut.";
    public const string ShowIntroduction = "Show introduction";

    // General: Drawing.
    public const string Drawing = "Drawing";
    public const string ShareStyles = "Share style between tools";
    public const string ShareStylesDetail =
        "When this is on, changing the color, fill, opacity, width or line style of one tool changes it for all drawing tools. " +
        "Effects, the eraser and images keep their own settings. Text size and arrowheads stay separate.";

    // General: Appearance.
    public const string Appearance = "Appearance";
    public const string Theme = "Theme";
    public const string ThemeHelp = "Changes app controls and windows, not your drawings.";
    public const string ShowTips = "Show tips on screen";
    public const string ShowTipsHelp = "Tips in the top-left corner show your keys and what to do next while you draw.";
    public const string WorkspaceAnimation = "Workspace animation";
    public const string WorkspaceAnimationHelp =
        "Choose how drawings and tools hide and return. Off turns animation off. When Windows animation effects are off, a short fade replaces any movement.";
    public const string HidingHidesCovers = "Hiding drawings and tools also hides covers.";

    // General: Updates and Privacy.
    public const string Updates = "Updates";
    public const string CheckForUpdates = "Check for updates...";
    public const string AutomaticChecks = "Automatically check for updates";
    public const string UpdatesUnavailable = "Updates aren't set up in this build. Find new versions in Settings > About.";
    public const string WhatUpdateCheckSends = "What an update check sends";
    public const string UpdateCheckSends =
        "The Microsoft Store gets connection details, including your IP address. Your drawings and screenshots are not sent.";
    public const string Privacy = "Privacy";
    public const string ShareUsageCounts = "Share usage counts";
    public const string UsageCountsUnavailable = "Usage counts aren't available on Windows yet. Nothing is sent.";
    public const string WhatsShared = "What's shared?";
    public const string WhatsSharedDetail =
        "One daily summary: version, Windows major, chip, a random ID and broad feature counts. No drawings, screenshots, typed " +
        "text or file names. Turning this off deletes the local ID and unsent counts. Outside Annotator, the Microsoft Store " +
        "counts installs and Windows keeps its own diagnostics.";
    public const string PrivacyDetails = "Privacy details";

    // General: coffee.
    public const string EnjoyingAnnotator = "Enjoying Annotator?";
    public const string CoffeeWelcome = "Coffee is always welcome. All tools stay free.";
    public const string BuyMeACoffee = "Buy me a coffee";

    // The footer.
    public const string Profiles = "Profiles";
    public const string ProfilesHelp = "Profiles save settings and shortcuts. Import replaces these, not boards.";
    public const string ExportProfile = "Export profile...";
    public const string ImportProfile = "Import profile...";
    public const string ProfileFileName = "Annotator profile.json";

    // Permissions (WIN-INP-08, WIN-CAP-04).
    public const string NoPermissions = "Drawing needs no permissions. You're good to go.";
    public const string CaptureTitle = Capture.ScreenAccessText.Title;
    public const string CaptureDetail = Capture.ScreenAccessText.Detail;
    public const string OpenCaptureSettings = "Open screen recording settings";
    public const string AllowScreenAccess = Capture.ScreenAccessText.Allow;
    public const string CaptureNotAllowed = "Not allowed";
    public const string CaptureFailed = "Access failed";
    public const string CaptureWorking = "Working";
    public const string CaptureAllowed = "Allowed. Add a Blur, Pixelate or Distort region to check.";
    public const string InputTitle = "Shortcuts and Alt+scroll";
    public const string InputDetail = "Windows needs no permission for global shortcuts or Alt+scroll over other apps.";
    public const string InputStatus = "Not needed";
    public const string RefreshStatus = "Refresh status";
    public const string AccessNotWorking = "If access isn't working";
    public const string RecoveryCheckSettings =
        "If effect regions stay covered, check Windows Settings > Privacy & security > Screenshots and screen recording and Screenshot borders. Leave other apps' entries alone.";
    public const string RecoveryRestart =
        "Stop sharing before you quit: quitting removes covers. Save your board, reopen Annotator, then try the effect again.";
    public const string BlurNotRedaction = "Blur isn't secure redaction. App or window sharing may leave out covers.";

    // Help.
    public const string QuickStart = "Quick start";
    public const string OnlineGuide = "Online guide";
    public const string Shortcuts = "Shortcuts";
    public const string SharingLimits = "Screen-sharing limits";
    public const string SharingLimitsShares =
        "Sharing one app or window may leave out drawings and covers. Ask someone on the call to check. Hiding, quitting or a crash removes visible covers.";
    public const string SharingLimitsCovers =
        "Covers don't follow windows or scrolling content. Blur can leave details readable. Remove private details before sharing; overlays aren't secure redaction.";
    public const string OpenPermissions = "Open Permissions";
    public const string SaveAndShare = "Save and share";
    public const string SaveAndShareDetail =
        "Boards keep original pasted images, even under covers. Don't share a board with private details. For a PNG, open the export and check it first; blur can still be readable.";
    public const string UseAnAssistant = "Use an AI assistant";
    public const string AssistantTools =
        "Your AI app needs computer-use tools and its own Windows permissions. The skill adds instructions, not those tools.";
    public const string AssistantPrivacy = "That app may send screenshots and text to its model provider. Check its privacy settings first.";
    public const string SaveSkill = "Save AI skill...";
    public const string SetupGuide = "Setup guide";
    public const string SomethingToImprove = "Something we can improve?";
    public const string ReportAProblemButton = "Report a problem...";
    public const string ShareFeedbackButton = "Share feedback...";
    public const string ReportingConfigured =
        "You'll write a draft first. Remove private details before opening GitHub; submitted issues are public.";
    public const string ReportingNotConfigured = "GitHub reporting isn't set up here. You can still copy a draft.";
    public const string SupportInfoMenu = "Support info";
    public const string SupportInfoHelp = "App and system details, not drawings or screenshots. Review before sharing.";
    public const string CopySupportInfo = "Copy support info";
    public const string SaveSupportInfo = "Save support info...";
    public const string SupportInfoFileName = "Annotator support.txt";
    public const string SupportInfoCopied = "Support info copied. It does not include your drawings or images.";
    public const string SupportInfoSaved = "Support info saved. No drawing content or screen images were included.";
    public const string SupportInfoNotCopied = "Could not copy support info. Try again.";
    public const string SkillDialogTitle = "Save AI skill";
    public const string SkillDialogButton = "Save here";

    // About.
    public const string AppIconName = "Annotator app icon";
    public const string AppName = "Annotator";
    public const string LocalBuild = "Local build";
    public const string Tagline = "Draw on your desktop. Keep your app open.";
    public const string OpenDownloads = "Open Downloads page...";
    public const string BuildAndSupport = "Build & support details";
    public const string SupportInfoReview = "Support info has app and system details, not drawings or screenshots. Review it before sharing.";

    // About on Windows only: the copyright and the third-party notices that ship next to the app.
    public const string Copyright = "© 2026 Dor Lugasi-Gal. Open source under the Apache License 2.0.";
    public const string ThirdPartyNotices = "Third-party notices";
    public const string NoticeMissing = "This notice is missing from the app folder. Install the latest build to get it back.";

    // What's changed (ReleaseNotesView).
    public const string WhatsChanged = "What's changed";
    public const string NotesCameWithApp = "These notes came with this app. Opening them does not check for updates.";
    public const string KnownLimits = "Known limits";

    /// <summary>The Mac's version line: "Version" and the label (formatVersion).</summary>
    public static string VersionLine(string versionLabel) => $"Version {versionLabel}";
}
