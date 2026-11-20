// Translated from behavioral/s3/check_concurrent_conditional_create_exactly_one_wins.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.Runtime;
using Amazon.S3.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3;

/// <summary>s3.concurrent-conditional-create-exactly-one-wins</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentConditionalCreateExactlyOneWins
{
    /// <summary>
    /// N racers PutObject with If-None-Match: * for the same new key: exactly one create
    /// succeeds, the rest get PreconditionFailed.
    /// </summary>
    [Test]
    [DisplayName("s3.concurrent-conditional-create-exactly-one-wins")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, "sim2real-behav-bkt-");
        const string key = "race-create.txt";
        const int n = 6;

        async Task<object> Attempt(int i)
        {
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = name,
                Key = key,
                InputStream = new MemoryStream(Encoding.UTF8.GetBytes($"writer-{i}")),
                IfNoneMatch = "*",
            });
            return i;
        }

        var results = await S3CoreFidelity.RunConcurrent(ctx, [.. Enumerable.Range(0, n).Select(i => (Func<Task<object>>)(() => Attempt(i)))]);
        var winners = results.Where(r => r is not Exception).ToList();
        var errors = S3CoreFidelity.ConcurrentErrorCodes(results);
        Ensure(winners.Count == 1, $"expected exactly 1 winner, got {winners.Count} (results={Py.Repr(results.Select(r => r.ToString()).ToList())})");
        Ensure(errors.Count == n - 1, $"expected {n - 1} losers, got {errors.Count}");
        Ensure(errors.All(c => c == "PreconditionFailed"), $"expected all losers to be PreconditionFailed, got {Py.Repr(errors)}");
        var messages = results.OfType<AmazonServiceException>().Select(e => AwsError.From(e).Message).ToList();
        const string want = "At least one of the pre-conditions you specified did not hold";
        Ensure(messages.SequenceEqual(Enumerable.Repeat(want, n - 1)), $"expected every loser to say {Py.Repr(want)}, got {Py.Repr(messages)}");
        var final = await S3Fidelity.GetBytes(s3, name, key);
        var expected = Encoding.UTF8.GetBytes($"writer-{winners[0]}");
        Ensure(
            final.AsSpan().SequenceEqual(expected),
            $"final object body must be exactly the winning racer's payload {Py.Repr(expected)}, got {Py.Repr(final)}");
    }
}
