using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Motion;

/// <summary>Windows settings that motion follows.</summary>
internal static class SystemMotion
{
    /// <summary>The Mac's Reduce Motion is Windows' Animation effects setting turned off (WIN-MOT-03); read it when motion starts.</summary>
    public static unsafe bool ReducesMotion()
    {
        BOOL animations = true;
        return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) && !animations;
    }
}
