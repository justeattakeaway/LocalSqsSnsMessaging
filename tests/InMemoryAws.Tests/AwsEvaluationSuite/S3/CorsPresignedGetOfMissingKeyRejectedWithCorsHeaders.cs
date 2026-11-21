// Translated from behavioral/s3/check_cors_presigned_get_of_missing_key_rejected_with_cors_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-get-of-missing-key-rejected-with-cors-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedGetOfMissingKeyRejectedWithCorsHeaders
{
    private const string Check = "cors-presigned-get-of-missing-key-rejected-with-cors-headers";
    private const string Origin = "https://app.example.com";
    private const string Key = "docs/present.txt";
    private const string Missing = "docs/absent.txt";
    private static readonly byte[] Body = "present"u8.ToArray();

    /// <summary>
    /// A presigned GET from an allowed origin for a key that does not exist answers 404 NoSuchKey
    /// with its exact message and still carries the matching rule's CORS headers, so a browser can read it.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-get-of-missing-key-rejected-with-cors-headers")]
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
                Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["GET"], ExposeHeaders = ["ETag"], MaxAgeSeconds = 60 }],
            },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "GET" }, 200);
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "GET",
            ["access-control-expose-headers"] = "ETag",
            ["access-control-max-age"] = "60",
            ["access-control-allow-credentials"] = "true",
        };

        // Positive control: the present key is served with the same headers.
        var url = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);
        var (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200 && body.SequenceEqual(Body), $"the present key answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the present key's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}");

        url = S3Fidelity.Presign(ctx, "GET", bucket, Missing, 300, pathStyle: true);
        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 404, $"the missing key answered {status}: {S3Fidelity.Head(body, 300)}");
        var err = S3Fidelity.XmlFields(body);
        var got = (err?.GetValueOrDefault("Code"), err?.GetValueOrDefault("Message"), err?.GetValueOrDefault("Key"));
        Ensure(
            got == ("NoSuchKey", "The specified key does not exist.", Missing),
            $"the missing key's error (Code, Message, Key) is ({Py.Repr(got.Item1)}, {Py.Repr(got.Item2)}, {Py.Repr(got.Item3)})");
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the 404's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the 404's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }
}
