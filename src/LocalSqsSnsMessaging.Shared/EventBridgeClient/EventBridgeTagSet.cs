using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// The tags on a taggable EventBridge resource (an event bus or a rule), with EventBridge's rules:
/// at most <see cref="MaxTags"/> per resource, counting the tags already there (overwriting a key
/// adds nothing), and no keys in the reserved <c>aws:</c> namespace. A refused request changes nothing.
/// </summary>
internal sealed class EventBridgeTagSet
{
    /// <summary>The most tags one resource can carry.</summary>
    public const int MaxTags = 50;

    private const string SystemKeyPrefix = "aws:";

    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);

    /// <summary>A tag set holding <paramref name="tags"/>, validated as a create request's tags.</summary>
    public static EventBridgeTagSet From(List<Tag>? tags)
    {
        var set = new EventBridgeTagSet();
        set.Add(tags);
        return set;
    }

    /// <summary>Adds or overwrites <paramref name="tags"/>, all or nothing.</summary>
    public void Add(List<Tag>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return;
        }

        if (tags.Exists(t => IsSystemKey(t.Key)))
        {
            throw SystemTagsNotAllowed();
        }

        lock (_tags)
        {
            var newKeys = tags.Select(t => t.Key ?? "").Distinct(StringComparer.Ordinal).Count(k => !_tags.ContainsKey(k));
            if (_tags.Count + newKeys > MaxTags)
            {
                throw new InternalValidationException("The request exceeds the maximum number of tags allowed per resource.");
            }

            foreach (var tag in tags)
            {
                _tags[tag.Key ?? ""] = tag.Value ?? "";
            }
        }
    }

    /// <summary>Removes <paramref name="keys"/>; a key that is not there is ignored.</summary>
    public void Remove(List<string>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return;
        }

        if (keys.Exists(IsSystemKey))
        {
            throw SystemTagsNotAllowed();
        }

        lock (_tags)
        {
            foreach (var key in keys)
            {
                _tags.Remove(key);
            }
        }
    }

    /// <summary>The tags, as ListTagsForResource returns them.</summary>
    public List<Tag> ToList()
    {
        lock (_tags)
        {
            return [.. _tags.Select(kv => new Tag { Key = kv.Key, Value = kv.Value })];
        }
    }

    private static bool IsSystemKey(string? key) =>
        key is not null && key.StartsWith(SystemKeyPrefix, StringComparison.OrdinalIgnoreCase);

    private static InternalValidationException SystemTagsNotAllowed() =>
        new("Add/Remove system tags is not allowed.");
}
