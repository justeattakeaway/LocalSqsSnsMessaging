// Translated from behavioral/events__sqs/check_non_matching_event_pattern_delivers_nothing_to_its_target.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.non-matching-event-pattern-delivers-nothing-to-its-target</summary>
[Category("AwsEvaluationSuite")]
public sealed class NonMatchingEventPatternDeliversNothingToItsTarget
{
    private const string Check = "non-matching-event-pattern-delivers-nothing-to-its-target";
    private const int NEvents = 300;
    private const int NTenants = 20;
    private const int NControlEvents = 60;
    private const string Source = "sim2real.d5";
    private const string DetailType = "sim2real.ordering";

    // The probe rule's first pattern matches no tenant this workload ever publishes; its second
    // matches every one of them. Same rule, same target, same queue, same policy — so the only
    // thing that changes between "nothing arrives" and "everything arrives" is the pattern.
    private const string Never = """{"source": ["sim2real.d5"], "detail": {"tenant": [{"prefix": "zzz-never-"}]}}""";
    private const string Always = """{"source": ["sim2real.d5"], "detail": {"tenant": [{"prefix": "t-0"}]}}""";
    private const string AllEvents = """{"source": ["sim2real.d5"], "detail-type": ["sim2real.ordering"]}""";

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real:check"] = Check };

    private static List<EventBridgeTag> EventBridgeTags() => [.. Tags().Select(kv => new EventBridgeTag { Key = kv.Key, Value = kv.Value })];

    /// <summary>
    /// A rule whose pattern matches none of 300 events delivers none to its queue while a neighbour rule
    /// on the same bus receives all 300; repointed at a matching pattern, the same rule and target then
    /// receive all 60 control events. PutEvents reports FailedEntryCount 0 either way.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.non-matching-event-pattern-delivers-nothing-to-its-target")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var sqs = ctx.Sqs;
        var account = ctx.Account;

