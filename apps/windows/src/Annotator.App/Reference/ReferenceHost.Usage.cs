using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Metrics;

namespace Annotator.App.Reference;

/// <summary>Usage counts for the Settings and tour states (WIN-PRV-01).</summary>
internal sealed partial class ReferenceHost
{
    /// <summary>
    /// Unavailable, as this build has them, or available and off with <c>--usage-counts available</c>: the Mac's states,
    /// as Windows will show them once the owner turns sending on. In memory, without an endpoint; nothing is ever sent.
    /// </summary>
    private UsageMetrics ReferenceUsage() => _request.UsageCountsAvailable
        ? new UsageMetrics(AppDefaults.InMemory(), available: true, endpoint: null, new UsageMetricsBuild("0.0.0", "0", 11, "x86_64"),
            static (_, _) => Task.FromException<int>(new UsageMetricsSendFailure("Reference states never send usage counts.")))
        : UsageMetrics.Unavailable();
}
