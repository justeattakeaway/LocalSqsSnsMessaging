// Translated from behavioral/events/check_twelve_rules_matching_one_event_each_deliver_their_own_copy_and_a_near_miss_rule_none.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.twelve-rules-matching-one-event-each-deliver-their-own-copy-and-a-near-miss-rule-none</summary>
[Category("AwsEvaluationSuite")]
public sealed class TwelveRulesMatchingOneEventEachDeliverTheirOwnCopyAndANearMissRuleNone
{
    private const string Check = "twelve-rules-matching-one-event-each-deliver-their-own-copy-and-a-near-miss-rule-none";
    private const double LivenessTimeoutS = 180.0;
    private const double DeliveryTimeoutS = 90.0;

    // Twelve different patterns that every one match the measured event below, written in the
    // pattern-language forms customers mix on one bus. The measured event is
    // source=<src>, detail-type "order.placed", detail {"m": <marker>, "qty": 5, "tier": "gold",
    // "tags": ["a", "b"], "ip": "10.0.3.7"}.
    private static readonly (string Label, string Extra)[] Matching =
    [
        ("by-source", "{}"),
        ("by-detail-type", """{"detail-type": ["order.placed"]}"""),
        ("by-prefix", """{"detail-type": [{"prefix": "order."}]}"""),
        ("by-suffix", """{"detail-type": [{"suffix": ".placed"}]}"""),
        ("by-numeric", """{"detail": {"qty": [{"numeric": [">", 1, "<=", 5]}]}}"""),
        ("by-anything-but", """{"detail": {"tier": [{"anything-but": ["bronze"]}]}}"""),
        ("by-exists", """{"detail": {"tier": [{"exists": true}]}}"""),
        ("by-array-element", """{"detail": {"tags": ["b"]}}"""),
        ("by-cidr", """{"detail": {"ip": [{"cidr": "10.0.0.0/16"}]}}"""),
        ("by-ignore-case", """{"detail": {"tier": [{"equals-ignore-case": "GOLD"}]}}"""),
        ("by-wildcard", """{"detail-type": [{"wildcard": "order.*"}]}"""),
        ("by-or", """{"$or": [{"detail": {"qty": [99]}}, {"detail": {"tier": ["gold"]}}]}"""),
    ];

    // A near-miss: the same source and detail-type, a tier the event does not carry. Its own
    // control event (tier "silver") proves the rule is live and routes to the same queue.
    private const string NearMiss = """{"detail-type": ["order.placed"], "detail": {"tier": ["silver"]}}""";

