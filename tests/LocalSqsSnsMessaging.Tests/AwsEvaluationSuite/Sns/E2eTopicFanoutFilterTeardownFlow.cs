// Translated from behavioral/sns/check_e2e_topic_fanout_filter_teardown_flow.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.e2e-topic-fanout-filter-teardown-flow</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eTopicFanoutFilterTeardownFlow
{
    /// <summary>
    /// Two differently-filtered subscriptions each receive only their matching message; after
    /// unsubscribe and DeleteTopic both read NotFound by ARN and leave their listings, and Publish
    /// to the deleted topic is refused NotFound while a fresh topic still accepts Publish.
    /// </summary>
    [Test]
    [DisplayName("sns.e2e-topic-fanout-filter-teardown-flow")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;

        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-fanout-topic-"),
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => BestEffortDeleteTopic(ctx, topicArn));

        var queueA = await CreateQueueWithSnsPolicy(ctx, topicArn);
        var queueB = await CreateQueueWithSnsPolicy(ctx, topicArn);

        var subA = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueA.Arn })).SubscriptionArn;
        var subB = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueB.Arn })).SubscriptionArn;
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subA, AttributeName = "FilterPolicy", AttributeValue = """{"team": ["a"]}""",
        });
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subB, AttributeName = "FilterPolicy", AttributeValue = """{"team": ["b"]}""",
        });
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest { SubscriptionArn = subA, AttributeName = "RawMessageDelivery", AttributeValue = "true" });
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest { SubscriptionArn = subB, AttributeName = "RawMessageDelivery", AttributeValue = "true" });

        var markerA = FidelityContext.UniqueName("sim2real-marker-a-");
        var markerB = FidelityContext.UniqueName("sim2real-marker-b-");
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = markerA,
            MessageAttributes = new() { ["team"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "a" } },
        });
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = markerB,
            MessageAttributes = new() { ["team"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "b" } },
        });

        // Each queue is drained until its own marker arrives, then once more, so a stray copy of the
        // other marker that arrives alongside it is caught; the twin queue is the delivery control.
        var bodiesA = await DrainUntil(ctx, queueA.Url, markerA, timeoutS: 120.0);
        var bodiesB = await DrainUntil(ctx, queueB.Url, markerB, timeoutS: 120.0);
        bodiesA.AddRange(await DrainOnce(ctx, queueA.Url));
        bodiesB.AddRange(await DrainOnce(ctx, queueB.Url));
        Ensure(bodiesA.Contains(markerA), $"queue A missing its matching message: {Show(bodiesA)}");
        Ensure(!bodiesA.Contains(markerB), $"queue A received the other filter's message: {Show(bodiesA)}");
        Ensure(bodiesB.Contains(markerB), $"queue B missing its matching message: {Show(bodiesB)}");
        Ensure(!bodiesB.Contains(markerA), $"queue B received the other filter's message: {Show(bodiesB)}");

        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subA });
        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subB });

        var subErr = await PollError(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subA }), "NotFound");
        AssertNotFound(subErr, $"GetSubscriptionAttributes on unsubscribed {subA}");
        await PollUntil(async () => (await ListSubs(sns, topicArn)).Select(s => s.SubscriptionArn).ToHashSet(),
            arns => !arns.Contains(subA) && !arns.Contains(subB), timeoutS: 120.0,
            description: "the unsubscribed subscriptions leaving ListSubscriptionsByTopic");

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn });

        var attrsErr = await PollError(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }), "NotFound");
        AssertNotFound(attrsErr, $"GetTopicAttributes on deleted topic {topicArn}");

        await PollUntil(() => ListTopicArns(sns), arns => !arns.Contains(topicArn),
            timeoutS: 120.0, description: $"deleted topic {topicArn} leaving ListTopics");

        var publishErr = await PollError(() => sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "after-delete" }), "NotFound");
        AssertNotFound(publishErr, $"Publish on deleted topic {topicArn}");

        var otherArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-fanout-control-"),
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = otherArn }));
        var resp = await sns.PublishAsync(new PublishRequest { TopicArn = otherArn, Message = "control-ok" });
        Ensure(resp.MessageId is not null, $"control publish to a live topic returned no MessageId: {resp}");
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = "sns-fanout-teardown" }, new() { Key = "sim2real:managed", Value = "true" }];

    private static async Task<(string Url, string Arn)> CreateQueueWithSnsPolicy(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-fanout-q-"),
            Tags = new() { ["sim2real"] = "sns-fanout-teardown", ["sim2real:managed"] = "true" },
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

    private static async Task<List<string>> DrainOnce(FidelityContext ctx, string queueUrl)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
        foreach (var msg in resp.Messages ?? [])
        {
            bodies.Add(msg.Body);
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
        }

        return bodies;
    }

    /// <summary>Receive (and delete) until marker is among the bodies; raise on timeout.</summary>
    private static async Task<List<string>> DrainUntil(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            bodies.AddRange(await DrainOnce(ctx, queueUrl));
            if (bodies.Contains(marker))
            {
                return bodies;
            }
        }

        throw Fail($"'{marker}' never arrived at {queueUrl} within {timeoutS}s; got {Show(bodies)}");
    }

    private static async Task<HashSet<string>> PollUntil(Func<Task<HashSet<string>>> probe, Func<HashSet<string>, bool> predicate,
        double timeoutS, string description, double intervalS = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        HashSet<string>? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not observed within {timeoutS}s; last saw {(last is null ? "None" : "{" + string.Join(", ", last.Select(v => $"'{v}'")) + "}")}");
    }

    /// <summary>Poll fn until it fails with code; return the error response. Raise on timeout.</summary>
    private static async Task<AwsError> PollError(Func<Task> fn, string code, double timeoutS = 120.0, double intervalS = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        var last = "<call succeeded>";
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var err = await FidelityContext.CaptureError(fn);
            if (err is null)
            {
                last = "<call succeeded>";
            }
            else
            {
                last = err.Code ?? "";
                if (last == code)
                {
                    return err;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"expected {code}, got '{last}' after {timeoutS}s");
    }

    private static void AssertNotFound(AwsError response, string what)
    {
        var status = (int)response.StatusCode;
        Ensure(status is >= 400 and < 500, $"{what} returned HTTP {status}, expected 4xx");
        Ensure(!string.IsNullOrEmpty(response.Message), $"{what} returned an empty NotFound message");
    }

    private static async Task<HashSet<string>> ListTopicArns(AmazonSimpleNotificationServiceClient sns)
    {
        var arns = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            foreach (var t in page.Topics ?? [])
            {
                arns.Add(t.TopicArn);
            }
        }

        return arns;
    }

    private static async Task<List<Subscription>> ListSubs(AmazonSimpleNotificationServiceClient sns, string topicArn)
    {
        var subs = new List<Subscription>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            subs.AddRange(page.Subscriptions ?? []);
        }

        return subs;
    }

    /// <summary>Teardown: delete the topic; only NotFound is tolerated, every other error raises.</summary>
    private static async Task BestEffortDeleteTopic(FidelityContext ctx, string topicArn)
    {
        var sns = ctx.Sns;
        var error = await FidelityContext.CaptureError(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));
        if (error is not null && error.Code != "NotFound")
        {
            throw error.Exception;
        }
    }
}
