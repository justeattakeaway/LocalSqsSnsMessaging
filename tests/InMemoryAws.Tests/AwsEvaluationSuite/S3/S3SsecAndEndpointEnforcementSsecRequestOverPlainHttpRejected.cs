// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_ssec_request_over_plain_http_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-ssec-request-over-plain-http-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementSsecRequestOverPlainHttpRejected
{
    /// <summary>
    /// S3 refuses an SSE-C request over plaintext HTTP with InvalidArgument and writes nothing, while the
    /// byte-identical presigned request over HTTPS succeeds and plain HTTP serves a non-SSE-C object: the
    /// transport alone decides, with no bucket policy involved.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-ssec-request-over-plain-http-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "s3-ssec-and-endpoint-enforcement-ssec-request-over-plain-http-rejected";
        const string insecureSsec = "Requests specifying Server Side Encryption with Customer provided keys must be made over a secure connection.";
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.EdgeBucket(ctx, check);
        await S3Fidelity.AllowSsec(s3, bucket);
        var key = RandomNumberGenerator.GetBytes(32);
        var hdrs = S3Fidelity.SseCustomerHeaders(key);
        var secret = RandomNumberGenerator.GetBytes(4096);
        var plain = "plaintext-transport-control"u8.ToArray();
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "ssec.bin",
            InputStream = new MemoryStream(secret),
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "plain.txt", InputStream = new MemoryStream(plain) });

        // Control: plaintext http serves a non-SSE-C object.
        var plainUrl = S3Fidelity.Presign(ctx, "GET", bucket, "plain.txt", 300, pathStyle: true);
        var (status, _, raw) = await S3Fidelity.Send(ctx, "GET", S3Fidelity.AsHttp(plainUrl));
        Ensure(
            status == 200 && raw.SequenceEqual(plain),
            $"control: presigned GET of a non-SSE-C object over http:// returned HTTP {status} " +
            $"{S3Fidelity.Head(raw)}; plaintext transport must work for the refusal below to mean anything");

        // SSE-C GET over http:// is refused; its https:// twin is served.
        var getUrl = S3Fidelity.Presign(ctx, "GET", bucket, "ssec.bin", 300, signedHeaders: hdrs, pathStyle: true);
        Ensure(getUrl.StartsWith("https://", StringComparison.Ordinal), $"presigned URL is not https: {Py.Repr(getUrl.Split('?')[0])}");
        (status, _, raw) = await S3Fidelity.Send(ctx, "GET", S3Fidelity.AsHttp(getUrl), hdrs);
        var (code, message) = S3Fidelity.XmlCodeMessage(raw);
        Ensure(
            (status, code, message) == (400, "InvalidArgument", insecureSsec),
            $"SSE-C GET over http:// returned HTTP {status} code {Py.Repr(code)}; expected 400 " +
            $"InvalidArgument — body {S3Fidelity.Head(raw, 300)}");
        Ensure(raw.AsSpan().IndexOf(secret.AsSpan(0, 64)) < 0, "the refused plaintext SSE-C GET leaked object bytes");
        (status, _, raw) = await S3Fidelity.Send(ctx, "GET", getUrl, hdrs);
        Ensure(
            status == 200 && raw.SequenceEqual(secret),
            $"the https:// twin of the refused GET returned HTTP {status} ({raw.Length} bytes); expected " +
            $"200 with the {secret.Length} stored bytes");

        // SSE-C PUT over http:// is refused and writes nothing; its https:// twin writes.
        var putUrl = S3Fidelity.Presign(ctx, "PUT", bucket, "over-http.bin", 300, signedHeaders: hdrs, pathStyle: true);
        (status, _, raw) = await S3Fidelity.Send(ctx, "PUT", S3Fidelity.AsHttp(putUrl), hdrs, "must not land"u8.ToArray());
        (code, message) = S3Fidelity.XmlCodeMessage(raw);
        Ensure(
            (status, code, message) == (400, "InvalidArgument", insecureSsec),
            $"SSE-C PUT over http:// returned HTTP {status} code {Py.Repr(code)}; expected 400 " +
            $"InvalidArgument — body {S3Fidelity.Head(raw, 300)}");
        var sseGet = new GetObjectRequest
        {
            BucketName = bucket,
            Key = "over-http.bin",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        };
        var (errorCode, _, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(sseGet));
        Ensure(
            (errorCode, msg) == ("NoSuchKey", "The specified key does not exist."),
            $"the refused plaintext SSE-C PUT created an object ({Py.Repr(errorCode)}: {Py.Repr(msg)})");
        (status, _, raw) = await S3Fidelity.Send(ctx, "PUT", putUrl, hdrs, "landed over tls"u8.ToArray());
        Ensure(status == 200, $"the https:// twin of the refused PUT returned HTTP {status}: {S3Fidelity.Head(raw, 300)}");
        using var back = await s3.GetObjectAsync(sseGet);
        var backBytes = await S3Fidelity.ReadBody(back);
        Ensure(backBytes.SequenceEqual("landed over tls"u8.ToArray()), $"the https:// SSE-C PUT read back as {S3Fidelity.Head(backBytes)}");
    }
}
