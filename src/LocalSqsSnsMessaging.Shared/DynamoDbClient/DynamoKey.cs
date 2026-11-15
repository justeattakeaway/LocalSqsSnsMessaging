namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// A primary key (or an index key): a partition value and, for a composite key, a sort value.
/// Ordered as DynamoDB orders keys, by <see cref="DynamoKeyOrder"/>. A key with an
/// <see cref="Edge"/> is a bound rather than a key: it sorts before (<c>-1</c>) or after
/// (<c>+1</c>) every key of its partition, so a sorted set can be cut down to one partition.
/// </summary>
internal sealed class DynamoKey : IEquatable<DynamoKey>
{
    public DynamoKey(DynamoValue partition, DynamoValue? sort)
    {
        Partition = partition;
        Sort = sort;
    }

    private DynamoKey(DynamoValue partition, int edge)
    {
        Partition = partition;
        Edge = edge;
    }

    public DynamoValue Partition { get; }

    public DynamoValue? Sort { get; }

    public int Edge { get; }

    /// <summary>The bound before every key of <paramref name="partition"/>.</summary>
    public static DynamoKey PartitionStart(DynamoValue partition) => new(partition, -1);

    /// <summary>The bound after every key of <paramref name="partition"/>.</summary>
    public static DynamoKey PartitionEnd(DynamoValue partition) => new(partition, 1);

    public static IComparer<DynamoKey> Comparer { get; } = new KeyComparer();

    public bool Equals(DynamoKey? other) =>
        other is not null && Edge == other.Edge && Comparer.Compare(this, other) == 0;

    public override bool Equals(object? obj) => obj is DynamoKey other && Equals(other);

    public override int GetHashCode() => unchecked(Partition.GetHashCode() * 31 + (Sort?.GetHashCode() ?? 0));

    public override string ToString() => Sort is null ? Partition.ToString() : $"{Partition}/{Sort}";

    private sealed class KeyComparer : IComparer<DynamoKey>
    {
        public int Compare(DynamoKey? x, DynamoKey? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            var partition = DynamoKeyOrder.Compare(x!.Partition, y!.Partition);
            if (partition != 0)
            {
                return partition;
            }

            if (x.Edge != 0 || y.Edge != 0)
            {
                return x.Edge.CompareTo(y.Edge);
            }

            return (x.Sort, y.Sort) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                _ => DynamoKeyOrder.Compare(x.Sort, y.Sort),
            };
        }
    }
}

/// <summary>An item's entry in a secondary index: its index key, then its table key to tell apart items with the same index key.</summary>
internal sealed class DynamoIndexEntry(DynamoKey indexKey, DynamoKey tableKey)
{
    public DynamoKey IndexKey { get; } = indexKey;

    public DynamoKey TableKey { get; } = tableKey;

    public static IComparer<DynamoIndexEntry> Comparer { get; } = new EntryComparer();

    /// <summary>A bound for a sorted index: before or after every entry of a partition.</summary>
    public static DynamoIndexEntry Bound(DynamoKey edge) => new(edge, edge);

    private sealed class EntryComparer : IComparer<DynamoIndexEntry>
    {
        public int Compare(DynamoIndexEntry? x, DynamoIndexEntry? y)
        {
            var index = DynamoKey.Comparer.Compare(x!.IndexKey, y!.IndexKey);
            if (index != 0 || x.IndexKey.Edge != 0 || y.IndexKey.Edge != 0)
            {
                return index;
            }

            return DynamoKey.Comparer.Compare(x.TableKey, y.TableKey);
        }
    }
}

/// <summary>
/// How DynamoDB orders key values: strings by their UTF-8 bytes, numbers numerically, binaries by
/// their unsigned bytes. (<see cref="string.CompareOrdinal(string, string)"/> compares UTF-16 code
/// units, which orders characters above U+FFFF before U+E000–U+FFFF; UTF-8 order doesn't.)
/// </summary>
internal static class DynamoKeyOrder
{
    public static int Compare(DynamoValue x, DynamoValue y)
    {
        if (x.Type != y.Type)
        {
            return ((int)x.Type).CompareTo((int)y.Type);
        }

        return x.Type switch
        {
            DynamoType.S => CompareUtf8(x.S, y.S),
            DynamoType.N => DynamoNumber.TryParse(x.N, out var a) && DynamoNumber.TryParse(y.N, out var b)
                ? a.CompareTo(b)
                : string.CompareOrdinal(x.N, y.N),
            DynamoType.B => CompareBytes(x.B, y.B),
            _ => 0,
        };
    }

    /// <summary>Compares two strings in the order of their UTF-8 encodings, which is code point order.</summary>
    public static int CompareUtf8(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x == y)
            {
                continue;
            }

            // Surrogates (U+D800–U+DFFF) encode code points above U+FFFF, which sort after every
            // BMP character in UTF-8, including U+E000–U+FFFF.
            var xSurrogate = char.IsSurrogate(x);
            var ySurrogate = char.IsSurrogate(y);
            if (xSurrogate != ySurrogate)
            {
                return xSurrogate ? 1 : -1;
            }

            return x.CompareTo(y);
        }

        return a.Length.CompareTo(b.Length);
    }

    public static int CompareBytes(byte[] a, byte[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
