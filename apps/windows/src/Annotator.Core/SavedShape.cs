// Port of apps/macos/Sources/AnnotationCore/SavedShape.swift.
namespace Annotator.Core;

/// <summary>The cases of Swift's `ShapeLibraryError`.</summary>
public enum ShapeLibraryErrorKind
{
    NoSelection,
    UnfinishedEdit,
    InvalidName,
    UnsupportedContent,
    InvalidFile,
    EntryTooLarge,
    LibraryFull,
    UndoBudget,
}

/// <summary>Swift `ShapeLibraryError`. <see cref="Exception.Message"/> is the Swift `errorDescription`, word for word.</summary>
public sealed class ShapeLibraryError : Exception, IEquatable<ShapeLibraryError>
{
    private ShapeLibraryError(ShapeLibraryErrorKind kind, string? detail = null)
        : base(Describe(kind, detail))
    {
        Kind = kind;
        Detail = detail;
    }

    public ShapeLibraryErrorKind Kind { get; }

    /// <summary>The detail of `invalidFile(String)`.</summary>
    public string? Detail { get; }

    public string ErrorDescription => Message;

    public static ShapeLibraryError NoSelection => new(ShapeLibraryErrorKind.NoSelection);

    public static ShapeLibraryError UnfinishedEdit => new(ShapeLibraryErrorKind.UnfinishedEdit);

    public static ShapeLibraryError InvalidName => new(ShapeLibraryErrorKind.InvalidName);

    public static ShapeLibraryError UnsupportedContent => new(ShapeLibraryErrorKind.UnsupportedContent);

    public static ShapeLibraryError EntryTooLarge => new(ShapeLibraryErrorKind.EntryTooLarge);

    public static ShapeLibraryError LibraryFull => new(ShapeLibraryErrorKind.LibraryFull);

    public static ShapeLibraryError UndoBudget => new(ShapeLibraryErrorKind.UndoBudget);

    public static ShapeLibraryError InvalidFile(string detail) => new(ShapeLibraryErrorKind.InvalidFile, detail);

    public bool Equals(ShapeLibraryError? other) => other is not null && Kind == other.Kind && Detail == other.Detail;

    public override bool Equals(object? obj) => Equals(obj as ShapeLibraryError);

    public override int GetHashCode() => HashCode.Combine(Kind, Detail);

