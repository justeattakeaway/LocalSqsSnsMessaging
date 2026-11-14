// Translated from behavioral/sns/check_fifo_message_group_and_deduplication_ids_over_128_characters_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-message-group-and-deduplication-ids-over-128-characters-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoMessageGroupAndDeduplicationIdsOver128CharactersRejected
{
    private const string Check = "fifo-message-group-and-deduplication-ids-over-128-characters-rejected";

    /// <summary>
    /// On a FIFO topic, 128-character MessageGroupId and MessageDeduplicationId are accepted and
    /// delivered under the full group id; 129 in either is refused and never reaches the queue.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-message-group-and-deduplication-ids-over-128-characters-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish to a FIFO topic accepts a 129-character MessageGroupId instead of refusing it with InvalidParameter")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-fifolen-") + ".fifo",
            Attributes = new() { ["FifoTopic"] = "true" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-sns-fifolen-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var qArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Effect"] = "Allow", ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage", ["Resource"] = qArn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic } },
                    }),
                }.ToJsonString(),
            },
        });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic,
            Protocol = "sqs",
            Endpoint = qArn,
            ReturnSubscriptionArn = true,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        var stem = FidelityContext.UniqueName("g");
        string g128 = stem + new string('g', 128 - stem.Length), d128 = stem + new string('d', 128 - stem.Length);
        string g129 = g128 + "x", d129 = d128 + "x";

        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = "long-group",
            MessageGroupId = g129,
            MessageDeduplicationId = d128,
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: The MessageGroupId parameter can only include " +
                                   "alphanumeric and punctuation characters. 1 to 128 in length"),
            $"a 129-character MessageGroupId gave '{code}': '{message}'");
        (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = "long-dedup",
            MessageGroupId = g128,
            MessageDeduplicationId = d129,
        }));
        Ensure((code, message) == ("InvalidParameter", "Invalid parameter: The MessageDeduplicationId parameter can only include " +
                                   "alphanumeric and punctuation characters. 1 to 128 in length"),
            $"a 129-character MessageDeduplicationId gave '{code}': '{message}'");

        var body = FidelityContext.UniqueName("sim2real-fifolen-ok-");
        var seq = (await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = body,
            MessageGroupId = g128,
            MessageDeduplicationId = d128,
        })).SequenceNumber;
        Ensure(!string.IsNullOrEmpty(seq), "a publish with 128-character ids must be accepted and sequenced");

        var got = new List<(string Body, string? GroupId)>();
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(90);
        TimeSpan? tail = null;
        while (clock.Elapsed < deadline && (tail is null || clock.Elapsed < tail))
        {
            if (tail is null && got.Any(g => g.Body == body))
            {
                // Keep receiving briefly so a delivered refused message is also caught.
                tail = clock.Elapsed + TimeSpan.FromSeconds(6);
            }

            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 2,
                MessageSystemAttributeNames = ["MessageGroupId"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                got.Add((m.Body, m.Attributes?.GetValueOrDefault("MessageGroupId")));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            }
        }

        Ensure(got.Count == 1 && got[0] == (body, g128),
            "the queue must hold only the accepted message, under its full 128-character group id; " +
            $"got bodies [{string.Join(", ", got.Select(g => $"'{g.Body}'"))}], group id lengths [{string.Join(", ", got.Select(g => (g.GroupId ?? "").Length))}]");
    }

    /// <summary>(Code, Message) of the error fn raises; an unexpected success raises, naming it.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }
}
