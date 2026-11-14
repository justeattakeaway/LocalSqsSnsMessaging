// Translated from behavioral/sns/check_publish_batch_duplicate_ids_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-duplicate-ids-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchDuplicateIdsRejected
{
    private const string Scenario = "sns-publish-batch-duplicate-ids";

    /// <summary>
    /// PublishBatch refuses a request whose two entries share an Id with BatchEntryIdsNotDistinct
    /// and delivers neither entry, while the same entries with distinct Ids succeed and both arrive.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-duplicate-ids-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsBatch, "PublishBatch with two entries sharing Id 'dup' succeeds for both instead of BatchEntryIdsNotDistinct")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx, FidelityContext.UniqueName("sim2real-behav-sns-batchdup-"));
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn, "sim2real-behav-sns-batchdup-q-");
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-batchdup-run-");
        string[] controlMarkers = [$"{runId}-control-a", $"{runId}-control-b"];

        var controlStart = FidelityTime.StartNew();
        var resp = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "a", Message = controlMarkers[0] },
                new() { Id = "b", Message = controlMarkers[1] },
            ],
        });
        Ensure((resp.Failed ?? []).Count == 0, $"positive control reported failures: {FailedRepr(resp.Failed)}");
        Ensure((resp.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["a", "b"]),
            $"positive control did not report both entries Successful: {SuccessfulRepr(resp.Successful)}");
        await DrainUntilAll(ctx, queueUrl, controlMarkers, timeoutS: 60.0);
        var controlLatencyS = controlStart.Elapsed.TotalSeconds;
        // The absence wait is bounded by the control's own measured delivery latency.
        var absenceWindowS = Math.Max(3.0 * controlLatencyS, 20.0);

        string[] brokenMarkers = [$"{runId}-broken-a", $"{runId}-broken-b"];
        var code = await ErrorCode(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "dup", Message = brokenMarkers[0] },
                new() { Id = "dup", Message = brokenMarkers[1] },
            ],
        }));
        Ensure(code == "BatchEntryIdsNotDistinct",
            $"expected BatchEntryIdsNotDistinct for a batch with two entries sharing Id 'dup', got '{code}'");

        var leaked = await ReceiveFor(ctx, queueUrl, durationS: absenceWindowS);
        var leakedBodies = leaked.Select(m => m.Body).ToList();
        Ensure(!leakedBodies.Any(b => brokenMarkers.Contains(b)),
            "the duplicate-Id batch was rejected as a whole, so neither entry may be delivered — " +
            $"but [{string.Join(", ", leakedBodies.Where(b => brokenMarkers.Contains(b)).Select(b => $"'{b}'"))}] arrived within " +
            $"{absenceWindowS.ToString("F1", CultureInfo.InvariantCulture)}s (>= 3x the {controlLatencyS.ToString("F1", CultureInfo.InvariantCulture)}s control latency)");
        var visible = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl, AttributeNames = ["ApproximateNumberOfMessages"],
        })).Attributes!["ApproximateNumberOfMessages"];
        Ensure(visible == "0", $"expected ApproximateNumberOfMessages == '0' after the rejected batch, got '{visible}'");

        var repaired = await sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries =
            [
                new() { Id = "a2", Message = brokenMarkers[0] },
                new() { Id = "b2", Message = brokenMarkers[1] },
            ],
        });
        Ensure((repaired.Failed ?? []).Count == 0, $"repaired batch (distinct Ids) reported failures: {FailedRepr(repaired.Failed)}");
        Ensure((repaired.Successful ?? []).Select(e => e.Id).Order(StringComparer.Ordinal).SequenceEqual(["a2", "b2"]),
            $"repaired batch did not report both entries Successful: {SuccessfulRepr(repaired.Successful)}");
        await DrainUntilAll(ctx, queueUrl, brokenMarkers, timeoutS: 60.0);
    }

    private static string FailedRepr(List<Amazon.SimpleNotificationService.Model.BatchResultErrorEntry>? failed) => failed is null ? "None" :
        $"[{string.Join(", ", failed.Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}', 'Message': '{f.Message}', 'SenderFault': {f.SenderFault}}}"))}]";

    private static string SuccessfulRepr(List<PublishBatchResultEntry>? ok) => ok is null ? "None" :
        $"[{string.Join(", ", ok.Select(e => $"{{'Id': '{e.Id}', 'MessageId': '{e.MessageId}'}}"))}]";

    /// <summary>The AWS error Code raised by fn(); raises if fn succeeds.</summary>
    private static async Task<string?> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {(result is PublishBatchResponse r ? $"{{'Successful': {SuccessfulRepr(r.Successful)}, 'Failed': {FailedRepr(r.Failed)}}}" : result)}");
        }

        return error.Code;
    }

    /// <summary>CreateTopic (tagged) + register DeleteTopic cleanup; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string name)
    {
        var arn = (await ctx.Sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags = [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a REAL, tagged SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, string namePrefix)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName(namePrefix);
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
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

    /// <summary>Subscribe an SQS queue (raw delivery) and poll until it is confirmed; returns the SubscriptionArn.</summary>
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

    /// <summary>Long-poll the queue until every wanted marker has been seen; returns the markers collected.</summary>
    private static async Task<List<string>> DrainUntilAll(FidelityContext ctx, string queueUrl, IEnumerable<string> wanted, double timeoutS = 60.0)
    {
        var sqs = ctx.Sqs;
        var seen = new List<string>();
        var outstanding = new HashSet<string>(wanted, StringComparer.Ordinal);
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
            throw Fail($"[{string.Join(", ", outstanding.Order(StringComparer.Ordinal).Select(o => $"'{o}'"))}] never arrived within {timeoutS}s; " +
                $"received [{string.Join(", ", seen.Select(s => $"'{s}'"))}]");
        }

        return seen;
    }

    /// <summary>Long-poll a queue for at least duration_s, deleting and returning everything received.</summary>
    private static async Task<List<Message>> ReceiveFor(FidelityContext ctx, string queueUrl, double durationS)
    {
        var sqs = ctx.Sqs;
        var messages = new List<Message>();
        var clock = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(durationS);
        while (clock.Elapsed < deadline)
        {
            var remaining = Math.Max(1, Math.Min(20, (int)(deadline - clock.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = remaining });
            var batch = resp.Messages ?? [];
            if (batch.Count > 0)
            {
                await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = queueUrl,
                    Entries = batch.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), ReceiptHandle = m.ReceiptHandle }).ToList(),
                });
            }

            messages.AddRange(batch);
        }

        return messages;
    }
}
