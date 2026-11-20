// Translated from behavioral/s3/check_s3_ssec_and_endpoint_enforcement_ssec_upload_part_copy_rekeys_an_ssec_source_into_a_multipart_object.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.s3-ssec-and-endpoint-enforcement-ssec-upload-part-copy-rekeys-an-ssec-source-into-a-multipart-object</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3SsecAndEndpointEnforcementSsecUploadPartCopyRekeysAnSsecSourceIntoAMultipartObject
{
    /// <summary>
    /// UploadPartCopy re-keys ranges of an SSE-C source into an SSE-C multipart object: each copy needs the
    /// source key and the upload key, and the result answers only to the new key.
    /// </summary>
    [Test]
    [DisplayName("s3.s3-ssec-and-endpoint-enforcement-ssec-upload-part-copy-rekeys-an-ssec-source-into-a-multipart-object")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string check = "s3-ssec-and-endpoint-enforcement-ssec-upload-part-copy-rekeys-an-ssec-source-into-a-multipart-object";
        const int mib = 1024 * 1024;
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.EdgeBucket(ctx, check);
        await S3Fidelity.AllowSsec(s3, bucket);
        var kSrc = RandomNumberGenerator.GetBytes(32);
        var kDst = RandomNumberGenerator.GetBytes(32);
        var source = RandomNumberGenerator.GetBytes(6 * mib);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "source.bin",
            InputStream = new MemoryStream(source),
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(kSrc),
        });
        var mpu = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "assembled.bin",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(kDst),
        });
        var uploadId = mpu.UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, "assembled.bin", uploadId));

        CopyPartRequest Copy(int partNumber, long first, long last, bool sourceKey, bool destinationKey) => new()
        {
            SourceBucket = bucket,
            SourceKey = "source.bin",
            DestinationBucket = bucket,
            DestinationKey = "assembled.bin",
            UploadId = uploadId,
            PartNumber = partNumber,
            FirstByte = first,
            LastByte = last,
            CopySourceServerSideEncryptionCustomerMethod = sourceKey ? ServerSideEncryptionCustomerMethod.AES256 : null,
            CopySourceServerSideEncryptionCustomerProvidedKey = sourceKey ? S3Fidelity.B64(kSrc) : null,
            ServerSideEncryptionCustomerMethod = destinationKey ? ServerSideEncryptionCustomerMethod.AES256 : null,
            ServerSideEncryptionCustomerProvidedKey = destinationKey ? S3Fidelity.B64(kDst) : null,
        };

        // Without the SOURCE key, then without the UPLOAD's key: both refused, no part left behind.
        var (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.CopyPartAsync(Copy(1, 0, (5 * mib) - 1, sourceKey: false, destinationKey: true)));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, S3Fidelity.StoredSse),
            $"UploadPartCopy without the SOURCE key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected InvalidRequest/400");
        (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.CopyPartAsync(Copy(1, 0, (5 * mib) - 1, sourceKey: true, destinationKey: false)));
        Ensure(
            (code, status, msg) == ("InvalidRequest", 400, S3Fidelity.InitiateEncrypted),
            $"UploadPartCopy without the UPLOAD's key returned {Py.Repr(code)}/{status} ({Py.Repr(msg)}); expected InvalidRequest/400");
        var listed = (await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = "assembled.bin", UploadId = uploadId })).Parts ?? [];
        Ensure(listed.Count == 0, $"the two refused part copies left parts behind: {listed.Count}");

        // With both keys: two ranges copied, assembled, readable only with the new key.
        var r1 = await s3.CopyPartAsync(Copy(1, 0, (5 * mib) - 1, sourceKey: true, destinationKey: true));
        var r2 = await s3.CopyPartAsync(Copy(2, 5 * mib, source.Length - 1, sourceKey: true, destinationKey: true));
        var sizes = ((await s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = "assembled.bin", UploadId = uploadId })).Parts ?? [])
            .Select(p => (p.PartNumber ?? 0, p.Size ?? 0)).ToList();
        Ensure(sizes.SequenceEqual([(1, (long)(5 * mib)), (2, mib)]), $"ListParts after the copies: [{string.Join(", ", sizes)}]");
        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = "assembled.bin",
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, r1.ETag), S3Fidelity.Part(2, r2.ETag)],
            SSECustomerAlgorithm = "AES256",
            SSECustomerKey = S3Fidelity.B64(kDst),
            SSECustomerKeyMD5 = S3Fidelity.B64(MD5.HashData(kDst)),
        });
        using (var got = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = "assembled.bin",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(kDst),
        }))
        {
            var body = await S3Fidelity.ReadBody(got);
            Ensure(body.SequenceEqual(source), $"the assembled object ({body.Length} bytes) is not the source's plaintext");
        }

        (code, status, msg) = await S3Fidelity.ErrorTriple(() => s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = "assembled.bin",
            ServerSideEncryptionCustomerMethod = ServerSideEncryptionCustomerMethod.AES256,
            ServerSideEncryptionCustomerProvidedKey = S3Fidelity.B64(kSrc),
        }));
        Ensure(
            (code, status, msg) == ("AccessDenied", 403, S3Fidelity.WrongSecret),
            $"reading the re-keyed object with the SOURCE's key returned {Py.Repr(code)}/{status} " +
            $"({Py.Repr(msg)}); expected AccessDenied/403");
        var hdrs = S3Fidelity.SseCustomerHeaders(kDst);
        var url = S3Fidelity.Presign(ctx, "GET", bucket, "assembled.bin", 300, signedHeaders: hdrs, pathStyle: true);
        var (rawStatus, _, raw) = await S3Fidelity.Send(ctx, "GET", url, hdrs);
        Ensure(
            rawStatus == 200 && raw.SequenceEqual(source),
            $"presigned SSE-C GET of the assembled object returned HTTP {rawStatus} ({raw.Length} bytes); " +
            "expected 200 with the source's plaintext");
    }
}
