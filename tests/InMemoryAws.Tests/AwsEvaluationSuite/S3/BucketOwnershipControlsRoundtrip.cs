// Translated from behavioral/s3/check_bucket_ownership_controls_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-ownership-controls-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketOwnershipControlsRoundtrip
{
    /// <summary>
    /// Buckets default to ObjectOwnership=BucketOwnerEnforced; the control can be switched and deleted,
    /// after which Get yields OwnershipControlsNotFoundError.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-ownership-controls-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var initial = (await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket })).OwnershipControls.Rules;
        Ensure(initial[0].ObjectOwnership == ObjectOwnership.BucketOwnerEnforced, $"expected the BucketOwnerEnforced default, got {Py.Repr(initial[0].ObjectOwnership?.Value)}");
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.ObjectWriter }] },
        });
        var rules = (await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket })).OwnershipControls.Rules;
        Ensure(rules[0].ObjectOwnership == ObjectOwnership.ObjectWriter, $"ownership switch not persisted: {Py.Repr(rules[0].ObjectOwnership?.Value)}");
        await s3.DeleteBucketOwnershipControlsAsync(new DeleteBucketOwnershipControlsRequest { BucketName = bucket });
        var error = await S3Fidelity.Error(() => s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket }));
        Ensure(error.Code == "OwnershipControlsNotFoundError", $"expected OwnershipControlsNotFoundError, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The bucket ownership controls were not found", error.Message ?? "None");
    }
}
