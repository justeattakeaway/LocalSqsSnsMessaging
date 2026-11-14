#pragma warning disable CS8600, CS8601, CS8602, CS8604 // Nullable warnings - internal POCOs use nullable properties but values are set at runtime

using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Tags: TagResource, UntagResource and ListTagsForResource, on event buses and rules.
internal sealed partial class InternalEventBridgeClient
{
    public Task<TagResourceResponse> TagResourceAsync(TagResourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ResolveTags(request.ResourceARN, write: true).Add(request.Tags);

        _bus.RecordOperation(AwsServiceName.EventBridge, "TagResource", request.ResourceARN);
        return Task.FromResult(new TagResourceResponse().SetCommonProperties());
    }

    public Task<UntagResourceResponse> UntagResourceAsync(UntagResourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ResolveTags(request.ResourceARN, write: true).Remove(request.TagKeys);

        _bus.RecordOperation(AwsServiceName.EventBridge, "UntagResource", request.ResourceARN);
        return Task.FromResult(new UntagResourceResponse().SetCommonProperties());
    }

    public Task<ListTagsForResourceResponse> ListTagsForResourceAsync(ListTagsForResourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tags = ResolveTags(request.ResourceARN, write: false).ToList();

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListTagsForResource", request.ResourceARN);
        return Task.FromResult(new ListTagsForResourceResponse { Tags = tags }.SetCommonProperties());
    }

    /// <summary>
    /// The tags of the event bus (<c>...:event-bus/name</c>) or rule (<c>...:rule/name</c> on the
    /// default bus, <c>...:rule/bus/name</c> on a custom bus) that <paramref name="arn"/> names.
    /// </summary>
    /// <param name="arn">The resource's ARN.</param>
    /// <param name="write">
    /// Whether the caller changes the tags, which a rule EventBridge manages (an archive's) refuses.
    /// </param>
    private EventBridgeTagSet ResolveTags(string? arn, bool write)
    {
        var parts = (arn ?? "").Split([':'], 6);
        if (parts.Length < 6 || !string.Equals(parts[0], "arn", StringComparison.Ordinal)
            || !string.Equals(parts[2], "events", StringComparison.Ordinal))
        {
            throw new InternalValidationException($"Parameter {arn} is not valid. Reason: Provided Arn is not in correct format.");
        }

        var resource = parts[5];
        if (resource.StartsWith("event-bus/", StringComparison.Ordinal))
        {
            var eventBus = ResolveBus(resource["event-bus/".Length..]);
            if (!string.Equals(eventBus.Arn, arn, StringComparison.Ordinal))
            {
                throw new InternalResourceNotFoundException($"Event bus {eventBus.Name} does not exist.");
            }

            return eventBus.Tags;
        }

        if (resource.StartsWith("rule/", StringComparison.Ordinal))
        {
            var path = resource["rule/".Length..];
            var slash = path.LastIndexOf('/');
            var rule = slash < 0 ? GetRule(null, path) : GetRule(path[..slash], path[(slash + 1)..]);
            if (!string.Equals(rule.Arn, arn, StringComparison.Ordinal))
            {
                throw new InternalResourceNotFoundException($"Rule {rule.Name} does not exist on EventBus {rule.EventBusName}.");
            }

            if (write && rule.ManagedBy is not null)
            {
                throw ManagedRuleError($"Tag related operations on the managed rule {rule.Name} is not allowed.");
            }

            return rule.Tags;
        }

        throw new InternalValidationException($"Parameter {arn} is not valid. Reason: Provided Arn is not in correct format.");
    }
}
