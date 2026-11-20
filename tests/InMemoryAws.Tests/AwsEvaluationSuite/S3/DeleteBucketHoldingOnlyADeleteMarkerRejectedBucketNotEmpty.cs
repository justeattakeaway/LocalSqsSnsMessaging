// Translated from behavioral/s3/check_delete_bucket_holding_only_a_delete_marker_rejected_bucket_not_empty.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-bucket-holding-only-a-delete-marker-rejected-bucket-not-empty</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteBucketHoldingOnlyADeleteMarkerRejectedBucketNotEmpty
{
    // A versioned bucket's refusal names the versions that keep it non-empty.
    private static readonly (string, string) NotEmpty =
        ("BucketNotEmpty", "The bucket you tried to delete is not empty. You must delete all versions in the bucket.");

    private static readonly (string, string) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>
    /// A versioned bucket whose only entry is a delete marker for a never-written key still refuses
    /// DeleteBucket with BucketNotEmpty and keeps that marker; deleting the marker by its version id is
    /// exactly what lets the identical DeleteBucket succeed.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-bucket-holding-only-a-delete-marker-rejected-bucket-not-empty")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // Positive control: a versioned bucket that never held anything deletes.
        var control = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await EnableVersioning(s3, control);
        await s3.DeleteBucketAsync(control);
        await AssertGone(s3, control);

        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await EnableVersioning(s3, bucket);
        const string key = "never-written/report.csv";
        var marker = await s3.DeleteObjectAsync(bucket, key);
        Ensure(
            marker.DeleteMarker == "true" && !string.IsNullOrEmpty(marker.VersionId),
            $"DeleteObject on an absent key in a versioned bucket did not create a delete marker: {Py.Repr(marker.DeleteMarker)}, {Py.Repr(marker.VersionId)}");
        var markerId = marker.VersionId;
        Ensure(
            ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Count == 0,
            "the current listing is not empty");

        var (code, msg, status) = CodeMsgStatus(await Err(() => s3.DeleteBucketAsync(bucket)));
        Ensure(
            (code, msg) == NotEmpty && status == 409,
            $"DeleteBucket holding one delete marker: expected {NotEmpty} HTTP 409, got {(code, msg, status)}");

        await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket });
        var listed = await s3.ListVersionsAsync(new ListVersionsRequest { BucketName = bucket });
        var markers = (listed.Versions ?? []).Where(v => v.IsDeleteMarker == true).Select(m => (m.Key, m.VersionId)).ToList();
        var versions = (listed.Versions ?? []).Where(v => v.IsDeleteMarker != true).ToList();
        Ensure(
            markers.SequenceEqual([(key, markerId)]) && versions.Count == 0,
            $"the refused DeleteBucket changed the version listing: ({string.Join(", ", markers)}, {versions.Count} versions)");

        await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = markerId });
        await s3.DeleteBucketAsync(bucket);
        await AssertGone(s3, bucket);
    }

    private static Task EnableVersioning(IAmazonS3 s3, string bucket) =>
        s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });

    /// <summary>Call fn(); return the ClientError response it raises. Raises on an unexpected success.</summary>
    private static async Task<AwsError> Err(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, the call unexpectedly succeeded");

    /// <summary>(Error.Code, Error.Message, HTTP status) of a ClientError response.</summary>
    private static (string? Code, string? Message, int Status) CodeMsgStatus(AwsError err) => (err.Code, err.Message, (int)err.StatusCode);

    /// <summary>HeadBucket answers 404 (bounded), ListBuckets omits it, a PutObject into it is refused.</summary>
    private static async Task AssertGone(IAmazonS3 s3, string bucket)
    {
        var deadline = FidelityTime.StartNew();
        AwsError head;
        while (true)
        {
            if (await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket })) is { } err)
            {
                head = err;
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(40), $"bucket {bucket} still answers HeadBucket after DeleteBucket");
            await FidelityTime.Delay(TimeSpan.FromSeconds(1));
        }

        // HEAD responses carry no body, so the code and message come from the bare 404 status.
        Ensure(S3CoreFidelity.IsBareHead404(head), $"HeadBucket after delete: {CodeMsgStatus(head)}");
        var names = await S3CoreFidelity.ListBucketNames(s3, bucket);
        Ensure(!names.Contains(bucket), $"deleted bucket still listed by ListBuckets: {Py.Repr(names)}");
        var (code, msg, status) = CodeMsgStatus(await Err(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "x",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("x")),
        })));
        Ensure(
            (code, msg) == NoSuchBucket && status == 404,
            $"PutObject into the deleted bucket: expected {NoSuchBucket} HTTP 404, got {(code, msg, status)}");
    }
}
