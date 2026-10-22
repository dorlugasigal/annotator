using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Annotator.Platform.Displays;

namespace Annotator.Platform.Accessibility;

/// <summary>A UI Automation control type the overlay's elements use.</summary>
public enum OverlayControlType
{
    Button = 50000,
    Edit = 50004,
    Image = 50006,
    Text = 50020,
    Group = 50026,
}

/// <summary>
/// One element of an overlay's UI Automation tree: its control type, name, identifier, read-only value, help text and
/// frame in pixels from the overlay's top-left corner. <see cref="Invoke"/> makes it invokable; UI Automation may call
/// it on any thread.
/// </summary>
public sealed record OverlayAccessibleNode(OverlayControlType ControlType, string Name, PixelRect Frame)
{
    public string? AutomationId { get; init; }

    public string? Value { get; init; }

    public string? HelpText { get; init; }

    public Action? Invoke { get; init; }

    /// <summary>The element has the keyboard focus (the text being edited).</summary>
    public bool IsFocused { get; init; }

    /// <summary>Makes <see cref="Value"/> editable: UI Automation's SetValue replaces it. UI Automation may call it on any thread.</summary>
    public Action<string>? SetValue { get; init; }
}

/// <summary>An overlay's tree: the root element (the window itself) and its children in reading order.</summary>
public sealed record OverlayAccessibleTree(OverlayAccessibleNode Root, ImmutableArray<OverlayAccessibleNode> Children);

/// <summary>
/// The UI Automation provider of one overlay window (WIN-A11Y-01): the window's element is <see cref="Tree"/>'s root,
/// and its children are fragments inside it. The tree is an immutable value that UI Automation may read on any thread;
/// set a new one whenever what it says changes. The providers are source-generated COM objects, so this works under
/// Native AOT.
/// </summary>
public sealed partial class OverlayAccessibility : IDisposable
{
    private const int UiaRootObjectId = -25;
    private static readonly StrategyBasedComWrappers s_wrappers = new();
    private static readonly Guid s_simple = new("d6dd68d1-86fd-4332-8666-9abedea2d24c");
    private static readonly Guid s_invoke = new("54fcb24b-e18e-47a2-b4d3-eccbe77599a2");
    private static readonly Guid s_value = new("c7935180-6fb3-4201-b174-7df73adbf64a");

    private readonly nint _window;
    private readonly RootProvider _root;
    private OverlayAccessibleTree _tree;
    private PixelRect _bounds;
    private ChildProvider[] _children = [];
    private bool _disposed;

    /// <param name="window">The overlay window.</param>
    /// <param name="bounds">The window's rectangle in screen pixels; element frames are relative to its top-left corner.</param>
    public OverlayAccessibility(nint window, PixelRect bounds, OverlayAccessibleTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _window = window;
        _bounds = bounds;
        _tree = tree;
        _root = new RootProvider(this);
    }

