// Translated from behavioral/s3/check_error_bucket_not_empty.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.error-bucket-not-empty</summary>
[Category("AwsEvaluationSuite")]
public sealed class ErrorBucketNotEmpty
{
    private const string ErrBucketNotEmpty = "BucketNotEmpty";
    private const string ErrNoSuchBucket = "NoSuchBucket";

    /// <summary>
    /// DeleteBucket on a non-empty bucket is rejected with BucketNotEmpty; the bucket
    /// survives the rejected call untouched; and emptying it is exactly what flips the
    /// identical DeleteBucket call back to succeeding.
    /// </summary>
    [Test]
    [DisplayName("s3.error-bucket-not-empty")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;

        // positive control: DeleteBucket itself works on this account/region before anything
        // is broken -- a separate, always-empty bucket, so the object we are about to add to
        // the bucket under test cannot be the reason a bare DeleteBucket would fail.
        var control = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.DeleteBucketAsync(control);
        await AssertGone(s3, control);

        // break exactly one thing: the bucket under test holds an object
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "still-here.txt", InputStream = new MemoryStream(Encoding.UTF8.GetBytes("data")) });

        var refusal = await ErrCode(() => s3.DeleteBucketAsync(bucket));
        var (code, status) = (refusal.Code, (int)refusal.StatusCode);
        Ensure(code == ErrBucketNotEmpty, $"expected {ErrBucketNotEmpty}, got {Py.Repr(code)}");
        Ensure(status / 100 == 4, $"expected HTTP 4xx (AWS answers 409) for {ErrBucketNotEmpty}, got {status}");
        Ensure(
            refusal.Message == "The bucket you tried to delete is not empty",
            $"expected the exact {ErrBucketNotEmpty} message, got {refusal}");

        // effect absent: an independent read (HeadBucket, not the failed DeleteBucket's own
        // response) still finds the bucket and its object -- the rejection removed neither
        if (await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket })) is { } exc)
        {
            throw Fail($"bucket {Py.Repr(bucket)} is unexpectedly gone after a rejected DeleteBucket: {Py.Repr(exc.Code)}");
        }

        var kept = Encoding.UTF8.GetString(await S3Fidelity.GetBytes(s3, bucket, "still-here.txt"));
        Ensure(kept == "data", $"the object changed across the rejected DeleteBucket: {Py.Repr(kept)}");

        // restore: removing the object is exactly what makes the identical DeleteBucket call
        // succeed afterward
        await s3.DeleteObjectAsync(bucket, "still-here.txt");
        await s3.DeleteBucketAsync(bucket);
        await AssertGone(s3, bucket);
    }

    /// <summary>Call fn(); return the ClientError response it raises. Raises on unexpected success.</summary>
    private static async Task<AwsError> ErrCode(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) ?? throw Fail("expected a ClientError, the call unexpectedly succeeded");

    /// <summary>HeadBucket answers 404, ListBuckets omits it, and a PutObject into it is refused NoSuchBucket.</summary>
    private static async Task AssertGone(IAmazonS3 s3, string bucket)
    {
        var head = await ErrCode(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }));
        // HEAD responses carry no body, so the code and message come from the bare 404 status.
        Ensure(
            S3CoreFidelity.IsBareHead404(head),
            $"expected HeadBucket 404 for the deleted bucket, got code {Py.Repr(head.Code)} status {(int)head.StatusCode}");
        Ensure(S3CoreFidelity.BotocoreMessage(head) == "Not Found", $"expected HeadBucket's 'Not Found' for the deleted bucket, got {head}");
        var names = await S3CoreFidelity.ListBucketNames(s3, bucket);
        Ensure(!names.Contains(bucket), $"deleted bucket still listed by ListBuckets: {Py.Repr(names)}");
        var put = await ErrCode(() => s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = "after-delete.txt",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("x")),
        }));
        var status = (int)put.StatusCode;
        Ensure(
            put.Code == ErrNoSuchBucket && status / 100 == 4,
            $"expected NoSuchBucket/4xx putting into the deleted bucket, got code {Py.Repr(put.Code)} status {status}");
        Ensure(put.Message == "The specified bucket does not exist", $"expected the exact NoSuchBucket message, got {put}");
    }
}
