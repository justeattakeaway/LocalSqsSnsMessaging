// Translated from behavioral/s3/check_op_contract_delete_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteBucket
{
    /// <summary>
    /// DeleteBucket removes an empty bucket, and the deleted name is unusable for a further
    /// mutation; a non-empty bucket is rejected with BucketNotEmpty, the bucket is left standing,
    /// and once the object is removed the identical DeleteBucket call succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        // --- success path: empty bucket deletes cleanly, disappears, and is unusable ---
        var emptyBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        // withdraw the registered cleanup: we delete it ourselves as part of the success assertion
        await s3.DeleteBucketAsync(emptyBucket);
        // A HEAD response carries no body, so there is no AWS message to compare.
        var (code, _) = await ClientError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = emptyBucket }));
        Ensure(code == "404", $"expected bucket gone, got code={Py.Repr(code)}");
        (code, var message) = await ClientError(() => s3.PutObjectAsync(new PutObjectRequest { BucketName = emptyBucket, Key = "ghost.txt", ContentBody = "x" }));
        Ensure(code == "NoSuchBucket", $"expected a mutation against the deleted bucket to fail with NoSuchBucket, got {Py.Repr(code)}");
        Ensure(message == "The specified bucket does not exist", $"unexpected message {Py.Repr(message)}");

        // --- error boundary: non-empty bucket cannot be deleted, until it is emptied ---
        var nonemptyBucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = nonemptyBucket, Key = "still-here.txt", ContentBody = "data" });
        (code, message) = await ClientError(() => s3.DeleteBucketAsync(nonemptyBucket));
        Ensure(code == "BucketNotEmpty", $"expected BucketNotEmpty, got {Py.Repr(code)}");
        Ensure(message == "The bucket you tried to delete is not empty", $"unexpected message {Py.Repr(message)}");
        // effect absent: an independent read shows the refused delete left the bucket standing
        await s3.HeadBucketAsync(new HeadBucketRequest { BucketName = nonemptyBucket });
        // restore: remove the object that caused the refusal
        await s3.DeleteObjectAsync(nonemptyBucket, "still-here.txt");
        // the identical call now succeeds
        await s3.DeleteBucketAsync(nonemptyBucket);
        (code, _) = await ClientError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = nonemptyBucket }));
        Ensure(code == "404", $"expected bucket gone after emptying and retrying the delete, got code={Py.Repr(code)}");
    }

    /// <summary>
    /// The (Error.Code, Error.Message) of fn(); a code-less HEAD error yields the HTTP status. Raises
    /// AssertionError if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
