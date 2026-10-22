using Annotator.App.Shell;
using Annotator.Workspace;
using Annotator.Workspace.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Annotator.App.Settings;

/// <summary>
/// The content of Settings (the Mac's CustomizationView): the six tabs in the Mac's order above the chosen page, then the
/// Profiles menu, inside 20 points of padding with 14 points between them. General, Permissions, Help and About are built
/// here when first shown; Toolbar &amp; Shortcuts and Pointer show the pages set with <see cref="SettingsWindow.SetPage"/>, or
/// nothing.
/// </summary>
internal sealed partial class SettingsView
{
    public const double Padding = 20;
    public const double FooterHeight = 24;
    public const double FooterSpacing = 14;

    private readonly SettingsActions _actions;
    private readonly IReadOnlyDictionary<SettingsTab, FrameworkElement> _external;
    private readonly SettingsTabView _tabs;
    private readonly Grid _empty = new();
    private GeneralPage? _general;
    private PermissionsPage? _permissions;
    private FrameworkElement? _help;
    private FrameworkElement? _about;

    /// <param name="external">The pages other parts of the app set for Toolbar &amp; Shortcuts and Pointer.</param>
    public SettingsView(SettingsActions actions, SettingsTab tab, IReadOnlyDictionary<SettingsTab, FrameworkElement> external)
    {
        _actions = actions;
        _external = external;
        _tabs = new SettingsTabView(Show);

        var profiles = new SettingsMenuButton(SettingsCopy.Profiles, SettingsWidths.Button(SettingsCopy.Profiles) ?? 93.5, SettingsCopy.ProfilesHelp);
        profiles.Add(SettingsCopy.ExportProfile, actions.ExportProfile);
        profiles.Add(SettingsCopy.ImportProfile, actions.ImportProfile);
        var footer = new Grid { Height = FooterHeight };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.Children.Add(profiles);

        Root = new Frame(_tabs, footer) { Background = SettingsLook.Window };
        Show(tab);
    }

    public Panel Root { get; }

    public SettingsTab Tab => _tabs.Selected;

    /// <summary>Shows <paramref name="tab"/>, building its page the first time.</summary>
    public void Show(SettingsTab tab)
    {
        _tabs.Select(tab);
        _tabs.ShowPage(Page(tab));
        if (tab == SettingsTab.Permissions)
        {
            _permissions?.Apply(_actions.Model.Snapshot);
        }
    }

    /// <summary>Shows a page that was set or replaced while its tab is chosen.</summary>
    public void PageChanged(SettingsTab tab)
    {
        if (_tabs.Selected == tab)
        {
            _tabs.ShowPage(Page(tab));
        }
    }

    public void FocusTabs() => _tabs.FocusSelectedTab();

    /// <summary>Shows the workspace's preferences and capture state on the pages that exist.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        _general?.Apply(snapshot);
        _permissions?.Apply(snapshot);
    }

    /// <summary>
    /// Refresh status and becoming active (the Mac's refreshPermissions): launch at sign-in, Windows' screen access and capture
    /// are read again.
    /// </summary>
    public void Refresh()
    {
        _ = _actions.Launch.RefreshAsync();
        _actions.Model.RefreshScreenAccess();
        _permissions?.Apply(_actions.Model.Snapshot);
    }

    /// <summary>Lets go of the shown page (it may be another view's set page) and of the workspace's events.</summary>
    public void Detach()
    {
        _tabs.ShowPage(null);
        _general?.Detach();
    }

    private FrameworkElement Page(SettingsTab tab) => tab switch
    {
        SettingsTab.General => (_general ??= new GeneralPage(_actions)).Root,
        SettingsTab.Permissions => (_permissions ??= new PermissionsPage(_actions)).Root,
        SettingsTab.Help => _help ??= HelpPage.Make(_actions),
        SettingsTab.About => _about ??= AboutPage.Make(_actions),
        _ => _external.TryGetValue(tab, out FrameworkElement? page) ? page : _empty,
    };

    /// <summary>
    /// CustomizationView's VStack(spacing: 14) with 20 points of padding, in exact points: the tab view takes what the footer
    /// leaves (at least 440 points, the Mac's minHeight), and WinUI rounds each position once.
    /// </summary>
    private sealed partial class Frame : Panel
    {
        private const double MinimumTabsHeight = 440;
        private readonly FrameworkElement tabs;
        private readonly FrameworkElement footer;

        public Frame(FrameworkElement tabs, FrameworkElement footer)
        {
            this.tabs = tabs;
            this.footer = footer;
            Children.Add(tabs);
            Children.Add(footer);
        }

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? SettingsWindow.DefaultWidth : availableSize.Width;
            double height = double.IsInfinity(availableSize.Height) ? SettingsWindow.DefaultHeight : availableSize.Height;
            double tabsHeight = TabsHeight(height);
            tabs.Measure(new Windows.Foundation.Size(Math.Max(0, width - 2 * Padding), tabsHeight));
            footer.Measure(new Windows.Foundation.Size(Math.Max(0, width - 2 * Padding), FooterHeight));
            return new Windows.Foundation.Size(width, tabsHeight + 2 * Padding + FooterSpacing + FooterHeight);
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            Windows.Foundation.Point residual = MacStack.InheritedResidual(this);
            double width = Math.Max(0, finalSize.Width - 2 * Padding);
            double tabsHeight = TabsHeight(finalSize.Height);
            if (tabs is SettingsTabView view)
            {
                // WinUI hands the tab view its height rounded to pixels; the page inside is laid out from the exact one.
                view.ExactHeight = tabsHeight;
            }
            MacStack.ArrangeExact(tabs, residual.X + Padding, residual.Y + Padding, width, tabsHeight, default, scale);
            MacStack.ArrangeExact(footer, residual.X + Padding, residual.Y + Padding + tabsHeight + FooterSpacing, width, FooterHeight, default, scale);
            return finalSize;
        }

        private static double TabsHeight(double height) => Math.Max(MinimumTabsHeight, height - 2 * Padding - FooterSpacing - FooterHeight);
    }
}
