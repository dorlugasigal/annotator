using System.Runtime.InteropServices;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference states for accessibility checks): WinUI's own test hooks, the private
/// <c>Microsoft.UI.Xaml.DxamlCoreTestHooks</c> class that the Windows App SDK registers with Microsoft.UI.Xaml.dll. They
/// make WinUI draw every window as in a Windows contrast theme (<c>OverrideHighContrast</c>, which WinUI's own tests use)
/// and scale text as Windows' Text size setting does (<c>UpdateFontScale</c>), without changing any Windows setting.
/// The interface is private and its slots belong to one build of Microsoft.UI.Xaml.dll: the slots below were read from
/// Microsoft's public symbols of Microsoft.UI.Xaml.dll 3.2.3.2609 (Microsoft.WindowsAppSDK.WinUI 2.3.9). Another build of
/// the DLL is refused before any call, and the reference host checks what each call changed, so the simulation is either
/// WinUI's own or reported as unavailable, never a made-up state. The released app never calls this class.
/// </summary>
internal static unsafe partial class XamlTestHooks
{
    /// <summary>IXamlTestHooks (dxaml/xcp/inc/IXamlTestHooks-win.h in microsoft-ui-xaml).</summary>
    private static readonly Guid s_xamlTestHooks = new("43d4bcbd-4f02-4651-9ecc-dcfec9f786a7");

    /// <summary>The build of Microsoft.UI.Xaml.dll whose symbols gave the slots below.</summary>
    private const string KnownBuild = "3.2.3.2609";

    // Slots of IXamlTestHooks after IUnknown (3) and IXamlLoggerTestHooks (2).
    private const int OverrideHighContrastSlot = 34;
    private const int UpdateFontScaleSlot = 74;

    private static nint s_hooks;

    /// <summary>Scales text as Windows' Text size setting does (1 to 2.25), through WinUI's own text scaling.</summary>
    public static void SetTextScale(double scale)
    {
        nint hooks = Hooks();
        int result = ((delegate* unmanaged[Stdcall]<nint, float, int>)Slot(hooks, UpdateFontScaleSlot))(hooks, (float)scale);
        Marshal.ThrowExceptionForHR(result);
    }

    /// <summary>
    /// Draws WinUI as in a Windows contrast theme with <paramref name="palette"/>'s system colors, as WinUI's ThemingHelper
    /// does in its own tests. The palette lives for the rest of the process.
    /// </summary>
    public static void SetContrast(ContrastPalette palette)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new NotSupportedException("The contrast simulation passes a C++ shared_ptr and is written for x64 only.");
        }
        nint hooks = Hooks();
        nint pointer = SharedPalette(palette);
        int result = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(hooks, OverrideHighContrastSlot))(hooks, pointer);
        Marshal.ThrowExceptionForHR(result);
    }

    private static nint Hooks()
    {
        if (s_hooks != 0)
        {
            return s_hooks;
        }
        string library = Path.Combine(AppContext.BaseDirectory, "Microsoft.ui.xaml.dll");
        string? build = File.Exists(library) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(library).FileVersion : null;
        if (build is null || !build.StartsWith(KnownBuild + " ", StringComparison.Ordinal) && build != KnownBuild)
        {
            throw new NotSupportedException($"WinUI's test hooks are known for Microsoft.UI.Xaml.dll {KnownBuild}; this app has {build ?? "none"}.");
        }
        const string ClassName = "Microsoft.UI.Xaml.DxamlCoreTestHooks";
        nint name = 0;
        nint factory = 0;
        nint instance = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(ClassName, ClassName.Length, out name));
            Guid activationFactory = new("00000035-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, in activationFactory, out factory));
            // IActivationFactory::ActivateInstance; the hooks act on the calling thread's WinUI core.
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, 6))(factory, &instance));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(instance, in s_xamlTestHooks, out nint hooks));
            s_hooks = hooks;
            return hooks;
        }
        finally
        {
            if (instance != 0)
            {
                Marshal.Release(instance);
            }
            if (factory != 0)
            {
                Marshal.Release(factory);
            }
            if (name != 0)
            {
                _ = WindowsDeleteString(name);
            }
        }
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    /// <summary>
    /// A <c>std::shared_ptr&lt;std::list&lt;std::pair&lt;int, unsigned int&gt;&gt;&gt;</c> as the MSVC x64 library lays it out:
    /// the pointer pair, a control block whose counts never reach zero (so WinUI never frees memory it did not allocate)
    /// and a circular list with its sentinel node. Passed by value, it travels as a pointer to this caller-owned copy.
    /// </summary>
    private static nint SharedPalette(ContrastPalette palette)
    {
        IReadOnlyList<(int Index, uint Color)> colors = palette.SystemColors;
        var vtable = (nint*)NativeMemory.AllocZeroed(3, (nuint)sizeof(nint));
        for (int index = 0; index < 3; index++)
        {
            vtable[index] = (nint)(delegate* unmanaged<nint, void>)&Ignore;
        }
        // _Ref_count_base: vtable, _Uses, _Weaks.
        var control = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        control[0] = (nint)vtable;
        ((int*)(control + 1))[0] = 1 << 20;
        ((int*)(control + 1))[1] = 1 << 20;
        // _List_node: _Next, _Prev, then the pair (int, unsigned int); the head is the sentinel.
        const int NodeSize = 24;
        byte* head = (byte*)NativeMemory.AllocZeroed(NodeSize);
        byte* previous = head;
        foreach ((int index, uint color) in colors)
        {
            byte* node = (byte*)NativeMemory.AllocZeroed(NodeSize);
            *(byte**)(previous) = node;
            *(byte**)(node + 8) = previous;
            *(int*)(node + 16) = index;
            *(uint*)(node + 20) = color;
            previous = node;
        }
        *(byte**)previous = head;
        *(byte**)(head + 8) = previous;
        // std::list: _Myhead, _Mysize.
        var list = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        list[0] = (nint)head;
        list[1] = colors.Count;
        var shared = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        shared[0] = (nint)list;
        shared[1] = (nint)control;
        return (nint)shared;
    }

    [UnmanagedCallersOnly]
    private static void Ignore(nint self)
    {
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string source, int length, out nint text);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint text);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
}

