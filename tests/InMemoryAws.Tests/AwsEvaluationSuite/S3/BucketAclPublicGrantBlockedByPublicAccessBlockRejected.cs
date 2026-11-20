// Translated from behavioral/s3/check_bucket_acl_public_grant_blocked_by_public_access_block_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-acl-public-grant-blocked-by-public-access-block-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAclPublicGrantBlockedByPublicAccessBlockRejected
{
    /// <summary>
    /// With BlockPublicAcls on an ObjectWriter bucket, PutBucketAcl('public-read') is rejected with
    /// AccessDenied and its exact message and leaves the grants unchanged; after the block is lifted the
    /// identical call applies the public grant.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-acl-public-grant-blocked-by-public-access-block-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.NewBucket(ctx, "sim2real-behav-aclblock-");

        // Allow ACL usage in general...
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = name,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.ObjectWriter }] },
        });

        // establish a known baseline: no block at all, regardless of account-level defaults
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = name, PublicAccessBlockConfiguration = S3Fidelity.Block() });

        // positive control: absent any block, the public canned ACL this check will later
        // reject is itself accepted -- otherwise "rejected under the block" is
        // indistinguishable from "rejected regardless of the block".
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = name, ACL = S3CannedACL.PublicRead });
        var controlGrants = await S3Fidelity.BucketGrants(s3, name);
        Ensure(
            S3Fidelity.HasPublicGrant(controlGrants),
            $"expected a public AllUsers grant with no block in effect, got {S3Fidelity.Describe(controlGrants)}");
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = name, ACL = S3CannedACL.Private }); // reset to a clean baseline

        // ...but explicitly block public ACL grants (plus public policy and
        // restrict-public-buckets).
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = name,
            PublicAccessBlockConfiguration = S3Fidelity.Block(acls: true, policy: true, restrict: true),
        });
        var before = S3Fidelity.Describe(await S3Fidelity.BucketGrants(s3, name));
        var error = await S3Fidelity.Error(() => s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = name, ACL = S3CannedACL.PublicRead }));
        Ensure(error.Code == "AccessDenied", $"expected AccessDenied for public ACL under BlockPublicAcls, got {error.Code}");

        // The message names the caller's own identity, read here rather than pinned.
        var expected = S3Fidelity.PublicAclRefusal(ctx, "PutBucketAcl", $"arn:aws:s3:::{name}");
        Ensure(error.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(error.Message)}");

        // the forbidden effect did not happen: bucket ACL grants are unchanged, still no public grant
        var afterGrants = await S3Fidelity.BucketGrants(s3, name);
        var after = S3Fidelity.Describe(afterGrants);
        Ensure(after == before, $"rejected PutBucketAcl must leave grants unchanged: before={before} after={after}");
        Ensure(!S3Fidelity.HasPublicGrant(afterGrants), $"no public grant may appear after the rejected call: {after}");

        // A non-public canned ACL still works fine under the same block configuration.
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = name, ACL = S3CannedACL.Private });
        var got = await S3Fidelity.BucketGrants(s3, name);
        Ensure(got.TrueForAll(g => g.Grantee.URI != S3Fidelity.AllUsers), S3Fidelity.Describe(got));

        // restore: lift the block, and the identical public-read call now succeeds
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = name, PublicAccessBlockConfiguration = S3Fidelity.Block() });
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = name, ACL = S3CannedACL.PublicRead });
        var restoredGrants = await S3Fidelity.BucketGrants(s3, name);
        Ensure(
            S3Fidelity.HasPublicGrant(restoredGrants),
            "expected the identical PutBucketAcl(public-read) to succeed once the block is " +
            $"lifted, got {S3Fidelity.Describe(restoredGrants)}");
    }
}
