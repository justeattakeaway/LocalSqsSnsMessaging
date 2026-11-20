using System.Globalization;
using System.Net;
using InMemoryAws.Http;
using InMemoryAws.S3.Model;

namespace InMemoryAws.S3;

/// <summary>
/// Cross-origin resource sharing: a bucket's CORS rules, and the browser preflight (<c>OPTIONS</c>)
/// requests S3 answers from them.
/// </summary>
/// <remarks>
/// A preflight is answered from the first rule that allows its origin, its
/// <c>Access-Control-Request-Method</c> and every header in its <c>Access-Control-Request-Headers</c>:
/// 200 with the rule's <c>Access-Control-*</c> headers and no body. A bucket without CORS rules, or a
/// preflight no rule allows, is refused 403 <c>AccessForbidden</c> with no <c>Access-Control-*</c>
/// headers. Preflights are anonymous, so no bucket policy applies to them.
/// </remarks>
internal sealed partial class InternalS3Client
{
    private const int MaxCorsRules = 100;

    private static readonly string[] CorsMethods = ["GET", "PUT", "HEAD", "POST", "DELETE"];

    /// <summary>
    /// Replaces the bucket's CORS rules. Each rule needs at least one allowed method and origin; a
    /// method S3 can't allow is <c>InvalidRequest</c>, an origin or header with more than one
    /// <c>*</c> is <c>InvalidRequest</c>, and more than 100 rules is <c>MalformedXML</c>.
    /// </summary>
    public override Task<PutBucketCorsOutput> PutBucketCorsAsync(PutBucketCorsRequest request, CancellationToken cancellationToken)
    {
        var rules = request.CORSConfiguration?.CORSRules;
        if (rules is null || rules.Count == 0 || rules.Count > MaxCorsRules)
        {
            throw S3Errors.MalformedXml();
        }

        foreach (var rule in rules)
        {
            if (rule.AllowedMethods is not { Count: > 0 } methods || rule.AllowedOrigins is not { Count: > 0 } origins)
            {
                throw S3Errors.MalformedXml();
            }

            foreach (var method in methods)
            {
                if (Array.IndexOf(CorsMethods, method) < 0)
                {
                    throw S3Errors.InvalidRequest($"Found unsupported HTTP method in CORS config. Unsupported method is {method}");
                }
            }

            foreach (var origin in origins)
            {
                if (CountWildcards(origin) > 1)
                {
                    throw S3Errors.InvalidRequest($"AllowedOrigin \"{origin}\" can not have more than one wildcard.");
                }
            }

            foreach (var header in rule.AllowedHeaders ?? [])
            {
                if (CountWildcards(header) > 1)
                {
                    throw S3Errors.InvalidRequest($"AllowedHeader \"{header}\" can not have more than one wildcard.");
                }
            }
        }

        lock (Store.Gate)
        {
            GetBucket(request.Bucket).CorsRules = rules;
            return Task.FromResult(new PutBucketCorsOutput());
        }
    }

