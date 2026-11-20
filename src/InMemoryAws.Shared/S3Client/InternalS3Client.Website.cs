using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using InMemoryAws.Http;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Static website hosting: a bucket's website configuration, and the website endpoint
/// (<c>bucket.s3-website-region.amazonaws.com</c> or <c>bucket.s3-website.region.amazonaws.com</c>),
/// which serves the bucket's objects to anonymous browsers as web pages.
/// </summary>
/// <remarks>
/// The website endpoint answers GET and HEAD only, in HTML rather than S3's XML. A request asking for
/// a directory (<c>/</c>, <c>/docs/</c>) gets that directory's index document; a request for a key
/// that is a directory without its trailing slash is redirected to it. Only what the bucket policy
/// grants to everyone is served (the requests are anonymous); anything else is 403. Errors are
/// answered with the configured error document, or S3's default error page, unless a routing rule
/// redirects them.
/// </remarks>
internal sealed partial class InternalS3Client
{
    /// <summary>
    /// Sets the bucket's website configuration: either <c>RedirectAllRequestsTo</c> alone, or an index
    /// document (with an optional error document and routing rules).
    /// </summary>
    public override Task<PutBucketWebsiteOutput> PutBucketWebsiteAsync(PutBucketWebsiteRequest request, CancellationToken cancellationToken)
    {
        var configuration = request.WebsiteConfiguration ?? throw S3Errors.MalformedXml();
        if (configuration.RedirectAllRequestsTo is { } redirectAll)
        {
            if (configuration.IndexDocument is not null || configuration.ErrorDocument is not null || configuration.RoutingRules is not null)
            {
                throw S3Errors.InvalidArgument("RedirectAllRequestsTo cannot be provided in conjunction with other Routing Rules.", "RedirectAllRequestsTo", "not null");
            }

            if (string.IsNullOrEmpty(redirectAll.HostName))
            {
                throw S3Errors.MalformedXml();
            }
        }
        else
        {
            var suffix = configuration.IndexDocument?.Suffix;
            if (suffix is null)
            {
                throw S3Errors.InvalidArgument("A value for IndexDocument Suffix must be provided if RedirectAllRequestsTo is empty", "IndexDocument", "null");
            }

            if (suffix.Length == 0 || suffix.Contains('/', StringComparison.Ordinal))
            {
                throw S3Errors.InvalidArgument("The IndexDocument Suffix is not well formed", "IndexDocument", suffix);
            }
        }

        lock (Store.Gate)
        {
            GetBucket(request.Bucket).Website = configuration;
            return Task.FromResult(new PutBucketWebsiteOutput());
        }
    }

