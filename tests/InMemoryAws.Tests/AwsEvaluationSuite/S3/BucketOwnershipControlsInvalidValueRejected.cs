// Translated from behavioral/s3/check_bucket_ownership_controls_invalid_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-ownership-controls-invalid-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketOwnershipControlsInvalidValueRejected
{
    /// <summary>
    /// PutBucketOwnershipControls with an ObjectOwnership value outside the enum is rejected with
    /// MalformedXML and its exact message, and the BucketOwnerPreferred rule set by the valid control call
    /// stays in place.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-ownership-controls-invalid-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerPreferred }] },
        });
        var rules = (await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket })).OwnershipControls.Rules;
        Ensure(rules[0].ObjectOwnership == ObjectOwnership.BucketOwnerPreferred, $"positive control did not take effect: {Py.Repr(rules[0].ObjectOwnership?.Value)}");
        var error = await S3Fidelity.Error(() => s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = new ObjectOwnership("NotARealValue") }] },
        }));
        Ensure(
            error.Code == "MalformedXML",
            $"expected the specific rejection code for an invalid ObjectOwnership value, got {Py.Repr(error.Code)}");
        Ensure(error.Message == "The XML you provided was not well-formed or did not validate against our published schema", error.Message ?? "None");
        var after = (await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket })).OwnershipControls.Rules;
        Ensure(after[0].ObjectOwnership == ObjectOwnership.BucketOwnerPreferred, $"the invalid value must not take effect: {Py.Repr(after[0].ObjectOwnership?.Value)}");
    }
}
