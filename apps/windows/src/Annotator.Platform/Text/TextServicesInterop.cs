using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Annotator.Platform.Text;

/// <summary>Text Services Framework values from the Windows SDK's TextStor.h and msctf.h (10.0.26100).</summary>
internal static class Tsf
{
    public const int S_OK = 0;
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
    public const int CONNECT_E_ADVISELIMIT = unchecked((int)0x80040201);
    public const int CONNECT_E_NOCONNECTION = unchecked((int)0x80040200);

    public const int TS_E_INVALIDPOS = unchecked((int)0x80040200);
    public const int TS_E_NOLOCK = unchecked((int)0x80040201);
    public const int TS_E_NOLAYOUT = unchecked((int)0x80040206);
    public const int TS_E_INVALIDPOINT = unchecked((int)0x80040207);
    public const int TS_E_SYNCHRONOUS = unchecked((int)0x80040208);
    public const int TS_E_NOSELECTION = unchecked((int)0x80040205);
    public const int TS_S_ASYNC = 0x00040300;

    public const uint TS_LF_SYNC = 0x1;
    public const uint TS_LF_READ = 0x2;
    public const uint TS_LF_READWRITE = 0x6;
    public const uint TS_SS_NOHIDDENTEXT = 0x8;
    public const uint TS_AS_TEXT_CHANGE = 0x1;
    public const uint TS_AS_SEL_CHANGE = 0x2;
    public const uint TS_AS_LAYOUT_CHANGE = 0x4;
    public const uint TS_IAS_NOQUERY = 0x1;
    public const uint TS_IAS_QUERYONLY = 0x2;
    public const uint TS_DEFAULT_SELECTION = uint.MaxValue;
    public const uint GXFPF_ROUND_NEAREST = 0x1;
    public const uint GXFPF_NEAREST = 0x2;
    public const uint TF_POPF_ALL = 0x1;
    public const uint TF_ES_SYNC = 0x1;
    public const uint TF_ES_READ = 0x2;
    public const uint TF_ES_READWRITE = 0x6;
    public const uint TF_INVALID_COOKIE = uint.MaxValue;

    /// <summary>The one view the store has.</summary>
    public const uint ViewCookie = 1;

    public static readonly Guid IID_ITextStoreACPSink = new("22d44c94-a419-4542-a272-ae26093ececf");
    public static readonly Guid IID_ITfTextEditSink = new("8127d409-ccd3-4683-967a-b43d5b482bf7");
    public static readonly Guid IID_ITfContextOwnerCompositionServices = new("86462810-593B-4916-9764-19C08E9CE110");
    public static readonly Guid IID_ITfContextComposition = new("D40C8AAE-AC92-4FC7-9A11-0EE0E23AA39B");
    public static readonly Guid IID_ITfEditSession = new("aa80e803-2021-11d2-93e0-0060b067b86e");
}

/// <summary>ITextStoreACP (TextStor.h), the text store a TSF context reads and edits; implemented by <see cref="TextStore"/>.</summary>
[GeneratedComInterface]
[Guid("28888fe3-c2a0-483a-a3ea-8cb1ce51ff3d")]
internal unsafe partial interface ITextStoreACP
{
    [PreserveSig]
    int AdviseSink(Guid* riid, nint punk, uint dwMask);

    [PreserveSig]
    int UnadviseSink(nint punk);

    [PreserveSig]
    int RequestLock(uint dwLockFlags, int* phrSession);

    [PreserveSig]
    int GetStatus(global::Windows.Win32.UI.TextServices.TS_STATUS* pdcs);

    [PreserveSig]
    int QueryInsert(int acpTestStart, int acpTestEnd, uint cch, int* pacpResultStart, int* pacpResultEnd);

    [PreserveSig]
    int GetSelection(uint ulIndex, uint ulCount, global::Windows.Win32.UI.TextServices.TS_SELECTION_ACP* pSelection, uint* pcFetched);

    [PreserveSig]
    int SetSelection(uint ulCount, global::Windows.Win32.UI.TextServices.TS_SELECTION_ACP* pSelection);

