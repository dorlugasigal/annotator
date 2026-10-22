using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace Annotator.Platform.Displays;

/// <summary>The names people see for their monitors, as Windows Settings shows them (the Mac's NSScreen.localizedName).</summary>
public static unsafe class DisplayNames
{
    /// <summary>
    /// Each active display's monitor name by its GDI device name, for example <c>\\.\DISPLAY1</c> to "Odyssey G95SD".
    /// Displays whose name Windows does not know are left out; an empty map when the display configuration cannot be read.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != WIN32_ERROR.ERROR_SUCCESS)
            {
                return names;
            }
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            WIN32_ERROR result;
            fixed (DISPLAYCONFIG_PATH_INFO* pathArray = paths)
            fixed (DISPLAYCONFIG_MODE_INFO* modeArray = modes)
            {
                result = PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS, &pathCount, pathArray, &modeCount, modeArray, null);
            }
            if (result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
            {
                // The configuration changed between the two calls.
                continue;
            }
            if (result != WIN32_ERROR.ERROR_SUCCESS)
            {
                return names;
            }
            for (int index = 0; index < pathCount; index++)
            {
                DISPLAYCONFIG_PATH_INFO path = paths[index];
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
                source.header.adapterId = path.sourceInfo.adapterId;
                source.header.id = path.sourceInfo.id;
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
                target.header.adapterId = path.targetInfo.adapterId;
                target.header.id = path.targetInfo.id;
                if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0 || PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
                {
                    continue;
                }
                string device = Text(source.viewGdiDeviceName.AsReadOnlySpan());
                string friendly = Text(target.monitorFriendlyDeviceName.AsReadOnlySpan());
                if (device.Length > 0 && friendly.Length > 0)
                {
                    names.TryAdd(device, friendly);
                }
            }
            return names;
        }
        return names;
    }

    private static string Text(ReadOnlySpan<char> value)
    {
        int end = value.IndexOf('\0');
        return (end < 0 ? value : value[..end]).Trim().ToString();
    }
}
