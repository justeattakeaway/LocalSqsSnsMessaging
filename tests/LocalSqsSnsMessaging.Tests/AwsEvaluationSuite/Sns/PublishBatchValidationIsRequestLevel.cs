// Translated from behavioral/sns/check_publish_batch_validation_is_request_level.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-validation-is-request-level</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchValidationIsRequestLevel
{
    private const string Scenario = "sns-publish-batch-validation-is-request-level";

    /// <summary>
    /// One malformed entry fails the whole PublishBatch with ParameterValueInvalid (not a
    /// per-entry Failed result) and its siblings are never delivered; the repaired batch is.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-validation-is-request-level")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsBatch, "PublishBatch with a Number attribute of 'not-a-number' succeeds instead of refusing the request with ParameterValueInvalid")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx, FidelityContext.UniqueName("sim2real-behav-sns-batchreq-"));
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-batchreq-run-");
        var good1 = $"{runId}-good-1";
        var good2 = $"{runId}-good-2";

        // Measure positive-control latency L on this exact wiring before breaking anything.
        var calib = $"{runId}-calib";
        var t0 = FidelityTime.StartNew();
        await sns.PublishAsync(new PublishRequest { TopicArn = topicArn, Message = calib });
        await DrainUntilBody(ctx, queueUrl, calib, timeoutS: 60.0);
        var latencyS = t0.Elapsed.TotalSeconds;

        var error = await FidelityContext.CaptureError(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "good-1", Message = good1 },
                new()
                {
                    Id = "bad-1", Message = "bad-attrs", MessageAttributes = new()
                    {
                        ["broken"] = new Amazon.SimpleNotificationService.Model.MessageAttributeValue { DataType = "Number", StringValue = "not-a-number" },
                    },
                },
                new() { Id = "good-2", Message = good2 },
            ],
        }));
        if (error is null)
        {
            throw Fail("a PublishBatch with a malformed Number attribute was expected to be rejected, " +
                "but it SUCCEEDED");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "ParameterValueInvalid", $"expected request-level ParameterValueInvalid for a malformed entry, got {code}");
        const string expectedMessage = "Could not cast message attribute 'broken' value to number.";
        Ensure(message == expectedMessage, $"expected the exact message '{expectedMessage}', got '{message}'");

        // Absence of delivery is bounded by >=3x the measured latency, then confirmed by queue depth.
        var windowS = Math.Max(3.0 * latencyS, 15.0);
        var leaked = await PollForLeak(ctx, queueUrl, [good1, good2], windowS);
        Ensure(leaked.Count == 0,
            "the batch was rejected as a whole, so neither sibling entry may be delivered — but " +
            $"{Repr(leaked)} arrived within a {windowS.ToString("F1", CultureInfo.InvariantCulture)}s window (>=3x the {latencyS.ToString("F1", CultureInfo.InvariantCulture)}s " +
            "positive-control latency measured on the same wiring)");
        var remaining = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes?.GetValueOrDefault("ApproximateNumberOfMessages");
        Ensure(remaining == "0",
            "expected an empty queue after the bounded absence window, got " +
            $"ApproximateNumberOfMessages='{remaining}'");

        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "good-1", Message = good1 },
                new() { Id = "good-2", Message = good2 },
            ],
        });
        var successfulIds = (resp.Successful ?? []).Select(e => e.Id).ToHashSet();
        Ensure(successfulIds.SetEquals(["good-1", "good-2"]),
            $"expected both entries in Successful, got [{string.Join(", ", (resp.Successful ?? []).Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}]");
        Ensure(resp.Failed is null or { Count: 0 },
            $"expected no Failed entries, got [{string.Join(", ", resp.Failed!.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}'}}"))}]");
        var msgIds = resp.Successful!.Select(e => e.MessageId).ToHashSet();
        Ensure(msgIds.Count == 2, $"expected a distinct MessageId per entry, got {{{string.Join(", ", msgIds.Select(m => $"'{m}'"))}}}");

        var delivered = await DrainUntilAll(ctx, queueUrl, [good1, good2], timeoutS: 60.0);
        Ensure(delivered.Order(StringComparer.Ordinal).SequenceEqual(new[] { good1, good2 }.Order(StringComparer.Ordinal)),
            $"the repaired batch did not deliver both entries at the destination, got {Repr(delivered)}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged) and register DeleteTopic; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string name)
    {
        var sns = ctx.Sns;
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-batchreq-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
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

    /// <summary>Subscribe an SQS queue (raw delivery) and poll GetSubscriptionAttributes until confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" }, ReturnSubscriptionArn = true,
        })).SubscriptionArn;
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

    /// <summary>Receive until <c>target</c>'s body arrives; returns everything seen (including target).</summary>
    private static async Task<List<string>> DrainUntilBody(FidelityContext ctx, string queueUrl, string target, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                seen.Add(body);
                if (body == target)
                {
                    return seen;
                }
            }
        }

        throw Fail($"'{target}' never arrived within {timeoutS}s; saw {Repr(seen)}");
    }

    /// <summary>Long-poll for <c>window_s</c> and return any received body that is one of <c>markers</c>.</summary>
    private static async Task<List<string>> PollForLeak(FidelityContext ctx, string queueUrl, HashSet<string> markers, double windowS)
    {
        var sqs = ctx.Sqs;
        var leaked = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var body = msg.Body ?? "";
                if (markers.Contains(body))
                {
                    leaked.Add(body);
                }
            }
        }

        return leaked;
    }

    /// <summary>Long-poll until every wanted marker has been seen; returns the markers collected.</summary>
    private static async Task<List<string>> DrainUntilAll(FidelityContext ctx, string queueUrl, IEnumerable<string> wanted, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var outstanding = new HashSet<string>(wanted, StringComparer.Ordinal);
        var seen = new List<string>();
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
}
