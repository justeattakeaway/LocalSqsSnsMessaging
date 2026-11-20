// Translated from behavioral/s3/check_list_objects_v2_delimiter.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v2-delimiter</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV2Delimiter
{
    /// <summary>ListObjectsV2 with Delimiter returns CommonPrefixes for 'directories'.</summary>
    [Test]
    [DisplayName("s3.list-objects-v2-delimiter")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "a/1.txt", ContentBody = "1" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "a/2.txt", ContentBody = "2" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "b/1.txt", ContentBody = "3" });
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "root.txt", ContentBody = "r" });
        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Delimiter = "/" });
        var prefixes = (resp.CommonPrefixes ?? []).Order(StringComparer.Ordinal).ToList();
        Ensure(prefixes.SequenceEqual(["a/", "b/"]), $"expected ['a/', 'b/'], got {Py.Repr(prefixes)}");
        var rootKeys = (resp.S3Objects ?? []).Select(o => o.Key).ToList();
        Ensure(rootKeys.SequenceEqual(["root.txt"]), $"expected ['root.txt'], got {Py.Repr(rootKeys)}");
        Ensure(resp.KeyCount == 3, $"KeyCount must count CommonPrefixes and Contents together (2 + 1 = 3), got {Py.Repr(resp.KeyCount)}");
        Ensure(resp.IsTruncated == false, $"a 3-entry stream under the default MaxKeys is not truncated, got {Py.Repr(resp.IsTruncated)}");
    }
}
