using System.Runtime.InteropServices;
using Annotator.Platform.Network;
using Annotator.Workspace.Metrics;

namespace Annotator.App;

/// <summary>
/// Usage counts (specification 011, WIN-PRV-01 to WIN-PRV-03): the Mac's UsageMetrics in the app's settings file, with
/// this build's version, Windows 11 and the app's architecture. They stay unavailable while
/// <see cref="UsageMetrics.ServiceAcceptsWindows"/> is off. Otherwise a day's single send runs on this thread after the
/// first activity, through Windows' HTTP stack, and only when the build names the service. The live check keeps them in
/// memory and never sends.
/// </summary>
internal sealed partial class AppHost
{
    private readonly UsageMetrics _usage;

    private UsageMetrics CreateUsageMetrics(bool live) => new(
        _defaults,
        UsageMetrics.ServiceAcceptsWindows,
        live ? UsageMetrics.ValidatedEndpoint(BuildInfo.MetricsUrl) : null,
        UsageMetricsBuild.ForWindows(BuildInfo.NotesVersion, BuildInfo.BuildNumber, Environment.OSVersion.Version, RuntimeInformation.ProcessArchitecture),
        SendUsageCountsAsync,
        schedule: live ? action => _queue.TryEnqueue(() => action()) : null);

    private static async Task<int> SendUsageCountsAsync(UsageMetricsRequest request, CancellationToken cancellation)
    {
        try
        {
            return await JsonPost.SendAsync(request.Url, request.Json, request.Timeout, cancellation);
        }
        catch (JsonPostFailure failure)
        {
            throw new UsageMetricsSendFailure(failure.Message, failure);
        }
    }
}
