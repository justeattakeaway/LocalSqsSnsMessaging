using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Amazon.SimpleNotificationService.Model;
using Microsoft.AspNetCore.WebUtilities;

namespace Amazon.SimpleNotificationService
{
    public sealed class SnsClientHandler : HttpClientHandler
    {
        private readonly IAmazonSimpleNotificationService _innerClient;

        public SnsClientHandler(IAmazonSimpleNotificationService innerClient)
        {
            _innerClient = innerClient;
        }
    
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Debug.Assert(request != null);
            if (request.Content is ByteArrayContent byteArrayContent)
            {
                var contentString = await byteArrayContent.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var query = QueryHelpers.ParseQuery(contentString);
                if (query.TryGetValue("Action", out var action))
                {
                    switch (action)
                    {

                        case "AddPermission":
                        {
                            var addPermissionRequest = new AddPermissionRequest
                            {
                                ActionName = query.TryGetValue("ActionName.member", out var actionNameValues) ? 
                                    actionNameValues.Select<string, System.String?>((string v) => v.ToString()).ToList() : null,
                                AWSAccountId = query.TryGetValue("AWSAccountId.member", out var aWSAccountIdValues) ? 
                                    aWSAccountIdValues.Select<string, System.String?>((string v) => v.ToString()).ToList() : null,
                                Label = query.TryGetValue("Label", out var label) ? 
                                    label.ToString() : default!,
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var addPermissionResult = await _innerClient.AddPermissionAsync(addPermissionRequest, cancellationToken).ConfigureAwait(false);
                            var addPermissionResponseContent = AddPermissionResponseXmlWriter.Write(addPermissionResult); 
                            var addPermissionResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(addPermissionResponseContent)
                            };
                            addPermissionResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return addPermissionResponse;
                        }

                        case "CheckIfPhoneNumberIsOptedOut":
                        {
                            var checkIfPhoneNumberIsOptedOutRequest = new CheckIfPhoneNumberIsOptedOutRequest
                            {
                                PhoneNumber = query.TryGetValue("PhoneNumber", out var phoneNumber) ? 
                                    phoneNumber.ToString() : default!,
                            };
                            var checkIfPhoneNumberIsOptedOutResult = await _innerClient.CheckIfPhoneNumberIsOptedOutAsync(checkIfPhoneNumberIsOptedOutRequest, cancellationToken).ConfigureAwait(false);
                            var checkIfPhoneNumberIsOptedOutResponseContent = CheckIfPhoneNumberIsOptedOutResponseXmlWriter.Write(checkIfPhoneNumberIsOptedOutResult); 
                            var checkIfPhoneNumberIsOptedOutResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(checkIfPhoneNumberIsOptedOutResponseContent)
                            };
                            checkIfPhoneNumberIsOptedOutResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return checkIfPhoneNumberIsOptedOutResponse;
                        }

                        case "ConfirmSubscription":
                        {
                            var confirmSubscriptionRequest = new ConfirmSubscriptionRequest
                            {
                                AuthenticateOnUnsubscribe = query.TryGetValue("AuthenticateOnUnsubscribe", out var authenticateOnUnsubscribe) ? 
                                    authenticateOnUnsubscribe.ToString() : default!,
                                Token = query.TryGetValue("Token", out var token) ? 
                                    token.ToString() : default!,
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var confirmSubscriptionResult = await _innerClient.ConfirmSubscriptionAsync(confirmSubscriptionRequest, cancellationToken).ConfigureAwait(false);
                            var confirmSubscriptionResponseContent = ConfirmSubscriptionResponseXmlWriter.Write(confirmSubscriptionResult); 
                            var confirmSubscriptionResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(confirmSubscriptionResponseContent)
                            };
                            confirmSubscriptionResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return confirmSubscriptionResponse;
                        }

                        case "CreatePlatformApplication":
                        {
                            var createPlatformApplicationRequest = new CreatePlatformApplicationRequest
                            {
                                Attributes = query.ToFlatDictionary<System.String>("Attributes", v => v.ToString()),
                                Name = query.TryGetValue("Name", out var name) ? 
                                    name.ToString() : default!,
                                Platform = query.TryGetValue("Platform", out var platform) ? 
                                    platform.ToString() : default!,
                            };
                            var createPlatformApplicationResult = await _innerClient.CreatePlatformApplicationAsync(createPlatformApplicationRequest, cancellationToken).ConfigureAwait(false);
                            var createPlatformApplicationResponseContent = CreatePlatformApplicationResponseXmlWriter.Write(createPlatformApplicationResult); 
                            var createPlatformApplicationResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(createPlatformApplicationResponseContent)
                            };
                            createPlatformApplicationResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return createPlatformApplicationResponse;
                        }

                        case "CreatePlatformEndpoint":
                        {
                            var createPlatformEndpointRequest = new CreatePlatformEndpointRequest
                            {
                                Attributes = query.ToFlatDictionary<System.String>("Attributes", v => v.ToString()),
                                CustomUserData = query.TryGetValue("CustomUserData", out var customUserData) ? 
                                    customUserData.ToString() : default!,
                                PlatformApplicationArn = query.TryGetValue("PlatformApplicationArn", out var platformApplicationArn) ? 
                                    platformApplicationArn.ToString() : default!,
                                Token = query.TryGetValue("Token", out var token) ? 
                                    token.ToString() : default!,
                            };
                            var createPlatformEndpointResult = await _innerClient.CreatePlatformEndpointAsync(createPlatformEndpointRequest, cancellationToken).ConfigureAwait(false);
                            var createPlatformEndpointResponseContent = CreatePlatformEndpointResponseXmlWriter.Write(createPlatformEndpointResult); 
                            var createPlatformEndpointResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(createPlatformEndpointResponseContent)
                            };
                            createPlatformEndpointResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return createPlatformEndpointResponse;
                        }

