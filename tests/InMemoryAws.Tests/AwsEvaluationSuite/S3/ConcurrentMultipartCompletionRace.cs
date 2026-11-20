// Translated from behavioral/s3/check_concurrent_multipart_completion_race.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.Runtime;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.concurrent-multipart-completion-race</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentMultipartCompletionRace
{
    private static readonly (string, string) UploadConsumed = (
        "NoSuchUpload",
        "The specified upload does not exist. The upload ID may be invalid, or the upload may have been aborted or completed.");

    /// <summary>
    /// Two identical CompleteMultipartUpload calls racing on one UploadId never both fail: either both
    /// succeed with one ETag or one succeeds and the other gets NoSuchUpload; the object holds the
    /// uploaded bytes and ListMultipartUploads omits the upload.
    /// </summary>
    [Test]
    [DisplayName("s3.concurrent-multipart-completion-race")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "mpu-race-obj.bin";
        var uploadId = (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = key })).UploadId;

        // A single part is both the first and the last part of this upload, so the 5MiB minimum
        // part-size floor (which only applies to non-final parts) does not apply here.
        var partResp = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            PartNumber = 1,
            UploadId = uploadId,
            InputStream = new MemoryStream("race-part-payload"u8.ToArray()),
        });
        List<PartETag> parts = [S3Fidelity.Part(1, partResp.ETag)];

        // Each call gets its own list: the .NET SDK sorts PartETags in place as it marshals them.
        Task<CompleteMultipartUploadResponse> Complete() =>
            s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = [.. parts] });

        var results = await S3CoreFidelity.RunConcurrent(ctx, [Complete, Complete]);
        var successes = results.Count(r => r is not Exception);
        var errors = results.OfType<Exception>()
            .Select(r => r is AmazonServiceException service ? (AwsError.From(service).Code, AwsError.From(service).Message) : (r.GetType().Name, r.Message))
            .ToList();

        // The race decides which outcome AWS gives; each outcome is graded exactly.
        if (successes == 2)
        {
            var etags = results.Cast<CompleteMultipartUploadResponse>().Select(r => r.ETag).ToHashSet(StringComparer.Ordinal);
            Ensure(etags.Count == 1, $"expected both racers' successful completions to agree on the same ETag, got {Py.Repr(etags.ToList())}");
        }
        else
        {
            Ensure(
                successes == 1 && errors.SequenceEqual([UploadConsumed]),
                $"expected one racer to complete the upload and the other to get {UploadConsumed}, got {successes} successes and errors {Py.Repr(errors.Select(e => e.ToString()).ToList())}");
        }

        // Regardless of how many racers "won", the assembled object must be readable exactly once
        // with the uploaded content -- no double-completion content artifacts.
        var body = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(Encoding.UTF8.GetString(body) == "race-part-payload", "expected the successful completion's content");

        // And regardless of how many racers won, the upload id is consumed -- it must not still be
        // listed as pending, or the race left a billed, orphaned multipart upload behind the object.
        var openUploadIds = ((await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket })).MultipartUploads ?? [])
            .Select(u => u.UploadId).ToList();
        Ensure(
            !openUploadIds.Contains(uploadId),
            $"upload {Py.Repr(uploadId)} is still listed as pending after the race resolved (open uploads: {Py.Repr(openUploadIds)}) -- a completed upload must not remain open");
    }
}