    /// <summary>The bucket's CORS rules, as set; <c>NoSuchCORSConfiguration</c> when it has none.</summary>
    public override Task<GetBucketCorsOutput> GetBucketCorsAsync(GetBucketCorsRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            var bucket = GetBucket(request.Bucket);
            return Task.FromResult(new GetBucketCorsOutput { CORSRules = bucket.CorsRules ?? throw NoSuchCorsConfiguration(bucket.Name) });
        }
    }

    /// <summary>Removes the bucket's CORS rules; removing none succeeds too.</summary>
    public override Task<DeleteBucketCorsOutput> DeleteBucketCorsAsync(DeleteBucketCorsRequest request, CancellationToken cancellationToken)
    {
        lock (Store.Gate)
        {
            GetBucket(request.Bucket).CorsRules = null;
            return Task.FromResult(new DeleteBucketCorsOutput());
        }
    }

    private static S3Exception NoSuchCorsConfiguration(string bucket) =>
        S3Errors.NoSuchConfiguration("NoSuchCORSConfiguration", "The CORS configuration does not exist", bucket);

    /// <summary>
    /// Answers a browser's preflight (<c>OPTIONS</c>) request for a bucket or an object into
    /// <paramref name="response"/>, or refuses it.
    /// </summary>
    internal void AnswerPreflight(S3WireResponse response)
    {
        var request = Request!;
        if (request.Header("Origin") is not { Length: > 0 } origin)
        {
            throw S3Errors.InvalidArgument("Insufficient information. Origin request header needed.");
        }

        if (request.Header("Access-Control-Request-Method") is not { Length: > 0 } method)
        {
            throw S3Errors.InvalidArgument("Invalid Access-Control-Request-Method: null");
        }

        var resourceType = request.PathKind == S3PathKind.Object ? "OBJECT" : "BUCKET";
        CORSRule? matched = null;
        string[] requestedHeaders;
        lock (Store.Gate)
        {
            if (string.IsNullOrEmpty(request.Bucket) || !Store.Buckets.TryGetValue(request.Bucket!, out var bucket))
            {
                throw S3Errors.NoSuchBucket(request.Bucket);
            }

            if (bucket.CorsRules is not { } rules)
            {
                throw new S3Exception("AccessForbidden", "CORSResponse: CORS is not enabled for this bucket.", HttpStatusCode.Forbidden,
                    ("Method", method), ("ResourceType", "BUCKET"));
            }

            requestedHeaders = (request.Header("Access-Control-Request-Headers") ?? string.Empty)
                .Split(',')
                .Select(h => h.Trim())
                .Where(h => h.Length > 0)
                .ToArray();
            foreach (var rule in rules)
            {
                if (rule.AllowedMethods!.Contains(method, StringComparer.Ordinal)
                    && rule.AllowedOrigins!.Exists(allowed => WildcardMatch(allowed, origin, ignoreCase: false))
                    && Array.TrueForAll(requestedHeaders, h => (rule.AllowedHeaders ?? []).Exists(allowed => WildcardMatch(allowed, h, ignoreCase: true))))
                {
                    matched = rule;
                    break;
                }
            }
        }

        if (matched is null)
        {
            throw new S3Exception(
                "AccessForbidden",
                "CORSResponse: This CORS request is not allowed. This is usually because the evalution of Origin, request method / "
                + "Access-Control-Request-Method or Access-Control-Request-Headers are not whitelisted by the resource's CORS spec.",
                HttpStatusCode.Forbidden,
                ("Method", method),
                ("ResourceType", resourceType));
        }

        var anyOrigin = matched.AllowedOrigins!.Contains("*", StringComparer.Ordinal);
        response.Status = 200;
        response.SetHeader("Access-Control-Allow-Origin", anyOrigin ? "*" : origin);
        response.SetHeader("Access-Control-Allow-Methods", string.Join(", ", matched.AllowedMethods!));
        if (requestedHeaders.Length > 0)
        {
            response.SetHeader("Access-Control-Allow-Headers", string.Join(", ", requestedHeaders));
        }

        if (matched.MaxAgeSeconds is { } maxAge)
        {
            response.SetHeader("Access-Control-Max-Age", maxAge.ToString(CultureInfo.InvariantCulture));
        }

        if (matched.ExposeHeaders is { Count: > 0 } expose)
        {
            response.SetHeader("Access-Control-Expose-Headers", string.Join(", ", expose));
        }

        if (!anyOrigin)
        {
            response.SetHeader("Access-Control-Allow-Credentials", "true");
        }

        response.SetHeader("Vary", "Origin, Access-Control-Request-Headers, Access-Control-Request-Method");
        response.SetHeader("Content-Length", "0");
    }

    /// <summary>
    /// Adds the <c>Access-Control-*</c> headers an actual cross-origin request (one with an
    /// <c>Origin</c>) gets when a CORS rule of its bucket allows its origin and method.
    /// </summary>
    internal static void AddCorsHeaders(S3WireRequest request, InMemoryAwsCloud cloud, S3WireResponse response)
    {
        if (request.Header("Origin") is not { Length: > 0 } origin || string.IsNullOrEmpty(request.Bucket))
        {
            return;
        }

        lock (cloud.S3.Gate)
        {
            if (!cloud.S3.Buckets.TryGetValue(request.Bucket!, out var bucket) || bucket.CorsRules is not { } rules)
            {
                return;
            }

            var rule = rules.Find(r => r.AllowedMethods!.Contains(request.Method, StringComparer.Ordinal)
                && r.AllowedOrigins!.Exists(allowed => WildcardMatch(allowed, origin, ignoreCase: false)));
            if (rule is null)
            {
                return;
            }

            var anyOrigin = rule.AllowedOrigins!.Contains("*", StringComparer.Ordinal);
            response.SetHeader("Access-Control-Allow-Origin", anyOrigin ? "*" : origin);
            response.SetHeader("Access-Control-Allow-Methods", string.Join(", ", rule.AllowedMethods!));
            if (rule.MaxAgeSeconds is { } maxAge)
            {
                response.SetHeader("Access-Control-Max-Age", maxAge.ToString(CultureInfo.InvariantCulture));
            }

            if (rule.ExposeHeaders is { Count: > 0 } expose)
            {
                response.SetHeader("Access-Control-Expose-Headers", string.Join(", ", expose));
            }

            if (!anyOrigin)
            {
                response.SetHeader("Access-Control-Allow-Credentials", "true");
            }

            response.SetHeader("Vary", "Origin, Access-Control-Request-Headers, Access-Control-Request-Method");
        }
    }

    private static int CountWildcards(string value) => value.Count(c => c == '*');

    /// <summary>Whether <paramref name="value"/> matches <paramref name="pattern"/>, which may hold one <c>*</c>.</summary>
    private static bool WildcardMatch(string pattern, string value, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return string.Equals(pattern, value, comparison);
        }

        var prefix = pattern.Substring(0, star);
        var suffix = pattern.Substring(star + 1);
        return value.Length >= prefix.Length + suffix.Length
            && value.StartsWith(prefix, comparison)
            && value.EndsWith(suffix, comparison);
    }
}

/// <summary>A bucket's CORS rules.</summary>
internal sealed partial class S3Bucket
{
    /// <summary>
    /// The CORS rules, as sent (replaced whole, never edited); null when the bucket has no CORS
    /// configuration.
    /// </summary>
    public List<CORSRule>? CorsRules { get; set; }
}
