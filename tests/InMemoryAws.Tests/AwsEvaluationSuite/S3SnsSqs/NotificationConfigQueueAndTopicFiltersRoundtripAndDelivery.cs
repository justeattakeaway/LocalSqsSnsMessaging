// Translated from behavioral/s3__sns__sqs/check_notification_config_queue_and_topic_filters_roundtrip_and_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsTag = Amazon.SimpleNotificationService.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3SnsSqs;

/// <summary>s3__sns__sqs.notification-config-queue-and-topic-filters-roundtrip-and-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class NotificationConfigQueueAndTopicFiltersRoundtripAndDelivery
{
    private const string Check = "notification-config-queue-and-topic-filters-roundtrip-and-delivery";

    /// <summary>
    /// A queue config and a topic config applied together keep separate, exact Ids, Events and
    /// FilterRules (an explicitly empty prefix kept as ''); the queue delivers only the key matching
    /// both its rules, withholds a key that breaks one, and clearing the config drops both.
    /// </summary>
    [Test]
    [DisplayName("s3__sns__sqs.notification-config-queue-and-topic-filters-roundtrip-and-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var sns = ctx.Sns;

        var bucket = await S3Fidelity.CreateTaggedBucket(ctx, "sim2real", Check, "sim2real-xsvc-bkt-");
        var (queueUrl, queueArn) = await NewQueue(ctx, sqs, bucket);
        var topicArn = await NewTopic(ctx, sns, bucket);

        const string queueId = "incoming-csv-to-queue";
        const string topicId = "removed-txt-to-topic";
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket,
            QueueConfigurations =
            [
                new QueueConfiguration
                {
                    Id = queueId,
                    Queue = queueArn,
                    Events = ["s3:ObjectCreated:*"],
                    Filter = new Filter
                    {
                        S3KeyFilter = new S3KeyFilter
                        {
                            FilterRules = [new FilterRule { Name = "prefix", Value = "incoming/" }, new FilterRule { Name = "suffix", Value = ".csv" }],
                        },
                    },
                },
            ],
            TopicConfigurations =
            [
                new TopicConfiguration
                {
                    Id = topicId,
                    Topic = topicArn,
                    Events = ["s3:ObjectRemoved:*"],
                    Filter = new Filter
                    {
                        S3KeyFilter = new S3KeyFilter
                        {
                            FilterRules = [new FilterRule { Name = "prefix", Value = string.Empty }, new FilterRule { Name = "suffix", Value = ".txt" }],
                        },
                    },
                },
            ],
        });

        var configured = await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket });
        var queues = configured.QueueConfigurations ?? [];
        var topics = configured.TopicConfigurations ?? [];
        Ensure(queues.Count == 1, $"expected exactly one QueueConfigurations entry, got {Py.Repr(queues.Select(c => c.Id).ToList())}");
        Ensure(topics.Count == 1, $"expected exactly one TopicConfigurations entry, got {Py.Repr(topics.Select(c => c.Id).ToList())}");
        var (q, t) = (queues[0], topics[0]);

        Ensure(q.Id == queueId, $"expected queue Id {Py.Repr(queueId)}, got {Py.Repr(q.Id)}");
        Ensure(q.Queue == queueArn, $"expected QueueArn {Py.Repr(queueArn)}, got {Py.Repr(q.Queue)}");
        var qEvents = (q.Events ?? []).Select(e => e.Value).ToList();
        Ensure(qEvents.SequenceEqual(["s3:ObjectCreated:*"]), $"expected queue Events ['s3:ObjectCreated:*'], got {Py.Repr(qEvents)}");
        var qRules = Rules(q.Filter);
        Ensure(
            Py.Equal(qRules, Expected(("Prefix", "incoming/"), ("Suffix", ".csv"))),
            $"expected queue FilterRules [Prefix='incoming/', Suffix='.csv'], got {Py.Repr(qRules)}");

        Ensure(t.Id == topicId, $"expected topic Id {Py.Repr(topicId)}, got {Py.Repr(t.Id)}");
        Ensure(t.Topic == topicArn, $"expected TopicArn {Py.Repr(topicArn)}, got {Py.Repr(t.Topic)}");
        var tEvents = (t.Events ?? []).Select(e => e.Value).ToList();
        Ensure(tEvents.SequenceEqual(["s3:ObjectRemoved:*"]), $"expected topic Events ['s3:ObjectRemoved:*'], got {Py.Repr(tEvents)}");
        var tRules = Rules(t.Filter);
        Ensure(
            Py.Equal(tRules, Expected(("Prefix", string.Empty), ("Suffix", ".txt"))),
            $"expected topic FilterRules [Prefix='', Suffix='.txt'] with the empty prefix kept, got {Py.Repr(tRules)}");

        // No wanted keys, so this returns at once; _drain_until skips the s3:TestEvent records.
        await DrainUntil(sqs, queueUrl, new HashSet<string>(StringComparer.Ordinal), budgetS: 20);

        // --- Delivered: a key matching both the queue's prefix and suffix.
        const string deliveredKey = "incoming/a.csv";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = deliveredKey, ContentBody = "a,b\n1,2\n" });
        var first = await DrainUntil(sqs, queueUrl, new HashSet<string>(StringComparer.Ordinal) { deliveredKey }, budgetS: 60);
        var matches = first.Where(r => r.Key == deliveredKey).ToList();
        Ensure(matches.Count == 1, $"expected exactly one delivery for {Py.Repr(deliveredKey)}, got {Py.Repr(first)}");
        var (configId, eventName, recBucket, _) = matches[0];
        Ensure(configId == queueId, $"expected configurationId {Py.Repr(queueId)}, got {Py.Repr(configId)}");
        Ensure(eventName == "ObjectCreated:Put", $"expected eventName 'ObjectCreated:Put', got {Py.Repr(eventName)}");
        Ensure(recBucket == bucket, $"expected record bucket {Py.Repr(bucket)}, got {Py.Repr(recBucket)}");

        // --- Withheld: a key under the same prefix but the wrong suffix, proven absent against two
        // later, known-good deliveries rather than a bare quiet window. If the withheld key had been
        // (incorrectly) sent, it was written before either control and would already be visible by
        // the time both controls, written after it, have arrived.
        const string withheldKey = "incoming/a.txt";
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = withheldKey, ContentBody = "not csv" });
        HashSet<string> controlKeys = ["incoming/b.csv", "incoming/c.csv"];
        foreach (var key in controlKeys.Order(StringComparer.Ordinal))
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "c,d\n3,4\n" });
        }

        var seen = await DrainUntil(sqs, queueUrl, controlKeys, budgetS: 60);
        var seenKeys = seen.Select(r => r.Key!).ToHashSet(StringComparer.Ordinal);
        Ensure(
            controlKeys.IsSubsetOf(seenKeys),
            $"the later positive controls {Py.Repr(controlKeys.Order(StringComparer.Ordinal).ToList())} never all arrived within budget; saw {Py.Repr(seenKeys.Order(StringComparer.Ordinal).ToList())}");
        Ensure(!seenKeys.Contains(withheldKey), $"{Py.Repr(withheldKey)} does not match the queue's .csv suffix filter but was delivered: {Py.Repr(seen)}");

        // --- Clear: an empty NotificationConfiguration drops every configuration key.
        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest { BucketName = bucket });
        var cleared = await s3.GetBucketNotificationAsync(new GetBucketNotificationRequest { BucketName = bucket });
        var configKeys = new List<string>();
        if (cleared.QueueConfigurations is { Count: > 0 })
        {
            configKeys.Add("QueueConfigurations");
        }

        if (cleared.TopicConfigurations is { Count: > 0 })
        {
            configKeys.Add("TopicConfigurations");
        }

        if (cleared.LambdaFunctionConfigurations is { Count: > 0 })
        {
            configKeys.Add("LambdaFunctionConfigurations");
        }

        if (cleared.EventBridgeConfiguration is not null)
        {
            configKeys.Add("EventBridgeConfiguration");
        }

        Ensure(configKeys.Count == 0, $"expected no configuration keys after clearing, got {Py.Repr(configKeys)}");
    }

    /// <summary><c>config["Filter"]["Key"]["FilterRules"]</c>, as boto3's list of dicts.</summary>
    private static List<object?> Rules(Filter? filter) =>
        [.. (filter?.S3KeyFilter?.FilterRules ?? []).Select(r => (object?)new Dictionary<string, object?> { ["Name"] = r.Name, ["Value"] = r.Value ?? string.Empty })];

    private static List<object?> Expected(params (string Name, string Value)[] rules) =>
        [.. rules.Select(r => (object?)new Dictionary<string, object?> { ["Name"] = r.Name, ["Value"] = r.Value })];

    /// <summary><c>_new_queue(ctx, sqs, bucket)</c>: a queue S3 may send to for the bucket, tagged; its URL and ARN.</summary>
    private static async Task<(string Url, string Arn)> NewQueue(FidelityContext ctx, IAmazonSQS sqs, string bucket)
    {
        var queueUrl = (await sqs.CreateQueueAsync(FidelityContext.UniqueName("sim2real-xsvc-q-"))).QueueUrl;
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteQueue(sqs, queueUrl));
        await sqs.TagQueueAsync(new TagQueueRequest { QueueUrl = queueUrl, Tags = new Dictionary<string, string> { ["sim2real"] = Check } });
        var queueArn = await S3Fidelity.NotificationQueueArn(sqs, queueUrl);
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new Dictionary<string, string> { ["Policy"] = S3Fidelity.NotificationAllowS3Policy(queueArn, bucket) },
        });
        return (queueUrl, queueArn);
    }

    /// <summary><c>_new_topic(ctx, sns, bucket)</c>: a tagged topic S3 may publish to for the bucket.</summary>
    private static async Task<string> NewTopic(FidelityContext ctx, IAmazonSimpleNotificationService sns, string bucket)
    {
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-xsvc-t-"),
            Tags = [new SnsTag { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => S3Fidelity.NotificationDeleteTopic(sns, topicArn));
        await sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topicArn,
            AttributeName = "Policy",
            AttributeValue = S3Fidelity.NotificationAllowS3Policy(topicArn, bucket, "SNS:Publish"),
        });
        return topicArn;
    }

    /// <summary>
    /// <c>_drain_until(sqs, queue_url, want_keys, budget_s=...)</c>: receives and deletes every real
    /// (non <c>s3:TestEvent</c>) S3 record for up to <paramref name="budgetS"/>, stopping once every
    /// wanted key has been seen; every <c>(configurationId, eventName, bucket, key)</c> observed.
    /// </summary>
    private static async Task<List<(string? ConfigurationId, string? EventName, string? Bucket, string? Key)>> DrainUntil(
        IAmazonSQS sqs, string queueUrl, HashSet<string> wantKeys, double budgetS)
    {
        var records = new List<(string?, string?, string?, string?)>();
        var started = FidelityTime.StartNew();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        while (started.Elapsed.TotalSeconds < budgetS && !wantKeys.IsSubsetOf(seenKeys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                var body = JsonNode.Parse(msg.Body)!;
                if ((string?)body["Event"] == "s3:TestEvent")
                {
                    continue;
                }

                foreach (var rec in body["Records"]?.AsArray() ?? [])
                {
                    var key = S3Fidelity.UnquotePlus((string)rec!["s3"]!["object"]!["key"]!);
                    records.Add(((string?)rec["s3"]!["configurationId"], (string?)rec["eventName"], (string?)rec["s3"]!["bucket"]!["name"], key));
                    seenKeys.Add(key);
                }
            }
        }

        return records;
    }
}
