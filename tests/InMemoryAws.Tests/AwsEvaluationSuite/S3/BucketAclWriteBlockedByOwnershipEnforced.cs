// Translated from behavioral/s3/check_bucket_acl_write_blocked_by_ownership_enforced.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-acl-write-blocked-by-ownership-enforced</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAclWriteBlockedByOwnershipEnforced
{
    /// <summary>
    /// PutBucketAcl on a BucketOwnerEnforced bucket fails with AccessControlListNotSupported and leaves
    /// grants unchanged; after switching to ObjectWriter the same call succeeds, so the rejection is
    /// attributable to ownership enforcement.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-acl-write-blocked-by-ownership-enforced")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx); // default ownership: BucketOwnerEnforced

        // Block Public Access defaults to fully-blocking on a new bucket, which would itself reject
        // a public-read ACL with AccessDenied. Disable it so the rejection we observe is attributable
        // solely to BucketOwnerEnforced, not to the public-access-block feature.
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = bucket, PublicAccessBlockConfiguration = S3Fidelity.Block() });
        var before = S3Fidelity.Describe(await S3Fidelity.BucketGrants(s3, bucket));
        var error = await S3Fidelity.Error(() => s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.PublicRead }));
        Ensure(
            error.Code == "AccessControlListNotSupported",
            "expected the ACL mutation to be rejected with AccessControlListNotSupported on a " +
            $"BucketOwnerEnforced bucket, got {error.Code}");
        Ensure(error.Message == "The bucket does not allow ACLs", $"unexpected message {Py.Repr(error.Message)}");

        // the forbidden effect did not happen: grants are unchanged, still just owner FULL_CONTROL
        var afterGrants = await S3Fidelity.BucketGrants(s3, bucket);
        var after = S3Fidelity.Describe(afterGrants);
        Ensure(after == before, $"ACL grants changed despite the rejected mutation: {before} -> {after}");
        Ensure(!afterGrants.Exists(g => S3Fidelity.GranteeType(g) == "Group"), $"a group grant leaked through despite the rejection: {after}");

        // positive control: relaxing ownership on this same bucket to ObjectWriter re-enables ACLs,
        // and the identical PutBucketAcl call now succeeds
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.ObjectWriter }] },
        });
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.PublicRead });
        var grants = await S3Fidelity.BucketGrants(s3, bucket);
        var granteeUris = grants.Where(g => S3Fidelity.GranteeType(g) == "Group").Select(g => g.Grantee.URI).ToHashSet();
        Ensure(
            granteeUris.Contains(S3Fidelity.AllUsers),
            $"expected an AllUsers group grant once ownership was relaxed to ObjectWriter, got {S3Fidelity.Describe(grants)}");
    }
}
