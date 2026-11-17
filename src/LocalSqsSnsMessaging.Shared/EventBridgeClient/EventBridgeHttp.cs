using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalSqsSnsMessaging.EventBridge.Model;

namespace LocalSqsSnsMessaging;

/// <summary>
/// EventBridge's HTTP calls, through <see cref="InMemoryAwsBus.HttpClient"/>: an OAuth connection's
/// token request, and an API destination's invocation.
/// </summary>
/// <remarks>
/// Each call is given up after <see cref="CallTimeout"/> on the bus's clock, EventBridge's documented
/// client execution timeout for an API destination.
/// </remarks>
internal static class EventBridgeHttp
{
    /// <summary>How long EventBridge waits for an endpoint to answer.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public const string UserAgent = "Amazon/EventBridge/ApiDestinations";

    /// <summary>The answer to one HTTP call: a status and payload, or why there was none.</summary>
    public readonly record struct Response(int? StatusCode, string Payload, bool TimedOut)
    {
        public bool Succeeded => StatusCode is >= 200 and < 300;
    }

    public static async Task<Response> SendAsync(InMemoryAwsBus bus, HttpRequestMessage request)
    {
        using var timeout = bus.TimeProvider.CreateCancellationTokenSource(CallTimeout);
        try
        {
            using var response = await bus.HttpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var payload = response.Content is null ? string.Empty : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return new Response((int)response.StatusCode, payload, TimedOut: false);
        }
        catch (OperationCanceledException)
        {
            return new Response(null, string.Empty, TimedOut: true);
        }
        catch (HttpRequestException)
        {
            return new Response(null, string.Empty, TimedOut: false);
        }
    }

    /// <summary>
    /// Requests an OAuth connection's token, and settles the connection on the answer: <c>AUTHORIZED</c>
    /// with the issued <c>access_token</c>, else <c>DEAUTHORIZED</c>. The request is the connection's
    /// <c>HttpMethod</c> to its <c>AuthorizationEndpoint</c>, with its <c>OAuthHttpParameters</c>, and
    /// <c>client_id</c>, <c>client_secret</c> and the body parameters as a form.
    /// </summary>
    public static async Task AuthorizeAsync(InMemoryAwsBus bus, ConnectionResource connection, int generation, ConnectionAuth auth)
    {
        string? token = null;
        string? reason;
        try
        {
            using var request = TokenRequest(auth);
            if (request is null)
            {
                reason = "Could not connect to the specified endpoint.";
            }
            else
            {
                var response = await SendAsync(bus, request).ConfigureAwait(false);
                if (response.StatusCode is null)
                {
                    reason = "Could not connect to the specified endpoint.";
                }
                else if (!response.Succeeded)
                {
                    reason = $"The authorization endpoint returned HTTP status {response.StatusCode}.";
                }
                else
                {
                    token = ReadAccessToken(response.Payload);
                    reason = token is null ? "The authorization endpoint did not return an access token." : null;
                }
            }
        }
#pragma warning disable CA1031 // A token request that fails in any way deauthorizes the connection.
        catch (Exception)
        {
            reason = "Could not connect to the specified endpoint.";
        }
#pragma warning restore CA1031

        lock (connection.Gate)
        {
            if (connection.AuthorizationGeneration != generation)
            {
                return;
            }

            if (token is not null)
            {
                connection.State = ConnectionResource.Authorized;
                connection.StateReason = null;
                connection.AccessToken = token;
                connection.LastAuthorizedTime = bus.TimeProvider.GetUtcNow();
            }
            else
            {
                connection.State = ConnectionResource.Deauthorized;
                connection.StateReason = reason;
                connection.AccessToken = null;
            }
        }
    }

