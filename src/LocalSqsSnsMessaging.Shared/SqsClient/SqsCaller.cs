using System.Net;
#if ASPNETCORE
using Microsoft.AspNetCore.Http;
#endif

namespace LocalSqsSnsMessaging;

/// <summary>
/// Who made an SQS request, as far as the request shows it: the SigV4 access key it was signed
/// with (none for an unsigned request), the address it came from and whether it came over TLS.
/// </summary>
/// <remarks>
/// The library models no IAM. Every signed request acts for an account (the account root): the
/// account named by an access key that is a 12-digit account id, as the server uses them to pick
/// an account, and otherwise the bus's own account. An unsigned request, which is what
/// <c>CreateSqsClient()</c> sends, is served as the queue's owner, as it always has been.
/// </remarks>
internal sealed class SqsCaller
{
    /// <summary>A caller with no request behind it: the raw client, or code inside the library.</summary>
    public static readonly SqsCaller Unsigned = new(accessKeyId: null, sourceIp: null, secureTransport: true);

    /// <summary>Where a request handed straight to the in-memory handler comes from.</summary>
    private static readonly IPAddress InProcessAddress = IPAddress.Loopback;

    private SqsCaller(string? accessKeyId, IPAddress? sourceIp, bool secureTransport)
    {
        AccessKeyId = accessKeyId;
        SourceIp = sourceIp;
        SecureTransport = secureTransport;
    }

    /// <summary>The access key the request was signed with, or <see langword="null"/> when it was not signed.</summary>
    public string? AccessKeyId { get; }

    /// <summary>Whether the request was signed.</summary>
    public bool IsSigned => AccessKeyId is not null;

    /// <summary>The address the request came from (<c>aws:SourceIp</c>), when known.</summary>
    public IPAddress? SourceIp { get; }

    /// <summary>Whether the request came over TLS (<c>aws:SecureTransport</c>).</summary>
    public bool SecureTransport { get; }

    /// <summary>The account the caller acts for.</summary>
    public string AccountId(InMemoryAwsBus bus) =>
        AccessKeyId is { } key && IsAccountId(key) ? key : bus.CurrentAccountId;

    /// <summary>The caller's ARN, as an authorization error names it.</summary>
    public string Arn(InMemoryAwsBus bus) => $"arn:aws:iam::{AccountId(bus)}:root";

    /// <summary>
    /// The <c>SenderId</c> SQS stamps on a message this caller sends: the IAM user id of a signed
    /// caller (what <c>sts:GetCallerIdentity</c> answers as <c>UserId</c>), which for an account's
    /// root user is the account id. An unsigned caller keeps the library's long-standing account id.
    /// </summary>
    public string SenderId(InMemoryAwsBus bus) =>
        AccessKeyId is { } key && !IsAccountId(key) ? bus.CurrentUserId : AccountId(bus);

    /// <summary>A caller signed with <paramref name="accessKeyId"/>, for tests.</summary>
    internal static SqsCaller Signed(string accessKeyId, IPAddress? sourceIp = null) =>
        new(accessKeyId, sourceIp ?? InProcessAddress, secureTransport: true);

#if !ASPNETCORE
    /// <summary>The caller behind a request handed to the in-memory HTTP handler, which arrives from loopback.</summary>
    public static SqsCaller FromRequest(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = request.Headers.TryGetValues("Authorization", out var values)
            ? string.Join(",", values)
            : null;
        return new SqsCaller(
            ExtractAccessKeyId(authorization, request.RequestUri?.Query),
            InProcessAddress,
            !string.Equals(request.RequestUri?.Scheme, "http", StringComparison.OrdinalIgnoreCase));
    }
#else
    /// <summary>The caller behind a request the server received.</summary>
    public static SqsCaller FromHttpContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var remote = context.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true })
        {
            remote = remote.MapToIPv4();
        }

        return new SqsCaller(
            ExtractAccessKeyId(context.Request.Headers.Authorization.ToString(), context.Request.QueryString.Value),
            remote ?? InProcessAddress,
            context.Request.IsHttps);
    }
#endif

    /// <summary>
    /// The access key id of a SigV4 request: from <c>Credential=</c> in the <c>Authorization</c>
    /// header, or from a presigned URL's <c>X-Amz-Credential</c> parameter.
    /// </summary>
    internal static string? ExtractAccessKeyId(string? authorization, string? query)
    {
        if (!string.IsNullOrEmpty(authorization))
        {
            const string marker = "Credential=";
            var start = authorization!.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                start += marker.Length;
                var end = authorization.IndexOf('/', start);
                if (end > start)
                {
                    return authorization[start..end];
                }
            }
        }

        if (!string.IsNullOrEmpty(query))
        {
            foreach (var pair in query!.TrimStart('?').Split('&'))
            {
                const string parameter = "X-Amz-Credential=";
                if (pair.StartsWith(parameter, StringComparison.OrdinalIgnoreCase))
                {
                    var credential = Uri.UnescapeDataString(pair[parameter.Length..]);
                    var slash = credential.IndexOf('/', StringComparison.Ordinal);
                    if (slash > 0)
                    {
                        return credential[..slash];
                    }
                }
            }
        }

        return null;
    }

    private static bool IsAccountId(string value) => value.Length == 12 && value.All(c => c is >= '0' and <= '9');
}
