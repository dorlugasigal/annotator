using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Annotator.Platform.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.TextServices;

namespace Annotator.Platform.Text;

/// <summary>
/// One text editor's connection to the Text Services Framework (spec 013 S-7, WIN-INP-09): a document manager and a
/// context whose text store is the editor's <see cref="ITextStoreDocument"/>, focused whenever <c>window</c> has the
/// keyboard focus. Input methods (Japanese, Chinese and Korean IMEs), the emoji panel (Win+.), voice typing (Win+H) and
/// the touch keyboard edit the text through it, and draw their candidate windows next to the text. Create it on the
/// editor's thread, a COM single-threaded apartment with a message loop (the overlay thread), and use it only there.
/// </summary>
public sealed unsafe class TextServicesSession : IDisposable
{
    private static readonly StrategyBasedComWrappers s_wrappers = new();

    private readonly nint _window;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly TextStore _store;
    private readonly List<TextStoreAttributeRun> _runs = [];
    private ITfThreadMgrEx* _manager;
    private ITfDocumentMgr* _documents;
    private ITfContext* _context;
    private ITfProperty* _attributes;
    private ITfCategoryMgr* _categories;
    private ITfDisplayAttributeMgr* _displayAttributes;
    private uint _client;
    private uint _editCookie = Tsf.TF_INVALID_COOKIE;
    private bool _activated;
    private bool _pushed;
    private bool _associated;
    private bool _disposed;

    private TextServicesSession(nint window, ITextStoreDocument document)
    {
        _window = window;
        _store = new TextStore(this, window, document);
    }

    /// <summary>
    /// A problem inside a TSF callback, where it cannot be thrown: show it to the person. Without a handler it goes to
    /// <see cref="NativeCallbackErrors"/>.
    /// </summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// Connects <paramref name="document"/> to TSF on the calling thread and focuses it while <paramref name="window"/> has
    /// the keyboard focus (0: no window, for tests; the context is focused at once).
    /// </summary>
    /// <exception cref="NativeCallException">TSF could not be started; the editor still takes typed characters.</exception>
    public static TextServicesSession Start(nint window, ITextStoreDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var session = new TextServicesSession(window, document);
        try
        {
            session.Connect();
        }
        catch
        {
            session.Dispose();
            throw;
        }
        return session;
    }

    /// <summary>An input method is composing text in the document.</summary>
    public bool IsComposing => _store.IsComposing;

    /// <summary>TSF holds the store's lock now (only true while TSF calls into the document).</summary>
    public bool IsLocked => _store.IsLocked;

    /// <summary>The client identifier TSF gave this thread; tests act as a text service with it.</summary>
    internal uint ClientId => _client;

    internal ITfContext* Context => _context;

    internal ITfThreadMgrEx* ThreadManager => _manager;

    internal ITfDocumentMgr* DocumentManager => _documents;

    internal TextStore Store => _store;

    /// <summary>The app changed the document's text (typing, deleting, pasting, Undo): TSF reads it again.</summary>
    public void TextChanged(int start, int oldEnd, int newEnd)
    {
        CheckThread();
        _store.NotifyTextChange(new TextStoreChange(start, oldEnd, newEnd));
        _store.NotifySelectionChange();
    }

    /// <summary>The app moved the selection.</summary>
    public void SelectionChanged()
    {
        CheckThread();
        _store.NotifySelectionChange();
    }

    /// <summary>The text moved, grew or turned on the screen.</summary>
    public void LayoutChanged()
    {
        CheckThread();
        _store.NotifyLayoutChange();
    }

    /// <summary>
    /// Ends the input method's composition and keeps its text, before the app edits, commits or cancels it (TSF's
    /// TerminateComposition). Nothing happens without a composition, or while TSF holds the lock.
    /// </summary>
    public void FinishComposition()
    {
        CheckThread();
        if (_disposed || !_store.IsComposing || _store.IsLocked || _context == null)
        {
            return;
        }
        ITfContextOwnerCompositionServices* services = ComPtr.QueryInterface<ITfContextOwnerCompositionServices>(
            _context, Tsf.IID_ITfContextOwnerCompositionServices, "QueryInterface(ITfContextOwnerCompositionServices)");
        try
        {
            NativeCallException.ThrowIfFailed(services->TerminateComposition(null), "ITfContextOwnerCompositionServices::TerminateComposition");
        }
        finally
        {
            ComPtr.Release(ref services);
        }
    }

