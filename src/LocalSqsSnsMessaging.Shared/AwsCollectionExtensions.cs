using System.ComponentModel;
using Amazon;

namespace LocalSqsSnsMessaging;

[EditorBrowsable(EditorBrowsableState.Never)]
internal static class AwsCollectionExtensions
{
    extension<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>>? source) where TKey : notnull
    {
        public Dictionary<TKey, TValue>? ToInitializedDictionary()
        {
            var result = source?.ToDictionary();
            if (result is null && AWSConfigs.InitializeCollections)
            {
                return [];
            }
            return result;
        }
    }

    extension<TSource>(IEnumerable<TSource>? source)
    {
        public List<TSource>? ToInitializedList()
        {
            var result = source?.ToList();
            if (result is null && AWSConfigs.InitializeCollections)
            {
                return [];
            }
            return result;
        }
    }
}
