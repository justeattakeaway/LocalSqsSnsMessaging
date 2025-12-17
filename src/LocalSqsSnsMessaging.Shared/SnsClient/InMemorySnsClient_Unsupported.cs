using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Endpoint = Amazon.Runtime.Endpoints.Endpoint;

namespace LocalSqsSnsMessaging;

public partial class InMemorySnsClient
{
    Task IAmazonSimpleNotificationService.AuthorizeS3ToPublishAsync(string topicArn, string bucket) => throw new NotImplementedException();
    Task<SetPlatformApplicationAttributesResponse> IAmazonSimpleNotificationService.SetPlatformApplicationAttributesAsync(SetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<PutDataProtectionPolicyResponse> IAmazonSimpleNotificationService.PutDataProtectionPolicyAsync(PutDataProtectionPolicyRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<CheckIfPhoneNumberIsOptedOutResponse> IAmazonSimpleNotificationService.CheckIfPhoneNumberIsOptedOutAsync(CheckIfPhoneNumberIsOptedOutRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ConfirmSubscriptionResponse> IAmazonSimpleNotificationService.ConfirmSubscriptionAsync(string topicArn, string token, string authenticateOnUnsubscribe, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ConfirmSubscriptionResponse> IAmazonSimpleNotificationService.ConfirmSubscriptionAsync(string topicArn, string token, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ConfirmSubscriptionResponse> IAmazonSimpleNotificationService.ConfirmSubscriptionAsync(ConfirmSubscriptionRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<CreatePlatformApplicationResponse> IAmazonSimpleNotificationService.CreatePlatformApplicationAsync(CreatePlatformApplicationRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<CreatePlatformEndpointResponse> IAmazonSimpleNotificationService.CreatePlatformEndpointAsync(CreatePlatformEndpointRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<CreateSMSSandboxPhoneNumberResponse> IAmazonSimpleNotificationService.CreateSMSSandboxPhoneNumberAsync(CreateSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<SetSMSAttributesResponse> IAmazonSimpleNotificationService.SetSMSAttributesAsync(SetSMSAttributesRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<DeleteEndpointResponse> IAmazonSimpleNotificationService.DeleteEndpointAsync(DeleteEndpointRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<DeletePlatformApplicationResponse> IAmazonSimpleNotificationService.DeletePlatformApplicationAsync(DeletePlatformApplicationRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<DeleteSMSSandboxPhoneNumberResponse> IAmazonSimpleNotificationService.DeleteSMSSandboxPhoneNumberAsync(DeleteSMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<GetDataProtectionPolicyResponse> IAmazonSimpleNotificationService.GetDataProtectionPolicyAsync(GetDataProtectionPolicyRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<GetEndpointAttributesResponse> IAmazonSimpleNotificationService.GetEndpointAttributesAsync(GetEndpointAttributesRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<GetPlatformApplicationAttributesResponse> IAmazonSimpleNotificationService.GetPlatformApplicationAttributesAsync(GetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<GetSMSAttributesResponse> IAmazonSimpleNotificationService.GetSMSAttributesAsync(GetSMSAttributesRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<GetSMSSandboxAccountStatusResponse> IAmazonSimpleNotificationService.GetSMSSandboxAccountStatusAsync(GetSMSSandboxAccountStatusRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ListEndpointsByPlatformApplicationResponse> IAmazonSimpleNotificationService.ListEndpointsByPlatformApplicationAsync(ListEndpointsByPlatformApplicationRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ListOriginationNumbersResponse> IAmazonSimpleNotificationService.ListOriginationNumbersAsync(ListOriginationNumbersRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ListPhoneNumbersOptedOutResponse> IAmazonSimpleNotificationService.ListPhoneNumbersOptedOutAsync(ListPhoneNumbersOptedOutRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ListPlatformApplicationsResponse> IAmazonSimpleNotificationService.ListPlatformApplicationsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ListPlatformApplicationsResponse> IAmazonSimpleNotificationService.ListPlatformApplicationsAsync(ListPlatformApplicationsRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<ListSMSSandboxPhoneNumbersResponse> IAmazonSimpleNotificationService.ListSMSSandboxPhoneNumbersAsync(ListSMSSandboxPhoneNumbersRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<VerifySMSSandboxPhoneNumberResponse> IAmazonSimpleNotificationService.VerifySMSSandboxPhoneNumberAsync(VerifySMSSandboxPhoneNumberRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<OptInPhoneNumberResponse> IAmazonSimpleNotificationService.OptInPhoneNumberAsync(OptInPhoneNumberRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Task<SetEndpointAttributesResponse> IAmazonSimpleNotificationService.SetEndpointAttributesAsync(SetEndpointAttributesRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
    Endpoint IAmazonSimpleNotificationService.DetermineServiceOperationEndpoint(AmazonWebServiceRequest request) => throw new NotImplementedException();
}