    private static HttpRequestMessage? TokenRequest(ConnectionAuth auth)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", auth.ClientId ?? string.Empty),
            new("client_secret", auth.ClientSecret ?? string.Empty),
        };
        form.AddRange((auth.OAuthHttpParameters?.BodyParameters ?? []).Select(p => new KeyValuePair<string, string>(p.Key ?? string.Empty, p.Value ?? string.Empty)));

        var query = (auth.OAuthHttpParameters?.QueryStringParameters ?? [])
            .Select(p => new KeyValuePair<string, string>(p.Key ?? string.Empty, p.Value ?? string.Empty))
            .ToList();
        var method = new HttpMethod(auth.OAuthHttpMethod ?? "POST");
        if (method == HttpMethod.Get)
        {
            query.AddRange(form);
        }

        if (!Uri.TryCreate(WithQuery(auth.AuthorizationEndpoint ?? string.Empty, query), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        foreach (var header in auth.OAuthHttpParameters?.HeaderParameters ?? [])
        {
            AddHeader(request, header.Key, header.Value);
        }

        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    private static string? ReadAccessToken(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) is JsonObject json && json["access_token"] is JsonValue value && value.TryGetValue<string>(out var token)
                ? token
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The request an API destination makes for one event: its method to its endpoint (each <c>*</c>
    /// replaced by the target's next path parameter), with the connection's and the target's query
    /// string parameters and headers (the connection's win on a clash), the connection's
    /// authorization header, and <paramref name="body"/> (with the connection's body parameters
    /// added, when it is a JSON object) as JSON. <see langword="null"/> when the endpoint is not a URL.
    /// </summary>
    public static HttpRequestMessage? InvocationRequest(ApiDestinationSettings destination, ConnectionSnapshot? connection, Target target, string body)
    {
        var invocation = connection?.Auth.InvocationHttpParameters;
        var endpoint = new StringBuilder();
        var pathValues = target.HttpParameters?.PathParameterValues ?? [];
        var next = 0;
        foreach (var c in destination.InvocationEndpoint)
        {
            if (c == '*' && next < pathValues.Count)
            {
                endpoint.Append(pathValues[next++]);
            }
            else
            {
                endpoint.Append(c);
            }
        }

        var connectionQuery = invocation?.QueryStringParameters ?? [];
        var query = (target.HttpParameters?.QueryStringParameters ?? [])
            .Where(p => !connectionQuery.Any(c => string.Equals(c.Key, p.Key, StringComparison.Ordinal)))
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Value ?? string.Empty))
            .Concat(connectionQuery.Select(p => new KeyValuePair<string, string>(p.Key ?? string.Empty, p.Value ?? string.Empty)))
            .ToList();
        if (!Uri.TryCreate(WithQuery(endpoint.ToString(), query), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var method = new HttpMethod(destination.HttpMethod);
        var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Content = new StringContent(WithBodyParameters(body, invocation?.BodyParameters), Encoding.UTF8, "application/json");
        }

        var connectionHeaders = invocation?.HeaderParameters ?? [];
        foreach (var header in target.HttpParameters?.HeaderParameters ?? [])
        {
            if (!connectionHeaders.Any(c => string.Equals(c.Key, header.Key, StringComparison.OrdinalIgnoreCase)))
            {
                AddHeader(request, header.Key, header.Value);
            }
        }

        foreach (var header in connectionHeaders)
        {
            AddHeader(request, header.Key, header.Value);
        }

        if (connection is not null)
        {
            var auth = connection.Auth;
            switch (connection.AuthorizationType)
            {
                case ConnectionResource.ApiKey:
                    AddHeader(request, auth.ApiKeyName, auth.ApiKeyValue);
                    break;
                case ConnectionResource.Basic:
                    request.Headers.TryAddWithoutValidation("Authorization",
                        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password}")));
                    break;
                case ConnectionResource.OAuthClientCredentials when connection.AccessToken is not null:
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + connection.AccessToken);
                    break;
            }
        }

        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    /// <summary>A JSON object body with the connection's body parameters added; any other body is sent as it is.</summary>
    private static string WithBodyParameters(string body, List<ConnectionBodyParameter>? parameters)
    {
        if (parameters is not { Count: > 0 })
        {
            return body;
        }

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }

        if (json is null)
        {
            return body;
        }

        foreach (var parameter in parameters)
        {
            json[parameter.Key ?? string.Empty] = parameter.Value;
        }

        return EventBridgeJson.Serialize(json);
    }

    private static string WithQuery(string url, List<KeyValuePair<string, string>> query)
    {
        if (query.Count == 0)
        {
            return url;
        }

        var separator = url.Any(c => c == '?') ? "&" : "?";
        return url + separator + string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    private static void AddHeader(HttpRequestMessage request, string? key, string? value)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Content is not null && MediaTypeHeaderValue.TryParse(value, out var mediaType))
            {
                request.Content.Headers.ContentType = mediaType;
            }

            return;
        }

        request.Headers.Remove(key!);
        request.Headers.TryAddWithoutValidation(key!, value ?? string.Empty);
    }
}
