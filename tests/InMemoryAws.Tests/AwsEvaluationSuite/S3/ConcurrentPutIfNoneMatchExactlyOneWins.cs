// Translated from behavioral/s3/check_concurrent_put_if_none_match_exactly_one_wins.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.concurrent-put-if-none-match-exactly-one-wins</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentPutIfNoneMatchExactlyOneWins
{
    private const string Check = "concurrent-put-if-none-match-exactly-one-wins";

    /// <summary>
    /// N racing PutObject calls with IfNoneMatch='*' on the same new key: exactly one must succeed,
    /// every other racer must be rejected with PreconditionFailed (conditional-write contention).
    /// </summary>
    [Test]
    [DisplayName("s3.concurrent-put-if-none-match-exactly-one-wins")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var bucket = await S3Fidelity.CreateBucket(ctx, "sim2real-race-");
        await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest { BucketName = bucket, TagSet = [new Tag { Key = "simparity-check", Value = Check }] });

        const string key = "racer-object.txt";
        const int racers = 8;

        async Task<(string Kind, string? Value)> Attempt(int i)
        {
            var error = await FidelityContext.CaptureError(() => s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                ContentBody = $"racer-{i}",
                IfNoneMatch = "*",
            }));
            return error is null ? ("ok", i.ToString(System.Globalization.CultureInfo.InvariantCulture)) : ("err", error.Code);
        }

        (string Kind, string? Value)[] results;
        using (ctx.Concurrently())
        {
            results = await PoolMap(racers, [.. Enumerable.Range(0, racers)], Attempt);
        }

        var successes = results.Where(r => r.Kind == "ok").ToList();
        var failures = results.Where(r => r.Kind == "err").ToList();
        var all = Py.Repr(results.Select(r => $"({r.Kind}, {r.Value})").ToList());

        Ensure(successes.Count == 1, $"expected exactly one winner, got {successes.Count}: {all}");
        Ensure(failures.Count == racers - 1, $"expected {racers - 1} losers, got {failures.Count}: {all}");
        Ensure(
            failures.All(f => f.Value == "PreconditionFailed"),
            $"expected all losers to fail with PreconditionFailed, got codes: {Py.Repr(failures.Select(f => f.Value).ToList())}");

        var winnerI = successes[0].Value;
        var expectedBody = Encoding.UTF8.GetBytes($"racer-{winnerI}");
        var finalBody = await S3Fidelity.GetBytes(s3, bucket, key);
        Ensure(
            finalBody.AsSpan().SequenceEqual(expectedBody),
            $"expected the stored object to be the winning racer's own body {Py.Repr(expectedBody)}, got {Py.Repr(finalBody)}");
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
