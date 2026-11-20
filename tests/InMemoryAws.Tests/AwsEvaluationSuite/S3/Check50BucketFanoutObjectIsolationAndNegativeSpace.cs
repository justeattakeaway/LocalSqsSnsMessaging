// Translated from behavioral/s3/check_50_bucket_fanout_object_isolation_and_negative_space.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.50-bucket-fanout-object-isolation-and-negative-space</summary>
[Category("AwsEvaluationSuite")]
public sealed class Check50BucketFanoutObjectIsolationAndNegativeSpace
{
    private const int N = 50;
    private const string Key = "payload.bin";
    private const int Dupes = 8;
    private const int PageSize = 10;
    private const double ListDeadlineS = 120.0;
    private const double ResidualDeadlineS = 180.0;

    // Bounded retry so throttling on CreateBucket is a delay rather than a failure. The assertions
    // need 50 buckets to exist simultaneously, not 50 simultaneous creations.
    private const double CreateDeadlineS = 120.0;

    /// <summary>
    /// Fifty concurrent buckets each hold one object under the same key with its own bytes, all list in
    /// a paginated ListBuckets walk, a sentinel is untouched, duplicates are refused, deletes stick.
    ///
    /// Reference: https://docs.aws.amazon.com/AmazonS3/latest/userguide/Welcome.html#ConsistencyModel
    /// </summary>
    [Test]
    [DisplayName("s3.50-bucket-fanout-object-isolation-and-negative-space")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3Client(maxAttempts: 3);
        var region = ctx.Region;
        var runid = FidelityContext.UniqueName(string.Empty);

        PutBucketRequest CreateKw(string bucket)
        {
            // us-east-1 is CreateBucket's default region and rejects an explicit
            // LocationConstraint for it; every other region requires one.
            var kw = new PutBucketRequest { BucketName = bucket, UseClientRegion = false };
            if (region != "us-east-1")
            {
                kw.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(region) };
            }

            return kw;
        }

