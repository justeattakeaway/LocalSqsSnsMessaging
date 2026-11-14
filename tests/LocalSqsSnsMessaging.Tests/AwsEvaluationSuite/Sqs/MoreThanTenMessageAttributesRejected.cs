// Translated from behavioral/sqs/check_more_than_ten_message_attributes_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.more-than-ten-message-attributes-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MoreThanTenMessageAttributesRejected
{
    private const string TooManyMessage = "Number of message attributes [11] exceeds the allowed maximum [10].";

    private static Dictionary<string, MessageAttributeValue> Attrs(int n)
        => Enumerable.Range(0, n).ToDictionary(i => $"attr{i:D2}", i => new MessageAttributeValue { DataType = "String", StringValue = $"value-{i}" });

    /// <summary>
    /// The botocore Error dict ``fn`` raises. An unexpected success raises too, naming what came
    /// back: an 11-attribute message that is accepted must not read as a code mismatch.
    /// </summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error ?? throw Fail($"expected the call to be refused, but it succeeded: {(result is SendMessageResponse r ? $"{{MD5OfMessageBody: {r.MD5OfMessageBody}, MessageId: {r.MessageId}}}" : result)}");
    }

    /// <summary>
    /// A message may carry at most 10 attributes: 10 round-trip intact, 11 are refused by
    /// SendMessage, and in SendMessageBatch the 11-attribute entry fails alone; the sibling lands.
    /// </summary>
    [Test]
    [DisplayName("sqs.more-than-ten-message-attributes-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-attrcap-") })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var ten = Attrs(10);
        await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "ten-attributes", MessageAttributes = ten });

        var err = await Error(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "eleven-single", MessageAttributes = Attrs(11) }));
        Ensure(err.Code == "InvalidParameterValue", $"SendMessage with 11 message attributes gave code '{err.Code}'");
        Ensure(err.Message == TooManyMessage, $"expected message '{TooManyMessage}', got '{err.Message}'");

        var batch = await sqs.SendMessageBatchAsync(new SendMessageBatchRequest
        {
            QueueUrl = url,
            Entries =
            [
                new() { Id = "ok", MessageBody = "batch-sibling" },
                new() { Id = "bad", MessageBody = "eleven-batch", MessageAttributes = Attrs(11) },
            ],
        });
        var okIds = (batch.Successful ?? []).Select(e => e.Id).ToList();
        var failed = (batch.Failed ?? []).ToDictionary(e => e.Id);
        Ensure(okIds.SequenceEqual(["ok"]) && failed.Keys.Order(StringComparer.Ordinal).SequenceEqual(["bad"]),
            $"expected entry 'ok' to succeed and 'bad' to fail; Successful ids [{string.Join(", ", okIds)}], " +
            $"Failed [{string.Join(", ", (batch.Failed ?? []).Select(e => $"{e.Id}: {e.Code}"))}]");
        var bad = failed["bad"];
        Ensure((bad.Code, bad.SenderFault, bad.Message) == ("InvalidParameterValue", true, TooManyMessage),
            $"the 11-attribute batch entry failed with {{Code: {bad.Code}, SenderFault: {bad.SenderFault}, Message: {bad.Message}}}");

        var got = new Dictionary<string, Dictionary<string, MessageAttributeValue>>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                MessageAttributeNames = ["All"],
            })).Messages ?? [];
            if (msgs.Count == 0 && got.Count >= 2)
            {
                break;
            }

            foreach (var m in msgs)
            {
                got[m.Body] = m.MessageAttributes ?? [];
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(got.Keys.Order(StringComparer.Ordinal).SequenceEqual(["batch-sibling", "ten-attributes"]),
            $"the queue must hold exactly the 10-attribute message and the batch sibling, none of the " +
            $"refused ones; received [{string.Join(", ", got.Keys.Order(StringComparer.Ordinal))}]");
        var values = got["ten-attributes"].ToDictionary(kv => kv.Key, kv => kv.Value.StringValue);
        var expectedValues = ten.ToDictionary(kv => kv.Key, kv => kv.Value.StringValue);
        Ensure(values.Count == expectedValues.Count && values.All(kv => expectedValues.TryGetValue(kv.Key, out var v) && v == kv.Value),
            $"the 10 attributes did not round-trip: received {{{string.Join(", ", values.Select(kv => $"{kv.Key}: {kv.Value}"))}}}");
    }
}
