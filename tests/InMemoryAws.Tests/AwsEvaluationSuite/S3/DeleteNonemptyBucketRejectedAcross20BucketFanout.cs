// Translated from behavioral/s3/check_delete_nonempty_bucket_rejected_across_20_bucket_fanout.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.delete-nonempty-bucket-rejected-across-20-bucket-fanout</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteNonemptyBucketRejectedAcross20BucketFanout
{
    private const int N = 20;
    private const string Key = "payload.bin";
    private const double ResidualDeadlineS = 180.0;

    // What Absent returns when its call succeeds; no S3 error code can equal it.
    private const string Succeeded = "<the call succeeded>";

    /// <summary>
    /// Across twenty buckets that each hold one object under the same key, emptying bucket 0 makes only
    /// bucket 0 deletable: DeleteBucket on the other nineteen fails with BucketNotEmpty and its exact
    /// message and leaves each one holding its own byte-exact body.
    /// </summary>
    [Test]
    [DisplayName("s3.delete-nonempty-bucket-rejected-across-20-bucket-fanout")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3Client(maxAttempts: 3);
        var region = ctx.Region;
        var runid = FidelityContext.UniqueName(string.Empty);

        PutBucketRequest CreateKw(string bucket)
        {
            var kw = new PutBucketRequest { BucketName = bucket, UseClientRegion = false };

            // us-east-1 is the one region CreateBucket rejects a LocationConstraint for.
            if (region != "us-east-1")
            {
                kw.PutBucketConfiguration = new PutBucketConfiguration { LocationConstraint = new BucketLocationConstraint(region) };
            }

            return kw;
        }

        // The emptied bucket is deleted by the check body itself; its teardown has nothing to do.
        Task Purge(string bucket) => S3Fidelity.EmptyAndDelete(s3, bucket);

        var buckets = Enumerable.Range(0, N).Select(i => $"sim2real-behav-d1e-{runid}-{i.ToString("00", CultureInfo.InvariantCulture)}").ToList();
        var bodies = buckets.Select((b, i) => (b, Encoding.ASCII.GetBytes($"bucket-{i.ToString("00", CultureInfo.InvariantCulture)}-").Concat(RandomNumberGenerator.GetBytes(500)).ToArray()))
            .ToDictionary(p => p.b, p => p.Item2, StringComparer.Ordinal);

        async Task<string?> Make(string bucket)
        {
            var error = await FidelityContext.CaptureError(async () =>
            {
                await s3.PutBucketAsync(CreateKw(bucket));
                ctx.OnCleanup(() => Purge(bucket));
                await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key, InputStream = new MemoryStream(bodies[bucket]) });
            });
            return error is null ? null : $"{bucket}: {error.Code}";
        }

        List<string?> failed;
        using (ctx.Concurrently())
        {
            failed = [.. (await PoolMap(N, buckets, Make)).Where(e => e is not null)];
        }

        Ensure(failed.Count == 0, $"setup fan-out failed for {failed.Count} of {N}: {Py.Repr(failed)}");

        // every bucket really does hold exactly its own object -- the setup is real
        async Task<(string Bucket, List<string> Keys)> Keys(string bucket) =>
            (bucket, [.. ((await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects ?? []).Select(o => o.Key).Order(StringComparer.Ordinal)]);

        (string Bucket, List<string> Keys)[] before;
        using (ctx.Concurrently())
        {
            before = await PoolMap(N, buckets, Keys);
        }

        Ensure(
            before.All(b => b.Keys.SequenceEqual([Key])),
            "the fan-out did not put exactly one object in each bucket: " + string.Join(", ", before.Where(b => !b.Keys.SequenceEqual([Key])).Select(b => $"{b.Bucket}: {Py.Repr(b.Keys)}")));

        // ---- break exactly one thing: empty bucket 0, leave the other nineteen populated ----
        var victim = buckets[0];
        var siblings = buckets.Skip(1).ToList();
        await s3.DeleteObjectAsync(victim, Key);
        var emptied = await s3.ListObjectsV2Async(new ListObjectsV2Request { BucketName = victim });
        Ensure(
            emptied.KeyCount == 0 && emptied.S3Objects is null or { Count: 0 },
            $"{Py.Repr(victim)} still lists {Py.Repr(emptied.S3Objects?.Select(o => o.Key).ToList())} after DeleteObject");

        // the delete did not reach into any sibling -- this is what makes the nineteen rejections mean
        // something rather than being a coincidence of a shared store
        (string Bucket, List<string> Keys)[] afterDelete;
        using (ctx.Concurrently())
        {
            afterDelete = await PoolMap(N, siblings, Keys);
        }

        Ensure(
            afterDelete.All(b => b.Keys.SequenceEqual([Key])),
            $"DeleteObject on {Py.Repr(victim)} emptied sibling buckets that share the key name: " + string.Join(", ", afterDelete.Where(b => !b.Keys.SequenceEqual([Key])).Select(b => $"{b.Bucket}: {Py.Repr(b.Keys)}")));

        // ---- the negative: nineteen concurrent DeleteBucket calls on non-empty buckets ----
        (string? Code, string? Message)[] codes;
        using (ctx.Concurrently())
        {
            codes = await PoolMap(siblings.Count, siblings, async b =>
            {
                var error = await FidelityContext.CaptureError(() => s3.DeleteBucketAsync(b))
                    ?? throw Fail("DeleteBucket succeeded on a non-empty bucket; real AWS rejects that");
                return (error.Code, error.Message);
            });
        }

        Ensure(
            codes.All(c => c == ("BucketNotEmpty", "The bucket you tried to delete is not empty")),
            $"DeleteBucket on {siblings.Count} non-empty buckets returned {Py.Repr(codes.Select(c => $"({c.Code}, {c.Message})").ToList())}, expected BucketNotEmpty with AWS's exact message for every one");

        // ---- effect ABSENT: all nineteen survive, with their own bytes ----
        (string Bucket, byte[] Body)[] surviving;
        using (ctx.Concurrently())
        {
            surviving = await PoolMap(N, siblings, async b => (b, await S3Fidelity.GetBytes(s3, b, Key)));
        }

        var altered = surviving.Where(s => !s.Body.AsSpan().SequenceEqual(bodies[s.Bucket])).ToList();
        Ensure(
            altered.Count == 0,
            "a rejected DeleteBucket still destroyed or altered an object: " + string.Join(", ", altered.Select(s => $"{s.Bucket}: ({Py.Repr(bodies[s.Bucket][..14])}, {Py.Repr(s.Body.Take(14).ToArray())})")));

        string[] absent;
        using (ctx.Concurrently())
        {
            absent = await PoolMap(N, siblings, b => Absent(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = b })));
        }

        var gone = siblings.Zip(absent).Where(p => p.Second != Succeeded).Select(p => $"{p.First}: {p.Second}").ToList();
        Ensure(gone.Count == 0, $"a rejected DeleteBucket removed the bucket anyway: {Py.Repr(gone)}");

        // ---- positive control, same file: the bucket we DID empty deletes cleanly ----
        await s3.DeleteBucketAsync(victim);
        var clock = FidelityTime.StartNew();
        while (true)
        {
            var error = await FidelityContext.CaptureError(() => s3.HeadBucketAsync(new HeadBucketRequest { BucketName = victim }));
            if (error is not null)
            {
                var code = S3Fidelity.Botocore(error).Code;
                Ensure(code == "404", $"HeadBucket on the deleted {Py.Repr(victim)} returned {Py.Repr(code)}, expected 404");
                break;
            }

            Ensure(
                clock.Elapsed.TotalSeconds < ResidualDeadlineS,
                $"{Py.Repr(victim)} still answers HeadBucket {ResidualDeadlineS}s after a successful DeleteBucket on an emptied bucket");
            await FidelityTime.Delay(TimeSpan.FromSeconds(3.0));
        }
    }

    /// <summary>
    /// Call fn(); return Succeeded when it succeeds, else the botocore Error.Code. The inverse of the
    /// error helper and deliberately separate: success is the expected outcome here.
    /// </summary>
    private static async Task<string> Absent(Func<Task> fn) =>
        await FidelityContext.CaptureError(fn) is { } error ? S3Fidelity.Botocore(error).Code ?? "ClientError-with-no-code" : Succeeded;

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
