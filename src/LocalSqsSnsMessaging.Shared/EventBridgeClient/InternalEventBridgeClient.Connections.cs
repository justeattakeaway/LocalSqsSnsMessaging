using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

// Connections (CreateConnection, DescribeConnection, UpdateConnection, DeauthorizeConnection,
// DeleteConnection, ListConnections) and API destinations (CreateApiDestination,
// DescribeApiDestination, UpdateApiDestination, DeleteApiDestination, ListApiDestinations).
// Delivery to API destinations is in EventBridgeEventDelivery.ApiDestinations.cs.
internal sealed partial class InternalEventBridgeClient
{
    private const string ResourceNamePattern = @"[\.\-_A-Za-z0-9]+";
    private const string ConnectionArnPattern = @"^arn:aws([a-z]|\-)*:events:([a-z]|\d|\-)*:([0-9]{12})?:connection\/[\.\-_A-Za-z0-9]+\/[\-A-Za-z0-9]+$";
    private const string HttpsEndpointPattern = @"^((%[0-9A-Fa-f]{2}|[-()_.!~*';/?:@\x26=+$,A-Za-z0-9])+)([).!';/?:,])?$";

    private static readonly string[] ApiDestinationHttpMethods = ["POST", "GET", "HEAD", "OPTIONS", "PUT", "PATCH", "DELETE"];
    private static readonly string[] ConnectionAuthorizationTypes = [ConnectionResource.Basic, ConnectionResource.OAuthClientCredentials, ConnectionResource.ApiKey];
    private static readonly string[] ConnectionStates = ["CREATING", "UPDATING", "DELETING", "AUTHORIZED", "DEAUTHORIZED", "AUTHORIZING", "DEAUTHORIZING", "ACTIVE", "FAILED_CONNECTIVITY"];

    public Task<CreateConnectionResponse> CreateConnectionAsync(CreateConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", ResourceNamePattern, maxLength: 64);
        CheckEnum(violations, request.AuthorizationType, "authorizationType", ConnectionAuthorizationTypes, required: true);
        if (request.AuthParameters is null)
        {
            violations.Add(NullViolation("authParameters"));
        }

        ThrowIfViolations(violations);

        var parameters = request.AuthParameters!;
        var given = new[]
        {
            parameters.ApiKeyAuthParameters is null ? null : ConnectionResource.ApiKey,
            parameters.BasicAuthParameters is null ? null : ConnectionResource.Basic,
            parameters.OAuthParameters is null ? null : ConnectionResource.OAuthClientCredentials,
        }.Where(type => type is not null).ToList();
        if (given.Count != 1 || given[0] != request.AuthorizationType)
        {
            throw new InternalValidationException(
                "Failed to create the connection(s). Parameter AuthParameters is not valid. Reason: " +
                "The value for AuthParameters must include exactly one authorization type.");
        }

        var auth = new ConnectionAuth
        {
            ApiKeyName = parameters.ApiKeyAuthParameters?.ApiKeyName,
            ApiKeyValue = parameters.ApiKeyAuthParameters?.ApiKeyValue,
            Username = parameters.BasicAuthParameters?.Username,
            Password = parameters.BasicAuthParameters?.Password,
            ClientId = parameters.OAuthParameters?.ClientParameters?.ClientID,
            ClientSecret = parameters.OAuthParameters?.ClientParameters?.ClientSecret,
            AuthorizationEndpoint = parameters.OAuthParameters?.AuthorizationEndpoint,
            OAuthHttpMethod = parameters.OAuthParameters?.HttpMethod,
            OAuthHttpParameters = ConnectionAuth.Copy(parameters.OAuthParameters?.OAuthHttpParameters),
            InvocationHttpParameters = ConnectionAuth.Copy(parameters.InvocationHttpParameters),
        };
        CheckAuthorizationEndpoint("create", auth);

        var now = _bus.TimeProvider.GetUtcNow();
        var id = Guid.NewGuid().ToString();
        var connection = new ConnectionResource
        {
            Name = request.Name!,
            Arn = $"arn:aws:events:{_resources.Region}:{_bus.CurrentAccountId}:connection/{request.Name}/{id}",
            SecretArn = $"arn:aws:secretsmanager:{_resources.Region}:{_bus.CurrentAccountId}:secret:events!connection/{request.Name}/{id}-{id.Substring(0, 6)}",
            CreationTime = now,
            Description = NullIfEmpty(request.Description),
            KmsKeyIdentifier = NullIfEmpty(request.KmsKeyIdentifier),
            AuthorizationType = request.AuthorizationType!,
            Auth = auth,
            State = ConnectionResource.Authorizing,
            LastModifiedTime = now,
        };
        if (!_resources.EventBridgeConnections.TryAdd(connection.Name, connection))
        {
            throw new InternalResourceAlreadyExistsException($"Failed to create the connection(s). Connection '{connection.Name}' already exists.");
        }

        string state;
        lock (connection.Gate)
        {
            state = StartAuthorization(connection);
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "CreateConnection", connection.Arn);
        return Task.FromResult(new CreateConnectionResponse
        {
            ConnectionArn = connection.Arn,
            ConnectionState = state,
            CreationTime = now.UtcDateTime,
            LastModifiedTime = now.UtcDateTime
        }.SetCommonProperties());
    }

