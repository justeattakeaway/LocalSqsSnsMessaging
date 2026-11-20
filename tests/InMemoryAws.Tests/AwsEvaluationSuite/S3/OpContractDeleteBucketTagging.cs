// Translated from behavioral/s3/check_op_contract_delete_bucket_tagging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-bucket-tagging</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteBucketTagging
{
    /// <summary>
    /// DeleteBucketTagging clears the bucket tag set; a missing bucket is rejected with NoSuchBucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-bucket-tagging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // --- success path: put tags, delete them, confirm they're gone ---
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "env", Value = "test" }] });
        var got = await s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket });
        Ensure(
            got.TagSet.Count == 1 && got.TagSet[0].Key == "env" && got.TagSet[0].Value == "test",
            $"expected the tag set just put, got {Py.Repr(got.TagSet.Select(t => (t.Key, t.Value)).ToList())}");

        await s3.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = bucket });
        var (code, message) = await ClientError(() => s3.GetBucketTaggingAsync(new GetBucketTaggingRequest { BucketName = bucket }));
        Ensure(code == "NoSuchTagSet", $"expected NoSuchTagSet, got {Py.Repr(code)}");
        Ensure(message == "The TagSet does not exist", $"unexpected message {Py.Repr(message)}");

        // --- error boundary: operating against a bucket that does not exist ---
        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.DeleteBucketTaggingAsync(new DeleteBucketTaggingRequest { BucketName = ghostBucket }));
        Ensure(code == "NoSuchBucket", $"expected NoSuchBucket, got {Py.Repr(code)}");
        Ensure(message == "The specified bucket does not exist", $"unexpected message {Py.Repr(message)}");
    }

    /// <summary>
    /// The (Error.Code, Error.Message) of fn(); a code-less HEAD error yields the HTTP status. Raises
    /// AssertionError if fn succeeds.
    /// </summary>
    private static Task<(string? Code, string? Message)> ClientError(Func<Task> fn) =>
        S3Fidelity.ClientError(fn, "expected a ClientError, but the call SUCCEEDED");
}
