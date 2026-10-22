using System.Text;

namespace Annotator.Workspace.Settings;

/// <summary>Pages of the product website that Help and About open (Mac ProductPage).</summary>
public enum ProductPage
{
    Help,
    Downloads,
    Ai,
    Privacy,
}

/// <summary>
/// Port of IssueReporting.swift: the GitHub issue form, the optional support link and the website's pages. Only links the
/// build names and that pass the Mac's checks open; nothing is sent until the person opens a link.
/// </summary>
public static class IssueReporting
{
    /// <summary>The website when the build names none (the Mac's fallback).</summary>
    public const string DefaultWebsite = "https://dorlugasigal.github.io/annotator";

    public const string NotConfiguredMessage =
        "Issue reporting is not configured for this build. Send your feedback to the person who shared it.";

    public const string BrowserUnavailableMessage = "The page could not open. Check your default browser and try again.";

    public const string SupportNotConfiguredMessage = "The optional support link is not configured yet.";

    /// <summary>The page's path below the website (Mac ProductPage raw values).</summary>
    public static string Path(this ProductPage page) => page switch
    {
        ProductPage.Help => "docs/",
        ProductPage.Downloads => "downloads/",
        ProductPage.Ai => "ai/",
        ProductPage.Privacy => "privacy/",
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };

    /// <summary>
    /// Mac IssueReporting.url: a new-issue page on github.com of the form https://github.com/owner/repo/issues/new, with no
    /// user, port, query or fragment, gets the bug report or feedback template, an empty title with the version and the
    /// version field. Anything else gives null.
    /// </summary>
    public static Uri? IssueUrl(string? configured, string version, bool feedback = false)
    {
        ArgumentNullException.ThrowIfNull(version);
        const string Scheme = "https://";
        if (string.IsNullOrEmpty(configured) || configured.Contains("$(", StringComparison.Ordinal)
            || !configured.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return null;
        }
        string rest = configured[Scheme.Length..];
        if (rest.Contains('?') || rest.Contains('#'))
        {
            return null;
        }
        int slash = rest.IndexOf('/');
        string authority = slash < 0 ? rest : rest[..slash];
        if (authority != "github.com")
        {
            return null;
        }
        string[] path = slash < 0 ? [] : rest[slash..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length != 4 || path[2] != "issues" || path[3] != "new" || !IsName(path[0]) || !IsName(path[1]))
        {
            return null;
        }
        string query = Query(
            ("template", feedback ? "feedback.yml" : "bug-report.yml"),
            ("title", $"[{version}] "),
            ("version", version));
        return new Uri(configured + "?" + query);
    }

    /// <summary>
    /// Mac IssueReporting.supportURL: an https address with a host, and no user, password, port or fragment; a query is
    /// allowed. Null for anything else, including an unexpanded build setting.
    /// </summary>
    public static Uri? SupportUrl(string? value)
    {
        const string Scheme = "https://";
        if (string.IsNullOrEmpty(value) || value.Contains("$(", StringComparison.Ordinal) || value.Contains('#')
            || !value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string rest = value[Scheme.Length..];
        int end = rest.IndexOfAny(['/', '?']);
        string authority = end < 0 ? rest : rest[..end];
        return authority.Length > 0 && !authority.Contains('@') && !authority.Contains(':')
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? link) && link.Host.Length > 0 ? link : null;
    }

    /// <summary>
    /// Mac openProductPage: the page below the build's website, or below <see cref="DefaultWebsite"/> when the build names
    /// none. Null when the result fails <see cref="SupportUrl"/>.
    /// </summary>
    public static Uri? ProductPageUrl(string? configuredWebsite, ProductPage page)
    {
        string website = string.IsNullOrEmpty(configuredWebsite) || configuredWebsite.Contains("$(", StringComparison.Ordinal)
            ? DefaultWebsite : configuredWebsite;
        return SupportUrl(website) is { } root ? SupportUrl(root.AbsoluteUri.TrimEnd('/') + "/" + page.Path()) : null;
    }

    /// <summary>
    /// A URL query of names and values, percent-encoded as Foundation's URLComponents encodes query items: everything
    /// outside the URL query characters, and the item separators &amp; and =.
    /// </summary>
    public static string Query(params (string Name, string Value)[] items)
    {
        var query = new StringBuilder();
        foreach ((string name, string value) in items)
        {
            if (query.Length > 0)
            {
                query.Append('&');
            }
            Encode(query, name);
            query.Append('=');
            Encode(query, value);
        }
        return query.ToString();
    }

