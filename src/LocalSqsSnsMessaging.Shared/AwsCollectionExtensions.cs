using System.ComponentModel;

namespace LocalSqsSnsMessaging;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class AwsCollectionExtensions
{
    extension<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>>? source) where TKey : notnull
    {
        public Dictionary<TKey, TValue>? ToInitializedDictionary()
        {
            return source?.ToDictionary() ?? [];
        }
    }

    extension<TSource>(IEnumerable<TSource>? source)
    {
        public List<TSource>? ToInitializedList()
        {
            return source?.ToList() ?? [];
        }
    }
}
