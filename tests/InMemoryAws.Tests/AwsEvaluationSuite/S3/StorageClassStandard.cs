// Translated from behavioral/s3/check_storage_class_standard.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.storage-class-standard</summary>
[Category("AwsEvaluationSuite")]
public sealed class StorageClassStandard
{
    /// <summary>
    /// Objects stored with STANDARD storage class are reported correctly.
    /// </summary>
    [Test]
    [DisplayName("s3.storage-class-standard")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.PutWithClass(s3, bucket, "std.txt", "standard"u8.ToArray(), "STANDARD");

        // HeadObject may not return StorageClass for STANDARD (it's the default),
        // but ListObjectsV2 should show it.
        var contents = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "std.txt" })).S3Objects ?? [];
        Ensure(contents.Count == 1, "assert len(contents) == 1");
        Ensure(contents[0].StorageClass is not null, $"ListObjectsV2 omitted StorageClass entirely: {Py.Repr(contents[0].Key)}");
        var sc = contents[0].StorageClass.Value;
        Ensure(sc == "STANDARD", $"expected STANDARD, got {Py.Repr(sc)}");
    }
}
