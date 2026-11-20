// Translated from behavioral/s3/check_storage_class_reduced_redundancy.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.storage-class-reduced-redundancy</summary>
[Category("AwsEvaluationSuite")]
public sealed class StorageClassReducedRedundancy
{
    /// <summary>
    /// Objects stored with REDUCED_REDUNDANCY storage class report it back on both
    /// HeadObject and ListObjectsV2 — two independent reads, neither the PutObject that wrote it.
    /// </summary>
    [Test]
    [DisplayName("s3.storage-class-reduced-redundancy")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await S3Fidelity.PutWithClass(s3, bucket, "rr.txt", "reduced"u8.ToArray(), "REDUCED_REDUNDANCY");
        var head = await s3.GetObjectMetadataAsync(bucket, "rr.txt");
        Ensure(head.StorageClass?.Value == "REDUCED_REDUNDANCY", $"expected REDUCED_REDUNDANCY on HeadObject, got {Py.Repr(head.StorageClass?.Value)}");
        var contents = (await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "rr.txt" })).S3Objects ?? [];
        Ensure(contents.Count == 1, $"expected exactly one listed object, got {contents.Count}");
        var sc = contents[0].StorageClass?.Value;
        Ensure(sc == "REDUCED_REDUNDANCY", $"expected REDUCED_REDUNDANCY on ListObjectsV2, got {sc}");
    }
}
