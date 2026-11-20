// Translated from behavioral/s3/check_op_contract_delete_public_access_block.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-public-access-block</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeletePublicAccessBlock
{
    /// <summary>
    /// DeletePublicAccessBlock clears the block config; a missing bucket is rejected with NoSuchBucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-public-access-block")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // --- success path: set a public access block, delete it, confirm it's gone ---
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true,
                IgnorePublicAcls = true,
                BlockPublicPolicy = true,
                RestrictPublicBuckets = true,
            },
        });
        var got = await s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket });
        var blockAcls = got.PublicAccessBlockConfiguration.BlockPublicAcls;
        Ensure(blockAcls == true, $"expected BlockPublicAcls True after put, got {Py.Repr(blockAcls)}");

        await s3.DeletePublicAccessBlockAsync(new DeletePublicAccessBlockRequest { BucketName = bucket });
        var (code, message) = await ClientError(() => s3.GetPublicAccessBlockAsync(new GetPublicAccessBlockRequest { BucketName = bucket }));
        Ensure(code == "NoSuchPublicAccessBlockConfiguration", $"expected NoSuchPublicAccessBlockConfiguration, got {Py.Repr(code)}");
        Ensure(message == "The public access block configuration was not found", $"unexpected message {Py.Repr(message)}");

        // --- error boundary: operating against a bucket that does not exist ---
        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.DeletePublicAccessBlockAsync(new DeletePublicAccessBlockRequest { BucketName = ghostBucket }));
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
