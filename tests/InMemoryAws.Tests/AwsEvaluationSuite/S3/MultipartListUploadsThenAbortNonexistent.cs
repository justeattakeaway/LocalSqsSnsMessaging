// Translated from behavioral/s3/check_multipart_list_uploads_then_abort_nonexistent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-list-uploads-then-abort-nonexistent</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartListUploadsThenAbortNonexistent
{
    /// <summary>
    /// ListMultipartUploads reflects an in-flight upload by key and UploadId; aborting a non-existent
    /// UploadId returns NoSuchUpload, while aborting the real one succeeds and removes it from the
    /// listing.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-list-uploads-then-abort-nonexistent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-mpu-");

        const string key = "big.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;

        var listed = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
        Ensure(listed.Count == 1, $"expected exactly 1 in-flight upload, got {listed.Count}");
        Ensure(listed[0].Key == key, $"expected key {Py.Repr(key)}, got {Py.Repr(listed[0].Key)}");
        Ensure(listed[0].UploadId == uploadId, $"expected UploadId {Py.Repr(uploadId)}, got {Py.Repr(listed[0].UploadId)}");

        // Negative: aborting a bogus UploadId for the same key fails specifically.
        var (code, message) = await S3Fidelity.ErrorPair(() => s3.AbortMultipartUploadAsync(bucket, key, "nonexistent-upload-id-0000"));
        Ensure(code == "NoSuchUpload", code ?? "None");
        Ensure(message == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", message ?? "None");
        // The real upload is untouched by the failed abort.
        var stillListed = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
        Ensure(stillListed.Count == 1 && stillListed[0].UploadId == uploadId, $"a failed abort must not disturb the real upload, got {stillListed.Count} uploads");

        // Positive control: aborting the real upload id succeeds and removes it from the listing.
        await s3.AbortMultipartUploadAsync(bucket, key, uploadId);
        var after = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
        Ensure(after.Count == 0, Py.Repr(after.Select(u => u.UploadId)));

        // Aborting the same (already-aborted) upload id again is documented S3 behavior as
        // idempotent: AbortMultipartUpload does not raise NoSuchUpload for an UploadId that
        // this same account already successfully aborted -- it just returns success again.
        // (Only a genuinely bogus/never-issued UploadId, as asserted above, yields
        // NoSuchUpload.) Confirm the repeat abort raises no error and the upload stays gone.
        await s3.AbortMultipartUploadAsync(bucket, key, uploadId);
        var after2 = (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
        Ensure(after2.Count == 0, Py.Repr(after2.Select(u => u.UploadId)));
    }
}