    private static string Describe(ShapeLibraryErrorKind kind, string? detail) => kind switch
    {
        ShapeLibraryErrorKind.NoSelection => "Select one or more drawings to save.",
        ShapeLibraryErrorKind.UnfinishedEdit => "Finish or cancel the current edit, then try again.",
        ShapeLibraryErrorKind.InvalidName => "Enter a name with 1 to 120 characters and no line breaks or control characters.",
        ShapeLibraryErrorKind.UnsupportedContent =>
            "My shapes saves drawings and text only. Deselect covers, pixel effects and images, then try again.",
        ShapeLibraryErrorKind.InvalidFile => $"My shapes could not read a saved shape. {detail}",
        ShapeLibraryErrorKind.EntryTooLarge => "This shape exceeds the 8 MB limit. Select fewer drawings; nothing was saved.",
        ShapeLibraryErrorKind.LibraryFull => "My shapes is full (256 shapes or 64 MB). Delete a saved shape, then try again.",
        ShapeLibraryErrorKind.UndoBudget =>
            "This canvas is too large to insert a shape with Undo. Save the board and use a smaller canvas.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>A drawing saved in My shapes. Immutable, like the Swift struct with `let` properties.</summary>
public sealed class SavedShape
{
    public const int CurrentSchema = 1;
    public const int MaximumBytes = 8 * 1_024 * 1_024;

    /// <summary>Swift `SavedShape(name:document:)`: saves the selected drawings of a document that is not being edited.</summary>
    public SavedShape(string name, CanvasDocument document)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (document.IsEditing)
        {
            throw ShapeLibraryError.UnfinishedEdit;
        }
        var selection = document.Selection;
        var selected = document.Elements.Filter(element => selection.Contains(element.Id));
        if (selected.IsEmpty)
        {
            throw ShapeLibraryError.NoSelection;
        }
        CheckContent(selected);
        var targets = ShapeIds(selected);
        var detached = new Element[selected.Count];
        for (var index = 0; index < selected.Count; index++)
        {
            var element = selected[index];
            if (element.StartAnchor is { } start && !targets.Contains(start.ElementId))
            {
                element.StartAnchor = null;
            }
            if (element.EndAnchor is { } end && !targets.Contains(end.ElementId))
            {
                element.EndAnchor = null;
            }
            detached[index] = element;
        }
        SchemaVersion = CurrentSchema;
        Id = Guid.NewGuid();
        Name = SwiftText.TrimWhitespacesAndNewlines(name);
        Drawing = new DrawingFile(new CanvasDocument(ElementList.Create(detached)));
        Validate();
        _ = Data();
    }

    private SavedShape(Guid id, string name, DrawingFile drawing)
    {
        SchemaVersion = CurrentSchema;
        Id = id;
        Name = SwiftText.TrimWhitespacesAndNewlines(name);
        Drawing = drawing;
        Validate();
    }

    /// <summary>The decoded form; <see cref="Read"/> validates it.</summary>
    internal SavedShape(long schemaVersion, Guid id, string name, DrawingFile drawing)
    {
        SchemaVersion = schemaVersion;
        Id = id;
        Name = name;
        Drawing = drawing;
    }

    public long SchemaVersion { get; }

    public Guid Id { get; }

    public string Name { get; }

    public DrawingFile Drawing { get; }

    public ElementList Elements => Drawing.Elements;

    public long EstimatedBytes
    {
        get
        {
            long total = 0;
            foreach (ref readonly var element in Elements)
            {
                total += element.EstimatedBytes;
            }
            return total;
        }
    }

    public Rect Bounds
    {
        get
        {
            Rect? result = null;
            foreach (ref readonly var element in Elements)
            {
                var frame = element.Frame;
                var center = frame.Center;
                var box = Rect.Enclosing(
                [
                    frame.Origin.Rotated(center, element.Rotation),
                    new Point(frame.MaxX, frame.Y).Rotated(center, element.Rotation),
                    new Point(frame.X, frame.MaxY).Rotated(center, element.Rotation),
                    new Point(frame.MaxX, frame.MaxY).Rotated(center, element.Rotation),
                ]);
                result = result is { } union ? union.Union(box) : box;
            }
            return result ?? Rect.Zero;
        }
    }

    public SavedShape Renamed(string name)
    {
        var result = new SavedShape(Id, name, Drawing);
        _ = result.Data();
        return result;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchema)
        {
            throw ShapeLibraryError.InvalidFile("This format needs a newer version of Annotator.");
        }
        if (Name.Length == 0 || SwiftText.ScalarCount(Name) > 120
            || Name != SwiftText.TrimWhitespacesAndNewlines(Name) || !NameCharactersAllowed(Name))
        {
            throw ShapeLibraryError.InvalidName;
        }
        CheckContent(Elements);
        if (Elements.IsEmpty)
        {
            throw ShapeLibraryError.NoSelection;
        }
        if (EstimatedBytes > MaximumBytes)
        {
            throw ShapeLibraryError.EntryTooLarge;
        }
        _ = Drawing.Document();
        var targets = ShapeIds(Elements);
        foreach (ref readonly var element in Elements)
        {
            if ((element.StartAnchor is { } start && !targets.Contains(start.ElementId))
                || (element.EndAnchor is { } end && !targets.Contains(end.ElementId)))
            {
                throw ShapeLibraryError.InvalidFile("A connector refers to a drawing outside this shape.");
            }
        }
    }

    /// <summary>The saved shape file: UTF-8 JSON with sorted keys, like the Mac.</summary>
    public byte[] Data()
    {
        Validate();
        var data = AnnotatorJson.Encode(this);
        if (data.Length > MaximumBytes)
        {
            throw ShapeLibraryError.EntryTooLarge;
        }
        return data;
    }

