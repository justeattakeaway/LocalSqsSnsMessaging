// Translated from behavioral/s3/check_op_contract_list_objects_v2.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.op-contract-listObjectsV2</summary>
[Category("AwsEvaluationSuite")]
public sealed class OpContractListObjectsV2
{
    private const string BucketPrefix = "sim2real-behav-";

    // S3's refusal of a bucket that does not exist, as real S3 returns it.
    private static readonly (string?, string?) NoSuchBucket = ("NoSuchBucket", "The specified bucket does not exist");

    /// <summary>ListObjectsV2 honors Delimiter/Prefix grouping, KeyCount, and pagination; errors NoSuchBucket when missing.</summary>
    [Test]
    [DisplayName("s3.op-contract-listObjectsV2")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await S3Fidelity.CreateBucket(ctx);
        var s3 = ctx.S3;
        string[] keys = ["a/1.txt", "a/2.txt", "b/1.txt"];
        foreach (var k in keys)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = k, ContentBody = "x" });
            ctx.OnCleanup(() => s3.DeleteObjectAsync(bucket, k));
        }

        // with a delimiter, top-level "directories" surface as CommonPrefixes, not Contents
        var resp = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Delimiter = "/" });
        var prefixes = (resp.CommonPrefixes ?? []).Order(StringComparer.Ordinal).ToList();
        Ensure(prefixes.SequenceEqual(["a/", "b/"]), $"expected ['a/','b/'], got {Py.Repr(prefixes)}");
        Ensure((resp.S3Objects ?? []).Count == 0, $"expected no top-level Contents, got {(resp.S3Objects ?? []).Count}");
        Ensure(resp.KeyCount == 2, $"expected KeyCount 2 (2 common prefixes), got {resp.KeyCount}");

        // without a delimiter, every key is flattened into Contents
        var resp2 = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        var gotKeys = (resp2.S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        var sortedKeys = keys.Order(StringComparer.Ordinal).ToList();
        Ensure(gotKeys.SequenceEqual(sortedKeys), $"expected {Py.Repr(sortedKeys)}, got {Py.Repr(gotKeys)}");
        Ensure(resp2.KeyCount == 3, $"expected KeyCount 3, got {resp2.KeyCount}");
        Ensure(resp2.IsTruncated == false, $"expected IsTruncated False, got {resp2.IsTruncated}");

        // pagination: MaxKeys=1 must truncate and provide a usable continuation token
        var resp3 = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1 });
        Ensure(resp3.IsTruncated == true, $"expected IsTruncated True, got {resp3.IsTruncated}");
        Ensure(resp3.S3Objects.Count == 1, $"expected 1 item under MaxKeys=1, got {resp3.S3Objects.Count}");
        var token = resp3.NextContinuationToken;
        var resp4 = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 1, ContinuationToken = token });
        Ensure(resp4.S3Objects[0].Key != resp3.S3Objects[0].Key, "pagination returned a repeated key");

        // distinct error boundary: nonexistent bucket
        var missing = FidelityContext.UniqueName(BucketPrefix + "missing-");
        var err = await FidelityContext.CaptureError(() => s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = missing }))
            ?? throw Fail("expected a ClientError, but the call SUCCEEDED");
        Ensure((err.Code, err.Message) == NoSuchBucket, $"expected NoSuchBucket, got {err}");
    }
}
