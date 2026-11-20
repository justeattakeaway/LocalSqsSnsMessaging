// Translated from behavioral/s3/check_op_contract_delete_bucket_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-delete-bucket-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractDeleteBucketLifecycle
{
    /// <summary>
    /// DeleteBucketLifecycle removes all lifecycle rules, so the get then fails.
    ///
    /// A missing bucket is rejected with NoSuchBucket.
    /// </summary>
    [Test]
    [DisplayName("s3.op-contract-delete-bucket-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");

        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "expire-logs",
                        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "logs/" } },
                        Status = LifecycleRuleStatus.Enabled,
                        Expiration = new LifecycleRuleExpiration { Days = 30 },
                    },
                ],
            },
        });
        var got = await s3.GetLifecycleConfigurationAsync(new GetLifecycleConfigurationRequest { BucketName = bucket });
        Ensure(got.Configuration.Rules[0].Id == "expire-logs", $"expected rule id 'expire-logs', got {Py.Repr(got.Configuration.Rules[0].Id)}");

        await s3.DeleteLifecycleConfigurationAsync(new DeleteLifecycleConfigurationRequest { BucketName = bucket });
        // The .NET SDK answers GetBucketLifecycleConfiguration's 404 as an empty success; the refusal is read off the wire.
        var (code, message) = S3Fidelity.Botocore(await S3Fidelity.RawError(ctx, "GET", $"{bucket}?lifecycle", "expected a ClientError, but the call SUCCEEDED"));
        Ensure(code == "NoSuchLifecycleConfiguration", $"expected NoSuchLifecycleConfiguration, got {Py.Repr(code)}");
        Ensure(message == "The lifecycle configuration does not exist", $"unexpected message {Py.Repr(message)}");

        var ghostBucket = FidelityContext.UniqueName("sim2real-behav-ghost-");
        (code, message) = await ClientError(() => s3.DeleteLifecycleConfigurationAsync(new DeleteLifecycleConfigurationRequest { BucketName = ghostBucket }));
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
