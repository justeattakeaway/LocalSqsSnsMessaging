// Translated from behavioral/sqs/check_send_message_batch_reserved_attribute_name_prefix_fails_alone.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.send-message-batch-reserved-attribute-name-prefix-fails-alone</summary>
[Category("AwsEvaluationSuite")]
public sealed class SendMessageBatchReservedAttributeNamePrefixFailsAlone
{
    private const string Check = "send-message-batch-reserved-attribute-name-prefix-fails-alone";

    private const string ReservedMessage =
        "You can't use message attribute names beginning with 'AWS.' or 'Amazon'. These strings are " +
        "reserved for internal use.";

    /// <summary>
    /// In SendMessageBatch, an entry whose message attribute name begins with the reserved 'AWS.'
    /// prefix fails alone (SenderFault, exact message) while a sibling entry with a legal
    /// <c>color.*</c>-namespaced attribute name is enqueued with its value intact.
    /// </summary>
    [Test]
    [DisplayName("sqs.send-message-batch-reserved-attribute-name-prefix-fails-alone")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sqs-"), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var resp = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new SendMessageBatchRequestEntry
                {
                    Id = "legal", MessageBody = "batch-reserved-prefix-legal",
                    MessageAttributes = new() { ["color.primary"] = new MessageAttributeValue { DataType = "String", StringValue = "red" } },
                },
                new SendMessageBatchRequestEntry
                {
                    Id = "reserved", MessageBody = "batch-reserved-prefix-refused",
                    MessageAttributes = new() { ["AWS.Internal"] = new MessageAttributeValue { DataType = "String", StringValue = "x" } },
                },
            ],
        });
        var successfulIds = (resp.Successful ?? []).Select(e => e.Id).ToList();
        Ensure(successfulIds.SequenceEqual(["legal"]),
            $"only the 'legal' entry may succeed, got Successful=[{string.Join(", ", successfulIds)}]");
        var failedEntries = resp.Failed ?? [];
        Ensure(failedEntries.Select(e => e.Id).SequenceEqual(["reserved"]),
            $"the 'reserved' entry must fail alone, got Failed=[{string.Join(", ", failedEntries.Select(e => $"({e.Id}, {e.Code}, {e.Message})"))}]");
        var failedEntry = failedEntries.Single();
        var code = failedEntry.Code ?? throw new KeyNotFoundException("Code");
        var message = failedEntry.Message ?? throw new KeyNotFoundException("Message");
        Ensure((code, message) == ("InvalidParameterValue", ReservedMessage),
            $"the 'reserved' entry failed with ('{code}', '{message}'), expected ('InvalidParameterValue', '{ReservedMessage}')");
        Ensure(failedEntry.SenderFault == true,
            $"the reserved-prefix failure must be SenderFault, got {failedEntry.SenderFault}");

        var seen = await PollUntilBoth(sqs, url, ["batch-reserved-prefix-legal"], timeoutSeconds: 30.0);
        var gotAttrs = seen["batch-reserved-prefix-legal"].MessageAttributes ?? [];
        Ensure(gotAttrs.Keys.ToHashSet().SetEquals(["color.primary"]),
            $"the enqueued message carried attributes [{string.Join(", ", gotAttrs.Keys.Order(StringComparer.Ordinal))}], expected only 'color.primary'");
        Ensure(gotAttrs["color.primary"].StringValue == "red",
            $"'color.primary' carried '{gotAttrs["color.primary"].StringValue}', expected 'red'");

        // The refused entry must never have been enqueued: drain any remaining traffic and confirm.
        var leftover = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
        Ensure(!leftover.Any(m => m.Body == "batch-reserved-prefix-refused"),
            "the refused entry was enqueued despite being reported as Failed");
    }

    /// <summary>Receive (and delete) until every body in <paramref name="wantBodies"/> has been seen; body -> message.</summary>
    private static async Task<Dictionary<string, Message>> PollUntilBoth(AmazonSQSClient sqs, string url, HashSet<string> wantBodies, double timeoutSeconds)
    {
        var seen = new Dictionary<string, Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutSeconds) && !wantBodies.IsSubsetOf(seen.Keys))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2, MessageAttributeNames = ["All"],
            })).Messages ?? [];
            foreach (var msg in msgs)
            {
                seen[msg.Body] = msg;
                await sqs.DeleteMessageAsync(url, msg.ReceiptHandle);
            }
        }

        var missing = wantBodies.Except(seen.Keys).Order(StringComparer.Ordinal).ToList();
        Ensure(missing.Count == 0,
            $"within {timeoutSeconds}s the queue never delivered [{string.Join(", ", missing)}]; saw [{string.Join(", ", seen.Keys.Order(StringComparer.Ordinal))}]");
        return seen;
    }
}