    public override Task<GetBucketWebsiteOutput> GetBucketWebsiteAsync(GetBucketWebsiteRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            var website = bucket.Website ?? throw NoSuchWebsiteConfiguration(bucket.Name);
            return Task.FromResult(new GetBucketWebsiteOutput
            {
                RedirectAllRequestsTo = website.RedirectAllRequestsTo,
                IndexDocument = website.IndexDocument,
                ErrorDocument = website.ErrorDocument,
                RoutingRules = website.RoutingRules,
            });
        }
    }

    public override Task<DeleteBucketWebsiteOutput> DeleteBucketWebsiteAsync(DeleteBucketWebsiteRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            GetBucket(request.Bucket).Website = null;
            return Task.FromResult(new DeleteBucketWebsiteOutput());
        }
    }

    /// <summary>
    /// Serves a request sent to a website endpoint, or answers null for any other request (which the
    /// REST protocol serves).
    /// </summary>
    internal static S3WireResponse? ServeWebsite(S3WireRequest request, InMemoryAwsCloud cloud)
    {
        if (!IsWebsiteHost(request.Host))
        {
            return null;
        }

        var requestId = RandomToken(12).ToUpperInvariant();
        var hostId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var client = new InternalS3Client(cloud, request.Region) { Request = request };
        S3WireResponse response;
        lock (client.Store.Gate)
        {
            response = client.Website(request, requestId, hostId);
        }

        response.SetHeader("x-amz-request-id", requestId);
        response.SetHeader("x-amz-id-2", hostId);
        response.SetHeader("Date", S3Wire.FormatTimestamp(cloud.TimeProvider.GetUtcNow().UtcDateTime, "rfc822"));
        response.SetHeader("Server", "AmazonS3");
        response.OmitBody = request.Method == "HEAD";
        return response;
    }

    /// <summary>
    /// A write's <c>x-amz-website-redirect-location</c> must be a path on the bucket's site (starting
    /// with <c>/</c>) or an <c>http://</c> or <c>https://</c> URL.
    /// </summary>
    private static void CheckWebsiteRedirectLocation(string? location)
    {
        if (location is not null
            && !(location.Length > 0 && location[0] == '/')
            && !location.StartsWith("http://", StringComparison.Ordinal)
            && !location.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new S3Exception(
                "InvalidRedirectLocation",
                "The website redirect location must have a prefix of 'http://' or 'https://' or '/'.",
                HttpStatusCode.BadRequest);
        }
    }

    /// <summary>Whether a host is a website endpoint: a label <c>s3-website</c> or <c>s3-website-region</c> after the bucket's.</summary>
    private static bool IsWebsiteHost(string host) =>
        host.Split('.').Skip(1).Any(label => label == "s3-website" || label.StartsWith("s3-website-", StringComparison.Ordinal));

    private static S3Exception NoSuchWebsiteConfiguration(string bucket) =>
        new("NoSuchWebsiteConfiguration", "The specified bucket does not have a website configuration", HttpStatusCode.NotFound, ("BucketName", bucket));

    private S3WireResponse Website(S3WireRequest request, string requestId, string hostId)
    {
        if (request.Method is not ("GET" or "HEAD"))
        {
            return ErrorPage(405, "Method Not Allowed", requestId, hostId, ("Code", "MethodNotAllowed"),
                ("Message", "The specified method is not allowed against this resource."), ("Method", request.Method), ("ResourceType", "OBJECT"));
        }

        if (request.Bucket is not { } name || !Store.Buckets.TryGetValue(name, out var bucket))
        {
            return ErrorPage(404, "Not Found", requestId, hostId, ("Code", "NoSuchBucket"), ("Message", "The specified bucket does not exist"), ("BucketName", request.Bucket));
        }

        if (bucket.Website is not { } website)
        {
            return ErrorPage(404, "Not Found", requestId, hostId, ("Code", "NoSuchWebsiteConfiguration"),
                ("Message", "The specified bucket does not have a website configuration"), ("BucketName", bucket.Name));
        }

        if (website.RedirectAllRequestsTo is { } redirectAll)
        {
            var query = request.RawQuery.Length == 0 ? string.Empty : "?" + request.RawQuery;
            return Redirect(301, $"{redirectAll.Protocol ?? "http"}://{redirectAll.HostName}{request.RawPath}{query}");
        }

        var requested = request.Key ?? string.Empty;
        foreach (var rule in website.RoutingRules ?? [])
        {
            if (rule.Condition?.HttpErrorCodeReturnedEquals is null && RuleMatchesKey(rule, requested))
            {
                return RoutingRedirect(rule, request, requested);
            }
        }

        var suffix = website.IndexDocument?.Suffix ?? "index.html";
        var key = requested.Length == 0 || requested[requested.Length - 1] == '/' ? requested + suffix : requested;
        // A key that is a directory, asked for without its trailing slash, redirects to the directory.
        if (key == requested && bucket.Current(key) is not { IsDeleteMarker: false }
            && bucket.Current(key + "/" + suffix) is { IsDeleteMarker: false }
            && ReadableAnonymously(bucket, key + "/" + suffix))
        {
            return Redirect(302, "/" + key + "/");
        }

        var readable = ReadableAnonymously(bucket, key);
        if (readable && bucket.Current(key) is { IsDeleteMarker: false } version)
        {
            if (version.WebsiteRedirectLocation is { } location)
            {
                return Redirect(301, location);
            }

            // An SSE-C object needs its key, which an anonymous website request can't send.
            return version.SseCustomerKeyMd5 is null
                ? ObjectPage(200, version)
                : ErrorPage(400, "Bad Request", requestId, hostId, ("Code", "InvalidRequest"),
                    ("Message", "The object was stored using a form of Server Side Encryption. The correct parameters must be provided to retrieve the object."));
        }


        var (status, reason, code, message) = readable
            ? (404, "Not Found", "NoSuchKey", "The specified key does not exist.")
            : (403, "Forbidden", "AccessDenied", "Access Denied");
        var statusText = status.ToString(CultureInfo.InvariantCulture);
        foreach (var rule in website.RoutingRules ?? [])
        {
            if (rule.Condition?.HttpErrorCodeReturnedEquals == statusText && RuleMatchesKey(rule, requested))
            {
                return RoutingRedirect(rule, request, requested);
            }
        }

        if (website.ErrorDocument?.Key is { } errorKey
            && ReadableAnonymously(bucket, errorKey)
            && bucket.Current(errorKey) is { IsDeleteMarker: false } errorDocument)
        {
            return ObjectPage(status, errorDocument);
        }

        return readable
            ? ErrorPage(status, reason, requestId, hostId, ("Code", code), ("Message", message), ("Key", key))
            : ErrorPage(status, reason, requestId, hostId, ("Code", code), ("Message", message));
    }

    /// <summary>Whether the bucket policy lets an anonymous caller read <paramref name="key"/>.</summary>
    private bool ReadableAnonymously(S3Bucket bucket, string key)
    {
        if (ResourcePolicy.Parse(ResourcePolicyKind.Bucket, bucket.Policy) is not { } policy)
        {
            return false;
        }

        var anonymous = PolicyRequest("GetObject", $"{bucket.Arn}/{key}") with { PrincipalAccount = null, PrincipalArn = null };
        return GrantsAnonymously(bucket, policy, anonymous);
    }

    private static bool RuleMatchesKey(RoutingRule rule, string key) =>
        rule.Condition?.KeyPrefixEquals is not { } prefix || key.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>The redirect a routing rule makes: to its host (this one by default) and the key it rewrites to.</summary>
    private static S3WireResponse RoutingRedirect(RoutingRule rule, S3WireRequest request, string key)
    {
        var redirect = rule.Redirect ?? new Redirect();
        var target = redirect.ReplaceKeyWith
            ?? (redirect.ReplaceKeyPrefixWith is { } replacement
                ? replacement + key.Remove(0, rule.Condition?.KeyPrefixEquals?.Length ?? 0)
                : key);
        var status = int.TryParse(redirect.HttpRedirectCode, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 301;
        return Redirect(status, $"{redirect.Protocol ?? "http"}://{redirect.HostName ?? request.Host}/{target}");
    }

    private static S3WireResponse Redirect(int status, string location)
    {
        var response = new S3WireResponse { Status = status };
        response.SetHeader("Location", location);
        response.SetHeader("Content-Length", "0");
        return response;
    }

    /// <summary>An object served as a page: its bytes, with its content headers.</summary>
    private static S3WireResponse ObjectPage(int status, S3ObjectVersion version)
    {
        var response = new S3WireResponse { Status = status, Payload = version.Data };
        response.SetHeader("Content-Type", version.ContentType ?? "binary/octet-stream");
        response.SetHeader("Content-Length", version.Size.ToString(CultureInfo.InvariantCulture));
        response.SetHeader("ETag", version.ETag);
        response.SetHeader("Last-Modified", S3Wire.FormatTimestamp(version.LastModified, "rfc822"));
        if (version.ContentEncoding is { } encoding)
        {
            response.SetHeader("Content-Encoding", encoding);
        }

        if (version.CacheControl is { } cacheControl)
        {
            response.SetHeader("Cache-Control", cacheControl);
        }

        response.SetHeaders("x-amz-meta-", version.Metadata.Count == 0 ? null : version.Metadata);
        return response;
    }

    /// <summary>S3's default HTML error page: the status, then a list of what went wrong.</summary>
    private static S3WireResponse ErrorPage(int status, string reason, string requestId, string hostId, params (string Name, string? Value)[] lines)
    {
        var title = $"{status.ToString(CultureInfo.InvariantCulture)} {reason}";
        var html = new StringBuilder()
            .Append("<html>\n<head><title>").Append(title).Append("</title></head>\n<body>\n<h1>").Append(title).Append("</h1>\n<ul>\n");
        foreach (var (name, value) in lines)
        {
            if (value is not null)
            {
                html.Append("<li>").Append(name).Append(": ").Append(WebUtility.HtmlEncode(value)).Append("</li>\n");
            }
        }

        html.Append("<li>RequestId: ").Append(requestId).Append("</li>\n<li>HostId: ").Append(hostId).Append("</li>\n</ul>\n<hr/>\n</body>\n</html>\n");
        var bytes = Encoding.UTF8.GetBytes(html.ToString());
        var response = new S3WireResponse { Status = status, Payload = bytes };
        response.SetHeader("Content-Type", "text/html; charset=utf-8");
        response.SetHeader("Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));
        return response;
    }
}
