using Windows.Win32;
using Windows.Win32.System.Recovery;

namespace Annotator.Platform.Updates;

/// <summary>
/// Asks Windows to start Annotator again after it closes it to install an update (WIN-SYS-03), like Sparkle's relaunch on
/// the Mac. A packaged desktop app has to register before the update starts (RegisterApplicationRestart): the Microsoft
/// Store and App Installer close the app through the Restart Manager, which then starts it with the registered arguments.
/// Only update restarts count: a crash, a hang or a restart of Windows never starts Annotator through this.
/// </summary>
public static class UpdateRestart
{
    /// <summary>
    /// Registers the next start, replacing an earlier registration. <paramref name="arguments"/> follow Annotator.exe
    /// (LaunchArguments), or are empty for an ordinary launch. Throws <see cref="NativeCallException"/> when Windows refuses.
    /// </summary>
    public static void Register(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        const REGISTER_APPLICATION_RESTART_FLAGS updatesOnly =
            REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_CRASH |
            REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_HANG |
            REGISTER_APPLICATION_RESTART_FLAGS.RESTART_NO_REBOOT;
        NativeCallException.ThrowIfFailed(PInvoke.RegisterApplicationRestart(arguments, updatesOnly), "RegisterApplicationRestart");
    }
}
