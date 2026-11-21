// Translated from behavioral/s3/check_put_object_acl_canned_and_policy_together_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-acl-canned-and-policy-together-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectAclCannedAndPolicyTogetherRejected
{
    /// <summary>
    /// PutObjectAcl rejects a canned ACL combined with an explicit AccessControlPolicy body,
    /// leaving the object's prior ACL grants unchanged.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-acl-canned-and-policy-together-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx, "sim2real-behav-aclconf-", ObjectOwnership.ObjectWriter);
        const string key = "object.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "acl-conflict-body" });

        var owner = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key })).Owner;

        // positive control: AccessControlPolicy alone is accepted and replaces the grant set
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
                    S3Fidelity.UserGrant(owner.Id, S3Permission.READ_ACP),
                ],
            },
        });
        var controlPerms = await Permissions(s3, bucket, key);
        Ensure(
            controlPerms.SequenceEqual(["FULL_CONTROL", "READ_ACP"]),
            $"expected the AccessControlPolicy-only put to set [FULL_CONTROL, READ_ACP], got {Py.Repr(controlPerms)}");

        var (code, message) = await S3Fidelity.ErrorPair(() => s3.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = bucket,
            Key = key,
            ACL = S3CannedACL.Private,
            AccessControlPolicy = new S3AccessControlList
            {
                Owner = owner,
                Grants = [S3Fidelity.UserGrant(owner.Id, S3Permission.FULL_CONTROL)],
            },
        }));
        Ensure(code == "UnexpectedContent", $"expected UnexpectedContent, got {Py.Repr(code)}");
        Ensure(message == "This request does not support content", $"unexpected message {Py.Repr(message)}");

        var afterPerms = await Permissions(s3, bucket, key);
        Ensure(
            afterPerms.SequenceEqual(["FULL_CONTROL", "READ_ACP"]),
            $"the rejected put must leave the prior two-grant ACL intact, got {Py.Repr(afterPerms)}");
    }

    /// <summary><c>sorted(g["Permission"] for g in s3.get_object_acl(...)["Grants"])</c>.</summary>
    private static async Task<List<string>> Permissions(IAmazonS3 s3, string bucket, string key) =>
        [.. (await S3Fidelity.ObjectGrants(s3, bucket, key)).Select(g => g.Permission.Value).Order(StringComparer.Ordinal)];
}
