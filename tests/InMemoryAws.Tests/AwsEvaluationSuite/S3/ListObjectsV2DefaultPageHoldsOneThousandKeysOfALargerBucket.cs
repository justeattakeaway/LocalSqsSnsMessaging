// Translated from behavioral/s3/check_list_objects_v2_default_page_holds_one_thousand_keys_of_a_larger_bucket.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.list-objects-v2-default-page-holds-one-thousand-keys-of-a-larger-bucket</summary>
[Category("AwsEvaluationSuite")]
public sealed class ListObjectsV2DefaultPageHoldsOneThousandKeysOfALargerBucket
{
    private const int Count = 1001;
    private const int Page = 1000;

    /// <summary>
    /// A bucket holds 1,001 objects; ListObjectsV2 without MaxKeys pages them 1,000 at a time.
    ///
    /// The first page holds the lowest 1,000 keys, truncated; its continuation token returns the last.
    ///
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html
    /// </summary>
    [Test]
    [DisplayName("s3.list-objects-v2-default-page-holds-one-thousand-keys-of-a-larger-bucket")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-lpage-");
        var keys = Enumerable.Range(0, Count).Select(i => $"obj/{i.ToString("0000", CultureInfo.InvariantCulture)}").ToList();

        async Task<bool> Put(string key)
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = key });
            return true;
        }

        using (ctx.Concurrently())
        {
            await PoolMap(8, keys, Put);
        }

        var first = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        var firstKeys = (first.S3Objects ?? []).Select(o => o.Key).ToList();
        Ensure(first.MaxKeys == Page, $"the default MaxKeys must be {Page}; got {Py.Repr(first.MaxKeys)}");
        Ensure(first.KeyCount == Page, $"the first page must count {Page} keys; got {Py.Repr(first.KeyCount)}");
        Ensure(
            firstKeys.SequenceEqual(keys.Take(Page)),
            $"the first page must be the {Page} lowest keys in ascending order; got {firstKeys.Count} keys from {Py.Repr(firstKeys.Take(1).ToList())} to {Py.Repr(firstKeys.TakeLast(1).ToList())}");
        Ensure(first.IsTruncated == true, $"a {Count}-key bucket's first page must be truncated; got {Py.Repr(first.IsTruncated)}");
        var token = first.NextContinuationToken;
        Ensure(!string.IsNullOrEmpty(token), "a truncated page must carry a NextContinuationToken");

        var second = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, ContinuationToken = token });
        var secondKeys = (second.S3Objects ?? []).Select(o => o.Key).ToList();
        Ensure(secondKeys.SequenceEqual(keys.Skip(Page)), $"the continuation page must hold only {Py.Repr(keys.Skip(Page).ToList())}; got {Py.Repr(secondKeys)}");
        Ensure(second.KeyCount == Count - Page, $"the continuation page must count {Count - Page} key(s); got {Py.Repr(second.KeyCount)}");
        Ensure(second.IsTruncated == false, $"the last page must not be truncated; got {Py.Repr(second.IsTruncated)}");
        Ensure(second.ContinuationToken == token, "the continuation page must echo the token it was sent");
        Ensure(second.NextContinuationToken is null, "the last page must carry no NextContinuationToken");
    }

    /// <summary><c>ThreadPoolExecutor(max_workers=workers).map(fn, items)</c>: results in order.</summary>
    private static async Task<TResult[]> PoolMap<T, TResult>(int workers, IReadOnlyList<T> items, Func<T, Task<TResult>> fn)
    {
        using var gate = new SemaphoreSlim(workers);
        return await Task.WhenAll(items.Select(item => Task.Run(async () =>
        {
            await gate.WaitAsync();
            try
            {
                return await fn(item);
            }
            finally
            {
                gate.Release();
            }
        })));
    }
}
