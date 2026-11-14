// Translated from behavioral/sqs/check_concurrent_set_queue_attributes_last_writer_wins.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.concurrent-set-queue-attributes-last-writer-wins</summary>
[Category("AwsEvaluationSuite")]
public sealed class ConcurrentSetQueueAttributesLastWriterWins
{
    /// <summary>
    /// Concurrent SetQueueAttributes on VisibilityTimeout converges to one of the submitted values.
    ///
    /// All five calls complete, and the value read back is exactly one of them, as an integer string.
    /// </summary>
    [Test]
    [DisplayName("sqs.concurrent-set-queue-attributes-last-writer-wins")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-cset-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var candidateValues = new[] { 11, 22, 33, 44, 55 }.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToList();

        async Task<string> Set(string value)
        {
            await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["VisibilityTimeout"] = value } });
            return value;
        }

        var results = await Task.WhenAll(candidateValues.Select(v => Task.Run(() => Set(v))));
        Ensure(results.Length == candidateValues.Count,
            $"expected all {candidateValues.Count} concurrent SetQueueAttributes calls to " +
            $"complete, got {results.Length} results: [{string.Join(", ", results)}]");

        var final = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["VisibilityTimeout"] }))
            .Attributes!["VisibilityTimeout"];

        Ensure(candidateValues.Contains(final),
            $"final VisibilityTimeout '{final}' is not one of the racing writers' values " +
            $"[{string.Join(", ", candidateValues)}] -- indicates a corrupted/partial merge");
        Ensure(final.Length > 0 && final.All(char.IsDigit), $"final VisibilityTimeout '{final}' is not a clean integer string");
    }
}
