using System.Text.Json;
using Annotator.Core;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Metrics;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Metrics;

/// <summary>
/// Usage counts (specification 011, WIN-PRV-01): the Mac's UsageMetricsTests, the Windows payload fields, the switched-off
/// Windows sending and the places the workspace counts.
/// </summary>
public sealed class UsageMetricsTests
{
    private static readonly Uri Endpoint = new("https://metrics.example.test/");
    private static readonly UsageMetricsBuild Build = new("0.1.0-beta.8", "26", 11, "x86_64");

    private readonly AppDefaults _defaults = AppDefaults.InMemory();

    private UsageMetrics Metrics(UsageMetricsSend? send = null, Uri? endpoint = null, Func<DateTimeOffset>? now = null,
                                 Queue<Action>? pending = null, bool available = true) =>
        new(_defaults, available, endpoint, Build, send ?? Accepted, now, pending is null ? null : pending.Enqueue);

    private static Task<int> Accepted(UsageMetricsRequest request, CancellationToken cancellation) => Task.FromResult(202);

    private static DateTimeOffset At(string time) => DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture);

    private static void Drain(Queue<Action> pending)
    {
        while (pending.TryDequeue(out Action? action))
        {
            action();
        }
    }

    [Fact]
    public void MetricsAreOffUntilConsentAndOptOutRemovesLocalState()
    {
        UsageMetrics metrics = Metrics();
        Assert.False(metrics.Enabled);
        Assert.Equal(UsageMetricsText.Off, metrics.Status);
        metrics.Record(UsageMetric.Drawing);
        Assert.Null(_defaults.String(UsageMetrics.DaysKey));

        metrics.SetEnabled(true);
        Assert.True(metrics.Enabled);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", _defaults.String(UsageMetrics.InstallationKey));
        Assert.NotNull(_defaults.String(UsageMetrics.DaysKey));
        Assert.Equal(UsageMetricsText.NotSetUp, metrics.Status);

        metrics.SetEnabled(false);
        Assert.False(metrics.Enabled);
        Assert.Null(_defaults.String(UsageMetrics.InstallationKey));
        Assert.Null(_defaults.String(UsageMetrics.DaysKey));
        Assert.Null(_defaults.String(UsageMetrics.SinceKey));
        Assert.Equal(UsageMetricsText.Deleted, metrics.Status);

        // A new consent is a new installation.
        metrics.SetEnabled(true);
        string first = _defaults.String(UsageMetrics.InstallationKey)!;
        metrics.SetEnabled(false);
        metrics.SetEnabled(true);
        Assert.NotEqual(first, _defaults.String(UsageMetrics.InstallationKey));
    }

    [Fact]
    public void BucketsDoNotExposeExactLargeCounts()
    {
        Assert.Equal("0", UsageMetrics.Bucket(0));
        Assert.Equal("1", UsageMetrics.Bucket(1));
        Assert.Equal("2-5", UsageMetrics.Bucket(5));
        Assert.Equal("6-20", UsageMetrics.Bucket(20));
        Assert.Equal("21+", UsageMetrics.Bucket(500));
    }

    [Fact]
    public async Task OnlyOneRequestIsAttemptedPerDayAndPayloadIsCoarse()
    {
        var requests = new List<UsageMetricsRequest>();
        UsageMetrics metrics = Metrics((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(202);
        }, Endpoint);
        metrics.SetEnabled(true);
        metrics.Record(UsageMetric.Drawing);
        metrics.Record(UsageMetric.Drawing);
        metrics.Record(UsageMetric.ImageInsertion);

        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);

        UsageMetricsRequest sent = Assert.Single(requests);
        Assert.Equal("https://metrics.example.test/api/metrics/app", sent.Url.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(8), sent.Timeout);
        Assert.Equal(UsageMetricsText.Sent, metrics.Status);
        using JsonDocument document = JsonDocument.Parse(sent.Json);
        JsonElement payload = document.RootElement;
        Assert.Equal(["schema", "installation", "since", "version", "build", "osMajor", "architecture", "platform", "days"],
            payload.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, payload.GetProperty("schema").GetInt32());
        Assert.Equal(_defaults.String(UsageMetrics.InstallationKey), payload.GetProperty("installation").GetString());
        Assert.Equal("0.1.0-beta.8", payload.GetProperty("version").GetString());
        Assert.Equal("26", payload.GetProperty("build").GetString());
        // WIN-PRV-01: the Windows fields.
        Assert.Equal("windows", payload.GetProperty("platform").GetString());
        Assert.Equal(11, payload.GetProperty("osMajor").GetInt32());
        Assert.Equal("x86_64", payload.GetProperty("architecture").GetString());
        JsonElement day = Assert.Single(payload.GetProperty("days").EnumerateArray());
        Assert.Equal(payload.GetProperty("since").GetString(), day.GetProperty("day").GetString());
        Assert.Equal("1", day.GetProperty("launches").GetString());
        Assert.Equal("2-5", day.GetProperty("features").GetProperty(UsageMetric.Drawing.RawValue).GetString());
        Assert.Equal("1", day.GetProperty("features").GetProperty(UsageMetric.ImageInsertion.RawValue).GetString());
        foreach (string forbidden in new[] { "screenshot", "filename", "textValue", "geometry", "clipboard" })
        {
            Assert.DoesNotContain(forbidden, sent.Json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FirstActivityOfANewDaySendsOnceWithoutRelaunching()
    {
        DateTimeOffset day = At("2026-09-23T10:00:00Z");
        int requests = 0;
        var pending = new Queue<Action>();
        UsageMetrics metrics = Metrics((_, _) =>
        {
            requests++;
            return Task.FromResult(202);
        }, Endpoint, () => day, pending);
        metrics.SetEnabled(true);
        metrics.Record(UsageMetric.Drawing);
        Drain(pending);
        Assert.Equal(1, requests);
        metrics.Record(UsageMetric.Zoom);
        Drain(pending);
        Assert.Equal(1, requests);
        day = day.AddDays(1);
        metrics.Record(UsageMetric.Drawing);
        Drain(pending);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task ReenablingOnTheSameDayDoesNotSendAgain()
    {
        int requests = 0;
        UsageMetrics metrics = Metrics((_, _) =>
        {
            requests++;
            return Task.FromResult(202);
        }, Endpoint);
        metrics.SetEnabled(true);
        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        metrics.SetEnabled(false);
        metrics.SetEnabled(true);
        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task DaysOutsideTheServiceWindowAreDroppedBeforeSending()
    {
        DateTimeOffset now = At("2026-08-01T10:00:00Z");
        var bodies = new List<string>();
        UsageMetrics metrics = Metrics((request, _) =>
        {
            bodies.Add(request.Json);
            return Task.FromResult(202);
        }, Endpoint, () => now);
        metrics.SetEnabled(true);
        metrics.Record(UsageMetric.Drawing);
        now = At("2026-09-23T10:00:00Z");
        metrics.Record(UsageMetric.Zoom);
        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(bodies));
        Assert.Equal(["2026-09-23"], document.RootElement.GetProperty("days").EnumerateArray().Select(day => day.GetProperty("day").GetString()));
        Assert.Equal("2026-08-01", document.RootElement.GetProperty("since").GetString());
    }

    [Fact]
    public async Task TheQueueKeepsTodayAndThePreviousSevenUtcDays()
    {
        DateTimeOffset now = At("2026-09-14T23:30:00Z");
        var bodies = new List<string>();
        UsageMetrics metrics = Metrics((request, _) =>
        {
            bodies.Add(request.Json);
            return Task.FromResult(202);
        }, Endpoint, () => now);
        metrics.SetEnabled(true);
        for (int day = 0; day < 10; day++)
        {
            metrics.Record(UsageMetric.Drawing);
            now = now.AddDays(1);
        }
        now = now.AddDays(-1);
        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(Assert.Single(bodies));
        string[] days = [.. document.RootElement.GetProperty("days").EnumerateArray().Select(day => day.GetProperty("day").GetString()!)];
        Assert.Equal(8, days.Length);
        Assert.Equal("2026-09-16", days[0]);
        Assert.Equal("2026-09-23", days[^1]);
    }

    [Fact]
    public async Task InvalidOrMissingEndpointDoesNotMarkTheDayAttempted()
    {
        UsageMetrics metrics = Metrics();
        metrics.SetEnabled(true);
        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        Assert.Null(_defaults.String(UsageMetrics.LastAttemptKey));
        Assert.Contains("isn't set up to send counts", metrics.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionEndpointMustBeAnHttpsOrigin()
    {
        Assert.Equal("metrics.example.test", UsageMetrics.ValidatedEndpoint("https://metrics.example.test")?.Host);
        Assert.Equal("metrics.example.test", UsageMetrics.ValidatedEndpoint(" https://metrics.example.test/\n")?.Host);
        foreach (string? value in new[] { null, "", "http://example.test", "https://user:secret@example.test", "https://user@example.test",
                                          "https://example.test:444", "https://example.test:443", "https://example.test/elsewhere",
                                          "https://example.test/?secret=1", "https://example.test?secret=1", "https://example.test/#part",
                                          "https://", "$(ANNOTATOR_METRICS_URL)", "https://$(ANNOTATOR_METRICS_URL)" })
        {
            Assert.Null(UsageMetrics.ValidatedEndpoint(value));
        }
    }

    [Fact]
    public async Task OptOutDoesNotBecomeSentWhenAnOldRequestCompletes()
    {
        var reply = new TaskCompletionSource<int>();
        UsageMetrics metrics = Metrics((_, _) => reply.Task, Endpoint);
        metrics.SetEnabled(true);
        Task send = metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        Assert.False(send.IsCompleted);
        metrics.SetEnabled(false);
        string status = metrics.Status;
        reply.SetResult(202);
        await send.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(status, metrics.Status);
        Assert.Null(_defaults.String(UsageMetrics.DaysKey));
        Assert.Null(_defaults.String(UsageMetrics.InstallationKey));
    }

    [Fact]
    public async Task AFailedOrRefusedSendSaysItWillTryAnotherDay()
    {
        foreach (UsageMetricsSend send in new UsageMetricsSend[]
        {
            static (_, _) => Task.FromResult(400),
            static (_, _) => Task.FromException<int>(new UsageMetricsSendFailure("No connection.")),
            static (_, _) => Task.FromException<int>(new TaskCanceledException("Timed out.")),
        })
        {
            var defaults = AppDefaults.InMemory();
            var metrics = new UsageMetrics(defaults, available: true, Endpoint, Build, send);
            metrics.SetEnabled(true);
            await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
            Assert.Equal(UsageMetricsText.NotSent, metrics.Status);
            Assert.NotNull(defaults.String(UsageMetrics.LastAttemptKey));
            Assert.NotNull(defaults.String(UsageMetrics.DaysKey));
        }
    }

    [Fact]
    public void CorruptQueueIsReportedAndNotOverwritten()
    {
        UsageMetrics metrics = Metrics();
        metrics.SetEnabled(true);
        foreach (string invalid in new[] { "invalid", "{}", "[{\"day\":\"2026-09-23\"}]", "[{\"day\":1,\"launches\":1,\"counts\":{}}]",
                                           "[{\"day\":\"2026-09-23\",\"launches\":1,\"counts\":{\"drawing\":\"2\"}}]" })
        {
            _defaults.Set(UsageMetrics.DaysKey, invalid);
            metrics.Record(UsageMetric.Drawing);
            Assert.Equal(invalid, _defaults.String(UsageMetrics.DaysKey));
            Assert.Contains("couldn't be read", metrics.Status, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OptOutCancelsTheOwnedAutomaticRequestAfterSeveralEvents()
    {
        bool started = false, cancelled = false;
        var pending = new Queue<Action>();
        UsageMetrics metrics = Metrics(async (_, token) =>
        {
            started = true;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (OperationCanceledException)
            {
                cancelled = token.IsCancellationRequested;
                throw;
            }
            return 202;
        }, Endpoint, pending: pending);
        metrics.SetEnabled(true);
        metrics.Record(UsageMetric.Drawing);
        metrics.Record(UsageMetric.Zoom);
        Assert.Single(pending);
        Drain(pending);
        Assert.True(started);
        Task automatic = metrics.AutomaticSend!;
        metrics.SetEnabled(false);
        await automatic.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(cancelled);
        Assert.False(metrics.Enabled);
        Assert.Equal(UsageMetricsText.Deleted, metrics.Status);
    }

    [Fact]
    public void TheWindowsSendingSwitchStaysOffUntilTheServiceIsDeployed()
    {
        // WIN-PRV-01: turn this on only after the owner deploys services/metrics with the platform field, then update this test.
        Assert.False(UsageMetrics.ServiceAcceptsWindows);
    }

    [Fact]
    public async Task UnavailableUsageCountsReadWriteRecordAndSendNothing()
    {
        _defaults.Set(UsageMetrics.EnabledKey, true);
        _defaults.Set(UsageMetrics.InstallationKey, "11111111-2222-4333-8444-555555555555");
        int requests = 0;
        var pending = new Queue<Action>();
        UsageMetrics metrics = Metrics((_, _) =>
        {
            requests++;
            return Task.FromResult(202);
        }, Endpoint, pending: pending, available: false);
        Assert.False(metrics.Available);
        Assert.False(metrics.Enabled);
        Assert.Equal("Usage counts aren't available on Windows yet. Nothing is sent.", metrics.Status);
        int changes = 0;
        metrics.Changed += () => changes++;
        metrics.SetEnabled(true);
        metrics.RecordLaunch();
        metrics.Record(UsageMetric.Drawing);
        await metrics.SendDailySummaryIfNeededAsync(TestContext.Current.CancellationToken);
        Drain(pending);
        Assert.False(metrics.Enabled);
        Assert.Equal(0, requests);
        Assert.Equal(0, changes);
        Assert.Null(_defaults.String(UsageMetrics.DaysKey));
        Assert.Null(_defaults.String(UsageMetrics.LastAttemptKey));
        Assert.Equal("11111111-2222-4333-8444-555555555555", _defaults.String(UsageMetrics.InstallationKey));
        Assert.False(UsageMetrics.Unavailable().Available);
    }

    [Fact]
    public void TheWindowsPayloadNamesWindows11AndTheAppsArchitecture()
    {
        UsageMetricsBuild x64 = UsageMetricsBuild.ForWindows("0.1.0-beta.8", 26, new Version(10, 0, 22621, 4317), System.Runtime.InteropServices.Architecture.X64);
        Assert.Equal(new UsageMetricsBuild("0.1.0-beta.8", "26", 11, "x86_64"), x64);
        Assert.Equal("arm64", UsageMetricsBuild.ForWindows("0.1.0", 1, new Version(10, 0, 26100), System.Runtime.InteropServices.Architecture.Arm64).Architecture);
        Assert.Equal("other", UsageMetricsBuild.ArchitectureName(System.Runtime.InteropServices.Architecture.X86));
        Assert.Equal(10, UsageMetricsBuild.WindowsMajor(new Version(10, 0, 19045)));
        Assert.Equal("unknown", UsageMetricsBuild.ForWindows("0.0.0-dev", null, new Version(10, 0, 22621), System.Runtime.InteropServices.Architecture.X64).Build);
        Assert.Equal("windows", UsageMetricsBuild.Platform);
    }

    [Fact]
    public void ThePayloadHasOnlyTheContractsFields()
    {
        var day = new UsageMetrics.DayCounts("2026-10-20") { Launches = 3 };
        day.Counts["zoom"] = 25;
        day.Counts["drawing"] = 7;
        Assert.Equal(
            "{\"schema\":1,\"installation\":\"11111111-2222-4333-8444-555555555555\",\"since\":\"2026-10-19\",\"version\":\"0.1.0-beta.8\"," +
            "\"build\":\"26\",\"osMajor\":11,\"architecture\":\"x86_64\",\"platform\":\"windows\"," +
            "\"days\":[{\"day\":\"2026-10-20\",\"launches\":\"2-5\",\"features\":{\"drawing\":\"6-20\",\"zoom\":\"21\\u002B\"}}]}",
            UsageMetrics.PayloadJson("11111111-2222-4333-8444-555555555555", "2026-10-19", Build, [day]));
        Assert.Equal("[{\"day\":\"2026-10-20\",\"launches\":3,\"counts\":{\"drawing\":7,\"zoom\":25}}]", UsageMetrics.DaysJson([day]));
        UsageMetrics.DayCounts read = Assert.Single(UsageMetrics.ParseDays(UsageMetrics.DaysJson([day])));
        Assert.Equal(3, read.Launches);
        Assert.Equal(25, read.Counts["zoom"]);
    }

    [Fact]
    public void CountsStopAtTwentyOne()
    {
        UsageMetrics metrics = Metrics();
        metrics.SetEnabled(true);
        for (int index = 0; index < 40; index++)
        {
            metrics.Record(UsageMetric.Zoom);
            metrics.RecordLaunch();
        }
        UsageMetrics.DayCounts day = Assert.Single(UsageMetrics.ParseDays(_defaults.String(UsageMetrics.DaysKey)!));
        Assert.Equal(21, day.Counts["zoom"]);
        Assert.Equal(21, day.Launches);
    }

    [Fact]
    public void ConsentAndCountsSurviveARestartInTheAppSettingsFile()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "scratch-usage-metrics", Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(folder, "app-defaults-v1.json");
            var first = new UsageMetrics(AppDefaults.InFile(path), available: true, Endpoint, Build, Accepted);
            first.SetEnabled(true);
            first.Record(UsageMetric.PngExport);
            AppDefaults reread = AppDefaults.InFile(path);
            Assert.Null(reread.LoadFailure);
            var second = new UsageMetrics(reread, available: true, Endpoint, Build, Accepted);
            Assert.True(second.Enabled);
            Assert.Equal(UsageMetricsText.On, second.Status);
            UsageMetrics.DayCounts day = Assert.Single(UsageMetrics.ParseDays(reread.String(UsageMetrics.DaysKey)!));
            Assert.Equal(1, day.Counts["pngExport"]);
            Assert.Equal(DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), reread.String(UsageMetrics.SinceKey));
            second.SetEnabled(false);
            Assert.Null(AppDefaults.InFile(path).String(UsageMetrics.InstallationKey));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void AppSettingsKeepStringsApartFromOtherValues()
    {
        _defaults.Set("flag", true);
        _defaults.Set("when", new DateTimeOffset(2026, 10, 20, 1, 2, 3, TimeSpan.Zero));
        _defaults.Set("text", "2026-10-20");
        Assert.Null(_defaults.String("flag"));
        Assert.Null(_defaults.String("when"));
        Assert.Null(_defaults.String("missing"));
        Assert.Equal("2026-10-20", _defaults.String("text"));
        Assert.False(_defaults.Bool("text"));
        _defaults.Set("text", "not a date");
        Assert.Null(_defaults.Date("text"));
        Assert.Throws<ArgumentNullException>(() => _defaults.Set("text", (string)null!));
    }

    [Fact]
    public void ToolCategoriesMatchTheMacsChoose()
    {
        Assert.Equal(UsageMetric.Editing, UsageMetricExtensions.ForTool(Tool.Select));
        foreach (Tool tool in new[] { Tool.Cover, Tool.Blur, Tool.Pixelate, Tool.Distort })
        {
            Assert.Equal(UsageMetric.CoverOrEffect, UsageMetricExtensions.ForTool(tool));
        }
        foreach (Tool tool in new[] { Tool.Pen, Tool.Highlighter, Tool.Eraser })
        {
            Assert.Equal(UsageMetric.Drawing, UsageMetricExtensions.ForTool(tool));
        }
        foreach (Tool tool in new[] { Tool.Text, Tool.Rectangle, Tool.Arrow, Tool.Line, Tool.Counter, Tool.Ruler, Tool.DatabaseStack, Tool.Image })
        {
            Assert.Equal(UsageMetric.TextOrShape, UsageMetricExtensions.ForTool(tool));
        }
        Assert.Equal(["drawing", "editing", "textOrShape", "coverOrEffect", "zoom", "imageInsertion", "boardOpen", "boardSave", "pngExport", "reusableShape"],
            Enum.GetValues<UsageMetric>().Select(metric => metric.RawValue));
    }

    [Fact]
    public void TheWorkspaceCountsWhereTheMacCounts()
    {
        var clipboard = new MemoryClipboard();
        WorkspaceModel model = new(new WorkspaceOptions { Displays = () => [PrimaryDisplay, LeftDisplay], Pointer = () => new Point(10, 10), Clipboard = clipboard });
        var recorded = new List<UsageMetric>();
        model.UsageRecorded += recorded.Add;
        List<UsageMetric> Take()
        {
            List<UsageMetric> taken = [.. recorded];
            recorded.Clear();
            return taken;
        }

        // Show / hide from Off opens drawing and counts it (Mac openDrawing).
        model.OpenDrawing();
        Assert.Equal([UsageMetric.Drawing], Take());
        model.UseApps();
        // Choosing a tool from Use apps opens drawing without counting drawing (Mac beginDrawing(recordsUsage: false)).
        model.Choose(Tool.Rectangle);
        Assert.Equal([UsageMetric.TextOrShape], Take());
        model.Choose(Tool.Select);
        model.Choose(Tool.Pen);
        model.Choose(Tool.Blur);
        Assert.Equal([UsageMetric.Editing, UsageMetric.Drawing, UsageMetric.CoverOrEffect], Take());
        model.HideTools();
        model.OpenDrawing();
        // Showing hidden drawings in Draw goes through the Mac's prepareDrawingForReveal, which counts nothing.
        Assert.Equal(WorkspaceMode.Draw, model.Snapshot.Mode);
        Assert.Empty(Take());
        model.UseApps();
        model.BeginDrawing();
        Assert.Equal([UsageMetric.Drawing], Take());

        model.InsertImage(new ClipboardImage(ValueList.Create<byte>([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3]), 24, 16));
        Assert.Equal([UsageMetric.ImageInsertion], Take());
        model.BoardSaved();
        model.PngExported();
        Assert.Equal([UsageMetric.BoardSave, UsageMetric.PngExport], Take());
        model.OpenBoard(new CanvasDocument(ElementList.Create([new Element(Tool.Ellipse, new Rect(0, 0, 10, 10))])));
        Assert.Equal([UsageMetric.BoardOpen], Take());
        CanvasDocument source = CanvasDocument.Empty();
        var box = new Element(Tool.Rectangle, new Rect(0, 0, 40, 30), style: Style.Defaults(Tool.Rectangle));
        source.Add(box);
        source.Select([box.Id]);
        model.InsertSavedShape(new SavedShape("Box", source));
        Assert.Equal([UsageMetric.ReusableShape], Take());

        model.UseApps();
        model.ToggleScreenZoom();
        Assert.Equal([UsageMetric.Zoom], Take());
        model.StopZoom();
        Assert.Empty(Take());

        // Spotlight opens a drawing session on its way, which the Mac does not count as drawing.
        WorkspaceModel spotlight = Create();
        spotlight.UsageRecorded += recorded.Add;
        spotlight.SetSpotlight(true);
        Assert.Empty(Take());
    }
}
