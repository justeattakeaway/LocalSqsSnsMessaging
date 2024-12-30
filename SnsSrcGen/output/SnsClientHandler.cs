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
            if (request.Content is ByteArrayContent byteArrayContent)
            {
                var contentString = await byteArrayContent.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var query = QueryHelpers.ParseQuery(contentString);
                if (query.TryGetValue("Action", out var action))
                {
                    switch (action)
                    {
case "CheckIfPhoneNumberIsOptedOut":
{
    var requestModel = new CheckIfPhoneNumberIsOptedOutRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PhoneNumber", out var phoneNumberValue))
    {
        requestModel.PhoneNumber = phoneNumberValue.ToString();
    }

    var result = await _innerClient.CheckIfPhoneNumberIsOptedOutAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = CheckIfPhoneNumberIsOptedOutResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ConfirmSubscription":
{
    var requestModel = new ConfirmSubscriptionRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("TopicArn", out var topicArnValue))
    {
        requestModel.TopicArn = topicArnValue.ToString();
    }
    if (query.TryGetValue("Token", out var tokenValue))
    {
        requestModel.Token = tokenValue.ToString();
    }
    if (query.TryGetValue("AuthenticateOnUnsubscribe", out var authenticateOnUnsubscribeValue))
    {
        requestModel.AuthenticateOnUnsubscribe = authenticateOnUnsubscribeValue.ToString();
    }

    var result = await _innerClient.ConfirmSubscriptionAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ConfirmSubscriptionResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "CreatePlatformApplication":
{
    var requestModel = new CreatePlatformApplicationRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("Name", out var nameValue))
    {
        requestModel.Name = nameValue.ToString();
    }
    if (query.TryGetValue("Platform", out var platformValue))
    {
        requestModel.Platform = platformValue.ToString();
    }
    // Handle map type Attributes
    {
        var attributesMap = query.ToFlatDictionary(
            "Attributes", 
            v => v.ToString());
        
        if (attributesMap.Count > 0)
        {
            requestModel.Attributes = attributesMap;
        }
    }

    var result = await _innerClient.CreatePlatformApplicationAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = CreatePlatformApplicationResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "CreatePlatformEndpoint":
{
    var requestModel = new CreatePlatformEndpointRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PlatformApplicationArn", out var platformApplicationArnValue))
    {
        requestModel.PlatformApplicationArn = platformApplicationArnValue.ToString();
    }
    if (query.TryGetValue("Token", out var tokenValue))
    {
        requestModel.Token = tokenValue.ToString();
    }
    if (query.TryGetValue("CustomUserData", out var customUserDataValue))
    {
        requestModel.CustomUserData = customUserDataValue.ToString();
    }
    // Handle map type Attributes
    {
        var attributesMap = query.ToFlatDictionary(
            "Attributes", 
            v => v.ToString());
        
        if (attributesMap.Count > 0)
        {
            requestModel.Attributes = attributesMap;
        }
    }

    var result = await _innerClient.CreatePlatformEndpointAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = CreatePlatformEndpointResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "CreateSMSSandboxPhoneNumber":
{
    var requestModel = new CreateSMSSandboxPhoneNumberRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PhoneNumber", out var phoneNumberValue))
    {
        requestModel.PhoneNumber = phoneNumberValue.ToString();
    }
    if (query.TryGetValue("LanguageCode", out var languageCodeValue))
    {
        requestModel.LanguageCode = languageCodeValue.ToString();
    }

    var result = await _innerClient.CreateSMSSandboxPhoneNumberAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = CreateSMSSandboxPhoneNumberResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "CreateTopic":
{
    var requestModel = new CreateTopicRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("Name", out var nameValue))
    {
        requestModel.Name = nameValue.ToString();
    }
    // Handle map type Attributes
    {
        var attributesMap = query.ToFlatDictionary(
            "Attributes", 
            v => v.ToString());
        
        if (attributesMap.Count > 0)
        {
            requestModel.Attributes = attributesMap;
        }
    }
    // Handle list of structures
    {
        var tagsList = new List<Tag>();
        var prefix = "Tags.Entry.";
        var entries = query.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k.Substring(prefix.Length))
            .Select(k => k.Split('.')[0])
            .Distinct()
            .OrderBy(x => int.Parse(x))
            .ToList();

        foreach (var index in entries)
        {
            var item = new Tag();
                if (query.TryGetValue($"Tags.Entry.{index}.Key", out var keyValue))
    {
        item.Key = keyValue.ToString();
    }
    if (query.TryGetValue($"Tags.Entry.{index}.Value", out var valueValue))
    {
        item.Value = valueValue.ToString();
    }

            tagsList.Add(item);
        }

        if (tagsList.Count > 0)
        {
            requestModel.Tags = tagsList;
        }
    }
    if (query.TryGetValue("DataProtectionPolicy", out var dataProtectionPolicyValue))
    {
        requestModel.DataProtectionPolicy = dataProtectionPolicyValue.ToString();
    }

    var result = await _innerClient.CreateTopicAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = CreateTopicResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "DeleteSMSSandboxPhoneNumber":
{
    var requestModel = new DeleteSMSSandboxPhoneNumberRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PhoneNumber", out var phoneNumberValue))
    {
        requestModel.PhoneNumber = phoneNumberValue.ToString();
    }

    var result = await _innerClient.DeleteSMSSandboxPhoneNumberAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = DeleteSMSSandboxPhoneNumberResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "GetDataProtectionPolicy":
{
    var requestModel = new GetDataProtectionPolicyRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("ResourceArn", out var resourceArnValue))
    {
        requestModel.ResourceArn = resourceArnValue.ToString();
    }

    var result = await _innerClient.GetDataProtectionPolicyAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = GetDataProtectionPolicyResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "GetEndpointAttributes":
{
    var requestModel = new GetEndpointAttributesRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("EndpointArn", out var endpointArnValue))
    {
        requestModel.EndpointArn = endpointArnValue.ToString();
    }

    var result = await _innerClient.GetEndpointAttributesAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = GetEndpointAttributesResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "GetPlatformApplicationAttributes":
{
    var requestModel = new GetPlatformApplicationAttributesRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PlatformApplicationArn", out var platformApplicationArnValue))
    {
        requestModel.PlatformApplicationArn = platformApplicationArnValue.ToString();
    }

    var result = await _innerClient.GetPlatformApplicationAttributesAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = GetPlatformApplicationAttributesResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "GetSMSAttributes":
{
    var requestModel = new GetSMSAttributesRequest();
    // Map query parameters to request properties based on schema
        // Handle simple list
    {
        var attributesList = new List<string>();
        var prefix = "Attributes.member.";
        
        foreach (var kvp in query.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            attributesList.Add(kvp.Value.ToString());
        }
        
        if (attributesList.Count > 0)
        {
            requestModel.Attributes = attributesList;
        }
    }

    var result = await _innerClient.GetSMSAttributesAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = GetSMSAttributesResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "GetSMSSandboxAccountStatus":
{
    var requestModel = new GetSMSSandboxAccountStatusRequest();
    // Map query parameters to request properties based on schema
    
    var result = await _innerClient.GetSMSSandboxAccountStatusAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = GetSMSSandboxAccountStatusResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "GetSubscriptionAttributes":
{
    var requestModel = new GetSubscriptionAttributesRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("SubscriptionArn", out var subscriptionArnValue))
    {
        requestModel.SubscriptionArn = subscriptionArnValue.ToString();
    }

    var result = await _innerClient.GetSubscriptionAttributesAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = GetSubscriptionAttributesResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "GetTopicAttributes":
{
    var requestModel = new GetTopicAttributesRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("TopicArn", out var topicArnValue))
    {
        requestModel.TopicArn = topicArnValue.ToString();
    }

    var result = await _innerClient.GetTopicAttributesAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = GetTopicAttributesResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListEndpointsByPlatformApplication":
{
    var requestModel = new ListEndpointsByPlatformApplicationRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PlatformApplicationArn", out var platformApplicationArnValue))
    {
        requestModel.PlatformApplicationArn = platformApplicationArnValue.ToString();
    }
    if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }

    var result = await _innerClient.ListEndpointsByPlatformApplicationAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListEndpointsByPlatformApplicationResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListOriginationNumbers":
{
    var requestModel = new ListOriginationNumbersRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }
    if (query.TryGetValue("MaxResults", out var maxResultsValue))
    {
        requestModel.MaxResults = long.Parse(maxResultsValue.ToString(), CultureInfo.InvariantCulture);
    }

    var result = await _innerClient.ListOriginationNumbersAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListOriginationNumbersResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListPhoneNumbersOptedOut":
{
    var requestModel = new ListPhoneNumbersOptedOutRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }

    var result = await _innerClient.ListPhoneNumbersOptedOutAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListPhoneNumbersOptedOutResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListPlatformApplications":
{
    var requestModel = new ListPlatformApplicationsRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }

    var result = await _innerClient.ListPlatformApplicationsAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListPlatformApplicationsResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListSMSSandboxPhoneNumbers":
{
    var requestModel = new ListSMSSandboxPhoneNumbersRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }
    if (query.TryGetValue("MaxResults", out var maxResultsValue))
    {
        requestModel.MaxResults = long.Parse(maxResultsValue.ToString(), CultureInfo.InvariantCulture);
    }

    var result = await _innerClient.ListSMSSandboxPhoneNumbersAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListSMSSandboxPhoneNumbersResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListSubscriptions":
{
    var requestModel = new ListSubscriptionsRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }

    var result = await _innerClient.ListSubscriptionsAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListSubscriptionsResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListSubscriptionsByTopic":
{
    var requestModel = new ListSubscriptionsByTopicRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("TopicArn", out var topicArnValue))
    {
        requestModel.TopicArn = topicArnValue.ToString();
    }
    if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }

    var result = await _innerClient.ListSubscriptionsByTopicAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListSubscriptionsByTopicResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListTagsForResource":
{
    var requestModel = new ListTagsForResourceRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("ResourceArn", out var resourceArnValue))
    {
        requestModel.ResourceArn = resourceArnValue.ToString();
    }

    var result = await _innerClient.ListTagsForResourceAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListTagsForResourceResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "ListTopics":
{
    var requestModel = new ListTopicsRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("NextToken", out var nextTokenValue))
    {
        requestModel.NextToken = nextTokenValue.ToString();
    }

    var result = await _innerClient.ListTopicsAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = ListTopicsResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "OptInPhoneNumber":
{
    var requestModel = new OptInPhoneNumberRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PhoneNumber", out var phoneNumberValue))
    {
        requestModel.PhoneNumber = phoneNumberValue.ToString();
    }

    var result = await _innerClient.OptInPhoneNumberAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = OptInPhoneNumberResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "Publish":
{
    var requestModel = new PublishRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("TopicArn", out var topicArnValue))
    {
        requestModel.TopicArn = topicArnValue.ToString();
    }
    if (query.TryGetValue("TargetArn", out var targetArnValue))
    {
        requestModel.TargetArn = targetArnValue.ToString();
    }
    if (query.TryGetValue("PhoneNumber", out var phoneNumberValue))
    {
        requestModel.PhoneNumber = phoneNumberValue.ToString();
    }
    if (query.TryGetValue("Message", out var messageValue))
    {
        requestModel.Message = messageValue.ToString();
    }
    if (query.TryGetValue("Subject", out var subjectValue))
    {
        requestModel.Subject = subjectValue.ToString();
    }
    if (query.TryGetValue("MessageStructure", out var messageStructureValue))
    {
        requestModel.MessageStructure = messageStructureValue.ToString();
    }
    // Handle map type MessageAttributes
    {
        var messageAttributesMap = query.ToFlatDictionary(
            "MessageAttributes", 
            v => new MessageAttributeValue(v.ToString()));
        
        if (messageAttributesMap.Count > 0)
        {
            requestModel.MessageAttributes = messageAttributesMap;
        }
    }
    if (query.TryGetValue("MessageDeduplicationId", out var messageDeduplicationIdValue))
    {
        requestModel.MessageDeduplicationId = messageDeduplicationIdValue.ToString();
    }
    if (query.TryGetValue("MessageGroupId", out var messageGroupIdValue))
    {
        requestModel.MessageGroupId = messageGroupIdValue.ToString();
    }

    var result = await _innerClient.PublishAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = PublishResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "PublishBatch":
{
    var requestModel = new PublishBatchRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("TopicArn", out var topicArnValue))
    {
        requestModel.TopicArn = topicArnValue.ToString();
    }
    // Handle list of structures
    {
        var publishBatchRequestEntriesList = new List<PublishBatchRequestEntry>();
        var prefix = "PublishBatchRequestEntries.Entry.";
        var entries = query.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k.Substring(prefix.Length))
            .Select(k => k.Split('.')[0])
            .Distinct()
            .OrderBy(x => int.Parse(x))
            .ToList();

        foreach (var index in entries)
        {
            var item = new PublishBatchRequestEntry();
                if (query.TryGetValue($"PublishBatchRequestEntries.Entry.{index}.Id", out var idValue))
    {
        item.Id = idValue.ToString();
    }
    if (query.TryGetValue($"PublishBatchRequestEntries.Entry.{index}.Message", out var messageValue))
    {
        item.Message = messageValue.ToString();
    }
    if (query.TryGetValue($"PublishBatchRequestEntries.Entry.{index}.Subject", out var subjectValue))
    {
        item.Subject = subjectValue.ToString();
    }
    if (query.TryGetValue($"PublishBatchRequestEntries.Entry.{index}.MessageStructure", out var messageStructureValue))
    {
        item.MessageStructure = messageStructureValue.ToString();
    }
    if (query.TryGetValue($"PublishBatchRequestEntries.Entry.{index}.MessageAttributes", out var messageAttributesValue))
    {
        item.MessageAttributes = messageAttributesValue.ToString();
    }
    if (query.TryGetValue($"PublishBatchRequestEntries.Entry.{index}.MessageDeduplicationId", out var messageDeduplicationIdValue))
    {
        item.MessageDeduplicationId = messageDeduplicationIdValue.ToString();
    }
    if (query.TryGetValue($"PublishBatchRequestEntries.Entry.{index}.MessageGroupId", out var messageGroupIdValue))
    {
        item.MessageGroupId = messageGroupIdValue.ToString();
    }

            publishBatchRequestEntriesList.Add(item);
        }

        if (publishBatchRequestEntriesList.Count > 0)
        {
            requestModel.PublishBatchRequestEntries = publishBatchRequestEntriesList;
        }
    }

    var result = await _innerClient.PublishBatchAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = PublishBatchResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "SetSMSAttributes":
{
    var requestModel = new SetSMSAttributesRequest();
    // Map query parameters to request properties based on schema
        // Handle map type Attributes
    {
        var attributesMap = query.ToFlatDictionary(
            "Attributes", 
            v => v.ToString());
        
        if (attributesMap.Count > 0)
        {
            requestModel.Attributes = attributesMap;
        }
    }

    var result = await _innerClient.SetSMSAttributesAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = SetSMSAttributesResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "Subscribe":
{
    var requestModel = new SubscribeRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("TopicArn", out var topicArnValue))
    {
        requestModel.TopicArn = topicArnValue.ToString();
    }
    if (query.TryGetValue("Protocol", out var protocolValue))
    {
        requestModel.Protocol = protocolValue.ToString();
    }
    if (query.TryGetValue("Endpoint", out var endpointValue))
    {
        requestModel.Endpoint = endpointValue.ToString();
    }
    // Handle map type Attributes
    {
        var attributesMap = query.ToFlatDictionary(
            "Attributes", 
            v => v.ToString());
        
        if (attributesMap.Count > 0)
        {
            requestModel.Attributes = attributesMap;
        }
    }
    if (query.TryGetValue("ReturnSubscriptionArn", out var returnSubscriptionArnValue))
    {
        requestModel.ReturnSubscriptionArn = bool.Parse(returnSubscriptionArnValue.ToString());
    }

    var result = await _innerClient.SubscribeAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = SubscribeResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "TagResource":
{
    var requestModel = new TagResourceRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("ResourceArn", out var resourceArnValue))
    {
        requestModel.ResourceArn = resourceArnValue.ToString();
    }
    // Handle list of structures
    {
        var tagsList = new List<Tag>();
        var prefix = "Tags.Entry.";
        var entries = query.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .Select(k => k.Substring(prefix.Length))
            .Select(k => k.Split('.')[0])
            .Distinct()
            .OrderBy(x => int.Parse(x))
            .ToList();

        foreach (var index in entries)
        {
            var item = new Tag();
                if (query.TryGetValue($"Tags.Entry.{index}.Key", out var keyValue))
    {
        item.Key = keyValue.ToString();
    }
    if (query.TryGetValue($"Tags.Entry.{index}.Value", out var valueValue))
    {
        item.Value = valueValue.ToString();
    }

            tagsList.Add(item);
        }

        if (tagsList.Count > 0)
        {
            requestModel.Tags = tagsList;
        }
    }

    var result = await _innerClient.TagResourceAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = TagResourceResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "UntagResource":
{
    var requestModel = new UntagResourceRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("ResourceArn", out var resourceArnValue))
    {
        requestModel.ResourceArn = resourceArnValue.ToString();
    }
    // Handle simple list
    {
        var tagKeysList = new List<string>();
        var prefix = "TagKeys.member.";
        
        foreach (var kvp in query.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            tagKeysList.Add(kvp.Value.ToString());
        }
        
        if (tagKeysList.Count > 0)
        {
            requestModel.TagKeys = tagKeysList;
        }
    }

    var result = await _innerClient.UntagResourceAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = UntagResourceResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
case "VerifySMSSandboxPhoneNumber":
{
    var requestModel = new VerifySMSSandboxPhoneNumberRequest();
    // Map query parameters to request properties based on schema
        if (query.TryGetValue("PhoneNumber", out var phoneNumberValue))
    {
        requestModel.PhoneNumber = phoneNumberValue.ToString();
    }
    if (query.TryGetValue("OneTimePassword", out var oneTimePasswordValue))
    {
        requestModel.OneTimePassword = oneTimePasswordValue.ToString();
    }

    var result = await _innerClient.VerifySMSSandboxPhoneNumberAsync(requestModel, cancellationToken).ConfigureAwait(false);
    var content = VerifySMSSandboxPhoneNumberResponseXmlWriter.Write(result);
    
    var response = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(content)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
    return response;
}
                    }
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
