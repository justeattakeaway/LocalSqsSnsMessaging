namespace InMemoryAws.DynamoDb;

/// <summary>The data types of a DynamoDB attribute value, named as the wire names them.</summary>
internal enum DynamoType : byte
{
    S,
    N,
    B,
    SS,
    NS,
    BS,
    M,
    L,
    NULL,
    BOOL,
}

/// <summary>
/// One DynamoDB attribute value, immutable. Requests are read straight into these (see
/// <see cref="DynamoValueJson"/>), and the store keeps them, so a stored item can be shared
/// between reads without copying.
/// </summary>
/// <remarks>
/// A value read from a request is as the caller sent it: a number keeps its text, a set may be
/// empty or repeat a member. <see cref="DynamoValueRules"/> holds a value to DynamoDB's rules
/// where DynamoDB checks them, and makes the stored (canonical) form.
/// </remarks>
internal sealed class DynamoValue : IEquatable<DynamoValue>
{
    private readonly string? _text;
    private readonly byte[]? _binary;
    private readonly IReadOnlyList<string>? _texts;
    private readonly IReadOnlyList<byte[]>? _binaries;
    private readonly IReadOnlyDictionary<string, DynamoValue>? _map;
    private readonly IReadOnlyList<DynamoValue>? _list;
    private readonly bool _flag;

    private DynamoValue(
        DynamoType type,
        string? text = null,
        byte[]? binary = null,
        IReadOnlyList<string>? texts = null,
        IReadOnlyList<byte[]>? binaries = null,
        IReadOnlyDictionary<string, DynamoValue>? map = null,
        IReadOnlyList<DynamoValue>? list = null,
        bool flag = false)
    {
        Type = type;
        _text = text;
        _binary = binary;
        _texts = texts;
        _binaries = binaries;
        _map = map;
        _list = list;
        _flag = flag;
    }

    public static DynamoValue NullValue { get; } = new(DynamoType.NULL, flag: true);

    public static DynamoValue True { get; } = new(DynamoType.BOOL, flag: true);

    public static DynamoValue False { get; } = new(DynamoType.BOOL, flag: false);

    public DynamoType Type { get; }

    public static DynamoValue FromString(string value) => new(DynamoType.S, text: value);

    /// <summary>A number from its text, which is kept as sent until the value is canonicalized.</summary>
    public static DynamoValue FromNumberText(string text) => new(DynamoType.N, text: text);

    public static DynamoValue FromNumber(DynamoNumber number) => new(DynamoType.N, text: number.ToString());

    public static DynamoValue FromNumber(long number) => new(DynamoType.N, text: number.ToString(CultureInfo.InvariantCulture));

    public static DynamoValue FromBinary(byte[] value) => new(DynamoType.B, binary: value);

    public static DynamoValue FromStringSet(IReadOnlyList<string> values) => new(DynamoType.SS, texts: values);

    public static DynamoValue FromNumberSet(IReadOnlyList<string> texts) => new(DynamoType.NS, texts: texts);

    public static DynamoValue FromBinarySet(IReadOnlyList<byte[]> values) => new(DynamoType.BS, binaries: values);

    public static DynamoValue FromMap(IReadOnlyDictionary<string, DynamoValue> entries) => new(DynamoType.M, map: entries);

    public static DynamoValue FromList(IReadOnlyList<DynamoValue> values) => new(DynamoType.L, list: values);

    public static DynamoValue FromBoolean(bool value) => value ? True : False;

    /// <summary>A <c>NULL</c> stating <paramref name="stated"/>; DynamoDB refuses one that states <see langword="false"/>.</summary>
    public static DynamoValue FromNull(bool stated) => stated ? NullValue : new DynamoValue(DynamoType.NULL, flag: false);

    public string S => Type == DynamoType.S ? _text! : throw WrongType(DynamoType.S);

    /// <summary>The number's text: canonical once the value has been canonicalized.</summary>
    public string N => Type == DynamoType.N ? _text! : throw WrongType(DynamoType.N);

    public byte[] B => Type == DynamoType.B ? _binary! : throw WrongType(DynamoType.B);

    public IReadOnlyList<string> SS => Type == DynamoType.SS ? _texts! : throw WrongType(DynamoType.SS);

    public IReadOnlyList<string> NS => Type == DynamoType.NS ? _texts! : throw WrongType(DynamoType.NS);

    public IReadOnlyList<byte[]> BS => Type == DynamoType.BS ? _binaries! : throw WrongType(DynamoType.BS);

    public IReadOnlyDictionary<string, DynamoValue> M => Type == DynamoType.M ? _map! : throw WrongType(DynamoType.M);

    public IReadOnlyList<DynamoValue> L => Type == DynamoType.L ? _list! : throw WrongType(DynamoType.L);

    public bool Bool => Type == DynamoType.BOOL ? _flag : throw WrongType(DynamoType.BOOL);

    /// <summary>What a <c>NULL</c> states: <see langword="true"/> for every valid one.</summary>
    public bool NullStated => Type == DynamoType.NULL ? _flag : throw WrongType(DynamoType.NULL);

    /// <summary>The number this <c>N</c> holds; the value must have been canonicalized.</summary>
    public DynamoNumber Number =>
        DynamoNumber.TryParse(N, out var number) ? number : throw new InvalidOperationException($"'{N}' is not a canonical number.");

    public bool IsSet => Type is DynamoType.SS or DynamoType.NS or DynamoType.BS;

    public bool IsScalarKeyType => Type is DynamoType.S or DynamoType.N or DynamoType.B;

