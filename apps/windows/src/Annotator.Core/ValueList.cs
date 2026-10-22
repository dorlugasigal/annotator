using System.Collections;
using System.Runtime.CompilerServices;

namespace Annotator.Core;

/// <summary>Factory methods for <see cref="ValueList{T}"/>.</summary>
public static class ValueList
{
    public static ValueList<T> Create<T>(ReadOnlySpan<T> items) => ValueList<T>.FromSpan(items);

    public static ValueList<T> CreateRange<T>(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items switch
        {
            ValueList<T> list => list,
            T[] array => ValueList<T>.FromSpan(array),
            List<T> list => ValueList<T>.FromSpan(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)),
            _ => ValueList<T>.FromSpan(items.ToArray()),
        };
    }

    public static ValueList<T> Repeat<T>(T value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var items = new T[count];
        Array.Fill(items, value);
        return ValueList<T>.Adopt(items);
    }

    public static ValueList<T> ToValueList<T>(this IEnumerable<T> items) => CreateRange(items);
}

/// <summary>
/// An immutable list with value equality: the C# form of a Swift array stored inside a value type.
/// Copies share storage safely because no value ever changes. Appending writes in place only when the
/// list ends at the newest claimed slot of its storage, so no other value can see the new item; that keeps
/// stroke sampling amortized O(1), like Swift's copy-on-write arrays. Any other change copies.
/// </summary>
[CollectionBuilder(typeof(ValueList), nameof(ValueList.Create))]
public readonly struct ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly Storage? storage;
    private readonly int count;

    private ValueList(Storage storage, int count)
    {
        this.storage = storage;
        this.count = count;
    }

    public static ValueList<T> Empty => default;

    public int Count => count;

    public bool IsEmpty => count == 0;

    public ref readonly T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)count)
            {
                ThrowIndex(index);
            }
            return ref storage!.Items[index];
        }
    }

    T IReadOnlyList<T>.this[int index] => this[index];

    public ReadOnlySpan<T> AsSpan() => storage is null ? default : new ReadOnlySpan<T>(storage.Items, 0, count);

    public static implicit operator ReadOnlySpan<T>(ValueList<T> list) => list.AsSpan();

    public T[] ToArray() => AsSpan().ToArray();

    public ValueList<T> Add(T item)
    {
        var current = storage;
        if (current is not null && count < current.Items.Length
            && Interlocked.CompareExchange(ref current.Claimed, count + 1, count) == count)
        {
            current.Items[count] = item;
            return new ValueList<T>(current, count + 1);
        }
        var grown = new Storage(GrowCapacity(count + 1), count + 1);
        AsSpan().CopyTo(grown.Items);
        grown.Items[count] = item;
        return new ValueList<T>(grown, count + 1);
    }

    public ValueList<T> AddRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty)
        {
            return this;
        }
        var total = checked(count + items.Length);
        var current = storage;
        if (current is not null && total <= current.Items.Length
            && Interlocked.CompareExchange(ref current.Claimed, total, count) == count)
        {
            items.CopyTo(current.Items.AsSpan(count));
            return new ValueList<T>(current, total);
        }
        var grown = new Storage(count == 0 ? total : GrowCapacity(total), total);
        AsSpan().CopyTo(grown.Items);
        items.CopyTo(grown.Items.AsSpan(count));
        return new ValueList<T>(grown, total);
    }

    public ValueList<T> AddRange(ValueList<T> items) => AddRange(items.AsSpan());

    public ValueList<T> SetItem(int index, T item)
    {
        if ((uint)index >= (uint)count)
        {
            ThrowIndex(index);
        }
        var copy = AsSpan().ToArray();
        copy[index] = item;
        return Adopt(copy);
    }

    /// <summary>Removes one item. The result never shares storage, so removed references are released.</summary>
    public ValueList<T> RemoveAt(int index)
    {
        if ((uint)index >= (uint)count)
        {
            ThrowIndex(index);
        }
        var source = AsSpan();
        var copy = new T[count - 1];
        source[..index].CopyTo(copy);
        source[(index + 1)..].CopyTo(copy.AsSpan(index));
        return Adopt(copy);
    }

    public ValueList<TResult> Map<TResult>(Func<T, TResult> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        var source = AsSpan();
        var result = new TResult[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            result[index] = transform(source[index]);
        }
        return ValueList<TResult>.Adopt(result);
    }

    public ValueList<T> Filter(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var result = new List<T>(count);
        foreach (var item in AsSpan())
        {
            if (predicate(item))
            {
                result.Add(item);
            }
        }
        return result.Count == count ? this : ValueList.CreateRange(result);
    }

    public bool Contains(T item) => IndexOf(item) >= 0;

    public int IndexOf(T item)
    {
        var span = AsSpan();
        for (var index = 0; index < span.Length; index++)
        {
            if (ItemEquals(span[index], item))
            {
                return index;
            }
        }
        return -1;
    }

    public Enumerator GetEnumerator() => new(storage?.Items, count);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() =>
        storage is null ? Enumerable.Empty<T>().GetEnumerator() : new ArraySegment<T>(storage.Items, 0, count).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();

    /// <summary>Element-wise equality, like Swift `==` on arrays. Doubles compare with `==`.</summary>
    public bool Equals(ValueList<T> other)
    {
        if (count != other.count)
        {
            return false;
        }
        if (count == 0 || ReferenceEquals(storage, other.storage))
        {
            return true;
        }
        var left = AsSpan();
        var right = other.AsSpan();
        for (var index = 0; index < left.Length; index++)
        {
            if (!ItemEquals(left[index], right[index]))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(count);
        var span = AsSpan();
        for (var index = 0; index < Math.Min(span.Length, 8); index++)
        {
            hash.Add(span[index]);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(ValueList<T> left, ValueList<T> right) => left.Equals(right);

    public static bool operator !=(ValueList<T> left, ValueList<T> right) => !left.Equals(right);

    public override string ToString() => $"[{string.Join(", ", ToArray())}]";

    internal static ValueList<T> FromSpan(ReadOnlySpan<T> items) => items.IsEmpty ? default : Adopt(items.ToArray());

    /// <summary>Wraps an array the caller will never change again.</summary>
    internal static ValueList<T> Adopt(T[] items) =>
        items.Length == 0 ? default : new ValueList<T>(new Storage(items), items.Length);

    private static bool ItemEquals(T left, T right)
    {
        if (typeof(T) == typeof(double))
        {
            return Unsafe.As<T, double>(ref left) == Unsafe.As<T, double>(ref right);
        }
        return EqualityComparer<T>.Default.Equals(left, right);
    }

    private static int GrowCapacity(int needed)
    {
        var doubled = (long)needed * 2;
        return (int)Math.Clamp(doubled, Math.Max(4, needed), Math.Max(needed, Array.MaxLength));
    }

    private static void ThrowIndex(int index) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, "The index is outside the list.");

    private sealed class Storage
    {
        internal readonly T[] Items;
        internal int Claimed;

        internal Storage(int capacity, int claimed)
        {
            Items = new T[capacity];
            Claimed = claimed;
        }

        internal Storage(T[] items)
        {
            Items = items;
            Claimed = items.Length;
        }
    }

    public struct Enumerator
    {
        private readonly T[]? items;
        private readonly int count;
        private int index;

        internal Enumerator(T[]? items, int count)
        {
            this.items = items;
            this.count = count;
            index = -1;
        }

        public readonly ref readonly T Current => ref items![index];

        public bool MoveNext() => ++index < count;
    }
}
