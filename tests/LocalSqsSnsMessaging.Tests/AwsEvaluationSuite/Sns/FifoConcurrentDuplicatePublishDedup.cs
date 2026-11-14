// Translated from behavioral/sns/check_fifo_concurrent_duplicate_publish_dedup.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.fifo-concurrent-duplicate-publish-dedup</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoConcurrentDuplicatePublishDedup
{
    private const string Scenario = "sns-fifo-concurrent-dedup";

    /// <summary>
    /// Five concurrent Publish calls sharing a MessageDeduplicationId collapse to exactly one
    /// message: all succeed with one SequenceNumber and MessageId, and the queue receives one copy.
    /// </summary>
    [Test]
    [DisplayName("sns.fifo-concurrent-duplicate-publish-dedup")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsFifo, "Publish to a FIFO topic returns no SequenceNumber, so the racing publishes' SequenceNumbers cannot be compared")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueue(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var sns = ctx.Sns;
        var runId = FidelityContext.UniqueName("sim2real-behav-sns-fifoconc-");
        var group = $"{runId}-group";
        var dedupId = $"{runId}-dedup";
        var racerBodies = Enumerable.Range(0, 5).Select(i => $"{runId}-racer-{i}").ToList();

        Task<PublishResponse> Publisher(string body) => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = body, MessageGroupId = group,
            MessageDeduplicationId = dedupId,
        });

        var results = await RunConcurrent(racerBodies.Select(b => (Func<Task<PublishResponse>>)(() => Publisher(b))).ToList());
        var nOk = results.Count(r => r is not Exception);
        Ensure(nOk == 5,
            $"expected all 5 racing Publish calls to be accepted, got {nOk} " +
            $"(errors: [{string.Join(", ", ErrorCodes(results).Select(c => $"'{c}'"))}])");

        var oks = results.OfType<PublishResponse>().ToList();
        // Python indexes r["SequenceNumber"], which raises KeyError when the response omits it.
        var seqs = oks.Select(r => r.SequenceNumber ?? throw new KeyNotFoundException("SequenceNumber")).ToHashSet();
        var msgIds = oks.Select(r => r.MessageId ?? throw new KeyNotFoundException("MessageId")).ToHashSet();
        Ensure(seqs.Count == 1,
            $"expected dedup to collapse concurrent duplicates to one SequenceNumber, got {{{string.Join(", ", seqs.Select(s => $"'{s}'"))}}}");
        Ensure(msgIds.Count == 1,
            $"expected dedup to collapse concurrent duplicates to one MessageId, got {{{string.Join(", ", msgIds.Select(s => $"'{s}'"))}}}");

        // FIFO order within the group makes the sentinel arrive last, bounding the drain.
        var sentinel = $"{runId}-sentinel";
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn, Message = sentinel, MessageGroupId = group,
            MessageDeduplicationId = $"{runId}-sentinel-dedup",
        });
        var beforeSentinel = await DrainUntilBody(ctx, queueUrl, sentinel, timeoutS: 120.0);
        var deliveredRacers = beforeSentinel.Where(racerBodies.Contains).ToList();
        Ensure(deliveredRacers.Count == 1,
            $"a shared MessageDeduplicationId produced {deliveredRacers.Count} deliveries at the " +
            $"queue, expected exactly 1: {List(deliveredRacers)} (seen before sentinel: {List(beforeSentinel)})");
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifoconc-") + ".fifo";
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new() { ["FifoTopic"] = "true" },
            Tags = [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS FIFO queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifoconc-q-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true" },
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

    /// <summary>Subscribe the real queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true,
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

    /// <summary>Unwrap the SNS Notification envelope and return its Message, or the body if it is not one.</summary>
    private static string InnerMessage(string body)
    {
        JsonNode? envelope;
        try
        {
            envelope = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (envelope is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification"
            && obj["Message"] is JsonValue inner && inner.TryGetValue<string>(out var s))
        {
            return s;
        }

        return body;
    }

    /// <summary>Receive until the sentinel body arrives; returns everything seen BEFORE it, in order.</summary>
    private static async Task<List<string>> DrainUntilBody(FidelityContext ctx, string queueUrl, string sentinel, double timeoutS = 120.0)
    {
        var sqs = ctx.Sqs;
        var before = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var inner = InnerMessage(msg.Body ?? "");
                if (inner == sentinel)
                {
                    return before;
                }

                before.Add(inner);
            }
        }

        throw Fail($"the FIFO sentinel '{sentinel}' never arrived within {timeoutS}s, so the deduplication " +
            $"count is not bounded; messages seen were {List(before)}");
    }

    /// <summary>Run callables in parallel; return results 1:1 in submission order (exceptions captured, not raised).</summary>
    private static async Task<object[]> RunConcurrent<T>(List<Func<Task<T>>> fns)
        where T : class
    {
        var tasks = fns.Select(fn => Task.Run(async () =>
        {
            try
            {
                return (object)await fn();
            }
#pragma warning disable CA1031 // Captured for the caller to assert on, as the Python does.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                return ex;
            }
        })).ToList();
        return await Task.WhenAll(tasks);
    }

    private static List<string?> ErrorCodes(object[] results)
    {
        var codes = new List<string?>();
        foreach (var r in results)
        {
            if (r is AmazonServiceException ce)
            {
                codes.Add(AwsError.From(ce).Code);
            }
            else if (r is Exception ex)
            {
                codes.Add(ex.GetType().Name);
            }
        }

        return codes;
    }
}