        async Task Purge(string bucket)
        {
            var keys = new List<KeyVersion>();
            var request = new ListObjectsV2Request { BucketName = bucket };
            do
            {
                var page = await s3.ListObjectsV2Async(request);
                keys.AddRange((page.S3Objects ?? []).Select(o => new KeyVersion { Key = o.Key }));
                request.ContinuationToken = page.NextContinuationToken;
            }
            while (request.ContinuationToken is not null);
            if (keys.Count > 0)
            {
                await s3.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = keys });
            }

            await s3.DeleteBucketAsync(bucket);
        }

        var sentinel = $"sim2real-behav-d1-sentinel-{runid}";
        await s3.PutBucketAsync(CreateKw(sentinel));
        ctx.OnCleanup(() => Purge(sentinel));
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = sentinel, Key = "sentinel", ContentBody = "SENTINEL" });

        var buckets = Enumerable.Range(0, N).Select(i => $"sim2real-behav-d1-{runid}-{Two(i)}").ToList();
        var bodies = buckets.Select((b, i) => (b, Encoding.ASCII.GetBytes($"bucket-{Two(i)}-").Concat(RandomNumberGenerator.GetBytes(1000)).ToArray()))
            .ToDictionary(p => p.b, p => p.Item2, StringComparer.Ordinal);

        async Task<string?> Make(string bucket)
        {
            var clock = FidelityTime.StartNew();
            var attempt = 0;
            while (true)
            {
                var error = await FidelityContext.CaptureError(() => s3.PutBucketAsync(CreateKw(bucket)));
                if (error is null)
                {
                    ctx.OnCleanup(() => Purge(bucket));
                    return null;
                }

                if (error.Code is not ("OperationAborted" or "SlowDown" or "503") || clock.Elapsed.TotalSeconds > CreateDeadlineS)
                {
                    return $"{bucket}: {error.Code}";
                }

                attempt += 1;
                await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Min(2.0, 0.1 * attempt) + (0.05 * (attempt % 3))));
            }
        }

        string?[] made;
        using (ctx.Concurrently())
        {
            made = await PoolMap(N, buckets, Make);
        }

        var failed = made.Where(e => e is not null).ToList();
        Ensure(failed.Count == 0, $"CreateBucket failed for {failed.Count} of {N} buckets: {Py.Repr(failed)}");

        async Task<(string Bucket, string ETag)> Put(string bucket)
        {
            var r = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(bodies[bucket]) });
            return (bucket, r.ETag.Trim('"'));
        }

        Dictionary<string, string> putEtags;
        using (ctx.Concurrently())
        {
            putEtags = (await PoolMap(N, buckets, Put)).ToDictionary(p => p.Bucket, p => p.ETag, StringComparer.Ordinal);
        }

        async Task<string?> Head(string bucket)
        {
            var error = await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = bucket }));
            return error is null ? null : $"{bucket}: {S3Fidelity.Botocore(error).Code}";
        }

        List<string?> headless;
        using (ctx.Concurrently())
        {
            headless = [.. (await PoolMap(N, buckets, Head)).Where(e => e is not null)];
        }

        Ensure(headless.Count == 0, $"HeadBucket failed after a successful CreateBucket: {Py.Repr(headless)}");

        // ListBuckets after CreateBucket is not documented as strongly consistent, so it is polled.
        async Task<(List<string> Names, int Pages)> Walk()
        {
            var names = new List<string>();
            string? token = null;
            var pages = 0;
            while (true)
            {
                var kw = new ListBucketsRequest { MaxBuckets = PageSize };
                if (token is not null)
                {
                    kw.ContinuationToken = token;
                }

                var resp = await s3.ListBucketsAsync(kw);
                var page = (resp.Buckets ?? []).Select(b => b.BucketName).ToList();
                Ensure(page.Count <= PageSize, $"ListBuckets returned {page.Count} buckets for MaxBuckets={PageSize}");
                names.AddRange(page);
                pages += 1;
                token = resp.ContinuationToken;
                if (string.IsNullOrEmpty(token))
                {
                    return (names, pages);
                }
            }
        }

        var listClock = FidelityTime.StartNew();
        List<string> walk;
        int pages;
        while (true)
        {
            (walk, pages) = await Walk();
            if (buckets.All(walk.Contains))
            {
                break;
            }

            Ensure(
                listClock.Elapsed.TotalSeconds < ListDeadlineS,
                $"ListBuckets never listed all {N} buckets within {ListDeadlineS}s; {Py.Repr(buckets.Except(walk).Order(StringComparer.Ordinal).ToList())} were still missing");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2.0));
        }

        Ensure(
            walk.Count == walk.Distinct(StringComparer.Ordinal).Count(),
            $"the paginated ListBuckets walk repeated a name -- the ContinuationToken is not a cursor: {Py.Repr(walk.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList())}");
        Ensure(
            pages >= 5,
            $"MaxBuckets={PageSize} over {walk.Count} buckets produced only {pages} page(s); MaxBuckets was ignored, so the walk was never a paginated walk");
        Ensure(
            walk.Contains(sentinel),
            $"the ListBuckets walk of {walk.Count} buckets omits the pre-existing sentinel {Py.Repr(sentinel)} -- the listing is echoing this run rather than reading the registry");

        // ETag == MD5(body) holds because every PUT here is single-part and unencrypted.
        async Task<(string Bucket, byte[] Body, string ETag, List<string> Keys, int? KeyCount)> Read(string bucket)
        {
            byte[] body;
            string etag;
            using (var got = await s3.GetObjectAsync(bucket, Key))
            {
                body = await S3Fidelity.ReadBody(got);
                etag = got.ETag.Trim('"');
            }

            var listing = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
            return (bucket, body, etag, [.. (listing.S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal)], listing.KeyCount);
        }

        (string Bucket, byte[] Body, string ETag, List<string> Keys, int? KeyCount)[] reads;
        using (ctx.Concurrently())
        {
            reads = await PoolMap(N, buckets, Read);
        }

        var mismatched = reads.Where(r => !r.Body.AsSpan().SequenceEqual(bodies[r.Bucket])).ToList();
        Ensure(
            mismatched.Count == 0,
            "the object read back from each bucket is not the object written to it. "
            + string.Join(", ", mismatched.Select(r => $"{r.Bucket}: ({Py.Repr(bodies[r.Bucket][..14])}, {Py.Repr(r.Body.Take(14).ToArray())})")));
        foreach (var (bucket, body, etag, keys, keyCount) in reads)
        {
            var md5 = Convert.ToHexStringLower(MD5.HashData(body));
            Ensure(
                etag == md5,
                $"{bucket}: GetObject reported ETag {Py.Repr(etag)} but MD5 of the {body.Length} bytes it returned is {Py.Repr(md5)} -- the digest does not describe the stored bytes");
            Ensure(etag == putEtags[bucket], $"{bucket}: PutObject reported ETag {Py.Repr(putEtags[bucket])} and GetObject {Py.Repr(etag)}");
            Ensure(
                keys.SequenceEqual([Key]),
                $"{bucket}: ListObjectsV2 returned {Py.Repr(keys)}, expected exactly [{Py.Repr(Key)}] -- {keys.Count} keys in a bucket written exactly one means the keyspace is shared");
            Ensure(keyCount == 1, $"{bucket}: KeyCount was {Py.Repr(keyCount)}, expected 1");
        }

        var sentKeys = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = sentinel })).S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(sentKeys.SequenceEqual(["sentinel"]), $"the sentinel bucket holds {Py.Repr(sentKeys)} after the fan-out, expected [\"sentinel\"] only");
        var sentBody = await S3Fidelity.GetBytes(s3, sentinel, "sentinel");
        Ensure(sentBody.AsSpan().SequenceEqual("SENTINEL"u8), $"the sentinel object body is now {Py.Repr(sentBody.Take(32).ToArray())}");

        // Buckets from a concurrent run of this check must not hold this run's bodies.
        var ours = buckets.Append(sentinel).ToHashSet(StringComparer.Ordinal);
        var siblings = walk.Where(n => n.StartsWith("sim2real-behav-d1-", StringComparison.Ordinal) && !ours.Contains(n)).ToList();
        foreach (var other in siblings)
        {
            byte[] leaked;
            try
            {
                leaked = await S3Fidelity.GetBytes(s3, other, Key);
            }
            catch (AmazonS3Exception)
            {
                continue; // a sibling run's bucket we cannot read tells us nothing either way
            }

            Ensure(
                !bodies.Values.Any(b => b.AsSpan().SequenceEqual(leaked)),
                $"a body written by THIS run turned up in {Py.Repr(other)}, a bucket belonging to a different run");
        }

        var dupes = buckets.Take(Dupes).ToList();
        string?[] codes;
        using (ctx.Concurrently())
        {
            codes = await PoolMap(Dupes, dupes, async b =>
            {
                var error = await FidelityContext.CaptureError(() => s3.PutBucketAsync(CreateKw(b)))
                    ?? throw Fail("the call succeeded; real AWS rejects it");
                return error.Code;
            });
        }

        Ensure(
            codes.All(c => c == "BucketAlreadyOwnedByYou"),
            $"concurrent CreateBucket on names this account already owns returned {Py.Repr(codes)}, expected BucketAlreadyOwnedByYou for all {Dupes}");
        foreach (var bucket in dupes)
        {
            var after = await S3Fidelity.GetBytes(s3, bucket, Key);
            Ensure(
                after.AsSpan().SequenceEqual(bodies[bucket]),
                $"{bucket}: the rejected CreateBucket truncated or replaced the object -- body is now {after.Length} bytes starting {Py.Repr(after.Take(14).ToArray())}");
            var keys = ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal).ToList();
            Ensure(keys.SequenceEqual([Key]), $"{bucket}: keys after the rejected CreateBucket are {Py.Repr(keys)}");
        }

        // A deleted bucket can still answer HeadBucket briefly, so the residual check polls.
        async Task<string?> Rm(string bucket)
        {
            var error = await FidelityContext.CaptureError(() => Purge(bucket));
            return error is null ? null : $"{bucket}: {error.Code}";
        }

        List<string?> undeleted;
        using (ctx.Concurrently())
        {
            undeleted = [.. (await PoolMap(N, buckets, Rm)).Where(e => e is not null)];
        }

        Ensure(undeleted.Count == 0, $"DeleteBucket failed for: {Py.Repr(undeleted)}");

        var residualClock = FidelityTime.StartNew();
        while (true)
        {
            string?[] heads;
            using (ctx.Concurrently())
            {
                heads = await PoolMap(N, buckets, Head);
            }

            var alive = buckets.Zip(heads).Where(p => p.Second is null).Select(p => p.First).Order(StringComparer.Ordinal).ToList();
            if (alive.Count == 0)
            {
                break;
            }

            Ensure(
                residualClock.Elapsed.TotalSeconds < ResidualDeadlineS,
                $"{alive.Count} of {N} buckets still answer HeadBucket {ResidualDeadlineS}s after a successful DeleteBucket: {Py.Repr(alive)}");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }

        var (final, _) = await Walk();
        var still = buckets.Intersect(final).Order(StringComparer.Ordinal).ToList();
        Ensure(still.Count == 0, $"ListBuckets still lists deleted buckets: {Py.Repr(still)}");
        Ensure(
            final.Contains(sentinel),
            $"the sentinel bucket vanished from ListBuckets while the fan-out was deleted -- the delete reached beyond the {N} named buckets");
    }

    private static string Two(int i) => i.ToString("00", CultureInfo.InvariantCulture);

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
