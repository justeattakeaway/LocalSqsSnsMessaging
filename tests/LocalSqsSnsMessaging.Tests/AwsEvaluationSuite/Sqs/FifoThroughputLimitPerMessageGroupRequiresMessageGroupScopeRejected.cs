// Translated from behavioral/sqs/check_fifo_throughput_limit_per_message_group_requires_message_group_scope_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Diagnostics;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-throughput-limit-per-message-group-requires-message-group-scope-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoThroughputLimitPerMessageGroupRequiresMessageGroupScopeRejected
{
    private static readonly Dictionary<string, string> Tags = new()
    {
        ["sim2real:managed"] = "true",
        ["sim2real"] = "fifo-ht-combination",
    };

    private const string ComboMessage =
        "Invalid value for the parameter FifoThroughputLimit. Reason: To set FifoThroughputLimit to " +
        "perMessageGroupId, the DeduplicationScope must be messageGroup.";

    private static readonly Dictionary<string, string> Ht = new() { ["DeduplicationScope"] = "messageGroup", ["FifoThroughputLimit"] = "perMessageGroupId" };
    private static readonly Dictionary<string, string> Normal = new() { ["DeduplicationScope"] = "queue", ["FifoThroughputLimit"] = "perQueue" };
    // Bound on how long the scope change may take to govern deduplication.
    private const double ConvergeS = 90.0;

    /// <summary>
    /// FifoThroughputLimit=perMessageGroupId requires DeduplicationScope=messageGroup.
    ///
    /// Illegal pairings are refused and change nothing; changing both together moves dedup queue-wide.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-throughput-limit-per-message-group-requires-message-group-scope-rejected")]
    [Timeout(FidelityContext.BudgetMilliseconds)]
    [KnownFidelityGap(FidelityGap.Fifo, "expected the call to be refused, but it succeeded: CreateQueue with FifoThroughputLimit=perMessageGroupId and DeduplicationScope=queue")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var token = Guid.NewGuid().ToString("N")[..8];

        var badName = FidelityContext.UniqueName("sim2real-behav-") + "-bad.fifo";
        var (code, msg) = await Error(() => sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = badName, Tags = new(Tags), Attributes = new()
            {
                ["FifoQueue"] = "true", ["DeduplicationScope"] = "queue",
                ["FifoThroughputLimit"] = "perMessageGroupId",
            },
        }));
        Ensure((code, msg) == ("InvalidAttributeValue", ComboMessage),
            $"illegal CreateQueue gave {(code, msg)}");
        (code, msg) = await Error(() => sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = badName }));
        Ensure((code, msg) == ("AWS.SimpleQueueService.NonExistentQueue",
                "The specified queue does not exist."),
            $"the refused CreateQueue left a resolvable queue behind: GetQueueUrl gave {(code, msg)}");

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo", Tags = new(Tags),
            Attributes = new Dictionary<string, string>(Ht) { ["FifoQueue"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var htRead = await HtAttrs(sqs, url);
        Ensure(DictEquals(htRead, Ht), $"high-throughput queue read back {Repr(await HtAttrs(sqs, url))}");

        (code, msg) = await Error(() => sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url, Attributes = new() { ["DeduplicationScope"] = "queue" },
        }));
        Ensure((code, msg) == ("InvalidAttributeValue", ComboMessage),
            $"scope-only downgrade gave {(code, msg)}");
        var stored = await HtAttrs(sqs, url);
        Ensure(DictEquals(stored, Ht), $"the refused update changed the stored attributes: {Repr(stored)}");
        Ensure(!await SameIdTwoGroups(sqs, url, $"kept-{token}"),
            "after the refused update a repeated dedup id in a second group was collapsed; " +
            "deduplication must still be per message group");

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new(Normal) });
        stored = await HtAttrs(sqs, url);
        Ensure(DictEquals(stored, Normal), $"the accepted downgrade read back {Repr(stored)}");
        string? collapsed = null;
        var deadline = Stopwatch.StartNew();
        var n = 0;
        while (collapsed is null && deadline.Elapsed.TotalSeconds < ConvergeS)
        {
            var dedupId = $"queue-wide-{n}-{token}";
            n++;
            if (await SameIdTwoGroups(sqs, url, dedupId))
            {
                collapsed = dedupId;
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        Ensure(collapsed is not null,
            $"{ConvergeS:F0}s after DeduplicationScope=queue a repeated dedup id in a second " +
            $"group was still delivered separately ({n} attempts)");

        List<string> bodies = [];
        var empties = 0;
        deadline = Stopwatch.StartNew();
        while (empties < 3 && deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var batch = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2,
            })).Messages ?? [];
            empties = batch.Count > 0 ? 0 : empties + 1;
            foreach (var m in batch)
            {
                bodies.Add(m.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(bodies.Contains($"g1-kept-{token}") && bodies.Contains($"g2-kept-{token}"),
            "per-group dedup kept both sends of the repeated id, so both must be delivered: " +
            $"[{string.Join(", ", bodies.Order(StringComparer.Ordinal))}]");
        Ensure(bodies.Contains($"g1-{collapsed}") && !bodies.Contains($"g2-{collapsed}"),
            $"queue-wide dedup must deliver the first send of '{collapsed}' only: [{string.Join(", ", bodies.Order(StringComparer.Ordinal))}]");
    }

    /// <summary>(code, message) of the refusal ``fn`` raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? throw new KeyNotFoundException("Code"), error.Message);
    }

    private static async Task<Dictionary<string, string>> HtAttrs(AmazonSQSClient sqs, string url) =>
        (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["DeduplicationScope", "FifoThroughputLimit"],
        })).Attributes!;

    /// <summary>Send one dedup id to two groups; True when the second send was deduplicated.</summary>
    private static async Task<bool> SameIdTwoGroups(AmazonSQSClient sqs, string url, string dedupId)
    {
        var a = (await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = $"g1-{dedupId}", MessageGroupId = "g1",
            MessageDeduplicationId = dedupId,
        })).MessageId;
        var b = (await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url, MessageBody = $"g2-{dedupId}", MessageGroupId = "g2",
            MessageDeduplicationId = dedupId,
        })).MessageId;
        return a == b;
    }

    private static bool DictEquals(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static string Repr(Dictionary<string, string> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";
}
