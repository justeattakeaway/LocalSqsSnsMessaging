// Translated from behavioral/s3/check_bucket_ownership_controls_lifecycle.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-ownership-controls-lifecycle</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketOwnershipControlsLifecycle
{
    /// <summary>
    /// PutBucketOwnershipControls(ObjectWriter) is reflected by Get, and after
    /// DeleteBucketOwnershipControls Get raises OwnershipControlsNotFoundError with its exact message: the
    /// control is removed, not reset to a default.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-ownership-controls-lifecycle")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.NewBucket(ctx, "sim2real-behav-ownctl-");
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = name,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.ObjectWriter }] },
        });
        var got = await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = name });
        Ensure(got.OwnershipControls.Rules[0].ObjectOwnership == ObjectOwnership.ObjectWriter, $"{Py.Repr(got.OwnershipControls.Rules[0].ObjectOwnership?.Value)}");
        await s3.DeleteBucketOwnershipControlsAsync(new DeleteBucketOwnershipControlsRequest { BucketName = name });
        var (after, error) = await FidelityContext.Capture(() => s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = name }));
        if (error is null)
        {
            throw Fail($"expected OwnershipControlsNotFoundError after delete, got a 200: {Py.Repr(after?.OwnershipControls?.Rules?.Select(r => r.ObjectOwnership?.Value).ToList())}");
        }

        Ensure(error.Code == "OwnershipControlsNotFoundError", $"expected OwnershipControlsNotFoundError after delete, got {error.Code}");
        Ensure(error.Message == "The bucket ownership controls were not found", error.Message ?? "None");
    }
}
