// Translated from behavioral/s3/check_bucket_acl_owner_full_control_default.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-acl-owner-full-control-default</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAclOwnerFullControlDefault
{
    /// <summary>
    /// A default (BucketOwnerEnforced) bucket's ACL reports exactly one FULL_CONTROL grant to the owner.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-acl-owner-full-control-default")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        var ownership = await s3.GetBucketOwnershipControlsAsync(new GetBucketOwnershipControlsRequest { BucketName = bucket });
        var rules = ownership.OwnershipControls.Rules;
        Ensure(
            rules is { Count: > 0 } && rules[0].ObjectOwnership == ObjectOwnership.BucketOwnerEnforced,
            $"expected the default ObjectOwnership to be BucketOwnerEnforced, got {Py.Repr(rules?.Select(r => r.ObjectOwnership?.Value).ToList())}");
        var acl = await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket });
        var ownerId = acl.Owner?.Id;
        Ensure(!string.IsNullOrEmpty(ownerId), $"expected a non-empty owner canonical id, got {Py.Repr(ownerId)}");
        var grants = acl.Grants ?? [];
        Ensure(grants.Count == 1, $"expected exactly 1 grant on a fresh bucket, got {S3Fidelity.Describe(grants)}");
        Ensure(grants[0].Permission == S3Permission.FULL_CONTROL, $"expected FULL_CONTROL, got {S3Fidelity.Describe(grants)}");
        Ensure(grants[0].Grantee.CanonicalUser == ownerId, "the sole grant must be to the bucket owner");
        Ensure(
            S3Fidelity.GranteeType(grants[0]) == "CanonicalUser",
            $"expected the sole grant's Grantee.Type to be CanonicalUser, got {S3Fidelity.Describe(grants)}");
    }
}
