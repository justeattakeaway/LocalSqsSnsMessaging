// Translated from behavioral/s3/check_put_object_acl_no_such_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-acl-no-such-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectAclNoSuchKey
{
    /// <summary>
    /// PutObjectAcl on a key that was never written fails with NoSuchKey and its exact message on an
    /// ObjectWriter bucket (where ACL writes are enabled), and HeadObject on that key answers 404.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-acl-no-such-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        await s3.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
        {
            BucketName = bucket,
            OwnershipControls = new OwnershipControls { Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.ObjectWriter }] },
        });

        // Positive control: PutObjectAcl on a key that does exist must succeed, so the
        // NoSuchKey below is attributable to the missing key and not to ACL writes being
        // rejected outright for this bucket.
        const string controlKey = "exists.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = controlKey, ContentBody = "acl-positive-control" });
        await s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = controlKey, ACL = S3CannedACL.Private });
        const string missingKey = "never-existed.txt";
        var error = await S3Fidelity.Error(() => s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = missingKey, ACL = S3CannedACL.Private }));
        Ensure(
            error.Code == "NoSuchKey" && error.Message == "The specified key does not exist.",
            $"expected NoSuchKey, got {S3Fidelity.Repr((error.Code, error.Message))}");

        // Effect absent, via an independent read on a different operation: the key really
        // isn't there, not merely rejected by PutObjectAcl specifically.
        var (headCode, _) = await S3Fidelity.HeadError(s3, bucket, missingKey);
        Ensure(headCode == "404", $"expected HeadObject 404 for a key that was never written, got {headCode}");

        // Restore: once the same key is created, the identical PutObjectAcl call succeeds.
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = missingKey, ContentBody = "now-it-exists" });
        await s3.PutObjectAclAsync(new PutObjectAclRequest { BucketName = bucket, Key = missingKey, ACL = S3CannedACL.Private });
    }
}
