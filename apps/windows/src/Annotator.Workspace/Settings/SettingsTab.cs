namespace Annotator.Workspace.Settings;

/// <summary>The Settings tabs in the Mac's order (CustomizationTab).</summary>
public enum SettingsTab
{
    General,
    Toolbar,
    Pointer,
    BreakTimer,
    Permissions,
    Help,
    About,
}

public static class SettingsTabExtensions
{
    extension(SettingsTab tab)
    {
        /// <summary>The Mac's raw value, as the reference states name the tabs (tab-general, tab-toolbar and so on).</summary>
        public string RawValue => tab switch
        {
            SettingsTab.General => "general",
            SettingsTab.Toolbar => "toolbar",
            SettingsTab.Pointer => "pointer",
            SettingsTab.BreakTimer => "breakTimer",
            SettingsTab.Permissions => "permissions",
            SettingsTab.Help => "help",
            SettingsTab.About => "about",
            _ => throw new ArgumentOutOfRangeException(nameof(tab)),
        };

        /// <summary>The tab's title (Mac tabItem Label).</summary>
        public string Title => tab switch
        {
            SettingsTab.General => "General",
            SettingsTab.Toolbar => "Toolbar & Shortcuts",
            SettingsTab.Pointer => "Pointer",
            SettingsTab.BreakTimer => "Break timer",
            SettingsTab.Permissions => "Permissions",
            SettingsTab.Help => "Help",
            SettingsTab.About => "About",
            _ => throw new ArgumentOutOfRangeException(nameof(tab)),
        };

        /// <summary>The tab's SF Symbol. The Mac's tab buttons report it as their accessibility identifier.</summary>
        public string Symbol => tab switch
        {
            SettingsTab.General => "gearshape",
            SettingsTab.Toolbar => "keyboard",
            SettingsTab.Pointer => "cursorarrow.rays",
            SettingsTab.BreakTimer => "timer",
            SettingsTab.Permissions => "lock.shield",
            SettingsTab.Help => "questionmark.circle",
            SettingsTab.About => "info.circle",
            _ => throw new ArgumentOutOfRangeException(nameof(tab)),
        };
    }

    extension(SettingsTab)
    {
        public static IReadOnlyList<SettingsTab> AllCases =>
            [SettingsTab.General, SettingsTab.Toolbar, SettingsTab.Pointer, SettingsTab.BreakTimer, SettingsTab.Permissions, SettingsTab.Help,
             SettingsTab.About];

        public static SettingsTab? FromRawValue(string rawValue) => rawValue switch
        {
            "general" => SettingsTab.General,
            "toolbar" => SettingsTab.Toolbar,
            "pointer" => SettingsTab.Pointer,
            "breakTimer" => SettingsTab.BreakTimer,
            "permissions" => SettingsTab.Permissions,
            "help" => SettingsTab.Help,
            "about" => SettingsTab.About,
            _ => null,
        };
    }
}
