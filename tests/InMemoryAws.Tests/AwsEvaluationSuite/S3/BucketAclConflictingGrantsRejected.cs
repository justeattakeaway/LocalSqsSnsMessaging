// Translated from behavioral/s3/check_bucket_acl_conflicting_grants_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.bucket-acl-conflicting-grants-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class BucketAclConflictingGrantsRejected
{
    /// <summary>
    /// PutBucketAcl with both a canned ACL and an explicit grant header is rejected with InvalidRequest and
    /// its exact message and leaves the ACL unchanged; the positive control uses the non-public LogDelivery
    /// group so Block Public Access cannot mask it.
    /// </summary>
    [Test]
    [DisplayName("s3.bucket-acl-conflicting-grants-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx, objectOwnership: ObjectOwnership.BucketOwnerPreferred);

        // Positive control: a grant-header-only PutBucketAcl (no canned ACL) succeeds and is visible
        // on an independent read, proving the mutation path itself works on this bucket.
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, GrantWrite = $"uri=\"{S3Fidelity.LogDelivery}\"" });
        var granted = await S3Fidelity.BucketGrants(s3, bucket);
        Ensure(
            granted.Any(g => g.Grantee.URI == S3Fidelity.LogDelivery && g.Permission == S3Permission.WRITE),
            $"positive control failed: LogDelivery WRITE grant missing from {S3Fidelity.Describe(granted)}");

        // Reset to a known baseline (bare canned ACL, no grant header) before probing the conflict.
        await s3.PutBucketAclAsync(new PutBucketAclRequest { BucketName = bucket, ACL = S3CannedACL.Private });
        var before = S3Fidelity.Describe(await S3Fidelity.BucketGrants(s3, bucket));
        var error = await S3Fidelity.Error(() => s3.PutBucketAclAsync(new PutBucketAclRequest
        {
            BucketName = bucket,
            ACL = S3CannedACL.Private,
            GrantWrite = $"uri=\"{S3Fidelity.LogDelivery}\"",
        }));
        Ensure(
            error.Code == "InvalidRequest",
            $"expected InvalidRequest for a canned ACL combined with an explicit grant header, got {error.Code}");
        const string expected = "Specifying both Canned ACLs and Header Grants is not allowed";
        Ensure(error.Message == expected, $"expected message {Py.Repr(expected)}, got {Py.Repr(error.Message)}");
        var after = S3Fidelity.Describe(await S3Fidelity.BucketGrants(s3, bucket));
        Ensure(after == before, $"rejected PutBucketAcl must not change the bucket ACL: before={before} after={after}");
    }
}
