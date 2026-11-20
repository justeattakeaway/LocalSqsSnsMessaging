// Translated from behavioral/s3/check_object_ownership_enforced_blocks_object_acl.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-ownership-enforced-blocks-object-acl</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectOwnershipEnforcedBlocksObjectAcl
{
    /// <summary>
    /// Under BucketOwnerEnforced, PutObjectAcl is rejected with AccessControlListNotSupported and its exact
    /// message and GetObjectAcl still shows only the owner's FULL_CONTROL grant, while the same call on an
    /// ObjectWriter sibling bucket applies the AllUsers grant.
    /// </summary>
    [Test]
    [DisplayName("s3.object-ownership-enforced-blocks-object-acl")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string aclsDisabledMessage = "The bucket does not allow ACLs";
        var s3 = ctx.S3;
        var enforcedBucket = await S3Fidelity.NewBucket(ctx, "sim2real-ownenf-");

        // New buckets default to Block Public Access enabled, which would reject a
        // public-read ACL with AccessDenied regardless of ownership controls. Disable
        // it here so the PutObjectAcl failure we observe is attributable solely to
        // BucketOwnerEnforced (ACLs disabled), not to the public-access-block feature.
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = enforcedBucket, PublicAccessBlockConfiguration = S3Fidelity.Block() });
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = enforcedBucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerEnforced }] },
        });
        const string key = "owned-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = enforcedBucket, Key = key, ContentBody = "content" });
        var beforeGrants = S3Fidelity.Describe(await S3Fidelity.ObjectGrants(s3, enforcedBucket, key));
        var error = await S3Fidelity.Error(() => s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = enforcedBucket, Key = key, ACL = S3CannedACL.PublicRead }));
        Ensure(
            error.Code == "AccessControlListNotSupported",
            $"expected AccessControlListNotSupported under BucketOwnerEnforced, got {error.Code}");
        Ensure(error.Message == aclsDisabledMessage, $"unexpected AccessControlListNotSupported message: {Py.Repr(error.Message)}");

        // the forbidden effect did not happen: ACL grants are unchanged, still just owner FULL_CONTROL
        var afterGrants = await S3Fidelity.ObjectGrants(s3, enforcedBucket, key);
        Ensure(
            S3Fidelity.Describe(afterGrants) == beforeGrants,
            $"expected grants unchanged by the rejected PutObjectAcl, before={beforeGrants} after={S3Fidelity.Describe(afterGrants)}");
        Ensure(
            afterGrants.Count == 1 && afterGrants[0].Permission == S3Permission.FULL_CONTROL,
            $"expected exactly one owner FULL_CONTROL grant, got {S3Fidelity.Describe(afterGrants)}");
        Ensure(
            !afterGrants.Exists(g => S3Fidelity.GranteeType(g) == "Group"),
            $"expected no Group grantee (no public grant applied), got {S3Fidelity.Describe(afterGrants)}");

        // positive control: an ObjectWriter-owned sibling bucket accepts the same PutObjectAcl call
        var writerBucket = await S3Fidelity.NewBucket(ctx, "sim2real-ownwriter-", ObjectOwnership.ObjectWriter);
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = writerBucket, PublicAccessBlockConfiguration = S3Fidelity.Block() });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = writerBucket, Key = key, ContentBody = "content" });
        await s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = writerBucket, Key = key, ACL = S3CannedACL.PublicRead });
        var grants = await S3Fidelity.ObjectGrants(s3, writerBucket, key);
        var granteeUris = grants.Where(g => S3Fidelity.GranteeType(g) == "Group").Select(g => g.Grantee.URI).ToHashSet();
        Ensure(
            granteeUris.Contains(S3Fidelity.AllUsers),
            $"expected the AllUsers group grant on the ObjectWriter sibling bucket, got {S3Fidelity.Describe(grants)}");
    }
}
