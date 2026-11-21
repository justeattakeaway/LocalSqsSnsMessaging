// Translated from behavioral/s3/check_cors_presigned_get_with_tampered_signature_rejected_with_cors_headers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.cors-presigned-get-with-tampered-signature-rejected-with-cors-headers</summary>
[Category("AwsEvaluationSuite")]
public sealed class CorsPresignedGetWithTamperedSignatureRejectedWithCorsHeaders
{
    private const string Check = "cors-presigned-get-with-tampered-signature-rejected-with-cors-headers";
    private const string Origin = "https://app.example.com";
    private const string Key = "docs/private.txt";
    private const string SigMsg = "The request signature we calculated does not match the signature you provided. Check your key and signing method.";
    private static readonly byte[] Body = "private bytes"u8.ToArray();

    /// <summary>
    /// A presigned GET from an allowed origin whose signature was altered answers 403
    /// SignatureDoesNotMatch with its exact message and the matching rule's CORS headers.
    /// </summary>
    [Test]
    [DisplayName("s3.cors-presigned-get-with-tampered-signature-rejected-with-cors-headers")]
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
            Configuration = new CORSConfiguration { Rules = [new CORSRule { AllowedOrigins = [Origin], AllowedMethods = ["GET"] }] },
        });
        await S3Fidelity.UntilPreflightStatus(
            ctx, S3Fidelity.ObjectUrl(ctx, bucket, Key), new Dictionary<string, string> { ["Origin"] = Origin, ["Access-Control-Request-Method"] = "GET" }, 200);
        var url = S3Fidelity.Presign(ctx, "GET", bucket, Key, 300, pathStyle: true);
        var want = new Dictionary<string, string>
        {
            ["access-control-allow-origin"] = Origin,
            ["access-control-allow-methods"] = "GET",
            ["access-control-allow-credentials"] = "true",
        };

        // Positive control: the untouched URL is served with the rule's headers.
        var (status, headers, body) = await S3Fidelity.Send(ctx, "GET", url, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 200 && body.SequenceEqual(Body), $"the untouched URL answered {status}: {S3Fidelity.Head(body, 300)}");
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the untouched URL's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}");

        var query = S3Fidelity.SignedQuery(url);
        var names = query.Select(p => p.Key).ToList();
        Ensure(names.Contains("X-Amz-Signature"), $"the presigned URL carries no X-Amz-Signature: {Py.Repr(names)}");
        var flipped = query
            .Select(p => p.Key == "X-Amz-Signature"
                ? new KeyValuePair<string, string>(p.Key, p.Value[..^1] + (p.Value[^1] != '0' ? "0" : "1"))
                : p)
            .ToList();
        var bad = S3Fidelity.Rebuild(url, flipped);
        (status, headers, body) = await S3Fidelity.Send(ctx, "GET", bad, new Dictionary<string, string> { ["Origin"] = Origin });
        Ensure(status == 403, $"the altered signature answered {status}: {S3Fidelity.Head(body, 300)}");

        // The error body names the signing key id, so only Code and Message are quoted.
        var got = S3Fidelity.XmlCodeMessage(body);
        Ensure(got == ("SignatureDoesNotMatch", SigMsg), $"the altered signature's error is {S3Fidelity.Repr(got)}");
        Ensure(S3Fidelity.SameHeaders(S3Fidelity.Cors(headers), want), $"the 403's CORS headers are {Py.Repr(S3Fidelity.Cors(headers))}, expected {Py.Repr(want)}");
        Ensure(S3Fidelity.HeaderTokens(headers.GetValueOrDefault("vary")).SetEquals(S3Fidelity.CorsVary), $"the 403's Vary is {Py.Repr(headers.GetValueOrDefault("vary"))}");
    }
}
