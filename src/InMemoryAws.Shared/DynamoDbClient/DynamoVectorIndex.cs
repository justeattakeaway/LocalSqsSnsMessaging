namespace InMemoryAws.DynamoDb;

/// <summary>
/// One element of a vector index's <c>SearchSchema</c>: an attribute a search can be scoped by,
/// with its declared type. <c>HASH</c> partitions the index (a search must name its value),
/// <c>INLINE_FILTER</c> filters it.
/// </summary>
internal sealed record DynamoSearchSchemaElement(DynamoKeyAttribute Attribute, string Type)
{
    public const string Hash = "HASH";

    public const string InlineFilter = "INLINE_FILTER";

    public string Name => Attribute.Name;
}

/// <summary>
/// A vector index: the vectors one attribute of the table's items holds, kept as 32-bit floats by
/// table key so <c>SearchVectors</c> can score them all (an exact search, where DynamoDB's is
/// approximate). Entries are sparse: an item is in the index only when it holds the vector
/// attribute, which every write checks against the index's dimensions.
/// </summary>
/// <remarks>
/// An index UpdateTable adds passes through <c>CREATING</c> (allocating, then backfilling) on
/// the cloud's clock before it is <c>ACTIVE</c>; it is filled at once and kept up to date
/// throughout, but only an <c>ACTIVE</c> index can be searched.
/// </remarks>
internal sealed class DynamoVectorIndex
{
    public const int MaxDimensions = 4096;

    /// <summary>
    /// Each indexed item's vector as written, its floats once a search has read them (a write
    /// only checks the numbers fit, so writing to a vector index that is never searched costs
    /// little more than writing to a table without one), and the entry's size.
    /// </summary>
    private readonly Dictionary<DynamoKey, (DynamoValue Value, float[]? Vector, long Size)> _entries = [];

    public required string Name { get; init; }

    public required string Arn { get; init; }

    /// <summary>The attribute holding each item's vector: a list of numbers.</summary>
    public required string VectorAttribute { get; init; }

    public required int Dimensions { get; init; }

    /// <summary><c>COSINE</c>, <c>EUCLIDEAN</c> or <c>DOT_PRODUCT</c>.</summary>
    public required string DistanceFunction { get; init; }

    public required DynamoProjection Projection { get; init; }

    public required IReadOnlyList<DynamoSearchSchemaElement> SearchSchema { get; init; }

    /// <summary><c>CREATING</c> or <c>ACTIVE</c>. A deleted index is removed at once, so is never seen <c>DELETING</c>.</summary>
    public string Status { get; set; } = "ACTIVE";

    /// <summary>While <c>CREATING</c>, when the index finishes allocating and starts backfilling; null once it has.</summary>
    public DateTimeOffset? BackfillingAt { get; set; }

    /// <summary>While <c>CREATING</c>, when the index becomes <c>ACTIVE</c>; null for one created with its table.</summary>
    public DateTimeOffset? ActiveAt { get; set; }

    /// <summary>Whether the index is past allocating and backfilling the table's items.</summary>
    public bool Backfilling => Status == "CREATING" && ActiveAt is not null && BackfillingAt is null;

    /// <summary>Whether the index is still allocating, before it backfills: it can't be deleted then.</summary>
    public bool Allocating => Status == "CREATING" && BackfillingAt is not null;

    public long SizeBytes { get; private set; }

    public int ItemCount => _entries.Count;

    /// <summary>Each indexed item's vector as floats, by table key, read (and kept) on first use.</summary>
    public List<(DynamoKey Key, float[] Vector)> ReadVectors()
    {
        var vectors = new List<(DynamoKey Key, float[] Vector)>(_entries.Count);
        List<DynamoKey>? unread = null;
        foreach (var (key, entry) in _entries)
        {
            if (entry.Vector is null)
            {
                (unread ??= []).Add(key);
            }

            // Every indexed value passed IsVector, so it reads as floats.
            vectors.Add((key, entry.Vector ?? Read(entry.Value, Dimensions)!));
        }

        if (unread is not null)
        {
            foreach (var (key, vector) in vectors)
            {
                var entry = _entries[key];
                if (entry.Vector is null)
                {
                    _entries[key] = entry with { Vector = vector };
                }
            }
        }

        return vectors;
    }