    private static void Encode(StringBuilder into, string text)
    {
        foreach (byte value in Encoding.UTF8.GetBytes(text))
        {
            char character = (char)value;
            if (value < 0x80 && (char.IsAsciiLetterOrDigit(character) || "-._~!$'()*+,;:@/?".Contains(character)))
            {
                into.Append(character);
            }
            else
            {
                into.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    private static bool IsName(string part) =>
        part is not ("." or "..") && part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');
}

/// <summary>The two feedback windows (Mac FeedbackKind); the raw value is the window's title.</summary>
public enum FeedbackKind
{
    Issue,
    Feedback,
}

public static class FeedbackKindExtensions
{
    extension(FeedbackKind kind)
    {
        /// <summary>The window's title and the report's first line.</summary>
        public string Title => kind == FeedbackKind.Issue ? "Report a problem" : "Share feedback";

        /// <summary>The line above the details field.</summary>
        public string Prompt => kind == FeedbackKind.Issue
            ? "What did you try, what did you expect, and what happened?"
            : "What worked well? What would you change?";

        /// <summary>The GitHub form field that receives the details.</summary>
        public string DetailsField => kind == FeedbackKind.Issue ? "steps" : "experience";
    }
}

/// <summary>A feedback problem with the Mac's message (FeedbackFailure).</summary>
public sealed class FeedbackFailure : Exception
{
    private FeedbackFailure(FeedbackFailureKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public FeedbackFailureKind Kind { get; }

    public static FeedbackFailure Empty() => new(FeedbackFailureKind.Empty, "Add a short title and describe what happened.");

    public static FeedbackFailure Unconfigured() => new(FeedbackFailureKind.Unconfigured,
        "Issue reporting is not set up in this copy. Choose Copy feedback to keep your report.");

    public static FeedbackFailure TooLongForLink() => new(FeedbackFailureKind.TooLongForLink,
        "This report is too long for a browser draft. Choose Copy feedback, then Open GitHub form and paste the report. Review it before submitting.");

    public static FeedbackFailure Clipboard() => new(FeedbackFailureKind.Clipboard, "Feedback could not be copied. Try Copy feedback again.");

    public static FeedbackFailure Browser() => new(FeedbackFailureKind.Browser,
        "The feedback page could not open. Check your browser or copy the feedback instead.");
}

public enum FeedbackFailureKind
{
    Empty,
    Unconfigured,
    TooLongForLink,
    Clipboard,
    Browser,
}

/// <summary>Port of FeedbackDraft: the report a person writes in Report a problem or Share feedback.</summary>
public sealed record FeedbackDraft(FeedbackKind Kind)
{
    /// <summary>The longest browser link the Mac opens, in UTF-8 bytes.</summary>
    public const int MaximumLinkBytes = 8_000;

    public const string TitleHeading = "Title";
    public const string TitlePlaceholder = "Short title";
    public const string TitleName = "Feedback title";
    public const string DetailsHeading = "Details";
    public const string DetailsName = "Feedback details";
    public const string Disclosure =
        "Remove private details before opening GitHub. Open GitHub draft sends your title, details and app version to GitHub in a browser link. " +
        "Submitting it there makes it public. GitHub may ask you to sign in. No drawings, screenshots or logs are attached automatically.";
    public const string CopyExplanation = "Copy feedback copies the report without opening GitHub or submitting it.";
    public const string NotConfigured = "Issue reporting is not set up in this copy. You can still copy your report.";
    public const string Copied = "Feedback copied.";
    public const string PlainForm = "Open GitHub form";
    public const string PlainFormExplanation =
        "This opens the form with your app version, without your title or details. Paste only what you want to share.";
    public const string CopyButton = "Copy feedback";
    public const string DraftButton = "Open GitHub draft";
    public const string CloseButton = "Close";

    public string Title { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    /// <summary>Mac report(version:): the text Copy feedback copies. Throws <see cref="FeedbackFailure"/> when a field is empty.</summary>
    public string Report(string version)
    {
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Details))
        {
            throw FeedbackFailure.Empty();
        }
        return $"{Kind.Title}\nVersion: {version}\n\n{Title}\n\n{Details}\n";
    }

    /// <summary>
    /// Mac githubURL(base:version:): the issue form with the title and details as a browser draft. Throws
    /// <see cref="FeedbackFailure"/> when a field is empty, reporting is not configured or the link would pass
    /// <see cref="MaximumLinkBytes"/>.
    /// </summary>
    public Uri GitHubUrl(string? configured, string version)
    {
        _ = Report(version);
        if (IssueReporting.IssueUrl(configured, version, Kind == FeedbackKind.Feedback) is null)
        {
            throw FeedbackFailure.Unconfigured();
        }
        string address = configured + "?" + IssueReporting.Query(
            ("template", Kind == FeedbackKind.Feedback ? "feedback.yml" : "bug-report.yml"),
            ("version", version),
            ("title", $"[{version}] {Title}"),
            (Kind.DetailsField, Details));
        if (Encoding.UTF8.GetByteCount(address) > MaximumLinkBytes || !Uri.TryCreate(address, UriKind.Absolute, out Uri? link))
        {
            throw FeedbackFailure.TooLongForLink();
        }
        return link;
    }
}
