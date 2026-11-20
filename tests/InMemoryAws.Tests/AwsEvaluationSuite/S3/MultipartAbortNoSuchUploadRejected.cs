// Translated from behavioral/s3/check_multipart_abort_no_such_upload_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.multipart-abort-no-such-upload-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MultipartAbortNoSuchUploadRejected
{
    /// <summary>
    /// AbortMultipartUpload with a never-issued uploadId raises NoSuchUpload and leaves a real
    /// in-flight upload on the same bucket untouched (per ListMultipartUploads before and after); the
    /// identical call against the real upload id succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.multipart-abort-no-such-upload-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "real-upload.bin";

        var realUploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;
        ctx.OnCleanup(S3Fidelity.AbortQuietly(s3, bucket, key, realUploadId));
        var before = await Uploads(s3, bucket);
        Ensure(
            before.Count == 1 && before[0].UploadId == realUploadId,
            $"the positive-control upload did not appear in ListMultipartUploads: {Py.Repr(before.Select(u => u.UploadId))}");

        const string fakeUploadId = "sim2real-behav-fake-upload-id-0000000000";
        var (code, message) = await S3Fidelity.ErrorPair(() => s3.AbortMultipartUploadAsync(bucket, "never-uploaded.bin", fakeUploadId));
        Ensure(code == "NoSuchUpload", $"expected NoSuchUpload for a fabricated uploadId, got {code}");
        Ensure(message == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", message ?? "None");

        var after = await Uploads(s3, bucket);
        Ensure(
            after.Count == 1 && after[0].UploadId == realUploadId,
            $"the rejected abort on a fabricated uploadId changed the real upload's listing: {Py.Repr(after.Select(u => u.UploadId))}");

        // The identical shape of call succeeds against the real upload id: the rejection above was
        // specific to the fabricated id, not to AbortMultipartUpload itself.
        await s3.AbortMultipartUploadAsync(bucket, key, realUploadId);
        var gone = await Uploads(s3, bucket);
        Ensure(gone.Count == 0, $"the real upload was not removed by its own abort: {Py.Repr(gone.Select(u => u.UploadId))}");

        // And the now-aborted real id is itself a dead id: a further operation against it fails
        // with the same specific code.
        var (code2, message2) = await S3Fidelity.ErrorPair(() => s3.ListPartsAsync(new ListPartsRequest { BucketName = bucket, Key = key, UploadId = realUploadId }));
        Ensure(code2 == "NoSuchUpload", $"expected NoSuchUpload against the aborted upload id, got {code2}");
        Ensure(message2 == "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.", message2 ?? "None");
    }

    private static async Task<List<MultipartUpload>> Uploads(IAmazonS3 s3, string bucket) =>
        (await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [];
}
