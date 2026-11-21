// Translated from behavioral/s3/check_cors_presigned_get_without_origin_on_configured_bucket_has_no_cors_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-get-without-origin-on-configured-bucket-has-no-cors-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedGetWithoutOriginOnConfiguredBucketHasNoCorsHeaders
{
    private const string Check = "cors-presigned-get-without-origin-on-configured-bucket-has-no-cors-headers";
    private const string Origin = "https://app.example.com";
    private const string Key = "docs/plain.txt";
    private static readonly byte[] Body = "plain"u8.ToArray();

    /// <summary>
    /// A presigned GET with no Origin header on a bucket whose CORS rule allows every origin is
    /// served with no Access-Control-* header and no Vary; with an Origin the same URL carries them.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-get-without-origin-on-configured-bucket-has-no-cors-headers")]
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
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = ["*"], AllowedMethods = ["GET"] }] },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "GET" }, 200);
        var url = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);
        var (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200 && body.SequenceEqual(Body), $"the GET with Origin answered {status}: {S3Fidelity.Head(body, 300)}");
        var want = new Dictionary<string, string> { ["access-control-allow-origin"] = "*", ["access-control-allow-methods"] = "GET" };
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the GET with Origin got {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");

        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string>());
        Ensure(status == 200 && body.SequenceEqual(Body), $"the GET without Origin answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(headers.GetValueOrDefault("content-type") == "text/plain", $"the GET without Origin has Content-Type {Py.Repr(headers.GetValueOrDefault("content-type"))}");
        Ensure(S3Fidelity.Cors(headers).Count == 0, $"the GET without Origin carries CORS headers {Py.Repr(S3Fidelity.Cors(headers))}");
        Ensure(!headers.ContainsKey("vary"), $"the GET without Origin has Vary {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }
}
