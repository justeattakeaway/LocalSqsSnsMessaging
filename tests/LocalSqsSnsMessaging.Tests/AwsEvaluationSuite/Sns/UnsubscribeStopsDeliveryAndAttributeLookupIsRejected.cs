// Translated from behavioral/sns/check_unsubscribe_stops_delivery_and_attribute_lookup_is_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.unsubscribe-stops-delivery-and-attribute-lookup-is-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class UnsubscribeStopsDeliveryAndAttributeLookupIsRejected
{
    private const string Scenario = "sns-unsubscribe";

    /// <summary>
    /// After Unsubscribe, GetSubscriptionAttributes on that ARN is rejected with NotFound and the
    /// queue receives nothing further, while a second subscription on the same topic keeps receiving
    /// every publish.
    /// </summary>
    [Test]
    [DisplayName("sns.unsubscribe-stops-delivery-and-attribute-lookup-is-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsErrorMessages, "GetSubscriptionAttributes on a removed subscription is refused with NotFound 'Subscription not found.' instead of 'Subscription does not exist'")]
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

        var baseline = FidelityContext.UniqueName("sim2real-behav-sns-baseline-");
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = baseline });
        var aBaseline = await DrainUntilAll(ctx, aUrl, [baseline], timeoutS: 90.0);
        var bBaseline = await DrainUntilAll(ctx, bUrl, [baseline], timeoutS: 90.0);
        Ensure(aBaseline.SequenceEqual([baseline]),
            $"queue A never received anything even before the unsubscribe: {Repr(aBaseline)}");
        Ensure(bBaseline.SequenceEqual([baseline]),
            $"queue B never received the baseline, so it cannot serve as the control: {Repr(bBaseline)}");

        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subA });
        var listed = await AwaitSubscriptionGone(ctx, topicArn, subA);
        var listedArns = listed.Select(s => s.SubscriptionArn).ToList();
        Ensure(!listedArns.Contains(subA),
            $"the removed subscription is still listed: {Repr(listedArns)}");
        Ensure(listedArns.Contains(subB),
            $"Unsubscribe removed the wrong subscription; '{subB}' is missing from " +
            $"{Repr(listedArns)}");

        var postMarkers = Enumerable.Range(0, 3).Select(i => FidelityContext.UniqueName($"sim2real-behav-sns-post-{i}-")).ToList();
        foreach (var marker in postMarkers)
        {
            await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = marker });
        }

        // B must receive all three before A is examined, so A's silence is evidence, not latency.
        var bAfter = await DrainUntilAll(ctx, bUrl, postMarkers, timeoutS: 120.0);
        Ensure(bAfter.Order(StringComparer.Ordinal).SequenceEqual(postMarkers.Order(StringComparer.Ordinal)),
            "the surviving subscription lost messages, so the absence below is not evidence: " +
            $"{Repr(bAfter)}");
        var aAfter = await ReceiveAny(ctx, aUrl, rounds: 2, waitS: 20);
        Ensure(aAfter.Count == 0, $"the unsubscribed queue kept receiving: {Repr(aAfter)}");

        var (code, message) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subA }));
        Ensure(code == "NotFound",
            $"GetSubscriptionAttributes on a removed subscription was not rejected: '{code}'");
        Ensure(message == "Subscription does not exist",
            "GetSubscriptionAttributes on a removed subscription carried the wrong message: " +
            $"'{message}'");
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    /// <summary>
    /// The AWS error (Code, Message) raised by fn(). Raises if fn SUCCEEDS — an unexpected success
    /// is the strongest possible signal.
    /// </summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {result}");
        }

        return (error.Code, error.Message ?? "");
    }

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
        ctx.OnCleanup(() => BestEffortUnsubscribe(ctx, subArn));
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

    /// <summary>Unsubscribe, tolerating NotFound for the subscription this check removes on purpose.</summary>
    private static async Task BestEffortUnsubscribe(FidelityContext ctx, string subArn)
    {
        try
        {
            await ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });
        }
        catch (AmazonServiceException err) when (err.ErrorCode == "NotFound")
        {
        }
    }

    /// <summary>Every subscription on the topic, paginated — page one is not the full set.</summary>
    private static async Task<List<Subscription>> ListSubscriptions(FidelityContext ctx, string topicArn)
    {
        var @out = new List<Subscription>();
        await foreach (var page in ctx.Sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            @out.AddRange(page.Subscriptions ?? []);
        }

        return @out;
    }

    /// <summary>
    /// Poll ListSubscriptionsByTopic until sub_arn is absent; returns the final listing.
    /// Delivery absence is only meaningful once the control plane agrees the subscription is gone.
    /// </summary>
    private static async Task<List<Subscription>> AwaitSubscriptionGone(FidelityContext ctx, string topicArn, string subArn, double timeoutS = 60.0)
    {
        var deadline = FidelityTime.StartNew();
        var listed = new List<Subscription>();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            listed = await ListSubscriptions(ctx, topicArn);
            if (!listed.Any(s => s.SubscriptionArn == subArn))
            {
                return listed;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"'{subArn}' was still listed on '{topicArn}' {timeoutS}s after Unsubscribe: " +
            $"{Repr(listed.Select(s => s.SubscriptionArn))}");
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
            throw Fail($"{Repr(outstanding.Order(StringComparer.Ordinal))} never arrived within {timeoutS}s; received {Repr(seen)}");
        }

        return seen;
    }

    /// <summary>Long-poll a queue `rounds` times and return every body it produced (expected: none).</summary>
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
}
