#if ASPNETCORE
using Microsoft.AspNetCore.Http;
#endif

namespace LocalSqsSnsMessaging.Http;

/// <summary>
/// Works out which AWS region a request was made in, so the bus serves it from that region's
/// resources, as each regional AWS endpoint does.
/// </summary>
internal static class AwsRequestRegion
{
    private const string CredentialKey = "Credential=";

#if !ASPNETCORE
    /// <summary>
    /// The region the request names, or <see langword="null"/> when it names none. In order: the
    /// region in the Signature Version 4 credential scope (the <c>Authorization</c> header, or the
    /// <c>X-Amz-Credential</c> query parameter of a presigned request), then the region in an AWS
    /// endpoint host such as <c>sqs.us-west-2.amazonaws.com</c>.
    /// </summary>
    public static string? FromRequest(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authorization = request.Headers.TryGetValues("Authorization", out var values) ? string.Join(",", values) : null;
        return request.RequestUri is { IsAbsoluteUri: true } uri
            ? From(authorization, uri.Query, uri.Host)
            : From(authorization, query: null, host: null);
    }
#else
    /// <summary>The region a request the server received names; see <c>FromRequest</c> in the library build.</summary>
    public static string? FromHttpContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return From(context.Request.Headers.Authorization.ToString(), context.Request.QueryString.Value, context.Request.Host.Host);
    }
#endif

    private static string? From(string? authorization, string? query, string? host)
    {
        if (!string.IsNullOrEmpty(authorization) && FromCredentialScope(authorization!) is { } signedRegion)
        {
            return signedRegion;
        }

        if (!string.IsNullOrEmpty(query))
        {
            var credential = System.Web.HttpUtility.ParseQueryString(query)["X-Amz-Credential"];
            if (credential is not null && FromScope(credential) is { } presignedRegion)
            {
                return presignedRegion;
            }
        }

        return string.IsNullOrEmpty(host) ? null : FromHost(host!);
    }

    /// <summary>
    /// The region in a SigV4 <c>Authorization</c> header:
    /// <c>AWS4-HMAC-SHA256 Credential=AKID/20240101/us-east-1/sqs/aws4_request, SignedHeaders=..., Signature=...</c>.
    /// </summary>
    internal static string? FromCredentialScope(string authorization)
    {
        var start = authorization.IndexOf(CredentialKey, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += CredentialKey.Length;
        var end = authorization.IndexOfAny([',', ' '], start);
        return FromScope(end < 0 ? authorization[start..] : authorization[start..end]);
    }

    /// <summary>The region in a credential: <c>AKID/date/region/service/aws4_request</c>.</summary>
    private static string? FromScope(string credential)
    {
        var parts = credential.Split('/');
        return parts.Length >= 5 && IsRegionName(parts[2]) ? parts[2] : null;
    }

    /// <summary>
    /// The region in an AWS endpoint host: <c>sqs.us-east-1.amazonaws.com</c> (and other partitions'
    /// domains) or the legacy <c>us-east-1.queue.amazonaws.com</c>.
    /// </summary>
    internal static string? FromHost(string host)
    {
        var labels = host.Split('.');
        if (labels.Length >= 4 && labels[0] is "sqs" or "sns" or "events" && IsRegionName(labels[1]))
        {
            return labels[1];
        }

        if (labels.Length >= 4 && labels[1] == "queue" && IsRegionName(labels[0]))
        {
            return labels[0];
        }

        return null;
    }

    /// <summary>A plausible region name: lowercase letters and digits in hyphen-separated parts, e.g. <c>eu-west-2</c>.</summary>
    private static bool IsRegionName(string value)
    {
        if (value.Length is < 4 or > 32 || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        var hyphenated = false;
        foreach (var c in value)
        {
            if (c == '-')
            {
                hyphenated = true;
            }
            else if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9')))
            {
                return false;
            }
        }

        return hyphenated;
    }
}
