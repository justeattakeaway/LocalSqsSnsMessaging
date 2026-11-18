using System.Text.Json.Nodes;
using InMemoryAws.Sns.Model;
using Endpoint = InMemoryAws.Sns.Model.Endpoint;

namespace InMemoryAws;

/// <summary>
/// Mobile push: platform applications and their endpoints. The cloud keeps them and answers for them as
/// SNS does, but contacts no push service: credentials and device tokens are never checked, and
/// nothing is pushed to an endpoint.
/// </summary>
internal sealed partial class InternalSnsClient
{
    internal const string PlatformApplicationDoesNotExist = "PlatformApplication does not exist";
    internal const string EndpointDoesNotExist = "Endpoint does not exist";

    private static readonly HashSet<string> Platforms = new(StringComparer.Ordinal)
    {
        "ADM", "APNS", "APNS_SANDBOX", "APNS_VOIP", "APNS_VOIP_SANDBOX", "MACOS", "MACOS_SANDBOX", "GCM", "BAIDU", "MPNS", "WNS",
    };

    /// <summary>The application attributes that hold credentials, which SNS never reads back.</summary>
    private static readonly HashSet<string> CredentialAttributes = new(StringComparer.Ordinal) { "PlatformCredential", "PlatformPrincipal" };

    /// <summary>The application events that notify a topic, by attribute, with the event type each sends.</summary>
    private static readonly Dictionary<string, string> ApplicationEvents = new(StringComparer.Ordinal)
    {
        ["EventEndpointCreated"] = "EndpointCreated",
        ["EventEndpointDeleted"] = "EndpointDeleted",
        ["EventEndpointUpdated"] = "EndpointUpdated",
        ["EventDeliveryFailure"] = "DeliveryFailure",
    };

    private static readonly HashSet<string> ApplicationAttributeNames = new(StringComparer.Ordinal)
    {
        "PlatformCredential", "PlatformPrincipal", "EventEndpointCreated", "EventEndpointDeleted", "EventEndpointUpdated",
        "EventDeliveryFailure", "SuccessFeedbackRoleArn", "FailureFeedbackRoleArn", "SuccessFeedbackSampleRate",
        "ApplePlatformTeamID", "ApplePlatformBundleID", "AuthenticationMethod", "Enabled",
    };