        var bus = FidelityContext.UniqueName("sim2real-behav-d5-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = EventBridgeTags() });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var (controlUrl, controlArn) = await Queue(sqs, ctx, ctx.Region, account, bus);
        var (probeUrl, probeArn) = await Queue(sqs, ctx, ctx.Region, account, bus);

        string controlRule = $"{bus}-control", probeRule = $"{bus}-probe";
        foreach (var (rule, pattern, arn) in new[] { (controlRule, AllEvents, controlArn), (probeRule, Never, probeArn) })
        {
            await ev.PutRuleAsync(new PutRuleRequest
            {
                Name = rule,
                EventBusName = bus,
                EventPattern = pattern,
                State = RuleState.ENABLED,
                Tags = EventBridgeTags(),
            });
            ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));
            var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "q", Arn = arn }] });
            ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ["q"] }));
            Ensure(
                (resp.FailedEntryCount ?? 0) == 0,
                $"put_targets for {rule} failed: [{string.Join(", ", (resp.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}"))}]");
        }

        Ensure(
            (await TargetArns(ev, probeRule, bus)).SequenceEqual([probeArn]),
            "the probe rule does not have exactly the probe queue as its target, so a zero would prove nothing");

        await WarmUp(ev, sqs, bus, controlUrl);

        var published = Enumerable.Range(0, NEvents).Select(i => ($"{bus}-{i:D4}", $"t-{i % NTenants:D3}", i)).ToList();
        var ids = published.Select(p => p.Item1).ToHashSet();
        await Publish(ev, bus, published);

        // ---- the live control: every event reaches the neighbouring rule's queue -------------- #
        var delivered = await Collect(sqs, controlUrl, NEvents, 240.0);
        Ensure(
            delivered.ToHashSet().SetEquals(ids),
            $"the control rule received {delivered.ToHashSet().Count} of {NEvents} events, so delivery was " +
            $"not working during the window in which the probe queue stayed empty; missing " +
            $"{Repr(ids.Except(delivered).Order(StringComparer.Ordinal).Take(5))}");

        // ---- the effect is absent: nothing reached the non-matching rule's queue -------------- #
        var leaked = await Collect(sqs, probeUrl, 1, 90.0);
        Ensure(
            leaked.Count == 0,
            $"a rule whose pattern matches no published tenant delivered {leaked.Count} events: {Repr(leaked.Take(5))}");
        var depth = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = probeUrl,
            AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
        })).Attributes ?? [];
        Ensure(
            depth.Count == 2
                && depth.GetValueOrDefault("ApproximateNumberOfMessages") == "0"
                && depth.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible") == "0",
            $"the non-matching rule's queue reports {{{string.Join(", ", depth.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}, not empty");

        // ---- positive control on the SAME rule and target: a matching pattern delivers -------- #
        await ev.PutRuleAsync(new PutRuleRequest { Name = probeRule, EventBusName = bus, EventPattern = Always, State = RuleState.ENABLED });
        Ensure(
            (await TargetArns(ev, probeRule, bus)).SequenceEqual([probeArn]),
            "updating the pattern dropped the rule's target, so the control would be untestable");
        var controlEvents = Enumerable.Range(0, NControlEvents).Select(i => ($"{bus}-c{i:D3}", $"t-{i % NTenants:D3}", i)).ToList();
        var controlIds = controlEvents.Select(p => p.Item1).ToHashSet();
        var arrived = new HashSet<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(240) && !arrived.SetEquals(controlIds))
        {
            await Publish(ev, bus, controlEvents);
            arrived.UnionWith((await Collect(sqs, probeUrl, NControlEvents, 45.0)).Where(controlIds.Contains));
        }

        Ensure(
            arrived.SetEquals(controlIds),
            $"after the SAME rule and target were repointed at a matching pattern, only " +
            $"{arrived.Count} of {NControlEvents} events arrived — the earlier zero cannot be " +
            $"attributed to the pattern; missing {Repr(controlIds.Except(arrived).Order(StringComparer.Ordinal).Take(5))}");
        Ensure(
            !arrived.Overlaps(ids),
            $"the repointed rule replayed events published before the change: " +
            $"{Repr(arrived.Intersect(ids).Order(StringComparer.Ordinal).Take(5))}");
    }

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    private static async Task<List<string>> TargetArns(AmazonEventBridgeClient ev, string rule, string bus) =>
        [.. ((await ev.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets ?? []).Select(t => t.Arn)];

    private static async Task<(string Url, string Arn)> Queue(AmazonSQSClient sqs, FidelityContext ctx, string region, string account, string bus)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName("sim2real-behav-d5-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridge",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject
                {
                    ["ArnLike"] = new JsonObject { ["aws:SourceArn"] = $"arn:aws:events:{region}:{account}:rule/{bus}/*" },
                },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    private static async Task Publish(AmazonEventBridgeClient ev, string bus, List<(string EventId, string Tenant, int N)> payloads)
    {
        for (var start = 0; start < payloads.Count; start += 10)
        {
            var batch = payloads.Skip(start).Take(10);
            var resp = await ev.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    .. batch.Select(p => new PutEventsRequestEntry
                    {
                        EventBusName = bus,
                        Source = Source,
                        DetailType = DetailType,
                        Detail = $$"""{"tenant": "{{p.Tenant}}", "n": {{p.N}}, "id": "{{p.EventId}}"}""",
                    }),
                ],
            });
            Ensure(
                (resp.FailedEntryCount ?? 0) == 0,
                $"put_events at offset {start} reported {resp.FailedEntryCount} failures: " +
                $"{{'FailedEntryCount': {resp.FailedEntryCount}, 'Entries': [{string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'EventId': '{e.EventId}', 'ErrorCode': '{e.ErrorCode}'}}"))}]}}");
        }
    }

    private static async Task<List<string>> Receive(AmazonSQSClient sqs, string url, int wait = 5)
    {
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
        var msgs = resp.Messages ?? [];
        if (msgs.Count > 0)
        {
            await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
            {
                QueueUrl = url,
                Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = $"d{i}", ReceiptHandle = m.ReceiptHandle })],
            });
        }

        return [.. msgs.Select(m => JsonNode.Parse(m.Body)!["detail"]!["id"]!.GetValue<string>())];
    }

    private static async Task<List<string>> Collect(AmazonSQSClient sqs, string url, int want, double budgetS)
    {
        var got = new List<string>();
        var idle = 0;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(budgetS) && got.Count < want)
        {
            var batch = await Receive(sqs, url);
            if (batch.Count == 0)
            {
                idle++;
                if (idle >= 8)
                {
                    return got;
                }

                continue;
            }

            idle = 0;
            got.AddRange(batch);
        }

        return got;
    }

    private static async Task DrainEmpty(AmazonSQSClient sqs, string url)
    {
        var empties = 0;
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(120) && empties < 2)
        {
            empties = (await Receive(sqs, url, wait: 2)).Count == 0 ? empties + 1 : 0;
        }

        Ensure(empties >= 2, $"queue '{url}' would not drain to empty inside 120 s");
    }

    /// <summary>Publish throwaway events until this queue proves it can receive one.</summary>
    /// <remarks>
    /// An SQS resource policy becomes effective asynchronously and exposes no condition to poll,
    /// and EventBridge drops what it cannot deliver with FailedEntryCount 0 — which would look
    /// exactly like the absence this check asserts. So the absence is only meaningful after a
    /// delivery has been OBSERVED through the same bus, rule surface and policy.
    /// </remarks>
    private static async Task WarmUp(AmazonEventBridgeClient ev, AmazonSQSClient sqs, string bus, string url)
    {
        var deadline = FidelityTime.StartNew();
        var roundNo = 0;
        while (deadline.Elapsed < TimeSpan.FromSeconds(240))
        {
            await Publish(ev, bus, [($"warm-{roundNo}", "t-000", 0)]);
            roundNo++;
            for (var i = 0; i < 5; i++)
            {
                if ((await Receive(sqs, url, wait: 2)).Count > 0)
                {
                    await DrainEmpty(sqs, url);
                    return;
                }
            }
        }

        throw Fail(
            "the control queue never received a warm-up event in 240 s, so no later absence could " +
            "be attributed to a pattern");
    }
}
