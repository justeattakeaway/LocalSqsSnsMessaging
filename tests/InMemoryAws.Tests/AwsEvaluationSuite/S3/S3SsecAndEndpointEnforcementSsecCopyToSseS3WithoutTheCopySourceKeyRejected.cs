// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_ssec_copy_to_sse_s3_without_the_copy_source_key_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-ssec-copy-to-sse-s3-without-the-copy-source-key-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementSsecCopyToSseS3WithoutTheCopySourceKeyRejected
{
    /// <summary>
    /// CopyObject from an SSE-C source to SSE-S3 is refused (400 InvalidRequest) without the right
    /// CopySource customer key and creates nothing; with it, the copy is re-encrypted as SSE-S3, readable
    /// keyless, and refuses the old customer key as inapplicable.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-ssec-copy-to-sse-s3-without-the-copy-source-key-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "s3-ssec-and-endpoint-enforcement-ssec-copy-to-sse-s3-without-the-copy-source-key-rejected";
        const string notApplicable = "The encryption parameters are not applicable to this object.";
        const string noSuchKey = "The specified key does not exist.";
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.EdgeBucket(ctx, check);
        await S3Fidelity.AllowSsec(s3, bucket);
        var key = RandomNumberGenerator.GetBytes(32);
        const string src = "ssec-source.bin";
        const string dst = "sse-s3-copy.bin";
        var payload = RandomNumberGenerator.GetBytes(64 * 1024);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = src,
            InputStream = new MemoryStream(payload),
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        });

        // Without the copy-source key: refused, nothing created.
        var (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = src,
            DestinationBucket = bucket,
            DestinationKey = dst,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        }));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, S3Fidelity.StoredSse),
            "CopyObject from an SSE-C source WITHOUT CopySourceSSECustomerKey returned " +
            $"{Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected InvalidRequest/400");
        (code, _, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(bucket, dst));
        Ensure((code, msg) == ("NoSuchKey", noSuchKey), $"the refused copy left a destination object behind ({Py.Repr(code)}: {Py.Repr(msg)})");

        // With the WRONG copy-source key: refused, nothing created.
        (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = src,
            DestinationBucket = bucket,
            DestinationKey = dst,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            CopySourceServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            CopySourceServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(RandomNumberGenerator.GetBytes(32)),
        }));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, S3Fidelity.ParamsMismatch),
            "CopyObject with the WRONG CopySourceSSECustomerKey returned " +
            $"{Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected InvalidRequest/400");
        (code, _, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(bucket, dst));
        Ensure((code, msg) == ("NoSuchKey", noSuchKey), $"the wrong-key copy left a destination object behind ({Py.Repr(code)}: {Py.Repr(msg)})");

        // With the right key: the copy is SSE-S3.
        var done = await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = src,
            DestinationBucket = bucket,
            DestinationKey = dst,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
            CopySourceServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            CopySourceServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        });
        Ensure(
            done.ServerSideEncryptionMethod == ServerSideEncryptionMethod.AES256,
            $"the keyed copy reported ServerSideEncryption {Py.Repr(done.ServerSideEncryptionMethod?.Value)}, expected 'AES256' (SSE-S3)");
        Ensure(
            done.ServerSideEncryptionCustomerMethod is null || done.ServerSideEncryptionCustomerMethod == ServerSideEncryptionCustomerMethod.None,
            $"the SSE-S3 destination still claims SSE-C ({Py.Repr(done.ServerSideEncryptionCustomerMethod?.Value)})");
        var head = await s3.GetObjectMetadataAsync(bucket, dst);
        Ensure(
            head.ServerSideEncryptionMethod == ServerSideEncryptionMethod.AES256
                && (head.ServerSideEncryptionCustomerMethod is null || head.ServerSideEncryptionCustomerMethod == ServerSideEncryptionCustomerMethod.None),
            $"HeadObject(dst) reports SSE {Py.Repr(head.ServerSideEncryptionMethod?.Value)} / SSE-C " +
            $"{Py.Repr(head.ServerSideEncryptionCustomerMethod?.Value)}; expected AES256 / None");
        Ensure(head.ContentLength == payload.Length, $"destination is {head.ContentLength} bytes, source was {payload.Length}");

        // Readable keyless over a presigned raw GET.
        var url = S3Fidelity.Presign(ctx, "GET", bucket, dst, 300, pathStyle: true);
        var (rawStatus, headers, raw) = await S3Fidelity.Send(ctx, "GET", url);
        Ensure(rawStatus == 200, $"presigned GET of the SSE-S3 copy returned HTTP {rawStatus}: {S3Fidelity.Head(raw, 300)}");
        Ensure(raw.SequenceEqual(payload), "the SSE-S3 copy's bytes differ from the SSE-C source's plaintext");
        Ensure(
            headers.GetValueOrDefault("x-amz-server-side-encryption") == "AES256",
            "raw GET reported x-amz-server-side-encryption " +
            $"{Py.Repr(headers.GetValueOrDefault("x-amz-server-side-encryption"))}, expected 'AES256'");

        // The old customer key is not applicable to the copy.
        (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = dst,
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(key),
        }));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, notApplicable),
            $"GetObject(dst) WITH the old customer key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); " +
            "expected InvalidRequest/400 — the copy is not SSE-C any more");

        // The source still refuses a keyless raw GET.
        var srcUrl = S3Fidelity.Presign(ctx, "GET", bucket, src, 300, pathStyle: true);
        (rawStatus, _, raw) = await S3Fidelity.Send(ctx, "GET", srcUrl);
        var (xmlCode, xmlMessage) = S3Fidelity.XmlCodeMessage(raw);
        Ensure(
            (rawStatus, xmlCode, xmlMessage) == (400, "InvalidRequest", S3Fidelity.StoredSse),
            $"keyless raw GET of the SSE-C SOURCE returned HTTP {rawStatus} code {Py.Repr(xmlCode)} " +
            $"message {Py.Repr(xmlMessage)}; expected 400 InvalidRequest {Py.Repr(S3Fidelity.StoredSse)}");
        Ensure(raw.AsSpan().IndexOf(payload.AsSpan(0, 64)) < 0, "the keyless raw GET of the source leaked its bytes");
    }
}
