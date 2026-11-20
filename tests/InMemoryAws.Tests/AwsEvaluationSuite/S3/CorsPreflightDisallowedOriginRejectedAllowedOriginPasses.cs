// Translated from behavioral/s3/check_cors_preflight_disallowed_origin_rejected_allowed_origin_passes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-disallowed-origin-rejected-allowed-origin-passes</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightDisallowedOriginRejectedAllowedOriginPasses
{
    /// <summary>
    /// A preflight from an origin no CORS rule allows is refused 403 AccessForbidden with no
    /// Access-Control-* headers; the allowed origin with the same method still passes.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-disallowed-origin-rejected-allowed-origin-passes")]
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
                Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["GET", "PUT"], AllowedHeaders = ["*"], MaxAgeSeconds = 600 }],
            },
        });
        var url = S3Fidelity.ObjectUrl(ctx, bucket, Key);

        // Positive control: the allowed origin and method pass, so a target that refuses every
        // preflight cannot pass the refusals below.
        var (status, headers, body) = await S3Fidelity.UntilPreflightStatus(
            ctx, url, new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "PUT" }, 200);
        Ensure(status == 200, $"the allowed preflight must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            headers.GetValueOrDefault("access-control-allow-origin") == Origin,
            $"the allowed preflight's Allow-Origin is {Py.Repr(headers.GetValueOrDefault("access-control-allow-origin"))}");

        var requestHeaders = new Dictionary<string, string> { ["Origin"] = OtherOrigin, ["Access-Control-Request-Method"] = "PUT" };
        (status, headers, body) = await S3Fidelity.Http(ctx, "OPTIONS", url, requestHeaders);
        Ensure(status == 403, $"the disallowed-origin preflight must answer 403, got {status}: {S3Fidelity.Head(body, 300)}");
        var got = S3Fidelity.XmlError(body);
        (string? Code, string? Message, string? Method, string? ResourceType) want = ("AccessForbidden", NotAllowedMessage, requestHeaders["Access-Control-Request-Method"], "OBJECT");
        Ensure(got == want, $"the disallowed-origin preflight's error is {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(want)}");
        Ensure(NoneLeaked(headers), $"the disallowed-origin refusal carries CORS headers {Leaked(headers)}");
    }

    private const string Check = "cors-preflight-disallowed-origin-rejected-allowed-origin-passes";
    private const string Origin = "https://app.example.com";
    private const string OtherOrigin = "https://other.example.net";
    private const string Key = "spa/assets/data.json";
    private const string NotAllowedMessage =
        "CORSResponse: This CORS request is not allowed. This is usually because the evalution of " +
        "Origin, request method / Access-Control-Request-Method or Access-Control-Request-Headers " +
        "are not whitelisted by the resource's CORS spec.";

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
