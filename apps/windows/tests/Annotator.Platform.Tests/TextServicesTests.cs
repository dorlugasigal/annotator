using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Annotator.Platform.Text;
using Annotator.Platform.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.TextServices;
using CsEditSession = Windows.Win32.UI.TextServices.ITfEditSession;

namespace Annotator.Platform.Tests;

/// <summary>
/// The text editor's Text Services Framework store (spec 013 S-7, WIN-INP-09). This machine has no Japanese, Chinese or
/// Korean input method turned on, so each test acts as a text service itself: it asks the real TSF manager for an edit
/// session and starts, changes and ends compositions with TSF's own calls, which reach the store the way an IME's do.
/// </summary>
public sealed class TextServicesTests
{
    [Fact]
    public async Task ACompositionAnInputMethodTypesReachesTheDocument()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator TSF test");
        TsfScenarios.Result result = await thread.Run(TsfScenarios.TypeAndEndAComposition);
        Assert.Equal("ab\u304b\u306a", result.Text);
        Assert.True(result.ComposingWhileTyping);
        Assert.False(result.ComposingAfterwards);
        Assert.Equal(0, result.Failures);
        Assert.Equal("start", result.Events[0]);
        Assert.Contains("replace 2-2 \"\u304b\u306a\"", result.Events);
        Assert.Contains("composition 2-4", result.Events);
        Assert.Contains("styled 0", result.Events);
        Assert.Equal("end", result.Events[^1]);
    }

    [Fact]
    public async Task TheAppsOwnEditsAreWhatTsfReadsNext()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator TSF test");
        (string before, string after) = await thread.Run(TsfScenarios.ReadAfterTheAppTypes);
        Assert.Equal("Hello", before);
        Assert.Equal("Hello world", after);
    }

    [Fact]
    public async Task FinishingACompositionKeepsItsTextAndEndsIt()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator TSF test");
        TsfScenarios.Result result = await thread.Run(TsfScenarios.FinishAComposition);
        Assert.True(result.ComposingWhileTyping);
        Assert.False(result.ComposingAfterwards);
        Assert.Equal("\ud55c", result.Text);
        Assert.Equal("end", result.Events[^1]);
    }

    [Fact]
    public async Task CompositionAttributesAreReadAfterEveryEdit()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator TSF test");
        TsfScenarios.Result result = await thread.Run(TsfScenarios.MarkACompositionWithAnUnknownAttribute);
        Assert.Equal(0, result.Failures);
        // An attribute no input method provides draws as plain composition text.
        Assert.Contains("styled 0", result.Events);
        Assert.Equal("abc", result.Text);
    }

    [Fact]
    public async Task TheStoreKeepsTsfsLockRules()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator TSF test");
        (int unlockedRead, int unlockedWrite, int readOnlyWrite, int nested) = await thread.Run(TsfScenarios.BreakTheLockRules);
        Assert.Equal(Tsf.TS_E_NOLOCK, unlockedRead);
        Assert.Equal(Tsf.TS_E_NOLOCK, unlockedWrite);
        Assert.Equal(Tsf.TS_E_NOLOCK, readOnlyWrite);
        Assert.Equal(Tsf.TS_E_SYNCHRONOUS, nested);
    }

    [Fact]
    public async Task StartingAndEndingASessionIsCheap()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator TSF test");
        double[] milliseconds = await thread.Run(TsfScenarios.TimeSessions);
        Array.Sort(milliseconds);
        TestContext.Current.SendDiagnosticMessage($"TSF session start and end: p50 {milliseconds[10]:F3} ms, max {milliseconds[^1]:F3} ms (20 runs).");
        Assert.True(milliseconds[10] < 50, $"A session took {milliseconds[10]} ms at p50.");
    }
}

