// Translated from behavioral/s3/check_cors_preflight_on_bucket_without_cors_configuration_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-on-bucket-without-cors-configuration-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightOnBucketWithoutCorsConfigurationRejected
{
    /// <summary>
    /// A preflight to an object of a bucket with no CORS configuration is refused 403
    /// AccessForbidden "CORS is not enabled for this bucket."; a configured sibling bucket answers 200.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-on-bucket-without-cors-configuration-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var configured = await NewBucket(ctx, s3);
        var bare = await NewBucket(ctx, s3);
        foreach (var bucket in new[] { configured, bare })
        {
            await PutJson(s3, bucket);
        }

        await PutGetOnlyRule(s3, configured);

        // Positive control: the same preflight passes where a rule allows it.
        var (status, headers, body) = await S3Fidelity.UntilPreflightStatus(ctx, S3Fidelity.ObjectUrl(ctx, configured, Key), Preflight, 200);
        Ensure(status == 200, $"the configured bucket's preflight must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            headers.GetValueOrDefault("access-control-allow-origin") == Origin,
            $"the configured bucket's Allow-Origin is {Py.Repr(headers.GetValueOrDefault("access-control-allow-origin"))}");

        var error = await S3Fidelity.Refusal(() => s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bare }));
        Ensure(
            error == ("NoSuchCORSConfiguration", "The CORS configuration does not exist"),
            $"GetBucketCors on the bucket without CORS answered {S3Fidelity.Repr(error)}");

        (status, headers, body) = await S3Fidelity.Http(ctx, "OPTIONS", S3Fidelity.ObjectUrl(ctx, bare, Key), Preflight);
        Ensure(status == 403, $"the preflight without CORS configuration must answer 403, got {status}: {S3Fidelity.Head(body, 300)}");
        var got = S3Fidelity.XmlError(body);
        (string? Code, string? Message, string? Method, string? ResourceType) want = ("AccessForbidden", "CORSResponse: CORS is not enabled for this bucket.", "GET", "BUCKET");
        Ensure(got == want, $"the preflight without CORS configuration was refused with {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(want)}");
        Ensure(NoneLeaked(headers), $"the refusal carries CORS headers {Leaked(headers)}");
    }

    private const string Check = "cors-preflight-on-bucket-without-cors-configuration-rejected";
    private const string Origin = "https://app.example.com";
    private const string Key = "spa/assets/data.json";

    private static Dictionary<string, string> Preflight => new() { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "GET" };

    private static Task PutGetOnlyRule(AmazonS3Client s3, string bucket) =>
        s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["GET"] }] },
        });

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
