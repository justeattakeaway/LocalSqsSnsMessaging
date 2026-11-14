using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Event bus resource policies: PutPermission and RemovePermission.
internal sealed partial class InternalEventBridgeClient
{
    public Task<PutPermissionResponse> PutPermissionAsync(PutPermissionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'PutPermission' is not supported.");

    public Task<RemovePermissionResponse> RemovePermissionAsync(RemovePermissionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("EventBridge operation 'RemovePermission' is not supported.");
}
