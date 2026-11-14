// Translated from behavioral/events/check_put_events_trace_header_reaches_sqs_targets_as_aws_trace_header.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-trace-header-reaches-sqs-targets-as-aws-trace-header</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsTraceHeaderReachesSqsTargetsAsAwsTraceHeader
{
    private const string Check = "put-events-trace-header-reaches-sqs-targets-as-aws-trace-header";
    private const double LivenessTimeoutS = 120.0;
    private const double DeliveryTimeoutS = 90.0;

    /// <summary>
    /// A well-formed X-Ray TraceHeader on a PutEvents entry reaches raw and input-transformed SQS
    /// targets as the AWSTraceHeader attribute; entries without one, or with a malformed one, arrive
    /// without it. A malformed header is still accepted by PutEvents.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-trace-header-reaches-sqs-targets-as-aws-trace-header")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.trace.");
        var ruleArn = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""");
        var rule = ruleArn.Split('/')[^1];
        var (queueUrl, queueArn) = await CreateQueue(ctx);
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "raw", Arn = queueArn },
                new Target
                {
                    Id = "shaped",
                    Arn = queueArn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new() { ["m"] = "$.detail.m", ["id"] = "$.id" },
                        InputTemplate = """{"shaped": <m>, "id": <id>}""",
                    },
                },
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        var live = FidelityContext.UniqueName("live-");
        await PublishUntilLive(ctx, events, queueUrl, new PutEventsRequestEntry
        {
            EventBusName = bus,
            Source = source,
            DetailType = "live",
            Detail = $$"""{"m": "{{live}}"}""",
        }, msgs => msgs.Any(m => m.Body.Contains(live, StringComparison.Ordinal)));

        var trace =
            $"Root=1-{FidelityTime.UtcNow.ToUnixTimeSeconds():x8}-{RandomNumberGenerator.GetHexString(24, lowercase: true)};" +
            $"Parent={RandomNumberGenerator.GetHexString(16, lowercase: true)};Sampled=1";
        string[] kinds = ["traced", "plain", "malformed"];
        var markers = kinds.ToDictionary(k => k, k => FidelityContext.UniqueName($"{k}-"));
        var headers = new Dictionary<string, string> { ["traced"] = trace, ["malformed"] = "sim2real-not-a-trace-header" };
        var entries = new List<PutEventsRequestEntry>();
        foreach (var kind in kinds)
        {
            var entry = new PutEventsRequestEntry
            {
                EventBusName = bus,
                Source = source,
                DetailType = "trace",
                Detail = $$"""{"m": "{{markers[kind]}}"}""",
            };
            if (headers.TryGetValue(kind, out var header))
            {
                entry.TraceHeader = header;
            }

            entries.Add(entry);
        }

        // One request for all three, so a missing header on the untraced deliveries is not lag.
        var resp = await events.PutEventsAsync(new PutEventsRequest { Entries = entries });
        var respEntries = resp.Entries ?? throw new KeyNotFoundException("'Entries'");
        Ensure(
            resp.FailedEntryCount == 0 && respEntries.All(e => !string.IsNullOrEmpty(e.EventId)),
            $"PutEvents must accept all three entries, the malformed trace header included: " +
            $"[{string.Join(", ", respEntries.Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})"))}]");
        var ids = kinds.Zip(respEntries).ToDictionary(p => p.First, p => p.Second.EventId);

        (string Kind, string Form)? Label(SqsMessage msg)
        {
            var body = JsonNode.Parse(msg.Body)!.AsObject();
            JsonNode? m;
            string form;
            if (body.ContainsKey("shaped"))
            {
                (m, form) = (body["shaped"], "shaped");
            }
            else
            {
                (m, form) = ((body["detail"] ?? new JsonObject()).AsObject()["m"], "raw");
            }

            var kind = markers.FirstOrDefault(kv => m is JsonValue v && v.TryGetValue<string>(out var s) && s == kv.Value).Key;
            return kind is not null ? (kind, form) : null;
        }

        bool Complete(List<SqsMessage> msgs)
        {
            var labels = msgs.Select(Label).ToHashSet();
            return kinds.SelectMany(k => new[] { "raw", "shaped" }.Select(f => ((string, string)?)(k, f))).All(labels.Contains);
        }

        var msgs = await Drain(ctx, queueUrl, until: Complete, timeoutS: DeliveryTimeoutS,
            description: "all three events reaching both targets");
        var seen = new Dictionary<(string Kind, string Form), HashSet<string?>>();
        foreach (var msg in msgs)
        {
            if (Label(msg) is not { } label)
            {
                continue;
            }

            var body = JsonNode.Parse(msg.Body)!;
            var bodyId = body["id"] ?? throw new KeyNotFoundException("'id'");
            Ensure(
                bodyId is JsonValue idValue && idValue.TryGetValue<string>(out var id) && id == ids[label.Kind],
                $"the {label} delivery carries event id {bodyId.ToJsonString()}, not the EventId PutEvents " +
                $"returned for that entry ('{ids[label.Kind]}')");
            if (!seen.TryGetValue(label, out var set))
            {
                seen[label] = set = [];
            }

            set.Add(msg.Attributes?.GetValueOrDefault("AWSTraceHeader"));
        }

        static string Sorted(HashSet<string?> values) =>
            $"[{string.Join(", ", values.Select(v => v ?? "None").Order(StringComparer.Ordinal).Select(v => $"'{v}'"))}]";

        foreach (var form in new[] { "raw", "shaped" })
        {
            Ensure(
                seen[("traced", form)].SetEquals([trace]),
                $"the {form} target's copy of the traced event must carry AWSTraceHeader '{trace}', " +
                $"got {Sorted(seen[("traced", form)])}");
            Ensure(
                seen[("plain", form)].SetEquals([null]),
                $"the {form} target's copy of the event sent without a TraceHeader carries " +
                $"AWSTraceHeader {Sorted(seen[("plain", form)])}");
            Ensure(
                seen[("malformed", form)].SetEquals([null]),
                $"the {form} target's copy of the event with a malformed TraceHeader must arrive " +
                $"without AWSTraceHeader, got {Sorted(seen[("malformed", form)])}");
        }
    }

    private static string FailedEntries(PutTargetsResponse put) =>
        $"[{string.Join(", ", (put.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}"))}]";

    private static async Task<string> CreateBus(FidelityContext ctx, AmazonEventBridgeClient events)
    {
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        return bus;
    }

    /// <summary>A rule whose teardown first removes whatever targets it still carries.</summary>
    private static async Task<string> CreateRule(FidelityContext ctx, AmazonEventBridgeClient events, string bus, string pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        var arn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern, State = RuleState.ENABLED })).RuleArn;

        async Task Delete()
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets
                ?? throw new KeyNotFoundException("'Targets'")).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus });
        }

        ctx.OnCleanup(Delete);
        return arn;
    }

    /// <summary>A queue EventBridge may send to, as (url, arn).</summary>
    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-q-");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attributes ?? [], Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = ctx.Account } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Receive (and delete) until <c>until(messages_so_far)</c> holds; raise on timeout.</summary>
    private static async Task<List<SqsMessage>> Drain(
        FidelityContext ctx, string queueUrl, Func<List<SqsMessage>, bool> until, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
#pragma warning disable CS0618 // The check deliberately uses the legacy AttributeNames parameter.
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                AttributeNames = ["All"],
                MessageAttributeNames = ["All"],
            });
#pragma warning restore CS0618
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }

            if (until(seen))
            {
                return seen;
            }
        }

        throw Fail(
            $"{description}: not met within {timeoutS}s; received {seen.Count} message(s): " +
            $"[{string.Join(", ", seen.Select(m => $"'{m.Body[..Math.Min(300, m.Body.Length)]}'"))}]");
    }

    /// <summary>
    /// Re-publish <c>entry</c> until the queue shows it: a just-created rule can drop events until
    /// its routing is live. Bounded; raises naming the timeout.
    /// </summary>
    private static async Task PublishUntilLive(
        FidelityContext ctx, AmazonEventBridgeClient events, string queueUrl, PutEventsRequestEntry entry, Func<List<SqsMessage>, bool> arrived)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(LivenessTimeoutS))
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
            var window = started.Elapsed + TimeSpan.FromSeconds(15.0);
            while (started.Elapsed < window)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
                foreach (var msg in resp.Messages ?? [])
                {
                    seen.Add(msg);
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }

                if (arrived(seen))
                {
                    return;
                }
            }
        }

        throw Fail(
            $"the rule never delivered a liveness event within {LivenessTimeoutS}s; " +
            $"{seen.Count} other message(s) seen");
    }
}
