using Annotator.Core;

namespace Annotator.Workspace.FirstUse;

/// <summary>
/// Port of the Mac's <c>SupportReminder</c> (spec 005, coffee reminders): after 5 days of use the first time, then 10
/// more days of use and at least 30 days after the last time it showed, the toolbar may ask once for a coffee. Days of
/// use are days on which drawing started (<see cref="RecordUse"/>), counted at most once per calendar day in the local
/// time zone, and only after a launch. Not now, a support link that opens and closing the reminder all snooze it; a
/// saved legacy opt-out (<see cref="DisabledKey"/>) is honored, though no current control sets it.
/// </summary>
public sealed class SupportReminder
{
    public const string UseDaysKey = "optional-support-use-days";
    public const string LastUseKey = "optional-support-last-use";
    public const string LastShownKey = "optional-support-last-shown";
    public const string DisabledKey = "optional-support-disabled";

    /// <summary>Days of use before the first reminder.</summary>
    public const int FirstUseDays = 5;

    /// <summary>Days of use before each later reminder.</summary>
    public const int LaterUseDays = 10;

    /// <summary>The shortest time between two reminders.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromDays(30);

    private readonly AppDefaults _defaults;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeZoneInfo _zone;
    private bool _trackingSession;
    private bool _presentedThisSession;
    private bool _available;

    /// <param name="zone">The calendar's time zone for counting days; the local one by default.</param>
    public SupportReminder(AppDefaults defaults, Func<DateTimeOffset>? now = null, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults;
        _now = now ?? (static () => DateTimeOffset.Now);
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>Raised when <see cref="Available"/> or <see cref="Enabled"/> changes.</summary>
    public event Action? Changed;

    /// <summary>The reminder may show now, when the workspace allows it (<see cref="SupportReminderRules.CanShow"/>).</summary>
    public bool Available
    {
        get => _available;
        private set
        {
            if (_available != value)
            {
                _available = value;
                Changed?.Invoke();
            }
        }
    }

    public bool Enabled => !_defaults.Bool(DisabledKey);

    /// <summary>Mac recordLaunch: days of use count from now on.</summary>
    public void RecordLaunch() => _trackingSession = true;

    /// <summary>Mac recordUse: drawing started. Counts the day once and decides whether the reminder is due.</summary>
    public void RecordUse()
    {
        if (!_trackingSession || !Enabled)
        {
            return;
        }
        DateTimeOffset today = _now();
        long days = Math.Min(10, Math.Max(0, _defaults.Integer(UseDaysKey)));
        DateTimeOffset? previousUse = _defaults.Date(LastUseKey);
        if (previousUse is not { } used || !SameDay(used, today))
        {
            _defaults.Set(LastUseKey, today);
            days = Math.Min(10, days + 1);
            _defaults.Set(UseDaysKey, days);
        }
        DateTimeOffset? previous = _defaults.Date(LastShownKey);
        bool cooldownPassed = previous is not { } shown || today - shown >= Cooldown;
        Available = days >= (previous is null ? FirstUseDays : LaterUseDays) && cooldownPassed;
    }

    /// <summary>Mac markPresented: the reminder appeared; it counts as shown once per launch.</summary>
    public void MarkPresented()
    {
        if (_presentedThisSession)
        {
            return;
        }
        _presentedThisSession = true;
        _defaults.Set(LastShownKey, _now());
        _defaults.Set(UseDaysKey, 0L);
    }

    /// <summary>Mac snooze: Not now, an opened support link or a closed reminder wait for the cooldown and more use.</summary>
    public void Snooze()
    {
        _defaults.Set(LastShownKey, _now());
        _defaults.Set(UseDaysKey, 0L);
        Available = false;
    }

    /// <summary>Mac setEnabled: kept for saved preferences; the current interface has no reminder switch.</summary>
    public void SetEnabled(bool enabled)
    {
        _defaults.Set(DisabledKey, !enabled);
        if (!enabled)
        {
            Available = false;
        }
        Changed?.Invoke();
    }

    private bool SameDay(DateTimeOffset first, DateTimeOffset second) =>
        TimeZoneInfo.ConvertTime(first, _zone).Date == TimeZoneInfo.ConvertTime(second, _zone).Date;
}

/// <summary>Mac <c>canShowSupportReminder</c>: when the coffee reminder may show over the toolbar.</summary>
public static class SupportReminderRules
{
    /// <summary>
    /// Only with a support link, in Use apps with the workspace shown, nothing animating, no zoom, no capture failure, no
    /// error, no Settings window, no introduction and no edit in progress on any display.
    /// </summary>
    public static bool CanShow(WorkspaceSnapshot snapshot, bool hasSupportLink, bool settingsVisible, bool introductionVisible)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return hasSupportLink && snapshot.Mode == WorkspaceMode.UseApps && !snapshot.WorkspaceHidden && snapshot.Visibility is null
            && !snapshot.ZoomActive && snapshot.CaptureFailureMessage is null && !settingsVisible && !introductionVisible
            && snapshot.ErrorMessage is null && !HasActiveEdit(snapshot);
    }

    internal static bool HasActiveEdit(WorkspaceSnapshot snapshot)
    {
        foreach (DisplaySnapshot display in snapshot.Displays)
        {
            if (display.ActiveEdit is not null)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>The words of the coffee reminder (Mac SupportReminderView) and its accessibility identifier.</summary>
public static class SupportReminderText
{
    public const string Title = "Enjoying Annotator?";
    public const string Detail = "Buy me a coffee if you'd like. All tools stay free.";
    public const string Support = "Buy me a coffee";
    public const string NotNow = "Not now";
    public const string NotNowHelp = "Wait at least 30 days and more active use before asking again.";
    public const string Identifier = "optional-support-reminder";

    /// <summary>The Mac's CoffeeSupportView accent, the Buy me a coffee button's tint.</summary>
    public static RGBA Accent { get; } = new(0.58, 0.29, 0.10);
}
