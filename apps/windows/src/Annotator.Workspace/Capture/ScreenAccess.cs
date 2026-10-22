namespace Annotator.Workspace.Capture;

/// <summary>
/// Whether Annotator may read screen pixels for zoom, the pointer lens, live effects, region sharing and Pick from screen
/// (the Mac's Screen Recording permission, WIN-CAP-04). A packaged Windows app needs the person's answer to two Windows
/// questions (research R-5, R-6): one for screenshots and screen recording, one for turning off the yellow screenshot border.
/// A development build needs none (Gate 0 S-1). Drawing never needs it.
/// </summary>
public enum ScreenAccess
{
    /// <summary>Windows asks nothing of this copy: an unpackaged development build.</summary>
    NotNeeded,

    /// <summary>The person allowed both.</summary>
    Allowed,

    /// <summary>Windows has not asked yet (one or both questions).</summary>
    NotAsked,

    /// <summary>Screenshots and screen recording is off for Annotator: the person said No, or turned it off in Windows Settings.</summary>
    ScreenOff,

    /// <summary>Screenshot borders is off for Annotator, so Windows would draw the yellow border.</summary>
    BorderOff,

    /// <summary>Windows or the organization does not allow it, or Windows could not tell.</summary>
    Blocked,
}

/// <summary>How a request for screen access ended.</summary>
public enum ScreenAccessOutcome
{
    /// <summary>Windows answered, or had nothing left to ask; the answer is in <see cref="ScreenAccessAnswer.Access"/>.</summary>
    Answered,

    /// <summary>The person closed Annotator's explanation without asking Windows.</summary>
    NotNow,

    /// <summary>Windows gave no answer in time; its question may still be open.</summary>
    NoAnswer,
}

/// <summary>What a request for screen access ended with: the access Windows reports now, and how the request ended.</summary>
public readonly record struct ScreenAccessAnswer(ScreenAccess Access, ScreenAccessOutcome Outcome);

/// <summary>The pixel features that need screen access, for their words (<see cref="ScreenAccessText"/>).</summary>
public enum ScreenAccessFeature
{
    Effects,
    Lens,
    ScreenZoom,
    RegionSharing,
    PickColor,
}

/// <summary>
/// The host's side of screen access (the Mac's DisplayCapture.hasPermission and requestPermission). Windows asks its
/// questions only while one of Annotator's windows is in front, so the host shows Annotator's own explanation, which may
/// come to the front, and asks Windows from there when the person agrees.
/// </summary>
public interface IScreenAccess
{
    /// <summary>Windows' answers now, without asking (cheap enough before each pixel feature starts).</summary>
    ScreenAccess Check();

    /// <summary>
    /// Asks Windows the questions it has not asked yet. With <paramref name="explain"/>, the host first shows its explanation
    /// and asks when the person agrees; without, it asks at once, as Settings &gt; Permissions does from its own window in
    /// front. <paramref name="answered"/> runs once, on the workspace's thread. A newer request replaces an open one, whose
    /// callback then never runs.
    /// </summary>
    void Ask(bool explain, Action<ScreenAccessAnswer> answered);
}

public static class ScreenAccessRules
{
    extension(ScreenAccess access)
    {
        /// <summary>Pixels may be read now.</summary>
        public bool IsGranted => access is ScreenAccess.NotNeeded or ScreenAccess.Allowed;

        /// <summary>Windows will not ask again: only Windows Settings or an administrator can change it.</summary>
        public bool IsRefused => access is ScreenAccess.ScreenOff or ScreenAccess.BorderOff or ScreenAccess.Blocked;
    }
}
