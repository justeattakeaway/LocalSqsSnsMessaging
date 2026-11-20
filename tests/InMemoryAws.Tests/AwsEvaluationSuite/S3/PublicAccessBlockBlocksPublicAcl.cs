// Translated from behavioral/s3/check_public_access_block_blocks_public_acl.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.public-access-block-blocks-public-acl</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublicAccessBlockBlocksPublicAcl
{
    /// <summary>
    /// With BlockPublicAcls=True, PutBucketAcl('public-read') is rejected with AccessDenied and its exact
    /// message and leaves the ACL unchanged, a private ACL still applies, and after the block is lifted the
    /// public-read call succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.public-access-block-blocks-public-acl")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(
            ctx,
            "sim2real-pab-",
            ObjectOwnership.ObjectWriter); // ACLs must be enabled to isolate the PublicAccessBlock effect

        // establish a known baseline: no block at all, regardless of account-level defaults
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = bucket, PublicAccessBlockConfiguration = S3Fidelity.Block() });

        // positive control: absent any block, the public canned ACL this check will later reject
        // is itself accepted — otherwise "rejected under the block" is indistinguishable from
        // "rejected regardless of the block".
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.PublicRead });
        var controlGrants = await S3Fidelity.BucketGrants(s3, bucket);
        Ensure(
            S3Fidelity.HasPublicGrant(controlGrants),
            $"expected a public AllUsers grant with no block in effect, got {S3Fidelity.Describe(controlGrants)}");
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.Private }); // reset to a clean baseline

        // break exactly one thing
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = bucket, PublicAccessBlockConfiguration = S3Fidelity.Block(acls: true) });
        var before = S3Fidelity.Describe(await S3Fidelity.BucketGrants(s3, bucket));
        var error = await S3Fidelity.Error(() => s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.PublicRead }));
        var expected = S3Fidelity.PublicAclRefusal(ctx, "PutBucketAcl", $"arn:aws:s3:::{bucket}");
        Ensure(
            error.Code == "AccessDenied" && error.Message == expected,
            $"expected AccessDenied for public ACL under BlockPublicAcls, got {S3Fidelity.Repr((error.Code, error.Message))}");

        // the forbidden effect did not happen: bucket ACL grants are unchanged, still no public grant
        var afterGrants = await S3Fidelity.BucketGrants(s3, bucket);
        var after = S3Fidelity.Describe(afterGrants);
        Ensure(after == before, $"rejected PutBucketAcl must leave grants unchanged: before={before} after={after}");
        Ensure(!S3Fidelity.HasPublicGrant(afterGrants), $"no public grant may appear after the rejected call: {after}");

        // a private (non-public) canned ACL is still accepted under the same block
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.Private });
        var privateGrants = await S3Fidelity.BucketGrants(s3, bucket);
        Ensure(privateGrants.Count == 1 && privateGrants[0].Permission == S3Permission.FULL_CONTROL, S3Fidelity.Describe(privateGrants));

        // restore: lift the block, and the identical public-read call now succeeds
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest { BucketName = bucket, PublicAccessBlockConfiguration = S3Fidelity.Block() });
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.PublicRead });
        var restoredGrants = await S3Fidelity.BucketGrants(s3, bucket);
        Ensure(
            S3Fidelity.HasPublicGrant(restoredGrants),
            "expected the identical PutBucketAcl(public-read) to succeed once BlockPublicAcls is " +
            $"lifted, got {S3Fidelity.Describe(restoredGrants)}");
    }
}
