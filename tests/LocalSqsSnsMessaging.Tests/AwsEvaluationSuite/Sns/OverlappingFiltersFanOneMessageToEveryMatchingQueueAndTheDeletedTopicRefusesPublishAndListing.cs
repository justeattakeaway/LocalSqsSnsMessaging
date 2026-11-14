// Translated from behavioral/sns/check_overlapping_filters_fan_one_message_to_every_matching_queue_and_the_deleted_topic_refuses_publish_and_listing.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.overlapping-filters-fan-one-message-to-every-matching-queue-and-the-deleted-topic-refuses-publish-and-listing</summary>
[Category("AwsEvaluationSuite")]
public sealed class OverlappingFiltersFanOneMessageToEveryMatchingQueueAndTheDeletedTopicRefusesPublishAndListing
{
    private const string TopicGone = "Topic does not exist";

    // Filter per queue, and the attributes of each published message.
    private static readonly (string Key, string? Policy)[] Filters =
    [
        ("control", null),
        ("a", """{"team": ["a", "shared"]}"""),
        ("b", """{"team": ["b", "shared"]}"""),
        ("c", """{"priority": [{"numeric": [">", 5]}]}"""),
    ];

    private static readonly (string Label, string Team, int Priority)[] Messages =
    [
        ("only-a", "a", 1),
        ("all-three", "shared", 9),
        ("only-c", "z", 7),
        ("none", "z", 5),
    ];

    private static readonly (string Key, string[] Labels)[] Expected =
    [
        ("control", Messages.Select(m => m.Label).ToArray()),
        ("a", ["only-a", "all-three"]),
        ("b", ["all-three"]),
        ("c", ["all-three", "only-c"]),
    ];

    /// <summary>
    /// Three subscriptions with overlapping string and numeric filters each receive exactly the
    /// messages their policy matches (one reaches all three, one none, an unfiltered twin gets all); DeleteTopic
    /// then refuses Publish and ListSubscriptionsByTopic with NotFound and drops the topic from ListTopics.
    /// </summary>
    [Test]
    [DisplayName("sns.overlapping-filters-fan-one-message-to-every-matching-queue-and-the-deleted-topic-refuses-publish-and-listing")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = FidelityContext.UniqueName("sim2real-fanout-topic-"), Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => DeleteTopicIfPresent(ctx, topicArn));

