using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Operations for the features this bus doesn't model (data protection policies).</summary>
internal sealed partial class InternalSnsClient
{
    public Task<GetDataProtectionPolicyResponse> GetDataProtectionPolicyAsync(GetDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetDataProtectionPolicy is not supported.");

    public Task<PutDataProtectionPolicyResponse> PutDataProtectionPolicyAsync(PutDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("PutDataProtectionPolicy is not supported.");
}
