// Translated from behavioral/s3/check_cors_presigned_put_from_origin_whose_rule_omits_put_rejected_without_cors_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-put-from-origin-whose-rule-omits-put-rejected-without-cors-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedPutFromOriginWhoseRuleOmitsPutRejectedWithoutCorsHeaders
{
    private const string Check = "cors-presigned-put-from-origin-whose-rule-omits-put-rejected-without-cors-headers";
    private const string Origin = "https://app.example.com";
    private const string Key = "uploads/note.txt";
    private static readonly byte[] Body = "written through a presigned url"u8.ToArray();

    /// <summary>
    /// A presigned PUT from an origin whose CORS rule allows only GET stores the object but carries
    /// no Access-Control-* header; a presigned GET from that origin carries the rule's headers.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-put-from-origin-whose-rule-omits-put-rejected-without-cors-headers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check);
        await s3.PutCORSConfigurationAsync(new PutCORSConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["GET"], ExposeHeaders = ["ETag"] }] },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "GET" }, 200);
        var putUrl = S3Fidelity.Presign(ctx, "PUT", bucket, Key, 300, pathStyle: true);
        var getUrl = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);
        var (status, headers, body) = await S3Fidelity.Send(ctx, "PUT", putUrl, new Dictionary<string, string> { ["Origin"] = Origin }, Body);
        Ensure(status == 200, $"the presigned PUT answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(S3Fidelity.Cors(headers).Count == 0, $"a PUT the rule does not list must get no Access-Control-* header, got {Py.Repr(S3Fidelity.Cors(headers))}");
        Ensure(!headers.ContainsKey("vary"), $"the presigned PUT's answer has Vary {Py.Repr(headers.GetValueOrDefault("vary"))}");

        // Positive control: the same origin's GET matches the rule and reads the stored bytes.
        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", getUrl, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200, $"the presigned GET answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(body.SequenceEqual(Body), $"the presigned GET served {S3Fidelity.Head(body, 300)}, expected {S3Fidelity.Head(Body)}");
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "GET",
            ["access-control-expose-headers"] = "ETag",
            ["access-control-allow-credentials"] = "true",
        };
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the presigned GET's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the presigned GET's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }
}
