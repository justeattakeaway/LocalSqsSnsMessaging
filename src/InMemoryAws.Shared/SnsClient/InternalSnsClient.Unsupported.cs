using InMemoryAws.Sns.Model;

namespace InMemoryAws;

/// <summary>Operations for the features this cloud doesn't model (data protection policies).</summary>
internal sealed partial class InternalSnsClient
{
    public Task<GetDataProtectionPolicyResponse> GetDataProtectionPolicyAsync(GetDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetDataProtectionPolicy is not supported.");

    public Task<PutDataProtectionPolicyResponse> PutDataProtectionPolicyAsync(PutDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("PutDataProtectionPolicy is not supported.");
}
