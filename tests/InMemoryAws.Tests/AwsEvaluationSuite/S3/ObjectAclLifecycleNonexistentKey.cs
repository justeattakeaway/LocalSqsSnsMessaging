// Translated from behavioral/s3/check_object_acl_lifecycle_nonexistent_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-acl-lifecycle-nonexistent-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectAclLifecycleNonexistentKey
{
    /// <summary>
    /// PutObjectAcl/GetObjectAcl round-trip exactly for an existing key, and GetObjectAcl on a key that was
    /// never written returns NoSuchKey.
    /// </summary>
    [Test]
    [DisplayName("s3.object-acl-lifecycle-nonexistent-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx, "sim2real-oacl-", ObjectOwnership.BucketOwnerPreferred);
        const string key = "obj.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "hello-acl" });
        var owner = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Owner;
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
                    S3Fidelity.GroupGrant(S3Fidelity.LogDelivery, S3Permission.READ),
                ],
            },
        });
        var got = await S3Fidelity.ObjectGrants(s3, bucket, key);
        var gotPairs = got.Select(g => (g.Grantee.CanonicalUser ?? g.Grantee.URI ?? "None", g.Permission.Value)).ToHashSet();
        var expectedPairs = new HashSet<(string, string)> { (owner.Id, "FULL_CONTROL"), (S3Fidelity.LogDelivery, "READ") };
        Ensure(gotPairs.SetEquals(expectedPairs), $"expected grants {string.Join(", ", expectedPairs)}, got {string.Join(", ", gotPairs)}");

        // Negative: a key that was never created has no ACL to retrieve.
        var error = await S3Fidelity.Error(() => s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = "never-written.txt" }));
        Ensure(error.Code == "NoSuchKey", $"expected NoSuchKey, got {error.Code}");
        Ensure(error.Message == "The specified key does not exist.", $"unexpected NoSuchKey message: {Py.Repr(error.Message)}");
    }
}
