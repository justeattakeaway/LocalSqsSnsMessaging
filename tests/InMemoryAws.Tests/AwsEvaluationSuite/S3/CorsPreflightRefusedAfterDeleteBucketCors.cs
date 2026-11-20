// Translated from behavioral/s3/check_cors_preflight_refused_after_delete_bucket_cors.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-refused-after-delete-bucket-cors</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightRefusedAfterDeleteBucketCors
{
    /// <summary>
    /// A preflight a CORS rule allowed is refused 403 "CORS is not enabled for this bucket." once
    /// DeleteBucketCors removes the configuration, while the object itself stays readable.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-refused-after-delete-bucket-cors")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await NewBucket(ctx, s3);
        await PutJson(s3, bucket);
        await PutGetOnlyRule(s3, bucket);
        var url = S3Fidelity.ObjectUrl(ctx, bucket, Key);

        // Positive control: the rule allows the preflight before the delete.
        var (status, headers, body) = await S3Fidelity.UntilPreflightStatus(ctx, url, Preflight, 200);
        Ensure(status == 200, $"the preflight before DeleteBucketCors must answer 200, got {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(
            headers.GetValueOrDefault("access-control-allow-origin") == Origin,
            $"the preflight before DeleteBucketCors has Allow-Origin {Py.Repr(headers.GetValueOrDefault("access-control-allow-origin"))}");

        await s3.DeleteCORSConfigurationAsync(new DeleteCORSConfigurationRequest { BucketName = bucket });
        var error = await S3Fidelity.Refusal(() => s3.GetCORSConfigurationAsync(new GetCORSConfigurationRequest { BucketName = bucket }));
        Ensure(
            error == ("NoSuchCORSConfiguration", "The CORS configuration does not exist"),
            $"GetBucketCors after DeleteBucketCors answered {S3Fidelity.Repr(error)}");

        (status, headers, body) = await S3Fidelity.UntilPreflightStatus(ctx, url, Preflight, 403);
        Ensure(status == 403, $"the preflight after DeleteBucketCors must answer 403, got {status}: {S3Fidelity.Head(body, 300)}");
        var got = S3Fidelity.XmlError(body);
        (string? Code, string? Message, string? Method, string? ResourceType) want = ("AccessForbidden", "CORSResponse: CORS is not enabled for this bucket.", "GET", "BUCKET");
        Ensure(got == want, $"the preflight after DeleteBucketCors was refused with {S3Fidelity.Repr(got)}, expected {S3Fidelity.Repr(want)}");
        Ensure(NoneLeaked(headers), $"the refusal carries CORS headers {Leaked(headers)}");
        var read = System.Text.Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, Key));
        Ensure(read == "{\"ok\": true}", $"the object reads {Py.Repr(read)} after DeleteBucketCors");
    }

    private const string Check = "cors-preflight-refused-after-delete-bucket-cors";
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
