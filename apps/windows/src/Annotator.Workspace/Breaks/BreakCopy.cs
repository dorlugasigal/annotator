namespace Annotator.Workspace.Breaks;

/// <summary>
/// The break timer's words (specification 014): the Mac copy with the Windows terms of specification 013. "Mac" becomes
/// "computer", as elsewhere in the Windows copy, and Control-Command-Q becomes Win+L.
/// </summary>
public static class BreakCopy
{
    public const string TimeUp = "Time's up";

    public const string LocksWhenTouched = "This computer locks if anyone touches it.";

    public const string NowLocks = "This computer now locks if anyone touches it.";

    public static string Arming(int seconds) => $"Locking in {seconds} s. Press Esc to cancel.";

    public const string LockUnavailable =
        "Annotator can't lock this computer, so the break didn't start. To use the timer without locking, turn off Lock when " +
        "someone touches it in Settings > Break timer.";

    public static string LockRefused(int error) =>
        $"Windows refused the lock (error {error}), so nothing is locked. Press Win+L to lock your computer. The lock screen hides the timer.";

    public const string LockNotConfirmed =
        "Windows didn't confirm the lock, so nothing is locked. Press Win+L to lock your computer. The lock screen hides the timer.";

    /// <summary>What region sharing's cover says when a break starts (Mac prepareForBreak).</summary>
    public const string SharingPaused = "Paused for a break. Start sharing again from the Annotator tray menu.";

    // Tray menu

    public const string StartBreak = "Start break timer";

    public static string QuickBreak(int minutes) => $"{minutes} min";

    public static string QuickBreakName(int minutes) => $"Start a {minutes}-minute break";

    public static string MenuCaption(bool locks) =>
        locks ? "Your computer locks if someone touches it." : "Press Esc to end the break.";

    // Settings

    public const string TabTitle = "Break timer";

    public const string Introduction = "Show a big countdown during a break, so everyone knows when to come back.";

    public static string StartButton(long minutes) => $"Start a {minutes}-minute break";

    public const string ShortcutLabel = "Shortcut";

    public const string ShortcutHelp = "Change it in Toolbar & Shortcuts.";

    public const string TimerSection = "Timer";

    public const string Length = "Length";

    public const string LengthName = "Length in minutes";

    public const string Minutes = "minutes";

    public const string Title = "Title";

    public const string NoTitle = "No title";

    public const string ShowEndTime = "Show when the break ends";

    public const string ShowEndTimeHelp = "Shows the end time, such as Back at 12:45.";

    public const string CountOver = "Keep counting after zero";

    public const string CountOverHelp = "After zero, shows how long the break has run over, such as +1:23.";

    public const string Chime = "Play a sound at zero";

    public const string LookSection = "Look";

    public const string Background = "Background";

    public const string BlurNote = "Blurred desktop needs no screen access. Large text can stay readable through the blur.";

    public const string Colors = "Colors";

    public const string AllDisplays = "Show on every display";

    public const string AllDisplaysHelp = "When this is off, the timer shows on the display with the pointer.";

    public const string LockingSection = "Locking";

    public const string LockWhenTouched = "Lock when someone touches it";

    public const string LockingExplanation =
        "Windows doesn't let apps show anything on the lock screen. So the timer stays on screen, and from 3 seconds after it " +
        "starts, your computer locks as soon as someone presses a key, clicks or moves the mouse. Until then, your computer " +
        "stays signed in behind the timer. While your computer is locked, the timer keeps counting and plays its sound at zero. " +
        "It comes back when you sign in. If Annotator quits, the timer goes away.";

    public const string NoLockingExplanation =
        "To close the timer, press Esc or click End break. Anyone at your keyboard can do this.";

    public const string MeetingNote =
        "Sharing your screen in a meeting? Turn this off. Meeting apps can't show the lock screen, so people would see a frozen timer.";

    public const string CannotLock = "Annotator can't lock this computer. Turn this off to use the timer without locking.";

    public const string PreviewName = "Preview of the break screen";

    // Break screen

    public const string CountdownName = "Break timer";

    public const string Paused = "Paused";

    public const string OverTime = "over time";

    public const string RemoveMinute = "Remove a minute";

    public const string AddMinute = "Add a minute";

    public const string Pause = "Pause";

    public const string Resume = "Resume";

    public const string EndBreak = "End break";

    public const string ControlsName = "Break controls";

    public const string OneMinute = "1 min";
}