    /// <summary>The number of members of a set.</summary>
    public int SetCount => Type switch
    {
        DynamoType.SS or DynamoType.NS => _texts!.Count,
        DynamoType.BS => _binaries!.Count,
        _ => throw WrongType(DynamoType.SS),
    };

    private InvalidOperationException WrongType(DynamoType wanted) => new($"A {Type} value is not a {wanted}.");

    /// <summary>
    /// The value's size as DynamoDB counts it toward the 400 KB item limit and capacity units:
    /// strings and binaries by their bytes, a number by its significant digits, one byte for a
    /// boolean or null, three bytes plus one per element (and per name) for a list or map.
    /// </summary>
    public long Size()
    {
        switch (Type)
        {
            case DynamoType.S:
                return Encoding.UTF8.GetByteCount(_text!);
            case DynamoType.N:
                return NumberSize(_text!);
            case DynamoType.B:
                return _binary!.Length;
            case DynamoType.SS:
                return _texts!.Sum(t => (long)Encoding.UTF8.GetByteCount(t));
            case DynamoType.NS:
                return _texts!.Sum(NumberSize);
            case DynamoType.BS:
                return _binaries!.Sum(b => (long)b.Length);
            case DynamoType.BOOL:
            case DynamoType.NULL:
                return 1;
            case DynamoType.L:
                var listSize = 3L + _list!.Count;
                foreach (var element in _list)
                {
                    listSize += element.Size();
                }

                return listSize;
            case DynamoType.M:
                var mapSize = 3L;
                foreach (var entry in _map!)
                {
                    mapSize += Encoding.UTF8.GetByteCount(entry.Key) + 1 + entry.Value.Size();
                }

                return mapSize;
            default:
                return 0;
        }
    }

    /// <summary>
    /// What a number costs: a byte, a byte per pair of significant digits (paired either side of
    /// the decimal point, as DynamoDB stores them in base 100), and a byte more when negative.
    /// </summary>
    private static long NumberSize(string text)
    {
        if (!DynamoNumber.TryParse(text, out var number))
        {
            return Encoding.UTF8.GetByteCount(text);
        }

        if (number.IsZero)
        {
            return 1;
        }

        // The digit worth 10^p falls in the pair worth 100^floor(p / 2).
        static int Pair(int power) => power >= 0 ? power / 2 : -((1 - power) / 2);
        var pairs = Pair(number.Point - 1) - Pair(number.Point - number.SignificantDigits) + 1;
        return 1 + pairs + (number.Negative ? 1 : 0);
    }

    /// <summary>How deeply the value nests: 1 for a scalar or set, one more per enclosing list or map.</summary>
    public int Depth()
    {
        var deepest = 0;
        if (Type == DynamoType.M)
        {
            foreach (var value in _map!.Values)
            {
                deepest = Math.Max(deepest, value.Depth());
            }
        }
        else if (Type == DynamoType.L)
        {
            foreach (var value in _list!)
            {
                deepest = Math.Max(deepest, value.Depth());
            }
        }

        return deepest + 1;
    }

    /// <summary>
    /// Whether two values are the same value, as DynamoDB compares them in a condition: numbers
    /// numerically, sets as sets (in any order), maps by their entries and lists element by element.
    /// </summary>
    public bool Equals(DynamoValue? other)
    {
        if (other is null || other.Type != Type)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        switch (Type)
        {
            case DynamoType.S:
                return string.Equals(_text, other._text, StringComparison.Ordinal);
            case DynamoType.N:
                return NumbersEqual(_text!, other._text!);
            case DynamoType.B:
                return _binary!.AsSpan().SequenceEqual(other._binary);
            case DynamoType.SS:
                return _texts!.Count == other._texts!.Count
                    && new HashSet<string>(_texts, StringComparer.Ordinal).SetEquals(other._texts);
            case DynamoType.NS:
                return _texts!.Count == other._texts!.Count
                    && new HashSet<string>(_texts.Select(CanonicalNumberText), StringComparer.Ordinal).SetEquals(other._texts.Select(CanonicalNumberText));
            case DynamoType.BS:
                return _binaries!.Count == other._binaries!.Count
                    && new HashSet<string>(_binaries.Select(Convert.ToBase64String), StringComparer.Ordinal).SetEquals(other._binaries.Select(Convert.ToBase64String));
            case DynamoType.M:
                if (_map!.Count != other._map!.Count)
                {
                    return false;
                }

                foreach (var entry in _map)
                {
                    if (!other._map.TryGetValue(entry.Key, out var otherValue) || !entry.Value.Equals(otherValue))
                    {
                        return false;
                    }
                }

                return true;
            case DynamoType.L:
                if (_list!.Count != other._list!.Count)
                {
                    return false;
                }

                for (var i = 0; i < _list.Count; i++)
                {
                    if (!_list[i].Equals(other._list[i]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return _flag == other._flag;
        }
    }

    private static bool NumbersEqual(string a, string b) =>
        DynamoNumber.TryParse(a, out var x) && DynamoNumber.TryParse(b, out var y)
            ? x == y
            : string.Equals(a, b, StringComparison.Ordinal);

    private static string CanonicalNumberText(string text) =>
        DynamoNumber.TryParse(text, out var number) ? number.ToString() : text;

    public override bool Equals(object? obj) => obj is DynamoValue other && Equals(other);

    public override int GetHashCode() => Type switch
    {
        DynamoType.S => StringComparer.Ordinal.GetHashCode(_text!),
        DynamoType.N => StringComparer.Ordinal.GetHashCode(CanonicalNumberText(_text!)),
        DynamoType.B => _binary!.Length == 0 ? 0 : _binary.Length * 31 + _binary[0],
        _ => (int)Type,
    };

    public override string ToString() => DynamoValueJson.ToJson(this);
}
