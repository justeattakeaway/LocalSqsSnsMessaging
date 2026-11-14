using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Archives and replays.
internal sealed partial class InternalEventBridgeClient
{
    public Task<CreateArchiveResponse> CreateArchiveAsync(CreateArchiveRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'CreateArchive' is not supported.");

    public Task<DescribeArchiveResponse> DescribeArchiveAsync(DescribeArchiveRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'DescribeArchive' is not supported.");

    public Task<UpdateArchiveResponse> UpdateArchiveAsync(UpdateArchiveRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'UpdateArchive' is not supported.");

    public Task<DeleteArchiveResponse> DeleteArchiveAsync(DeleteArchiveRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'DeleteArchive' is not supported.");

    public Task<ListArchivesResponse> ListArchivesAsync(ListArchivesRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'ListArchives' is not supported.");

    public Task<StartReplayResponse> StartReplayAsync(StartReplayRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'StartReplay' is not supported.");

    public Task<DescribeReplayResponse> DescribeReplayAsync(DescribeReplayRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'DescribeReplay' is not supported.");

    public Task<CancelReplayResponse> CancelReplayAsync(CancelReplayRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'CancelReplay' is not supported.");

    public Task<ListReplaysResponse> ListReplaysAsync(ListReplaysRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'ListReplays' is not supported.");
}
