using Microsoft.Extensions.Primitives;

internal static class DictionaryExtensions
{
    public static Dictionary<string, TValue> ToFlatDictionary<TValue>(
        this Dictionary<string, StringValues> source,
        string prefix,
        Func<StringValues, TValue> mapper)
    {
        var result = new Dictionary<string, TValue>();
        var prefixWithDot = prefix + ".entry.";
        var prefixLength = prefixWithDot.Length;

        // First pass - find and add all keys
        foreach (var kvp in source)
        {
            var key = kvp.Key;
            if (!key.StartsWith(prefixWithDot, StringComparison.Ordinal))
                continue;

            var afterPrefix = key.AsSpan(prefixLength);
            var firstDot = afterPrefix.IndexOf('.');
            if (firstDot == -1) continue;

            var typeSpan = afterPrefix[(firstDot + 1)..];
            if (!typeSpan.Equals("key", StringComparison.Ordinal))
                continue;

            // We found a key entry, look for its matching value
            var indexSpan = afterPrefix[..firstDot];
            var valueKey = string.Concat(prefixWithDot, indexSpan.ToString(), ".value");
        
            if (source.TryGetValue(valueKey, out var valueEntry))
            {
                result[kvp.Value.ToString()] = mapper(valueEntry);
            }
        }

        return result;
    }
}