    /// <summary>The <c>HASH</c> element of the search schema, if it has one.</summary>
    public DynamoSearchSchemaElement? HashElement => SearchSchema.FirstOrDefault(e => e.Type == DynamoSearchSchemaElement.Hash);

    public DynamoSearchSchemaElement? SchemaElement(string attribute) =>
        SearchSchema.FirstOrDefault(e => string.Equals(e.Name, attribute, StringComparison.Ordinal));

    /// <summary>Moves a <c>CREATING</c> index on as its times come.</summary>
    public void Refresh(DateTimeOffset now)
    {
        if (BackfillingAt is { } backfillingAt && now >= backfillingAt)
        {
            BackfillingAt = null;
        }

        if (ActiveAt is { } activeAt && now >= activeAt)
        {
            ActiveAt = null;
            BackfillingAt = null;
            Status = "ACTIVE";
        }
    }

    /// <summary>
    /// Whether the index holds <paramref name="attribute"/> in an entry: the table's keys, the
    /// vector, the search schema, and the projection's attributes.
    /// </summary>
    public bool Projects(DynamoKeySchema tableKey, string attribute) =>
        Projection.Type == "ALL"
        || tableKey.IsKeyAttribute(attribute)
        || string.Equals(attribute, VectorAttribute, StringComparison.Ordinal)
        || SchemaElement(attribute) is not null
        || (Projection.Type == "INCLUDE" && Projection.NonKeyAttributes!.Contains(attribute, StringComparer.Ordinal));

    /// <summary>An item as the index holds it: the attributes it projects.</summary>
    public Dictionary<string, DynamoValue> Entry(DynamoKeySchema tableKey, IReadOnlyDictionary<string, DynamoValue> item)
    {
        var entry = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
        foreach (var attribute in item)
        {
            if (Projects(tableKey, attribute.Key))
            {
                entry[attribute.Key] = attribute.Value;
            }
        }

        return entry;
    }

    /// <summary>The item's vector, when it holds one this index can take (a backfill skips any other).</summary>
    public DynamoValue? IndexableVector(IReadOnlyDictionary<string, DynamoValue> item) =>
        item.TryGetValue(VectorAttribute, out var value) && IsShaped(value, Dimensions) && value.L.All(e => FitsFloat(e.N)) ? value : null;

    /// <summary>
    /// Whether the item holds a vector of this index's shape, without reading its numbers: every
    /// write's vector has passed <see cref="Refusal"/>, so a stored or validated item's list of
    /// <see cref="Dimensions"/> numbers is one.
    /// </summary>
    public bool HoldsVector(IReadOnlyDictionary<string, DynamoValue> item) =>
        item.TryGetValue(VectorAttribute, out var value) && IsShaped(value, Dimensions);

    /// <summary>A list of <paramref name="dimensions"/> numbers, its numbers unread.</summary>
    public static bool IsShaped(DynamoValue value, int dimensions)
    {
        if (value.Type != DynamoType.L || value.L.Count != dimensions)
        {
            return false;
        }

        foreach (var element in value.L)
        {
            if (element.Type != DynamoType.N)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A list of <paramref name="dimensions"/> numbers, each a finite 32-bit float, as floats; null for anything else.</summary>
    public static float[]? Read(DynamoValue value, int dimensions)
    {
        if (value.Type != DynamoType.L || value.L.Count != dimensions)
        {
            return null;
        }

        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            if (value.L[i].Type != DynamoType.N || !TryFloat(value.L[i].N, out vector[i]))
            {
                return null;
            }
        }

        return vector;
    }

    /// <summary>
    /// Whether a number DynamoDB accepted is a finite 32-bit float, reading it only when it might
    /// not be: one with an exponent, or more than 38 digits before its point.
    /// </summary>
    public static bool FitsFloat(string number)
    {
        var integerDigits = 0;
        foreach (var c in number)
        {
            if (c is 'e' or 'E')
            {
                return TryFloat(number, out _);
            }

            if (c == '.')
            {
                break;
            }

            if (c is >= '1' and <= '9' || (c == '0' && integerDigits > 0))
            {
                integerDigits++;
            }
        }

        return integerDigits <= 38 || TryFloat(number, out _);
    }

    /// <summary>A number's 32-bit float, refused when it overflows one.</summary>
    public static bool TryFloat(string number, out float value)
    {
        value = 0;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var wide))
        {
            return false;
        }

