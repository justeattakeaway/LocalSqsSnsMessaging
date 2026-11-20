// Translated from behavioral/s3/check_object_acl_canned_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-acl-canned-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAclCannedRoundtrip
{
    /// <summary>
    /// PutObjectAcl with a canned ACL replaces the grant list; GetObjectAcl reflects the new grants (owner
    /// FULL_CONTROL + public READ).
    /// </summary>
    [Test]
    [DisplayName("s3.object-acl-canned-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);

        // Buckets created after Apr-2023 default to ObjectOwnership=BucketOwnerEnforced,
        // which DISABLES ACLs outright (PutObjectAcl -> AccessControlListNotSupported),
        // and to Block Public Access all-on. Both must be relaxed before a canned
        // public-read ACL is even expressible.
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.ObjectWriter }] },
        });
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = bucket, PublicAccessBlockConfiguration = S3Fidelity.Block() });
        const string key = "acl-object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x" });
        var owner = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Owner;

        // Grant something the canned ACL below does not mention, so the final read can tell
        // "replaces the grant list" (the contract) apart from "merges into it".
        await s3.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
            AccessControlPolicy = new S3AccessControlList
            {
                Owner = owner,
                Grants =
                [
                    S3Fidelity.UserGrant(owner.Id, S3Permission.FULL_CONTROL),
                    S3Fidelity.GroupGrant(S3Fidelity.LogDelivery, S3Permission.READ_ACP),
                ],
            },
        });
        var interim = await S3Fidelity.ObjectGrants(s3, bucket, key);
        Ensure(
            interim.Exists(g => g.Permission == S3Permission.READ_ACP),
            "explicit LogDelivery READ_ACP grant must be visible before the canned ACL replaces it, " +
            $"got grants={S3Fidelity.Describe(interim)}");
        await s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = key, ACL = S3CannedACL.PublicRead });
        var acl = await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key });
        Ensure(acl.Owner.Id == owner.Id && acl.Owner.DisplayName == owner.DisplayName, "owner must not change when ACL is replaced");
        var granteeUris = acl.Grants.Where(g => S3Fidelity.GranteeType(g) == "Group").Select(g => g.Grantee.URI).ToHashSet();
        Ensure(
            granteeUris.Contains(S3Fidelity.AllUsers),
            $"expected AllUsers group grant for public-read ACL, got grants={S3Fidelity.Describe(acl.Grants)}");
        var perms = acl.Grants.Select(g => g.Permission.Value).ToHashSet();
        Ensure(perms.Contains("READ") && perms.Contains("FULL_CONTROL"), $"expected READ+FULL_CONTROL permissions, got {Py.Repr(perms.Order().ToList())}");
        Ensure(
            !acl.Grants.Exists(g => g.Permission == S3Permission.READ_ACP),
            "canned ACL must replace the prior grant list, but the LogDelivery READ_ACP grant " +
            $"survived: {S3Fidelity.Describe(acl.Grants)}");
    }
}
