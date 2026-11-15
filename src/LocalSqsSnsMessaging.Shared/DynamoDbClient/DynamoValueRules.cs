namespace LocalSqsSnsMessaging.DynamoDb;

/// <summary>
/// DynamoDB's rules for the values a request carries, which its model can't express: a number has
/// at most 38 significant digits within the documented exponent range, a set is neither empty nor
/// repeats a member, a <c>NULL</c> states <see langword="true"/>, an item nests at most 32 levels
/// and is at most 400 KB. Canonicalizing a value applies the rules and makes the form DynamoDB
/// stores (each number in its one canonical text).
/// </summary>
/// <remarks>
/// DynamoDB states some refusals inside the validation envelope (a value an item or key member
/// carried) and some bare (a value an expression computed, or a statement spelled), so each check
/// takes <c>enveloped</c>.
/// </remarks>
internal static class DynamoValueRules
{
    public const int MaxItemBytes = 400 * 1024;
    public const int MaxDepth = 32;
    public const string ItemSizeExceeded = "Item size has exceeded the maximum allowed size";
    public const string UpdateItemSizeExceeded = "Item size to update has exceeded the maximum allowed size";
    public const string NestingExceeded =
        "Nesting Levels have exceeded supported limits: Attributes in the item have nested levels beyond supported limit";

    private static DynamoDbException Refusal(string clause, bool enveloped) =>
        enveloped ? DynamoDbErrors.ValidationClause(clause) : DynamoDbErrors.Validation(clause);

    /// <summary>The refusal a number's text earns, naming the rule it breaks.</summary>
    public static DynamoDbException NumberRefusal(string text, DynamoNumberFault fault, bool enveloped) => fault switch
    {
        DynamoNumberFault.Precision => Refusal($"Attempting to store more than {DynamoNumber.MaxSignificantDigits} significant digits in a Number", enveloped),
        DynamoNumberFault.Overflow => Refusal("Number overflow. Attempting to store a number with magnitude larger than supported range", enveloped),
        DynamoNumberFault.Underflow => Refusal("Number underflow. Attempting to store a number with magnitude smaller than supported range", enveloped),
        // A bare refusal of empty text names no value at all, not an empty one after a colon.
        _ => Refusal($"The parameter cannot be converted to a numeric value: {Clipped(text)}", enveloped),
    };

    private static string Clipped(string text) => text.Length <= 48 ? text : text[..48] + "…";

    /// <summary>The number <paramref name="text"/> denotes, or the refusal it earns.</summary>
    public static DynamoNumber ParseNumber(string text, bool enveloped)
    {
        var fault = DynamoNumber.Parse(text, out var number);
        return fault == DynamoNumberFault.None ? number : throw NumberRefusal(text, fault, enveloped);
    }

    /// <summary>
    /// The stored form of <paramref name="value"/>: every number, at any depth, in its canonical
    /// text, with every set rule and the <c>NULL</c> rule checked.
    /// </summary>
    public static DynamoValue Canonicalize(DynamoValue value, bool enveloped = true)
    {
        switch (value.Type)
        {
            case DynamoType.N:
                var number = ParseNumber(value.N, enveloped);
                var text = number.ToString();
                return string.Equals(text, value.N, StringComparison.Ordinal) ? value : DynamoValue.FromNumberText(text);
            case DynamoType.NS:
                CheckSetNotEmpty(value, enveloped);
                var members = value.NS.Select(member => ParseNumber(member, enveloped).ToString()).ToList();
                if (HasDuplicate(members))
                {
                    throw DuplicateMember(string.Empty, enveloped);
                }

                return DynamoValue.FromNumberSet(members);
            case DynamoType.SS:
            case DynamoType.BS:
                CheckSetRules(value, enveloped);
                return value;
            case DynamoType.L:
                List<DynamoValue>? list = null;
                for (var i = 0; i < value.L.Count; i++)
                {
                    var element = Canonicalize(value.L[i], enveloped);
                    if (list is null && !ReferenceEquals(element, value.L[i]))
                    {
                        list = new List<DynamoValue>(value.L.Take(i));
                    }

                    list?.Add(element);
                }

                return list is null ? value : DynamoValue.FromList(list);
            case DynamoType.M:
                Dictionary<string, DynamoValue>? map = null;
                foreach (var entry in value.M)
                {
                    var canonical = Canonicalize(entry.Value, enveloped);
                    if (map is null && !ReferenceEquals(canonical, entry.Value))
                    {
                        map = new Dictionary<string, DynamoValue>(StringComparer.Ordinal);
                        foreach (var earlier in value.M)
                        {
                            if (string.Equals(earlier.Key, entry.Key, StringComparison.Ordinal))
                            {
                                break;
                            }

                            map[earlier.Key] = earlier.Value;
                        }
                    }

                    if (map is not null)
                    {
                        map[entry.Key] = canonical;
                    }
                }

                return map is null ? value : DynamoValue.FromMap(map);
            case DynamoType.NULL:
                if (!value.NullStated)
                {
                    throw Refusal("One or more parameter values were invalid: Null attribute value types must have the value of true", enveloped);
                }

                return value;
            default:
                return value;
        }
    }

