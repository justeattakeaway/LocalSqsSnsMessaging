// Translated from behavioral/sqs/check_standard_queue_holds_more_than_one_thousand_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.standard-queue-holds-more-than-one-thousand-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class StandardQueueHoldsMoreThanOneThousandMessages
{
    private const int Count = 1001;
    private const double CounterCapSeconds = 120.0;
    private const double DrainCapSeconds = 120.0;

    /// <summary>
    /// A standard queue holds 1,001 messages: every batch entry succeeds and every body is received.
    ///
    /// ApproximateNumberOfMessages reaches 1001 before any receive.
    /// </summary>
    [Test]
    [DisplayName("sqs.standard-queue-holds-more-than-one-thousand-messages")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-volq-");
        ctx.OnCleanup(() => DropQueue(sqs, name));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = new() { ["VisibilityTimeout"] = "300" } })).QueueUrl;

        var bodies = Enumerable.Range(0, Count).Select(i => $"m-{i:D4}").ToList();
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        for (var @base = 0; @base < Count; @base += 10)
        {
            var chunk = bodies.Skip(@base).Take(10).ToList();
            var output = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [.. chunk.Select((b, j) => new SendMessageBatchRequestEntry { Id = $"e{j}", MessageBody = b })],
            });
            Ensure(output.Failed is null or { Count: 0 }, $"batch at {@base} reported failures {output.Failed?.Count}");
            Ensure((output.Successful ?? []).Count == chunk.Count,
                $"batch at {@base} acknowledged {(output.Successful ?? []).Count} of {chunk.Count}");
            messageIds.UnionWith(output.Successful!.Select(e => e.MessageId));
        }

        Ensure(messageIds.Count == Count, $"{Count} sends must return distinct MessageIds; got {messageIds.Count}");

        string? count = null;
        var deadline = FidelityTime.StartNew();
        var budget = BudgetSeconds(ctx, CounterCapSeconds);
        var reached = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(budget))
        {
            count = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages"],
            })).Attributes!["ApproximateNumberOfMessages"];
            if (int.Parse(count, CultureInfo.InvariantCulture) >= Count)
            {
                reached = true;
                break;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }

        if (!reached)
        {
            if (BudgetSpent(ctx))
            {
                throw new TimeoutException("waiting for ApproximateNumberOfMessages: the run budget ended first; not graded");
            }

            throw Fail($"ApproximateNumberOfMessages never reached {Count}; last read '{count}'");
        }

        var received = new HashSet<string>(StringComparer.Ordinal);
        deadline = FidelityTime.StartNew();
        budget = BudgetSeconds(ctx, DrainCapSeconds);
        while (received.Count < Count && deadline.Elapsed < TimeSpan.FromSeconds(budget))
        {
            var output = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
            var msgs = output.Messages ?? [];
            received.UnionWith(msgs.Select(m => m.Body));
        }

        if (received.Count < Count && BudgetSpent(ctx))
        {
            throw new TimeoutException("receiving every message: the run budget ended first; not graded");
        }

        var neverArrived = bodies.Except(received).ToList();
        Ensure(received.SetEquals(bodies),
            $"the queue must deliver all {Count} bodies; {neverArrived.Count} never arrived, " +
            $"e.g. [{string.Join(", ", neverArrived.Order(StringComparer.Ordinal).Take(3))}]");
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s.
    ///
    /// The poll then fails with its own message.
    /// </summary>
    private static double BudgetSeconds(FidelityContext ctx, double capSeconds)
        => Math.Max(1.0, Math.Min(capSeconds, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    /// <summary>The cleanup: delete the queue by name. A queue never made is fine.</summary>
    private static async Task DropQueue(AmazonSQSClient sqs, string name)
    {
        string url;
        try
        {
            url = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = name })).QueueUrl;
        }
        catch (QueueDoesNotExistException)
        {
            return;
        }

        await sqs.DeleteQueueAsync(url);
    }
}
