using LocalSqsSnsMessaging.Sns.Model;

namespace LocalSqsSnsMessaging;

/// <summary>Operations for the protocols and features this bus doesn't model (SMS, mobile push, data protection).</summary>
internal sealed partial class InternalSnsClient
{
    public Task<CheckIfPhoneNumberIsOptedOutResponse> CheckIfPhoneNumberIsOptedOutAsync(CheckIfPhoneNumberIsOptedOutRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CheckIfPhoneNumberIsOptedOut is not supported.");


    public Task<CreatePlatformApplicationResponse> CreatePlatformApplicationAsync(CreatePlatformApplicationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CreatePlatformApplication is not supported.");

    public Task<CreatePlatformEndpointResponse> CreatePlatformEndpointAsync(CreatePlatformEndpointRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CreatePlatformEndpoint is not supported.");

    public Task<CreateSMSSandboxPhoneNumberResponse> CreateSMSSandboxPhoneNumberAsync(CreateSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("CreateSMSSandboxPhoneNumber is not supported.");

    public Task<DeleteEndpointResponse> DeleteEndpointAsync(DeleteEndpointRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DeleteEndpoint is not supported.");

    public Task<DeletePlatformApplicationResponse> DeletePlatformApplicationAsync(DeletePlatformApplicationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DeletePlatformApplication is not supported.");

    public Task<DeleteSMSSandboxPhoneNumberResponse> DeleteSMSSandboxPhoneNumberAsync(DeleteSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DeleteSMSSandboxPhoneNumber is not supported.");

    public Task<GetDataProtectionPolicyResponse> GetDataProtectionPolicyAsync(GetDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetDataProtectionPolicy is not supported.");

    public Task<GetEndpointAttributesResponse> GetEndpointAttributesAsync(GetEndpointAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetEndpointAttributes is not supported.");

    public Task<GetPlatformApplicationAttributesResponse> GetPlatformApplicationAttributesAsync(GetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetPlatformApplicationAttributes is not supported.");

    public Task<GetSMSAttributesResponse> GetSMSAttributesAsync(GetSMSAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetSMSAttributes is not supported.");

    public Task<GetSMSSandboxAccountStatusResponse> GetSMSSandboxAccountStatusAsync(GetSMSSandboxAccountStatusRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("GetSMSSandboxAccountStatus is not supported.");

    public Task<ListEndpointsByPlatformApplicationResponse> ListEndpointsByPlatformApplicationAsync(ListEndpointsByPlatformApplicationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListEndpointsByPlatformApplication is not supported.");

    public Task<ListOriginationNumbersResponse> ListOriginationNumbersAsync(ListOriginationNumbersRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListOriginationNumbers is not supported.");

    public Task<ListPhoneNumbersOptedOutResponse> ListPhoneNumbersOptedOutAsync(ListPhoneNumbersOptedOutRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListPhoneNumbersOptedOut is not supported.");

    public Task<ListPlatformApplicationsResponse> ListPlatformApplicationsAsync(ListPlatformApplicationsRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListPlatformApplications is not supported.");

    public Task<ListSMSSandboxPhoneNumbersResponse> ListSMSSandboxPhoneNumbersAsync(ListSMSSandboxPhoneNumbersRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ListSMSSandboxPhoneNumbers is not supported.");

    public Task<OptInPhoneNumberResponse> OptInPhoneNumberAsync(OptInPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("OptInPhoneNumber is not supported.");

    public Task<PutDataProtectionPolicyResponse> PutDataProtectionPolicyAsync(PutDataProtectionPolicyRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("PutDataProtectionPolicy is not supported.");

    public Task<SetEndpointAttributesResponse> SetEndpointAttributesAsync(SetEndpointAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SetEndpointAttributes is not supported.");

    public Task<SetPlatformApplicationAttributesResponse> SetPlatformApplicationAttributesAsync(SetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SetPlatformApplicationAttributes is not supported.");

    public Task<SetSMSAttributesResponse> SetSMSAttributesAsync(SetSMSAttributesRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("SetSMSAttributes is not supported.");

    public Task<VerifySMSSandboxPhoneNumberResponse> VerifySMSSandboxPhoneNumberAsync(VerifySMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("VerifySMSSandboxPhoneNumber is not supported.");
}
