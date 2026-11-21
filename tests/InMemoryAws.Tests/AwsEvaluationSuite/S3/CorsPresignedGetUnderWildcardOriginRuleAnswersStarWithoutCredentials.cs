// Translated from behavioral/s3/check_cors_presigned_get_under_wildcard_origin_rule_answers_star_without_credentials.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-get-under-wildcard-origin-rule-answers-star-without-credentials</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedGetUnderWildcardOriginRuleAnswersStarWithoutCredentials
{
    private const string Check = "cors-presigned-get-under-wildcard-origin-rule-answers-star-without-credentials";
    private const string Origin = "https://viewer.example.org";
    private const string Key = "public/logo.txt";
    private static readonly byte[] Body = "logo bytes"u8.ToArray();

    /// <summary>
    /// A presigned GET with any Origin under an AllowedOrigins "*" rule answers Allow-Origin "*",
    /// the rule's methods and Vary, and no Allow-Credentials header.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-get-under-wildcard-origin-rule-answers-star-without-credentials")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(Body), ContentType = "text/plain" });
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = ["*"], AllowedMethods = ["GET", "HEAD"], MaxAgeSeconds = 120 }] },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "GET" }, 200);
        var url = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);
        var (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200, $"the presigned GET answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(body.SequenceEqual(Body), $"the presigned GET served {S3Fidelity.Head(body, 300)}, expected {S3Fidelity.Head(Body)}");
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = "*",
            ["access-control-allow-methods"] = "GET, HEAD",
            ["access-control-max-age"] = "120",
        };
        Ensure(
            S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want),
            $"the wildcard rule's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the wildcard rule's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }
}
