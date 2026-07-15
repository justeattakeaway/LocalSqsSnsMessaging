using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Implements EventBridge content-based event pattern matching.
/// See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-event-patterns.html
/// </summary>
internal static class EventBridgeEventPattern
{
    /// <summary>Validates that a pattern string is a well-formed event pattern (a JSON object).</summary>
    public static bool IsValid(string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Event pattern is empty.";
            return false;
        }

        try
        {
            if (JsonNode.Parse(pattern!) is not JsonObject)
            {
                error = "Event pattern must be a JSON object.";
                return false;
            }
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>Returns true if the given event matches the pattern.</summary>
    public static bool Matches(string? patternJson, JsonNode? @event)
    {
        // A rule with no event pattern (e.g. a scheduled rule) never matches PutEvents traffic.
        if (string.IsNullOrWhiteSpace(patternJson))
        {
            return false;
        }

        JsonNode? pattern;
        try
        {
            pattern = JsonNode.Parse(patternJson!);
        }
        catch (JsonException)
        {
            return false;
        }

        return pattern is JsonObject obj && MatchObject(obj, @event as JsonObject);
    }

    private static bool MatchObject(JsonObject pattern, JsonObject? eventObj)
    {
        foreach (var (key, value) in pattern)
        {
            if (string.Equals(key, "$or", StringComparison.Ordinal))
            {
                if (value is not JsonArray orArr || !orArr.Any(sub => sub is JsonObject o && MatchObject(o, eventObj)))
                {
                    return false;
                }
                continue;
            }

            var present = eventObj is not null && eventObj.TryGetPropertyValue(key, out _);
            JsonNode? fieldValue = null;
            eventObj?.TryGetPropertyValue(key, out fieldValue);

            switch (value)
            {
                case JsonArray candidates:
                    if (!candidates.Any(c => MatchCandidate(c, fieldValue, present)))
                    {
                        return false;
                    }
                    break;
                case JsonObject nested:
                    if (!MatchNested(nested, fieldValue))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool MatchNested(JsonObject nested, JsonNode? fieldValue)
    {
        return fieldValue switch
        {
            JsonObject o => MatchObject(nested, o),
            JsonArray arr => arr.Any(el => el is JsonObject eo && MatchObject(nested, eo)),
            _ => false
        };
    }

    private static bool MatchCandidate(JsonNode? candidate, JsonNode? fieldValue, bool present)
    {
        return candidate is JsonObject filter
            ? MatchContentFilter(filter, fieldValue, present)
            : MatchLiteral(candidate, fieldValue, present);
    }

    private static bool MatchLiteral(JsonNode? candidate, JsonNode? fieldValue, bool present)
    {
        if (!present)
        {
            return false;
        }
        return fieldValue is JsonArray arr ? arr.Any(el => JsonEquals(candidate, el)) : JsonEquals(candidate, fieldValue);
    }

    private static bool MatchContentFilter(JsonObject filter, JsonNode? fieldValue, bool present)
    {
        if (filter.Count != 1)
        {
            return false;
        }

        var (op, val) = (filter.First().Key, filter.First().Value);

        switch (op)
        {
            case "exists":
                return TryGetBool(val, out var b) && (b ? present : !present);

            case "prefix":
                return present && MatchAffix(val, fieldValue, isPrefix: true);

            case "suffix":
                return present && MatchAffix(val, fieldValue, isPrefix: false);

            case "equals-ignore-case":
                return present && TryGetString(val, out var eq) &&
                       AnyString(fieldValue, s => string.Equals(s, eq, StringComparison.OrdinalIgnoreCase));

            case "wildcard":
                return present && TryGetString(val, out var wc) && AnyString(fieldValue, s => WildcardMatch(s, wc));

            case "cidr":
                return present && TryGetString(val, out var cidr) && AnyString(fieldValue, s => CidrMatch(s, cidr));

            case "numeric":
                return present && val is JsonArray numeric && MatchNumeric(numeric, fieldValue);

            case "anything-but":
                return MatchAnythingBut(val, fieldValue, present);

            default:
                return false;
        }
    }

    private static bool MatchAffix(JsonNode? val, JsonNode? fieldValue, bool isPrefix)
    {
        // Simple string form, or {"equals-ignore-case": "..."} form.
        if (TryGetString(val, out var s))
        {
            return AnyString(fieldValue, v => isPrefix
                ? v.StartsWith(s, StringComparison.Ordinal)
                : v.EndsWith(s, StringComparison.Ordinal));
        }

        if (val is JsonObject o && o.Count == 1 && o.TryGetPropertyValue("equals-ignore-case", out var ic) &&
            TryGetString(ic, out var ci))
        {
            return AnyString(fieldValue, v => isPrefix
                ? v.StartsWith(ci, StringComparison.OrdinalIgnoreCase)
                : v.EndsWith(ci, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool MatchNumeric(JsonArray spec, JsonNode? fieldValue)
    {
        // spec is a flat list: [op, number, op, number, ...]
        var comparisons = new List<(string Op, double Value)>();
        for (var i = 0; i + 1 < spec.Count; i += 2)
        {
            if (!TryGetString(spec[i], out var op) || !TryGetDouble(spec[i + 1], out var num))
            {
                return false;
            }
            comparisons.Add((op, num));
        }

        return comparisons.Count > 0 && AnyNumber(fieldValue, d => comparisons.All(c => Compare(d, c.Op, c.Value)));
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

    private static bool MatchAnythingBut(JsonNode? val, JsonNode? fieldValue, bool present)
    {
        if (!present)
        {
            return false;
        }

        switch (val)
        {
            case JsonObject o when o.Count == 1:
                var (op, inner) = (o.First().Key, o.First().Value);
                if (!TryGetString(inner, out var s))
                {
                    // {"anything-but": {"prefix": ["a", "b"]}} style with an array
                    if (inner is JsonArray arr && op is "prefix" or "suffix" or "wildcard" or "equals-ignore-case")
                    {
                        return !arr.Any(c => TryGetString(c, out var cs) && AnyString(fieldValue, v => AffixOrWildcard(op, v, cs)));
                    }
                    return false;
                }
                return !AnyString(fieldValue, v => AffixOrWildcard(op, v, s));

            case JsonArray arr:
                return !arr.Any(c => MatchLiteral(c, fieldValue, present));

            default:
                return !MatchLiteral(val, fieldValue, present);
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

    private static bool AnyString(JsonNode? field, Func<string, bool> predicate)
    {
        if (field is JsonArray arr)
        {
            return arr.Any(el => AnyString(el, predicate));
        }
        return TryGetString(field, out var s) && predicate(s);
    }

    private static bool AnyNumber(JsonNode? field, Func<double, bool> predicate)
    {
        if (field is JsonArray arr)
        {
            return arr.Any(el => AnyNumber(el, predicate));
        }
        return TryGetDouble(field, out var d) && predicate(d);
    }

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
        // EventBridge wildcards support '*' only.
        int t = 0, p = 0, starIdx = -1, matchIdx = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = t;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == text[t])
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

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    private static bool CidrMatch(string ip, string cidr)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 ||
            !IPAddress.TryParse(cidr.AsSpan(0, slash), out var network) ||
            !int.TryParse(cidr.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var prefixLength) ||
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
}
