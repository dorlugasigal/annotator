// Port of apps/macos/Sources/AnnotationCore/Document.swift.
using System.Collections.Immutable;

namespace Annotator.Core;

/// <summary>Changes one element in place; used by <see cref="CanvasDocument.Update"/> and <see cref="CanvasDocument.EditSelection"/>.</summary>
public delegate void ElementMutation(ref Element element);

/// <summary>The clipboard format shared with the Mac (declared in the Mac app's WorkspaceModel.swift).</summary>
public static class AnnotationClipboard
{
    /// <summary>Registered clipboard format name. The payload is <see cref="DrawingFile.Data"/> (UTF-8 JSON).</summary>
    public const string TypeName = "app.annotator.annotations";
}

/// <summary>
/// Swift `CanvasDocument`: the objects on one display with transactions, undo and redo.
/// A value type: copying a document copies its whole state, including history.
/// Create documents with <see cref="Empty"/> or a constructor; <c>default(CanvasDocument)</c> is not a valid document.
/// </summary>
public struct CanvasDocument
{
    public const long DefaultHistoryByteLimit = 64L * 1_024 * 1_024;
    public const int DefaultHistoryCountLimit = 100;

    private delegate void DocumentMutation(ref CanvasDocument document);

    private ImmutableHashSet<Guid>? selection;
    private ValueList<Snapshot> past;
    private ValueList<Snapshot> future;
    private Snapshot? pending;
    private ImmutableHashSet<Guid>? restoredStrokeIds;

    /// <summary>An empty, validated document with the default history limits.</summary>
    public CanvasDocument()
        : this(ElementList.Empty)
    {
    }

    /// <summary>Swift `CanvasDocument(elements:historyByteLimit:historyCountLimit:)`. Validates every element.</summary>
    public CanvasDocument(ElementList elements = default, long historyByteLimit = DefaultHistoryByteLimit,
                          int historyCountLimit = DefaultHistoryCountLimit)
    {
        Identity = Guid.NewGuid();
        if (historyByteLimit <= 0 || historyCountLimit <= 0)
        {
            throw CanvasError.InvalidDocument("History limits must be positive.");
        }
        var identifiers = new HashSet<Guid>();
        foreach (ref readonly var element in elements)
        {
            identifiers.Add(element.Id);
        }
        if (identifiers.Count != elements.Count)
        {
            throw CanvasError.InvalidDocument("Duplicate object identifiers.");
        }
        foreach (ref readonly var element in elements)
        {
            element.Validate();
        }
        Elements = elements;
        Presentation = new PresentationSettings();
        HistoryByteLimit = historyByteLimit;
        HistoryCountLimit = historyCountLimit;
        ResolveBindings();
    }

    private CanvasDocument(bool validatedEmpty)
    {
        _ = validatedEmpty;
        Identity = Guid.NewGuid();
        Elements = ElementList.Empty;
        Presentation = new PresentationSettings();
        HistoryByteLimit = DefaultHistoryByteLimit;
        HistoryCountLimit = DefaultHistoryCountLimit;
    }

    /// <summary>New documents may share a generation counter, but not a render identity.</summary>
    public Guid Identity { get; }

    public ElementList Elements { get; private set; }

    public ImmutableHashSet<Guid> Selection
    {
        readonly get => selection ?? ImmutableHashSet<Guid>.Empty;
        private set => selection = value;
    }

    public ulong Generation { get; private set; }

    public PresentationSettings Presentation { get; private set; }

    public long HistoryByteLimit { get; }

    public int HistoryCountLimit { get; }

    public readonly bool CanUndo => !past.IsEmpty;

    public readonly bool CanRedo => !future.IsEmpty;

    public readonly Guid? UndoOperationId => past.IsEmpty ? null : past[past.Count - 1].OperationId;

    public readonly Guid? RedoOperationId => future.IsEmpty ? null : future[future.Count - 1].OperationId;

