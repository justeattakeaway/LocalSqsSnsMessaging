// Translated from behavioral/events__s3__sqs/check_s3_eventbridge_not_enabled_no_delivery.py
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

/// <summary>events__s3__sqs.s3-eventbridge-not-enabled-no-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class S3EventbridgeNotEnabledNoDelivery
{
    /// <summary>
    /// Without EventBridgeConfiguration enabled on the bucket, S3 must not emit events to EB -- proven
    /// against a twin control bucket, wired identically but WITH EventBridgeConfiguration enabled, so
    /// a target that delivers nothing to anyone cannot pass by coincidence.
    /// </summary>
    [Test]
    [DisplayName("events__s3__sqs.s3-eventbridge-not-enabled-no-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var s3 = ctx.S3;
        var events = ctx.Events;
        var subjectBucket = await CreateBucket(ctx);
        var controlBucket = await CreateBucket(ctx);
        var queue = await CreateQueue(ctx);

        await s3.PutBucketNotificationAsync(new PutBucketNotificationRequest
        {
            BucketName = controlBucket.Name,
            EventBridgeConfiguration = new EventBridgeConfiguration(),
        });

        // NOTE: intentionally skip put_bucket_notification_configuration(EventBridgeConfiguration={})
        // for subject_bucket -- that omission is the subject under test.
        var ruleName = FidelityContext.UniqueName("s3-eb-disabled-");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = new JsonObject
            {
                ["source"] = new JsonArray("aws.s3"),
                ["detail-type"] = new JsonArray("Object Created"),
                ["detail"] = new JsonObject
                {
                    ["bucket"] = new JsonObject { ["name"] = new JsonArray(subjectBucket.Name, controlBucket.Name) },
                },
            }.ToJsonString(),
        });
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));
        var ruleArn = await RuleArn(ctx, ruleName);
        await SetQueuePolicy(ctx, queue.Url, SqsPolicyForEvents(queue.Arn, ruleArn));
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sqs-target", Arn = queue.Arn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sqs-target"] }));

        await s3.PutObjectAsync(new PutObjectRequest { BucketName = subjectBucket.Name, Key = "no-eb.txt", ContentBody = "data" });

        var (controlDelivered, subjectMessages) = await DrainTwin(ctx, queue.Url, controlBucket.Name, subjectBucket.Name);
        Ensure(
            controlDelivered,
            "positive control failed: S3->EventBridge delivery for control_bucket (which HAS EventBridgeConfiguration enabled) never reached the queue, so absence for subject_bucket cannot be attributed to correct behavior rather than a dead pipe");
        Ensure(
            subjectMessages.Count == 0,
            $"SQS received an S3 event via EventBridge even though EventBridgeConfiguration was never enabled on subject_bucket: [{string.Join(", ", subjectMessages.Select(m => m?.ToJsonString()))}]");
    }

    /// <summary>True for S3's one-off destination-validation handshake message (s3:TestEvent).</summary>
    private static bool IsS3TestEvent(JsonNode? body) => body is JsonObject o && (string?)o["Event"] == "s3:TestEvent";

    private static string? BucketNameOf(JsonNode? body) =>
        body is JsonObject o ? (string?)o["detail"]?["bucket"]?["name"] : null;

    /// <summary>
    /// Prove delivery is alive on *control_bucket* while watching *subject_bucket* for a leak. Drives
    /// the SAME rule/target/queue with a twin bucket that DOES have EventBridgeConfiguration enabled,
    /// re-putting into it until a control message lands; the hold continues a further
    /// *hold_after_control_s* past that point to catch a late/racy leak.
    /// Returns (control_delivered, subject_messages_seen).
    /// </summary>
    private static async Task<(bool ControlDelivered, List<JsonNode?> SubjectMessages)> DrainTwin(
        FidelityContext ctx, string queueUrl, string controlBucket, string subjectBucket, double controlTimeoutS = 300.0, double holdAfterControlS = 45.0)
    {
        var s3 = ctx.S3;
        var sqs = ctx.Sqs;
        var clock = FidelityTime.StartNew();
        var deadline = controlTimeoutS;
        var controlDelivered = false;
        var holdDeadline = 0.0;
        var subjectMessages = new List<JsonNode?>();
        var attempt = 0;
        while ((controlDelivered && clock.Elapsed.TotalSeconds < holdDeadline) || (!controlDelivered && clock.Elapsed.TotalSeconds < deadline))
        {
            if (!controlDelivered)
            {
                attempt++;
                await s3.PutObjectAsync(new PutObjectRequest { BucketName = controlBucket, Key = $"control-probe-{attempt}.txt", ContentBody = "data" });
            }

            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            foreach (var m in resp.Messages ?? [])
            {
                var body = Body(m);
                await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
                if (IsS3TestEvent(body))
                {
                    continue;
                }

                var name = BucketNameOf(body);
                if (name == subjectBucket)
                {
                    subjectMessages.Add(body);
                }
                else if (name == controlBucket && !controlDelivered)
                {
                    controlDelivered = true;
                    holdDeadline = clock.Elapsed.TotalSeconds + holdAfterControlS;
                }
            }

            if (subjectMessages.Count > 0)
            {
                break;
            }
        }

        return (controlDelivered, subjectMessages);
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
