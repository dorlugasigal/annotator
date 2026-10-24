using Microsoft.UI.Xaml;

namespace Annotator.App.Shell;

/// <summary>
/// Marks parts of app windows that keep their own colors in a Windows contrast theme (WIN-UI-07): annotation colors (color
/// wells, swatches, gradients and the halo's colors), pictures of drawings and effects (previews and
/// thumbnails) and the app's logo. Everything else follows the contrast theme, and the development accessibility checks
/// skip only these parts when they look for colors outside it.
/// </summary>
internal static class OwnColors
{
    public static readonly DependencyProperty IsMarkedProperty =
        DependencyProperty.RegisterAttached("IsMarked", typeof(bool), typeof(OwnColors), new PropertyMetadata(false));

    /// <summary>Marks <paramref name="element"/> and everything in it; returns it.</summary>
    public static T Mark<T>(T element) where T : DependencyObject
    {
        element.SetValue(IsMarkedProperty, true);
        return element;
    }

    public static bool IsMarked(DependencyObject element) => element.GetValue(IsMarkedProperty) is true;
}