    /// <summary>The tree UI Automation reads; replace it whenever what it says changes.</summary>
    public OverlayAccessibleTree Tree
    {
        get => Volatile.Read(ref _tree);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _tree, value);
        }
    }

    /// <summary>Answers WM_GETOBJECT for the UI Automation root; returns false for other objects.</summary>
    public bool TryAnswer(nuint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (_disposed || (int)lParam != UiaRootObjectId)
        {
            return false;
        }
        nint provider = Pointer(_root, s_simple);
        try
        {
            result = Native.UiaReturnRawElementProvider(_window, wParam, lParam, provider);
        }
        finally
        {
            Marshal.Release(provider);
        }
        return true;
    }

    /// <summary>Tells UI Automation the window's elements are gone; call while the window still exists.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Native.UiaReturnRawElementProvider(_window, 0, 0, 0);
    }

    /// <summary>
    /// Tells UI Automation clients, such as Narrator, that the focused child now has the keyboard focus (the text editor
    /// opened). Nothing happens when no client listens or no child is focused.
    /// </summary>
    public void RaiseFocusChanged()
    {
        if (_disposed || FocusedIndex() is not { } index || !Native.UiaClientsAreListening())
        {
            return;
        }
        nint provider = Pointer(Child(index)!, s_simple);
        try
        {
            Native.UiaRaiseAutomationEvent(provider, FocusChangedEvent);
        }
        finally
        {
            Marshal.Release(provider);
        }
    }

    /// <summary>Tells UI Automation clients that the focused child's value changed from <paramref name="previous"/> (typing).</summary>
    public void RaiseValueChanged(string previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (_disposed || FocusedIndex() is not { } index || Tree.Children[index].Value is not { } current || !Native.UiaClientsAreListening())
        {
            return;
        }
        nint provider = Pointer(Child(index)!, s_simple);
        var before = new StringVariant(previous);
        var after = new StringVariant(current);
        try
        {
            Native.UiaRaiseAutomationPropertyChangedEvent(provider, Ids.ValueValue, before, after);
        }
        finally
        {
            before.Free();
            after.Free();
            Marshal.Release(provider);
        }
    }

    private const int FocusChangedEvent = 20005;

    /// <summary>
    /// A VARIANT that holds a BSTR, laid out like the Windows VARIANT (24 bytes on 64-bit Windows, 16 on 32-bit), so it can
    /// be passed by value without runtime marshalling. <see cref="ComVariant"/> cannot: it overlays a decimal.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StringVariant(string value)
    {
        private const ushort BstrType = 8;
        private readonly ushort _type = BstrType;
        private readonly ushort _reserved1;
        private readonly ushort _reserved2;
        private readonly ushort _reserved3;
        private nint _value = Marshal.StringToBSTR(value);
        private readonly nint _record;

        public void Free()
        {
            Marshal.FreeBSTR(_value);
            _value = 0;
        }
    }

    private int? FocusedIndex()
    {
        ImmutableArray<OverlayAccessibleNode> nodes = Tree.Children;
        for (int index = 0; index < nodes.Length; index++)
        {
            if (nodes[index].IsFocused)
            {
                return index;
            }
        }
        return null;
    }

    /// <summary>The window's rectangle in screen pixels; set it when the window moves or changes size.</summary>
    public PixelRect Bounds
    {
        get => _bounds;
        set => _bounds = value;
    }

    /// <summary>The window's element, for tests that call the provider without UI Automation.</summary>
    internal object RootForTesting => _root;

    private ChildProvider? Child(int index)
    {
        ImmutableArray<OverlayAccessibleNode> nodes = Tree.Children;
        if (index < 0 || index >= nodes.Length)
        {
            return null;
        }
        ChildProvider[] children = Volatile.Read(ref _children);
        if (children.Length < nodes.Length)
        {
            var created = new ChildProvider[nodes.Length];
            for (int item = 0; item < created.Length; item++)
            {
                created[item] = item < children.Length ? children[item] : new ChildProvider(this, item);
            }
            Volatile.Write(ref _children, created);
            children = created;
        }
        return children[index];
    }

    private UiaRect ScreenRect(PixelRect frame) =>
        new(Bounds.Left + frame.Left, Bounds.Top + frame.Top, frame.Width, frame.Height);

    /// <summary>The COM interface <paramref name="iid"/> of a provider, with a reference the caller owns.</summary>
    private static nint Pointer(object provider, Guid iid)
    {
        nint unknown = s_wrappers.GetOrCreateComInterfaceForObject(provider, CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint pointer));
            return pointer;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static ComVariant Property(OverlayAccessibleNode node, int property, bool isRoot) => property switch
    {
        Ids.Name => ComVariant.Create(node.Name),
        Ids.ControlType => ComVariant.Create((int)node.ControlType),
        Ids.AutomationId when node.AutomationId is { } id => ComVariant.Create(id),
        Ids.HelpText when node.HelpText is { } help => ComVariant.Create(help),
        Ids.HasKeyboardFocus when !isRoot => ComVariant.Create(node.IsFocused),
        Ids.IsKeyboardFocusable => ComVariant.Create(isRoot || node.IsFocused || node.SetValue is not null),
        Ids.IsEnabled or Ids.IsControlElement or Ids.IsContentElement => ComVariant.Create(true),
        Ids.IsInvokePatternAvailable => ComVariant.Create(node.Invoke is not null),
        Ids.IsValuePatternAvailable => ComVariant.Create(node.Value is not null),
        Ids.ValueValue when node.Value is { } value => ComVariant.Create(value),
        Ids.ValueIsReadOnly when node.Value is not null => ComVariant.Create(node.SetValue is null),
        Ids.FrameworkId => ComVariant.Create("Annotator"),
        _ => default,
    };

    /// <summary>UI Automation pattern and property identifiers (UIAutomationClient.h).</summary>
    private static class Ids
    {
        public const int InvokePattern = 10000;
        public const int ValuePattern = 10002;
        public const int ControlType = 30003;
        public const int Name = 30005;
        public const int HasKeyboardFocus = 30008;
        public const int IsKeyboardFocusable = 30009;
        public const int IsEnabled = 30010;
        public const int AutomationId = 30011;
        public const int HelpText = 30013;
        public const int IsControlElement = 30016;
        public const int IsContentElement = 30017;
        public const int FrameworkId = 30024;
        public const int IsInvokePatternAvailable = 30031;
        public const int IsValuePatternAvailable = 30043;
        public const int ValueValue = 30045;
        public const int ValueIsReadOnly = 30046;
    }

    [GeneratedComClass]
    private sealed partial class RootProvider(OverlayAccessibility owner)
        : IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot, IValueProvider
    {
        public ProviderOptions get_ProviderOptions() => ProviderOptions.ServerSideProvider;

        public nint GetPatternProvider(int patternId) =>
            patternId == Ids.ValuePattern && owner.Tree.Root.Value is not null ? Pointer(this, s_value) : 0;

        public unsafe void GetPropertyValue(int propertyId, ComVariant* result) => *result = Property(owner.Tree.Root, propertyId, isRoot: true);

        public nint get_HostRawElementProvider()
        {
            Marshal.ThrowExceptionForHR(Native.UiaHostProviderFromHwnd(owner._window, out nint host));
            return host;
        }

        public IRawElementProviderFragment? Navigate(NavigateDirection direction) => direction switch
        {
            NavigateDirection.FirstChild => owner.Child(0),
            NavigateDirection.LastChild => owner.Child(owner.Tree.Children.Length - 1),
            _ => null,
        };

        // A fragment root hosted in a window takes the window's runtime identifier.
        public nint GetRuntimeId() => 0;

        public UiaRect get_BoundingRectangle() => new(owner.Bounds.Left, owner.Bounds.Top, owner.Bounds.Width, owner.Bounds.Height);

        public nint GetEmbeddedFragmentRoots() => 0;

        public void SetFocus()
        {
        }

        public IRawElementProviderFragmentRoot? get_FragmentRoot() => this;

        public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y)
        {
            ImmutableArray<OverlayAccessibleNode> nodes = owner.Tree.Children;
            for (int index = nodes.Length - 1; index >= 0; index--)
            {
                UiaRect rect = owner.ScreenRect(nodes[index].Frame);
                if (x >= rect.Left && y >= rect.Top && x < rect.Left + rect.Width && y < rect.Top + rect.Height)
                {
                    return owner.Child(index);
                }
            }
            return this;
        }

        public IRawElementProviderFragment? GetFocus() => owner.FocusedIndex() is { } index ? owner.Child(index) : null;

        public void SetValue(string value) => throw new InvalidOperationException("The value is read-only.");

        public string get_Value() => owner.Tree.Root.Value ?? string.Empty;

        public int get_IsReadOnly() => 1;
    }

    [GeneratedComClass]
    private sealed partial class ChildProvider(OverlayAccessibility owner, int index)
        : IRawElementProviderSimple, IRawElementProviderFragment, IInvokeProvider, IValueProvider
    {
        private OverlayAccessibleNode? Node =>
            owner.Tree.Children is var nodes && index < nodes.Length ? nodes[index] : null;

        public ProviderOptions get_ProviderOptions() => ProviderOptions.ServerSideProvider;

        public nint GetPatternProvider(int patternId) => patternId switch
        {
            Ids.InvokePattern when Node?.Invoke is not null => Pointer(this, s_invoke),
            Ids.ValuePattern when Node?.Value is not null => Pointer(this, s_value),
            _ => 0,
        };

        public unsafe void GetPropertyValue(int propertyId, ComVariant* result) =>
            *result = Node is { } node ? Property(node, propertyId, isRoot: false) : default;

        public nint get_HostRawElementProvider() => 0;

        public IRawElementProviderFragment? Navigate(NavigateDirection direction) => direction switch
        {
            NavigateDirection.Parent => owner._root,
            NavigateDirection.NextSibling => owner.Child(index + 1),
            NavigateDirection.PreviousSibling => owner.Child(index - 1),
            _ => null,
        };

        public unsafe nint GetRuntimeId()
        {
            // UiaAppendRuntimeId (3), then a number unique among the window's elements.
            nint array = Native.SafeArrayCreateVector(VarTypeInt32, 0, 2);
            if (array == 0)
            {
                throw new OutOfMemoryException("The UI Automation runtime identifier could not be created.");
            }
            int* values;
            Marshal.ThrowExceptionForHR(Native.SafeArrayAccessData(array, (void**)&values));
            values[0] = 3;
            values[1] = index + 1;
            Marshal.ThrowExceptionForHR(Native.SafeArrayUnaccessData(array));
            return array;
        }

        public UiaRect get_BoundingRectangle() => Node is { } node ? owner.ScreenRect(node.Frame) : default;

        public nint GetEmbeddedFragmentRoots() => 0;

        public void SetFocus()
        {
        }

        public IRawElementProviderFragmentRoot? get_FragmentRoot() => owner._root;

        public void Invoke()
        {
            if (Node?.Invoke is not { } action)
            {
                throw new InvalidOperationException("This element cannot be invoked now.");
            }
            action();
        }

        public void SetValue(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Node?.SetValue is not { } set)
            {
                throw new InvalidOperationException("The value is read-only.");
            }
            set(value);
        }

        public string get_Value() => Node?.Value ?? string.Empty;

        public int get_IsReadOnly() => Node?.SetValue is null ? 1 : 0;
    }

    private const ushort VarTypeInt32 = 3;

    private static partial class Native
    {
        [LibraryImport("UIAutomationCore.dll")]
        public static partial nint UiaReturnRawElementProvider(nint hwnd, nuint wParam, nint lParam, nint provider);

        [LibraryImport("UIAutomationCore.dll")]
        public static partial int UiaHostProviderFromHwnd(nint hwnd, out nint provider);

        [LibraryImport("UIAutomationCore.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UiaClientsAreListening();

        [LibraryImport("UIAutomationCore.dll")]
        public static partial int UiaRaiseAutomationEvent(nint provider, int eventId);

        [LibraryImport("UIAutomationCore.dll")]
        public static partial int UiaRaiseAutomationPropertyChangedEvent(nint provider, int propertyId, StringVariant oldValue, StringVariant newValue);

        [LibraryImport("OleAut32.dll")]
        public static partial nint SafeArrayCreateVector(ushort type, int lowerBound, uint count);

        [LibraryImport("OleAut32.dll")]
        public static unsafe partial int SafeArrayAccessData(nint array, void** data);

        [LibraryImport("OleAut32.dll")]
        public static partial int SafeArrayUnaccessData(nint array);
    }
}

[Flags]
internal enum ProviderOptions
{
    ServerSideProvider = 0x1,
}

internal enum NavigateDirection
{
    Parent = 0,
    NextSibling = 1,
    PreviousSibling = 2,
    FirstChild = 3,
    LastChild = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct UiaRect(double Left, double Top, double Width, double Height);

[GeneratedComInterface]
[Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal partial interface IRawElementProviderSimple
{
    ProviderOptions get_ProviderOptions();

    nint GetPatternProvider(int patternId);

    /// <summary>Writes the VARIANT UI Automation passes; an unsupported property stays VT_EMPTY.</summary>
    unsafe void GetPropertyValue(int propertyId, ComVariant* result);

    nint get_HostRawElementProvider();
}

[GeneratedComInterface]
[Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    IRawElementProviderFragment? Navigate(NavigateDirection direction);

    nint GetRuntimeId();

    UiaRect get_BoundingRectangle();

    nint GetEmbeddedFragmentRoots();

    void SetFocus();

    IRawElementProviderFragmentRoot? get_FragmentRoot();
}

[GeneratedComInterface]
[Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    IRawElementProviderFragment? ElementProviderFromPoint(double x, double y);

    IRawElementProviderFragment? GetFocus();
}

[GeneratedComInterface]
[Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface IInvokeProvider
{
    void Invoke();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("c7935180-6fb3-4201-b174-7df73adbf64a")]
internal partial interface IValueProvider
{
    void SetValue(string value);

    [return: MarshalAs(UnmanagedType.BStr)]
    string get_Value();

    int get_IsReadOnly();
}
