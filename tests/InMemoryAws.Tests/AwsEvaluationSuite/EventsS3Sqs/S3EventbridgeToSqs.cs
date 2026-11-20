// Translated from behavioral/events__s3__sqs/check_s3_eventbridge_to_sqs.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace InMemoryAws.Tests.AwsEvaluationSuite.EventsS3Sqs;

/// <summary>events__s3__sqs.s3-eventbridge-to-sqs</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3EventbridgeToSqs
{
    /// <summary>S3 EventBridge notifications route Object Created events to SQS via a rule.</summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-eventbridge-to-sqs")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var events = ctx.Events;
        var bucket = await CreateBucket(ctx);
        var queue = await CreateQueue(ctx);

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = bucket.Name,
            EventBridgeConfiguration = new EventBridgeConfiguration(),
        });

        var ruleName = FidelityContext.UniqueName("s3-eb-rule-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = new JsonObject
            {
                ["source"] = new JsonArray("aws.s3"),
                ["detail-type"] = new JsonArray("Object Created"),
                ["detail"] = new JsonObject { ["bucket"] = new JsonObject { ["name"] = new JsonArray(bucket.Name) } },
            }.ToJsonString(),
        });
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));
        var ruleArn = await RuleArn(ctx, ruleName);
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForEvents(queue.Arn, ruleArn));
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sqs-target", Arn = queue.Arn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sqs-target"] }));

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket.Name, Key = "via-eventbridge.txt", ContentBody = "data" });

        var messages = await PutUntilDelivered(ctx, bucket.Name, "via-eventbridge.txt", queue.Url, timeoutS: 300);
        Ensure(messages.Count > 0, "expected S3->EventBridge event in SQS queue");
        var detail = messages[0];
        Ensure((string?)detail?["source"] == "aws.s3", $"expected source aws.s3, got {Py.Repr((string?)detail?["source"])}");
        Ensure(
            (string?)detail?["detail"]?["bucket"]?["name"] == bucket.Name,
            $"expected bucket {Py.Repr(bucket.Name)} in event detail, got {Py.Repr((string?)detail?["detail"]?["bucket"]?["name"])}");
        Ensure(
            (string?)detail?["detail"]?["object"]?["key"] == "via-eventbridge.txt",
            $"expected object key 'via-eventbridge.txt', got {Py.Repr((string?)detail?["detail"]?["object"]?["key"])}");
    }

    /// <summary>
    /// Re-put an object until its S3-&gt;EventBridge notification reaches the queue. Enabling
    /// EventBridgeConfiguration on a bucket is eventually consistent: AWS documents that it can take
    /// ~5 minutes before events actually start flowing. A single put right after enabling is therefore
    /// racy, so we keep writing new object versions until a notification lands (or the deadline passes).
    /// </summary>
    private static async Task<List<JsonNode?>> PutUntilDelivered(FidelityContext ctx, string bucket, string key, string queueUrl, double timeoutS = 300)
    {
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var clock = FidelityTime.StartNew();
        var attempt = 0;
        while (clock.Elapsed.TotalSeconds < timeoutS)
        {
            attempt++;
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = $"attempt-{attempt}" });
            var inner = clock.Elapsed.TotalSeconds + 20;
            while (clock.Elapsed.TotalSeconds < inner && clock.Elapsed.TotalSeconds < timeoutS)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 5,
                    MessageAttributeNames = ["All"],
                });
                var msgs = resp.Messages ?? [];
                if (msgs.Count > 0)
                {
                    var output = new List<JsonNode?>();
                    foreach (var m in msgs)
                    {
                        output.Add(Body(m));
                        await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
                    }

                    return output;
                }
            }
        }

        throw Fail($"no S3->EventBridge notification reached the queue within {timeoutS}s after {attempt} put_object attempts");
    }

    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    private sealed record Queue(string Name, string Url, string Arn);

    /// <summary>One received message: its body parsed as JSON (or the raw string), id, receipt handle and attributes.</summary>
    private sealed record Received(JsonNode? Body, string MessageId, string ReceiptHandle, Dictionary<string, MessageAttributeValue> Attributes);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attrs = null)
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
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>Parse an SQS message body as JSON, falling back to the raw string.</summary>
    private static JsonNode? Body(SqsMessage msg)
    {
        var raw = msg.Body ?? string.Empty;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    private static JsonObject SqsPolicyForEvents(string queueArn, string? ruleArn = null)
    {
        var statement = new JsonObject
        {
            ["Sid"] = "AllowEventsSendMessage",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
        };
        if (ruleArn is not null)
        {
            statement["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } };
        }

        return new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = new JsonArray(statement) };
    }

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });

    private static async Task<string> RuleArn(FidelityContext ctx, string ruleName, string? busName = null) =>
        (await ctx.Events.DescribeRuleAsync(new DescribeRuleRequest { Name = ruleName, EventBusName = busName })).Arn;

    /// <summary>Poll SQS until expected_count messages arrive or timeout_s elapses; return what arrived.</summary>
    private static async Task<List<Received>> ReceiveMessages(FidelityContext ctx, string queueUrl, int expectedCount = 1, double timeoutS = 60.0, bool delete = true)
    {
        var sqs = ctx.Sqs;
        var messages = new List<Received>();
        var started = FidelityTime.StartNew();
        while (messages.Count < expectedCount && started.Elapsed.TotalSeconds < timeoutS)
        {
            var remaining = Math.Max(1, (int)(timeoutS - started.Elapsed.TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = Math.Min(10, expectedCount - messages.Count),
                WaitTimeSeconds = Math.Min(5, remaining),
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                messages.Add(new Received(Body(msg), msg.MessageId, msg.ReceiptHandle, msg.MessageAttributes ?? []));
                if (delete)
                {
                    await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle);
                }
            }
        }

        return messages;
    }

    /// <summary>
    /// Emit an EventBridge event repeatedly until it lands on the SQS queue; return the messages list.
    /// A freshly-created rule/target can silently drop the first PutEvents before the path is live, and
    /// EventBridge never retries a dropped event, so re-emitting is more reliable than one longer wait.
    /// Raises AssertionError if never delivered.
    /// </summary>
    private static async Task<List<Received>> ReemitUntilDelivered(FidelityContext ctx, string queueUrl, PutEventsRequestEntry entry, int attempts = 8, double pollS = 6.0)
    {
        var events = ctx.Events;
        for (var i = 0; i < attempts; i++)
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
            var messages = await ReceiveMessages(ctx, queueUrl, expectedCount: 1, timeoutS: pollS);
            if (messages.Count > 0)
            {
                return messages;
            }
        }

        throw Fail($"event never delivered to {queueUrl} after {attempts} attempts of {pollS}s each");
    }

    private sealed record Bucket(string Name, string Arn);

    /// <summary>Create an S3 bucket (registers empty-then-delete teardown). Returns {name,arn}.</summary>
    private static async Task<Bucket> CreateBucket(FidelityContext ctx)
    {
        var s3 = ctx.S3;
        var name = await S3Fidelity.CreateBucket(ctx, $"{XsvcPrefix}bkt-", s3);
        try
        {
            await s3.PutBucketTaggingAsync(new PutBucketTaggingRequest
            {
                BucketName = name,
                TagSet = [.. XsvcResourceTags().Select(t => new Amazon.S3.Model.Tag { Key = t.Key, Value = t.Value })],
            });
        }
        catch (AmazonS3Exception)
        {
        }

        return new Bucket(name, $"arn:aws:s3:::{name}");
    }
}