/// <summary>The tests' work on the TSF thread, acting as a text service with TSF's own calls.</summary>
internal static unsafe class TsfScenarios
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public sealed record Result(string Text, List<string> Events, bool ComposingWhileTyping, bool ComposingAfterwards, int Failures);

    public static Result TypeAndEndAComposition()
    {
        var document = new FakeDocument("ab");
        using TextServicesSession session = TextServicesSession.Start(0, document);
        var failures = new List<Exception>();
        session.Failed += failures.Add;
        nint composition = 0;
        Assert.Equal(0, Edit(session, ec =>
        {
            ITfRange* range = SelectionRange(session, ec);
            composition = (nint)StartComposition(session, ec, range);
            SetText(range, ec, "\u304b\u306a");
            ComPtr.Release(ref range);
            return 0;
        }));
        bool composing = session.IsComposing;
        Assert.Equal(0, Edit(session, ec =>
        {
            NativeCallException.ThrowIfFailed(((ITfComposition*)composition)->EndComposition(ec), "ITfComposition::EndComposition");
            return 0;
        }));
        Marshal.Release(composition);
        return new Result(document.Text, document.Events, composing, session.IsComposing, failures.Count);
    }

    public static (string Before, string After) ReadAfterTheAppTypes()
    {
        var document = new FakeDocument("Hello");
        using TextServicesSession session = TextServicesSession.Start(0, document);
        string before = ReadAll(session);
        // The person types: the app changes its text, then tells TSF.
        document.Text = "Hello world";
        document.Selection = new TextStoreSelection(11, 11);
        session.TextChanged(5, 5, 11);
        session.LayoutChanged();
        return (before, ReadAll(session));
    }

    public static Result FinishAComposition()
    {
        var document = new FakeDocument(string.Empty);
        using TextServicesSession session = TextServicesSession.Start(0, document);
        Assert.Equal(0, Edit(session, ec =>
        {
            ITfRange* range = SelectionRange(session, ec);
            ITfComposition* started = StartComposition(session, ec, range);
            SetText(range, ec, "\ud55c");
            ComPtr.Release(ref started);
            ComPtr.Release(ref range);
            return 0;
        }));
        bool composing = session.IsComposing;
        session.FinishComposition();
        return new Result(document.Text, document.Events, composing, session.IsComposing, 0);
    }

    public static Result MarkACompositionWithAnUnknownAttribute()
    {
        var document = new FakeDocument(string.Empty);
        using TextServicesSession session = TextServicesSession.Start(0, document);
        var failures = new List<Exception>();
        session.Failed += failures.Add;
        uint atom = Atom(Guid.NewGuid());
        Assert.Equal(0, Edit(session, ec =>
        {
            ITfRange* range = SelectionRange(session, ec);
            ITfComposition* started = StartComposition(session, ec, range);
            SetText(range, ec, "abc");
            Guid property = PInvoke.GUID_PROP_ATTRIBUTE;
            ITfProperty* attributes;
            NativeCallException.ThrowIfFailed(session.Context->GetProperty(&property, &attributes), "ITfContext::GetProperty");
            VARIANT value = default;
            value.vt = VARENUM.VT_I4;
            value.lVal = (int)atom;
            NativeCallException.ThrowIfFailed(attributes->SetValue(ec, range, &value), "ITfProperty::SetValue");
            ComPtr.Release(ref attributes);
            ComPtr.Release(ref started);
            ComPtr.Release(ref range);
            return 0;
        }));
        bool composing = session.IsComposing;
        session.FinishComposition();
        return new Result(document.Text, document.Events, composing, session.IsComposing, failures.Count);
    }

    public static (int UnlockedRead, int UnlockedWrite, int ReadOnlyWrite, int Nested) BreakTheLockRules()
    {
        var document = new FakeDocument("text");
        using TextServicesSession session = TextServicesSession.Start(0, document);
        ITextStoreACP store = session.Store;
        TS_SELECTION_ACP selection;
        uint fetched;
        int unlockedRead = store.GetSelection(0, 1, &selection, &fetched);
        TS_TEXTCHANGE change;
        char letter = 'x';
        int unlockedWrite = store.SetText(0, 0, 0, &letter, 1, &change);
        int readOnlyWrite = 0, nested = 0;
        Assert.Equal(0, Edit(session, ec =>
        {
            TS_TEXTCHANGE inner;
            char value = 'y';
            readOnlyWrite = store.SetText(0, 0, 0, &value, 1, &inner);
            int result;
            Assert.Equal(0, store.RequestLock(Tsf.TS_LF_SYNC | Tsf.TS_LF_READ, &result));
            nested = result;
            return 0;
        }, write: false));
        return (unlockedRead, unlockedWrite, readOnlyWrite, nested);
    }

    public static double[] TimeSessions()
    {
        var times = new double[20];
        for (int index = 0; index < times.Length; index++)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            using (TextServicesSession.Start(0, new FakeDocument("x")))
            {
            }
            times[index] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return times;
    }

    /// <summary>Runs <paramref name="work"/> as a text service would, in a synchronous edit session; returns the session's result.</summary>
    private static int Edit(TextServicesSession session, Func<uint, int> work, bool write = true)
    {
        var editor = new EditSession(work);
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(editor, CreateComInterfaceFlags.None);
        nint pointer = 0;
        try
        {
            pointer = ComPtr.QueryInterface(unknown, Tsf.IID_ITfEditSession, "QueryInterface(ITfEditSession)");
            HRESULT result;
            var flags = (TF_CONTEXT_EDIT_CONTEXT_FLAGS)(Tsf.TF_ES_SYNC | (write ? Tsf.TF_ES_READWRITE : Tsf.TF_ES_READ));
            NativeCallException.ThrowIfFailed(session.Context->RequestEditSession(session.ClientId, (CsEditSession*)pointer, flags, &result),
                                              "ITfContext::RequestEditSession");
            editor.Rethrow();
            return result.Value;
        }
        finally
        {
            ComPtr.Release(ref pointer);
            ComPtr.Release(ref unknown);
        }
    }

    /// <summary>The range at the selection, as ITfInsertAtSelection gives it to a text service.</summary>
    private static ITfRange* SelectionRange(TextServicesSession session, uint ec)
    {
        ITfInsertAtSelection* insert = ComPtr.QueryInterface<ITfInsertAtSelection>(session.Context, ITfInsertAtSelection.IID_Guid, "QueryInterface(ITfInsertAtSelection)");
        try
        {
            ITfRange* range;
            NativeCallException.ThrowIfFailed(insert->InsertTextAtSelection(ec, INSERT_TEXT_AT_SELECTION_FLAGS.TF_IAS_QUERYONLY, default, 0, &range),
                                              "ITfInsertAtSelection::InsertTextAtSelection");
            return range;
        }
        finally
        {
            ComPtr.Release(ref insert);
        }
    }

    private static ITfComposition* StartComposition(TextServicesSession session, uint ec, ITfRange* range)
    {
        ITfContextComposition* compositions = ComPtr.QueryInterface<ITfContextComposition>(session.Context, ITfContextComposition.IID_Guid,
                                                                                           "QueryInterface(ITfContextComposition)");
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(new CompositionSink(), CreateComInterfaceFlags.None);
        nint sink = 0;
        try
        {
            sink = ComPtr.QueryInterface(unknown, CompositionSink.Id, "QueryInterface(ITfCompositionSink)");
            ITfComposition* started;
            NativeCallException.ThrowIfFailed(compositions->StartComposition(ec, range, (Windows.Win32.UI.TextServices.ITfCompositionSink*)sink, &started),
                                              "ITfContextComposition::StartComposition");
            return started;
        }
        finally
        {
            ComPtr.Release(ref sink);
            ComPtr.Release(ref unknown);
            ComPtr.Release(ref compositions);
        }
    }

    private static void SetText(ITfRange* range, uint ec, string text)
    {
        fixed (char* chars = text)
        {
            NativeCallException.ThrowIfFailed(range->SetText(ec, 0, new PCWSTR(chars), text.Length), "ITfRange::SetText");
        }
    }

    /// <summary>All of the context's text, read as a text service reads it.</summary>
    private static string ReadAll(TextServicesSession session)
    {
        string text = string.Empty;
        Assert.Equal(0, Edit(session, ec =>
        {
            ITfRange* start = null;
            try
            {
                NativeCallException.ThrowIfFailed(session.Context->GetStart(ec, &start), "ITfContext::GetStart");
                int shifted;
                NativeCallException.ThrowIfFailed(start->ShiftEnd(ec, 1_000, &shifted, null), "ITfRange::ShiftEnd");
                char* buffer = stackalloc char[256];
                uint count;
                NativeCallException.ThrowIfFailed(start->GetText(ec, 0, new PWSTR(buffer), 256, &count), "ITfRange::GetText");
                text = new string(buffer, 0, (int)count);
                return 0;
            }
            finally
            {
                ComPtr.Release(ref start);
            }
        }, write: false));
        return text;
    }

    private static uint Atom(Guid guid)
    {
        Guid clsid = PInvoke.CLSID_TF_CategoryMgr, iid = ITfCategoryMgr.IID_Guid;
        ITfCategoryMgr* categories;
        NativeCallException.ThrowIfFailed(PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&categories),
                                          "CoCreateInstance(CLSID_TF_CategoryMgr)");
        try
        {
            uint atom;
            NativeCallException.ThrowIfFailed(categories->RegisterGUID(&guid, &atom), "ITfCategoryMgr::RegisterGUID");
            return atom;
        }
        finally
        {
            ComPtr.Release(ref categories);
        }
    }

    private sealed class FakeDocument(string text) : ITextStoreDocument
    {
        public string Text { get; set; } = text;

        public List<string> Events { get; } = [];

        public int Length => Text.Length;

        public TextStoreSelection Selection { get; set; } = new(text.Length, text.Length);

        public PixelRect ScreenBounds => new(100, 200, 400, 240);

        public void CopyText(int start, Span<char> destination) => Text.AsSpan(start, destination.Length).CopyTo(destination);

        public void Select(TextStoreSelection selection)
        {
            Selection = selection;
            Events.Add($"select {selection.Start}-{selection.End}");
        }

        public TextStoreChange Replace(int start, int end, ReadOnlySpan<char> text)
        {
            string inserted = text.ToString();
            Text = string.Concat(Text.AsSpan(0, start), inserted, Text.AsSpan(end));
            if (Selection.End >= start && Selection.Start <= end)
            {
                Selection = new TextStoreSelection(start + inserted.Length, start + inserted.Length);
            }
            Events.Add($"replace {start}-{end} \"{inserted}\"");
            return new TextStoreChange(start, end, start + inserted.Length);
        }

        public PixelRect TextBounds(int start, int end) => new(100 + start * 10, 200, 100 + Math.Max(end, start + 1) * 10, 240);

        public int? OffsetAt(int x, int y, bool nearest) => ScreenBounds.Contains(x, y) || nearest ? Math.Clamp((x - 100) / 10, 0, Text.Length) : null;

        public void CompositionStarted() => Events.Add("start");

        public void CompositionChanged(int start, int end) => Events.Add($"composition {start}-{end}");

        public void CompositionStyled(ReadOnlySpan<TextStoreAttributeRun> runs) => Events.Add($"styled {runs.Length}");

        public void CompositionEnded() => Events.Add("end");
    }
}

