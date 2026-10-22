using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Annotator.App.Tests;

/// <summary>
/// WIN-UI-07 and WIN-A11Y-03: App.xaml gives every color of app windows a contrast-theme version made of Windows' contrast
/// colors, so a contrast theme recolors whole windows. Reads the source; needs no running app.
/// </summary>
public sealed partial class ThemeResourceTests
{
    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace s_x = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryThemeColorHasAContrastVersion()
    {
        Dictionary<string, XElement> light = Dictionary("Light"), dark = Dictionary("Dark"), contrast = Dictionary("HighContrast");
        string[] missing = [.. light.Keys.Union(dark.Keys)
            .Where(key => !key.StartsWith("SystemAccentColor", StringComparison.Ordinal) && !contrast.ContainsKey(key))
            .Order(StringComparer.Ordinal)];
        Assert.True(missing.Length == 0, $"No HighContrast version: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ContrastVersionsUseOnlyWindowsContrastColors()
    {
        var problems = new List<string>();
        foreach ((string key, XElement value) in Dictionary("HighContrast"))
        {
            if (value.Name == s_xaml + "SolidColorBrush")
            {
                string color = value.Attribute("Color")?.Value ?? string.Empty;
                if (!SystemColor().IsMatch(color))
                {
                    problems.Add($"{key}: {color}");
                }
                if (value.Attribute("Opacity") is { } opacity)
                {
                    problems.Add($"{key}: opacity {opacity.Value}");
                }
            }
            else if (value.Name == s_xaml + "StaticResource")
            {
                continue;
            }
            else if (value.Name == s_xaml + "Color" && key != "AnnotatorAccentColor")
            {
                problems.Add($"{key}: a fixed color {value.Value}");
            }
        }
        Assert.True(problems.Count == 0, $"Not Windows contrast colors:\n{string.Join('\n', problems)}");
    }

    private static Dictionary<string, XElement> Dictionary(string theme)
    {
        XDocument document = XDocument.Load(Path.Combine(Repository.AppSources, "App.xaml"));
        XElement dictionary = document.Descendants(s_xaml + "ResourceDictionary")
            .Single(element => (string?)element.Attribute(s_x + "Key") == theme);
        return dictionary.Elements().ToDictionary(element => (string)element.Attribute(s_x + "Key")!, element => element);
    }

    [GeneratedRegex(@"^\{ThemeResource SystemColor[A-Za-z]+Color\}$")]
    private static partial Regex SystemColor();
}
