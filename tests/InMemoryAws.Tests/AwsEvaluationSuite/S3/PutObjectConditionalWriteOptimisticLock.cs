// Translated from behavioral/s3/check_put_object_conditional_write_optimistic_lock.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.put-object-conditional-write-optimistic-lock</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutObjectConditionalWriteOptimisticLock
{
    /// <summary>
    /// Two concurrent PutObject calls with If-Match on the same original ETag produce exactly one winner,
    /// whose body becomes current; the loser gets PreconditionFailed with its exact message, and a later
    /// write on the winner's ETag succeeds.
    /// </summary>
    [Test]
    [DisplayName("s3.put-object-conditional-write-optimistic-lock")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-condwrite-");

        const string key = "contended-object.txt";
        var initialEtag = (await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "initial-content" })).ETag;

        async Task<(bool Ok, string? Code, string? Message, string Payload)> Racer(string payload)
        {
            var error = await FidelityContext.CaptureError(() => s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = payload,
                IfMatch = initialEtag,
            }));
            return (error is null, error?.Code, error?.Message, payload);
        }

        const string payloadA = "racer-a-wins-maybe";
        const string payloadB = "racer-b-wins-maybe";
        (bool Ok, string? Code, string? Message, string Payload)[] results;
        using (ctx.Concurrently())
        {
            results = await PoolMap(2, [payloadA, payloadB], Racer);
        }

        var winners = results.Where(r => r.Ok).ToList();
        var losers = results.Where(r => !r.Ok).ToList();
        var all = Py.Repr(results.Select(r => $"{{ok: {r.Ok}, code: {r.Code}, payload: {r.Payload}}}").ToList());
        Ensure(winners.Count == 1, $"expected exactly one winner, got {winners.Count}: {all}");
        Ensure(losers.Count == 1, $"expected exactly one loser, got {losers.Count}: {all}");
        Ensure(losers[0].Code == "PreconditionFailed", $"loser should fail with PreconditionFailed, got {losers[0].Code}");
        const string want = "At least one of the pre-conditions you specified did not hold";
        Ensure(losers[0].Message == want, $"loser should say {Py.Repr(want)}, got {Py.Repr(losers[0].Message)}");

        // the object's final content is exactly the winner's payload, not a merge/overwrite artifact
        var finalBody = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            finalBody.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(winners[0].Payload)),
            $"expected final body to be the winner's payload {Py.Repr(winners[0].Payload)}, got {Py.Repr(finalBody)}");

        // positive control: a fresh conditional write against the NEW current ETag succeeds
        var newEtag = (await s3.GetObjectMetadataAsync(bucket, key)).ETag;
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "final-write", IfMatch = newEtag });
        var postControlBody = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            postControlBody.AsSpan().SequenceEqual("final-write"u8),
            $"expected positive-control conditional write against the fresh ETag to land, got {Py.Repr(postControlBody)}");
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