    /// <summary>
    /// Twelve rules in different pattern forms targeting one queue each deliver their own copy of
    /// one event, all carrying its EventId, while a live near-miss rule on that queue delivers none.
    /// </summary>
    [Test]
    [DisplayName("events.twelve-rules-matching-one-event-each-deliver-their-own-copy-and-a-near-miss-rule-none")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeInput, "InputTransformer leaves the predefined variable <aws.events.rule-name> unsubstituted, so the delivered body ({\"rule\": <aws.events.rule-name>, ...}) is not JSON")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.fanout.");
        var (queueUrl, queueArn) = await CreateQueue(ctx);

        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, extra) in Matching.Append(("near-miss", NearMiss)))
        {
            var pattern = new JsonObject { ["source"] = new JsonArray(source) };
            foreach (var (key, value) in JsonNode.Parse(extra)!.AsObject())
            {
                pattern[key] = value?.DeepClone();
            }

            var name = await CreateRule(ctx, events, bus, pattern);
            rules[label] = name;
            // <aws.events.rule-name> stamps each copy with its rule, so one queue tells them apart.
            var put = await events.PutTargetsAsync(new PutTargetsRequest
            {
                Rule = name,
                EventBusName = bus,
                Targets =
                [
                    new Target
                    {
                        Id = "q",
                        Arn = queueArn,
                        InputTransformer = new InputTransformer
                        {
                            InputPathsMap = new Dictionary<string, string> { ["id"] = "$.id", ["m"] = "$.detail.m" },
                            InputTemplate = """{"rule": <aws.events.rule-name>, "id": <id>, "m": <m>}""",
                        },
                    },
                ],
            });
            Ensure(put.FailedEntryCount == 0, $"PutTargets on '{label}' failed: [{string.Join(", ", (put.FailedEntries ?? []).Select(e => $"{e.TargetId}: {e.ErrorCode} {e.ErrorMessage}"))}]");
        }

        var matching = Matching.Select(m => rules[m.Label]).ToHashSet(StringComparer.Ordinal);
        var nearMiss = rules["near-miss"];

        PutEventsRequestEntry Entry(string marker, string tier) => new()
        {
            EventBusName = bus,
            Source = source,
            DetailType = "order.placed",
            Detail = $$"""{"m": "{{marker}}", "qty": 5, "tier": "{{tier}}", "tags": ["a", "b"], "ip": "10.0.3.7"}""",
        };

        // Liveness: a just-created rule can drop events until its routing is live, so re-publish a
        // gold control event until all twelve rules have delivered it, and a silver one until the
        // near-miss rule has delivered it.
        await PublishUntil(ctx, events, queueUrl, m => Entry(m, "gold"), matching, "gold control");
        await PublishUntil(ctx, events, queueUrl, m => Entry(m, "silver"), [nearMiss], "silver control");

        var marker = FidelityContext.UniqueName("measured-");
        var resp = await events.PutEventsAsync(new PutEventsRequest { Entries = [Entry(marker, "gold")] });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the measured event: [{string.Join(", ", (resp.Entries ?? []).Select(e => $"{e.EventId} {e.ErrorCode} {e.ErrorMessage}"))}]");
        var eventId = resp.Entries![0].EventId;

        var copies = await Drain(ctx, queueUrl, marker, until: got => got.Select(Rule).ToHashSet(StringComparer.Ordinal).IsSupersetOf(matching),
            timeoutS: DeliveryTimeoutS);
        var byRule = copies.Select(Rule).ToHashSet(StringComparer.Ordinal);
        if (!byRule.SetEquals(matching))
        {
            var missing = rules.Where(r => matching.Contains(r.Value) && !byRule.Contains(r.Value)).Select(r => r.Key).Order(StringComparer.Ordinal);
            throw Fail(
                $"the measured event must reach the queue once per matching rule: {matching.Count} rules " +
                $"matched, copies came from {byRule.Intersect(matching).Count()} of them; missing " +
                $"[{string.Join(", ", missing.Select(l => $"'{l}'"))}], near-miss " +
                $"rule delivered: {byRule.Contains(nearMiss)}");
        }

        var ids = copies.Select(c => (string?)c["id"]).ToHashSet();
        Ensure(
            ids.SetEquals([eventId]),
            $"every copy must carry the one EventId PutEvents returned; {ids.Count} distinct id(s), " +
            $"match: {ids.Contains(eventId)}");

        // The near-miss rule is live (it delivered its silver control) and shares the queue, so a
        // copy of the gold event from it would have arrived alongside the twelve. Drain a little
        // longer to catch a late one.
        var late = await Drain(ctx, queueUrl, marker, until: _ => false, timeoutS: 15.0, allowTimeout: true);
        Ensure(
            !copies.Concat(late).Select(Rule).Contains(nearMiss),
            "the rule whose pattern requires tier 'silver' delivered the tier-'gold' event");
    }

    private static string? Rule(JsonObject copy) => (string?)copy["rule"];

    private static async Task<string> CreateBus(FidelityContext ctx, AmazonEventBridgeClient events)
    {
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new EventBridgeTag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        return bus;
    }

    private static async Task<string> CreateRule(FidelityContext ctx, AmazonEventBridgeClient events, string bus, JsonObject pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern.ToJsonString(), State = RuleState.ENABLED });

        async Task Delete()
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name, EventBusName = bus })).Targets ?? []).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus });
        }

        ctx.OnCleanup(Delete);
        return name;
    }

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-q-"),
            Tags = new Dictionary<string, string> { ["sim2real"] = Check },
        })).QueueUrl;
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
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new Dictionary<string, string> { ["Policy"] = policy.ToJsonString() },
        });
        return (url, arn);
    }

    private static async Task<List<JsonObject>> Receive(FidelityContext ctx, string queueUrl)
    {
        var sqs = ctx.Sqs;
        var output = new List<JsonObject>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
        foreach (var msg in resp.Messages ?? [])
        {
            // json.loads: a body that is not JSON raises, as in the check.
            output.Add(JsonNode.Parse(msg.Body)!.AsObject());
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
        }

        return output;
    }

    /// <summary>Copies of the event carrying <paramref name="marker"/>, received until <paramref name="until"/>(copies) holds.</summary>
    private static async Task<List<JsonObject>> Drain(FidelityContext ctx, string queueUrl, string marker, Func<List<JsonObject>, bool> until,
        double timeoutS, bool allowTimeout = false)
    {
        var got = new List<JsonObject>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            got.AddRange((await Receive(ctx, queueUrl)).Where(c => (string?)c["m"] == marker));
            if (until(got))
            {
                return got;
            }
        }

        if (allowTimeout)
        {
            return got;
        }

        throw Fail($"copies of the measured event still incomplete after {timeoutS:F1}s: {got.Count} received");
    }

    /// <summary>Publish fresh control events until every rule in <paramref name="wantRules"/> has delivered one.</summary>
    private static async Task PublishUntil(FidelityContext ctx, AmazonEventBridgeClient events, string queueUrl,
        Func<string, PutEventsRequestEntry> makeEntry, HashSet<string> wantRules, string what)
    {
        var seen = new HashSet<string?>(StringComparer.Ordinal);
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(LivenessTimeoutS))
        {
            var marker = FidelityContext.UniqueName("live-");
            await events.PutEventsAsync(new PutEventsRequest { Entries = [makeEntry(marker)] });
            var window = FidelityTime.StartNew();
            while (window.Elapsed < TimeSpan.FromSeconds(15.0))
            {
                seen.UnionWith((await Receive(ctx, queueUrl)).Where(c => ((string?)c["m"] ?? "").StartsWith("live-", StringComparison.Ordinal)).Select(Rule));
                if (seen.IsSupersetOf(wantRules))
                {
                    return;
                }
            }
        }

        throw Fail(
            $"the {what} event never reached the queue through {wantRules.Count(r => !seen.Contains(r))} of " +
            $"{wantRules.Count} rule(s) within {LivenessTimeoutS:F1}s");
    }
}