                        case "CreateSMSSandboxPhoneNumber":
                        {
                            var createSMSSandboxPhoneNumberRequest = new CreateSMSSandboxPhoneNumberRequest
                            {
                                LanguageCode = query.TryGetValue("LanguageCode", out var languageCode) ? 
                                    default! : default!,
                                PhoneNumber = query.TryGetValue("PhoneNumber", out var phoneNumber) ? 
                                    phoneNumber.ToString() : default!,
                            };
                            var createSMSSandboxPhoneNumberResult = await _innerClient.CreateSMSSandboxPhoneNumberAsync(createSMSSandboxPhoneNumberRequest, cancellationToken).ConfigureAwait(false);
                            var createSMSSandboxPhoneNumberResponseContent = CreateSMSSandboxPhoneNumberResponseXmlWriter.Write(createSMSSandboxPhoneNumberResult); 
                            var createSMSSandboxPhoneNumberResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(createSMSSandboxPhoneNumberResponseContent)
                            };
                            createSMSSandboxPhoneNumberResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return createSMSSandboxPhoneNumberResponse;
                        }

                        case "CreateTopic":
                        {
                            var createTopicRequest = new CreateTopicRequest
                            {
                                Attributes = query.ToFlatDictionary<System.String>("Attributes", v => v.ToString()),
                                DataProtectionPolicy =
                                    query.TryGetValue("DataProtectionPolicy", out var dataProtectionPolicy)
                                        ? dataProtectionPolicy.ToString()
                                        : default!,
                                Name = query.TryGetValue("Name", out var name) ? name.ToString() : default!,
                                Tags = query.Keys
                                    .Where(k => k.StartsWith("Tags.member.", StringComparison.Ordinal))
                                    .Select(k => int.Parse(k.Split('.')[2], CultureInfo.InvariantCulture))
                                    .Distinct()
                                    .Select(index => new Tag
                                    {
                                        Key = query.TryGetValue($"Tags.member.{index}.Key", out var tagsKey)
                                            ? tagsKey.ToString()
                                            : default,
                                        Value = query.TryGetValue($"Tags.member.{index}.Value", out var tagsValue)
                                            ? tagsValue.ToString()
                                            : default
                                    })
                                    .ToList(),
                            };
                            var createTopicResult = await _innerClient.CreateTopicAsync(createTopicRequest, cancellationToken).ConfigureAwait(false);
                            var createTopicResponseContent = CreateTopicResponseXmlWriter.Write(createTopicResult); 
                            var createTopicResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(createTopicResponseContent)
                            };
                            createTopicResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return createTopicResponse;
                        }

                        case "DeleteEndpoint":
                        {
                            var deleteEndpointRequest = new DeleteEndpointRequest
                            {
                                EndpointArn = query.TryGetValue("EndpointArn", out var endpointArn) ? 
                                    endpointArn.ToString() : default!,
                            };
                            var deleteEndpointResult = await _innerClient.DeleteEndpointAsync(deleteEndpointRequest, cancellationToken).ConfigureAwait(false);
                            var deleteEndpointResponseContent = DeleteEndpointResponseXmlWriter.Write(deleteEndpointResult); 
                            var deleteEndpointResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(deleteEndpointResponseContent)
                            };
                            deleteEndpointResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return deleteEndpointResponse;
                        }

                        case "DeletePlatformApplication":
                        {
                            var deletePlatformApplicationRequest = new DeletePlatformApplicationRequest
                            {
                                PlatformApplicationArn = query.TryGetValue("PlatformApplicationArn", out var platformApplicationArn) ? 
                                    platformApplicationArn.ToString() : default!,
                            };
                            var deletePlatformApplicationResult = await _innerClient.DeletePlatformApplicationAsync(deletePlatformApplicationRequest, cancellationToken).ConfigureAwait(false);
                            var deletePlatformApplicationResponseContent = DeletePlatformApplicationResponseXmlWriter.Write(deletePlatformApplicationResult); 
                            var deletePlatformApplicationResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(deletePlatformApplicationResponseContent)
                            };
                            deletePlatformApplicationResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return deletePlatformApplicationResponse;
                        }

                        case "DeleteSMSSandboxPhoneNumber":
                        {
                            var deleteSMSSandboxPhoneNumberRequest = new DeleteSMSSandboxPhoneNumberRequest
                            {
                                PhoneNumber = query.TryGetValue("PhoneNumber", out var phoneNumber) ? 
                                    phoneNumber.ToString() : default!,
                            };
                            var deleteSMSSandboxPhoneNumberResult = await _innerClient.DeleteSMSSandboxPhoneNumberAsync(deleteSMSSandboxPhoneNumberRequest, cancellationToken).ConfigureAwait(false);
                            var deleteSMSSandboxPhoneNumberResponseContent = DeleteSMSSandboxPhoneNumberResponseXmlWriter.Write(deleteSMSSandboxPhoneNumberResult); 
                            var deleteSMSSandboxPhoneNumberResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(deleteSMSSandboxPhoneNumberResponseContent)
                            };
                            deleteSMSSandboxPhoneNumberResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return deleteSMSSandboxPhoneNumberResponse;
                        }

                        case "DeleteTopic":
                        {
                            var deleteTopicRequest = new DeleteTopicRequest
                            {
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var deleteTopicResult = await _innerClient.DeleteTopicAsync(deleteTopicRequest, cancellationToken).ConfigureAwait(false);
                            var deleteTopicResponseContent = DeleteTopicResponseXmlWriter.Write(deleteTopicResult); 
                            var deleteTopicResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(deleteTopicResponseContent)
                            };
                            deleteTopicResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return deleteTopicResponse;
                        }

                        case "GetDataProtectionPolicy":
                        {
                            var getDataProtectionPolicyRequest = new GetDataProtectionPolicyRequest
                            {
                                ResourceArn = query.TryGetValue("ResourceArn", out var resourceArn) ? 
                                    resourceArn.ToString() : default!,
                            };
                            var getDataProtectionPolicyResult = await _innerClient.GetDataProtectionPolicyAsync(getDataProtectionPolicyRequest, cancellationToken).ConfigureAwait(false);
                            var getDataProtectionPolicyResponseContent = GetDataProtectionPolicyResponseXmlWriter.Write(getDataProtectionPolicyResult); 
                            var getDataProtectionPolicyResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(getDataProtectionPolicyResponseContent)
                            };
                            getDataProtectionPolicyResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return getDataProtectionPolicyResponse;
                        }

                        case "GetEndpointAttributes":
                        {
                            var getEndpointAttributesRequest = new GetEndpointAttributesRequest
                            {
                                EndpointArn = query.TryGetValue("EndpointArn", out var endpointArn) ? 
                                    endpointArn.ToString() : default!,
                            };
                            var getEndpointAttributesResult = await _innerClient.GetEndpointAttributesAsync(getEndpointAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var getEndpointAttributesResponseContent = GetEndpointAttributesResponseXmlWriter.Write(getEndpointAttributesResult); 
                            var getEndpointAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(getEndpointAttributesResponseContent)
                            };
                            getEndpointAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return getEndpointAttributesResponse;
                        }

                        case "GetPlatformApplicationAttributes":
                        {
                            var getPlatformApplicationAttributesRequest = new GetPlatformApplicationAttributesRequest
                            {
                                PlatformApplicationArn = query.TryGetValue("PlatformApplicationArn", out var platformApplicationArn) ? 
                                    platformApplicationArn.ToString() : default!,
                            };
                            var getPlatformApplicationAttributesResult = await _innerClient.GetPlatformApplicationAttributesAsync(getPlatformApplicationAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var getPlatformApplicationAttributesResponseContent = GetPlatformApplicationAttributesResponseXmlWriter.Write(getPlatformApplicationAttributesResult); 
                            var getPlatformApplicationAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(getPlatformApplicationAttributesResponseContent)
                            };
                            getPlatformApplicationAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return getPlatformApplicationAttributesResponse;
                        }

                        case "GetSMSAttributes":
                        {
                            var getSMSAttributesRequest = new GetSMSAttributesRequest
                            {
                                Attributes = query.TryGetValue("Attributes.member", out var attributesValues) ? 
                                    attributesValues.Select<string, System.String?>((string v) => v.ToString()).ToList() : null,
                            };
                            var getSMSAttributesResult = await _innerClient.GetSMSAttributesAsync(getSMSAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var getSMSAttributesResponseContent = GetSMSAttributesResponseXmlWriter.Write(getSMSAttributesResult); 
                            var getSMSAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(getSMSAttributesResponseContent)
                            };
                            getSMSAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return getSMSAttributesResponse;
                        }

                        case "GetSMSSandboxAccountStatus":
                        {
                            var getSMSSandboxAccountStatusRequest = new GetSMSSandboxAccountStatusRequest
                            {
                            };
                            var getSMSSandboxAccountStatusResult = await _innerClient.GetSMSSandboxAccountStatusAsync(getSMSSandboxAccountStatusRequest, cancellationToken).ConfigureAwait(false);
                            var getSMSSandboxAccountStatusResponseContent = GetSMSSandboxAccountStatusResponseXmlWriter.Write(getSMSSandboxAccountStatusResult); 
                            var getSMSSandboxAccountStatusResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(getSMSSandboxAccountStatusResponseContent)
                            };
                            getSMSSandboxAccountStatusResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return getSMSSandboxAccountStatusResponse;
                        }

                        case "GetSubscriptionAttributes":
                        {
                            var getSubscriptionAttributesRequest = new GetSubscriptionAttributesRequest
                            {
                                SubscriptionArn = query.TryGetValue("SubscriptionArn", out var subscriptionArn) ? 
                                    subscriptionArn.ToString() : default!,
                            };
                            var getSubscriptionAttributesResult = await _innerClient.GetSubscriptionAttributesAsync(getSubscriptionAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var getSubscriptionAttributesResponseContent = GetSubscriptionAttributesResponseXmlWriter.Write(getSubscriptionAttributesResult); 
                            var getSubscriptionAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(getSubscriptionAttributesResponseContent)
                            };
                            getSubscriptionAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return getSubscriptionAttributesResponse;
                        }

                        case "GetTopicAttributes":
                        {
                            var getTopicAttributesRequest = new GetTopicAttributesRequest
                            {
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var getTopicAttributesResult = await _innerClient.GetTopicAttributesAsync(getTopicAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var getTopicAttributesResponseContent = GetTopicAttributesResponseXmlWriter.Write(getTopicAttributesResult); 
                            var getTopicAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(getTopicAttributesResponseContent)
                            };
                            getTopicAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return getTopicAttributesResponse;
                        }

                        case "ListEndpointsByPlatformApplication":
                        {
                            var listEndpointsByPlatformApplicationRequest = new ListEndpointsByPlatformApplicationRequest
                            {
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                                PlatformApplicationArn = query.TryGetValue("PlatformApplicationArn", out var platformApplicationArn) ? 
                                    platformApplicationArn.ToString() : default!,
                            };
                            var listEndpointsByPlatformApplicationResult = await _innerClient.ListEndpointsByPlatformApplicationAsync(listEndpointsByPlatformApplicationRequest, cancellationToken).ConfigureAwait(false);
                            var listEndpointsByPlatformApplicationResponseContent = ListEndpointsByPlatformApplicationResponseXmlWriter.Write(listEndpointsByPlatformApplicationResult); 
                            var listEndpointsByPlatformApplicationResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listEndpointsByPlatformApplicationResponseContent)
                            };
                            listEndpointsByPlatformApplicationResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listEndpointsByPlatformApplicationResponse;
                        }

                        case "ListOriginationNumbers":
                        {
                            var listOriginationNumbersRequest = new ListOriginationNumbersRequest
                            {
                                MaxResults = query.TryGetValue("MaxResults", out var maxResults) ? 
                                    int.Parse(maxResults.ToString(), CultureInfo.InvariantCulture) : default!,
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                            };
                            var listOriginationNumbersResult = await _innerClient.ListOriginationNumbersAsync(listOriginationNumbersRequest, cancellationToken).ConfigureAwait(false);
                            var listOriginationNumbersResponseContent = ListOriginationNumbersResponseXmlWriter.Write(listOriginationNumbersResult); 
                            var listOriginationNumbersResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listOriginationNumbersResponseContent)
                            };
                            listOriginationNumbersResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listOriginationNumbersResponse;
                        }

                        case "ListPhoneNumbersOptedOut":
                        {
                            var listPhoneNumbersOptedOutRequest = new ListPhoneNumbersOptedOutRequest
                            {
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                            };
                            var listPhoneNumbersOptedOutResult = await _innerClient.ListPhoneNumbersOptedOutAsync(listPhoneNumbersOptedOutRequest, cancellationToken).ConfigureAwait(false);
                            var listPhoneNumbersOptedOutResponseContent = ListPhoneNumbersOptedOutResponseXmlWriter.Write(listPhoneNumbersOptedOutResult); 
                            var listPhoneNumbersOptedOutResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listPhoneNumbersOptedOutResponseContent)
                            };
                            listPhoneNumbersOptedOutResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listPhoneNumbersOptedOutResponse;
                        }

                        case "ListPlatformApplications":
                        {
                            var listPlatformApplicationsRequest = new ListPlatformApplicationsRequest
                            {
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                            };
                            var listPlatformApplicationsResult = await _innerClient.ListPlatformApplicationsAsync(listPlatformApplicationsRequest, cancellationToken).ConfigureAwait(false);
                            var listPlatformApplicationsResponseContent = ListPlatformApplicationsResponseXmlWriter.Write(listPlatformApplicationsResult); 
                            var listPlatformApplicationsResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listPlatformApplicationsResponseContent)
                            };
                            listPlatformApplicationsResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listPlatformApplicationsResponse;
                        }

                        case "ListSMSSandboxPhoneNumbers":
                        {
                            var listSMSSandboxPhoneNumbersRequest = new ListSMSSandboxPhoneNumbersRequest
                            {
                                MaxResults = query.TryGetValue("MaxResults", out var maxResults) ? 
                                    int.Parse(maxResults.ToString(), CultureInfo.InvariantCulture) : default!,
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                            };
                            var listSMSSandboxPhoneNumbersResult = await _innerClient.ListSMSSandboxPhoneNumbersAsync(listSMSSandboxPhoneNumbersRequest, cancellationToken).ConfigureAwait(false);
                            var listSMSSandboxPhoneNumbersResponseContent = ListSMSSandboxPhoneNumbersResponseXmlWriter.Write(listSMSSandboxPhoneNumbersResult); 
                            var listSMSSandboxPhoneNumbersResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listSMSSandboxPhoneNumbersResponseContent)
                            };
                            listSMSSandboxPhoneNumbersResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listSMSSandboxPhoneNumbersResponse;
                        }

                        case "ListSubscriptionsByTopic":
                        {
                            var listSubscriptionsByTopicRequest = new ListSubscriptionsByTopicRequest
                            {
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var listSubscriptionsByTopicResult = await _innerClient.ListSubscriptionsByTopicAsync(listSubscriptionsByTopicRequest, cancellationToken).ConfigureAwait(false);
                            var listSubscriptionsByTopicResponseContent = ListSubscriptionsByTopicResponseXmlWriter.Write(listSubscriptionsByTopicResult); 
                            var listSubscriptionsByTopicResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listSubscriptionsByTopicResponseContent)
                            };
                            listSubscriptionsByTopicResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listSubscriptionsByTopicResponse;
                        }

                        case "ListSubscriptions":
                        {
                            var listSubscriptionsRequest = new ListSubscriptionsRequest
                            {
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                            };
                            var listSubscriptionsResult = await _innerClient.ListSubscriptionsAsync(listSubscriptionsRequest, cancellationToken).ConfigureAwait(false);
                            var listSubscriptionsResponseContent = ListSubscriptionsResponseXmlWriter.Write(listSubscriptionsResult); 
                            var listSubscriptionsResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listSubscriptionsResponseContent)
                            };
                            listSubscriptionsResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listSubscriptionsResponse;
                        }

                        case "ListTagsForResource":
                        {
                            var listTagsForResourceRequest = new ListTagsForResourceRequest
                            {
                                ResourceArn = query.TryGetValue("ResourceArn", out var resourceArn) ? 
                                    resourceArn.ToString() : default!,
                            };
                            var listTagsForResourceResult = await _innerClient.ListTagsForResourceAsync(listTagsForResourceRequest, cancellationToken).ConfigureAwait(false);
                            var listTagsForResourceResponseContent = ListTagsForResourceResponseXmlWriter.Write(listTagsForResourceResult); 
                            var listTagsForResourceResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listTagsForResourceResponseContent)
                            };
                            listTagsForResourceResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listTagsForResourceResponse;
                        }

                        case "ListTopics":
                        {
                            var listTopicsRequest = new ListTopicsRequest
                            {
                                NextToken = query.TryGetValue("NextToken", out var nextToken) ? 
                                    nextToken.ToString() : default!,
                            };
                            var listTopicsResult = await _innerClient.ListTopicsAsync(listTopicsRequest, cancellationToken).ConfigureAwait(false);
                            var listTopicsResponseContent = ListTopicsResponseXmlWriter.Write(listTopicsResult); 
                            var listTopicsResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(listTopicsResponseContent)
                            };
                            listTopicsResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return listTopicsResponse;
                        }

                        case "OptInPhoneNumber":
                        {
                            var optInPhoneNumberRequest = new OptInPhoneNumberRequest
                            {
                                PhoneNumber = query.TryGetValue("PhoneNumber", out var phoneNumber) ? 
                                    phoneNumber.ToString() : default!,
                            };
                            var optInPhoneNumberResult = await _innerClient.OptInPhoneNumberAsync(optInPhoneNumberRequest, cancellationToken).ConfigureAwait(false);
                            var optInPhoneNumberResponseContent = OptInPhoneNumberResponseXmlWriter.Write(optInPhoneNumberResult); 
                            var optInPhoneNumberResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(optInPhoneNumberResponseContent)
                            };
                            optInPhoneNumberResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return optInPhoneNumberResponse;
                        }

                        case "PublishBatch":
                        {
                            var publishBatchRequest = new PublishBatchRequest
                            {
                            PublishBatchRequestEntries = query.Keys
                                .Where(k => k.StartsWith("PublishBatchRequestEntries.member.", StringComparison.Ordinal))
                                .Select(k => int.Parse(k.Split('.')[2], CultureInfo.InvariantCulture))
                                .Distinct()
                                .Select(index => new PublishBatchRequestEntry
                                {
                                    Id = query.TryGetValue($"PublishBatchRequestEntries.member.{index}.Id", out var publishBatchRequestEntriesId) ? 
publishBatchRequestEntriesId.ToString() : default,
Message = query.TryGetValue($"PublishBatchRequestEntries.member.{index}.Message", out var publishBatchRequestEntriesMessage) ? 
publishBatchRequestEntriesMessage.ToString() : default,
                        MessageAttributes = query.ToFlatDictionary<Amazon.SimpleNotificationService.Model.MessageAttributeValue>($"PublishBatchRequestEntries.member.{index}.MessageAttributes", v => default!),
MessageDeduplicationId = query.TryGetValue($"PublishBatchRequestEntries.member.{index}.MessageDeduplicationId", out var publishBatchRequestEntriesMessageDeduplicationId) ? 
publishBatchRequestEntriesMessageDeduplicationId.ToString() : default,
MessageGroupId = query.TryGetValue($"PublishBatchRequestEntries.member.{index}.MessageGroupId", out var publishBatchRequestEntriesMessageGroupId) ? 
publishBatchRequestEntriesMessageGroupId.ToString() : default,
MessageStructure = query.TryGetValue($"PublishBatchRequestEntries.member.{index}.MessageStructure", out var publishBatchRequestEntriesMessageStructure) ? 
publishBatchRequestEntriesMessageStructure.ToString() : default,
Subject = query.TryGetValue($"PublishBatchRequestEntries.member.{index}.Subject", out var publishBatchRequestEntriesSubject) ? 
publishBatchRequestEntriesSubject.ToString() : default
                                })
                                .ToList(),
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var publishBatchResult = await _innerClient.PublishBatchAsync(publishBatchRequest, cancellationToken).ConfigureAwait(false);
                            var publishBatchResponseContent = PublishBatchResponseXmlWriter.Write(publishBatchResult); 
                            var publishBatchResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(publishBatchResponseContent)
                            };
                            publishBatchResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return publishBatchResponse;
                        }

                        case "Publish":
                        {
                            var publishRequest = new PublishRequest
                            {
                                Message = query.TryGetValue("Message", out var message) ? 
                                    message.ToString() : default!,
                                MessageAttributes = query.ToFlatDictionary<Amazon.SimpleNotificationService.Model.MessageAttributeValue>("MessageAttributes", v => default!),
                                MessageDeduplicationId = query.TryGetValue("MessageDeduplicationId", out var messageDeduplicationId) ? 
                                    messageDeduplicationId.ToString() : default!,
                                MessageGroupId = query.TryGetValue("MessageGroupId", out var messageGroupId) ? 
                                    messageGroupId.ToString() : default!,
                                MessageStructure = query.TryGetValue("MessageStructure", out var messageStructure) ? 
                                    messageStructure.ToString() : default!,
                                PhoneNumber = query.TryGetValue("PhoneNumber", out var phoneNumber) ? 
                                    phoneNumber.ToString() : default!,
                                Subject = query.TryGetValue("Subject", out var subject) ? 
                                    subject.ToString() : default!,
                                TargetArn = query.TryGetValue("TargetArn", out var targetArn) ? 
                                    targetArn.ToString() : default!,
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var publishResult = await _innerClient.PublishAsync(publishRequest, cancellationToken).ConfigureAwait(false);
                            var publishResponseContent = PublishResponseXmlWriter.Write(publishResult); 
                            var publishResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(publishResponseContent)
                            };
                            publishResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return publishResponse;
                        }

                        case "PutDataProtectionPolicy":
                        {
                            var putDataProtectionPolicyRequest = new PutDataProtectionPolicyRequest
                            {
                                DataProtectionPolicy = query.TryGetValue("DataProtectionPolicy", out var dataProtectionPolicy) ? 
                                    dataProtectionPolicy.ToString() : default!,
                                ResourceArn = query.TryGetValue("ResourceArn", out var resourceArn) ? 
                                    resourceArn.ToString() : default!,
                            };
                            var putDataProtectionPolicyResult = await _innerClient.PutDataProtectionPolicyAsync(putDataProtectionPolicyRequest, cancellationToken).ConfigureAwait(false);
                            var putDataProtectionPolicyResponseContent = PutDataProtectionPolicyResponseXmlWriter.Write(putDataProtectionPolicyResult); 
                            var putDataProtectionPolicyResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(putDataProtectionPolicyResponseContent)
                            };
                            putDataProtectionPolicyResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return putDataProtectionPolicyResponse;
                        }

                        case "RemovePermission":
                        {
                            var removePermissionRequest = new RemovePermissionRequest
                            {
                                Label = query.TryGetValue("Label", out var label) ? 
                                    label.ToString() : default!,
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var removePermissionResult = await _innerClient.RemovePermissionAsync(removePermissionRequest, cancellationToken).ConfigureAwait(false);
                            var removePermissionResponseContent = RemovePermissionResponseXmlWriter.Write(removePermissionResult); 
                            var removePermissionResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(removePermissionResponseContent)
                            };
                            removePermissionResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return removePermissionResponse;
                        }

                        case "SetEndpointAttributes":
                        {
                            var setEndpointAttributesRequest = new SetEndpointAttributesRequest
                            {
                                Attributes = query.ToFlatDictionary<System.String>("Attributes", v => v.ToString()),
                                EndpointArn = query.TryGetValue("EndpointArn", out var endpointArn) ? 
                                    endpointArn.ToString() : default!,
                            };
                            var setEndpointAttributesResult = await _innerClient.SetEndpointAttributesAsync(setEndpointAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var setEndpointAttributesResponseContent = SetEndpointAttributesResponseXmlWriter.Write(setEndpointAttributesResult); 
                            var setEndpointAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(setEndpointAttributesResponseContent)
                            };
                            setEndpointAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return setEndpointAttributesResponse;
                        }

                        case "SetPlatformApplicationAttributes":
                        {
                            var setPlatformApplicationAttributesRequest = new SetPlatformApplicationAttributesRequest
                            {
                                Attributes = query.ToFlatDictionary<System.String>("Attributes", v => v.ToString()),
                                PlatformApplicationArn = query.TryGetValue("PlatformApplicationArn", out var platformApplicationArn) ? 
                                    platformApplicationArn.ToString() : default!,
                            };
                            var setPlatformApplicationAttributesResult = await _innerClient.SetPlatformApplicationAttributesAsync(setPlatformApplicationAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var setPlatformApplicationAttributesResponseContent = SetPlatformApplicationAttributesResponseXmlWriter.Write(setPlatformApplicationAttributesResult); 
                            var setPlatformApplicationAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(setPlatformApplicationAttributesResponseContent)
                            };
                            setPlatformApplicationAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return setPlatformApplicationAttributesResponse;
                        }

                        case "SetSMSAttributes":
                        {
                            var setSMSAttributesRequest = new SetSMSAttributesRequest
                            {
                                Attributes = query.ToFlatDictionary<System.String>("Attributes", v => v.ToString()),
                            };
                            var setSMSAttributesResult = await _innerClient.SetSMSAttributesAsync(setSMSAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var setSMSAttributesResponseContent = SetSMSAttributesResponseXmlWriter.Write(setSMSAttributesResult); 
                            var setSMSAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(setSMSAttributesResponseContent)
                            };
                            setSMSAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return setSMSAttributesResponse;
                        }

                        case "SetSubscriptionAttributes":
                        {
                            var setSubscriptionAttributesRequest = new SetSubscriptionAttributesRequest
                            {
                                AttributeName = query.TryGetValue("AttributeName", out var attributeName) ? 
                                    attributeName.ToString() : default!,
                                AttributeValue = query.TryGetValue("AttributeValue", out var attributeValue) ? 
                                    attributeValue.ToString() : default!,
                                SubscriptionArn = query.TryGetValue("SubscriptionArn", out var subscriptionArn) ? 
                                    subscriptionArn.ToString() : default!,
                            };
                            var setSubscriptionAttributesResult = await _innerClient.SetSubscriptionAttributesAsync(setSubscriptionAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var setSubscriptionAttributesResponseContent = SetSubscriptionAttributesResponseXmlWriter.Write(setSubscriptionAttributesResult); 
                            var setSubscriptionAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(setSubscriptionAttributesResponseContent)
                            };
                            setSubscriptionAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return setSubscriptionAttributesResponse;
                        }

                        case "SetTopicAttributes":
                        {
                            var setTopicAttributesRequest = new SetTopicAttributesRequest
                            {
                                AttributeName = query.TryGetValue("AttributeName", out var attributeName) ? 
                                    attributeName.ToString() : default!,
                                AttributeValue = query.TryGetValue("AttributeValue", out var attributeValue) ? 
                                    attributeValue.ToString() : default!,
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var setTopicAttributesResult = await _innerClient.SetTopicAttributesAsync(setTopicAttributesRequest, cancellationToken).ConfigureAwait(false);
                            var setTopicAttributesResponseContent = SetTopicAttributesResponseXmlWriter.Write(setTopicAttributesResult); 
                            var setTopicAttributesResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(setTopicAttributesResponseContent)
                            };
                            setTopicAttributesResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return setTopicAttributesResponse;
                        }

                        case "Subscribe":
                        {
                            var subscribeRequest = new SubscribeRequest
                            {
                                Attributes = query.ToFlatDictionary<System.String>("Attributes", v => v.ToString()),
                                Endpoint = query.TryGetValue("Endpoint", out var endpoint) ? 
                                    endpoint.ToString() : default!,
                                Protocol = query.TryGetValue("Protocol", out var protocol) ? 
                                    protocol.ToString() : default!,
                                ReturnSubscriptionArn = query.TryGetValue("ReturnSubscriptionArn", out var returnSubscriptionArn) ? 
                                    bool.Parse(returnSubscriptionArn.ToString()) : default!,
                                TopicArn = query.TryGetValue("TopicArn", out var topicArn) ? 
                                    topicArn.ToString() : default!,
                            };
                            var subscribeResult = await _innerClient.SubscribeAsync(subscribeRequest, cancellationToken).ConfigureAwait(false);
                            var subscribeResponseContent = SubscribeResponseXmlWriter.Write(subscribeResult); 
                            var subscribeResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(subscribeResponseContent)
                            };
                            subscribeResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return subscribeResponse;
                        }

                        case "TagResource":
                        {
                            var tagResourceRequest = new TagResourceRequest
                            {
                                ResourceArn = query.TryGetValue("ResourceArn", out var resourceArn) ? 
                                    resourceArn.ToString() : default!,
                            Tags = query.Keys
                                .Where(k => k.StartsWith("Tags.member.", StringComparison.Ordinal))
                                .Select(k => int.Parse(k.Split('.')[2], CultureInfo.InvariantCulture))
                                .Distinct()
                                .Select(index => new Tag
                                {
                                    Key = query.TryGetValue($"Tags.member.{index}.Key", out var tagsKey) ? 
tagsKey.ToString() : default,
Value = query.TryGetValue($"Tags.member.{index}.Value", out var tagsValue) ? 
tagsValue.ToString() : default
                                })
                                .ToList(),
                            };
                            var tagResourceResult = await _innerClient.TagResourceAsync(tagResourceRequest, cancellationToken).ConfigureAwait(false);
                            var tagResourceResponseContent = TagResourceResponseXmlWriter.Write(tagResourceResult); 
                            var tagResourceResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(tagResourceResponseContent)
                            };
                            tagResourceResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return tagResourceResponse;
                        }

                        case "Unsubscribe":
                        {
                            var unsubscribeRequest = new UnsubscribeRequest
                            {
                                SubscriptionArn = query.TryGetValue("SubscriptionArn", out var subscriptionArn) ? 
                                    subscriptionArn.ToString() : default!,
                            };
                            var unsubscribeResult = await _innerClient.UnsubscribeAsync(unsubscribeRequest, cancellationToken).ConfigureAwait(false);
                            var unsubscribeResponseContent = UnsubscribeResponseXmlWriter.Write(unsubscribeResult); 
                            var unsubscribeResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(unsubscribeResponseContent)
                            };
                            unsubscribeResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return unsubscribeResponse;
                        }

                        case "UntagResource":
                        {
                            var untagResourceRequest = new UntagResourceRequest
                            {
                                ResourceArn = query.TryGetValue("ResourceArn", out var resourceArn) ? 
                                    resourceArn.ToString() : default!,
                                TagKeys = query.TryGetValue("TagKeys.member", out var tagKeysValues) ? 
                                    tagKeysValues.Select<string, System.String?>((string v) => v.ToString()).ToList() : null,
                            };
                            var untagResourceResult = await _innerClient.UntagResourceAsync(untagResourceRequest, cancellationToken).ConfigureAwait(false);
                            var untagResourceResponseContent = UntagResourceResponseXmlWriter.Write(untagResourceResult); 
                            var untagResourceResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(untagResourceResponseContent)
                            };
                            untagResourceResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return untagResourceResponse;
                        }

                        case "VerifySMSSandboxPhoneNumber":
                        {
                            var verifySMSSandboxPhoneNumberRequest = new VerifySMSSandboxPhoneNumberRequest
                            {
                                OneTimePassword = query.TryGetValue("OneTimePassword", out var oneTimePassword) ? 
                                    oneTimePassword.ToString() : default!,
                                PhoneNumber = query.TryGetValue("PhoneNumber", out var phoneNumber) ? 
                                    phoneNumber.ToString() : default!,
                            };
                            var verifySMSSandboxPhoneNumberResult = await _innerClient.VerifySMSSandboxPhoneNumberAsync(verifySMSSandboxPhoneNumberRequest, cancellationToken).ConfigureAwait(false);
                            var verifySMSSandboxPhoneNumberResponseContent = VerifySMSSandboxPhoneNumberResponseXmlWriter.Write(verifySMSSandboxPhoneNumberResult); 
                            var verifySMSSandboxPhoneNumberResponse = new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new StreamContent(verifySMSSandboxPhoneNumberResponseContent)
                            };
                            verifySMSSandboxPhoneNumberResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
                            return verifySMSSandboxPhoneNumberResponse;
                        }

                    }
                }
            }
            return new HttpResponseMessage();
        }
    }
}
