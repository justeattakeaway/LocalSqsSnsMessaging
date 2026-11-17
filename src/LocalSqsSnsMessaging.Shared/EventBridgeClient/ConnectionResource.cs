using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// An EventBridge connection: the authorization (and extra HTTP parameters) its API destinations
/// send to their endpoints.
/// </summary>
/// <remarks>
/// An <c>API_KEY</c> or <c>BASIC</c> connection is <c>AUTHORIZED</c> as soon as it is created or
/// given new credentials. An <c>OAUTH_CLIENT_CREDENTIALS</c> connection is <c>AUTHORIZING</c> until
/// its token request to the authorization endpoint (through <see cref="InMemoryAwsBus.HttpClient"/>)
/// answers: <c>AUTHORIZED</c> with the issued token, else <c>DEAUTHORIZED</c>. Its state and
/// credentials change under <see cref="Gate"/>; a token request started before a later change of
/// credentials is ignored when it answers (<see cref="AuthorizationGeneration"/>).
/// </remarks>
internal sealed class ConnectionResource
{
    public const string Authorized = "AUTHORIZED";
    public const string Authorizing = "AUTHORIZING";
    public const string Deauthorized = "DEAUTHORIZED";
    public const string Deauthorizing = "DEAUTHORIZING";
    public const string Deleting = "DELETING";

    public const string ApiKey = "API_KEY";
    public const string Basic = "BASIC";
    public const string OAuthClientCredentials = "OAUTH_CLIENT_CREDENTIALS";

    public required string Name { get; init; }
    public required string Arn { get; init; }

    /// <summary>The Secrets Manager secret EventBridge keeps the credentials in (named, never created).</summary>
    public required string SecretArn { get; init; }
    public required DateTimeOffset CreationTime { get; init; }

    public object Gate { get; } = new();

    // Everything below changes under Gate.
    public string? Description { get; set; }
    public string? KmsKeyIdentifier { get; set; }
    public required string AuthorizationType { get; set; }
    public required ConnectionAuth Auth { get; set; }
    public required string State { get; set; }
    public string? StateReason { get; set; }
    public DateTimeOffset LastModifiedTime { get; set; }
    public DateTimeOffset? LastAuthorizedTime { get; set; }

    /// <summary>The access token an OAuth connection's authorization endpoint issued.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Counts authorizations, so a token request answers only the one that started it.</summary>
    public int AuthorizationGeneration { get; set; }

    /// <summary>A copy of what an invocation needs, read under the gate.</summary>
    public ConnectionSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new ConnectionSnapshot(Arn, State, AuthorizationType, Auth, AccessToken);
        }
    }
}

/// <summary>A connection's authorization as an invocation reads it.</summary>
internal sealed record ConnectionSnapshot(string Arn, string State, string AuthorizationType, ConnectionAuth Auth, string? AccessToken);

/// <summary>A connection's credentials and HTTP parameters, secrets included; never answered as is.</summary>
internal sealed record ConnectionAuth
{
    public string? ApiKeyName { get; init; }
    public string? ApiKeyValue { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? AuthorizationEndpoint { get; init; }
    public string? OAuthHttpMethod { get; init; }
    public ConnectionHttpParameters? OAuthHttpParameters { get; init; }
    public ConnectionHttpParameters? InvocationHttpParameters { get; init; }

    /// <summary>A copy of <paramref name="parameters"/>, so the stored ones never change with a request object.</summary>
    public static ConnectionHttpParameters? Copy(ConnectionHttpParameters? parameters) => parameters is null
        ? null
        : new ConnectionHttpParameters
        {
            HeaderParameters = [.. (parameters.HeaderParameters ?? []).Select(p => new ConnectionHeaderParameter { Key = p.Key, Value = p.Value, IsValueSecret = p.IsValueSecret })],
            QueryStringParameters = [.. (parameters.QueryStringParameters ?? []).Select(p => new ConnectionQueryStringParameter { Key = p.Key, Value = p.Value, IsValueSecret = p.IsValueSecret })],
            BodyParameters = [.. (parameters.BodyParameters ?? []).Select(p => new ConnectionBodyParameter { Key = p.Key, Value = p.Value, IsValueSecret = p.IsValueSecret })],
        };

    /// <summary>
    /// <paramref name="parameters"/> as DescribeConnection answers them: a value marked
    /// <c>IsValueSecret</c> is left out.
    /// </summary>
    public static ConnectionHttpParameters? Redact(ConnectionHttpParameters? parameters) => parameters is null
        ? null
        : new ConnectionHttpParameters
        {
            HeaderParameters = [.. (parameters.HeaderParameters ?? []).Select(p => new ConnectionHeaderParameter { Key = p.Key, Value = p.IsValueSecret == true ? null : p.Value, IsValueSecret = p.IsValueSecret })],
            QueryStringParameters = [.. (parameters.QueryStringParameters ?? []).Select(p => new ConnectionQueryStringParameter { Key = p.Key, Value = p.IsValueSecret == true ? null : p.Value, IsValueSecret = p.IsValueSecret })],
            BodyParameters = [.. (parameters.BodyParameters ?? []).Select(p => new ConnectionBodyParameter { Key = p.Key, Value = p.IsValueSecret == true ? null : p.Value, IsValueSecret = p.IsValueSecret })],
        };
}
