// Translated from behavioral/s3/check_object_storage_class_reflected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.object-storage-class-reflected</summary>
[Category("AwsEvaluationSuite")]
public sealed class ObjectStorageClassReflected
{
    /// <summary>
    /// StorageClass is persisted per-object: STANDARD_IA surfaces on HeadObject/ListObjectsV2,
    /// while STANDARD is omitted from HeadObject.
    /// </summary>
    [Test]
    [DisplayName("s3.object-storage-class-reflected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-");
        await S3Fidelity.PutWithClass(s3, name, "ia.txt", "x"u8.ToArray(), "STANDARD_IA");
        await S3Fidelity.PutWithClass(s3, name, "std.txt", "x"u8.ToArray());
        var iaHead = await s3.GetObjectMetadataAsync(name, "ia.txt");
        Ensure(iaHead.StorageClass?.Value == "STANDARD_IA", $"expected STANDARD_IA, got {Py.Repr(iaHead.StorageClass?.Value)}");
        var stdHead = await s3.GetObjectMetadataAsync(name, "std.txt");
        Ensure(stdHead.StorageClass is null, $"HeadObject must omit StorageClass for STANDARD objects, got {Py.Repr(stdHead.StorageClass?.Value)}");
        var listed = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = name })).S3Objects ?? []).ToDictionary(o => o.Key, o => o.StorageClass?.Value);
        Ensure(
            listed.Count == 2 && listed.GetValueOrDefault("ia.txt") == "STANDARD_IA" && listed.GetValueOrDefault("std.txt") == "STANDARD",
            $"unexpected listing storage classes: {Py.Repr(listed.Select(l => $"{l.Key}={l.Value}").ToList())}");
    }
}
