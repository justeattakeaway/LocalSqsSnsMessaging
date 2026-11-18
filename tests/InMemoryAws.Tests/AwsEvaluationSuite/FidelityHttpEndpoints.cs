using System.Collections.Concurrent;
using System.Net;

namespace InMemoryAws.Tests.AwsEvaluationSuite;

/// <summary>
/// What the cloud reaches over HTTP during a check (its <see cref="InMemoryAwsCloud.HttpClient"/>): the
/// endpoints the check stands up, and nothing else.
/// </summary>
/// <remarks>
/// The Python checks stand up their HTTP endpoints as Lambda function URLs, which needs Lambda and
/// IAM; a port registers the function's handler here instead (<see cref="Register"/>) and gets a
/// function-URL-shaped base URL for it. Other hosts behave as they do from AWS: a <c>.invalid</c> name
/// does not resolve, and an address in a documentation range (such as <c>192.0.2.1</c>) never
/// answers, so a call to it waits until its caller gives up.
/// </remarks>
public sealed class FidelityHttpEndpoints : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Serves <paramref name="handler"/> at a new host, and returns its base URL (<c>https://host</c>).</summary>
    public string Register(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler, string region = "us-west-2")
    {
        var host = $"{Guid.NewGuid():N}.lambda-url.{region}.on.aws";
        _hosts[host] = handler;
        return $"https://{host}";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? string.Empty;
        if (_hosts.TryGetValue(host, out var handler))
        {
            return await handler(request);
        }

        if (IPAddress.TryParse(host, out var address) && IsDocumentationAddress(address))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        throw new HttpRequestException($"No such host is known. ({host}:443)");
    }

    /// <summary>TEST-NET-1, -2 and -3 (RFC 5737): addresses that route nowhere.</summary>
    private static bool IsDocumentationAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0], bytes[1], bytes[2]) is (192, 0, 2) or (198, 51, 100) or (203, 0, 113);
    }
}
