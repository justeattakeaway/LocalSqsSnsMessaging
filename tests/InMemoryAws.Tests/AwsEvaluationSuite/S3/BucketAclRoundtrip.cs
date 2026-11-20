// Translated from behavioral/s3/check_bucket_acl_roundtrip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-acl-roundtrip</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAclRoundtrip
{
    /// <summary>
    /// Bucket ACL grants set via PutBucketAcl exactly replace the prior grant set and are reflected
    /// precisely by GetBucketAcl.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-acl-roundtrip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx, "sim2real-bacl-", ObjectOwnership.BucketOwnerPreferred);
        var owner = (await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket })).Owner;
        await s3.PutBucketAclAsync(new PutBucketAclRequest
        {
            BucketName = bucket,
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
        var got = await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket });
        Ensure(got.Owner.Id == owner.Id, $"({Py.Repr(owner.Id)}, {Py.Repr(got.Owner.Id)})");
        var gotPairs = got.Grants.Select(g => (g.Grantee.CanonicalUser ?? g.Grantee.URI ?? "None", g.Permission.Value)).ToHashSet();
        var expectedPairs = new HashSet<(string, string)> { (owner.Id, "FULL_CONTROL"), (S3Fidelity.LogDelivery, "READ_ACP") };
        Ensure(gotPairs.SetEquals(expectedPairs), string.Join(", ", gotPairs));

        // Replacing the ACL with a strictly smaller grant set drops the previous grant entirely
        // (PutBucketAcl replaces, it does not merge).
        await s3.PutBucketAclAsync(new PutBucketAclRequest
        {
            BucketName = bucket,
            AccessControlPolicy = new S3AccessControlList { Owner = owner, Grants = [S3Fidelity.UserGrant(owner.Id, S3Permission.FULL_CONTROL)] },
        });
        var got2 = await s3.GetBucketAclAsync(new GetBucketAclRequest { BucketName = bucket });
        Ensure(got2.Grants.Count == 1, S3Fidelity.Describe(got2.Grants));
        Ensure(got2.Grants[0].Permission == S3Permission.FULL_CONTROL, S3Fidelity.Describe(got2.Grants));
    }
}
