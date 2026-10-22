using Annotator.Workspace.Metrics;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    /// <summary>
    /// Raised on the model's thread wherever the Mac's model calls usageMetrics.record (specification 011): opening drawing,
    /// choosing a tool, inserting an image, zooming, saving or opening a board, exporting a PNG and inserting a saved shape.
    /// The app's <see cref="UsageMetrics"/> counts it only while sharing is on.
    /// </summary>
    public event Action<UsageMetric>? UsageRecorded;

    private void RecordUsage(UsageMetric metric) => UsageRecorded?.Invoke(metric);
}
