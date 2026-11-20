// Translated from behavioral/s3/check_list_objects_v2_basic.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v2-basic</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV2Basic
{
    /// <summary>ListObjectsV2 returns uploaded keys with correct metadata.</summary>
    [Test]
    [DisplayName("s3.list-objects-v2-basic")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        string[] keys = ["a/1.txt", "a/2.txt", "b/1.txt"];
        foreach (var k in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = $"content-{k}" });
        }

        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        var gotKeys = (resp.S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        var expected = keys.Order(StringComparer.Ordinal).ToList();
        Ensure(gotKeys.SequenceEqual(expected), $"expected {Py.Repr(expected)}, got {Py.Repr(gotKeys)}");
        Ensure(resp.KeyCount == 3, $"expected KeyCount 3, got {Py.Repr(resp.KeyCount)}");
    }
}
