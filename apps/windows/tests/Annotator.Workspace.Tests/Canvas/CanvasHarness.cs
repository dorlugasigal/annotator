using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Canvas;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>Fixed text metrics: each character is half the font size wide and a line is 1.2 font sizes high.</summary>
internal sealed class FixedTextMeasurer : ITextMeasurer
{
    public Size Measure(string text, TextFont font)
    {
        string[] lines = text.Split('\n');
        return new Size(lines.Max(line => line.Length) * font.Size * 0.5, lines.Length * font.Size * 1.2);
    }

    public FontLineMetrics LineMetrics(TextFont font) => new(font.Size * 0.9, font.Size * 0.3, 0);
}

/// <summary>Queues channel calls like the app's channel, which posts them to the app thread; the test runs them with <see cref="CanvasHarness.Pump"/>.</summary>
internal sealed class QueuedChannel(WorkspaceModel model) : IWorkspaceChannel
{
    private readonly Queue<Action> _calls = new();

    public List<EditResult> Results { get; } = [];

    public List<CanvasCommand> Commands { get; } = [];

    public int Pending => _calls.Count;

    public void BeginEdit(DisplayId display, DocumentVersion baseVersion, EditKind kind) =>
        _calls.Enqueue(() => Results.Add(model.BeginEdit(display, baseVersion, kind)));

    public void UpdateEdit(DisplayId display, DocumentVersion baseVersion, CanvasDocument document) =>
        _calls.Enqueue(() => Results.Add(model.UpdateEdit(display, baseVersion, document)));

    public void CancelEdit(DisplayId display, DocumentVersion baseVersion) =>
        _calls.Enqueue(() => Results.Add(model.CancelEdit(display, baseVersion)));

    public void Submit(EditSubmission submission) => _calls.Enqueue(() => Results.Add(model.Submit(submission)));

    public void Send(CanvasCommand command)
    {
        Commands.Add(command);
        _calls.Enqueue(() => model.Perform(command));
    }

    public bool RunOne()
    {
        if (_calls.Count == 0)
        {
            return false;
        }
        _calls.Dequeue()();
        return true;
    }
}

/// <summary>A text editor host that types by replacing the whole text, like a person selecting all and typing.</summary>
internal sealed class FakeTextEditor : ITextEditorHost
{
    public CanvasController? Canvas { get; private set; }

    public CanvasTextSession? Session { get; private set; }

    public int Begun { get; private set; }

    public int Ended { get; private set; }

    public void Begin(CanvasController canvas, CanvasTextSession session)
    {
        Canvas = canvas;
        Session = session;
        Begun++;
    }

    public void Update(CanvasTextSession session) => Session = session;

    public void End()
    {
        Session = null;
        Ended++;
    }
}

/// <summary>Runs scheduled work only when the test asks, to show what happens while recognition is still running.</summary>
internal sealed class ManualScheduler : ICanvasScheduler
{
    private readonly Queue<Action> _work = new();

    public int Pending => _work.Count;

    public void Run<T>(Func<T> work, Action<T> done, Action<Exception> failed) => _work.Enqueue(() => done(work()));

    public bool RunNext()
    {
        if (_work.Count == 0)
        {
            return false;
        }
        _work.Dequeue()();
        return true;
    }
}

/// <summary>
/// A workspace in Draw with one canvas per display, wired as the app wires them: canvas calls queue up and
/// <see cref="Pump"/> runs them on the model, then hands the new snapshot to every canvas.
/// </summary>
internal sealed class CanvasHarness : IDisposable
{
    /// <summary>One large display at 100% scale, so points are pixels (the Mac tests' 1x reference canvases).</summary>
    public static readonly DisplayId Main = new("main");

    public static DisplayDescriptor MainDisplay { get; } = new(Main, new Rect(0, 0, 1600, 1200), new Rect(0, 0, 1600, 1160), 1);

    private readonly Dictionary<DisplayId, CanvasController> _canvases = [];
    private long _time = 10_000;
    private ulong _delivered;