        value = (float)wide;
        return !float.IsInfinity(value) && !float.IsNaN(value);
    }

    /// <summary>
    /// Why a write's item can't go in this index (its vector attribute is not a list of
    /// <see cref="Dimensions"/> 32-bit floats), as DynamoDB words it; null when it can.
    /// </summary>
    public string? Refusal(IReadOnlyDictionary<string, DynamoValue> item)
    {
        if (!item.TryGetValue(VectorAttribute, out var value))
        {
            return null;
        }

        const string Invalid = "One or more parameter values were invalid. ";
        if (value.Type != DynamoType.L)
        {
            return $"{Invalid}Invalid type for parameter {VectorAttribute}, Expected: 32-bit floating point number list IndexName: {Name}";
        }

        if (value.L.Count != Dimensions)
        {
            return $"{Invalid}Invalid size for parameter {VectorAttribute}, Expected: {Dimensions.ToString(CultureInfo.InvariantCulture)}, Actual: {value.L.Count.ToString(CultureInfo.InvariantCulture)} IndexName: {Name}";
        }

        for (var i = 0; i < value.L.Count; i++)
        {
            var element = value.L[i];
            if (element.Type != DynamoType.N || !FitsFloat(element.N))
            {
                return $"{Invalid}Invalid type for parameter {VectorAttribute}[{i.ToString(CultureInfo.InvariantCulture)}], Expected: 32-bit floating point number, Actual: {element.Type}. IndexName: {Name}";
            }
        }

        return null;
    }

    /// <summary>Indexes an item's vector; <paramref name="entryBytes"/> is the size of its entry.</summary>
    public void Add(DynamoKey tableKey, DynamoValue vector, long entryBytes)
    {
        _entries[tableKey] = (vector, null, entryBytes);
        SizeBytes += entryBytes;
    }

    public void Remove(DynamoKey tableKey)
    {
        if (_entries.Remove(tableKey, out var entry))
        {
            SizeBytes -= entry.Size;
        }
    }

    public void Clear()
    {
        _entries.Clear();
        SizeBytes = 0;
    }

    /// <summary>Whether a lower score is a closer match: distances (<c>COSINE</c>, <c>EUCLIDEAN</c>) yes, <c>DOT_PRODUCT</c> no.</summary>
    public bool LowerIsCloser => DistanceFunction != "DOT_PRODUCT";

    /// <summary>
    /// How <paramref name="vector"/> scores against <paramref name="query"/>, in 32-bit float
    /// arithmetic as DynamoDB computes it: the cosine distance (1 - cosine similarity), the
    /// Euclidean distance, or the dot product.
    /// </summary>
    public float Score(float[] query, float[] vector)
    {
        switch (DistanceFunction)
        {
            case "DOT_PRODUCT":
            {
                var dot = 0f;
                for (var i = 0; i < query.Length; i++)
                {
                    dot += query[i] * vector[i];
                }

                return dot;
            }

            case "EUCLIDEAN":
            {
                var sum = 0f;
                for (var i = 0; i < query.Length; i++)
                {
                    var difference = query[i] - vector[i];
                    sum += difference * difference;
                }

                return (float)Math.Sqrt(sum);
            }

            default:
            {
                float dot = 0f, queryNorm = 0f, vectorNorm = 0f;
                for (var i = 0; i < query.Length; i++)
                {
                    dot += query[i] * vector[i];
                    queryNorm += query[i] * query[i];
                    vectorNorm += vector[i] * vector[i];
                }

                // A zero vector has no direction: it is as far from every vector as an orthogonal one.
                if (queryNorm == 0f || vectorNorm == 0f)
                {
                    return 1f;
                }

                return 1f - (dot / ((float)Math.Sqrt(queryNorm) * (float)Math.Sqrt(vectorNorm)));
            }
        }
    }
}
