// Translated from behavioral/s3/check_get_object_acl_no_such_key.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3.Model;
using Amazon.S3;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.get-object-acl-no-such-key</summary>
[Category("AwsEvaluationSuite")]
public sealed class GetObjectAclNoSuchKey
{
    /// <summary>
    /// Positive control: GetObjectAcl on a key that exists succeeds and reports an owner. GetObjectAcl on a
    /// key that was never written raises the specific NoSuchKey error.
    /// </summary>
    [Test]
    [DisplayName("s3.get-object-acl-no-such-key")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.NewBucket(ctx);
        const string key = "exists.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "acl-positive-control" });
        var acl = await s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = key });
        var ownerId = acl.Owner?.Id;
        Ensure(!string.IsNullOrEmpty(ownerId), $"expected an owner ID on the positive control, got {Py.Repr(ownerId)}");
        var err = await S3Fidelity.Error(
            () => s3.GetObjectAclAsync(new GetObjectAclRequest { BucketName = bucket, Key = "never-existed.txt" }),
            "GetObjectAcl on a never-written key succeeded; expected a refusal");
        Ensure(err.Code == "NoSuchKey", $"expected NoSuchKey, got {err}");
        Ensure(err.Message == "The specified key does not exist.", err.Message ?? "None");
    }
}
