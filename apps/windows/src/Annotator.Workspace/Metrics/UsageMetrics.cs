using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Annotator.Core;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Settings;
using ProcessArchitecture = System.Runtime.InteropServices.Architecture;

namespace Annotator.Workspace.Metrics;

/// <summary>The Mac's UsageMetric: the broad feature categories a daily summary counts. Raw values are shared (WIN-PAR-04).</summary>
public enum UsageMetric
{
    Drawing,
    Editing,
    TextOrShape,
    CoverOrEffect,
    Zoom,
    ImageInsertion,
    BoardOpen,
    BoardSave,
    PngExport,
    ReusableShape,
}

public static class UsageMetricExtensions
{
    extension(UsageMetric metric)
    {
        public string RawValue => metric switch
        {
            UsageMetric.Drawing => "drawing",
            UsageMetric.Editing => "editing",
            UsageMetric.TextOrShape => "textOrShape",
            UsageMetric.CoverOrEffect => "coverOrEffect",
            UsageMetric.Zoom => "zoom",
            UsageMetric.ImageInsertion => "imageInsertion",
            UsageMetric.BoardOpen => "boardOpen",
            UsageMetric.BoardSave => "boardSave",
            UsageMetric.PngExport => "pngExport",
            UsageMetric.ReusableShape => "reusableShape",
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };
    }

    /// <summary>Mac choose(_:): the category a chosen tool counts for.</summary>
    public static UsageMetric ForTool(Tool tool) =>
        tool == Tool.Select ? UsageMetric.Editing
        : tool.IsProtection ? UsageMetric.CoverOrEffect
        : tool == Tool.Text || (!tool.IsPixelEffect && tool is not (Tool.Pen or Tool.Highlighter or Tool.Laser or Tool.Eraser)) ? UsageMetric.TextOrShape
        : UsageMetric.Drawing;
}

/// <summary>
/// What a summary says about this copy of Annotator (WIN-PRV-01): the version and build, Windows' major version and the
/// app's architecture. The Mac reads its bundle, ProcessInfo and the compiled architecture.
/// </summary>
public sealed record UsageMetricsBuild(string Version, string Build, int OsMajor, string Architecture)
{
    /// <summary>The platform field of every Windows summary (specification 011).</summary>
    public const string Platform = "windows";

    /// <param name="version">The version with its prerelease suffix, without the build.</param>
    /// <param name="build">The shared build number, or null when this build has none.</param>
    public static UsageMetricsBuild ForWindows(string version, int? build, Version os, ProcessArchitecture process) =>
        new(version, build?.ToString(CultureInfo.InvariantCulture) ?? "unknown", WindowsMajor(os), ArchitectureName(process));

    /// <summary>Windows 11 still reports version 10; its builds start at 22000.</summary>
    public static int WindowsMajor(Version os)
    {
        ArgumentNullException.ThrowIfNull(os);
        return os.Major == 10 && os.Build >= 22000 ? 11 : os.Major;
    }

    /// <summary>The Mac's names: arm64, x86_64 or other.</summary>
    public static string ArchitectureName(ProcessArchitecture process) => process switch
    {
        ProcessArchitecture.X64 => "x86_64",
        ProcessArchitecture.Arm64 => "arm64",
        _ => "other",
    };
}

/// <summary>One summary for the service: the app endpoint, the JSON body and the Mac's 8-second timeout.</summary>
public sealed record UsageMetricsRequest(Uri Url, string Json, TimeSpan Timeout);

/// <summary>
/// Posts <paramref name="request"/> and returns the HTTP status. Throws <see cref="UsageMetricsSendFailure"/> when the
/// service cannot be reached and <see cref="OperationCanceledException"/> when <paramref name="cancellation"/> ends it.
/// </summary>
public delegate Task<int> UsageMetricsSend(UsageMetricsRequest request, CancellationToken cancellation);

