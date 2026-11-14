using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Content-based JSON pattern matching shared by EventBridge event patterns and SNS filter
/// policies. Both services use the same engine (AWS Event Ruler): a pattern is a JSON object whose
/// leaf values are arrays of candidates (literals or operator objects such as <c>prefix</c>,
/// <c>anything-but</c>, <c>numeric</c>, <c>exists</c>), and a document matches when every
/// key in the pattern matches at least one candidate.
/// See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-event-patterns.html and
/// https://docs.aws.amazon.com/sns/latest/dg/sns-subscription-filter-policies.html
/// </summary>
/// <remarks>
/// As Event Ruler does, the document is matched as its flattened leaves: each scalar (or
/// <c>null</c>) is a value at its dotted path, so <c>{"a":{"b":1}}</c> and <c>{"a.b":1}</c> both
/// have the value 1 at <c>a.b</c>, and an array contributes each of its elements (nested arrays
/// too) at the array's own path. Objects and empty arrays contribute no value, so <c>exists</c>
/// treats them as absent. A key matches when some value at its path satisfies a candidate, except
/// <c>exists: false</c>, which matches when the path has no value at all. Values a rule matches in
/// several keys must come from the same element of any array they share.
/// </remarks>
internal static class JsonPatternMatcher
{
    public static bool Matches(JsonObject pattern, JsonObject? document)
    {
        var fields = new Dictionary<string, List<FieldValue>>(StringComparer.Ordinal);
        if (document is not null)
        {
            Flatten(document, path: null, [], fields);
        }

        return ExpandRules(pattern, path: null).Any(rule => MatchesRule(rule, fields));
    }

    /// <summary>A value of the document, with the array elements it lies within.</summary>
    private sealed record FieldValue(JsonNode? Value, (JsonArray Array, int Index)[] Elements);

    /// <summary>One key of a rule: the dotted path it names and the candidates its value may match.</summary>
    private sealed record KeyCondition(string Path, JsonArray Candidates);