    /// <summary>A workspace with only <see cref="MainDisplay"/>.</summary>
    public static CanvasHarness Single(IWorkspacePreferencesStore? store = null, ICanvasScheduler? scheduler = null, bool reducesMotion = false) =>
        new([MainDisplay], store, scheduler, reducesMotion);

    public CanvasHarness(DisplayDescriptor[]? displays = null, IWorkspacePreferencesStore? store = null, ICanvasScheduler? scheduler = null,
                         bool reducesMotion = false, bool startDrawing = true, bool textEditing = true, ITextEditorHost? textEditor = null)
    {
        Displays = displays ?? [TestWorkspace.PrimaryDisplay, TestWorkspace.LeftDisplay];
        Model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => Displays,
            Pointer = () => DesktopPointer,
            PreferencesStore = store ?? new MemoryPreferencesStore(),
            Clipboard = Clipboard,
            TextEditingAvailable = textEditing,
        });
        Channel = new QueuedChannel(Model);
        var options = new CanvasOptions
        {
            TextMeasurer = new FixedTextMeasurer(),
            Scheduler = scheduler ?? ImmediateCanvasScheduler.Instance,
            TextEditor = textEditing ? textEditor ?? Editor : null,
            Coordinator = Coordinator,
            ReducesMotion = () => ReducesMotion,
        };
        ReducesMotion = reducesMotion;
        foreach (DisplayDescriptor display in Displays)
        {
            _canvases[display.Id] = new CanvasController(display.Id, Channel, options);
        }
        if (startDrawing)
        {
            Model.BeginDrawing();
        }
        Pump();
    }

    public DisplayDescriptor[] Displays { get; set; }

    public WorkspaceModel Model { get; }

    public QueuedChannel Channel { get; }

    public CanvasCoordinator Coordinator { get; } = new();

    public FakeTextEditor Editor { get; } = new();

    public MemoryClipboard Clipboard { get; } = new();

    /// <summary>The pointer in desktop units for paste placement, or null when unknown.</summary>
    public Point? DesktopPointer { get; set; }

    public bool ReducesMotion { get; set; }

    public List<CanvasRequest> Requests { get; } = [];

    public CanvasController this[DisplayId display] => _canvases[display];

    public CanvasController Primary => _canvases[TestWorkspace.Primary];

    public CanvasController Left => _canvases[TestWorkspace.Left];

    /// <summary>Runs every queued canvas call on the model and delivers the latest snapshot until nothing is left.</summary>
    public void Pump()
    {
        for (int round = 0; round < 100; round++)
        {
            while (Channel.RunOne())
            {
            }
            WorkspaceSnapshot snapshot = Model.Snapshot;
            foreach ((DisplayId id, CanvasController canvas) in _canvases)
            {
                canvas.Update(snapshot, snapshot.Display(id));
            }
            _delivered = snapshot.Revision;
            if (Channel.Pending == 0)
            {
                return;
            }
        }
        throw new InvalidOperationException("The canvases and the workspace kept sending each other changes.");
    }

    /// <summary>Delivers changes the test made on the model directly, as the app delivers every snapshot before the next input.</summary>
    public void Sync()
    {
        if (Channel.Pending > 0 || Model.Snapshot.Revision != _delivered)
        {
            Pump();
        }
    }

    public void Watch(CanvasController canvas) => canvas.Requested += Requests.Add;

    public void Choose(Tool tool)
    {
        Model.Choose(tool);
        Pump();
    }

    /// <summary>Replaces a display's document, as the Mac tests assign session.document.</summary>
    public void SetDocument(DisplayId display, params Element[] elements)
    {
        Model.ReplaceDocumentForTesting(display, new CanvasDocument(ElementList.Create(elements)));
        Pump();
    }

    /// <summary>Adds objects through a workspace edit and selects <paramref name="select"/>.</summary>
    public void Add(DisplayId display, Element[] elements, params Guid[] select)
    {
        TestWorkspace.AssertAccepted(Model.Edit(display, document =>
        {
            foreach (Element element in elements)
            {
                document[0].Add(element);
            }
            if (select.Length > 0)
            {
                document[0].Select(select);
            }
        }));
        Pump();
    }

    public Element[] Elements(DisplayId display) => Model.Elements(display);

    public CanvasDocument Document(DisplayId display) => Model.Document(display);

    public void Down(DisplayId display, Point point, KeyModifiers modifiers = KeyModifiers.None, PointerButtons button = PointerButtons.Primary,
                     PointerDevice device = PointerDevice.Mouse, double pressure = 1, bool hasPressure = false, long gap = 1_000, bool pump = true)
    {
        if (pump)
        {
            Sync();
        }
        _time += gap;
        PointerSample[] samples = [new(point, pressure, _time)];
        this[display].OnPointer(new PointerEvent
        {
            Phase = PointerPhase.Down,
            Device = device,
            PointerId = 1,
            Buttons = button,
            ChangedButton = button,
            Modifiers = modifiers,
            HasPressure = hasPressure,
            Samples = samples,
        });
        if (pump)
        {
            Pump();
        }
    }

    public void Move(DisplayId display, Point point, KeyModifiers modifiers = KeyModifiers.None, bool held = true, double pressure = 1,
                     bool hasPressure = false, bool pump = true) =>
        Samples(display, [new PointerSample(point, pressure, _time += 8)], modifiers, held, hasPressure, pump);

    /// <summary>One move event that carries several coalesced samples.</summary>
    public void Samples(DisplayId display, PointerSample[] samples, KeyModifiers modifiers = KeyModifiers.None, bool held = true,
                        bool hasPressure = false, bool pump = true)
    {
        if (pump)
        {
            Sync();
        }
        this[display].OnPointer(new PointerEvent
        {
            Phase = PointerPhase.Move,
            PointerId = 1,
            Buttons = held ? PointerButtons.Primary : PointerButtons.None,
            Modifiers = modifiers,
            HasPressure = hasPressure,
            Samples = samples,
        });
        if (pump)
        {
            Pump();
        }
    }

    public void Up(DisplayId display, Point point, KeyModifiers modifiers = KeyModifiers.None, double pressure = 0, bool hasPressure = false,
                   PointerButtons button = PointerButtons.Primary, bool pump = true)
    {
        if (pump)
        {
            Sync();
        }
        PointerSample[] samples = [new(point, pressure, _time += 8)];
        this[display].OnPointer(new PointerEvent
        {
            Phase = PointerPhase.Up,
            PointerId = 1,
            ChangedButton = button,
            Modifiers = modifiers,
            HasPressure = hasPressure,
            Samples = samples,
        });
        if (pump)
        {
            Pump();
        }
    }

    public void Click(DisplayId display, Point point, int count = 1, KeyModifiers modifiers = KeyModifiers.None)
    {
        for (int index = 0; index < count; index++)
        {
            Down(display, point, modifiers, gap: index == 0 ? 1_000 : 100);
            Up(display, point, modifiers);
        }
    }

    public void Drag(DisplayId display, Point from, Point to, int steps = 1, KeyModifiers modifiers = KeyModifiers.None)
    {
        Down(display, from, modifiers);
        for (int step = 1; step <= steps; step++)
        {
            Move(display, from + (to - from) * (step / (double)steps), modifiers);
        }
        Up(display, to, modifiers);
    }

    public bool Key(DisplayId display, int virtualKey, KeyModifiers modifiers = KeyModifiers.None, string? text = null)
    {
        Sync();
        bool handled = this[display].OnKey(new KeyEvent(virtualKey, modifiers, text));
        Pump();
        return handled;
    }

    public void Type(string text)
    {
        Sync();
        CanvasController canvas = Editor.Canvas ?? throw new InvalidOperationException("No text editor is open.");
        canvas.SetText(text);
        Pump();
    }

    public void Dispose()
    {
        foreach (CanvasController canvas in _canvases.Values)
        {
            canvas.Dispose();
        }
    }

    /// <summary>A preferences store whose profile already has these tool styles.</summary>
    public static MemoryPreferencesStore Store(params (Tool Tool, Style Style)[] styles)
    {
        var preferences = new WorkspacePreferences();
        foreach ((Tool tool, Style style) in styles)
        {
            preferences.ToolStyles = preferences.ToolStyles.SetItem(tool.RawValue, style);
        }
        return new MemoryPreferencesStore(preferences.Data());
    }
}