    public Task<DescribeConnectionResponse> DescribeConnectionAsync(DescribeConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = GetConnection(request.Name, "describe");
        DescribeConnectionResponse response;
        lock (connection.Gate)
        {
            var auth = connection.Auth;
            response = new DescribeConnectionResponse
            {
                ConnectionArn = connection.Arn,
                Name = connection.Name,
                Description = connection.Description,
                ConnectionState = connection.State,
                StateReason = connection.StateReason,
                AuthorizationType = connection.AuthorizationType,
                SecretArn = connection.SecretArn,
                KmsKeyIdentifier = connection.KmsKeyIdentifier,
                AuthParameters = new ConnectionAuthResponseParameters
                {
                    ApiKeyAuthParameters = connection.AuthorizationType == ConnectionResource.ApiKey
                        ? new ConnectionApiKeyAuthResponseParameters { ApiKeyName = auth.ApiKeyName }
                        : null,
                    BasicAuthParameters = connection.AuthorizationType == ConnectionResource.Basic
                        ? new ConnectionBasicAuthResponseParameters { Username = auth.Username }
                        : null,
                    OAuthParameters = connection.AuthorizationType == ConnectionResource.OAuthClientCredentials
                        ? new ConnectionOAuthResponseParameters
                        {
                            ClientParameters = new ConnectionOAuthClientResponseParameters { ClientID = auth.ClientId },
                            AuthorizationEndpoint = auth.AuthorizationEndpoint,
                            HttpMethod = auth.OAuthHttpMethod,
                            OAuthHttpParameters = ConnectionAuth.Redact(auth.OAuthHttpParameters),
                        }
                        : null,
                    InvocationHttpParameters = ConnectionAuth.Redact(auth.InvocationHttpParameters),
                },
                CreationTime = connection.CreationTime.UtcDateTime,
                LastModifiedTime = connection.LastModifiedTime.UtcDateTime,
                LastAuthorizedTime = connection.LastAuthorizedTime?.UtcDateTime,
            };
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeConnection", connection.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    /// <summary>
    /// Changes a connection's description, KMS key, or authorization. New credentials need their
    /// <c>AuthorizationType</c>; they authorize the connection again, as creating it did.
    /// </summary>
    public Task<UpdateConnectionResponse> UpdateConnectionAsync(UpdateConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckEnum(violations, request.AuthorizationType, "authorizationType", ConnectionAuthorizationTypes, required: false);
        ThrowIfViolations(violations);

        var connection = GetConnection(request.Name, "update");
        var parameters = request.AuthParameters;
        var changesAuth = request.AuthorizationType is not null && parameters is not null;
        if (request.Description is null && request.KmsKeyIdentifier is null && !changesAuth)
        {
            throw new InternalValidationException(
                "Failed to update the connection(s). At least one of the following is required: Description, " +
                "KmsKeyIdentifier, AuthorizationType with BasicAuthParameters, ApiKeyAuthParameters, or OAuthParameters.");
        }

        UpdateConnectionResponse response;
        lock (connection.Gate)
        {
            if (changesAuth)
            {
                var type = request.AuthorizationType!;
                var current = type == connection.AuthorizationType ? connection.Auth : new ConnectionAuth { InvocationHttpParameters = connection.Auth.InvocationHttpParameters };
                var auth = current with
                {
                    ApiKeyName = parameters!.ApiKeyAuthParameters?.ApiKeyName ?? current.ApiKeyName,
                    ApiKeyValue = parameters.ApiKeyAuthParameters?.ApiKeyValue ?? current.ApiKeyValue,
                    Username = parameters.BasicAuthParameters?.Username ?? current.Username,
                    Password = parameters.BasicAuthParameters?.Password ?? current.Password,
                    ClientId = parameters.OAuthParameters?.ClientParameters?.ClientID ?? current.ClientId,
                    ClientSecret = parameters.OAuthParameters?.ClientParameters?.ClientSecret ?? current.ClientSecret,
                    AuthorizationEndpoint = parameters.OAuthParameters?.AuthorizationEndpoint ?? current.AuthorizationEndpoint,
                    OAuthHttpMethod = parameters.OAuthParameters?.HttpMethod ?? current.OAuthHttpMethod,
                    OAuthHttpParameters = ConnectionAuth.Copy(parameters.OAuthParameters?.OAuthHttpParameters) ?? current.OAuthHttpParameters,
                    InvocationHttpParameters = ConnectionAuth.Copy(parameters.InvocationHttpParameters) ?? current.InvocationHttpParameters,
                };
                CheckAuthorizationEndpoint("update", auth);
                connection.AuthorizationType = type;
                connection.Auth = auth;
                StartAuthorization(connection);
            }

            if (request.Description is not null)
            {
                connection.Description = NullIfEmpty(request.Description);
            }

            if (request.KmsKeyIdentifier is not null)
            {
                connection.KmsKeyIdentifier = NullIfEmpty(request.KmsKeyIdentifier);
            }

            connection.LastModifiedTime = _bus.TimeProvider.GetUtcNow();
            response = new UpdateConnectionResponse
            {
                ConnectionArn = connection.Arn,
                ConnectionState = connection.State,
                CreationTime = connection.CreationTime.UtcDateTime,
                LastModifiedTime = connection.LastModifiedTime.UtcDateTime,
                LastAuthorizedTime = connection.LastAuthorizedTime?.UtcDateTime,
            };
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "UpdateConnection", connection.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    /// <summary>Removes a connection's authorization; it is <c>DEAUTHORIZED</c> when the call returns, which answers <c>DEAUTHORIZING</c>.</summary>
    public Task<DeauthorizeConnectionResponse> DeauthorizeConnectionAsync(DeauthorizeConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = GetConnection(request.Name, "deauthorize");
        DeauthorizeConnectionResponse response;
        lock (connection.Gate)
        {
            connection.AuthorizationGeneration++;
            connection.State = ConnectionResource.Deauthorized;
            connection.StateReason = null;
            connection.AccessToken = null;
            connection.LastModifiedTime = _bus.TimeProvider.GetUtcNow();
            response = new DeauthorizeConnectionResponse
            {
                ConnectionArn = connection.Arn,
                ConnectionState = ConnectionResource.Deauthorizing,
                CreationTime = connection.CreationTime.UtcDateTime,
                LastModifiedTime = connection.LastModifiedTime.UtcDateTime,
                LastAuthorizedTime = connection.LastAuthorizedTime?.UtcDateTime,
            };
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeauthorizeConnection", connection.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    /// <summary>
    /// Deletes a connection, which is gone when the call returns (answering <c>DELETING</c>). API
    /// destinations that use it keep its ARN.
    /// </summary>
    public Task<DeleteConnectionResponse> DeleteConnectionAsync(DeleteConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = GetConnection(request.Name, "delete");
        if (!_resources.EventBridgeConnections.TryRemove(connection.Name, out _))
        {
            throw ConnectionNotFound(connection.Name, "delete");
        }

        DeleteConnectionResponse response;
        lock (connection.Gate)
        {
            connection.AuthorizationGeneration++;
            response = new DeleteConnectionResponse
            {
                ConnectionArn = connection.Arn,
                ConnectionState = ConnectionResource.Deleting,
                CreationTime = connection.CreationTime.UtcDateTime,
                LastModifiedTime = connection.LastModifiedTime.UtcDateTime,
                LastAuthorizedTime = connection.LastAuthorizedTime?.UtcDateTime,
            };
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteConnection", connection.Arn);
        return Task.FromResult(response.SetCommonProperties());
    }

    /// <summary>Lists connections by name prefix or by state; EventBridge takes one of the two filters at a time.</summary>
    public Task<ListConnectionsResponse> ListConnectionsAsync(ListConnectionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        if (request.NamePrefix is not null)
        {
            CheckPattern(violations, request.NamePrefix, "namePrefix", ResourceNamePattern, maxLength: 64);
        }

        CheckEnum(violations, request.ConnectionState, "connectionState", ConnectionStates, required: false);
        ThrowIfViolations(violations);
        if (!string.IsNullOrEmpty(request.NamePrefix) && !string.IsNullOrEmpty(request.ConnectionState))
        {
            throw new InternalValidationException(
                "Failed to list the connection(s). Request failed because only one filter is supported per " +
                "request, either ConnectionState or NamePrefix.");
        }

        var connections = _resources.EventBridgeConnections.Values
            .Where(c => string.IsNullOrEmpty(request.NamePrefix) || c.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .Select(c =>
            {
                lock (c.Gate)
                {
                    return new Connection
                    {
                        ConnectionArn = c.Arn,
                        Name = c.Name,
                        ConnectionState = c.State,
                        StateReason = c.StateReason,
                        AuthorizationType = c.AuthorizationType,
                        CreationTime = c.CreationTime.UtcDateTime,
                        LastModifiedTime = c.LastModifiedTime.UtcDateTime,
                        LastAuthorizedTime = c.LastAuthorizedTime?.UtcDateTime,
                    };
                }
            })
            .Where(c => string.IsNullOrEmpty(request.ConnectionState) || c.ConnectionState == request.ConnectionState)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        var page = EventBridgePage.Take(connections, request.Limit, request.NextToken, out var nextToken);

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListConnections");
        return Task.FromResult(new ListConnectionsResponse { Connections = page, NextToken = nextToken }.SetCommonProperties());
    }

    public Task<CreateApiDestinationResponse> CreateApiDestinationAsync(CreateApiDestinationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckPattern(violations, request.Name, "name", ResourceNamePattern, maxLength: 64);
        CheckApiDestinationDescription(violations, request.Description);
        CheckPattern(violations, request.ConnectionArn, "connectionArn", ConnectionArnPattern, maxLength: 1600);
        CheckPattern(violations, request.InvocationEndpoint, "invocationEndpoint", HttpsEndpointPattern, maxLength: 2048);
        CheckEnum(violations, request.HttpMethod, "httpMethod", ApiDestinationHttpMethods, required: true);
        CheckRateLimit(violations, request.InvocationRateLimitPerSecond);
        ThrowIfViolations(violations);
        CheckRateLimitMaximum("create", request.Name!, request.InvocationRateLimitPerSecond);
        if (!EventBridgeEventDelivery.TryGetConnection(_bus, request.ConnectionArn, out _))
        {
            throw new InternalResourceNotFoundException($"Failed to create the api-destination(s). Connection '{request.ConnectionArn}' does not exist.");
        }

        var now = _bus.TimeProvider.GetUtcNow();
        var destination = new ApiDestinationResource
        {
            Name = request.Name!,
            Arn = $"arn:aws:events:{_resources.Region}:{_bus.CurrentAccountId}:api-destination/{request.Name}/{Guid.NewGuid()}",
            CreationTime = now,
            Settings = new ApiDestinationSettings(
                NullIfEmpty(request.Description),
                request.ConnectionArn!,
                request.InvocationEndpoint!,
                request.HttpMethod!,
                request.InvocationRateLimitPerSecond ?? ApiDestinationResource.MaxInvocationRateLimitPerSecond,
                now),
        };
        if (!_resources.EventBridgeApiDestinations.TryAdd(destination.Name, destination))
        {
            throw new InternalResourceAlreadyExistsException($"Failed to create the api-destination(s). An api-destination '{destination.Name}' already exists.");
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "CreateApiDestination", destination.Arn);
        return Task.FromResult(new CreateApiDestinationResponse
        {
            ApiDestinationArn = destination.Arn,
            ApiDestinationState = ApiDestinationState(destination.Settings),
            CreationTime = now.UtcDateTime,
            LastModifiedTime = now.UtcDateTime,
        }.SetCommonProperties());
    }

    public Task<DescribeApiDestinationResponse> DescribeApiDestinationAsync(DescribeApiDestinationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destination = GetApiDestination(request.Name, "describe");
        var settings = destination.Settings;
        _bus.RecordOperation(AwsServiceName.EventBridge, "DescribeApiDestination", destination.Arn);
        return Task.FromResult(new DescribeApiDestinationResponse
        {
            ApiDestinationArn = destination.Arn,
            Name = destination.Name,
            Description = settings.Description,
            ApiDestinationState = ApiDestinationState(settings),
            ConnectionArn = settings.ConnectionArn,
            InvocationEndpoint = settings.InvocationEndpoint,
            HttpMethod = settings.HttpMethod,
            InvocationRateLimitPerSecond = settings.InvocationRateLimitPerSecond,
            CreationTime = destination.CreationTime.UtcDateTime,
            LastModifiedTime = settings.LastModifiedTime.UtcDateTime,
        }.SetCommonProperties());
    }

    public Task<UpdateApiDestinationResponse> UpdateApiDestinationAsync(UpdateApiDestinationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        CheckApiDestinationDescription(violations, request.Description);
        if (request.ConnectionArn is not null)
        {
            CheckPattern(violations, request.ConnectionArn, "connectionArn", ConnectionArnPattern, maxLength: 1600);
        }

        if (request.InvocationEndpoint is not null)
        {
            CheckPattern(violations, request.InvocationEndpoint, "invocationEndpoint", HttpsEndpointPattern, maxLength: 2048);
        }

        CheckEnum(violations, request.HttpMethod, "httpMethod", ApiDestinationHttpMethods, required: false);
        CheckRateLimit(violations, request.InvocationRateLimitPerSecond);
        ThrowIfViolations(violations);

        var destination = GetApiDestination(request.Name, "update");
        CheckRateLimitMaximum("update", destination.Name, request.InvocationRateLimitPerSecond);
        if (request.ConnectionArn is not null && !EventBridgeEventDelivery.TryGetConnection(_bus, request.ConnectionArn, out _))
        {
            throw new InternalResourceNotFoundException($"Failed to update the api-destination(s). Connection '{request.ConnectionArn}' does not exist.");
        }

        ApiDestinationSettings settings;
        lock (destination.Gate)
        {
            var current = destination.Settings;
            settings = current with
            {
                Description = request.Description is null ? current.Description : NullIfEmpty(request.Description),
                ConnectionArn = request.ConnectionArn ?? current.ConnectionArn,
                InvocationEndpoint = request.InvocationEndpoint ?? current.InvocationEndpoint,
                HttpMethod = request.HttpMethod ?? current.HttpMethod,
                InvocationRateLimitPerSecond = request.InvocationRateLimitPerSecond ?? current.InvocationRateLimitPerSecond,
                LastModifiedTime = _bus.TimeProvider.GetUtcNow(),
            };
            destination.Settings = settings;
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "UpdateApiDestination", destination.Arn);
        return Task.FromResult(new UpdateApiDestinationResponse
        {
            ApiDestinationArn = destination.Arn,
            ApiDestinationState = ApiDestinationState(settings),
            CreationTime = destination.CreationTime.UtcDateTime,
            LastModifiedTime = settings.LastModifiedTime.UtcDateTime,
        }.SetCommonProperties());
    }

    public Task<DeleteApiDestinationResponse> DeleteApiDestinationAsync(DeleteApiDestinationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Name is null || !_resources.EventBridgeApiDestinations.TryRemove(request.Name, out var destination))
        {
            throw ApiDestinationNotFound(request.Name, "delete");
        }

        _bus.RecordOperation(AwsServiceName.EventBridge, "DeleteApiDestination", destination.Arn);
        return Task.FromResult(new DeleteApiDestinationResponse().SetCommonProperties());
    }

    public Task<ListApiDestinationsResponse> ListApiDestinationsAsync(ListApiDestinationsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var violations = new List<string>();
        if (request.NamePrefix is not null)
        {
            CheckPattern(violations, request.NamePrefix, "namePrefix", ResourceNamePattern, maxLength: 64);
        }

        ThrowIfViolations(violations);

        var destinations = _resources.EventBridgeApiDestinations.Values
            .Select(d => (Destination: d, Settings: d.Settings))
            .Where(d => string.IsNullOrEmpty(request.NamePrefix) || d.Destination.Name.StartsWith(request.NamePrefix, StringComparison.Ordinal))
            .Where(d => string.IsNullOrEmpty(request.ConnectionArn) || d.Settings.ConnectionArn == request.ConnectionArn)
            .OrderBy(d => d.Destination.Name, StringComparer.Ordinal)
            .Select(d => new ApiDestination
            {
                ApiDestinationArn = d.Destination.Arn,
                Name = d.Destination.Name,
                ApiDestinationState = ApiDestinationState(d.Settings),
                ConnectionArn = d.Settings.ConnectionArn,
                InvocationEndpoint = d.Settings.InvocationEndpoint,
                HttpMethod = d.Settings.HttpMethod,
                InvocationRateLimitPerSecond = d.Settings.InvocationRateLimitPerSecond,
                CreationTime = d.Destination.CreationTime.UtcDateTime,
                LastModifiedTime = d.Settings.LastModifiedTime.UtcDateTime,
            })
            .ToList();
        var page = EventBridgePage.Take(destinations, request.Limit, request.NextToken, out var nextToken);

        _bus.RecordOperation(AwsServiceName.EventBridge, "ListApiDestinations");
        return Task.FromResult(new ListApiDestinationsResponse { ApiDestinations = page, NextToken = nextToken }.SetCommonProperties());
    }

    /// <summary>
    /// Authorizes a connection whose credentials were just set (under its gate) and returns its new
    /// state: <c>API_KEY</c> and <c>BASIC</c> are <c>AUTHORIZED</c> at once; OAuth is
    /// <c>AUTHORIZING</c> until its token request answers.
    /// </summary>
    private string StartAuthorization(ConnectionResource connection)
    {
        var generation = ++connection.AuthorizationGeneration;
        connection.StateReason = null;
        connection.AccessToken = null;
        if (connection.AuthorizationType != ConnectionResource.OAuthClientCredentials)
        {
            connection.State = ConnectionResource.Authorized;
            connection.LastAuthorizedTime = _bus.TimeProvider.GetUtcNow();
            return connection.State;
        }

        connection.State = ConnectionResource.Authorizing;
        var auth = connection.Auth;
        var bus = _bus;
        _ = Task.Run(() => EventBridgeHttp.AuthorizeAsync(bus, connection, generation, auth));
        return connection.State;
    }

    /// <summary>An API destination is <c>ACTIVE</c> unless its connection exists and is not authorized.</summary>
    private string ApiDestinationState(ApiDestinationSettings settings) =>
        EventBridgeEventDelivery.TryGetConnection(_bus, settings.ConnectionArn, out var connection)
        && connection.Snapshot().State != ConnectionResource.Authorized
            ? ApiDestinationResource.Inactive
            : ApiDestinationResource.Active;

    private static void CheckAuthorizationEndpoint(string verb, ConnectionAuth auth)
    {
        if (auth.AuthorizationEndpoint is { } endpoint
            && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InternalValidationException(
                $"Failed to {verb} the connection(s). Parameter AuthorizationEndpoint is not valid. Reason: " +
                $"Endpoint '{endpoint}' is invalid, please provide a valid HTTPS endpoint URL.");
        }
    }

    private static void CheckApiDestinationDescription(List<string> violations, string? description)
    {
        if (description is { Length: > 512 })
        {
            violations.Add(Violation(description, "description", "Member must have length less than or equal to 512"));
        }
    }

    private static void CheckRateLimit(List<string> violations, int? rate)
    {
        if (rate is < 1)
        {
            violations.Add(Violation(rate.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "invocationRateLimitPerSecond", "Member must have value greater than or equal to 1"));
        }
    }

    private static void CheckRateLimitMaximum(string verb, string name, int? rate)
    {
        if (rate is > ApiDestinationResource.MaxInvocationRateLimitPerSecond)
        {
            throw new InternalLimitExceededException(
                $"Failed to {verb} the api-destination(s). Invalid Invocation Rate Limit: Rate specified " +
                $"'{rate}' for '{name}' exceeds maximum allowed of '{ApiDestinationResource.MaxInvocationRateLimitPerSecond}'.");
        }
    }

    private static void CheckEnum(List<string> violations, string? value, string member, string[] allowed, bool required)
    {
        if (value is null)
        {
            if (required)
            {
                violations.Add(NullViolation(member));
            }

            return;
        }

        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            violations.Add(Violation(value, member, $"Member must satisfy enum value set: [{string.Join(", ", allowed)}]"));
        }
    }

    private ConnectionResource GetConnection(string? name, string verb) =>
        name is not null && _resources.EventBridgeConnections.TryGetValue(name, out var connection)
            ? connection
            : throw ConnectionNotFound(name, verb);

    private static InternalResourceNotFoundException ConnectionNotFound(string? name, string verb) =>
        new($"Failed to {verb} the connection(s). Connection '{name}' does not exist.");

    private ApiDestinationResource GetApiDestination(string? name, string verb) =>
        name is not null && _resources.EventBridgeApiDestinations.TryGetValue(name, out var destination)
            ? destination
            : throw ApiDestinationNotFound(name, verb);

    private static InternalResourceNotFoundException ApiDestinationNotFound(string? name, string verb) =>
        new($"Failed to {verb} the api-destination(s). An api-destination '{name}' does not exist.");
}
