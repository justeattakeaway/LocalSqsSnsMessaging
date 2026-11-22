// Translated from behavioral/s3/check_cors_presigned_get_from_unlisted_origin_rejected_without_cors_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-get-from-unlisted-origin-rejected-without-cors-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedGetFromUnlistedOriginRejectedWithoutCorsHeaders
{
    private const string Check = "cors-presigned-get-from-unlisted-origin-rejected-without-cors-headers";
    private const string Allowed = "https://app.example.com";
    private const string Other = "https://other.example.net";
    private const string Key = "docs/readme.txt";
    private static readonly byte[] Body = "hello from the bucket"u8.ToArray();

    /// <summary>
    /// A presigned GET from an origin no CORS rule lists is served but carries no Access-Control-*
    /// header and no Vary, while the same URL from the listed origin carries the rule's headers.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-get-from-unlisted-origin-rejected-without-cors-headers")]
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
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Allowed], AllowedMethods = ["GET"] }] },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Allowed, ["Access-Control-Request-Method"] = "GET" }, 200);
        var url = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);

        // Positive control: the listed origin is granted on the same URL.
        var (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Allowed });
        Ensure(status == 200, $"the presigned GET from the listed origin answered {status}: {S3Fidelity.Head(body, 300)}");
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Allowed,
            ["access-control-allow-methods"] = "GET",
            ["access-control-allow-credentials"] = "true",
        };
        Ensure(
            S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want),
            $"the listed origin's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the listed origin's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");

        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Other });
        Ensure(status == 200, $"the presigned GET from an unlisted origin answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(body.SequenceEqual(Body), $"the presigned GET from an unlisted origin served {S3Fidelity.Head(body, 300)}");
        Ensure(S3Fidelity.Cors(headers).Count == 0, $"the unlisted origin must get no Access-Control-* header, got {Py.Repr(S3Fidelity.Cors(headers))}");
        Ensure(!headers.ContainsKey("vary"), $"the unlisted origin's answer has Vary {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }
}
