// Translated from behavioral/events__sqs/check_put_events_trace_header_into_archived_bus_absent_on_replay.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.put-events-trace-header-into-archived-bus-absent-on-replay</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsTraceHeaderIntoArchivedBusAbsentOnReplay
{
    /// <summary>
    /// A PutEvents TraceHeader reaches the live SQS delivery as the AWSTraceHeader attribute, but
    /// is absent from the same event's redelivery once it is replayed from an archive.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.put-events-trace-header-into-archived-bus-absent-on-replay")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeArchives, "CreateArchive is not implemented: it fails with NotSupportedException, so no archive or replay exists")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var busName = FidelityContext.UniqueName("sim2real-eb-bus-");
        var ruleName = FidelityContext.UniqueName("sim2real-eb-rule-");
        var archiveName = FidelityContext.UniqueName("sim2real-eb-arc-");
        var replayName = FidelityContext.UniqueName("sim2real-eb-replay-");
        var sourceName = FidelityContext.UniqueName("sim2real.archtrace.src.").Replace('-', '.');
        var marker = FidelityContext.UniqueName("marker-");

        List<EventBridgeTag> TagsKv() => [new EventBridgeTag { Key = "sim2real:managed", Value = "true" }];

        var bus = await events.CreateEventBusAsync(new CreateEventBusRequest { Name = busName, Tags = TagsKv() });
        var busArn = bus.EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = busName }));

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-eb-q-"),
            Tags = new() { ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];

        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = busName,
            EventPattern = $$"""{"source": ["{{sourceName}}"]}""",
            State = RuleState.ENABLED,
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

        await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, EventBusName = busName, Targets = [new Target { Id = "1", Arn = queueArn }] });
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

        var trace =
            $"Root=1-{FidelityTime.UtcNow.ToUnixTimeSeconds():x8}-{TokenHex(12)};" +
            $"Parent={TokenHex(8)};Sampled=1";
        var putTime = FidelityTime.UtcNow.UtcDateTime;
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Time = putTime,
                    Source = sourceName,
                    DetailType = "sim2real-archive-trace-test",
                    Detail = $$"""{"marker": "{{marker}}"}""",
                    EventBusName = busName,
                    TraceHeader = trace,
                },
            ],
        });
        Ensure(
            (resp.FailedEntryCount ?? 0) == 0,
            $"PutEvents rejected the traced entry: {{'FailedEntryCount': {resp.FailedEntryCount}, 'Entries': " +
            $"[{string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]}}");

        // Direct delivery first: drain it so the later replayed copy is unambiguous.
        var first = await WaitForMarker(ctx, queueUrl, marker, timeoutS: BudgetS(ctx, 60));
        Ensure(!first.Body.ContainsKey("replay-name"), "direct delivery must not carry a replay-name marker");
        Ensure(
            first.TraceHeader == trace,
            $"the live SQS delivery must carry AWSTraceHeader '{trace}', got {Repr(first.TraceHeader)}");

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
        var replayedName = replayed.Body["replay-name"] is JsonValue rn && rn.TryGetValue<string>(out var rns) ? rns : null;
        Ensure(
            replayedName == replayName,
            $"redelivered event carries replay-name={Repr(replayedName)}, " +
            $"expected '{replayName}' -- without that field it is indistinguishable from a " +
            $"leftover live delivery: {PyRepr(replayed.Body)}");
        var replayedMarker = replayed.Body["detail"]?["marker"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : null;
        Ensure(
            replayedMarker == marker,
            $"redelivered event's detail.marker={Repr(replayedMarker)} does " +
            $"not match the published marker '{marker}': {PyRepr(replayed.Body)}");
        Ensure(
            replayed.TraceHeader is null,
            $"the replayed SQS delivery must not carry AWSTraceHeader, got {Repr(replayed.TraceHeader)}");
    }

    private static string TokenHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static string Repr(string? s) => s is null ? "None" : $"'{s}'";

    /// <summary>Python's repr() of a parsed JSON value, for the assertion messages.</summary>
    private static string PyRepr(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"'{kv.Key}': {PyRepr(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyRepr)) + "]",
        JsonValue v when v.TryGetValue<string>(out var s) => $"'{s}'",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        _ => node.ToJsonString(),
    };

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
        var timeout = TimeSpan.FromSeconds(timeoutS);
        T? last = default;
        while (started.Elapsed < timeout)
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(Math.Max(0.0, Math.Min(intervalS, (timeout - started.Elapsed).TotalSeconds))));
        }

        if (ctx is not null && BudgetSpent(ctx))
        {
            throw new TimeoutException("condition: the run budget ended first; not graded");
        }

        throw Fail($"condition never met within {timeoutS.ToString(CultureInfo.InvariantCulture)}s, last value={last}");
    }

    /// <summary>The first SQS message whose detail.marker matches, as {"body", "trace_header"}.</summary>
    private static async Task<(JsonObject Body, string? TraceHeader)> WaitForMarker(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var started = FidelityTime.StartNew();
        var timeout = TimeSpan.FromSeconds(timeoutS);
        while (started.Elapsed < timeout)
        {
            var waitS = Math.Max(0, Math.Min(5, (int)(timeout - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = waitS,
                MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if (body["detail"] is JsonObject detail && detail["marker"] is JsonValue v && v.TryGetValue<string>(out var m) && m == marker)
                {
                    return (body, msg.Attributes?.GetValueOrDefault("AWSTraceHeader"));
                }
            }
        }

        if (BudgetSpent(ctx))
        {
            throw new TimeoutException($"marker '{marker}' on the queue: the run budget ended first; not graded");
        }

        throw Fail($"marker '{marker}' never arrived on queue within {timeoutS.ToString(CultureInfo.InvariantCulture)}s");
    }
}