    public static SavedShape Read(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumBytes)
        {
            throw ShapeLibraryError.EntryTooLarge;
        }
        var shape = AnnotatorJson.Decode<SavedShape>(data);
        shape.Validate();
        return shape;
    }

    /// <summary>Inserts fresh copies centered at <paramref name="center"/> as one undo step and selects them.</summary>
    public CanvasDocument Adding(CanvasDocument document, Point center)
    {
        if (document.IsEditing)
        {
            throw ShapeLibraryError.UnfinishedEdit;
        }
        if (!center.IsFinite)
        {
            throw CanvasError.InvalidGeometry;
        }
        _ = Data();
        long originalBytes = 64;
        foreach (ref readonly var element in document.Elements)
        {
            originalBytes += element.EstimatedBytes;
        }
        if (originalBytes + EstimatedBytes > Math.Min(document.HistoryByteLimit, ShapeLibraryStore.MaximumBytes))
        {
            throw ShapeLibraryError.UndoBudget;
        }
        var usedIds = new HashSet<Guid>();
        ElementList[] sources = [document.Elements, Elements];
        foreach (var list in sources)
        {
            foreach (ref readonly var element in list)
            {
                usedIds.Add(element.Id);
                if (element.GroupId is { } group)
                {
                    usedIds.Add(group);
                }
            }
        }
        Guid FreshId()
        {
            var id = Guid.NewGuid();
            while (!usedIds.Add(id))
            {
                id = Guid.NewGuid();
            }
            return id;
        }
        var ids = new Dictionary<Guid, Guid>();
        foreach (ref readonly var element in Elements)
        {
            ids.Add(element.Id, FreshId());
        }
        var groups = new Dictionary<Guid, Guid>();
        foreach (ref readonly var element in Elements)
        {
            if (element.GroupId is { } group && !groups.ContainsKey(group))
            {
                groups.Add(group, FreshId());
            }
        }
        Guid? reusableGroup = Elements.Count > 1 ? FreshId() : null;
        var offset = center - Bounds.Center;
        var copies = new Element[Elements.Count];
        for (var index = 0; index < Elements.Count; index++)
        {
            ref readonly var original = ref Elements[index];
            var copy = original;
            if (!ids.TryGetValue(original.Id, out var id))
            {
                throw ShapeLibraryError.InvalidFile("An object ID is missing.");
            }
            copy.Id = id;
            copy.GroupId = reusableGroup ?? (original.GroupId is { } group && groups.TryGetValue(group, out var copiedGroup) ? copiedGroup : null);
            copy.Locked = false;
            copy.StartAnchor = original.StartAnchor is { } start && ids.TryGetValue(start.ElementId, out var startTarget)
                ? new Anchor(startTarget, start.Relative) : null;
            copy.EndAnchor = original.EndAnchor is { } end && ids.TryGetValue(end.ElementId, out var endTarget)
                ? new Anchor(endTarget, end.Relative) : null;
            copy.Translate(offset);
            copy.Validate();
            copies[index] = copy;
        }
        var result = document;
        result.Begin();
        foreach (var copy in copies)
        {
            result.Add(copy);
        }
        result.Select(copies.Select(copy => copy.Id));
        result.Commit();
        return result;
    }

    private static void CheckContent(ElementList elements)
    {
        foreach (ref readonly var element in elements)
        {
            if (element.Tool.IsProtection || element.Tool == Tool.Image || element.ImagePng is not null)
            {
                throw ShapeLibraryError.UnsupportedContent;
            }
        }
    }

    private static HashSet<Guid> ShapeIds(ElementList elements)
    {
        var ids = new HashSet<Guid>();
        foreach (ref readonly var element in elements)
        {
            if (element.Tool.IsShape)
            {
                ids.Add(element.Id);
            }
        }
        return ids;
    }

    private static bool NameCharactersAllowed(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            if (SwiftText.IsControl(rune) || SwiftText.IsNewline(rune)
                || rune.Value is 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069))
            {
                return false;
            }
        }
        return true;
    }
}
