using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Tags: TagResource, UntagResource and ListTagsForResource.
internal sealed partial class InternalEventBridgeClient
{
    public Task<TagResourceResponse> TagResourceAsync(TagResourceRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'TagResource' is not supported.");

    public Task<UntagResourceResponse> UntagResourceAsync(UntagResourceRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'UntagResource' is not supported.");

    public Task<ListTagsForResourceResponse> ListTagsForResourceAsync(ListTagsForResourceRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'ListTagsForResource' is not supported.");
}