        var queues = new Dictionary<string, (string Url, string Arn)>();
        var subs = new Dictionary<string, string>();
        foreach (var (key, policy) in Filters)
        {
            queues[key] = await CreateQueueWithSnsPolicy(ctx, topicArn);
            var attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" };
            if (policy is not null)
            {
                attributes["FilterPolicy"] = policy;
            }

            subs[key] = (await sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topicArn,
                Protocol = "sqs",
                Endpoint = queues[key].Arn,
                Attributes = attributes,
                ReturnSubscriptionArn = true,
            })).SubscriptionArn;
            var arn = subs[key];
            ctx.OnCleanup(() => UnsubscribeIfPresent(ctx, arn));
        }

        var markers = Messages.ToDictionary(m => m.Label, m => FidelityContext.UniqueName($"sim2real-{m.Label}-"));
        foreach (var (label, team, priority) in Messages)
        {
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = markers[label],
                MessageAttributes = new()
                {
                    ["team"] = new SnsMessageAttributeValue { DataType = "String", StringValue = team },
                    ["priority"] = new SnsMessageAttributeValue { DataType = "Number", StringValue = priority.ToString(CultureInfo.InvariantCulture) },
                },
            });
        }

        // The unfiltered control queue is drained first: once it holds all four, every message has been
        // dispatched, so each filtered queue is drained to its expected set and once more for strays.
        foreach (var (key, wantLabels) in Expected)
        {
            var want = wantLabels.Select(label => markers[label]).ToHashSet();
            var bodies = await DrainUntil(ctx, queues[key].Url, want, timeoutS: 40.0);
            bodies.AddRange(await DrainOnce(ctx, queues[key].Url));
            var got = markers.Where(kv => bodies.Contains(kv.Value)).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
            Ensure(got.SequenceEqual(wantLabels.Order(StringComparer.Ordinal)),
                $"queue {key} received {Repr(got)}, expected {Repr(wantLabels.Order(StringComparer.Ordinal))}");
            // Markers are unique per publish, so a repeated delivery is the same marker; anything else
            // is a stray.
            Ensure(bodies.ToHashSet().SetEquals(wantLabels.Select(label => markers[label])),
                $"queue {key} received {Repr(bodies.Distinct().Order(StringComparer.Ordinal))}, expected only its matching markers");
        }

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn });
        var attrsErr = await PollError(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = topicArn }), "NotFound");
        AssertNotFound(attrsErr, "GetTopicAttributes on the deleted topic", TopicGone);
        await PollUntil(
            () => ListTopicArns(sns),
            arns => !arns.Contains(topicArn),
            timeoutS: 40.0,
            description: "the deleted topic leaving ListTopics");
        var listingErr = await PollError(
            () => sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }), "NotFound");
        AssertNotFound(listingErr, "ListSubscriptionsByTopic on the deleted topic", TopicGone);
        var publishErr = await Error(
            () => sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = "after-delete" }),
            "NotFound",
            "Publish on the deleted topic");
        AssertNotFound(publishErr, "Publish on the deleted topic", TopicGone);
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = "sns-overlap-fanout" }, new() { Key = "sim2real:managed", Value = "true" }];

    private static async Task<(string Url, string Arn)> CreateQueueWithSnsPolicy(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-fanout-q-"),
            Tags = new() { ["sim2real"] = "sns-overlap-fanout", ["sim2real:managed"] = "true" },
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

    /// <summary>Receive (and delete) until every marker in `wanted` is among the bodies; raise on timeout.</summary>
    private static async Task<List<string>> DrainUntil(FidelityContext ctx, string queueUrl, HashSet<string> wanted, double timeoutS)
    {
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            bodies.AddRange(await DrainOnce(ctx, queueUrl));
            if (wanted.IsSubsetOf(bodies))
            {
                return bodies;
            }
        }

        throw Fail($"{Repr(wanted.Except(bodies).Order(StringComparer.Ordinal))} never arrived at {queueUrl} within {timeoutS}s; got {Repr(bodies)}");
    }

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, string description, double intervalS = 2.0)
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not observed within {timeoutS}s; last saw {(last is IEnumerable<string> e ? Repr(e) : $"'{last}'")}");
    }

    /// <summary>The error response of one call failing with code; any other outcome fails.</summary>
    private static async Task<AwsError> Error(Func<Task> fn, string code, string what)
    {
        var err = await FidelityContext.CaptureError(fn);
        if (err is not null)
        {
            Ensure(err.Code == code, $"{what}: expected {code}, got '{err.Code}'");
            return err;
        }

        throw Fail($"{what}: expected {code}, the call succeeded");
    }

    /// <summary>Poll fn until it fails with `code`; return the error response. Raise on timeout.</summary>
    private static async Task<AwsError> PollError(Func<Task> fn, string code, double timeoutS = 40.0, double intervalS = 2.0)
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
                last = err.Code ?? "None";
                if (last == code)
                {
                    return err;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"expected {code}, got '{last}' after {timeoutS}s");
    }

    private static void AssertNotFound(AwsError response, string what, string message)
    {
        var status = (int)response.StatusCode;
        Ensure(status == 404, $"{what} returned HTTP {status}, expected 404");
        var got = response.Message;
        Ensure(got == message, $"{what} returned message '{got}', expected '{message}'");
    }

    private static async Task<HashSet<string>> ListTopicArns(AmazonSimpleNotificationServiceClient sns)
    {
        var arns = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            arns.UnionWith((page.Topics ?? []).Select(t => t.TopicArn));
        }

        return arns;
    }

    /// <summary>Teardown: delete the topic; only NotFound is tolerated, every other error raises.</summary>
    private static async Task DeleteTopicIfPresent(FidelityContext ctx, string topicArn)
    {
        try
        {
            await ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn });
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "NotFound")
        {
        }
    }

    /// <summary>Teardown: remove the subscription; only NotFound is tolerated.</summary>
    private static async Task UnsubscribeIfPresent(FidelityContext ctx, string subArn)
    {
        try
        {
            await ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });
        }
        catch (AmazonServiceException exc) when (exc.ErrorCode == "NotFound")
        {
        }
    }
}