    public Task<CreatePlatformApplicationResponse> CreatePlatformApplicationAsync(CreatePlatformApplicationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.Name))
        {
            throw new InternalInvalidParameterException("Invalid parameter: Name");
        }

        if (string.IsNullOrEmpty(request.Platform))
        {
            throw new InternalInvalidParameterException("Invalid parameter: Platform");
        }

        if (!Platforms.Contains(request.Platform!))
        {
            throw new InternalInvalidParameterException($"Invalid parameter: Platform Reason: {request.Platform} is not supported");
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { ["Enabled"] = "true" };
        foreach (var (name, value) in request.Attributes ?? [])
        {
            ValidateApplicationAttribute(name, value);
            if (!string.IsNullOrEmpty(value))
            {
                attributes[name] = value;
            }
        }

        var arn = $"arn:aws:sns:{_resources.Region}:{_cloud.CurrentAccountId}:app/{request.Platform}/{request.Name}";
        // Creating an application that exists again answers with it, keeping its endpoints.
        _resources.PlatformApplications.GetOrAdd(arn, _ => new SnsPlatformApplication(arn, request.Name!, request.Platform!, attributes));

        _cloud.RecordOperation(AwsServiceName.Sns, "CreatePlatformApplication", arn);
        return Task.FromResult(new CreatePlatformApplicationResponse { PlatformApplicationArn = arn }.SetCommonProperties());
    }

    public Task<GetPlatformApplicationAttributesResponse> GetPlatformApplicationAttributesAsync(GetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var application = GetPlatformApplication(request.PlatformApplicationArn);
        Dictionary<string, string> attributes;
        lock (application.Gate)
        {
            attributes = application.Attributes
                .Where(a => !CredentialAttributes.Contains(a.Key))
                .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        }

        _cloud.RecordOperation(AwsServiceName.Sns, "GetPlatformApplicationAttributes", application.Arn);
        return Task.FromResult(new GetPlatformApplicationAttributesResponse { Attributes = attributes }.SetCommonProperties());
    }

    public Task<SetPlatformApplicationAttributesResponse> SetPlatformApplicationAttributesAsync(SetPlatformApplicationAttributesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var application = GetPlatformApplication(request.PlatformApplicationArn);
        var requested = request.Attributes ?? [];
        foreach (var (name, value) in requested)
        {
            ValidateApplicationAttribute(name, value);
        }

        lock (application.Gate)
        {
            foreach (var (name, value) in requested)
            {
                // SNS accepts Enabled here but keeps the application's own: it turns an application
                // off itself, when the push service refuses its credentials.
                if (name == "Enabled")
                {
                    continue;
                }

                if (string.IsNullOrEmpty(value))
                {
                    application.Attributes.Remove(name);
                }
                else
                {
                    application.Attributes[name] = value;
                }
            }
        }

        _cloud.RecordOperation(AwsServiceName.Sns, "SetPlatformApplicationAttributes", application.Arn);
        return Task.FromResult(new SetPlatformApplicationAttributesResponse().SetCommonProperties());
    }

    public Task<DeletePlatformApplicationResponse> DeletePlatformApplicationAsync(DeletePlatformApplicationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Deleting an application that doesn't exist succeeds, as on AWS. Its endpoints go with it.
        SnsArn.Parse(request.PlatformApplicationArn, "PlatformApplicationArn");
        _resources.PlatformApplications.TryRemove(request.PlatformApplicationArn!, out _);

        _cloud.RecordOperation(AwsServiceName.Sns, "DeletePlatformApplication", request.PlatformApplicationArn);
        return Task.FromResult(new DeletePlatformApplicationResponse().SetCommonProperties());
    }

    public Task<ListPlatformApplicationsResponse> ListPlatformApplicationsAsync(ListPlatformApplicationsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (arns, nextToken) = SnsPagination.Page(_resources.PlatformApplications.Keys, request.NextToken,
            invalidTokenMessage: "Invalid parameter: NextToken Reason: token is invalid or expired");
        var applications = new List<PlatformApplication>();
        foreach (var arn in arns)
        {
            if (_resources.PlatformApplications.TryGetValue(arn, out var application))
            {
                lock (application.Gate)
                {
                    applications.Add(new PlatformApplication
                    {
                        PlatformApplicationArn = arn,
                        Attributes = application.Attributes
                            .Where(a => !CredentialAttributes.Contains(a.Key))
                            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
                    });
                }
            }
        }

        _cloud.RecordOperation(AwsServiceName.Sns, "ListPlatformApplications");
        return Task.FromResult(new ListPlatformApplicationsResponse { PlatformApplications = applications, NextToken = nextToken }.SetCommonProperties());
    }

    public Task<CreatePlatformEndpointResponse> CreatePlatformEndpointAsync(CreatePlatformEndpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var application = GetPlatformApplication(request.PlatformApplicationArn);
        if (string.IsNullOrEmpty(request.Token))
        {
            throw new InternalInvalidParameterException("Invalid parameter: Token Reason: cannot be empty");
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Enabled"] = "true",
            ["Token"] = request.Token!,
        };
        if (!string.IsNullOrEmpty(request.CustomUserData))
        {
            attributes["CustomUserData"] = request.CustomUserData!;
        }

        foreach (var (name, value) in request.Attributes ?? [])
        {
            attributes[name] = value;
        }

        SnsPlatformEndpoint endpoint;
        bool created;
        lock (application.Gate)
        {
            // The same token again answers with its endpoint, unless the request asks for other attributes.
            var existing = application.Endpoints.Values.FirstOrDefault(e => e.Attributes.GetValueOrDefault("Token") == request.Token);
            if (existing is not null)
            {
                if (attributes.Any(a => existing.Attributes.GetValueOrDefault(a.Key) != a.Value))
                {
                    throw new InternalInvalidParameterException(
                        $"Invalid parameter: Token Reason: Endpoint {existing.Arn} already exists with the same Token, but different attributes.");
                }

                endpoint = existing;
                created = false;
            }
            else
            {
                var arn = $"arn:aws:sns:{_resources.Region}:{_cloud.CurrentAccountId}:endpoint/{application.Platform}/{application.Name}/{Guid.NewGuid():D}";
                endpoint = new SnsPlatformEndpoint(arn, application, attributes);
                application.Endpoints.Add(arn, endpoint);
                created = true;
            }
        }

        if (created)
        {
            NotifyApplicationEvent(application, "EventEndpointCreated", endpoint);
        }

        _cloud.RecordOperation(AwsServiceName.Sns, "CreatePlatformEndpoint", endpoint.Arn);
        return Task.FromResult(new CreatePlatformEndpointResponse { EndpointArn = endpoint.Arn }.SetCommonProperties());
    }

    public Task<GetEndpointAttributesResponse> GetEndpointAttributesAsync(GetEndpointAttributesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = GetPlatformEndpoint(request.EndpointArn);
        Dictionary<string, string> attributes;
        lock (endpoint.Application.Gate)
        {
            attributes = new Dictionary<string, string>(endpoint.Attributes, StringComparer.Ordinal);
        }

        _cloud.RecordOperation(AwsServiceName.Sns, "GetEndpointAttributes", endpoint.Arn);
        return Task.FromResult(new GetEndpointAttributesResponse { Attributes = attributes }.SetCommonProperties());
    }

    public Task<SetEndpointAttributesResponse> SetEndpointAttributesAsync(SetEndpointAttributesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = GetPlatformEndpoint(request.EndpointArn);
        lock (endpoint.Application.Gate)
        {
            foreach (var (name, value) in request.Attributes ?? [])
            {
                endpoint.Attributes[name] = value;
            }
        }

        NotifyApplicationEvent(endpoint.Application, "EventEndpointUpdated", endpoint);
        _cloud.RecordOperation(AwsServiceName.Sns, "SetEndpointAttributes", endpoint.Arn);
        return Task.FromResult(new SetEndpointAttributesResponse().SetCommonProperties());
    }

    public Task<DeleteEndpointResponse> DeleteEndpointAsync(DeleteEndpointRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Deleting an endpoint that doesn't exist succeeds, as on AWS.
        SnsArn.Parse(request.EndpointArn, "EndpointArn");
        if (TryGetPlatformEndpoint(request.EndpointArn!, out var endpoint))
        {
            bool removed;
            lock (endpoint.Application.Gate)
            {
                removed = endpoint.Application.Endpoints.Remove(endpoint.Arn);
            }

            if (removed)
            {
                NotifyApplicationEvent(endpoint.Application, "EventEndpointDeleted", endpoint);
            }
        }

        _cloud.RecordOperation(AwsServiceName.Sns, "DeleteEndpoint", request.EndpointArn);
        return Task.FromResult(new DeleteEndpointResponse().SetCommonProperties());
    }

    public Task<ListEndpointsByPlatformApplicationResponse> ListEndpointsByPlatformApplicationAsync(ListEndpointsByPlatformApplicationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var application = GetPlatformApplication(request.PlatformApplicationArn);
        List<Endpoint> endpoints;
        string? nextToken;
        lock (application.Gate)
        {
            (var arns, nextToken) = SnsPagination.Page(application.Endpoints.Keys, request.NextToken,
                invalidTokenMessage: "Invalid parameter: NextToken Reason: token is invalid or expired");
            endpoints = arns.Select(arn => new Endpoint
            {
                EndpointArn = arn,
                Attributes = new Dictionary<string, string>(application.Endpoints[arn].Attributes, StringComparer.Ordinal),
            }).ToList();
        }

        _cloud.RecordOperation(AwsServiceName.Sns, "ListEndpointsByPlatformApplication", application.Arn);
        return Task.FromResult(new ListEndpointsByPlatformApplicationResponse { Endpoints = endpoints, NextToken = nextToken }.SetCommonProperties());
    }

    /// <summary>Refuses an attribute SNS doesn't know for a platform application, or an event topic that isn't one.</summary>
    private void ValidateApplicationAttribute(string name, string? value)
    {
        if (!ApplicationAttributeNames.Contains(name))
        {
            throw new InternalInvalidParameterException($"Invalid parameter: Attributes Reason: Invalid attribute name: {name}");
        }

        if (!ApplicationEvents.ContainsKey(name) || string.IsNullOrEmpty(value))
        {
            return;
        }

        var parts = value!.Split(':');
        if (parts.Length != 6 || parts[0] != "arn" || parts[2] != "sns")
        {
            throw new InternalInvalidParameterException($"Invalid parameter: Attributes Reason: {name} has invalid ARN format");
        }

        if (!_cloud.TryGetTopicByArn(value, out _))
        {
            throw new InternalInvalidParameterException($"Invalid parameter: Attributes Reason: {name} is not an existing topic");
        }
    }

    /// <summary>
    /// Publishes an application event (<c>EndpointCreated</c>, ...) about <paramref name="endpoint"/> to
    /// the topic the application's <paramref name="attribute"/> names, if it names one that exists.
    /// </summary>
    private void NotifyApplicationEvent(SnsPlatformApplication application, string attribute, SnsPlatformEndpoint endpoint)
    {
        string? topicArn;
        lock (application.Gate)
        {
            topicArn = application.Attributes.GetValueOrDefault(attribute);
        }

        if (topicArn is null || !_cloud.TryGetTopicByArn(topicArn, out var topic))
        {
            return;
        }

        var message = new JsonObject
        {
            ["EventType"] = ApplicationEvents[attribute],
            ["Resource"] = application.Arn,
            ["EndpointArn"] = endpoint.Arn,
        }.ToJsonString();
        topic.Publish(new PublishRequest { TopicArn = topic.Arn, Message = message });
    }

    private SnsPlatformApplication GetPlatformApplication(string? arn)
    {
        SnsArn.Parse(arn, "PlatformApplicationArn");
        return _resources.PlatformApplications.TryGetValue(arn!, out var application)
            ? application
            : throw new InternalNotFoundException(PlatformApplicationDoesNotExist);
    }

    private SnsPlatformEndpoint GetPlatformEndpoint(string? arn)
    {
        SnsArn.Parse(arn, "EndpointArn");
        return TryGetPlatformEndpoint(arn!, out var endpoint)
            ? endpoint
            : throw new InternalNotFoundException(EndpointDoesNotExist);
    }

    /// <summary>
    /// The endpoint <paramref name="arn"/> names. Its application is the one the ARN's
    /// <c>endpoint/{platform}/{application}/{id}</c> resource names.
    /// </summary>
    private bool TryGetPlatformEndpoint(string arn, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SnsPlatformEndpoint? endpoint)
    {
        endpoint = null;
        var parts = arn.Split(':');
        var resource = parts.Length == 6 ? parts[5].Split('/') : [];
        if (resource is not ["endpoint", var platform, var name, _])
        {
            return false;
        }

        var applicationArn = $"arn:aws:sns:{parts[3]}:{parts[4]}:app/{platform}/{name}";
        if (!_resources.PlatformApplications.TryGetValue(applicationArn, out var application))
        {
            return false;
        }

        lock (application.Gate)
        {
            return application.Endpoints.TryGetValue(arn, out endpoint);
        }
    }
}
