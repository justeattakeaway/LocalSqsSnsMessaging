// Translated from behavioral/s3/check_cors_first_matching_rule_governs_presigned_get.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-first-matching-rule-governs-presigned-get</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsFirstMatchingRuleGovernsPresignedGet
{
    private const string Check = "cors-first-matching-rule-governs-presigned-get";
    private const string Listed = "https://app.example.com";
    private const string Other = "https://other.example.net";
    private const string Key = "docs/report.txt";
    private static readonly byte[] Body = "report"u8.ToArray();

    /// <summary>
    /// With two rules matching a presigned GET, S3 answers with the first one's headers: a listed
    /// origin gets rule one's, any other origin falls through to the wildcard rule two.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-first-matching-rule-governs-presigned-get")]
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
            Configuration = new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule { AllowedOrigins = [Listed], AllowedMethods = ["GET"], ExposeHeaders = ["ETag"], MaxAgeSeconds = 60 },
                    new CORSRule { AllowedOrigins = ["*"], AllowedMethods = ["GET", "HEAD"], ExposeHeaders = ["x-amz-request-id"], MaxAgeSeconds = 600 },
                ],
            },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Listed, ["Access-Control-Request-Method"] = "GET" }, 200);
        var url = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);

        var (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Listed });
        Ensure(status == 200 && body.SequenceEqual(Body), $"the listed origin's GET answered {status}: {S3Fidelity.Head(body, 300)}");
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Listed,
            ["access-control-allow-methods"] = "GET",
            ["access-control-expose-headers"] = "ETag",
            ["access-control-max-age"] = "60",
            ["access-control-allow-credentials"] = "true",
        };
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the listed origin got {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the listed origin's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");

        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Other });
        Ensure(status == 200 && body.SequenceEqual(Body), $"the other origin's GET answered {status}: {S3Fidelity.Head(body, 300)}");
        want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = "*",
            ["access-control-allow-methods"] = "GET, HEAD",
            ["access-control-expose-headers"] = "x-amz-request-id",
            ["access-control-max-age"] = "600",
        };
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the other origin got {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the other origin's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }
}
