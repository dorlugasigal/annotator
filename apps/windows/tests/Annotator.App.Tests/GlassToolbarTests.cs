using System.Globalization;
using System.Xml.Linq;

namespace Annotator.App.Tests;

/// <summary>
/// Glass toolbar (WIN-UI-09): its switch in Settings &gt; General as the Mac names it, and the toolbar's glass and raised
/// lenses, which add nothing for UI Automation and stay solid in a contrast theme. Like
/// <see cref="WindowAccessibilityTests"/>, the checks that start the development build in its UX reference mode, off screen
/// and never activated, are explicit:
/// <code>
/// dotnet test --project tests/Annotator.App.Tests -- --explicit only
/// </code>
/// </summary>
public sealed class GlassToolbarTests
{
    /// <summary>The Mac's help with the Windows term for Reduce transparency (docs/parity.md).</summary>
    private const string Help =
        "Your desktop and drawings show softly through the toolbar. When Transparency effects are off in Windows Settings, the toolbar stays solid.";

    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace s_x = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly string[] s_toolbarStates =
        ["toolbar/expanded-horizontal-properties", "toolbar/expanded-vertical-properties", "toolbar/collapsed-expanded-horizontal-draw"];

    /// <summary>
    /// More of the raised lenses (WIN-UI-09): the chosen tool and Apps while apps get the clicks, a board, Apps on the vertical
    /// one-button toolbar and the minimal toolbar's smaller corners.
    /// </summary>
    private static readonly string[] s_lensStates =
        ["toolbar/use-apps", "toolbar/board-blackboard", "toolbar/collapsed-expanded-vertical-apps", "toolbar/minimal-horizontal-properties"];

    [Fact(Explicit = true)]
    public void TheSwitchSitsUnderThemeWithTheMacsNameIdAndHelp()
    {
        using ReferenceApp app = ReferenceApp.Start("settings/tab-general-full");
        Assert.Equal("shown", app.Status);
        List<UiaElement> elements = Uia.Read(app.Window);
        int index = elements.FindIndex(element => element.Id == "glass-toolbar");
        Assert.True(index >= 0, "Settings > General has no glass-toolbar switch.");
        UiaElement glass = elements[index];
        Assert.Equal("Glass toolbar", glass.Name);
        Assert.Equal(Help, glass.Help);
        Assert.Contains("Toggle", glass.Patterns);
        Assert.True(glass.Enabled && glass.Focusable, $"{glass} can't be used with the keyboard.");
        // Directly under Theme in Appearance: the Theme pop-up, then the switch's title, then the switch.
        int theme = elements.FindIndex(element => element.Name == "Theme" && element.Type == "ComboBox");
        Assert.True(theme >= 0 && theme < index, "The switch is not under Theme.");
        Assert.Equal(new[] { "Glass toolbar" }, elements.Skip(theme + 1).Take(index - theme - 1).Select(element => element.Name));
    }

    /// <summary>
    /// The glass and its raised lenses are decoration: the same elements, words and frames as the solid toolbar, also with
    /// Smart draw and Cursor halo on, the Spotlight menu lit and the Cursor halo chip in the mode strip.
    /// </summary>
    [Fact(Explicit = true)]
    public void GlassAddsNothingForUIAutomation()
    {
        foreach (string state in s_toolbarStates.Concat(s_lensStates))
        {
            Assert.Equal(Read(state, null).Select(Describe), Read(state, null, "--glass").Select(Describe));
        }
        Assert.Equal(Read("toolbar/added-buttons", TogglesOn).Select(Describe), Read("toolbar/added-buttons", TogglesOn, "--glass").Select(Describe));
    }

    /// <summary>
    /// The raised lenses follow the glass (WIN-UI-09): none on the solid toolbar or in a contrast theme, and while the glass
    /// shows one behind each highlight of the added buttons: the chosen tool, Draw, the five toggles, the Spotlight and Zoom
    /// menus and the Properties button of the shown properties.
    /// </summary>
    [Fact(Explicit = true)]
    public void RaisedLensesShowOnlyWithTheGlass()
    {
        const string state = "toolbar/added-buttons";
        using (ReferenceApp solid = ReferenceApp.Start(state))
        {
            Assert.Equal(0, Count(solid, "GlassLens"));
        }
        using (ReferenceApp contrast = ReferenceApp.Start(state, "--contrast", "nightsky", "--glass"))
        {
            Assert.Equal(0, Count(contrast, "GlassLens"));
        }
        using ReferenceApp glass = ReferenceApp.Start(state, "--glass");
        if (Count(glass, "SystemBackdropElement") == 0)
        {
            // Transparency effects are off or the energy saver is on: the toolbar is solid, with its flat tints.
            Assert.Equal(0, Count(glass, "GlassLens"));
            Assert.Skip("Windows doesn't allow transparency on this computer now, so the toolbar is solid.");
        }
        Assert.Equal(10, Count(glass, "GlassLens"));
    }

