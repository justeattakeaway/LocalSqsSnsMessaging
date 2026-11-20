// Translated from behavioral/s3/check_list_objects_v2_prefix.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v2-prefix</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV2Prefix
{
    /// <summary>ListObjectsV2 with Prefix filters correctly.</summary>
    [Test]
    [DisplayName("s3.list-objects-v2-prefix")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "photos/2024/jan.jpg", ContentBody = "j" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "photos/2024/feb.jpg", ContentBody = "f" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "docs/readme.md", ContentBody = "r" });
        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = "photos/" });
        var gotKeys = (resp.S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        List<string> expectedKeys = ["photos/2024/feb.jpg", "photos/2024/jan.jpg"];
        Ensure(gotKeys.SequenceEqual(expectedKeys), $"expected {Py.Repr(expectedKeys)}, got {Py.Repr(gotKeys)}");
        Ensure(resp.KeyCount == 2, $"expected KeyCount 2, got {resp.KeyCount}");
    }
}
