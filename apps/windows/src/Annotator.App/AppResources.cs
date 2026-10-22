using System.Diagnostics.CodeAnalysis;
using AppTheme = Annotator.Core.AppTheme;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Annotator.App;

/// <summary>
/// Reads App.xaml resources. C#/WinRT finds the wrapper type of a resource by its runtime class name; when Native AOT
/// trims that type, it wraps the resource as a base type and the cast fails. The dependencies keep the types.
/// It also keeps the app's theme (WIN-UI-07, the Mac's applyTheme): System follows Windows; Light and Dark change the app's
/// windows, never drawings. A Windows contrast theme always wins, so app windows use the contrast colors.
/// </summary>
internal static class AppResources
{
    /// <summary>The theme the person chose for app windows; Default follows Windows.</summary>
    public static ElementTheme Theme { get; private set; } = ElementTheme.Default;

    /// <summary>Raised after <see cref="Theme"/> changed, on the app thread.</summary>
    public static event Action? ThemeChanged;

    /// <summary>
    /// Raised on the app thread after Windows turned a contrast theme on or off or changed the text size (WIN-UI-07,
    /// WIN-A11Y-03), once <see cref="WatchSystem"/> runs: windows that stay open build their controls again with the new colors
    /// and size.
    /// </summary>
    public static event Action? SystemLookChanged;

    private static Windows.UI.ViewManagement.UISettings? s_settings;
    private static Microsoft.UI.Dispatching.DispatcherQueue? s_queue;
    private static bool s_contrast;
    private static double s_text;

    /// <summary>
    /// Starts raising <see cref="SystemLookChanged"/> on <paramref name="queue"/>, the app thread: Windows reports a new text
    /// size and new system colors (a contrast theme changes them) through UISettings. AccessibilitySettings is not used: in
    /// this desktop process it failed at start with 0x80070490 (element not found).
    /// </summary>
    public static void WatchSystem(Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        if (s_settings is not null)
        {
            return;
        }
        s_queue = queue;
        s_contrast = Shell.ContrastTheme.IsOn;
        s_text = Shell.TextScaling.Factor;
        s_settings = new Windows.UI.ViewManagement.UISettings();
        s_settings.TextScaleFactorChanged += (_, _) => CheckSystemLook();
        s_settings.ColorValuesChanged += (_, _) => CheckSystemLook();
    }

    /// <summary>
    /// Raises <see cref="SystemLookChanged"/> on the app thread when the contrast theme or the text size differs from the last
    /// check; any thread may call it (the tray icon's window calls it for WM_SETTINGCHANGE as well).
    /// </summary>
    public static void CheckSystemLook() => s_queue?.TryEnqueue(() =>
    {
        bool contrast = Shell.ContrastTheme.IsOn;
        double text = Shell.TextScaling.Factor;
        if (contrast == s_contrast && text == s_text)
        {
            return;
        }
        s_contrast = contrast;
        s_text = text;
        SystemLookChanged?.Invoke();
    });

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof(Style))]
    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    /// <summary>A brush of the app's theme: the chosen theme's dictionary, or the resources Windows' theme selects.</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors, typeof(SolidColorBrush))]
    public static Brush Brush(string key)
    {
        if (Theme != ElementTheme.Default && !Shell.ContrastTheme.IsOn
            && Application.Current.Resources.ThemeDictionaries.TryGetValue(Theme == ElementTheme.Light ? "Light" : "Dark", out object? found)
            && found is ResourceDictionary dictionary && dictionary.TryGetValue(key, out object? value) && value is Brush brush)
        {
            return brush;
        }
        return (Brush)Application.Current.Resources[key];
    }

    /// <summary>Uses <paramref name="theme"/> for app windows from now on; windows that follow it update at once.</summary>
    public static void SetTheme(AppTheme theme)
    {
        ElementTheme chosen = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (chosen == Theme)
        {
            return;
        }
        Theme = chosen;
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Makes <paramref name="root"/>, and the title bar of <paramref name="window"/> when given, follow the app's theme while
    /// the root is in a window.
    /// </summary>
    public static void FollowTheme(FrameworkElement root, AppWindow? window = null)
    {
        bool following = false;
        void Apply()
        {
            root.RequestedTheme = Theme;
            if (window is not null)
            {
                window.TitleBar.PreferredTheme = Theme switch
                {
                    ElementTheme.Light => TitleBarTheme.Light,
                    ElementTheme.Dark => TitleBarTheme.Dark,
                    _ => TitleBarTheme.UseDefaultAppMode,
                };
            }
        }
        root.Loaded += (_, _) =>
        {
            if (!following)
            {
                following = true;
                ThemeChanged += Apply;
            }
            Apply();
        };
        root.Unloaded += (_, _) =>
        {
            following = false;
            ThemeChanged -= Apply;
        };
        Apply();
    }
}
