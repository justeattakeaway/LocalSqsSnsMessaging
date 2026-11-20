// Translated from behavioral/s3/check_bucket_acl_put_get_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-acl-put-get-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAclPutGetRoundtrip
{
    /// <summary>
    /// PutBucketAcl sets an explicit grant set (owner FULL_CONTROL plus a LogDelivery group grant) and
    /// GetBucketAcl returns exactly that grantee/permission set back.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-acl-put-get-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.NewBucket(ctx, "sim2real-behav-bacl-");

        // ACLs are unusable on a bucket with BucketOwnerEnforced ownership (today's
        // default); relax it so PutBucketAcl is actually honored.
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = name,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerPreferred }] },
        });
        var owner = (await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = name })).Owner;
        await s3.PutBucketAclAsync(new PutBucketAclRequest
        {
            BucketName = name,
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
        var got = await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = name });
        Ensure(got.Owner.Id == owner.Id, $"{Py.Repr(got.Owner.Id)}");
        var grants = got.Grants;
        Ensure(grants.Count == 2, S3Fidelity.Describe(grants)); // PutBucketAcl replaces the grant set; nothing else survives it
        var fullControl = grants.Where(g => g.Permission == S3Permission.FULL_CONTROL).ToList();
        Ensure(fullControl.Exists(g => g.Grantee.CanonicalUser == owner.Id), S3Fidelity.Describe(grants));
        var logDelivery = grants.Where(g => g.Permission == S3Permission.READ_ACP && g.Grantee.URI == S3Fidelity.LogDelivery).ToList();
        Ensure(logDelivery.Count == 1, S3Fidelity.Describe(grants));
    }
}
