namespace Annotator.App.Tests;

/// <summary>
/// Glass toolbar (WIN-UI-09): its switch in Settings &gt; General as the Mac names it, and the toolbar's glass, which adds
/// nothing for UI Automation and stays solid in a contrast theme. Like <see cref="WindowAccessibilityTests"/>, the checks start
/// the development build in its UX reference mode, off screen and never activated, so they are explicit:
/// <code>
/// dotnet test --project tests/Annotator.App.Tests -- --explicit only
/// </code>
/// </summary>
public sealed class GlassToolbarTests
{
    /// <summary>The Mac's help with the Windows term for Reduce transparency (docs/parity.md).</summary>
    private const string Help =
        "Your desktop and drawings show softly through the toolbar. When Transparency effects are off in Windows Settings, the toolbar stays solid.";

    private static readonly string[] s_toolbarStates =
        ["toolbar/expanded-horizontal-properties", "toolbar/expanded-vertical-properties", "toolbar/collapsed-expanded-horizontal-draw"];

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

    [Fact(Explicit = true)]
    public void GlassAddsNothingForUIAutomation()
    {
        foreach (string state in s_toolbarStates)
        {
            List<UiaElement> solid = Read(state);
            List<UiaElement> glass = Read(state, "--glass");
            Assert.True(solid.Count > 0, $"{state} has no UI Automation elements.");
            Assert.Equal(solid.Select(Describe), glass.Select(Describe));
        }
    }

    [Fact(Explicit = true)]
    public void AContrastThemeKeepsTheToolbarSolid()
    {
        foreach (string state in s_toolbarStates)
        {
            using ReferenceApp solid = ReferenceApp.Start(state, "--contrast", "nightsky");
            using ReferenceApp glass = ReferenceApp.Start(state, "--contrast", "nightsky", "--glass");
            Assert.Equal("shown", solid.Status);
            Assert.Equal("shown", glass.Status);
            Assert.True(solid.Pixels().AsSpan().SequenceEqual(glass.Pixels()), $"{state} draws glass in a contrast theme.");
        }
    }

    private static List<UiaElement> Read(string state, params string[] options)
    {
        using ReferenceApp app = ReferenceApp.Start(state, options);
        Assert.Equal("shown", app.Status);
        return Uia.Read(app.Window);
    }

    private static string Describe(UiaElement element) =>
        $"{element} help \"{element.Help}\" enabled {element.Enabled} focusable {element.Focusable} {string.Join(',', element.Patterns)}";
}