/// <summary>ITfCompositionSink (msctf.h): how a text service hears that its composition was ended for it.</summary>
[GeneratedComInterface]
[Guid("A781718C-579A-4B15-A280-32B8577ACC5E")]
internal partial interface ICompositionTerminationSink
{
    [PreserveSig]
    int OnCompositionTerminated(uint ecWrite, nint pComposition);
}

[GeneratedComClass]
internal sealed partial class CompositionSink : ICompositionTerminationSink
{
    public static readonly Guid Id = new("A781718C-579A-4B15-A280-32B8577ACC5E");

    public int OnCompositionTerminated(uint ecWrite, nint pComposition) => 0;
}

/// <summary>A text service's edit session: runs its work with the cookie TSF grants and keeps any exception for the test.</summary>
[GeneratedComClass]
internal sealed partial class EditSession(Func<uint, int> work) : Text.ITfEditSession
{
    private Exception? _failure;

    public int DoEditSession(uint ec)
    {
        try
        {
            return work(ec);
        }
        catch (Exception error)
        {
            _failure = error;
            return unchecked((int)0x80004005);
        }
    }

    public void Rethrow()
    {
        if (_failure is { } failure)
        {
            _failure = null;
            throw new InvalidOperationException("The edit session failed.", failure);
        }
    }
}
