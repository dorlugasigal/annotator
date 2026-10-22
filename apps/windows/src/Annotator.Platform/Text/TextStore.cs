using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Windows.Win32.Foundation;
using Windows.Win32.UI.TextServices;

namespace Annotator.Platform.Text;

/// <summary>
/// One editor's Text Services Framework text store (ITextStoreACP) and context owner sinks (spec 013 S-7). It answers
/// TSF from an <see cref="ITextStoreDocument"/> and keeps TSF's lock rules: input methods read only under a read lock and
/// edit only under a read/write lock, a lock asked for during another is queued, and the app's own changes are reported
/// to the sink outside locks. Every call comes on the editor's thread; an exception never leaves a COM method.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class TextStore : ITextStoreACP, ITfContextOwnerCompositionSink, ITfTextEditSink
{
    private static readonly Guid IUnknownId = new("00000000-0000-0000-C000-000000000046");

    private readonly TextServicesSession _session;
    private readonly nint _window;
    private readonly Queue<uint> _pending = new();
    private readonly List<TextStoreChange> _deferredChanges = [];
    private ITextStoreDocument? _document;
    private ITextStoreACPSink* _sink;
    private nint _sinkIdentity;
    private uint _sinkMask;
    private uint _lock;
    private bool _deferredSelection;
    private bool _deferredLayout;
    private ITfCompositionView* _composition;

    public TextStore(TextServicesSession session, nint window, ITextStoreDocument document)
    {
        _session = session;
        _window = window;
        _document = document;
    }

    /// <summary>TSF holds a lock now: the document must not change outside it, and notifications wait for its end.</summary>
    public bool IsLocked => _lock != 0;

    public bool IsComposing => _composition != null;

    public bool HasSink => _sink != null;

    internal ITfCompositionView* Composition => _composition;

    /// <summary>The editor ended. TSF may still call while it lets go; nothing reaches the document any more.</summary>
    public void Detach()
    {
        _document = null;
        ComPtr.Release(ref _composition);
        ReleaseSink();
        _pending.Clear();
    }

    /// <summary>The app changed the text: TSF must know before it reads again (OnTextChange, then OnSelectionChange).</summary>
    public void NotifyTextChange(TextStoreChange change)
    {
        if (_lock != 0)
        {
            _deferredChanges.Add(change);
            return;
        }
        if (_sink != null && (_sinkMask & Tsf.TS_AS_TEXT_CHANGE) != 0)
        {
            var value = new TS_TEXTCHANGE { acpStart = change.Start, acpOldEnd = change.OldEnd, acpNewEnd = change.NewEnd };
            _sink->OnTextChange(0, &value);
        }
    }

    public void NotifySelectionChange()
    {
        if (_lock != 0)
        {
            _deferredSelection = true;
            return;
        }
        if (_sink != null && (_sinkMask & Tsf.TS_AS_SEL_CHANGE) != 0)
        {
            _sink->OnSelectionChange();
        }
    }

    /// <summary>The text moved or changed size on the screen: input methods move their candidate windows.</summary>
    public void NotifyLayoutChange()
    {
        if (_lock != 0)
        {
            _deferredLayout = true;
            return;
        }
        if (_sink != null && (_sinkMask & Tsf.TS_AS_LAYOUT_CHANGE) != 0)
        {
            _sink->OnLayoutChange(TsLayoutCode.TS_LC_CHANGE, Tsf.ViewCookie);
        }
    }

    public int AdviseSink(Guid* riid, nint punk, uint dwMask)
    {
        try
        {
            if (riid == null || punk == 0)
            {
                return Tsf.E_INVALIDARG;
            }
            if (*riid != Tsf.IID_ITextStoreACPSink)
            {
                return Tsf.E_INVALIDARG;
            }
            nint identity = Identity(punk);
            if (_sink != null)
            {
                if (identity == _sinkIdentity)
                {
                    _sinkMask = dwMask;
                    return Tsf.S_OK;
                }
                return Tsf.CONNECT_E_ADVISELIMIT;
            }
            int result = Marshal.QueryInterface(punk, in Tsf.IID_ITextStoreACPSink, out nint sink);
            if (result < 0)
            {
                return result;
            }
            _sink = (ITextStoreACPSink*)sink;
            _sinkIdentity = identity;
            _sinkMask = dwMask;
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int UnadviseSink(nint punk)
    {
        try
        {
            if (punk == 0)
            {
                return Tsf.E_INVALIDARG;
            }
            if (_sink == null || Identity(punk) != _sinkIdentity)
            {
                return Tsf.CONNECT_E_NOCONNECTION;
            }
            ReleaseSink();
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int RequestLock(uint dwLockFlags, int* phrSession)
    {
        try
        {
            if (phrSession == null)
            {
                return Tsf.E_INVALIDARG;
            }
            if (_sink == null || _document == null)
            {
                *phrSession = Tsf.E_FAIL;
                return Tsf.E_FAIL;
            }
            uint kind = dwLockFlags & Tsf.TS_LF_READWRITE;
            if (kind is not (Tsf.TS_LF_READ or Tsf.TS_LF_READWRITE))
            {
                return Tsf.E_INVALIDARG;
            }
            if (_lock != 0)
            {
                if ((dwLockFlags & Tsf.TS_LF_SYNC) != 0)
                {
                    *phrSession = Tsf.TS_E_SYNCHRONOUS;
                    return Tsf.S_OK;
                }
                _pending.Enqueue(kind);
                *phrSession = Tsf.TS_S_ASYNC;
                return Tsf.S_OK;
            }
            *phrSession = Grant(kind);
            while (_pending.Count > 0 && _sink != null)
            {
                Grant(_pending.Dequeue());
            }
            _pending.Clear();
            SendDeferred();
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int GetStatus(TS_STATUS* pdcs)
    {
        if (pdcs == null)
        {
            return Tsf.E_INVALIDARG;
        }
        pdcs->dwDynamicFlags = 0;
        pdcs->dwStaticFlags = Tsf.TS_SS_NOHIDDENTEXT;
        return Tsf.S_OK;
    }

    public int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, int* pacpResultStart, int* pacpResultEnd)
    {
        if (_document is not { } document)
        {
            return Tsf.E_FAIL;
        }
        if (pacpResultStart == null || pacpResultEnd == null || acpTestStart < 0 || acpTestStart > acpTestEnd || acpTestEnd > document.Length)
        {
            return Tsf.E_INVALIDARG;
        }
        *pacpResultStart = acpTestStart;
        *pacpResultEnd = acpTestEnd;
        return Tsf.S_OK;
    }

    public int GetSelection(uint ulIndex, uint ulCount, TS_SELECTION_ACP* pSelection, uint* pcFetched)
    {
        try
        {
            if (!CanRead(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (pcFetched == null)
            {
                return Tsf.E_INVALIDARG;
            }
            *pcFetched = 0;
            if (ulIndex != 0 && ulIndex != Tsf.TS_DEFAULT_SELECTION)
            {
                return Tsf.TS_E_NOSELECTION;
            }
            if (ulCount == 0)
            {
                return Tsf.S_OK;
            }
            if (pSelection == null)
            {
                return Tsf.E_INVALIDARG;
            }
            TextStoreSelection selection = document.Selection;
            pSelection[0].acpStart = selection.Start;
            pSelection[0].acpEnd = selection.End;
            pSelection[0].style.ase = selection.ActiveAtStart ? TsActiveSelEnd.TS_AE_START : TsActiveSelEnd.TS_AE_END;
            pSelection[0].style.fInterimChar = selection.InterimCharacter;
            *pcFetched = 1;
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int SetSelection(uint ulCount, TS_SELECTION_ACP* pSelection)
    {
        try
        {
            if (!CanWrite(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (ulCount == 0 || pSelection == null)
            {
                return Tsf.E_INVALIDARG;
            }
            TS_SELECTION_ACP selection = pSelection[0];
            if (selection.acpStart < 0 || selection.acpStart > selection.acpEnd || selection.acpEnd > document.Length)
            {
                return Tsf.TS_E_INVALIDPOS;
            }
            document.Select(new TextStoreSelection(selection.acpStart, selection.acpEnd, selection.style.ase == TsActiveSelEnd.TS_AE_START,
                                                   selection.style.fInterimChar));
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int GetText(int acpStart, int acpEnd, char* pchPlain, uint cchPlainReq, uint* pcchPlainRet, TS_RUNINFO* prgRunInfo, uint cRunInfoReq,
                       uint* pcRunInfoRet, int* pacpNext)
    {
        try
        {
            if (!CanRead(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (pcchPlainRet == null || pcRunInfoRet == null || pacpNext == null)
            {
                return Tsf.E_INVALIDARG;
            }
            int length = document.Length;
            int end = acpEnd == -1 ? length : acpEnd;
            if (acpStart < 0 || acpStart > end || end > length)
            {
                return Tsf.TS_E_INVALIDPOS;
            }
            bool wantsText = pchPlain != null && cchPlainReq > 0;
            int count = wantsText ? (int)Math.Min((uint)(end - acpStart), cchPlainReq) : end - acpStart;
            if (wantsText)
            {
                document.CopyText(acpStart, new Span<char>(pchPlain, count));
            }
            *pcchPlainRet = wantsText ? (uint)count : 0;
            if (prgRunInfo != null && cRunInfoReq > 0 && count > 0)
            {
                prgRunInfo[0].uCount = (uint)count;
                prgRunInfo[0].type = TsRunType.TS_RT_PLAIN;
                *pcRunInfoRet = 1;
            }
            else
            {
                *pcRunInfoRet = 0;
            }
            *pacpNext = acpStart + count;
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int SetText(uint dwFlags, int acpStart, int acpEnd, char* pchText, uint cch, TS_TEXTCHANGE* pChange)
    {
        try
        {
            if (!CanWrite(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (pchText == null && cch > 0)
            {
                return Tsf.E_INVALIDARG;
            }
            if (acpStart < 0 || acpStart > acpEnd || acpEnd > document.Length)
            {
                return Tsf.TS_E_INVALIDPOS;
            }
            TextStoreChange change = document.Replace(acpStart, acpEnd, new ReadOnlySpan<char>(pchText, (int)cch));
            if (pChange != null)
            {
                *pChange = new TS_TEXTCHANGE { acpStart = change.Start, acpOldEnd = change.OldEnd, acpNewEnd = change.NewEnd };
            }
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int GetFormattedText(int acpStart, int acpEnd, nint* ppDataObject) => Tsf.E_NOTIMPL;

    public int GetEmbedded(int acpPos, Guid* rguidService, Guid* riid, nint* ppunk) => Tsf.E_NOTIMPL;

    public int QueryInsertEmbedded(Guid* pguidService, void* pFormatEtc, int* pfInsertable)
    {
        if (pfInsertable == null)
        {
            return Tsf.E_INVALIDARG;
        }
        // Plain text only: input methods may not insert objects.
        *pfInsertable = 0;
        return Tsf.S_OK;
    }

    public int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, nint pDataObject, TS_TEXTCHANGE* pChange) => Tsf.E_NOTIMPL;

    public int InsertTextAtSelection(uint dwFlags, char* pchText, uint cch, int* pacpStart, int* pacpEnd, TS_TEXTCHANGE* pChange)
    {
        try
        {
            if ((dwFlags & Tsf.TS_IAS_QUERYONLY) != 0)
            {
                if (!CanRead(out ITextStoreDocument? reader))
                {
                    return Tsf.TS_E_NOLOCK;
                }
                TextStoreSelection current = reader.Selection;
                if (pacpStart != null)
                {
                    *pacpStart = current.Start;
                }
                if (pacpEnd != null)
                {
                    *pacpEnd = current.End;
                }
                return Tsf.S_OK;
            }
            if (!CanWrite(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (pchText == null && cch > 0)
            {
                return Tsf.E_INVALIDARG;
            }
            TextStoreSelection selection = document.Selection;
            TextStoreChange change = document.Replace(selection.Start, selection.End, new ReadOnlySpan<char>(pchText, (int)cch));
            if ((dwFlags & Tsf.TS_IAS_NOQUERY) == 0)
            {
                if (pacpStart != null)
                {
                    *pacpStart = change.Start;
                }
                if (pacpEnd != null)
                {
                    *pacpEnd = change.NewEnd;
                }
            }
            if (pChange != null)
            {
                *pChange = new TS_TEXTCHANGE { acpStart = change.Start, acpOldEnd = change.OldEnd, acpNewEnd = change.NewEnd };
            }
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int InsertEmbeddedAtSelection(uint dwFlags, nint pDataObject, int* pacpStart, int* pacpEnd, TS_TEXTCHANGE* pChange) => Tsf.E_NOTIMPL;

    // The store has no attributes of its own (fonts and colors belong to the whole text), so every query finds none.
    public int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, Guid* paFilterAttrs) => Tsf.S_OK;

    public int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags) => Tsf.S_OK;

    public int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags) => Tsf.S_OK;

    public int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags, int* pacpNext, int* pfFound,
                                      int* plFoundOffset)
    {
        if (pacpNext == null || pfFound == null || plFoundOffset == null)
        {
            return Tsf.E_INVALIDARG;
        }
        *pacpNext = acpHalt;
        *pfFound = 0;
        *plFoundOffset = 0;
        return Tsf.S_OK;
    }

    public int RetrieveRequestedAttrs(uint ulCount, TS_ATTRVAL* paAttrVals, uint* pcFetched)
    {
        if (pcFetched == null)
        {
            return Tsf.E_INVALIDARG;
        }
        *pcFetched = 0;
        return Tsf.S_OK;
    }

    public int GetEndACP(int* pacp)
    {
        try
        {
            if (!CanRead(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (pacp == null)
            {
                return Tsf.E_INVALIDARG;
            }
            *pacp = document.Length;
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int GetActiveView(uint* pvcView)
    {
        if (pvcView == null)
        {
            return Tsf.E_INVALIDARG;
        }
        *pvcView = Tsf.ViewCookie;
        return Tsf.S_OK;
    }

    public int GetACPFromPoint(uint vcView, System.Drawing.Point* ptScreen, uint dwFlags, int* pacp)
    {
        try
        {
            if (!CanRead(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (ptScreen == null || pacp == null || vcView != Tsf.ViewCookie)
            {
                return Tsf.E_INVALIDARG;
            }
            if (document.OffsetAt(ptScreen->X, ptScreen->Y, (dwFlags & Tsf.GXFPF_NEAREST) != 0) is not { } offset)
            {
                return Tsf.TS_E_INVALIDPOINT;
            }
            *pacp = offset;
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int GetTextExt(uint vcView, int acpStart, int acpEnd, RECT* prc, int* pfClipped)
    {
        try
        {
            if (!CanRead(out ITextStoreDocument? document))
            {
                return Tsf.TS_E_NOLOCK;
            }
            if (prc == null || pfClipped == null || vcView != Tsf.ViewCookie)
            {
                return Tsf.E_INVALIDARG;
            }
            int end = acpEnd == -1 ? document.Length : acpEnd;
            if (acpStart < 0 || acpStart > end || end > document.Length)
            {
                return Tsf.TS_E_INVALIDPOS;
            }
            PixelRect bounds = document.TextBounds(acpStart, end);
            *prc = new RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            *pfClipped = 0;
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int GetScreenExt(uint vcView, RECT* prc)
    {
        try
        {
            if (_document is not { } document)
            {
                return Tsf.E_FAIL;
            }
            if (prc == null || vcView != Tsf.ViewCookie)
            {
                return Tsf.E_INVALIDARG;
            }
            PixelRect bounds = document.ScreenBounds;
            *prc = new RECT(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int GetWnd(uint vcView, nint* phwnd)
    {
        if (phwnd == null)
        {
            return Tsf.E_INVALIDARG;
        }
        *phwnd = _window;
        return Tsf.S_OK;
    }

    public int OnStartComposition(nint pComposition, int* pfOk)
    {
        try
        {
            if (pfOk != null)
            {
                *pfOk = 1;
            }
            if (_document is not { } document || pComposition == 0)
            {
                return Tsf.S_OK;
            }
            ComPtr.Release(ref _composition);
            Marshal.AddRef(pComposition);
            _composition = (ITfCompositionView*)pComposition;
            document.CompositionStarted();
            UpdateComposition(document, _composition, null);
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int OnUpdateComposition(nint pComposition, nint pRangeNew)
    {
        try
        {
            if (_document is { } document && pComposition != 0)
            {
                UpdateComposition(document, (ITfCompositionView*)pComposition, (ITfRange*)pRangeNew);
            }
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int OnEndComposition(nint pComposition)
    {
        try
        {
            ComPtr.Release(ref _composition);
            _document?.CompositionEnded();
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    public int OnEndEdit(nint pic, uint ecReadOnly, nint pEditRecord)
    {
        try
        {
            if (_document is { } document && _composition != null)
            {
                _session.ReadCompositionStyle(ecReadOnly, _composition, document);
            }
            return Tsf.S_OK;
        }
        catch (Exception error)
        {
            return Failed(error);
        }
    }

    private int Grant(uint kind)
    {
        _lock = kind;
        try
        {
            return _sink->OnLockGranted((TEXT_STORE_LOCK_FLAGS)kind).Value;
        }
        finally
        {
            _lock = 0;
        }
    }

    /// <summary>Tells TSF what the app changed while a lock was held, now that it has ended.</summary>
    private void SendDeferred()
    {
        if (_deferredChanges.Count > 0)
        {
            TextStoreChange[] changes = [.. _deferredChanges];
            _deferredChanges.Clear();
            foreach (TextStoreChange change in changes)
            {
                NotifyTextChange(change);
            }
        }
        if (_deferredSelection)
        {
            _deferredSelection = false;
            NotifySelectionChange();
        }
        if (_deferredLayout)
        {
            _deferredLayout = false;
            NotifyLayoutChange();
        }
    }

    private static void UpdateComposition(ITextStoreDocument document, ITfCompositionView* view, ITfRange* range)
    {
        ITfRange* owned = null;
        try
        {
            if (range == null)
            {
                NativeCallException.ThrowIfFailed(view->GetRange(&owned), "ITfCompositionView::GetRange");
                range = owned;
            }
            (int start, int end) = TextServicesSession.Extent(range);
            document.CompositionChanged(start, end);
        }
        finally
        {
            ComPtr.Release(ref owned);
        }
    }

    private bool CanRead([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITextStoreDocument? document)
    {
        document = _document;
        return document is not null && (_lock & Tsf.TS_LF_READ) != 0;
    }

    private bool CanWrite([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ITextStoreDocument? document)
    {
        document = _document;
        return document is not null && (_lock & Tsf.TS_LF_READWRITE) == Tsf.TS_LF_READWRITE;
    }

    private void ReleaseSink()
    {
        ComPtr.Release(ref _sink);
        _sinkIdentity = 0;
        _sinkMask = 0;
    }

    private static nint Identity(nint unknown)
    {
        NativeCallException.ThrowIfFailed(Marshal.QueryInterface(unknown, in IUnknownId, out nint identity), "QueryInterface(IUnknown)");
        Marshal.Release(identity);
        return identity;
    }

    private int Failed(Exception error)
    {
        _session.Report(error);
        return error is NativeCallException native ? native.HResult : Tsf.E_UNEXPECTED;
    }
}
