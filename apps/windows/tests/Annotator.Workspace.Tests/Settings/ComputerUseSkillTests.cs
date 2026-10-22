using System.Text.RegularExpressions;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Settings;

namespace Annotator.Workspace.Tests.Settings;

/// <summary>
/// The bundled annotator skill (WIN-AI-01): Save AI skill... exports the source file with the Mac's pinned phrases
/// (apps/macos/Tests/NativeAppTests/ComputerUseSkillTests.swift) and its Windows section, whose names are the Windows app's.
/// </summary>
public sealed partial class ComputerUseSkillTests
{
    [Fact]
    public void TheExportedSkillKeepsTheMacPhrasesAndHasTheWindowsSection()
    {
        string source = SettingsLogicTests.RepositoryFile("integrations", "computer-use", "annotator", "SKILL.md");
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch-skill", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string saved = ComputerUseSkill.Export(scratch, source);
            string content = File.ReadAllText(Path.Combine(saved, ComputerUseSkill.FileName));
            Assert.Equal(File.ReadAllText(source), content);
            string words = Whitespace().Replace(content, " ");

            // The Mac's ComputerUseSkillTests: these stay unchanged.
            foreach (string phrase in new[] { "name: annotator", "Option-scroll", "computer-use", "Start arrowhead", "Database" })
            {
                Assert.Contains(phrase, content, StringComparison.Ordinal);
            }
            foreach (string phrase in new[]
            {
                "only two drag endpoints", "repeatable endpoints, not random target positions", "unauthorized client, stop native control",
                "Do not promise human-like pen movement", "not evidence of a new computer-use session",
            })
            {
                Assert.Contains(phrase, words, StringComparison.Ordinal);
            }

            // WIN-AI-01: Ctrl keys, Snipping Tool with Win+Shift+S and its recorder, Windows Settings paths and the tray icon.
            Assert.Contains("## On Windows", content, StringComparison.Ordinal);
            Assert.Contains("macOS or Windows", content.Split('\n')[2], StringComparison.Ordinal);
            foreach (string phrase in new[]
            {
                "Ctrl+Shift+2", "Ctrl+2", "Ctrl+1", "Ctrl+Alt+Shift+2", "Ctrl+Enter", "Ctrl+L", "Alt+scroll", "Backspace deletes like Delete",
                "Snipping Tool", "Win+Shift+S", "**Record** mode", "tray icon near the clock", "**Annotator menu**", "**Show hidden icons**",
                "Windows Settings > Privacy & security > Screenshots and screen recording", "Windows Settings > Personalization > Taskbar > Other system tray icons",
                "Windows Settings > Apps > Startup", "Windows Settings > Accessibility > Visual effects > Animation effects",
                "Windows Settings > System > Notifications", "`Annotator.exe`", "**Annotator Drawing Tools**", "**Annotator Settings**",
                "**Use apps**", "**Apps receive input**", "**Pick from screen**", "**Export PNG**", "**Save board**",
            })
            {
                Assert.Contains(phrase, words, StringComparison.Ordinal);
            }

            // Names that the Windows workspace defines.
            Assert.Contains($"**{CanvasAccessibility.Label}**", words, StringComparison.Ordinal);
            Assert.Contains($"**{SettingsCopy.LocalBuild}**", words, StringComparison.Ordinal);
            Assert.Contains("Windows Settings > Privacy & security > Screenshots and screen recording", SettingsCopy.RecoveryCheckSettings, StringComparison.Ordinal);
            Assert.Contains("Windows Settings > Personalization > Taskbar", IntroductionText.TrayActionHelp, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
