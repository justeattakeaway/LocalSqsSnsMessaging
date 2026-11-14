// Translated from behavioral/sqs/check_fifo_content_based_dedup_ignores_attributes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sqs;

/// <summary>sqs.fifo-content-based-dedup-ignores-attributes</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoContentBasedDedupIgnoresAttributes
{
    /// <summary>
    /// Content-based FIFO dedup hashes the body only: resending the same body with different
    /// MessageAttributes is deduplicated and the first send's attributes are retained (first write
    /// wins), while a different body is not deduplicated.
    /// </summary>
    [Test]
    [DisplayName("sqs.fifo-content-based-dedup-ignores-attributes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));

        var token = Guid.NewGuid().ToString("N")[..8];
        var group = $"grp-{token}";
        var body = $"dedup-body-{token}";

        var first = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = body,
            MessageGroupId = group,
            MessageAttributes = new() { ["Attempt"] = new() { DataType = "String", StringValue = "one" } },
        });
        var second = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = body,
            MessageGroupId = group,
            // Break exactly one thing relative to the first send: the attributes, not the body.
            MessageAttributes = new()
            {
                ["Attempt"] = new() { DataType = "String", StringValue = "two" },
                ["Extra"] = new() { DataType = "Number", StringValue = "42" },
            },
        });

        // The two requests genuinely differed -- otherwise this check proves nothing.
        var md51 = first.MD5OfMessageAttributes;
        var md52 = second.MD5OfMessageAttributes;
        Ensure(!string.IsNullOrEmpty(md51) && !string.IsNullOrEmpty(md52),
            $"SendMessage did not echo MD5OfMessageAttributes ({md51 ?? "None"}, {md52 ?? "None"}); " +
            "cannot establish that the two requests differed");
        Ensure(md51 != md52,
            $"both sends hashed their attributes to {md51} -- the attribute sets were not " +
            "actually different, so dedup behaviour is untested");
        Ensure(first.MD5OfMessageBody == second.MD5OfMessageBody,
            "the two sends must carry the same body digest");

        // --- dedup: same MessageId echoed, so the second send was discarded ---------------
        Ensure(second.MessageId == first.MessageId,
            "content-based dedup broken: attribute-varied re-send minted a new MessageId " +
            $"{second.MessageId} instead of replaying {first.MessageId}");
        Ensure(Key(second.SequenceNumber, "SequenceNumber") == Key(first.SequenceNumber, "SequenceNumber"),
            $"deduplicated re-send returned SequenceNumber {second.SequenceNumber}, " +
            $"expected the original {first.SequenceNumber}");

        // --- effect absent: exactly one message exists, carrying the FIRST attributes ------
        var got = await ReceiveAll(sqs, url, expect: 2, timeoutSeconds: 30);
        Ensure(got.Count == 1,
            $"expected 1 message after a deduplicated re-send, received {got.Count}: " +
            $"[{string.Join(", ", got.Select(m => m.MessageId))}]");
        var msg = got[0];
        Ensure(msg.MessageId == first.MessageId,
            $"delivered MessageId {msg.MessageId} != {first.MessageId}");
        var attrs = msg.MessageAttributes ?? [];
        Ensure(!attrs.ContainsKey("Extra"),
            $"the discarded duplicate's attributes leaked onto the retained message: [{string.Join(", ", attrs.Keys.Order(StringComparer.Ordinal))}]");
        Ensure(attrs.GetValueOrDefault("Attempt")?.StringValue == "one",
            "expected the first send's Attempt=one to be retained, got " +
            $"{attrs.GetValueOrDefault("Attempt")?.StringValue ?? "None"}");

        // --- control: a DIFFERENT body is not deduplicated --------------------------------
        var otherBody = $"{body}-distinct";
        var third = await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = otherBody,
            MessageGroupId = group,
            MessageAttributes = new() { ["Attempt"] = new() { DataType = "String", StringValue = "one" } },
        });
        Ensure(third.MessageId != first.MessageId,
            "a different body must enqueue as a new message, but the MessageId was replayed");
        var control = await ReceiveAll(sqs, url, expect: 1, timeoutSeconds: 30);
        Ensure(control.Select(m => m.Body).SequenceEqual([otherBody]),
            $"control send did not enqueue: received [{string.Join(", ", control.Select(m => m.Body))}]");
    }

    /// <summary>Receive up to `expect` distinct messages within timeout_s, preserving arrival order.</summary>
    private static async Task<List<Message>> ReceiveAll(AmazonSQSClient sqs, string url, int expect, double timeoutSeconds, string[]? names = null)
    {
        names ??= ["All"];
        var seen = new HashSet<string>();
        var msgs = new List<Message>();
        var deadline = FidelityTime.StartNew();
        while (msgs.Count < expect && deadline.Elapsed.TotalSeconds < timeoutSeconds)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageAttributeNames = [.. names],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                if (seen.Add(msg.MessageId))
                {
                    msgs.Add(msg);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return msgs;
    }

    /// <summary>Python's <c>resp["Key"]</c>: a <see cref="KeyNotFoundException"/> when the response omitted it.</summary>
    private static T Key<T>(T? value, string name)
        where T : class
        => value ?? throw new KeyNotFoundException(name);
}
