// Translated from behavioral/s3/check_object_acl_lifecycle_nosuchkey.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-acl-lifecycle-nosuchkey</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAclLifecycleNosuchkey
{
    /// <summary>
    /// PutObjectAcl/GetObjectAcl succeed and roundtrip a custom grant while the object exists; after
    /// DeleteObject, GetObjectAcl transitions to NoSuchKey.
    /// </summary>
    [Test]
    [DisplayName("s3.object-acl-lifecycle-nosuchkey")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.NewBucket(ctx, "sim2real-behav-oacl-");
        const string key = "object-acl-test.txt";

        // Object ACLs need ACLs enabled on the bucket (BucketOwnerEnforced disables them).
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = name,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerPreferred }] },
        });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = name, Key = key, ContentBody = "acl-lifecycle-payload" });
        var owner = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = name, Key = key })).Owner;
        await s3.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = name,
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
        var got = await S3Fidelity.ObjectGrants(s3, name, key);
        var logDelivery = got.Where(g => g.Permission == S3Permission.READ_ACP && g.Grantee.URI == S3Fidelity.LogDelivery).ToList();
        Ensure(logDelivery.Count == 1, S3Fidelity.Describe(got));

        await s3.DeleteObjectAsync(name, key);
        var error = await S3Fidelity.Error(() => s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = name, Key = key }));
        Ensure(error.Code == "NoSuchKey", error.Code ?? "None");
        Ensure(error.Message == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(error.Message)}");

        // Gone means unusable too: a mutation against the same dead key fails identically.
        var putError = await S3Fidelity.Error(() => s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = name, Key = key, ACL = S3CannedACL.Private }));
        Ensure(putError.Code == "NoSuchKey", putError.Code ?? "None");
        Ensure(putError.Message == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(putError.Message)}");
    }
}
