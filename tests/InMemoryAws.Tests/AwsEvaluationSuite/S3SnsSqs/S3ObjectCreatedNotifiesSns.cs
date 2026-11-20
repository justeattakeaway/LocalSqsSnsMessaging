// Translated from behavioral/s3__sns__sqs/check_s3_object_created_notifies_sns.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.S3SnsSqs;

/// <summary>s3__sns__sqs.s3-object-created-notifies-sns</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3ObjectCreatedNotifiesSns
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A PutObject to a bucket with an SNS TopicConfiguration delivers an ObjectCreated event,
    /// carrying the exact object key, to the topic and on to a subscribed SQS queue.
    /// </summary>
    [Test]
    [DisplayName("s3__sns__sqs.s3-object-created-notifies-sns")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var bucket = await CreateBucket(ctx);
        var topic = await CreateTopic(ctx);
        await TopicPublishableByS3(ctx, topic, bucket.Arn);
        var queue = await QueueSubscribedToTopic(ctx, topic);
        await ctx.S3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            TopicConfigurations = [new TopicConfiguration { Topic = topic.Arn, Events = ["s3:ObjectCreated:*"] }],
        });
        var key = $"probe/{FidelityContext.UniqueName("obj-")}.txt";
        await ctx.S3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = key, ContentBody = "payload" });
        var (record, envelope) = await WaitForObjectCreated(ctx, queue.Url, key);
        Ensure(
            ((string?)record["eventName"] ?? string.Empty).StartsWith("ObjectCreated", StringComparison.Ordinal),
            $"unexpected eventName {Py.Repr((string?)record["eventName"])}");
        Ensure(
            (string?)envelope["TopicArn"] == topic.Arn,
            $"expected envelope TopicArn {Py.Repr(topic.Arn)}, got {Py.Repr((string?)envelope["TopicArn"])}");
        var gotBucket = (string?)record["s3"]?["bucket"]?["name"];
        Ensure(gotBucket == bucket.Name, $"expected record bucket {Py.Repr(bucket.Name)}, got {Py.Repr(gotBucket)}");

        // Tear down in reverse dependency order and prove each resource gone.
        await S3SnsTeardown.GoneSubscription(ctx, queue.Sub, topic.Arn);
        await S3SnsTeardown.GoneTopic(ctx, topic.Arn);
        await S3SnsTeardown.GoneQueue(ctx, queue.Name, queue.Url);
        await S3SnsTeardown.GoneBucket(ctx, bucket.Name);
    }

    private sealed record Bucket(string Name, string Arn);

    private sealed record Topic(string Name, string Arn);

    private sealed record Queue(string Name, string Url, string Arn, string Sub = "");

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var name = XName("q");
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = await S3Fidelity.NotificationQueueArn(sqs, url);
        ctx.OnCleanup(S3SnsTeardown.Tolerant(() => sqs.DeleteQueueAsync(url), S3SnsTeardown.SqsNotFoundCodes));
        return new Queue(name, url, arn);
    }

    /// <summary>Create an SNS topic (registers delete_topic teardown). Returns {name,arn}.</summary>
    private static async Task<Topic> CreateTopic(FidelityContext ctx)
    {
        var name = XName("topic");
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags = [.. XsvcResourceTags().Select(t => new Amazon.SimpleNotificationService.Model.Tag { Key = t.Key, Value = t.Value })],
        })).TopicArn;
        ctx.OnCleanup(S3SnsTeardown.Tolerant(() => sns.DeleteTopicAsync(arn), "NotFound"));
        return new Topic(name, arn);
    }

    /// <summary>Create an S3 bucket (registers empty-then-delete teardown). Returns {name,arn}.</summary>
    private static async Task<Bucket> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        await FidelityContext.CaptureError(() => s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
        {
            BucketName = name,
            TagSet = [.. XsvcResourceTags().Select(t => new Amazon.S3.Model.Tag { Key = t.Key, Value = t.Value })],
        }));
        return new Bucket(name, $"arn:aws:s3:::{name}");
    }

    /// <summary>Attach an SNS topic policy allowing s3.amazonaws.com to publish for this bucket.</summary>
    private static Task TopicPublishableByS3(FidelityContext ctx, Topic topic, string bucketArn)
    {
        var account = topic.Arn.Split(':')[4];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "s3.amazonaws.com" },
                ["Action"] = "sns:Publish",
                ["Resource"] = topic.Arn,
                ["Condition"] = new JsonObject
                {
                    ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = bucketArn },
                    ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = account },
                },
            }),
        };
        return ctx.Sns.SetTopicAttributesAsync(new SetTopicAttributesRequest
        {
            TopicArn = topic.Arn,
            AttributeName = "Policy",
            AttributeValue = policy.ToJsonString(),
        });
    }

    /// <summary>Create an SQS queue subscribed to the topic (with the queue policy SNS needs). Returns queue.</summary>
    private static async Task<Queue> QueueSubscribedToTopic(FidelityContext ctx, Topic topic)
    {
        var queue = await CreateQueue(ctx);
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queue.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic.Arn } },
            }),
        };
        await ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queue.Url,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });
        var sub = (await ctx.Sns.SubscribeAsync(new SubscribeRequest { TopicArn = topic.Arn, Protocol = "sqs", Endpoint = queue.Arn })).SubscriptionArn;
        ctx.OnCleanup(S3SnsTeardown.Tolerant(() => ctx.Sns.UnsubscribeAsync(sub), "NotFound"));
        return queue with { Sub = sub };
    }

    /// <summary>
    /// Drain SNS-&gt;SQS messages until an S3 ObjectCreated event for `key` arrives. Returns
    /// (record, SNS envelope).
    /// </summary>
    /// <remarks>
    /// Skips the `s3:TestEvent` S3 emits when a notification is first configured (it has no Records)
    /// and any unrelated notification. S3 URL-encodes the object key in the event, so compare decoded.
    /// </remarks>
    private static async Task<(JsonNode Record, JsonObject Envelope)> WaitForObjectCreated(FidelityContext ctx, string queueUrl, string key, double timeoutS = 120)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        var seen = new List<string>();
        while (started.Elapsed.TotalSeconds < timeoutS)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                JsonObject envelope;
                JsonObject @event;
                try
                {
                    envelope = JsonNode.Parse(msg.Body)!.AsObject();
                    @event = JsonNode.Parse((string)envelope["Message"]!)!.AsObject();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentNullException or NullReferenceException)
                {
                    continue;
                }

                if ((string?)@event["Event"] == "s3:TestEvent" || !@event.ContainsKey("Records"))
                {
                    continue; // S3's config-time probe, not a real object event
                }

                foreach (var r in @event["Records"]!.AsArray())
                {
                    var evtKey = Uri.UnescapeDataString(((string?)r?["s3"]?["object"]?["key"] ?? string.Empty).Replace('+', ' '));
                    seen.Add(evtKey);
                    if (evtKey == key)
                    {
                        return (r!, envelope);
                    }
                }
            }
        }

        throw Fail($"S3 ObjectCreated event for {Py.Repr(key)} never delivered via SNS (saw {Py.Repr(seen)})");
    }
}
