// Translated from behavioral/s3/check_op_contract_delete_object_tagging.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-object-tagging</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteObjectTagging
{
    /// <summary>
    /// DeleteObjectTagging clears an object's tag set; a missing bucket is rejected with NoSuchBucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-object-tagging")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        const string key = "tagged-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "hello" });

        // --- success path: tag the object, delete tags, confirm the tag set is empty ---
        await s3.PutObjectTaggingAsync(new PutObjectTaggingRequest { BucketName = bucket, Key = key, Tagging = new Tagging { TagSet = [new Tag { Key = "team", Value = "sim2real" }] } });
        var got = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key });
        Ensure(
            got.Tagging.Count == 1 && got.Tagging[0].Key == "team" && got.Tagging[0].Value == "sim2real",
            $"expected tag set to be set, got {Py.Repr(got.Tagging.Select(t => (t.Key, t.Value)).ToList())}");

        await s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
        var gotAfter = await s3.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key });
        Ensure(gotAfter.Tagging is null or { Count: 0 }, $"expected empty tag set, got {Py.Repr(gotAfter.Tagging?.Select(t => (t.Key, t.Value)).ToList())}");

        // --- error boundary: operating against a bucket that does not exist ---
        string? code, message;
        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = ghostBucket, Key = key }));
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