    /// <summary>
    /// A symbol that's on, on its lens (WIN-UI-09): the accent in Light; in Dark the accent mixed 35% toward white, as the Mac
    /// blends it, so it stays readable on the dark glass.
    /// </summary>
    [Fact]
    public void ALensSymbolIsTheAccentLightenedInDark()
    {
        XDocument app = XDocument.Load(Path.Combine(Repository.AppSources, "App.xaml"));
        XElement Resource(string theme, string key) => app.Descendants(s_xaml + "ResourceDictionary")
            .Single(dictionary => (string?)dictionary.Attribute(s_x + "Key") == theme).Elements()
            .Single(element => (string?)element.Attribute(s_x + "Key") == key);
        Assert.Equal("{ThemeResource AnnotatorAccentColor}", Resource("Light", "AnnotatorLensAccentBrush").Attribute("Color")?.Value);
        string accent = Resource("Dark", "AnnotatorAccentColor").Value;
        byte Lifted(int index)
        {
            byte channel = Convert.ToByte(accent.Substring(index, 2), 16);
            return (byte)Math.Round(channel + (255 - channel) * 0.35);
        }
        Assert.Equal($"#FF{Lifted(3):X2}{Lifted(5):X2}{Lifted(7):X2}", Resource("Dark", "AnnotatorLensAccentBrush").Attribute("Color")?.Value);
    }

    [Fact(Explicit = true)]
    public void AContrastThemeKeepsTheToolbarSolid()
    {
        foreach (string state in s_toolbarStates.Concat(s_lensStates).Append("toolbar/added-buttons"))
        {
            using ReferenceApp solid = ReferenceApp.Start(state, "--contrast", "nightsky");
            using ReferenceApp glass = ReferenceApp.Start(state, "--contrast", "nightsky", "--glass");
            Assert.Equal("shown", solid.Status);
            Assert.Equal("shown", glass.Status);
            Assert.True(solid.Pixels().AsSpan().SequenceEqual(glass.Pixels()), $"{state} draws glass in a contrast theme.");
        }
    }

    private static List<UiaElement> Read(string state, Action<nint>? act, params string[] options)
    {
        using ReferenceApp app = ReferenceApp.Start(state, options);
        Assert.Equal("shown", app.Status);
        act?.Invoke(app.Window);
        List<UiaElement> elements = Uia.Read(app.Window);
        Assert.True(elements.Count > 0, $"{state} has no UI Automation elements.");
        return elements;
    }

    /// <summary>Smart draw and Cursor halo on; the Cursor halo chip joins the mode strip and the toolbar settles around it.</summary>
    private static void TogglesOn(nint window)
    {
        Uia.Toggle(window, "Smart draw");
        Uia.Toggle(window, "Cursor halo", then: "Stop cursor halo");
        Thread.Sleep(1000);
    }

    /// <summary>How many elements of <paramref name="type"/> the app laid out (its layout.txt).</summary>
    private static int Count(ReferenceApp app, string type)
    {
        Assert.Equal("shown", app.Status);
        return File.ReadLines(Path.Combine(app.Folder, "layout.txt")).Count(line => line.TrimStart().StartsWith(type + " ", StringComparison.Ordinal));
    }

    private static string Describe(UiaElement element) =>
        $"{element} help \"{element.Help}\" enabled {element.Enabled} focusable {element.Focusable} {string.Join(',', element.Patterns)} at " +
        (element.Bounds.IsEmpty ? "nowhere" : string.Create(CultureInfo.InvariantCulture,
            $"{element.Bounds.X:0.#},{element.Bounds.Y:0.#} {element.Bounds.Width:0.#}x{element.Bounds.Height:0.#}"));
}
