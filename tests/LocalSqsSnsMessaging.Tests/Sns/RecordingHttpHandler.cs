using System.Collections.Concurrent;
using System.Net;

namespace LocalSqsSnsMessaging.Tests.Sns;

/// <summary>
/// Stand-in for an HTTP/S subscription endpoint: captures every request the bus posts and
/// answers with whatever status the test dictates.
/// </summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    public sealed record CapturedRequest(Uri Uri, Dictionary<string, string> Headers, string? ContentType, string Body);

    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

    public Func<CapturedRequest, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.OK;

    public Exception? ThrowOnSend { get; set; }

    public List<CapturedRequest> RequestsOfType(string messageType) =>
        Requests.Where(r => r.Headers.TryGetValue("x-amz-sns-message-type", out var t) && t == messageType).ToList();

    public static async Task WaitForRequestsAsync(Func<int> count, int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (count() < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }
        // User-Agent is tokenised by HttpClient; reassemble it the way the server would see it.
        headers["User-Agent"] = request.Headers.UserAgent.ToString();

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(request.RequestUri!, headers, request.Content?.Headers.ContentType?.ToString(), body);
        Requests.Enqueue(captured);

        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        return new HttpResponseMessage(Respond(captured));
    }
}
