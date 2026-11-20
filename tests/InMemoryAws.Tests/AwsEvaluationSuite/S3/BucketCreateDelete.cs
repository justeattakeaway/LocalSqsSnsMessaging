// Translated from behavioral/s3/check_bucket_create_delete.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-create-delete</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketCreateDelete
{
    /// <summary>
    /// CreateBucket then HeadBucket succeeds (positive control); DeleteBucket removes it, an
    /// independent HeadBucket then returns the specific not-found code, and a mutation (PutObject)
    /// against the dead bucket id also fails rather than only the read.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-create-delete")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        // Positive control: the bucket exists and is reachable.
        await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket });
        // Delete the bucket to exercise the delete path (registered teardown re-attempts, best-effort).
        await s3.DeleteBucketAsync(bucket);
        // Independent read: HeadBucket now returns the specific not-found code, not just "some error".
        // A HEAD response carries no body, so there is no AWS message to compare.
        var (headCode, _) = await ClientError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }));
        Ensure(headCode == "404", $"expected 404 on HeadBucket after delete, got {Py.Repr(headCode)}");
        // A mutation against the dead id fails too, not just a read.
        var (putCode, putMessage) = await ClientError(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "probe", ContentBody = "x" }));
        Ensure(putCode == "NoSuchBucket", $"expected NoSuchBucket on PutObject to deleted bucket, got {Py.Repr(putCode)}");
        const string expected = "The specified bucket does not exist";
        Ensure(putMessage == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(putMessage)}");
    }

    /// <summary>
    /// Call fn(); return the ClientError's (Code, Message). An unexpected SUCCESS raises, so a
    /// target that accepts a call real AWS refuses cannot read as a code mismatch.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected the call to be rejected, but it SUCCEEDED");
}