    [PreserveSig]
    int GetText(int acpStart, int acpEnd, char* pchPlain, uint cchPlainReq, uint* pcchPlainRet, global::Windows.Win32.UI.TextServices.TS_RUNINFO* prgRunInfo, uint cRunInfoReq,
                uint* pcRunInfoRet, int* pacpNext);

    [PreserveSig]
    int SetText(uint dwFlags, int acpStart, int acpEnd, char* pchText, uint cch, global::Windows.Win32.UI.TextServices.TS_TEXTCHANGE* pChange);

    [PreserveSig]
    int GetFormattedText(int acpStart, int acpEnd, nint* ppDataObject);

    [PreserveSig]
    int GetEmbedded(int acpPos, Guid* rguidService, Guid* riid, nint* ppunk);

    [PreserveSig]
    int QueryInsertEmbedded(Guid* pguidService, void* pFormatEtc, int* pfInsertable);

    [PreserveSig]
    int InsertEmbedded(uint dwFlags, int acpStart, int acpEnd, nint pDataObject, global::Windows.Win32.UI.TextServices.TS_TEXTCHANGE* pChange);

    [PreserveSig]
    int InsertTextAtSelection(uint dwFlags, char* pchText, uint cch, int* pacpStart, int* pacpEnd, global::Windows.Win32.UI.TextServices.TS_TEXTCHANGE* pChange);

    [PreserveSig]
    int InsertEmbeddedAtSelection(uint dwFlags, nint pDataObject, int* pacpStart, int* pacpEnd, global::Windows.Win32.UI.TextServices.TS_TEXTCHANGE* pChange);

    [PreserveSig]
    int RequestSupportedAttrs(uint dwFlags, uint cFilterAttrs, Guid* paFilterAttrs);

    [PreserveSig]
    int RequestAttrsAtPosition(int acpPos, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags);

    [PreserveSig]
    int RequestAttrsTransitioningAtPosition(int acpPos, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags);

    [PreserveSig]
    int FindNextAttrTransition(int acpStart, int acpHalt, uint cFilterAttrs, Guid* paFilterAttrs, uint dwFlags, int* pacpNext, int* pfFound,
                               int* plFoundOffset);

    [PreserveSig]
    int RetrieveRequestedAttrs(uint ulCount, global::Windows.Win32.UI.TextServices.TS_ATTRVAL* paAttrVals, uint* pcFetched);

    [PreserveSig]
    int GetEndACP(int* pacp);

    [PreserveSig]
    int GetActiveView(uint* pvcView);

    [PreserveSig]
    int GetACPFromPoint(uint vcView, global::System.Drawing.Point* ptScreen, uint dwFlags, int* pacp);

    [PreserveSig]
    int GetTextExt(uint vcView, int acpStart, int acpEnd, global::Windows.Win32.Foundation.RECT* prc, int* pfClipped);

    [PreserveSig]
    int GetScreenExt(uint vcView, global::Windows.Win32.Foundation.RECT* prc);

    [PreserveSig]
    int GetWnd(uint vcView, nint* phwnd);
}

/// <summary>ITfContextOwnerCompositionSink (msctf.h): the context's owner hears when compositions start, change and end.</summary>
[GeneratedComInterface]
[Guid("5F20AA40-B57A-4F34-96AB-3576F377CC79")]
internal unsafe partial interface ITfContextOwnerCompositionSink
{
    [PreserveSig]
    int OnStartComposition(nint pComposition, int* pfOk);

    [PreserveSig]
    int OnUpdateComposition(nint pComposition, nint pRangeNew);

    [PreserveSig]
    int OnEndComposition(nint pComposition);
}

/// <summary>ITfTextEditSink (msctf.h): called after every edit session, with a cookie that can read the context.</summary>
[GeneratedComInterface]
[Guid("8127d409-ccd3-4683-967a-b43d5b482bf7")]
internal partial interface ITfTextEditSink
{
    [PreserveSig]
    int OnEndEdit(nint pic, uint ecReadOnly, nint pEditRecord);
}

/// <summary>ITfEditSession (msctf.h): work a text service does while it holds the context's lock. Tests act as a text service with it.</summary>
[GeneratedComInterface]
[Guid("aa80e803-2021-11d2-93e0-0060b067b86e")]
internal partial interface ITfEditSession
{
    [PreserveSig]
    int DoEditSession(uint ec);
}