/// <summary>The service could not be reached or did not answer in time.</summary>
public sealed class UsageMetricsSendFailure(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The words of usage counts: the Mac's statuses (UsageMetrics.swift) and the Windows-only unavailable state.</summary>
public static class UsageMetricsText
{
    public const string On = "Usage counts are on. Drawing content stays private.";
    public const string Off = "Usage counts are off.";
    public const string NotSetUp = "Sharing is on, but this build isn't set up to send counts, so nothing is sent.";
    public const string Deleted = "Usage counts are off. The local ID and unsent counts were deleted.";
    public const string NeedsNewId = "Usage counts need a new ID. Turn sharing off and on again.";
    public const string StartDateMissing = "The usage-count start date is missing. Turn sharing off and on again.";
    public const string Sent = "Usage counts were sent today.";
    public const string NotSent = "Counts couldn't be sent. We'll try on another day.";
    public const string NotRead = "Saved counts couldn't be read. Turn sharing off and on again.";

    /// <summary>WIN-PRV-01: what Settings says while Windows may not send usage counts.</summary>
    public const string Unavailable = SettingsCopy.UsageCountsUnavailable;
}

/// <summary>
/// Port of the Mac's UsageMetrics (specification 011, ADR 0009, WIN-PRV-01): optional daily usage counts, off by default.
/// Turning sharing on makes a random installation ID and the opt-in day; turning it off deletes the ID, that day and every
/// unsent count. Counts are bucketed, kept for today and the previous seven UTC days and sent at most once a UTC day, at
/// the first activity of the day. A failure never blocks drawing; it shows as the status. Windows adds the platform
/// field. Use it on the app thread.
/// </summary>
public sealed class UsageMetrics
{
    public const string EnabledKey = "usage-metrics-enabled-v1";
    public const string InstallationKey = "usage-metrics-installation-v1";
    public const string DaysKey = "usage-metrics-days-v1";
    public const string LastAttemptKey = "usage-metrics-last-attempt-v1";
    public const string SinceKey = "usage-metrics-since-v1";

    /// <summary>
    /// The Windows sending switch (WIN-PRV-01). The service deployed today rejects the Windows payload, so Windows offers
    /// no usage counts: Settings and the tutorial say they aren't available, sharing cannot be turned on and nothing is
    /// recorded or sent. Owner step: deploy services/metrics with the platform field (specification 011), then set this
    /// to true and give the release build its AnnotatorMetricsUrl.
    /// </summary>
    public const bool ServiceAcceptsWindows = false;

    /// <summary>The Mac's request timeoutInterval.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private const int Cap = 21;

    private readonly AppDefaults _defaults;
    private readonly bool _available;
    private readonly Uri? _endpoint;
    private readonly UsageMetricsBuild _build;
    private readonly Func<DateTimeOffset> _now;
    private readonly UsageMetricsSend _send;
    private readonly Action<Action>? _schedule;
    private CancellationTokenSource? _sending;
    private int _consentGeneration;
    private bool _enabled;
    private string _status;

    /// <param name="available">
    /// False keeps usage counts unavailable (<see cref="ServiceAcceptsWindows"/> in the app): nothing is read, written,
    /// recorded or sent.
    /// </param>
    /// <param name="endpoint">The service's origin (<see cref="ValidatedEndpoint"/>); null when this build has none.</param>
    /// <param name="schedule">
    /// Runs the day's automatic send later on the app thread (the Mac's Task on the main actor); null sends only when
    /// <see cref="SendDailySummaryIfNeededAsync"/> is called (the Mac's sendsAutomatically false).
    /// </param>
    public UsageMetrics(AppDefaults defaults, bool available, Uri? endpoint, UsageMetricsBuild build, UsageMetricsSend send,
                        Func<DateTimeOffset>? now = null, Action<Action>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(send);
        _defaults = defaults;
        _available = available;
        _endpoint = endpoint;
        _build = build;
        _send = send;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _schedule = schedule;
        _enabled = available && defaults.Bool(EnabledKey);
        _status = !available ? UsageMetricsText.Unavailable : _enabled ? UsageMetricsText.On : UsageMetricsText.Off;
    }

    /// <summary>Usage counts that are never available and keep nothing (reference states and tests).</summary>
    public static UsageMetrics Unavailable() =>
        new(AppDefaults.InMemory(), available: false, endpoint: null, new UsageMetricsBuild("0.0.0", "0", 11, "x86_64"),
            static (_, _) => Task.FromException<int>(new UsageMetricsSendFailure("Usage counts are not available.")));

    /// <summary>Raised on the app thread when <see cref="Enabled"/> or <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Windows may offer usage counts: the switch can be turned on (WIN-PRV-01).</summary>
    public bool Available => _available;

    /// <summary>The person turned on Share usage counts.</summary>
    public bool Enabled => _enabled;

    /// <summary>The line under the switch (Mac status).</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>The day's automatic send while it runs (tests wait for it).</summary>
    internal Task? AutomaticSend { get; private set; }

    /// <summary>The Mac's endpoint rules: an https origin with a host, without user, password, port, path, query or fragment.</summary>
    public static Uri? ValidatedEndpoint(string? input)
    {
        const string Scheme = "https://";
        string value = (input ?? string.Empty).Trim();
        if (value.Length == 0 || value.Contains("$(", StringComparison.Ordinal) || !value.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return null;
        }
        string rest = value[Scheme.Length..];
        int end = rest.IndexOfAny(['/', '?', '#']);
        string authority = end < 0 ? rest : rest[..end];
        string tail = end < 0 ? string.Empty : rest[end..];
        if (authority.Length == 0 || authority.Contains('@') || authority.Contains(':') || (tail.Length > 0 && tail != "/"))
        {
            return null;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? url) && url.Host.Length > 0 ? url : null;
    }

    /// <summary>The Mac's buckets: 0, 1, 2-5, 6-20 and 21+, never an exact larger count.</summary>
    public static string Bucket(int value) => value switch
    {
        <= 0 => "0",
        1 => "1",
        <= 5 => "2-5",
        <= 20 => "6-20",
        _ => "21+",
    };

    /// <summary>
    /// Mac setEnabled: turning on makes a random installation ID and the opt-in day when there is none, and counts a launch;
    /// turning off deletes the ID, the opt-in day and every unsent count, and cancels a send in progress. The last send day
    /// stays, so turning sharing on again sends nothing more that day.
    /// </summary>
    public void SetEnabled(bool value)
    {
        if (!_available || _enabled == value)
        {
            return;
        }
        _consentGeneration++;
        CancellationTokenSource? sending = _sending;
        _sending = null;
        sending?.Cancel();
        _enabled = value;
        _defaults.Set(EnabledKey, value);
        if (value)
        {
            if (_defaults.String(InstallationKey) is null)
            {
                // A random (version 4) GUID: no time, device or person is part of it.
                _defaults.Set(InstallationKey, Guid.NewGuid().ToString("D"));
                _defaults.Set(SinceKey, DayString(_now()));
            }
            RecordLaunch();
            _status = _endpoint is null ? UsageMetricsText.NotSetUp : UsageMetricsText.On;
        }
        else
        {
            _defaults.Remove(InstallationKey);
            _defaults.Remove(DaysKey);
            _defaults.Remove(SinceKey);
            _status = UsageMetricsText.Deleted;
        }
        Changed?.Invoke();
    }

    /// <summary>Mac recordLaunch: counts today's launch while sharing is on.</summary>
    public void RecordLaunch()
    {
        if (_enabled)
        {
            UpdateToday(static day => day.Launches = Math.Min(Cap, day.Launches + 1));
        }
    }

    /// <summary>Mac record(_:): counts one use of a feature category today while sharing is on.</summary>
    public void Record(UsageMetric metric)
    {
        if (_enabled)
        {
            string name = metric.RawValue;
            UpdateToday(day => day.Counts[name] = Math.Min(Cap, day.Counts.GetValueOrDefault(name) + 1));
        }
    }

    /// <summary>
    /// Mac sendDailySummaryIfNeeded: at most one request per UTC day, with today and the unsent days of the previous seven.
    /// A late answer after sharing was turned off, or turned off and on again, changes nothing.
    /// </summary>
    public async Task SendDailySummaryIfNeededAsync(CancellationToken cancellation = default)
    {
        if (!_enabled || _endpoint is null)
        {
            if (_enabled)
            {
                Status = UsageMetricsText.NotSetUp;
            }
            return;
        }
        string today = DayString(_now());
        int generation = _consentGeneration;
        if (_defaults.String(LastAttemptKey) == today)
        {
            return;
        }
        if (_defaults.String(InstallationKey) is not { } installation)
        {
            Status = UsageMetricsText.NeedsNewId;
            return;
        }
        if (LoadDays() is not { } saved)
        {
            return;
        }
        List<DayCounts> days = RecentDays(saved, today);
        if (days.Count == 0)
        {
            return;
        }
        if (_defaults.String(SinceKey) is not { } since)
        {
            Status = UsageMetricsText.StartDateMissing;
            return;
        }
        _defaults.Set(LastAttemptKey, today);
        var request = new UsageMetricsRequest(new Uri(_endpoint, "api/metrics/app"), PayloadJson(installation, since, _build, days), RequestTimeout);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            int answer = await _send(request, cancellation);
            if (!_enabled || generation != _consentGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }
            if (answer is < 200 or > 299)
            {
                throw new UsageMetricsSendFailure($"The service answered {answer}.");
            }
            Status = UsageMetricsText.Sent;
        }
        catch (Exception error) when (error is UsageMetricsSendFailure or OperationCanceledException)
        {
            if (!_enabled || generation != _consentGeneration || cancellation.IsCancellationRequested)
            {
                return;
            }
            Status = UsageMetricsText.NotSent;
        }
    }

    /// <summary>The Mac's payload with the Windows platform field (specification 011).</summary>
    internal static string PayloadJson(string installation, string since, UsageMetricsBuild build, IReadOnlyList<DayCounts> days)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("installation", installation);
            writer.WriteString("since", since);
            writer.WriteString("version", build.Version);
            writer.WriteString("build", build.Build);
            writer.WriteNumber("osMajor", build.OsMajor);
            writer.WriteString("architecture", build.Architecture);
            writer.WriteString("platform", UsageMetricsBuild.Platform);
            writer.WriteStartArray("days");
            foreach (DayCounts day in days)
            {
                writer.WriteStartObject();
                writer.WriteString("day", day.Day);
                writer.WriteString("launches", Bucket(day.Launches));
                writer.WriteStartObject("features");
                foreach ((string name, int count) in day.Counts)
                {
                    writer.WriteString(name, Bucket(count));
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The Mac's [UsageMetricDay]: day, launches and counts per category.</summary>
    internal static string DaysJson(IReadOnlyList<DayCounts> days)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (DayCounts day in days)
            {
                writer.WriteStartObject();
                writer.WriteString("day", day.Day);
                writer.WriteNumber("launches", day.Launches);
                writer.WriteStartObject("counts");
                foreach ((string name, int count) in day.Counts)
                {
                    writer.WriteNumber(name, count);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads <see cref="DaysJson"/>'s shape; anything else throws, so a damaged queue is reported, never overwritten.</summary>
    internal static List<DayCounts> ParseDays(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The saved counts are not a list.");
        }
        var days = new List<DayCounts>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            var day = new DayCounts(item.GetProperty("day").GetString() ?? throw new JsonException("A saved day has no date."))
            {
                Launches = item.GetProperty("launches").GetInt32(),
            };
            foreach (JsonProperty count in item.GetProperty("counts").EnumerateObject())
            {
                day.Counts[count.Name] = count.Value.GetInt32();
            }
            days.Add(day);
        }
        return days;
    }

    private void UpdateToday(Action<DayCounts> update)
    {
        string today = DayString(_now());
        if (LoadDays() is not { } days)
        {
            return;
        }
        DayCounts? entry = days.Find(day => day.Day == today);
        if (entry is null)
        {
            entry = new DayCounts(today);
            days.Add(entry);
        }
        update(entry);
        _defaults.Set(DaysKey, DaysJson(RecentDays(days, today)));
        // The app can stay open for days, so the first activity of a new day triggers that day's single send.
        if (_schedule is not null && _sending is null && _endpoint is not null && _defaults.String(LastAttemptKey) != today)
        {
            var sending = new CancellationTokenSource();
            _sending = sending;
            CancellationToken token = sending.Token;
            _schedule(() => AutomaticSend = SendAutomaticallyAsync(sending, token));
        }
    }

    private async Task SendAutomaticallyAsync(CancellationTokenSource sending, CancellationToken token)
    {
        try
        {
            await SendDailySummaryIfNeededAsync(token);
        }
        finally
        {
            if (_sending == sending)
            {
                _sending = null;
            }
        }
    }

    /// <summary>Keeps only the days the service still accepts (today and the previous seven), so an old queue never blocks a send.</summary>
    private List<DayCounts> RecentDays(List<DayCounts> days, string today)
    {
        string earliest = DayString(_now() - TimeSpan.FromDays(7));
        return [.. days.Where(day => string.CompareOrdinal(day.Day, earliest) >= 0 && string.CompareOrdinal(day.Day, today) <= 0)
                       .OrderBy(day => day.Day, StringComparer.Ordinal)];
    }

    /// <summary>The saved days, an empty list when there are none, or null when they cannot be read (the status says so).</summary>
    private List<DayCounts>? LoadDays()
    {
        if (_defaults.String(DaysKey) is not { } text)
        {
            return [];
        }
        try
        {
            return ParseDays(text);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            Status = UsageMetricsText.NotRead;
            return null;
        }
    }

    private static string DayString(DateTimeOffset date) => date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>One UTC day's launches and counts per category (the Mac's UsageMetricDay).</summary>
    internal sealed class DayCounts(string day)
    {
        public string Day { get; } = day;

        public int Launches { get; set; }

        public SortedDictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);
    }
}
