// Translated from behavioral/s3/check_cors_preflight_on_presigned_put_url_answers_rule_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-preflight-on-presigned-put-url-answers-rule-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPreflightOnPresignedPutUrlAnswersRuleHeaders
{
    private const string Check = "cors-preflight-on-presigned-put-url-answers-rule-headers";
    private const string Origin = "https://shop.example.org";
    private const string Key = "uploads/photo.bin";
    private static readonly byte[] Body = [0x00, 0x01, .. "photo"u8];

    /// <summary>
    /// A browser preflight sent to the presigned PUT URL itself, query string and all, is answered by
    /// the CORS rule, and the PUT that follows on that URL stores the bytes with the same grant.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-preflight-on-presigned-put-url-answers-rule-headers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["PUT"], MaxAgeSeconds = 300 }] },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "PUT" }, 200);
        var url = S3Fidelity.Presign(ctx, "PUT", bucket, Key, 300, pathStyle: true);
        Ensure(new Uri(url).Query.Contains("X-Amz-Signature=", StringComparison.Ordinal), "the URL is not presigned");
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "PUT",
            ["access-control-max-age"] = "300",
            ["access-control-allow-credentials"] = "true",
        };
        var (status, headers, body) = await S3Fidelity.Send(
            ctx, "OPTIONS", url, new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "PUT" });
        Ensure(status == 200, $"the preflight on the presigned URL answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(body.Length == 0, $"the preflight answered a body {S3Fidelity.Head(body, 300)}");
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the preflight's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the preflight's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");

        (status, headers, body) = await S3Fidelity.Send(ctx, "PUT", url, new Dictionary<string, string> { ["Origin"] = Origin }, Body);
        Ensure(status == 200, $"the presigned PUT answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the presigned PUT's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}");
        var got = await S3Fidelity.GetBytes(s3, bucket, Key);
        Ensure(got.SequenceEqual(Body), $"GetObject reads {S3Fidelity.Head(got)} after the presigned PUT, expected {S3Fidelity.Head(Body)}");
    }
}
