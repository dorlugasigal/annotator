using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Tutorial;

namespace Annotator.Workspace.FirstUse;

/// <summary>
/// Port of the Mac's TutorialPreferences: whether the welcome card shows on launch (the "Show on launch" switch, the old tour's
/// key) and whether the person reached the end of the tutorial.
/// </summary>
public sealed class TutorialPreferences
{
    /// <summary>The Mac's UserDefaults key for Show on launch turned off, kept from the old tour.</summary>
    public const string SuppressedKey = "introduction-suppressed";

    /// <summary>The Mac's UserDefaults key for a tutorial that reached its finish card.</summary>
    public const string CompletedKey = "tutorial-completed";

    private readonly AppDefaults _defaults;

    public TutorialPreferences(AppDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults;
    }

    public event Action? Changed;

    /// <summary>The person turned off Show on launch. Ending the tutorial never changes it.</summary>
    public bool Suppressed => _defaults.Bool(SuppressedKey);

    /// <summary>The person reached the finish card once; the welcome card stops showing on launch.</summary>
    public bool Completed => _defaults.Bool(CompletedKey);

    public void SetSuppressed(bool value)
    {
        _defaults.Set(SuppressedKey, value);
        Changed?.Invoke();
    }

    public void SetCompleted(bool value)
    {
        _defaults.Set(CompletedKey, value);
        Changed?.Invoke();
    }
}

/// <summary>When the tutorial may start (Mac TutorialController.isEligible and showAutomatically).</summary>
public static class TutorialRules
{
    /// <summary>Mac showManually's status when the tutorial cannot open now.</summary>
    public const string NotNowMessage = "Finish drawing and stop zoom, then open the tutorial from Settings > Help.";

    /// <summary>
    /// Mac isEligible: the welcome card opens only when nothing is being drawn, zoomed, animated or reported, and no display has
    /// an edit in progress. Once the person starts, the coach stays through drawing.
    /// </summary>
    public static bool IsEligible(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Mode != WorkspaceMode.Draw && !snapshot.ZoomActive && snapshot.Visibility is null && snapshot.ErrorMessage is null
            && snapshot.CaptureFailureMessage is null && !SupportReminderRules.HasActiveEdit(snapshot);
    }

    /// <summary>
    /// Mac showAutomatically: once per launch, never for a launch at sign-in (WIN-SYS-01), never after the person turned off
    /// Show on launch or finished the tutorial, and only at a safe moment.
    /// </summary>
    public static bool ShowsAutomatically(bool startupLaunch, bool suppressed, bool completed, bool eligible) =>
        !startupLaunch && !suppressed && !completed && eligible;
}

/// <summary>Port of the Mac's TutorialController.observation: what the tutorial reads from a workspace snapshot.</summary>
public static class TutorialObserver
{
    public static TutorialObservation Observe(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<TutorialElement>.Builder elements = ImmutableArray.CreateBuilder<TutorialElement>();
        foreach (DisplaySnapshot display in snapshot.Displays)
        {
            foreach (Element element in display.Document.Elements)
            {
                elements.Add(TutorialElement.From(element));
            }
        }
        TutorialMode mode = snapshot.Mode switch
        {
            WorkspaceMode.Draw => TutorialMode.Draw,
            WorkspaceMode.UseApps => TutorialMode.UseApps,
            _ => TutorialMode.Off,
        };
        return new TutorialObservation(mode, snapshot.ToolsPresented, snapshot.Tool, snapshot.Style.Color, snapshot.Style.Curved,
            elements.ToImmutable(), snapshot.UndoSerial, SupportReminderRules.HasActiveEdit(snapshot));
    }

    /// <summary>
    /// Where the ghost's move demo presses (Mac TutorialController.grabPoint): a point on the line of the newest unlocked drawing
    /// on <paramref name="display"/>, in points from the display's top-left corner: the middle of its path, or the middle of its
    /// top edge, turned with the drawing. Null when that display has no unlocked drawing.
    /// </summary>
    public static Point? NewestGrip(WorkspaceSnapshot snapshot, DisplayId display)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (DisplaySnapshot candidate in snapshot.Displays)
        {
            if (candidate.Id != display)
            {
                continue;
            }
            for (int index = candidate.Document.Elements.Count - 1; index >= 0; index--)
            {
                Element element = candidate.Document.Elements[index];
                if (element.Locked)
                {
                    continue;
                }
                Point local = element.Points.Count >= 2 ? element.Points[element.Points.Count / 2]
                    : new Point(element.Frame.X + element.Frame.Width / 2, element.Frame.Y);
                return local.Rotated(element.Frame.Center, element.Rotation);
            }
        }
        return null;
    }
}
