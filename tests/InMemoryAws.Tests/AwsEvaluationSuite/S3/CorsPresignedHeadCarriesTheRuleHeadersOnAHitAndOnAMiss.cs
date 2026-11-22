// Translated from behavioral/s3/check_cors_presigned_head_carries_the_rule_headers_on_a_hit_and_on_a_miss.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-head-carries-the-rule-headers-on-a-hit-and-on-a-miss</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedHeadCarriesTheRuleHeadersOnAHitAndOnAMiss
{
    private const string Check = "cors-presigned-head-carries-the-rule-headers-on-a-hit-and-on-a-miss";
    private const string Site = "https://viewer.example.net";
    private const string StoredKey = "thumbnails/stored.bin";
    private const string AbsentKey = "thumbnails/never-written.bin";
    private static readonly byte[] ObjectBody = "0123456789abcdef"u8.ToArray();

    /// <summary>
    /// A presigned HEAD carrying an allowed Origin answers the rule's CORS headers both for a
    /// stored key and for a key that does not exist, where the bodiless 404 carries no other evidence.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-head-carries-the-rule-headers-on-a-hit-and-on-a-miss")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-behav-head-");
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = StoredKey,
            InputStream = new MemoryStream(ObjectBody),
            ContentType = "application/octet-stream",
        });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Site], AllowedMethods = ["HEAD"], AllowedHeaders = ["content-type"] }] },
        });
        var origin = new Dictionary<string, string> { ["Origin"] = Site };
        var hit = await S3Fidelity.AwaitAnswer(
            ctx,
            "HEAD",
            SignedHead(ctx, bucket, StoredKey),
            origin,
            a => a.Headers.GetValueOrDefault("access-control-allow-origin") == Site,
            (a, budget) => $"after {budget:F0}s the HEAD answered {a.Status} with Allow-Origin {Py.Repr(a.Headers.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}");
        Ensure(hit.Status == 200, $"the HEAD of the stored key answered {hit.Status}, expected 200");
        Ensure(
            hit.Headers.GetValueOrDefault("access-control-allow-methods") == "HEAD",
            $"the HEAD of the stored key answered Access-Control-Allow-Methods {Py.Repr(hit.Headers.GetValueOrDefault("access-control-allow-methods"))}, expected 'HEAD'");
        Ensure(
            hit.Headers.GetValueOrDefault("content-length") == ObjectBody.Length.ToString(CultureInfo.InvariantCulture),
            $"the HEAD of the stored key reported Content-Length {Py.Repr(hit.Headers.GetValueOrDefault("content-length"))}, expected {ObjectBody.Length}");
        Ensure(hit.Body.Length == 0, $"the HEAD of the stored key returned a body: {S3Fidelity.Head(hit.Body, 300)}");

        var miss = await S3Fidelity.Send(ctx, "HEAD", SignedHead(ctx, bucket, AbsentKey), origin);
        Ensure(miss.Status == 404, $"the HEAD of the absent key answered {miss.Status}, expected 404");
        Ensure(miss.Body.Length == 0, $"the HEAD of the absent key returned a body: {S3Fidelity.Head(miss.Body, 300)}");
        Ensure(
            miss.Headers.GetValueOrDefault("access-control-allow-origin") == Site,
            $"the HEAD of the absent key answered Access-Control-Allow-Origin {Py.Repr(miss.Headers.GetValueOrDefault("access-control-allow-origin"))}, expected {Py.Repr(Site)}");
        Ensure(
            miss.Headers.GetValueOrDefault("access-control-allow-methods") == "HEAD",
            $"the HEAD of the absent key answered Access-Control-Allow-Methods {Py.Repr(miss.Headers.GetValueOrDefault("access-control-allow-methods"))}, expected 'HEAD'");
        Ensure(
            (miss.Headers.GetValueOrDefault("vary") ?? string.Empty).Contains("origin", StringComparison.OrdinalIgnoreCase),
            $"the HEAD of the absent key answered Vary {Py.Repr(miss.Headers.GetValueOrDefault("vary"))}, which must name Origin");
    }

    /// <summary><c>_signed_head(s3, bucket, key)</c>: a presigned HeadObject URL.</summary>
    private static string SignedHead(FidelityContext ctx, string bucket, string key) => S3Fidelity.Presign(ctx, "HEAD", bucket, key, 600);
}
