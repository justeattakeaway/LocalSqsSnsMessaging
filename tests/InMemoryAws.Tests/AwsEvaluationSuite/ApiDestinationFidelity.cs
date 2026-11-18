using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Amazon.SQS.Model;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// The HTTP endpoint the EventBridge API destination checks call: in Python, a Lambda function URL
/// (Lambda, IAM) whose function admits only requests carrying the run's connection credential
/// (anything else gets a fixed 403), and copies each admitted request's method, path, query string,
/// headers (the credential as its sha256 hex), body and arrival time into a capture queue. A path
/// ending in <c>/s400</c> or <c>/s500</c> is answered with that status.
/// </summary>
/// <remarks>
/// Here the function's handler is C#, served at a function-URL-shaped host through
/// <see cref="FidelityHttpEndpoints"/>; it writes the same capture record, with the arrival time on
/// the check's clock. Creating the function, its role and their cleanups are dropped.
/// </remarks>
internal static class CaptureFunction
{
    /// <summary>
    /// <c>_https_endpoint(ctx, auth_header, auth_value)</c>: (base URL, capture queue URL); only a
    /// request whose <paramref name="authHeader"/> is <paramref name="authValue"/> is admitted.
    /// </summary>
    public static async Task<(string BaseUrl, string CaptureUrl)> HttpsEndpoint(FidelityContext ctx, string check, string authHeader, string authValue)
    {
        var captureUrl = await CreateCaptureQueue(ctx, check);
        var baseUrl = ctx.HttpEndpoints.Register(async request =>
        {
            var (path, query, headers, body) = await Read(request);
            if (!FixedTimeEquals(headers.GetValueOrDefault(authHeader, ""), authValue))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("") };
            }

            headers[authHeader] = Sha256(authValue);
            await Capture(ctx, captureUrl, request.Method.Method, path, query, headers, body);
            return new HttpResponseMessage(StatusFor(path)) { Content = new StringContent("") };
        });
        return (baseUrl, captureUrl);
    }

    /// <summary>
    /// The OAuth variant: a path ending in <c>/token</c> admits a form whose <c>client_secret</c> is
    /// <paramref name="clientSecret"/> (captured as its sha256 hex) and answers with
    /// <paramref name="token"/>; any other path admits <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    public static async Task<(string BaseUrl, string CaptureUrl)> OAuthHttpsEndpoint(FidelityContext ctx, string check, string token, string clientSecret)
    {
        var captureUrl = await CreateCaptureQueue(ctx, check);
        var baseUrl = ctx.HttpEndpoints.Register(async request =>
        {
            var (path, query, headers, body) = await Read(request);
            var isToken = path.EndsWith("/token", StringComparison.Ordinal);
            var (got, want) = isToken
                ? (HttpUtility.ParseQueryString(body)["client_secret"] ?? "", clientSecret)
                : (headers.GetValueOrDefault("authorization", ""), "Bearer " + token);
            if (!FixedTimeEquals(got, want))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("") };
            }

            if (isToken)
            {
                body = string.Join("&", ParseForm(body).Select(kv =>
                    Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Key == "client_secret" ? Sha256(kv.Value) : kv.Value)));
            }
            else
            {
                headers["authorization"] = Sha256(got);
            }

            await Capture(ctx, captureUrl, request.Method.Method, path, query, headers, body);
            if (isToken)
            {
                var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["access_token"] = token, ["token_type"] = "Bearer", ["expires_in"] = 3600 });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(StatusFor(path)) { Content = new StringContent("") };
        });
        return (baseUrl, captureUrl);
    }

    /// <summary><c>secrets.token_urlsafe(32)</c>: a random URL-safe credential.</summary>
    public static string TokenUrlSafe() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The sha256 hex the endpoint captures in place of a credential.</summary>
    public static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static HttpStatusCode StatusFor(string path) =>
        path.EndsWith("/s400", StringComparison.Ordinal) ? HttpStatusCode.BadRequest
        : path.EndsWith("/s500", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError
        : HttpStatusCode.OK;

    private static async Task<string> CreateCaptureQueue(FidelityContext ctx, string check)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-cap-"),
            Tags = new() { ["sim2real"] = check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return url;
    }

    /// <summary>The request as a function URL hands it to the function: lower-cased header names, the raw path and query.</summary>
    private static async Task<(string Path, string Query, Dictionary<string, string> Headers, string Body)> Read(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[name.ToLowerInvariant()] = string.Join(",", values);
        }

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
        var uri = request.RequestUri!;
        return (uri.AbsolutePath, uri.Query.TrimStart('?'), headers, body);
    }

    private static async Task Capture(FidelityContext ctx, string captureUrl, string method, string path, string query, Dictionary<string, string> headers, string body)
    {
        var record = new Dictionary<string, object>
        {
            ["t"] = ctx.Time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0,
            ["method"] = method,
            ["path"] = path,
            ["query"] = query,
            ["headers"] = headers,
            ["body"] = body,
        };
        await ctx.Sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = captureUrl, MessageBody = JsonSerializer.Serialize(record) });
    }

    private static List<KeyValuePair<string, string>> ParseForm(string body) =>
        [.. body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            return new KeyValuePair<string, string>(Uri.UnescapeDataString(key.Replace('+', ' ')), Uri.UnescapeDataString(value.Replace('+', ' ')));
        })];

    private static bool FixedTimeEquals(string got, string want) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(want));
}

/// <summary>A request the capture function recorded (<c>json.loads</c> of a capture queue message).</summary>
internal sealed record CapturedRequest(double T, string Method, string Path, string Query, Dictionary<string, string> Headers, string Body)
{
    public static CapturedRequest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new CapturedRequest(
            root.GetProperty("t").GetDouble(),
            root.GetProperty("method").GetString() ?? "",
            root.GetProperty("path").GetString() ?? "",
            root.GetProperty("query").GetString() ?? "",
            root.GetProperty("headers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal),
            root.GetProperty("body").GetString() ?? "");
    }

    /// <summary>The event id in the body, or <see langword="null"/> when the body is not a JSON object with one.</summary>
    public string? BodyId => BodyString("id");

    /// <summary>A string member of the JSON body, or <see langword="null"/>.</summary>
    public string? BodyString(string member)
    {
        try
        {
            using var doc = JsonDocument.Parse(Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(member, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{{'method': '{Method}', 'path': '{Path}', 'query': '{Query}'}}");
}
