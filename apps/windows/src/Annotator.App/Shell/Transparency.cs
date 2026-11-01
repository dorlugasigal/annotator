using Annotator.Workspace.Toolbar;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;

namespace Annotator.App.Shell;

/// <summary>
/// What Windows says about see-through materials, for Glass toolbar (WIN-UI-09): Transparency effects (Settings &gt;
/// Personalization &gt; Colors), a contrast theme and the energy saver. Once <see cref="Watch"/> runs, <see cref="Changed"/>
/// is raised on the app thread when Transparency effects or the energy saver change, so a glass toolbar turns solid, or
/// glass again, at once. Windows that stay open already follow a contrast theme through
/// <see cref="AppResources.SystemLookChanged"/>.
/// </summary>
internal static class Transparency
{
    private static readonly Lazy<bool> s_supported = new(DesktopAcrylicController.IsSupported);
    private static Windows.UI.ViewManagement.UISettings? s_settings;
    private static bool s_effects = true;
    private static bool s_energySaver;

    /// <summary>Raised on the app thread after Transparency effects or the energy saver changed.</summary>
    public static event Action? Changed;

    /// <summary>Windows' settings now. The contrast theme is read each time; the rest follow Windows' change events.</summary>
    public static SystemTransparency Current => s_settings is null
        ? new SystemTransparency(new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled, ContrastTheme.IsOn, ReadEnergySaver(), s_supported.Value)
        : new SystemTransparency(s_effects, ContrastTheme.IsOn, s_energySaver, s_supported.Value);

    /// <summary>Follows Windows' settings on <paramref name="queue"/>, the app thread; Windows reports them on a thread of its own.</summary>
    public static void Watch(DispatcherQueue queue)
    {
        if (s_settings is not null)
        {
            return;
        }
        var settings = new Windows.UI.ViewManagement.UISettings();
        s_settings = settings;
        s_effects = settings.AdvancedEffectsEnabled;
        s_energySaver = ReadEnergySaver();
        settings.AdvancedEffectsEnabledChanged += (_, _) => queue.TryEnqueue(() => Update(settings.AdvancedEffectsEnabled, s_energySaver));
        Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) => queue.TryEnqueue(() => Update(s_effects, ReadEnergySaver()));
    }

    private static void Update(bool effects, bool energySaver)
    {
        if (effects == s_effects && energySaver == s_energySaver)
        {
            return;
        }
        s_effects = effects;
        s_energySaver = energySaver;
        Changed?.Invoke();
    }

    private static bool ReadEnergySaver() =>
        Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
}
