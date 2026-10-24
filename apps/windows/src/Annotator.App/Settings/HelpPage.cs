using Annotator.App.Shell;
using Annotator.Workspace.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Annotator.App.Settings;

/// <summary>
/// Settings > Help (the Mac's SettingsView): the guide buttons, three disclosure groups on screen sharing, saving and
/// AI assistants, the feedback buttons and Support info. Text is callout-sized, as the Mac's .font(.callout). Tutorial
/// opens the hands-on tutorial (WIN-UI-04).
/// </summary>
internal static class HelpPage
{
    private const double TextWidth = 794 - 2 * 28 - 12;

    public static FrameworkElement Make(SettingsActions actions)
    {
        var stack = new MacStack { Spacing = 22, HorizontalAlignment = HorizontalAlignment.Stretch };

        var guides = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        var quickStart = new SettingsButton(SettingsCopy.Tutorial, ButtonLook.Prominent, SettingsWidths.Button(SettingsCopy.Tutorial),
            symbol: "play.rectangle", fontSize: 12);
        quickStart.Click += (_, _) => actions.ShowTutorial?.Invoke();
        guides.Children.Add(quickStart);
        guides.Children.Add(Button(SettingsCopy.OnlineGuide, "book", () => actions.OpenProductPage(ProductPage.Help)));
        guides.Children.Add(Button(SettingsCopy.Shortcuts, "keyboard", () => actions.OpenTab?.Invoke(SettingsTab.Toolbar)));
        stack.Children.Add(guides);
        stack.Children.Add(Divider());

        stack.Children.Add(new SettingsDisclosure(SettingsCopy.SharingLimits, "help-sharing", 12, () =>
        {
            var content = new MacStack { Spacing = 10 };
            content.Children.Add(Callout(SettingsCopy.SharingLimitsShares));
            content.Children.Add(Callout(SettingsCopy.SharingLimitsCovers));
            content.Children.Add(Button(SettingsCopy.OpenPermissions, null, () => actions.OpenTab?.Invoke(SettingsTab.Permissions)));
            return content;
        }));
        stack.Children.Add(new SettingsDisclosure(SettingsCopy.SaveAndShare, "help-saving", 12, () => Callout(SettingsCopy.SaveAndShareDetail)));
        stack.Children.Add(new SettingsDisclosure(SettingsCopy.UseAnAssistant, "help-ai", 12, () =>
        {
            var content = new MacStack { Spacing = 12 };
            content.Children.Add(Callout(SettingsCopy.AssistantTools));
            content.Children.Add(Callout(SettingsCopy.AssistantPrivacy));
            var buttons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Button(SettingsCopy.SaveSkill, null, actions.SaveSkill));
            buttons.Children.Add(Button(SettingsCopy.SetupGuide, null, () => actions.OpenProductPage(ProductPage.Ai)));
            content.Children.Add(buttons);
            return content;
        }));
        stack.Children.Add(Divider());

        var feedback = new MacStack { Spacing = 10 };
        feedback.Children.Add(new FormText(SettingsCopy.SomethingToImprove, 13, weight: FontWeights.SemiBold));
        var reports = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        reports.Children.Add(Button(SettingsCopy.ReportAProblemButton, null, () => actions.OpenFeedback?.Invoke(FeedbackKind.Issue)));
        reports.Children.Add(Button(SettingsCopy.ShareFeedbackButton, null, () => actions.OpenFeedback?.Invoke(FeedbackKind.Feedback)));
        feedback.Children.Add(reports);
        feedback.Children.Add(SettingsForm.Caption(actions.IssueUrl is null ? SettingsCopy.ReportingNotConfigured : SettingsCopy.ReportingConfigured,
            wrapWidth: TextWidth));
        stack.Children.Add(feedback);

        var support = new SettingsMenuButton(SettingsCopy.SupportInfoMenu, SettingsWidths.Button(SettingsCopy.SupportInfoMenu) ?? 122.5,
            SettingsCopy.SupportInfoHelp);
        support.Add(SettingsCopy.CopySupportInfo, actions.CopySupportInfo);
        support.Add(SettingsCopy.SaveSupportInfo, actions.SaveSupportInfo);
        stack.Children.Add(support);
        return SettingsForm.Scroll(stack, new Thickness(28));
    }

    private static SettingsButton Button(string title, string? symbol, Action action)
    {
        var button = new SettingsButton(title, ButtonLook.Bordered, SettingsWidths.Button(title), symbol: symbol, fontSize: 12);
        button.Click += (_, _) => action();
        return button;
    }

    private static FormText Callout(string text) => new(text, 12, wrapWidth: TextWidth);

    private static Microsoft.UI.Xaml.Shapes.Rectangle Divider() => new()
    {
        Height = 1,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Style = AppResources.Style("AnnotatorDivider"),
    };
}
