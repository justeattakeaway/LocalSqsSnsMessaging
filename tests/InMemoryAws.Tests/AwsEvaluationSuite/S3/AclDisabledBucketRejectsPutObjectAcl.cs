// Translated from behavioral/s3/check_acl_disabled_bucket_rejects_put_object_acl.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.acl-disabled-bucket-rejects-put-object-acl</summary>
[Category("AwsEvaluationSuite")]
public sealed class AclDisabledBucketRejectsPutObjectAcl
{
    /// <summary>
    /// On a BucketOwnerEnforced bucket PutObjectAcl is refused without effect: a private ACL with
    /// AccessControlListNotSupported, a public one with AccessDenied (Block Public Access applies first).
    /// The same call succeeds on an ObjectWriter sibling bucket, the positive control.
    /// </summary>
    [Test]
    [DisplayName("s3.acl-disabled-bucket-rejects-put-object-acl")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        const string aclNotSupportedMessage = "The bucket does not allow ACLs";
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        const string key = "acl-disabled.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "x" });
        var baselineGrants = S3Fidelity.Describe(await S3Fidelity.ObjectGrants(s3, bucket, key));

        // A *private* ACL is not a public grant, so Block Public Access cannot be what
        // rejects it; the only reason left is that ACLs are disabled on the bucket.
        var error = await S3Fidelity.Error(() => s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = key, ACL = S3CannedACL.Private }));
        Ensure(
            error.Code == "AccessControlListNotSupported",
            $"expected AccessControlListNotSupported on a BucketOwnerEnforced bucket, got {Py.Repr(error.Code)}");
        Ensure(error.Message == aclNotSupportedMessage, $"unexpected message {Py.Repr(error.Message)}");
        var grants = S3Fidelity.Describe(await S3Fidelity.ObjectGrants(s3, bucket, key));
        Ensure(grants == baselineGrants, $"object ACL grants changed despite a rejected PutObjectAcl: {baselineGrants} -> {grants}");

        // A *public* ACL trips Block Public Access first, which answers with a
        // different code -- the precedence between the two guards is itself behaviour.
        error = await S3Fidelity.Error(() => s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = key, ACL = S3CannedACL.PublicRead }));
        Ensure(
            error.Code == "AccessDenied",
            $"expected AccessDenied (Block Public Access precedes the ACL-disabled check), got {Py.Repr(error.Code)}");

        // The message names the caller's own identity, read here rather than pinned.
        var expected = S3Fidelity.PublicAclRefusal(ctx, "PutObjectAcl", $"arn:aws:s3:::{bucket}/{key}");
        Ensure(error.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(error.Message)}");
        grants = S3Fidelity.Describe(await S3Fidelity.ObjectGrants(s3, bucket, key));
        Ensure(grants == baselineGrants, $"object ACL grants changed despite a rejected PutObjectAcl: {baselineGrants} -> {grants}");

        // By contrast, a canned ACL on PutObject that grants nothing beyond the bucket
        // owner is accepted as a no-op rather than refused...
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "acl2.txt", ContentBody = "x", CannedACL = S3CannedACL.Private });

        // ...whereas PutBucketAcl, whose whole purpose is to set an ACL, is refused
        // even for the private canned ACL.
        var baselineBucketGrants = S3Fidelity.Describe(await S3Fidelity.BucketGrants(s3, bucket));
        error = await S3Fidelity.Error(() => s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.Private }));
        Ensure(
            error.Code == "AccessControlListNotSupported",
            $"expected PutBucketAcl to be refused with AccessControlListNotSupported, got {Py.Repr(error.Code)}");
        Ensure(error.Message == aclNotSupportedMessage, $"unexpected message {Py.Repr(error.Message)}");
        var bucketGrants = S3Fidelity.Describe(await S3Fidelity.BucketGrants(s3, bucket));
        Ensure(
            bucketGrants == baselineBucketGrants,
            $"bucket ACL grants changed despite a rejected PutBucketAcl: {baselineBucketGrants} -> {bucketGrants}");

        // Positive control: PutObjectAcl on a sibling bucket whose ownership control is ObjectWriter
        // (ACLs enabled) succeeds and has an observable effect -- ruling out an account-wide inability
        // to call PutObjectAcl at all, which would otherwise make every rejection above
        // indistinguishable from "PutObjectAcl never works here". The grant names the S3 log delivery
        // group, which is not public, so the sibling keeps Block Public Access on.
        var writerBucket = await S3Fidelity.NewBucket(ctx, objectOwnership: ObjectOwnership.ObjectWriter);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = writerBucket, Key = key, ContentBody = "x" });
        var ownerId = (await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = writerBucket, Key = key })).Owner.Id;
        await s3.PutObjectAclAsync(new PutObjectAclRequest
        {
            BucketName = writerBucket,
            Key = key,
            GrantFullControl = $"id={ownerId}",
            GrantRead = $"uri={S3Fidelity.LogDelivery}",
        });
        var writerGrants = await S3Fidelity.ObjectGrants(s3, writerBucket, key);
        var granteeUris = writerGrants.Where(g => S3Fidelity.GranteeType(g) == "Group").Select(g => g.Grantee.URI).ToHashSet();
        Ensure(
            granteeUris.SetEquals([S3Fidelity.LogDelivery]),
            "expected the positive control's PutObjectAcl to apply exactly its log-delivery grant, " +
            $"got {S3Fidelity.Describe(writerGrants)}");
    }
}
