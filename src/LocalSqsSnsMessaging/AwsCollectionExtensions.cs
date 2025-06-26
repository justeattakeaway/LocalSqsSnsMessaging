using Amazon;

namespace LocalSqsSnsMessaging;

internal static class AwsCollectionExtensions
{
    public static Dictionary<TKey, TValue>? ToInitializedDictionary<TKey, TValue>(
        this IEnumerable<KeyValuePair<TKey, TValue>>? source)
        where TKey : notnull
    {
        var result = source?.ToDictionary();
        if (result is null && AWSConfigs.InitializeCollections)
        {
            return [];
        }
        return result;
    }

    public static List<TSource>? ToInitializedList<TSource>(this IEnumerable<TSource>? source)
    {
        var result = source?.ToList();
        if (result is null && AWSConfigs.InitializeCollections)
        {
            return [];
        }
        return result;
    }
}
