// Translated from behavioral/s3/check_conc_s3_conditional_writes_optimistic_counter_loses_no_increment.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text;
using Amazon.Runtime;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.conc-s3-conditional-writes-optimistic-counter-loses-no-increment</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcS3ConditionalWritesOptimisticCounterLosesNoIncrement
{
    private const int Workers = 4;
    private const int IncrementsEach = 5;
    private const int MaxAttemptsPerIncrement = 200;
    private static readonly HashSet<string> LostRace = new(StringComparer.Ordinal) { "PreconditionFailed", "ConditionalRequestConflict" };

    /// <summary>
    /// Concurrent GET-then-PutObject(If-Match) increments lose no update: the values the workers wrote
    /// are exactly 1..N with no duplicate, and the final object reads N with the matching ETag.
    /// </summary>
    [Test]
    [DisplayName("s3.conc-s3-conditional-writes-optimistic-counter-loses-no-increment")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var workerClient = ctx.S3Client(maxAttempts: 1);
        var bucket = await S3Fidelity.CreateCondwBucket(ctx);
        const string key = "counters/jobs-processed";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "0" });

        async Task<List<int>> Worker(int w)
        {
            var wrote = new List<int>();
            for (var n = 0; n < IncrementsEach; n++)
            {
                var landed = false;
                for (var attempt = 0; attempt < MaxAttemptsPerIncrement; attempt++)
                {
                    using var got = await workerClient.GetObjectAsync(bucket, key);
                    var value = int.Parse(Encoding.UTF8.GetString(await S3Fidelity.ReadBody(got)), CultureInfo.InvariantCulture);
                    try
                    {
                        await workerClient.PutObjectAsync(new PutObjectRequest
                        {
                            BucketName = bucket,
                            Key = key,
                            ContentBody = (value + 1).ToString(CultureInfo.InvariantCulture),
                            IfMatch = got.ETag,
                        });
                    }
                    catch (AmazonServiceException ex) when (LostRace.Contains(AwsError.From(ex).Code ?? string.Empty))
                    {
                        continue;
                    }

                    wrote.Add(value + 1);
                    landed = true;
                    break;
                }

                Ensure(landed, $"worker {w} could not land an increment in {MaxAttemptsPerIncrement} attempts; the If-Match loop never converges");
            }

            return wrote;
        }

        List<int>[] perWorker;
        using (ctx.Concurrently())
        {
            perWorker = await Task.WhenAll(Enumerable.Range(0, Workers).Select(w => Task.Run(() => Worker(w))));
        }

        const int total = Workers * IncrementsEach;
        var written = perWorker.SelectMany(v => v).Order().ToList();
        Ensure(written.Count == total, $"workers reported {written.Count} successful increments, want {total}: {Py.Repr(perWorker.Select(Py.Repr).ToList())}");
        Ensure(
            written.SequenceEqual(Enumerable.Range(1, total)),
            $"successful If-Match writes produced values {Py.Repr(written)}; want exactly 1..{total} once each: a duplicate means two workers both won the same compare-and-swap");
        foreach (var vals in perWorker)
        {
            Ensure(vals.SequenceEqual(vals.Order()), $"one worker's own successful writes went backwards {Py.Repr(vals)}: a write was accepted against an ETag older than one it had already superseded");
        }

        using var final = await s3.GetObjectAsync(bucket, key);
        var body = await S3Fidelity.ReadBody(final);
        Ensure(Encoding.UTF8.GetString(body) == total.ToString(CultureInfo.InvariantCulture), $"the counter reads {Py.Repr(body)}; want {total} (lost update)");
        var wantEtag = S3Fidelity.Md5ETag(body);
        Ensure(final.ETag == wantEtag, $"final ETag {Py.Repr(final.ETag)} != MD5 {Py.Repr(wantEtag)}");
    }
}
