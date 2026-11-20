// Translated from behavioral/s3/check_multipart_upload_complete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-upload-complete</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartUploadComplete
{
    /// <summary>Multipart upload: initiate, upload 2 parts, complete — object is concatenation.</summary>
    [Test]
    [DisplayName("s3.multipart-upload-complete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "multipart/assembled.bin";
        var part1 = S3Fidelity.Repeat('A', 5 * 1024 * 1024); // 5 MB minimum part size
        var part2 = S3Fidelity.Repeat('B', 1024);
        byte[] whole = [.. part1, .. part2];

        var mpu = await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key });
        var uploadId = mpu.UploadId;

        var r1 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 1, InputStream = new MemoryStream(part1) });
        var r2 = await s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 2, InputStream = new MemoryStream(part2) });

        await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = [S3Fidelity.Part(1, r1.ETag), S3Fidelity.Part(2, r2.ETag)],
        });
        var got = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(got.AsSpan().SequenceEqual(whole), $"assembled size {got.Length} != expected {part1.Length + part2.Length}");

        var head = await s3.GetObjectMetadataAsync(bucket, key);
        Ensure(head.ContentLength == whole.Length, $"HeadObject ContentLength {head.ContentLength} != {whole.Length}");
        Ensure(head.ETag.EndsWith("-2\"", StringComparison.Ordinal), $"expected a 2-part multipart ETag ending '-2\"', got {Py.Repr(head.ETag)}");

        // The completed upload id is consumed: unreadable, unlisted, and refuses new parts.
        const string expectedMessage = "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.";
        var refused = await Err(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = uploadId }));
        AssertError(refused.Code, refused.Message, (int)refused.StatusCode, "ListParts on the completed upload", ["NoSuchUpload"], 404);
        Ensure(refused.Message == expectedMessage, $"expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(refused.Message)}");
        var uploads = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = key })).MultipartUploads ?? [];
        Ensure(uploads.All(u => u.UploadId != uploadId), "the completed upload is still listed by ListMultipartUploads");
        refused = await Err(() => s3.UploadPartAsync(new UploadPartRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartNumber = 3, InputStream = new MemoryStream("C"u8.ToArray()) }));
        AssertError(refused.Code, refused.Message, (int)refused.StatusCode, "UploadPart on the completed upload", ["NoSuchUpload"], 404);
        Ensure(refused.Message == expectedMessage, $"expected the message {Py.Repr(expectedMessage)}, got {Py.Repr(refused.Message)}");

        await s3.DeleteObjectAsync(bucket, key);
        var headRefused = await Err(() => s3.GetObjectMetadataAsync(bucket, key));
        var (headCode, headMessage) = S3Fidelity.HeadError(headRefused);
        AssertError(headCode, headMessage, (int)headRefused.StatusCode, "HeadObject after delete", ["404", "NoSuchKey"], 404, checkMessage: false);
    }

    /// <summary>
    /// Call fn(); return the error it raises. An unexpected success raises, so a target that accepts
    /// what AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static Task<AwsError> Err(Func<Task> call) => S3Fidelity.ExpectError(call, "expected a ClientError, but the call SUCCEEDED");

    /// <summary>Grade a refusal on code + HTTP status + non-empty message (a HEAD refusal has no body).</summary>
    private static void AssertError(string? code, string? message, int status, string what, string[] codes, int wantStatus, bool checkMessage = true)
    {
        Ensure(codes.Contains(code), $"{what}: expected code in {Py.Repr(codes)}, got {Py.Repr(code)}");
        Ensure(status == wantStatus, $"{what}: expected HTTP {wantStatus}, got {status}");
        if (checkMessage)
        {
            Ensure(!string.IsNullOrEmpty(message), $"{what}: expected a non-empty error message, got {Py.Repr(message)}");
        }
    }
}
