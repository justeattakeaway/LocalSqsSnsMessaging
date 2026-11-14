// Translated from behavioral/sns/check_publish_batch_too_many_entries_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-batch-too-many-entries-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishBatchTooManyEntriesRejected
{
    private const string Scenario = "sns-publish-batch-too-many-entries";

    /// <summary>
    /// PublishBatch rejects an 11-entry request wholesale with TooManyEntriesInBatchRequest and
    /// delivers none of it to a subscriber, while an identical batch trimmed to the 10-entry
    /// maximum succeeds in full and every entry actually arrives at the destination.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-batch-too-many-entries-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsBatch, "PublishBatch accepts an 11-entry batch in full instead of refusing it with TooManyEntriesInBatchRequest")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var runId = FidelityContext.UniqueName("sim2real-behav-sns-batchmax-");
        var oversizedMarkers = Enumerable.Range(0, 11).Select(i => $"{runId}-over-{i}").ToList();
        var oversizedEntries = oversizedMarkers.Select((m, i) => new PublishBatchRequestEntry { Id = $"e{i}", Message = m }).ToList();

        var (code, message) = await Error(() => sns.PublishBatchAsync(new PublishBatchRequest
        {
            TopicArn = topicArn,
            PublishBatchRequestEntries = oversizedEntries,
        }));
        Ensure(code == "TooManyEntriesInBatchRequest",
            $"expected TooManyEntriesInBatchRequest for an 11-entry batch, got '{code}'");
        const string expectedMessage = "The batch request contains more entries than permissible.";
        Ensure(message == expectedMessage,
            $"expected the exact message '{expectedMessage}', got '{message}'");

        var leaked = await ReceiveAny(ctx, queueUrl, rounds: 1, waitS: 10);
        var stray = leaked.Where(oversizedMarkers.Contains).ToList();
        Ensure(stray.Count == 0,
            "the 11-entry batch was rejected wholesale, so none of it may be delivered — but " +
            $"{Repr(stray)} arrived");

        var validMarkers = Enumerable.Range(0, 10).Select(i => $"{runId}-ok-{i}").ToList();
        var validEntries = validMarkers.Select((m, i) => new PublishBatchRequestEntry { Id = $"ok{i}", Message = m }).ToList();
        var resp = await sns.PublishBatchAsync(new PublishBatchRequest { TopicArn = topicArn, PublishBatchRequestEntries = validEntries });
        Ensure(resp.Failed is not { Count: > 0 }, $"expected no Failed entries, got [{string.Join(", ", (resp.Failed ?? []).Select(f => $"{{'Id': '{f.Id}', 'Code': '{f.Code}'}}"))}]");
        Ensure((resp.Successful ?? []).Count == 10,
            $"expected a 10-entry batch to be accepted in full, got [{string.Join(", ", (resp.Successful ?? []).Select(s => $"{{'Id': '{s.Id}', 'MessageId': '{s.MessageId}'}}"))}]");

        var delivered = await DrainUntilAll(ctx, queueUrl, validMarkers, timeoutS: 90.0);
        Ensure(delivered.Order(StringComparer.Ordinal).SequenceEqual(validMarkers.Order(StringComparer.Ordinal)),
            $"the accepted 10-entry batch did not deliver every entry: {Repr(delivered)}");
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    /// <summary>
    /// The AWS error (Code, Message) raised by fn(). Raises if fn SUCCEEDS — an unexpected success is
    /// the strongest possible signal.
    /// </summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-batchmax-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags = [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-batchmax-q-");
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

    /// <summary>Subscribe an SQS queue (raw delivery) and poll GetSubscriptionAttributes until confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["RawMessageDelivery"] = "true" },
            ReturnSubscriptionArn = true,
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

    /// <summary>Long-poll the queue until every wanted marker has been seen; returns the markers collected.</summary>
    private static async Task<List<string>> DrainUntilAll(FidelityContext ctx, string queueUrl, List<string> wanted, double timeoutS = 90.0)
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
}
