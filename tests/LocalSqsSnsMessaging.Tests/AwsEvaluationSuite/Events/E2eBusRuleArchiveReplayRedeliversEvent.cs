// Translated from behavioral/events/check_e2e_bus_rule_archive_replay_redelivers_event.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.e2e-bus-rule-archive-replay-redelivers-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class E2eBusRuleArchiveReplayRedeliversEvent
{
    /// <summary>
    /// PutEvents on a custom bus delivers to the rule's SQS target, and a StartReplay of the
    /// bus archive redelivers the same event, carrying the replay name, to that target.
    /// </summary>
    [Test]
    [DisplayName("events.e2e-bus-rule-archive-replay-redelivers-event")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var busName = FidelityContext.UniqueName("sim2real-eb-bus-");
        var ruleName = FidelityContext.UniqueName("sim2real-eb-rule-");
        var archiveName = FidelityContext.UniqueName("sim2real-eb-arc-");
        var replayName = FidelityContext.UniqueName("sim2real-eb-replay-");
        var sourceName = FidelityContext.UniqueName("sim2real.archive.src.").Replace('-', '.');
        var marker = FidelityContext.UniqueName("marker-");

        List<Tag> TagsKv() => [new Tag { Key = "sim2real:managed", Value = "true" }];

        var bus = await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = TagsKv() });
        var busArn = bus.EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-eb-q-"), Tags = new() { ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = $$"""{"source": ["{{sourceName}}"]}""",
            State = "ENABLED",
            Tags = TagsKv(),
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = busName }));

        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridgeSend",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });

        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = busName,
            Targets = [new Target { Id = "1", Arn = queueArn }],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = busName, Ids = ["1"] }));

        var archiveArn = (await events.CreateArchiveAsync(new CreateArchiveRequest
        {
            ArchiveName = archiveName,
            EventSourceArn = busArn,
            EventPattern = $$"""{"source": ["{{sourceName}}"]}""",
        })).ArchiveArn;
        ctx.OnCleanup(() => events.DeleteArchiveAsync(new DeleteArchiveRequest { ArchiveName = archiveName }));

        await PollUntil(
            async () => (await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName })).State?.Value,
            state => state == "ENABLED",
            timeoutS: BudgetS(ctx, 60),
            intervalS: 1,
            ctx: ctx);

        var putTime = FidelityTime.UtcNow.UtcDateTime;
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Time = putTime,
                    Source = sourceName,
                    DetailType = "sim2real-archive-test",
                    Detail = $$"""{"marker": "{{marker}}"}""",
                    EventBusName = busName,
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {resp}");

        // First, direct delivery: drain the original message so the replayed one is unambiguous.
        var first = await WaitForMarker(ctx, queueUrl, marker, timeoutS: BudgetS(ctx, 60));
        Ensure(!first.ContainsKey("replay-name"), "direct delivery must not carry a replay-name marker");

        // Archive ingestion lags PutEvents with no documented bound; it can exceed 15 minutes.
        await PollUntil(
            async () => (await events.DescribeArchiveAsync(new DescribeArchiveRequest { ArchiveName = archiveName })).EventCount ?? 0,
            count => count >= 1,
            timeoutS: BudgetS(ctx, 1500),
            intervalS: 1,
            ctx: ctx);

        var replayStart = putTime - TimeSpan.FromMinutes(15);
        var replayEnd = putTime + TimeSpan.FromMinutes(15);
        await events.StartReplayAsync(new StartReplayRequest
        {
            ReplayName = replayName,
            EventSourceArn = archiveArn,
            EventStartTime = replayStart,
            EventEndTime = replayEnd,
            Destination = new ReplayDestination { Arn = busArn, FilterArns = [ruleArn] },
        });

        var finalState = await PollUntil(
            async () => (await events.DescribeReplayAsync(new DescribeReplayRequest { ReplayName = replayName })).State?.Value,
            state => state is "COMPLETED" or "FAILED" or "CANCELLED",
            timeoutS: BudgetS(ctx, 180),
            intervalS: 1,
            ctx: ctx);
        Ensure(finalState == "COMPLETED", $"replay ended in {finalState}");

        var replayed = await WaitForMarker(ctx, queueUrl, marker, timeoutS: BudgetS(ctx, 120));
        Ensure(
            (string?)replayed["replay-name"] == replayName,
            $"redelivered event carries replay-name='{(string?)replayed["replay-name"]}', expected " +
            $"'{replayName}' -- without that field it is indistinguishable from a leftover live " +
            $"delivery: {replayed.ToJsonString()}");
        Ensure(
            (string?)replayed["detail"]?["marker"] == marker,
            $"redelivered event's detail.marker='{(string?)replayed["detail"]?["marker"]}' does not match " +
            $"the published marker '{marker}': {replayed.ToJsonString()}");
        Ensure(
            (string?)replayed["source"] == sourceName,
            $"redelivered event carries source='{(string?)replayed["source"]}', expected '{sourceName}'");
    }

    /// <summary>
    /// A poll's timeout: cap_s, bounded by the runner's remaining time less 5 s so the poll fails
    /// with its own message.
    /// </summary>
    private static double BudgetS(FidelityContext ctx, double capS) => Math.Max(1.0, Math.Min(capS, ctx.TimeLeftSeconds - 5.0));

    /// <summary>True when the runner's remaining time, not the wait's own deadline, ended a capped wait.</summary>
    private static bool BudgetSpent(FidelityContext ctx) => ctx.TimeLeftSeconds <= 6.0;

    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, double timeoutS, double intervalS, FidelityContext? ctx = null)
    {
        var started = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutS);
        T? last = default;
        while (started.Elapsed < deadline)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            var remaining = deadline - started.Elapsed;
            var sleep = TimeSpan.FromSeconds(intervalS);
            await FidelityTime.Delay(remaining < sleep ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : sleep);
        }

        if (ctx is not null && BudgetSpent(ctx))
        {
            throw new TimeoutException("condition: the run budget ended first; not graded");
        }

        throw Fail($"condition never met within {timeoutS}s, last value={last}");
    }

    private static async Task<JsonObject> WaitForMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        var deadline = TimeSpan.FromSeconds(timeoutS);
        while (started.Elapsed < deadline)
        {
            var waitS = Math.Max(0, Math.Min(5, (int)(deadline - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = waitS });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if ((string?)body["detail"]?["marker"] == marker)
                {
                    return body;
                }
            }
        }

        if (BudgetSpent(ctx))
        {
            throw new TimeoutException($"marker '{marker}' on the queue: the run budget ended first; not graded");
        }

        throw Fail($"marker '{marker}' never arrived on queue within {timeoutS}s");
    }
}
