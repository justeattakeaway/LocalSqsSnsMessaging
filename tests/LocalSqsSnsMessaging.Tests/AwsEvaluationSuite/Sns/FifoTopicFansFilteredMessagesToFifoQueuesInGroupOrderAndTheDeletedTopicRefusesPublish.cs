// Translated from behavioral/sns/check_fifo_topic_fans_filtered_messages_to_fifo_queues_in_group_order_and_the_deleted_topic_refuses_publish.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-topic-fans-filtered-messages-to-fifo-queues-in-group-order-and-the-deleted-topic-refuses-publish</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicFansFilteredMessagesToFifoQueuesInGroupOrderAndTheDeletedTopicRefusesPublish
{
    private const string TopicGone = "Topic does not exist";
    private const string MessageGroup = "orders-1";

    // Publish order: team per message; "c" matches no filter.
    private static readonly string[] Teams = ["a", "b", "b", "a", "c", "a", "b"];

    private static readonly (string Key, JsonObject? Policy)[] Filters =
    [
        ("control", null),
        ("a", new JsonObject { ["team"] = new JsonArray("a") }),
        ("b", new JsonObject { ["team"] = new JsonArray("b") }),
    ];

    /// <summary>
    /// A FIFO topic's filtered FIFO-queue subscriptions each receive only their team's messages, in
    /// publish order and with the message group id, while an unfiltered twin receives all seven in order;
    /// after DeleteTopic, ListSubscriptionsByTopic and Publish on it are refused NotFound.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-topic-fans-filtered-messages-to-fifo-queues-in-group-order-and-the-deleted-topic-refuses-publish")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsErrorMessages, "ListSubscriptionsByTopic on a deleted topic is refused NotFound 'Topic not found.' instead of 'Topic does not exist'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-fanout-topic-") + ".fifo",
            Attributes = new() { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "false" },
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => DeleteTopicIfPresent(ctx, topicArn));

        var queues = new Dictionary<string, (string Url, string Arn)>();
        var subs = new Dictionary<string, string>();
        foreach (var (key, policy) in Filters)
        {
            queues[key] = await CreateFifoQueueWithSnsPolicy(ctx, topicArn);
            var attrs = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" };
            if (policy is not null)
            {
                attrs["FilterPolicy"] = policy.ToJsonString();
            }

            var subArn = (await sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queues[key].Arn,
                Attributes = attrs,
                ReturnSubscriptionArn = true,
            })).SubscriptionArn;
            subs[key] = subArn;
            ctx.OnCleanup(() => UnsubscribeIfPresent(ctx, subArn));
        }

        var prefix = FidelityContext.UniqueName("sim2real-ord-");
        var bodies = Teams.Select((team, n) => $"{prefix}-{n}-{team}").ToList();
        for (var n = 0; n < bodies.Count; n++)
        {
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = bodies[n],
                MessageGroupId = MessageGroup,
                MessageDeduplicationId = $"{prefix[^20..]}-{n}",
                MessageAttributes = new() { ["team"] = new SnsMessageAttributeValue { DataType = "String", StringValue = Teams[n] } },
            });
        }

        // The control queue holding all seven proves every message was dispatched before the filtered
        // queues are graded for exact contents.
        var want = new Dictionary<string, List<string>> { ["control"] = bodies };
        foreach (var key in new[] { "a", "b" })
        {
            want[key] = bodies.Where((_, i) => Teams[i] == key).ToList();
        }

        foreach (var key in new[] { "control", "a", "b" })
        {
            var got = await DrainInOrder(ctx, queues[key].Url, want[key].Count, timeoutS: 40.0);
            got.AddRange(await DrainInOrder(ctx, queues[key].Url, 0, timeoutS: 0.0));
            var gotBodies = got.Select(g => g.Body).ToList();
            Ensure(gotBodies.SequenceEqual(want[key]), $"queue {key} received {Repr(gotBodies)}, expected {Repr(want[key])}");
            var groups = got.Select(g => g.Group).ToHashSet();
            Ensure(groups.SetEquals([MessageGroup]),
                $"queue {key} messages carried MessageGroupId {{{string.Join(", ", groups.Select(g => g is null ? "None" : $"'{g}'"))}}}, expected '{MessageGroup}'");
        }

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn });
        var listingErr = await PollError(() => sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }), "NotFound");
        AssertNotFound(listingErr, "ListSubscriptionsByTopic on the deleted FIFO topic", TopicGone);
        var publishErr = await Error(
            () => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = "after-delete",
                MessageGroupId = MessageGroup,
                MessageDeduplicationId = "after-delete",
            }),
            "NotFound",
            "Publish on the deleted FIFO topic");
        AssertNotFound(publishErr, "Publish on the deleted FIFO topic", TopicGone);
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = "sns-fifo-fanout" }, new() { Key = "sim2real:managed", Value = "true" }];

    private static async Task<(string Url, string Arn)> CreateFifoQueueWithSnsPolicy(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-fanout-q-") + ".fifo",
            Attributes = new() { ["FifoQueue"] = "true" },
            Tags = new() { ["sim2real"] = "sns-fifo-fanout", ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>
    /// Receive (and delete) in arrival order until <c>want</c> messages arrived, then one more empty-or-not
    /// receive when <c>want</c> is 0; return (body, MessageGroupId) pairs. Raise on timeout.
    /// </summary>
    private static async Task<List<(string Body, string? Group)>> DrainInOrder(FidelityContext ctx, string queueUrl, int want, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var got = new List<(string Body, string? Group)>();
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageSystemAttributeNames = ["MessageGroupId"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                got.Add((msg.Body, msg.Attributes?.GetValueOrDefault("MessageGroupId")));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }

            if (want == 0 || got.Count >= want)
            {
                return got;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{got.Count} of {want} messages arrived at {queueUrl} within {timeoutS}s: " +
                    $"[{string.Join(", ", got.Select(g => $"('{g.Body}', {(g.Group is null ? "None" : $"'{g.Group}'")})"))}]");
            }
        }
    }

    /// <summary>The error response of one call failing with <c>code</c>; any other outcome fails.</summary>
    private static async Task<AwsError> Error<T>(Func<Task<T>> fn, string code, string what)
        where T : class
    {
        var (_, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"{what}: expected {code}, the call succeeded");
        }

        Ensure(error.Code == code, $"{what}: expected {code}, got '{error.Code}'");
        return error;
    }

    /// <summary>Poll fn until it fails with <c>code</c>; return the error response. Raise on timeout.</summary>
    private static async Task<AwsError> PollError<T>(Func<Task<T>> fn, string code, double timeoutS = 40.0, double intervalS = 2.0)
        where T : class
    {
        var deadline = FidelityTime.StartNew();
        var last = "<call succeeded>";
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var (_, error) = await FidelityContext.Capture(fn);
            if (error is null)
            {
                last = "<call succeeded>";
            }
            else
            {
                last = error.Code ?? "None";
                if (last == code)
                {
                    return error;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"expected {code}, got '{last}' after {timeoutS}s");
    }

    private static void AssertNotFound(AwsError response, string what, string message)
    {
        var status = response.StatusCode;
        Ensure(status == HttpStatusCode.NotFound, $"{what} returned HTTP {(int)status}, expected 404");
        var got = response.Message;
        Ensure(got == message, $"{what} returned message '{got}', expected '{message}'");
    }

    /// <summary>Teardown: delete the topic; only NotFound is tolerated, every other error raises.</summary>
    private static async Task DeleteTopicIfPresent(FidelityContext ctx, string topicArn)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        if (error is not null && error.Code != "NotFound")
        {
            throw error.Exception;
        }
    }

    /// <summary>Teardown: remove the subscription; only NotFound is tolerated.</summary>
    private static async Task UnsubscribeIfPresent(FidelityContext ctx, string subArn)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        if (error is not null && error.Code != "NotFound")
        {
            throw error.Exception;
        }
    }
}