    private static void Flatten(JsonNode? node, string? path, List<(JsonArray, int)> elements, Dictionary<string, List<FieldValue>> fields)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    Flatten(child, Join(path, key), elements, fields);
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    elements.Add((array, i));
                    Flatten(array[i], path, elements, fields);
                    elements.RemoveAt(elements.Count - 1);
                }
                break;

            default:
                if (path is null)
                {
                    break;
                }

                if (!fields.TryGetValue(path, out var values))
                {
                    fields[path] = values = [];
                }
                values.Add(new FieldValue(node, [.. elements]));
                break;
        }
    }

    private static string Join(string? path, string key) => path is null ? key : $"{path}.{key}";

    /// <summary>
    /// Expands <paramref name="pattern"/> into the rules it stands for, one per combination of its
    /// <c>$or</c> branches; a document matches the pattern when it matches any of them.
    /// </summary>
    private static List<List<KeyCondition>> ExpandRules(JsonObject pattern, string? path)
    {
        List<List<KeyCondition>> rules = [[]];
        foreach (var (key, value) in pattern)
        {
            List<List<KeyCondition>> alternatives;
            if (string.Equals(key, "$or", StringComparison.Ordinal) && value is JsonArray branches)
            {
                alternatives = branches.OfType<JsonObject>().SelectMany(branch => ExpandRules(branch, path)).ToList();
            }
            else
            {
                var keyPath = Join(path, key);
                alternatives = value switch
                {
                    JsonArray candidates => [[new KeyCondition(keyPath, candidates)]],
                    JsonObject nested => ExpandRules(nested, keyPath),
                    _ => [] // Not a valid pattern value, so nothing matches.
                };
            }

            rules = rules.SelectMany(rule => alternatives.Select(alternative => rule.Concat(alternative).ToList())).ToList();
        }

        return rules;
    }

    private static bool MatchesRule(List<KeyCondition> rule, Dictionary<string, List<FieldValue>> fields)
    {
        // For each key that needs a value, the document's values that satisfy it.
        var choices = new List<List<FieldValue>>();
        foreach (var condition in rule)
        {
            if (!fields.TryGetValue(condition.Path, out var values))
            {
                if (condition.Candidates.Any(IsExistsFalse))
                {
                    continue;
                }
                return false;
            }

            var matching = values.Where(v => condition.Candidates.Any(c => MatchCandidate(c, v.Value))).ToList();
            if (matching.Count == 0)
            {
                return false;
            }
            choices.Add(matching);
        }

        return choices.Count < 2 || ChooseConsistent(DistinctPlacements(choices), 0, []);
    }

    /// <summary>
    /// Each key's matching values reduced to their distinct placements in the arrays that another
    /// key's values also lie within.
    /// </summary>
    /// <remarks>
    /// Only an array two keys share can make their choices inconsistent, so two values of one key
    /// placed alike in every shared array are interchangeable. Keeping one of them stops the search
    /// from trying every combination of the matches of keys that never constrain one another (three
    /// keys matching every element of their own long arrays, say) before a later key fails.
    /// </remarks>
    private static List<List<FieldValue>> DistinctPlacements(List<List<FieldValue>> choices)
    {
        // How many keys have a value within each array. JsonNode compares by reference.
        var keysPerArray = new Dictionary<JsonArray, int>();
        foreach (var values in choices)
        {
            foreach (var array in values.SelectMany(v => v.Elements).Select(e => e.Array).Distinct())
            {
                keysPerArray[array] = keysPerArray.TryGetValue(array, out var count) ? count + 1 : 1;
            }
        }

        var shared = new Dictionary<JsonArray, int>();
        foreach (var (array, keys) in keysPerArray)
        {
            if (keys > 1)
            {
                shared[array] = shared.Count;
            }
        }

        return choices
            .Select(values => values
                .Select(v => v with { Elements = [.. v.Elements.Where(e => shared.ContainsKey(e.Array))] })
                .GroupBy(v => string.Join(",", v.Elements.Select(e => $"{shared[e.Array]}:{e.Index}")), StringComparer.Ordinal)
                .Select(placement => placement.First())
                .ToList())
            .ToList();
    }

    /// <summary>
    /// Whether one value can be chosen per key, from <paramref name="choices"/> onwards, that lies in
    /// the same element as <paramref name="chosen"/> of every array they share.
    /// </summary>
    private static bool ChooseConsistent(List<List<FieldValue>> choices, int index, List<FieldValue> chosen)
    {
        if (index == choices.Count)
        {
            return true;
        }

        foreach (var candidate in choices[index])
        {
            if (!chosen.All(other => SameElements(candidate, other)))
            {
                continue;
            }

            chosen.Add(candidate);
            var found = ChooseConsistent(choices, index + 1, chosen);
            chosen.RemoveAt(chosen.Count - 1);
            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameElements(FieldValue a, FieldValue b)
    {
        foreach (var (array, index) in a.Elements)
        {
            foreach (var (otherArray, otherIndex) in b.Elements)
            {
                if (ReferenceEquals(array, otherArray) && index != otherIndex)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsExistsFalse(JsonNode? candidate) =>
        candidate is JsonObject { Count: 1 } filter &&
        filter.TryGetPropertyValue("exists", out var exists) &&
        TryGetBool(exists, out var b) && !b;

    /// <summary>Whether one value of the document satisfies <paramref name="candidate"/>.</summary>
    private static bool MatchCandidate(JsonNode? candidate, JsonNode? value)
    {
        if (candidate is not JsonObject filter)
        {
            return JsonEquals(candidate, value);
        }

        if (filter.Count != 1)
        {
            return false;
        }

        var (op, operand) = (filter.First().Key, filter.First().Value);
        return op switch
        {
            // A value is present; exists: false only matches a path without one.
            "exists" => TryGetBool(operand, out var b) && b,
            "prefix" => MatchAffix(operand, value, isPrefix: true),
            "suffix" => MatchAffix(operand, value, isPrefix: false),
            "equals-ignore-case" => TryGetString(operand, out var eq) && TryGetString(value, out var s) &&
                                    string.Equals(s, eq, StringComparison.OrdinalIgnoreCase),
            "wildcard" => TryGetString(operand, out var wc) && TryGetString(value, out var w) && WildcardMatch(w, wc),
            "cidr" => TryGetString(operand, out var cidr) && TryGetString(value, out var ip) && CidrMatch(ip, cidr),
            "numeric" => operand is JsonArray numeric && MatchNumeric(numeric, value),
            "anything-but" => !IsExcluded(operand, value),
            _ => false
        };
    }

    private static bool MatchAffix(JsonNode? operand, JsonNode? value, bool isPrefix)
    {
        if (!TryGetString(value, out var v))
        {
            return false;
        }

        // Simple string form, or {"equals-ignore-case": "..."} form.
        if (TryGetString(operand, out var s))
        {
            return isPrefix
                ? v.StartsWith(s, StringComparison.Ordinal)
                : v.EndsWith(s, StringComparison.Ordinal);
        }

        if (operand is JsonObject o && o.Count == 1 && o.TryGetPropertyValue("equals-ignore-case", out var ic) &&
            TryGetString(ic, out var ci))
        {
            return isPrefix
                ? v.StartsWith(ci, StringComparison.OrdinalIgnoreCase)
                : v.EndsWith(ci, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool MatchNumeric(JsonArray spec, JsonNode? value)
    {
        // spec is a flat list: [op, number, op, number, ...]
        if (!TryGetDouble(value, out var actual) || spec.Count < 2)
        {
            return false;
        }

        for (var i = 0; i + 1 < spec.Count; i += 2)
        {
            if (!TryGetString(spec[i], out var op) || !TryGetDouble(spec[i + 1], out var expected) ||
                !Compare(actual, op, expected))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Compare(double actual, string op, double expected) => op switch
    {
        "=" => actual == expected,
        "!=" => actual != expected,
        "<" => actual < expected,
        "<=" => actual <= expected,
        ">" => actual > expected,
        ">=" => actual >= expected,
        _ => false
    };

    /// <summary>Whether an <c>anything-but</c> with <paramref name="operand"/> excludes <paramref name="value"/>.</summary>
    private static bool IsExcluded(JsonNode? operand, JsonNode? value)
    {
        switch (operand)
        {
            // {"anything-but": {"prefix": "a"}} or {"anything-but": {"prefix": ["a", "b"]}}, and the
            // suffix, wildcard and equals-ignore-case forms: they only exclude strings.
            case JsonObject o when o.Count == 1:
                var (op, inner) = (o.First().Key, o.First().Value);
                if (!TryGetString(value, out var v))
                {
                    return false;
                }

                return inner is JsonArray operands
                    ? operands.Any(c => TryGetString(c, out var cs) && AffixOrWildcard(op, v, cs))
                    : TryGetString(inner, out var s) && AffixOrWildcard(op, v, s);

            case JsonArray excluded:
                return excluded.Any(c => JsonEquals(c, value));

            default:
                return JsonEquals(operand, value);
        }
    }

    private static bool AffixOrWildcard(string op, string value, string operand) => op switch
    {
        "prefix" => value.StartsWith(operand, StringComparison.Ordinal),
        "suffix" => value.EndsWith(operand, StringComparison.Ordinal),
        "equals-ignore-case" => string.Equals(value, operand, StringComparison.OrdinalIgnoreCase),
        "wildcard" => WildcardMatch(value, operand),
        _ => false
    };

    // ---- value helpers ----

    private static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var ka = a.GetValueKind();
        var kb = b.GetValueKind();

        if (ka == JsonValueKind.String && kb == JsonValueKind.String)
        {
            return string.Equals(a.GetValue<string>(), b.GetValue<string>(), StringComparison.Ordinal);
        }
        if (ka == JsonValueKind.Number && kb == JsonValueKind.Number)
        {
            return TryGetDouble(a, out var da) && TryGetDouble(b, out var db) && da == db;
        }
        if (ka is JsonValueKind.True or JsonValueKind.False && kb is JsonValueKind.True or JsonValueKind.False)
        {
            return ka == kb;
        }
        return ka == JsonValueKind.Null && kb == JsonValueKind.Null;
    }

    private static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            value = v.GetValue<string>();
            return true;
        }
        return false;
    }

    private static bool TryGetDouble(JsonNode? node, out double value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out value);
    }

    private static bool TryGetBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            value = v.GetValue<bool>();
            return true;
        }
        return false;
    }

    private static bool WildcardMatch(string text, string pattern)
    {
        // '*' matches any run of characters; "\*" is a literal '*' and "\\" a literal '\'.
        var tokens = new List<(char Character, bool IsStar)>(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 1 < pattern.Length && pattern[i + 1] is '*' or '\\')
            {
                tokens.Add((pattern[++i], false));
            }
            else
            {
                tokens.Add((pattern[i], pattern[i] == '*'));
            }
        }

        int t = 0, p = 0, starIdx = -1, matchIdx = 0;
        while (t < text.Length)
        {
            if (p < tokens.Count && tokens[p].IsStar)
            {
                starIdx = p;
                matchIdx = t;
                p++;
            }
            else if (p < tokens.Count && tokens[p].Character == text[t])
            {
                p++;
                t++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                t = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < tokens.Count && tokens[p].IsStar)
        {
            p++;
        }
        return p == tokens.Count;
    }

    private static bool CidrMatch(string ip, string cidr)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 ||
            !IPAddress.TryParse(cidr.AsSpan(0, slash), out var network) ||
            !int.TryParse(cidr.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefixLength) ||
            !IsStandardAddress(ip) ||
            !IPAddress.TryParse(ip, out var address) ||
            address!.AddressFamily != network!.AddressFamily)
        {
            return false;
        }

        var addrBytes = address.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > addrBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (addrBytes[i] != netBytes[i])
            {
                return false;
            }
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addrBytes[fullBytes] & mask) == (netBytes[fullBytes] & mask);
    }

    /// <summary>
    /// Whether <paramref name="ip"/> is written as a full dotted-quad IPv4 address or a colon-separated
    /// IPv6 address. <see cref="IPAddress.TryParse(string, out IPAddress)"/> also accepts shorthand
    /// such as <c>10.0.0</c> or a bare integer, which an event's value never matches as an address.
    /// </summary>
    private static bool IsStandardAddress(string ip)
    {
        if (ip.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        var octets = ip.Split('.');
        return octets.Length == 4 && octets.All(o => o.Length is > 0 and <= 3 && o.All(c => c is >= '0' and <= '9'));
    }
}
