// Translated from behavioral/sns__sqs/check_sns_publish_batch_delivers_one_envelope_per_entry_to_the_subscribed_queue.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.sns-publish-batch-delivers-one-envelope-per-entry-to-the-subscribed-queue</summary>
[Category("AwsEvaluationSuite")]
public sealed class SnsPublishBatchDeliversOneEnvelopePerEntryToTheSubscribedQueue
{
    // Entry id -> (message, subject or None); bodies and subjects differ so no entry can stand in for another.
    private static readonly (string Id, string Message, string? Subject)[] Entries =
    [
        ("first", "one", "Subject one"),
        ("second", "{\"n\": 2, \"text\": \"zwei 二\"}", null),
        ("third", string.Concat(Enumerable.Repeat("drei-", 300)), "Dritter Betreff"),
    ];

    private const string XsvcPrefix = "sim2real-xsvc-";

    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// Bounded convergence for a delete to reach its terminal not-found answer (the default service
    /// class budget); a deadline, never an assertion on elapsed time.
    /// </summary>
    private const double GoneS = 40.0;

    private static readonly string[] SqsNf = ["AWS.SimpleQueueService.NonExistentQueue", "QueueDoesNotExist"];

    private const string SqsNfMsg = "The specified queue does not exist.";

    /// <summary>
    /// A three-entry PublishBatch reaches the subscribed queue as three Notification envelopes, each
    /// carrying the MessageId PublishBatch returned for its entry, that entry's exact Message, its
    /// Subject where one was given (none otherwise) and the TopicArn.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.sns-publish-batch-delivers-one-envelope-per-entry-to-the-subscribed-queue")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsErrorMessages, "GetSubscriptionAttributes on an unsubscribed ARN is refused NotFound 'Subscription not found.' instead of 'Subscription does not exist'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var topic = await CreateTopic(ctx);
        var queue = await CreateQueue(ctx);
        await AllowTopic(ctx, queue, topic.Arn);
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic.Arn, Protocol = "sqs", Endpoint = queue.Arn, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(Tolerant(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }), "NotFound"));

        var request = new List<PublishBatchRequestEntry>();
        foreach (var (entryId, message, subject) in Entries)
        {
            var entry = new PublishBatchRequestEntry { Id = entryId, Message = message };
            if (!string.IsNullOrEmpty(subject))
            {
                entry.Subject = subject;
            }

            request.Add(entry);
        }

        var resp = await sns.PublishBatchAsync(new PublishBatchRequest { TopicArn = topic.Arn, PublishBatchRequestEntries = request });
        var failed = resp.Failed ?? [];
        Ensure(failed.Count == 0,
            $"PublishBatch failed entries: [{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]");
        var ids = (resp.Successful ?? throw new KeyNotFoundException("Successful")).ToDictionary(e => e.Id, e => e.MessageId);
        var sortedIds = ids.Keys.Order(StringComparer.Ordinal).ToList();
        var sortedEntries = Entries.Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(sortedIds.SequenceEqual(sortedEntries),
            $"PublishBatch succeeded ids {List(sortedIds)}, expected {List(sortedEntries)}");
        Ensure(ids.Values.Distinct().Count() == ids.Count, $"PublishBatch returned repeated MessageIds: {Repr(ids)}");

        // Delivery is at least once: envelopes are deduplicated by their MessageId, and a stray is an
        // envelope whose MessageId PublishBatch never returned.
        var sqs = ctx.Sqs;
        var envelopes = new Dictionary<string, JsonObject>();
        var strays = new List<JsonNode?>();
        var deadline = FidelityTime.StartNew();
        var finalPoll = false;
        while (true)
        {
            var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queue.Url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in received.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body);
                var mid = body is JsonObject obj ? (string?)obj["MessageId"] : null;
                if (mid is not null && ids.ContainsValue(mid))
                {
                    envelopes.TryAdd(mid, body!.AsObject());
                }
                else
                {
                    strays.Add(body);
                }

                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queue.Url, ReceiptHandle = msg.ReceiptHandle });
            }

            if (finalPoll)
            {
                break;
            }

            if (envelopes.Keys.ToHashSet().SetEquals(ids.Values))
            {
                finalPoll = true; // one more receive catches a stray landing beside the last entry
            }
            else if (deadline.Elapsed >= TimeSpan.FromSeconds(40.0))
            {
                throw Fail($"{envelopes.Count} of {ids.Count} entries' envelopes arrived within 40s; strays {Head(StrayRepr(strays), 300)}");
            }
        }

        Ensure(strays.Count == 0, $"the queue received envelopes PublishBatch never returned: {Head(StrayRepr(strays), 300)}");
        foreach (var (entryId, message, subject) in Entries)
        {
            var env = envelopes[ids[entryId]];
            var got = ((string?)env["Type"], (string?)env["TopicArn"], (string?)env["Message"], (string?)env["Subject"]);
            var want = ((string?)"Notification", (string?)topic.Arn, (string?)message, subject);
            Ensure(got == want,
                $"entry {entryId}: envelope (Type, TopicArn, Message, Subject) {Tuple(got)}, expected {Tuple(want)}");
        }

        await GoneSubscription(ctx, subArn, topic.Arn);
        await GoneTopic(ctx, topic.Arn);
        await GoneQueue(ctx, queue.Name, queue.Url);
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Repr(Dictionary<string, string> d) =>
        "{" + string.Join(", ", d.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}";

    private static string StrayRepr(List<JsonNode?> strays) => "[" + string.Join(", ", strays.Select(s => s?.ToJsonString() ?? "None")) + "]";

    private static string Tuple((string?, string?, string?, string?) t) =>
        $"({PyStr(t.Item1)}, {PyStr(t.Item2)}, {PyStr(t.Item3)}, {PyStr(t.Item4)})";

    private static string PyStr(string? s) => s is null ? "None" : $"'{s}'";

    private static async Task AllowTopic(FidelityContext ctx, (string Name, string Url, string Arn) queue, string topicArn)
    {
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queue.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.Url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
    }

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Amazon.SimpleNotificationService.Model.Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new Amazon.SimpleNotificationService.Model.Tag { Key = kv.Key, Value = kv.Value })];

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<(string Name, string Url, string Arn)> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attrs = null)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var createAttrs = new Dictionary<string, string>(attrs ?? []);
        if (fifo)
        {
            createAttrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = createAttrs, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        ctx.OnCleanup(Tolerant(() => sqs.DeleteQueueAsync(url), SqsNf));
        return (name, url, arn);
    }

    /// <summary>Create an SNS topic (registers delete_topic teardown). Returns {name,arn}.</summary>
    private static async Task<(string Name, string Arn)> CreateTopic(FidelityContext ctx)
    {
        var name = XName("topic");
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = XsvcTagsKv() })).TopicArn;
        ctx.OnCleanup(Tolerant(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }), "NotFound"));
        return (name, arn);
    }

    /// <summary>(code, HTTP status, message) of a ClientError.</summary>
    private static (string Code, int Status, string Message) Err(AwsError e) =>
        (e.Code ?? "", (int)e.StatusCode, e.Message ?? "");

    /// <summary><c>fn()</c> must fail with one of <c>codes</c> in the HTTP class of <c>status</c>, with exactly <c>message</c>.</summary>
    private static async Task Refused(Func<Task> fn, string[] codes, int status, string what, string message)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null)
        {
            var (code, got, msg) = Err(error);
            var inClass = got / 100 == status / 100;
            Ensure(codes.Contains(code) && inClass,
                $"{what}: expected {string.Join("/", codes)} HTTP {status / 100}xx, got '{code}' HTTP {got}: '{Head(msg, 150)}'");
            Ensure(msg == message, $"{what}: {code} message is '{Head(msg, 200)}', expected '{message}'");
            return;
        }

        throw Fail($"{what}: expected {string.Join("/", codes)} HTTP {status}, the call succeeded");
    }

    /// <summary>
    /// Poll <c>fn</c> until it reaches the terminal not-found answer, then grade it with <c>Refused</c>.
    /// </summary>
    /// <remarks>A success while the delete converges is retried; any other error fails at once.</remarks>
    private static async Task PollRefused<T>(Func<Task<T>> fn, string[] codes, int status, string what, string message, double timeoutS = GoneS)
        where T : class
    {
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var (last, error) = await FidelityContext.Capture(fn);
            if (error is not null)
            {
                var (code, _, _) = Err(error);
                if (codes.Contains(code))
                {
                    await Refused(fn, codes, status, what, message);
                    return;
                }

                throw error.Exception;
            }

            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: still readable {timeoutS:F0}s after the delete; last answer {Head($"{last}", 300)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }
    }

    /// <summary>A teardown that ignores only the given not-found codes (the body already deleted it).</summary>
    private static Func<Task> Tolerant(Func<Task> fn, params string[] codes) => async () =>
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && !codes.Contains(Err(error).Code))
        {
            throw error.Exception;
        }
    };

    private static async Task GoneQueue(FidelityContext ctx, string name, string url)
    {
        var sqs = ctx.Sqs;
        await sqs.DeleteQueueAsync(url);
        // A deleted queue can keep answering for a while, so the read is polled.
        await PollRefused(() => sqs.GetQueueUrlAsync(name), SqsNf, 400, $"GetQueueUrl {name}", SqsNfMsg);
        var listed = (await sqs.ListQueuesAsync(new ListQueuesRequest { QueueNamePrefix = name })).QueueUrls ?? [];
        Ensure(!listed.Any(u => u.TrimEnd('/').Split('/')[^1] == name), $"ListQueues still lists deleted queue {name}");
        await Refused(() => sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = url, MessageBody = "gone" }),
            SqsNf, 400, $"SendMessage {name}", SqsNfMsg);
    }

    private static async Task GoneTopic(FidelityContext ctx, string arn)
    {
        var sns = ctx.Sns;
        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        string[] nf = ["NotFound"];
        await PollRefused(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }),
            nf, 404, "GetTopicAttributes", "Topic does not exist");
        var listed = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            foreach (var t in page.Topics ?? [])
            {
                listed.Add(t.TopicArn);
            }
        }

        Ensure(!listed.Contains(arn), "ListTopics still lists the deleted topic");
        await Refused(() => sns.PublishAsync(new PublishRequest { TopicArn = arn, Message = "gone" }),
            nf, 404, "Publish to deleted topic", "Topic does not exist");
    }

    private static async Task GoneSubscription(FidelityContext ctx, string subArn, string topicArn)
    {
        var sns = ctx.Sns;
        await sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn });
        string[] nf = ["NotFound"];
        await PollRefused(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }),
            nf, 404, "GetSubscriptionAttributes", "Subscription does not exist");
        var listed = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            foreach (var s in page.Subscriptions ?? [])
            {
                listed.Add(s.SubscriptionArn);
            }
        }

        Ensure(!listed.Contains(subArn), "ListSubscriptionsByTopic still lists the removed subscription");
        await Refused(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subArn, AttributeName = "RawMessageDelivery", AttributeValue = "true",
            }),
            nf, 404, "SetSubscriptionAttributes on removed subscription", "Subscription does not exist");
    }
}