    /// <summary>Disconnects the document; TSF lets go of the store and the thread's activation.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        CheckThread();
        _disposed = true;
        try
        {
            if (_store.IsComposing && !_store.IsLocked && _context != null)
            {
                ITfContextOwnerCompositionServices* services;
                Guid iid = Tsf.IID_ITfContextOwnerCompositionServices;
                if (_context->QueryInterface(&iid, (void**)&services).Succeeded)
                {
                    services->TerminateComposition(null);
                    ComPtr.Release(ref services);
                }
            }
            if (_editCookie != Tsf.TF_INVALID_COOKIE && _context != null)
            {
                ITfSource* source;
                Guid iid = ITfSource.IID_Guid;
                if (_context->QueryInterface(&iid, (void**)&source).Succeeded)
                {
                    source->UnadviseSink(_editCookie);
                    ComPtr.Release(ref source);
                }
                _editCookie = Tsf.TF_INVALID_COOKIE;
            }
            if (_associated && _manager != null)
            {
                ITfDocumentMgr* previous = null;
                _manager->AssociateFocus(new HWND(_window), null, &previous);
                ComPtr.Release(ref previous);
            }
            if (_pushed && _documents != null)
            {
                _documents->Pop(Tsf.TF_POPF_ALL);
            }
        }
        finally
        {
            _store.Detach();
            ComPtr.Release(ref _attributes);
            ComPtr.Release(ref _displayAttributes);
            ComPtr.Release(ref _categories);
            ComPtr.Release(ref _context);
            ComPtr.Release(ref _documents);
            if (_activated && _manager != null)
            {
                _manager->Deactivate();
            }
            ComPtr.Release(ref _manager);
        }
    }

    internal void Report(Exception error)
    {
        if (Failed is { } handler)
        {
            handler(error);
        }
        else
        {
            NativeCallbackErrors.Report(error);
        }
    }

    /// <summary>The ACP extent of a range: [start, end).</summary>
    internal static (int Start, int End) Extent(ITfRange* range)
    {
        ITfRangeACP* acp = ComPtr.QueryInterface<ITfRangeACP>(range, ITfRangeACP.IID_Guid, "QueryInterface(ITfRangeACP)");
        try
        {
            int start, count;
            NativeCallException.ThrowIfFailed(acp->GetExtent(&start, &count), "ITfRangeACP::GetExtent");
            return (start, start + count);
        }
        finally
        {
            ComPtr.Release(ref acp);
        }
    }

    /// <summary>
    /// After an input method's edit, reads how it wants its composition drawn: the GUID_PROP_ATTRIBUTE ranges inside the
    /// composition and their display attributes.
    /// </summary>
    internal void ReadCompositionStyle(uint readCookie, ITfCompositionView* view, ITextStoreDocument document)
    {
        _runs.Clear();
        ITfRange* composition = null;
        IEnumTfRanges* ranges = null;
        try
        {
            NativeCallException.ThrowIfFailed(view->GetRange(&composition), "ITfCompositionView::GetRange");
            if (_attributes == null)
            {
                Guid property = PInvoke.GUID_PROP_ATTRIBUTE;
                ITfProperty* attributes;
                NativeCallException.ThrowIfFailed(_context->GetProperty(&property, &attributes), "ITfContext::GetProperty(GUID_PROP_ATTRIBUTE)");
                _attributes = attributes;
            }
            NativeCallException.ThrowIfFailed(_attributes->EnumRanges(readCookie, &ranges, composition), "ITfProperty::EnumRanges");
            while (true)
            {
                ITfRange* range = null;
                uint fetched;
                if (ranges->Next(1, &range, &fetched).Value != Tsf.S_OK || fetched != 1)
                {
                    ComPtr.Release(ref range);
                    break;
                }
                try
                {
                    VARIANT value = default;
                    NativeCallException.ThrowIfFailed(_attributes->GetValue(readCookie, range, &value), "ITfProperty::GetValue");
                    try
                    {
                        if (value.Anonymous.Anonymous.vt == VARENUM.VT_I4 && Attribute(value.Anonymous.Anonymous.Anonymous.lVal) is { } attribute)
                        {
                            (int start, int end) = Extent(range);
                            _runs.Add(Run(start, end, attribute));
                        }
                    }
                    finally
                    {
                        PInvoke.VariantClear(&value);
                    }
                }
                finally
                {
                    ComPtr.Release(ref range);
                }
            }
        }
        finally
        {
            ComPtr.Release(ref ranges);
            ComPtr.Release(ref composition);
        }
        document.CompositionStyled(CollectionsMarshal.AsSpan(_runs));
    }

    private void Connect()
    {
        Guid clsid = PInvoke.CLSID_TF_ThreadMgr;
        Guid iid = ITfThreadMgrEx.IID_Guid;
        ITfThreadMgrEx* manager;
        NativeCallException.ThrowIfFailed(PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&manager),
                                          "CoCreateInstance(CLSID_TF_ThreadMgr)");
        _manager = manager;
        uint client;
        NativeCallException.ThrowIfFailed(manager->ActivateEx(&client, 0), "ITfThreadMgrEx::ActivateEx");
        _activated = true;
        _client = client;
        ITfDocumentMgr* documents;
        NativeCallException.ThrowIfFailed(manager->CreateDocumentMgr(&documents), "ITfThreadMgr::CreateDocumentMgr");
        _documents = documents;

        nint store = s_wrappers.GetOrCreateComInterfaceForObject(_store, CreateComInterfaceFlags.None);
        try
        {
            ITfContext* context;
            uint storeCookie;
            NativeCallException.ThrowIfFailed(documents->CreateContext(client, 0, (IUnknown*)store, &context, &storeCookie),
                                              "ITfDocumentMgr::CreateContext");
            _context = context;
        }
        finally
        {
            Marshal.Release(store);
        }
        NativeCallException.ThrowIfFailed(documents->Push(_context), "ITfDocumentMgr::Push");
        _pushed = true;

        ITfSource* source = ComPtr.QueryInterface<ITfSource>(_context, ITfSource.IID_Guid, "QueryInterface(ITfSource)");
        nint unknown = s_wrappers.GetOrCreateComInterfaceForObject(_store, CreateComInterfaceFlags.None);
        nint sink = 0;
        try
        {
            sink = ComPtr.QueryInterface(unknown, Tsf.IID_ITfTextEditSink, "QueryInterface(ITfTextEditSink)");
            Guid sinkId = Tsf.IID_ITfTextEditSink;
            uint cookie;
            NativeCallException.ThrowIfFailed(source->AdviseSink(&sinkId, (IUnknown*)sink, &cookie), "ITfSource::AdviseSink(ITfTextEditSink)");
            _editCookie = cookie;
        }
        finally
        {
            ComPtr.Release(ref sink);
            ComPtr.Release(ref unknown);
            ComPtr.Release(ref source);
        }

        if (_window != 0)
        {
            ITfDocumentMgr* previous = null;
            NativeCallException.ThrowIfFailed(manager->AssociateFocus(new HWND(_window), documents, &previous), "ITfThreadMgr::AssociateFocus");
            ComPtr.Release(ref previous);
            _associated = true;
        }
        if (_window == 0 || PInvoke.GetFocus() == new HWND(_window))
        {
            NativeCallException.ThrowIfFailed(manager->SetFocus(documents), "ITfThreadMgr::SetFocus");
        }
    }

    private TF_DISPLAYATTRIBUTE? Attribute(int atom)
    {
        if (_categories == null)
        {
            Guid clsid = PInvoke.CLSID_TF_CategoryMgr, iid = ITfCategoryMgr.IID_Guid;
            ITfCategoryMgr* categories;
            NativeCallException.ThrowIfFailed(PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&categories),
                                              "CoCreateInstance(CLSID_TF_CategoryMgr)");
            _categories = categories;
        }
        if (_displayAttributes == null)
        {
            Guid clsid = PInvoke.CLSID_TF_DisplayAttributeMgr, iid = ITfDisplayAttributeMgr.IID_Guid;
            ITfDisplayAttributeMgr* attributes;
            NativeCallException.ThrowIfFailed(PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&attributes),
                                              "CoCreateInstance(CLSID_TF_DisplayAttributeMgr)");
            _displayAttributes = attributes;
        }
        Guid guid;
        if (_categories->GetGUID((uint)atom, &guid).Failed)
        {
            return null;
        }
        ITfDisplayAttributeInfo* info = null;
        try
        {
            // An attribute GUID no input method registered draws as plain composition text.
            if (_displayAttributes->GetDisplayAttributeInfo(&guid, &info, null).Failed || info == null)
            {
                return null;
            }
            TF_DISPLAYATTRIBUTE attribute;
            return info->GetAttributeInfo(&attribute).Succeeded ? attribute : null;
        }
        finally
        {
            ComPtr.Release(ref info);
        }
    }

    private static TextStoreAttributeRun Run(int start, int end, in TF_DISPLAYATTRIBUTE attribute) => new(
        start,
        end,
        attribute.lsStyle switch
        {
            TF_DA_LINESTYLE.TF_LS_SOLID => TextStoreLine.Solid,
            TF_DA_LINESTYLE.TF_LS_DOT => TextStoreLine.Dot,
            TF_DA_LINESTYLE.TF_LS_DASH => TextStoreLine.Dash,
            TF_DA_LINESTYLE.TF_LS_SQUIGGLE => TextStoreLine.Squiggle,
            _ => TextStoreLine.None,
        },
        attribute.fBoldLine,
        Color(attribute.crLine),
        Color(attribute.crText),
        Color(attribute.crBk));

    private static uint? Color(TF_DA_COLOR color) => color.type switch
    {
        TF_DA_COLORTYPE.TF_CT_SYSCOLOR => PInvoke.GetSysColor((SYS_COLOR_INDEX)color.nIndex),
        TF_DA_COLORTYPE.TF_CT_COLORREF => color.cr.Value,
        _ => null,
    };

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
        {
            throw new InvalidOperationException("Use the text services session on the thread that started it.");
        }
    }
}
