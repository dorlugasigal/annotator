using System.Collections;
using System.Runtime.CompilerServices;

namespace Annotator.Core;

/// <summary>
/// An immutable list of elements with value equality: the C# form of Swift's `[Element]`.
/// Elements are large structs, so the list keeps them in fixed chunks of 16. Replacing one element copies
/// one chunk and the chunk table, not the whole board, which keeps stroke sampling and drags cheap on
/// boards with thousands of objects. Unchanged chunks are shared between versions and history snapshots.
/// </summary>
[CollectionBuilder(typeof(ElementList), nameof(Create))]
public readonly struct ElementList : IReadOnlyList<Element>, IEquatable<ElementList>
{
    private const int Shift = 4;
    private const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private readonly Element[][]? chunks;
    private readonly int count;

    private ElementList(Element[][] chunks, int count)
    {
        this.chunks = chunks;
        this.count = count;
    }

    public static ElementList Empty => default;

    public int Count => count;

    public bool IsEmpty => count == 0;

    public ref readonly Element this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the list.");
            }
            return ref chunks![index >> Shift][index & Mask];
        }
    }

    Element IReadOnlyList<Element>.this[int index] => this[index];

    public static ElementList Create(ReadOnlySpan<Element> items)
    {
        if (items.IsEmpty)
        {
            return default;
        }
        var table = new Element[(items.Length + Mask) >> Shift][];
        for (var chunk = 0; chunk < table.Length; chunk++)
        {
            var start = chunk << Shift;
            var slice = items.Slice(start, Math.Min(ChunkSize, items.Length - start));
            table[chunk] = new Element[ChunkSize];
            slice.CopyTo(table[chunk]);
        }
        return new ElementList(table, items.Length);
    }

    public static ElementList CreateRange(IEnumerable<Element> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items switch
        {
            ElementList list => list,
            Element[] array => Create(array),
            List<Element> list => Create(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)),
            _ => Create(items.ToArray()),
        };
    }

    public ElementList Add(in Element element)
    {
        var chunkIndex = count >> Shift;
        var offset = count & Mask;
        var old = chunks ?? [];
        var table = new Element[offset == 0 ? chunkIndex + 1 : old.Length][];
        Array.Copy(old, table, old.Length);
        table[chunkIndex] = offset == 0 ? new Element[ChunkSize] : (Element[])old[chunkIndex].Clone();
        table[chunkIndex][offset] = element;
        return new ElementList(table, count + 1);
    }

    public ElementList AddRange(ReadOnlySpan<Element> items)
    {
        if (items.IsEmpty)
        {
            return this;
        }
        if (count == 0)
        {
            return Create(items);
        }
        var total = checked(count + items.Length);
        var old = chunks!;
        var table = new Element[(total + Mask) >> Shift][];
        Array.Copy(old, table, old.Length);
        var lastOld = old.Length - 1;
        if ((count & Mask) != 0)
        {
            table[lastOld] = (Element[])old[lastOld].Clone();
        }
        for (var chunk = old.Length; chunk < table.Length; chunk++)
        {
            table[chunk] = new Element[ChunkSize];
        }
        for (var index = 0; index < items.Length; index++)
        {
            var position = count + index;
            table[position >> Shift][position & Mask] = items[index];
        }
        return new ElementList(table, total);
    }

    public ElementList AddRange(ElementList items) => count == 0 ? items : AddRange(items.ToArray());

    public ElementList SetItem(int index, in Element element)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the list.");
        }
        var table = (Element[][])chunks!.Clone();
        var chunk = (Element[])table[index >> Shift].Clone();
        chunk[index & Mask] = element;
        table[index >> Shift] = chunk;
        return new ElementList(table, count);
    }

    /// <summary>Keeps the elements that match, in order (Swift `filter`).</summary>
    public ElementList Filter(Func<Element, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var kept = new List<Element>(count);
        foreach (ref readonly var element in this)
        {
            if (predicate(element))
            {
                kept.Add(element);
            }
        }
        return kept.Count == count ? this : CreateRange(kept);
    }

    /// <summary>Removes the elements that match (Swift `removeAll(where:)`).</summary>
    public ElementList RemoveAll(Func<Element, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return Filter(element => !match(element));
    }

    public int FindIndex(Func<Element, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        for (var index = 0; index < count; index++)
        {
            if (predicate(this[index]))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>The index of the first element with this identifier, or -1.</summary>
    public int IndexOf(Guid id)
    {
        for (var index = 0; index < count; index++)
        {
            if (this[index].Id == id)
            {
                return index;
            }
        }
        return -1;
    }

    public bool Contains(Guid id) => IndexOf(id) >= 0;

    public Element[] ToArray()
    {
        var result = new Element[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = this[index];
        }
        return result;
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<Element> IEnumerable<Element>.GetEnumerator()
    {
        for (var index = 0; index < count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<Element>)this).GetEnumerator();

    /// <summary>Element-wise equality, like Swift `==` on arrays. Shared chunks compare by reference.</summary>
    public bool Equals(ElementList other)
    {
        if (count != other.count)
        {
            return false;
        }
        if (count == 0 || ReferenceEquals(chunks, other.chunks))
        {
            return true;
        }
        for (var chunk = 0; chunk < chunks!.Length; chunk++)
        {
            var left = chunks[chunk];
            var right = other.chunks![chunk];
            if (ReferenceEquals(left, right))
            {
                continue;
            }
            var length = Math.Min(ChunkSize, count - (chunk << Shift));
            for (var index = 0; index < length; index++)
            {
                if (!left[index].Equals(in right[index]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ElementList other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(count);
        for (var index = 0; index < Math.Min(count, 8); index++)
        {
            hash.Add(this[index].Id);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(ElementList left, ElementList right) => left.Equals(right);

    public static bool operator !=(ElementList left, ElementList right) => !left.Equals(right);

    public struct Enumerator
    {
        private readonly ElementList list;
        private int index;

        internal Enumerator(ElementList list)
        {
            this.list = list;
            index = -1;
        }

        public readonly ref readonly Element Current => ref list[index];

        public bool MoveNext() => ++index < list.Count;
    }
}
