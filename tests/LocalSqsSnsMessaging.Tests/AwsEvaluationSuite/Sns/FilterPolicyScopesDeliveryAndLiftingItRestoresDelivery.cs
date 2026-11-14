// Translated from behavioral/sns/check_filter_policy_scopes_delivery_and_lifting_it_restores_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.filter-policy-scopes-delivery-and-lifting-it-restores-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class FilterPolicyScopesDeliveryAndLiftingItRestoresDelivery
{
    private const string Scenario = "sns-filter-policy";

    /// <summary>
    /// A publish reaches only the subscription whose filter policy matches: the non-matching queue
    /// is empty once all four matching deliveries landed on the other, and widening its policy makes
    /// the same queue receive the next publish.
    /// </summary>
    [Test]
    [DisplayName("sns.filter-policy-scopes-delivery-and-lifting-it-restores-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (aUrl, aArn) = await CreateQueueForTopic(ctx, topicArn);
        var (bUrl, bArn) = await CreateQueueForTopic(ctx, topicArn);

        var subA = await SubscribeAndConfirm(ctx, topicArn, aArn,
            attributes: new() { ["RawMessageDelivery"] = "true" });
        var subB = await SubscribeAndConfirm(ctx, topicArn, bArn,
            attributes: new() { ["RawMessageDelivery"] = "true" });
        await SetFilterPolicy(ctx, subA, new JsonObject { ["kind"] = new JsonArray("a") });
        await SetFilterPolicy(ctx, subB, new JsonObject { ["kind"] = new JsonArray("b") });

        var sentMarkers = Enumerable.Range(0, 4).Select(i => FidelityContext.UniqueName($"sim2real-behav-sns-kind-a-{i}-")).ToList();
        foreach (var marker in sentMarkers)
        {
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = marker,
                MessageAttributes = new() { ["kind"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "a" } },
            });
        }

        // The barrier: every one of the four matching deliveries must land before the non-matching
        // queue is examined. Had the filter let them through, they would have taken the same trip.
        var aMarkers = await DrainUntilAll(ctx, aUrl, sentMarkers, timeoutS: 120.0);
        Ensure(aMarkers.Count == 4, $"the barrier did not actually complete: {Repr(aMarkers)}");
        Ensure(aMarkers.Order(StringComparer.Ordinal).SequenceEqual(sentMarkers.Order(StringComparer.Ordinal)),
            $"the matching subscription lost messages: {Repr(sentMarkers.Except(aMarkers).Order(StringComparer.Ordinal))}");

        var bBefore = await ReceiveAny(ctx, bUrl, rounds: 2, waitS: 20);
        Ensure(bBefore.Count == 0,
            $"the non-matching subscription received a filtered-out message: {Repr(bBefore)}");

        // Repair exactly the one broken thing and require the same queue to receive.
        await SetFilterPolicy(ctx, subB, new JsonObject { ["kind"] = new JsonArray("a") });
        var repairedMarker = FidelityContext.UniqueName("sim2real-behav-sns-repaired-");
        var bAfter = await PublishUntilDelivered(
            ctx, topicArn, bUrl, repairedMarker,
            attributes: new() { ["kind"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "a" } });
        Ensure(bAfter.Contains(repairedMarker),
            $"after the filter policy was widened the subscription still receives nothing: {Repr(bAfter)}");
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
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

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        var deadline = FidelityTime.StartNew();
        string? pending = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                return subArn;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"subscription '{subArn}' was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>Set a FilterPolicy and poll until GetSubscriptionAttributes reads the same document back.</summary>
    private static async Task SetFilterPolicy(FidelityContext ctx, string subArn, JsonNode policy, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subArn,
            AttributeName = "FilterPolicy",
            AttributeValue = policy.ToJsonString(),
        });
        var deadline = FidelityTime.StartNew();
        JsonNode? got = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var raw = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("FilterPolicy");
            got = !string.IsNullOrEmpty(raw) ? JsonNode.Parse(raw) : null;
            if (JsonNode.DeepEquals(got, policy))
            {
                return;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"FilterPolicy on '{subArn}' read back as {got?.ToJsonString() ?? "None"}, not {policy.ToJsonString()}, after {timeoutS}s");
    }

    /// <summary>Long-poll the queue until every wanted marker has been seen; returns the markers collected.</summary>
    private static async Task<List<string>> DrainUntilAll(FidelityContext ctx, string queueUrl, List<string> wanted, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var outstanding = wanted.ToHashSet();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS) && outstanding.Count > 0)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                if (outstanding.Remove(body))
                {
                    seen.Add(body);
                }
            }
        }

        if (outstanding.Count > 0)
        {
            throw Fail($"the matching subscription never received {Repr(outstanding.Order(StringComparer.Ordinal))} within {timeoutS}s; " +
                $"it received {Repr(seen)}");
        }

        return seen;
    }

    /// <summary>Long-poll a queue `rounds` times and return every body it produced (usually none).</summary>
    private static async Task<List<string>> ReceiveAny(FidelityContext ctx, string queueUrl, int rounds, int waitS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        for (var i = 0; i < rounds; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = waitS });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body ?? "");
            }
        }

        return bodies;
    }

    /// <summary>
    /// Publish the same marker up to `attempts` times, draining the queue between attempts.
    ///
    /// A SetSubscriptionAttributes(FilterPolicy) update is eventually consistent, so the bounded
    /// republish is the retry that makes the repaired-path assertion correct rather than racy; the
    /// marker itself is constant, so the assertion stays exact.
    /// </summary>
    private static async Task<List<string>> PublishUntilDelivered(FidelityContext ctx, string topicArn, string queueUrl, string marker,
        Dictionary<string, SnsMessageAttributeValue> attributes, int attempts = 5)
    {
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        for (var i = 0; i < attempts; i++)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker, MessageAttributes = attributes });
            var deadline = FidelityTime.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20.0))
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                    bodies.Add(msg.Body ?? "");
                }

                if (bodies.Contains(marker))
                {
                    return bodies;
                }
            }
        }

        return bodies;
    }
}