    /// <summary>Canonicalizes every attribute of an item (or key), keeping attribute order.</summary>
    public static Dictionary<string, DynamoValue> CanonicalizeItem(IReadOnlyDictionary<string, DynamoValue> item, bool enveloped = true)
    {
        var result = new Dictionary<string, DynamoValue>(item.Count, StringComparer.Ordinal);
        foreach (var entry in item)
        {
            result[entry.Key] = Canonicalize(entry.Value, enveloped);
        }

        return result;
    }

    /// <summary>The set rules alone, at every depth, without canonicalizing.</summary>
    public static void CheckSetRules(DynamoValue value, bool enveloped)
    {
        switch (value.Type)
        {
            case DynamoType.SS:
                CheckSetNotEmpty(value, enveloped);
                if (HasDuplicate(value.SS))
                {
                    throw DuplicateMember("[" + string.Join(", ", value.SS.Take(8).Select(s => $"\"{Clipped(s)}\"")) + "] ", enveloped);
                }

                break;
            case DynamoType.NS:
                CheckSetNotEmpty(value, enveloped);
                var numbers = value.NS.Select(n => DynamoNumber.TryParse(n, out var number) ? number.ToString() : null).ToList();
                if (numbers.TrueForAll(n => n is not null) && HasDuplicate(numbers!))
                {
                    throw DuplicateMember(string.Empty, enveloped);
                }

                break;
            case DynamoType.BS:
            {
                CheckSetNotEmpty(value, enveloped);

                // DynamoDB names the distinct members it read before the duplicate in byte
                // order, then the duplicate.
                var seen = new SortedSet<byte[]>(Comparer<byte[]>.Create(DynamoKeyOrder.CompareBytes));
                foreach (var member in value.BS)
                {
                    if (!seen.Add(member))
                    {
                        var named = seen.Append(member).Select(Convert.ToBase64String);
                        throw DuplicateMember("[" + string.Join(",", named.Take(8).Select(s => $"\"{Clipped(s)}\"")) + "]of type BS ", enveloped);
                    }
                }

                break;
            }
            case DynamoType.L:
                foreach (var element in value.L)
                {
                    CheckSetRules(element, enveloped);
                }

                break;
            case DynamoType.M:
                foreach (var element in value.M.Values)
                {
                    CheckSetRules(element, enveloped);
                }

                break;
        }
    }

    private static void CheckSetNotEmpty(DynamoValue set, bool enveloped)
    {
        if (set.SetCount > 0)
        {
            return;
        }

        var clause = set.Type switch
        {
            DynamoType.SS => "An string set  may not be empty",
            DynamoType.NS => "An number set  may not be empty",
            _ => "Binary sets should not be empty",
        };
        throw Refusal("One or more parameter values were invalid: " + clause, enveloped);
    }

    private static DynamoDbException DuplicateMember(string collection, bool enveloped) =>
        Refusal($"One or more parameter values were invalid: Input collection {collection}contains duplicates.", enveloped);

    private static bool HasDuplicate(IReadOnlyList<string> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!seen.Add(member))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The item's size: each attribute's name (UTF-8) plus its value.</summary>
    public static long ItemSize(IReadOnlyDictionary<string, DynamoValue> item)
    {
        var size = 0L;
        foreach (var entry in item)
        {
            size += Encoding.UTF8.GetByteCount(entry.Key) + entry.Value.Size();
        }

        return size;
    }

    /// <summary>Whether any attribute nests deeper than DynamoDB's 32 levels.</summary>
    public static bool ExceedsDepth(IReadOnlyDictionary<string, DynamoValue> item)
    {
        foreach (var value in item.Values)
        {
            if (value.Depth() > MaxDepth)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Refuses an item that nests too deeply or is over 400 KB.</summary>
    public static void CheckItemLimits(IReadOnlyDictionary<string, DynamoValue> item, string sizeMessage = ItemSizeExceeded)
    {
        if (ExceedsDepth(item))
        {
            throw DynamoDbErrors.ValidationClause(NestingExceeded);
        }

        if (ItemSize(item) > MaxItemBytes)
        {
            throw DynamoDbErrors.Validation(sizeMessage);
        }
    }
}