    public readonly ImmutableHashSet<Guid> HistoryOperationIds
    {
        get
        {
            var builder = ImmutableHashSet.CreateBuilder<Guid>();
            foreach (var snapshot in past)
            {
                if (snapshot.OperationId is { } id)
                {
                    builder.Add(id);
                }
            }
            foreach (var snapshot in future)
            {
                if (snapshot.OperationId is { } id)
                {
                    builder.Add(id);
                }
            }
            return builder.ToImmutable();
        }
    }

    public readonly bool IsEditing => pending is not null;

    public readonly bool HasPendingChanges => pending is { } original && (original.Elements != Elements || original.Presentation != Presentation);

    public readonly bool HasProtection
    {
        get
        {
            foreach (ref readonly var element in Elements)
            {
                if (element.Tool.IsProtection)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public readonly long HistoryBytes
    {
        get
        {
            long total = 0;
            foreach (var snapshot in past)
            {
                total += snapshot.Bytes;
            }
            foreach (var snapshot in future)
            {
                total += snapshot.Bytes;
            }
            return total;
        }
    }

    private readonly Snapshot CurrentSnapshot => Snapshot.Capture(Elements, Selection, Presentation);

    public static CanvasDocument Empty() => new(validatedEmpty: true);

    public void Begin()
    {
        pending ??= CurrentSnapshot;
    }

    public void Commit(Guid? operationId = null)
    {
        if (pending is not { } original)
        {
            throw CanvasError.NoTransaction;
        }
        ResolveBindings();
        pending = null;
        if (original.Elements != Elements || original.Presentation != Presentation || operationId is not null)
        {
            Generation = unchecked(Generation + 1);
            past = past.Add(original with { OperationId = operationId });
            future = default;
            TrimHistory();
        }
    }

    public void Cancel()
    {
        if (pending is not { } original)
        {
            return;
        }
        Restore(original);
        pending = null;
    }

    public void Undo()
    {
        if (pending is not null)
        {
            Cancel();
            return;
        }
        if (past.IsEmpty)
        {
            return;
        }
        var previous = past[past.Count - 1];
        past = past.RemoveAt(past.Count - 1);
        future = future.Add(CurrentSnapshot with { OperationId = previous.OperationId });
        Restore(previous);
        Generation = unchecked(Generation + 1);
        TrimHistory();
    }

    public void Redo()
    {
        Cancel();
        if (future.IsEmpty)
        {
            return;
        }
        var next = future[future.Count - 1];
        future = future.RemoveAt(future.Count - 1);
        past = past.Add(CurrentSnapshot with { OperationId = next.OperationId });
        Restore(next);
        Generation = unchecked(Generation + 1);
        TrimHistory();
    }

    public void Select(IEnumerable<Guid> ids, bool extending = false)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var valid = ImmutableHashSet.CreateRange(ids).Intersect(ElementIds(Elements));
        Selection = extending ? Selection.SymmetricExcept(valid) : valid;
    }

    public void SetPresentation(PresentationSettings settings)
    {
        settings.Validate();
        Transaction((ref CanvasDocument document) => document.Presentation = settings);
    }

    public void StopPresentationWithoutHistory()
    {
        Presentation = Presentation with { Mode = PresentationMode.Off };
        if (pending is not null)
        {
            pending = pending with { Presentation = pending.Presentation with { Mode = PresentationMode.Off } };
        }
        if (!past.IsEmpty && past[past.Count - 1] is var last && last.Elements == Elements && last.Presentation == Presentation)
        {
            past = past.RemoveAt(past.Count - 1);
        }
    }

    /// <summary>Swift `select(at:extending:)`: selects the frontmost object, or its whole group.</summary>
    public void Select(Point point, bool extending = false)
    {
        if (ElementStacking.Frontmost(point, Elements) is not { } hit)
        {
            if (!extending)
            {
                Selection = ImmutableHashSet<Guid>.Empty;
            }
            return;
        }
        var elements = Elements;
        IEnumerable<Guid> ids = hit.GroupId is { } group
            ? elements.Filter(element => element.GroupId == group).Select(element => element.Id).ToArray()
            : [hit.Id];
        Select(ids, extending);
    }

    public void Add(Element element)
    {
        element.Validate();
        if (Elements.Contains(element.Id))
        {
            throw CanvasError.InvalidDocument("Duplicate object identifier.");
        }
        Transaction((ref CanvasDocument document) => document.Elements = document.Elements.Add(element));
    }

    public void Update(Guid id, ElementMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var index = Elements.IndexOf(id);
        if (index < 0)
        {
            throw CanvasError.MissingElement;
        }
        if (Elements[index].Locked)
        {
            return;
        }
        var changed = Elements[index];
        mutation(ref changed);
        if (changed.Id != id)
        {
            throw CanvasError.InvalidDocument("Object identity cannot change.");
        }
        changed.Validate();
        changed.Revision = unchecked(changed.Revision + 1);
        changed.InvalidateGeometry();
        Transaction((ref CanvasDocument document) =>
        {
            document.Elements = document.Elements.SetItem(index, changed);
            if (changed.Tool.IsShape)
            {
                document.ResolveBindings();
            }
        });
    }

    public void AppendSample(Guid id, Point point, double pressure = 1)
    {
        if (!point.IsFinite || !double.IsFinite(pressure) || !(pressure >= 0 && pressure <= 1))
        {
            throw CanvasError.InvalidGeometry;
        }
        var index = Elements.IndexOf(id);
        if (index < 0)
        {
            throw CanvasError.MissingElement;
        }
        if (!Elements[index].Tool.IsFreehand)
        {
            throw CanvasError.InvalidGeometry;
        }
        if (Elements[index].Locked)
        {
            return;
        }
        Transaction((ref CanvasDocument document) =>
        {
            var element = document.Elements[index];
            if (document.restoredStrokeIds is { } restored && restored.Contains(id))
            {
                document.restoredStrokeIds = restored.Remove(id);
                element.AppendEpoch = Guid.NewGuid();
            }
            var hadPoints = !element.Points.IsEmpty;
            if (hadPoints && element.Pressures.IsEmpty)
            {
                element.Pressures = ValueList.Repeat(1.0, element.Points.Count);
            }
            element.Points = element.Points.Add(point);
            element.Pressures = element.Pressures.Add(pressure);
            element.Frame = hadPoints ? element.Frame.Union(new Rect(point, point)) : new Rect(point, point);
            element.Revision = unchecked(element.Revision + 1);
            element.GeometryToken = Guid.NewGuid();
            document.Elements = document.Elements.SetItem(index, element);
        });
    }

    public void EditSelection(ElementMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        Transaction((ref CanvasDocument document) =>
        {
            var count = document.Elements.Count;
            for (var index = 0; index < count; index++)
            {
                ref readonly var current = ref document.Elements[index];
                if (!document.Selection.Contains(current.Id) || current.Locked)
                {
                    continue;
                }
                var changed = current;
                mutation(ref changed);
                if (changed.Id != current.Id)
                {
                    throw CanvasError.InvalidDocument("Object identity cannot change.");
                }
                changed.Validate();
                changed.Revision = unchecked(changed.Revision + 1);
                changed.InvalidateGeometry();
                document.Elements = document.Elements.SetItem(index, changed);
            }
            document.ResolveBindings();
        });
    }

    public void DeleteSelection(bool includingProtection = true)
    {
        Transaction((ref CanvasDocument document) =>
        {
            var selected = document.Selection;
            document.Elements = document.Elements.RemoveAll(element =>
                selected.Contains(element.Id) && !element.Locked && (includingProtection || !element.Tool.IsProtection));
            document.Selection = selected.Intersect(ElementIds(document.Elements));
            document.ResolveBindings();
        });
    }

    public void ClearAll()
    {
        Transaction((ref CanvasDocument document) =>
        {
            document.Elements = ElementList.Empty;
            document.Selection = ImmutableHashSet<Guid>.Empty;
            document.Presentation = document.Presentation with { Mode = PresentationMode.Off };
        });
    }

    public void ClearAnnotations()
    {
        Transaction((ref CanvasDocument document) =>
        {
            document.Elements = document.Elements.RemoveAll(element => !element.Tool.IsProtection && !element.Locked);
            document.Selection = document.Selection.Intersect(ElementIds(document.Elements));
            document.ResolveBindings();
        });
    }

    public void ClearProtection()
    {
        Transaction((ref CanvasDocument document) =>
        {
            document.Elements = document.Elements.RemoveAll(element => element.Tool.IsProtection);
            document.Selection = document.Selection.Intersect(ElementIds(document.Elements));
        });
    }

    public void DuplicateSelection()
    {
        var selected = Selection;
        InsertCopies(Elements.Filter(element => selected.Contains(element.Id) && !element.Locked), new Point(18, 18));
    }

    /// <summary>
    /// Adds unlocked copies with fresh identities and selects them. Groups and connector
    /// attachments are kept only between the copied objects.
    /// </summary>
    public void InsertCopies(ElementList originals, Point offset)
    {
        if (originals.IsEmpty)
        {
            return;
        }
        var remapping = new Dictionary<Guid, Guid>();
        foreach (ref readonly var original in originals)
        {
            remapping.TryAdd(original.Id, Guid.NewGuid());
        }
        Transaction((ref CanvasDocument document) =>
        {
            var groups = new Dictionary<Guid, Guid>();
            var copies = new Element[originals.Count];
            for (var index = 0; index < originals.Count; index++)
            {
                ref readonly var original = ref originals[index];
                var copy = original;
                copy.Id = remapping.TryGetValue(original.Id, out var mapped) ? mapped : Guid.NewGuid();
                copy.Locked = false;
                if (original.GroupId is { } group)
                {
                    if (!groups.TryGetValue(group, out var copiedGroup))
                    {
                        copiedGroup = Guid.NewGuid();
                        groups[group] = copiedGroup;
                    }
                    copy.GroupId = copiedGroup;
                }
                copy.Translate(offset);
                copy.StartAnchor = Remap(original.StartAnchor, remapping);
                copy.EndAnchor = Remap(original.EndAnchor, remapping);
                copies[index] = copy;
            }
            foreach (var copy in copies)
            {
                copy.Validate();
            }
            document.Elements = document.Elements.AddRange(copies);
            document.Selection = ImmutableHashSet.CreateRange(copies.Select(copy => copy.Id));
            document.ResolveBindings();
        });
    }

    public void GroupSelection()
    {
        var group = Guid.NewGuid();
        EditSelection((ref Element element) => element.GroupId = group);
    }

    public void UngroupSelection() => EditSelection((ref Element element) => element.GroupId = null);

    public void SetSelectionLocked(bool locked)
    {
        Transaction((ref CanvasDocument document) =>
        {
            for (var index = 0; index < document.Elements.Count; index++)
            {
                if (document.Selection.Contains(document.Elements[index].Id))
                {
                    var element = document.Elements[index];
                    element.Locked = locked;
                    document.Elements = document.Elements.SetItem(index, element);
                }
            }
        });
    }

    public void ArrangeSelection(bool toFront)
    {
        Transaction((ref CanvasDocument document) =>
        {
            var selected = document.Selection;
            var moved = document.Elements.Filter(element => selected.Contains(element.Id) && !element.Locked);
            var ids = ElementIds(moved);
            var rest = document.Elements.Filter(element => !ids.Contains(element.Id));
            document.Elements = toFront ? rest.AddRange(moved) : moved.AddRange(rest);
        });
    }

    public readonly Anchor? NearestAnchor(Point point, Guid excluding, double threshold = 18)
    {
        Anchor? nearest = null;
        var nearestDistance = double.PositiveInfinity;
        foreach (ref readonly var element in Elements)
        {
            if (element.Id == excluding || !element.Tool.IsShape)
            {
                continue;
            }
            foreach (var location in element.AnchorLocations)
            {
                var distance = element.AnchorPoint(location).Distance(point);
                if (distance <= threshold && distance < (nearest is null ? double.PositiveInfinity : nearestDistance))
                {
                    nearest = new Anchor(element.Id, location);
                    nearestDistance = distance;
                }
            }
        }
        return nearest;
    }

    /// <summary>Replaces a pen stroke with its recognized shape without adding a history step, so one undo removes both.</summary>
    public bool ApplyRecognition(Element replacement, ulong expectedGeneration, Guid expectedGeometryToken)
    {
        if (pending is not null || Generation != expectedGeneration)
        {
            return false;
        }
        var index = Elements.IndexOf(replacement.Id);
        if (index < 0)
        {
            return false;
        }
        ref readonly var current = ref Elements[index];
        if (current.GeometryToken != expectedGeometryToken || current.Tool != Tool.Pen || current.Locked
            || !(replacement.Tool.IsShape || replacement.Tool == Tool.Arrow))
        {
            return false;
        }
        replacement.Validate();
        var applied = replacement;
        applied.InvalidateGeometry();
        Elements = Elements.SetItem(index, applied);
        Generation = unchecked(Generation + 1);
        return true;
    }

    private void Transaction(DocumentMutation mutation)
    {
        var ownsTransaction = pending is null;
        if (ownsTransaction)
        {
            Begin();
        }
        try
        {
            mutation(ref this);
            if (ownsTransaction)
            {
                Commit();
            }
        }
        catch
        {
            Cancel();
            throw;
        }
    }

    private void ResolveBindings()
    {
        var elements = Elements;
        var targets = new Dictionary<Guid, int>();
        for (var index = 0; index < elements.Count; index++)
        {
            if (elements[index].Tool.IsShape)
            {
                targets.TryAdd(elements[index].Id, index);
            }
        }
        var changedAny = false;
        for (var index = 0; index < elements.Count; index++)
        {
            if (!elements[index].Tool.IsConnector || elements[index].Points.Count < 2)
            {
                continue;
            }
            var element = elements[index];
            var changed = false;
            for (var pass = 0; pass < 2; pass++)
            {
                var isStart = pass == 0;
                if ((isStart ? element.StartAnchor : element.EndAnchor) is not { } anchor)
                {
                    continue;
                }
                if (!targets.TryGetValue(anchor.ElementId, out var target))
                {
                    if (isStart)
                    {
                        element.StartAnchor = null;
                    }
                    else
                    {
                        element.EndAnchor = null;
                    }
                    changed = true;
                    continue;
                }
                var pointIndex = isStart ? 0 : element.Points.Count - 1;
                var resolved = elements[target].AnchorPoint(anchor.Relative);
                if (element.Points[pointIndex] != resolved)
                {
                    element.Points = element.Points.SetItem(pointIndex, resolved);
                    element.Frame = Rect.Enclosing(element.Points);
                    element.Revision = unchecked(element.Revision + 1);
                    element.InvalidateGeometry();
                    changed = true;
                }
            }
            if (changed)
            {
                elements = elements.SetItem(index, element);
                changedAny = true;
            }
        }
        if (changedAny)
        {
            Elements = elements;
        }
    }

    private void Restore(Snapshot value)
    {
        Elements = value.Elements;
        // Fork the append lineage on the next sample, keeping restored snapshots exact.
        var freehand = ImmutableHashSet.CreateBuilder<Guid>();
        foreach (ref readonly var element in Elements)
        {
            if (element.Tool.IsFreehand)
            {
                freehand.Add(element.Id);
            }
        }
        restoredStrokeIds = freehand.ToImmutable();
        Selection = value.Selection.Intersect(ElementIds(Elements));
        Presentation = value.Presentation;
    }

    private void TrimHistory()
    {
        while (past.Count > HistoryCountLimit)
        {
            past = past.RemoveAt(0);
        }
        while (future.Count > HistoryCountLimit)
        {
            future = future.RemoveAt(0);
        }
        while (HistoryBytes > HistoryByteLimit)
        {
            if (!past.IsEmpty)
            {
                past = past.RemoveAt(0);
            }
            else if (!future.IsEmpty)
            {
                future = future.RemoveAt(0);
            }
            else
            {
                break;
            }
        }
    }

    private static Anchor? Remap(Anchor? anchor, Dictionary<Guid, Guid> remapping) =>
        anchor is { } value && remapping.TryGetValue(value.ElementId, out var target) ? new Anchor(target, value.Relative) : null;

    private static HashSet<Guid> ElementIds(ElementList elements)
    {
        var ids = new HashSet<Guid>();
        foreach (ref readonly var element in elements)
        {
            ids.Add(element.Id);
        }
        return ids;
    }

    private sealed record Snapshot(ElementList Elements, ImmutableHashSet<Guid> Selection,
                                   PresentationSettings Presentation, long Bytes, Guid? OperationId)
    {
        public static Snapshot Capture(ElementList elements, ImmutableHashSet<Guid> selection, PresentationSettings presentation)
        {
            long bytes = 64;
            foreach (ref readonly var element in elements)
            {
                bytes += element.EstimatedBytes;
            }
            return new Snapshot(elements, selection, presentation, bytes, null);
        }
    }
}

/// <summary>Swift `DrawingFile`: the board file (`.annotator`) and the clipboard payload.</summary>
public struct DrawingFile
{
    public const int CurrentSchema = 3;

    private const int MaximumBytes = 128 * 1_024 * 1_024;

    public ElementList Elements;

    /// <summary>Chooses the oldest format that can hold the document: 3 for gradients or matching labels, 2 for labels, else 1.</summary>
    public DrawingFile(CanvasDocument document)
    {
        SchemaVersion = RequiresPaintSchema(document.Elements) ? 3 : HasLabels(document.Elements) ? 2 : 1;
        Elements = document.Elements;
    }

    internal DrawingFile(long schemaVersion, ElementList elements)
    {
        SchemaVersion = schemaVersion;
        Elements = elements;
    }

    public long SchemaVersion { get; }

    public readonly CanvasDocument Document()
    {
        if (!(SchemaVersion >= 1 && SchemaVersion <= CurrentSchema))
        {
            throw CanvasError.UnsupportedSchema(SchemaVersion);
        }
        ValidatePaintSchema();
        if (!(SchemaVersion >= 2 || !HasLabels(Elements)))
        {
            throw CanvasError.InvalidDocument("Shape labels require board format 2.");
        }
        return new CanvasDocument(Elements);
    }

    /// <summary>The board as UTF-8 JSON with sorted keys, like the Mac.</summary>
    public readonly byte[] Data()
    {
        ValidatePaintSchema();
        if (!(SchemaVersion >= 2 || !HasLabels(Elements)))
        {
            throw CanvasError.InvalidDocument("Shape labels require board format 2.");
        }
        var data = AnnotatorJson.Encode(this);
        if (data.Length > MaximumBytes)
        {
            throw CanvasError.InvalidDocument(
                "The board exceeds the supported file budget. Export a PNG or split the board; no objects were discarded.");
        }
        return data;
    }

    public static CanvasDocument Read(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes)
        {
            throw CanvasError.InvalidDocument("The file exceeds the safe import memory budget.");
        }
        return AnnotatorJson.Decode<DrawingFile>(data).Document();
    }

    private static bool HasLabels(ElementList elements)
    {
        foreach (ref readonly var element in elements)
        {
            if (element.Label is not null)
            {
                return true;
            }
        }
        return false;
    }

    private static bool RequiresPaintSchema(ElementList elements)
    {
        foreach (ref readonly var element in elements)
        {
            if (element.Style.UsesGradient || element.Label?.Style.UsesGradient == true || element.Label?.FollowsStroke == true)
            {
                return true;
            }
        }
        return false;
    }

    private readonly void ValidatePaintSchema()
    {
        if (!(SchemaVersion >= 3 || !RequiresPaintSchema(Elements)))
        {
            throw CanvasError.InvalidDocument("Gradients and matching labels need board format 3.");
        }
    }
}