/// <summary>
/// A Windows 11 contrast theme's system colors (Settings > Accessibility > Contrast themes), as ARGB values for the
/// GetSysColor indices WinUI's contrast resources read.
/// </summary>
internal sealed record ContrastPalette(string Name, uint Window, uint WindowText, uint Hotlight, uint GrayText, uint HighlightText,
                                       uint Highlight, uint ButtonText, uint ButtonFace)
{
    private const int ColorWindow = 5, ColorWindowText = 8, ColorHighlight = 13, ColorHighlightText = 14, ColorButtonFace = 15,
        ColorGrayText = 17, ColorButtonText = 18, ColorHotlight = 26;

    public static ContrastPalette Aquatic { get; } = new("aquatic", 0xFF202020, 0xFFFFFFFF, 0xFF75E9FC, 0xFFA6A6A6, 0xFF263B50, 0xFF8EE3F0, 0xFFFFFFFF, 0xFF202020);

    public static ContrastPalette Desert { get; } = new("desert", 0xFFFFFAEF, 0xFF3D3D3D, 0xFF1C5E75, 0xFF676767, 0xFFFFF5E3, 0xFF903909, 0xFF202020, 0xFFFFFAEF);

    public static ContrastPalette Dusk { get; } = new("dusk", 0xFF2D3236, 0xFFFFFFFF, 0xFF70EBDE, 0xFFA6A6A6, 0xFF212D3B, 0xFFA1BFDE, 0xFFB6F6F0, 0xFF2D3236);

    public static ContrastPalette NightSky { get; } = new("nightsky", 0xFF000000, 0xFFFFFFFF, 0xFF8080FF, 0xFFA6A6A6, 0xFF2B2B2B, 0xFFD6B4FD, 0xFFFFEE32, 0xFF000000);

    public static IReadOnlyList<ContrastPalette> All { get; } = [Aquatic, Desert, Dusk, NightSky];

    public static ContrastPalette? Named(string name) => All.FirstOrDefault(palette => palette.Name == name);

    /// <summary>The palette for the GetSysColor indices, in WinUI ThemingHelper's order.</summary>
    public IReadOnlyList<(int Index, uint Color)> SystemColors =>
    [
        (ColorWindowText, WindowText), (ColorHotlight, Hotlight), (ColorGrayText, GrayText), (ColorHighlightText, HighlightText),
        (ColorHighlight, Highlight), (ColorButtonText, ButtonText), (ColorButtonFace, ButtonFace), (ColorWindow, Window),
    ];

    /// <summary>Every color of the palette, for checks that a window draws only contrast colors.</summary>
    public IReadOnlyList<uint> Colors => [Window, WindowText, Hotlight, GrayText, HighlightText, Highlight, ButtonText, ButtonFace];
}
