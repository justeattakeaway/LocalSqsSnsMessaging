// Translated from behavioral/s3/check_cors_preflight_allowed_origin_returns_rule_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-allowed-origin-returns-rule-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightAllowedOriginReturnsRuleHeaders
{
    /// <summary>
    /// A browser preflight matching a CORS rule gets 200 with the rule's origin, methods, requested
    /// headers, Max-Age, Expose-Headers, credentials and Vary, and an empty body.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-allowed-origin-returns-rule-headers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await NewBucket(ctx, s3);
        await PutJson(s3, bucket);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        AllowedOrigins = [Origin],
                        AllowedMethods = ["GET", "PUT"],
                        AllowedHeaders = ["content-type", "x-amz-meta-note"],
                        ExposeHeaders = ["ETag", "x-amz-meta-note"],
                        MaxAgeSeconds = 3000,
                    },
                ],
            },
        });
        var url = S3Fidelity.ObjectUrl(ctx, bucket, Key);
        var (status, headers, body) = await S3Fidelity.UntilPreflightStatus(ctx, url, new Dictionary<string, string>
        {
            ["Origin"] = Origin,
            ["Access-Control-Request-Method"] = "PUT",
            ["Access-Control-Request-Headers"] = "content-type,x-amz-meta-note",
        }, 200);
        Ensure(status == 200, $"the matching preflight must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        var got = S3Fidelity.CorsHeaders.ToDictionary(name => name, name => headers.GetValueOrDefault(name));
        var want = new Dictionary<string, string?>
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "GET, PUT",
            ["access-control-allow-headers"] = "content-type, x-amz-meta-note",
            ["access-control-max-age"] = "3000",
            ["access-control-expose-headers"] = "ETag, x-amz-meta-note",
            ["access-control-allow-credentials"] = "true",
        };
        Ensure(
            want.All(w => got[w.Key] == w.Value),
            $"the preflight's CORS headers are {Show(got)}, expected {Show(want)}");
        var vary = Tokens(headers.GetValueOrDefault("vary"));
        Ensure(
            vary.SetEquals(["origin", "access-control-request-headers", "access-control-request-method"]),
            $"the preflight's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
        Ensure(body.Length == 0, $"a preflight answer carries no body, got {S3Fidelity.Head(body, 300)}");
    }

    private const string Check = "cors-preflight-allowed-origin-returns-rule-headers";
    private const string Origin = "https://app.example.com";
    private const string Key = "spa/assets/data.json";

    /// <summary>The lower-cased, comma-separated tokens of a header value.</summary>
    private static HashSet<string> Tokens(string? value) =>
        (value ?? string.Empty).Split(',').Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToHashSet();

    private static string Show(Dictionary<string, string?> headers) =>
        "{" + string.Join(", ", headers.Select(h => $"{Py.Repr(h.Key)}: {Py.Repr(h.Value)}")) + "}";

    /// <summary>_new_bucket(ctx, s3): a uniquely-named bucket tagged with the check; emptied and deleted at cleanup.</summary>
    private static async Task<string> NewBucket(FidelityContext ctx, AmazonS3Client s3)
    {
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = name, TagSet = [new Tag { Key = "sim2real", Value = Check }] });
        return name;
    }

    private static Task PutJson(AmazonS3Client s3, string bucket) =>
        s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, ContentBody = "{\"ok\": true}", ContentType = "application/json" });

    private static string Leaked(Dictionary<string, string> headers) =>
        Py.Repr(headers.Keys.Where(k => k.StartsWith("access-control-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList());

    private static bool NoneLeaked(Dictionary<string, string> headers) =>
        !headers.Keys.Any(k => k.StartsWith("access-control-", StringComparison.Ordinal));
}
