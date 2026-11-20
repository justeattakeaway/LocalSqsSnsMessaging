// Translated from behavioral/s3/check_op_contract_get_bucket_location.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-get-bucket-location</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractGetBucketLocation
{
    /// <summary>
    /// GetBucketLocation reports the bucket's actual creation region; a missing bucket raises NoSuchBucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-get-bucket-location")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        // --- success path: reported LocationConstraint matches the region the bucket was created in ---
        var got = await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
        var constraint = got.Location?.Value;
        // us-east-1 is AWS's legacy sentinel: GetBucketLocation reports it as null/empty, not the
        // literal region name, regardless of which region this suite actually runs in.
        if (ctx.Region == "us-east-1")
        {
            Ensure(constraint is null or "" or "us-east-1", $"expected empty/us-east-1, got {Py.Repr(constraint)}");
        }
        else
        {
            Ensure(constraint == ctx.Region, $"expected {Py.Repr(ctx.Region)}, got {Py.Repr(constraint)}");
        }

        // --- error boundary: operating against a bucket that does not exist ---
        string? code, message;
        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = ghostBucket }));
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
