// Translated from behavioral/events__sqs/check_custom_bus_fanout_is_at_least_once_and_order_independent.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.custom-bus-fanout-is-at-least-once-and-order-independent</summary>
[Category("AwsEvaluationSuite")]
public sealed class CustomBusFanoutIsAtLeastOnceAndOrderIndependent
{
    private const int NEvents = 1000;
    private const int NTenants = 20;
    private const string Source = "sim2real.d5";
    private const string DetailType = "sim2real.ordering";
    private const double DrainDeadlineS = 300.0;

    // `sim2real:managed=true` marks every resource for account cleanup, which discovers resources
    // through the Resource Groups Tagging API.
    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real"] = "custom-bus-fanout" };

    private static List<EventBridgeTag> TagsKv() => [.. Tags().Select(kv => new EventBridgeTag { Key = kv.Key, Value = kv.Value })];

    // Four independent pattern semantics over one event space. `prefix` and `anything-but` are
    // complementary on the tenant field, so R1 and R4 must partition the 1,000 events between
    // them — an engine that ignores `anything-but` passes R1 and fails that partition.
    private static readonly (string Rule, string Pattern)[] Patterns =
    [
        ("r1", $$$"""{"source": ["{{{Source}}}"], "detail": {"tenant": [{"prefix": "t-000"}]}}"""),
        ("r2", $$$"""{"source": ["{{{Source}}}"], "detail": {"n": [{"numeric": [">=", 500]}]}}"""),
        ("r3", $$"""{"source": ["{{Source}}"], "detail-type": ["{{DetailType}}"]}"""),
        ("r4", $$$"""{"source": ["{{{Source}}}"], "detail": {"tenant": [{"anything-but": ["t-000"]}]}}"""),
    ];

    private static readonly Dictionary<string, Func<string, int, bool>> Predicates = new(StringComparer.Ordinal)
    {
        ["r1"] = (tenant, n) => tenant.StartsWith("t-000", StringComparison.Ordinal),
        ["r2"] = (tenant, n) => n >= 500,
        ["r3"] = (tenant, n) => true,
        ["r4"] = (tenant, n) => tenant != "t-000",
    };

    private static readonly Dictionary<string, int> ExpectedSizes = new(StringComparer.Ordinal) { ["r1"] = 50, ["r2"] = 500, ["r3"] = 1000, ["r4"] = 950 };

    /// <summary>
    /// 1,000 events fan out to four patterns at-least-once, and the verdict ignores order.
    /// Each rule exercises a different matcher (prefix, numeric range, detail-type, anything-but)
    /// over the same events, and the prefix and anything-but rules must partition the tenant space.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.custom-bus-fanout-is-at-least-once-and-order-independent")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Events;
        var sqs = ctx.Sqs;
        var account = ctx.Account;

        var bus = FidelityContext.UniqueName("sim2real-behav-d5-");
        await ev.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = TagsKv() });
        ctx.OnCleanup(() => ev.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));

        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rule, pattern) in Patterns)
        {
            var (url, arn) = await Queue(sqs, ctx, ctx.Region, account, bus);
            urls[rule] = url;
            var name = $"{bus}-{rule}";
            await ev.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern, State = RuleState.ENABLED });
            ctx.OnCleanup(() => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus }));
            var resp = await ev.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = bus, Targets = [new Target { Id = "q", Arn = arn }] });
            ctx.OnCleanup(() => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ["q"] }));
            Ensure(
                resp.FailedEntryCount == 0,
                $"put_targets for {rule} failed: [{string.Join(", ", (resp.FailedEntries ?? []).Select(e => $"{e.TargetId}: {e.ErrorCode}"))}]");
        }

        await WarmUp(ev, sqs, bus, urls);

        var published = new List<(string EventId, string Tenant, int N)>();
        var expected = Patterns.ToDictionary(p => p.Rule, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        for (var i = 0; i < NEvents; i++)
        {
            var tenant = $"t-{(i % NTenants).ToString("000", CultureInfo.InvariantCulture)}";
            var eventId = $"{bus}-{i.ToString("0000", CultureInfo.InvariantCulture)}";
            published.Add((eventId, tenant, i));
            foreach (var (rule, predicate) in Predicates)
            {
                if (predicate(tenant, i))
                {
                    expected[rule].Add(eventId);
                }
            }
        }

        var sizes = expected.ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);
        Ensure(
            sizes.Count == ExpectedSizes.Count && sizes.All(kv => ExpectedSizes[kv.Key] == kv.Value),
            $"the in-check predicates select {Repr(sizes)}, " +
            $"expected {Repr(ExpectedSizes)} — the workload no longer discriminates the four patterns");
        await Publish(ev, bus, published);

        var deadline = FidelityTime.StartNew();
        var futures = urls.ToDictionary(
            kv => kv.Key,
            kv => Enumerable.Range(0, 3).Select(_ => Task.Run(() => Drain(sqs, kv.Value, expected[kv.Key].Count, deadline))).ToList(),
            StringComparer.Ordinal);
        var observed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (rule, fs) in futures)
        {
            observed[rule] = (await Task.WhenAll(fs)).SelectMany(ids => ids).ToList();
        }

        var allIds = published.Select(p => p.EventId).ToHashSet(StringComparer.Ordinal);
        var verdicts = new Dictionary<string, Verdict>(StringComparer.Ordinal);
        foreach (var (rule, _) in Patterns)
        {
            var got = observed[rule];
            var verdict = VerdictOf(got);
            verdicts[rule] = verdict;
            var missing = expected[rule].Except(verdict.Support).Order(StringComparer.Ordinal).ToList();
            var foreign = verdict.Support.Except(expected[rule]).Order(StringComparer.Ordinal).ToList();
            Ensure(
                missing.Count == 0,
                $"{rule} is missing {missing.Count} of {expected[rule].Count} expected events; " +
                $"first missing {Repr(missing.Take(5))}");
            Ensure(
                foreign.Count == 0,
                $"{rule} received {foreign.Count} events its pattern does not match; first " +
                $"{Repr(foreign.Take(5))}");
            Ensure(
                verdict.Total >= expected[rule].Count,
                $"{rule} received {verdict.Total} messages for {expected[rule].Count} expected events — " +
                $"at-least-once permits more, never fewer (min multiplicity {verdict.MinMultiplicity})");
            Ensure(
                verdict.Support.IsSubsetOf(allIds),
                $"{rule} received ids that were never published: {Repr(verdict.Support.Except(allIds).Order(StringComparer.Ordinal).Take(5))}");
        }

        Ensure(
            verdicts["r3"].Support.Count == NEvents,
            $"the match-everything rule received {verdicts["r3"].Support.Count} of {NEvents} events");
        Ensure(
            !verdicts["r1"].Support.Overlaps(verdicts["r4"].Support),
            "prefix 't-000' and anything-but 't-000' both delivered " +
            $"{verdicts["r1"].Support.Intersect(verdicts["r4"].Support).Count} events — the two matchers do not " +
            "partition the tenant space");
        Ensure(
            verdicts["r1"].Support.Union(verdicts["r4"].Support).SetEquals(verdicts["r3"].Support),
            "R1 and R4 together do not cover exactly what R3 received");

        // LOGGED, NOT ASSERTED — EventBridge neither guarantees nor forbids incidental order
        // preservation, so this is measured and reported, never asserted. Asserting it is exactly
        // the mistake this axis exists to prevent.
        var r3FirstPass = observed["r3"];
        var orderPreserved = r3FirstPass.SequenceEqual(published.Select(p => p.EventId));

        foreach (var (rule, _) in Patterns)
        {
            var shuffled = observed[rule].ToArray();
            Random.Shared.Shuffle(shuffled);
            Ensure(
                !shuffled.SequenceEqual(observed[rule]),
                $"shuffling {rule}'s {shuffled.Length} observations produced the identical list; the " +
                "invariance test would be vacuous");
            Ensure(
                VerdictOf(shuffled).Equals(verdicts[rule]),
                $"{rule}'s verdict function is NOT shuffle-invariant, so this check depends on the " +
                "order EventBridge delivered in — which EventBridge does not define (observed " +
                $"order matched the publish order: {orderPreserved})");
        }
    }

    private static string Repr(IEnumerable<string> ids) => "[" + string.Join(", ", ids.Select(i => $"'{i}'")) + "]";

    private static string Repr(Dictionary<string, int> sizes) =>
        "{" + string.Join(", ", sizes.Select(kv => $"'{kv.Key}': {kv.Value}")) + "}";

    private static async Task<(string Url, string Arn)> Queue(AmazonSQSClient sqs, FidelityContext ctx, string region, string account, string bus)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-d5-"), Tags = Tags(),
        })).QueueUrl;
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
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url, Attributes = new Dictionary<string, string> { ["Policy"] = policy.ToJsonString() },
        });
        return (url, arn);
    }

    /// <summary>Publish in batches of 10; every batch must be wholly accepted.</summary>
    private static async Task Publish(AmazonEventBridgeClient ev, string bus, List<(string EventId, string Tenant, int N)> payloads)
    {
        for (var start = 0; start < payloads.Count; start += 10)
        {
            var batch = payloads.Skip(start).Take(10).ToList();
            var resp = await ev.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    .. batch.Select(p => new PutEventsRequestEntry
                    {
                        EventBusName = bus,
                        Source = Source,
                        DetailType = DetailType,
                        Detail = $$"""{"tenant": "{{p.Tenant}}", "n": {{p.N.ToString(CultureInfo.InvariantCulture)}}, "id": "{{p.EventId}}"}""",
                    }),
                ],
            });
            Ensure(
                resp.FailedEntryCount == 0,
                $"put_events at offset {start} reported {resp.FailedEntryCount} failures: " +
                $"[{string.Join(", ", (resp.Entries ?? []).Select(e => $"{e.EventId} {e.ErrorCode}"))}]");
            var entries = resp.Entries ?? [];
            Ensure(
                entries.Count == batch.Count,
                $"put_events at offset {start} returned {entries.Count} entries for " +
                $"{batch.Count} events");
            Ensure(
                entries.All(entry => !string.IsNullOrEmpty(entry.EventId)),
                $"put_events at offset {start} returned an entry with no EventId: " +
                $"[{string.Join(", ", entries.Select(e => $"{e.EventId} {e.ErrorCode}"))}]");
        }
    }

    private static async Task<List<SqsMessage>> Receive(AmazonSQSClient sqs, string url, int wait = 5)
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

        return msgs;
    }

    /// <summary>The id we published, parsed structurally out of the SNS-less EventBridge envelope.</summary>
    private static string EventId(SqsMessage message) => (string)JsonNode.Parse(message.Body)!["detail"]!["id"]!;

    private static async Task<List<string>> Drain(AmazonSQSClient sqs, string url, int want, FidelityStopwatch deadline)
    {
        var got = new List<string>();
        var idle = 0;
        while (deadline.Elapsed < TimeSpan.FromSeconds(DrainDeadlineS) && got.Count < want)
        {
            var msgs = await Receive(sqs, url);
            if (msgs.Count == 0)
            {
                idle++;
                if (idle >= 8)
                {
                    return got;
                }

                continue;
            }

            idle = 0;
            got.AddRange(msgs.Select(EventId));
        }

        return got;
    }

    /// <summary>Drain to two consecutive empty receives, so a later arrival is a real arrival.</summary>
    private static async Task DrainEmpty(AmazonSQSClient sqs, string url)
    {
        var empties = 0;
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(120) && empties < 2)
        {
            empties = (await Receive(sqs, url, wait: 2)).Count == 0 ? empties + 1 : 0;
        }

        Ensure(empties >= 2, $"queue '{url}' would not drain to empty inside 120 s");
    }

    /// <summary>Publish throwaway events until every queue has proven it can receive one.</summary>
    /// <remarks>
    /// An SQS resource policy becomes effective asynchronously and exposes no condition to poll,
    /// and EventBridge drops — silently, with FailedEntryCount 0 — anything it cannot deliver. So
    /// the warm-up is a bounded retry against an OBSERVABLE condition (a message arriving), not a
    /// sleep. Two tenants are used because R1 (prefix t-000) and R4 (anything-but t-000) are
    /// complementary by construction, so no single event can reach all four queues.
    /// </remarks>
    private static async Task WarmUp(AmazonEventBridgeClient ev, AmazonSQSClient sqs, string bus, Dictionary<string, string> urls)
    {
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        var started = FidelityTime.StartNew();
        var roundNo = 0;
        while (started.Elapsed < TimeSpan.FromSeconds(240) && !confirmed.SetEquals(urls.Keys))
        {
            await Publish(ev, bus, [($"warm-{roundNo}-a", "t-000", 999), ($"warm-{roundNo}-b", "t-001", 999)]);
            roundNo++;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                foreach (var (rule, url) in urls)
                {
                    if ((await Receive(sqs, url, wait: 2)).Count > 0)
                    {
                        confirmed.Add(rule);
                    }
                }

                if (confirmed.SetEquals(urls.Keys))
                {
                    break;
                }
            }
        }

        Ensure(
            confirmed.SetEquals(urls.Keys),
            $"only {Repr(confirmed.Order(StringComparer.Ordinal))} of {Repr(urls.Keys.Order(StringComparer.Ordinal))} queues ever received a warm-up event, so " +
            "a later absence could not be attributed to a pattern");
        foreach (var url in urls.Values)
        {
            await DrainEmpty(sqs, url);
        }
    }

    private sealed record Verdict(ImmutableHashSet<string> Support, int MinMultiplicity, int Total)
    {
        public bool Equals(Verdict? other) =>
            other is not null && Support.SetEquals(other.Support) && MinMultiplicity == other.MinMultiplicity && Total == other.Total;

        public override int GetHashCode() => HashCode.Combine(Support.Count, MinMultiplicity, Total);
    }

    /// <summary>The verdict over one rule's deliveries, by construction independent of arrival order.</summary>
    /// <remarks>
    /// EventBridge is at-least-once and guarantees NO ordering, so the only defensible verdict is
    /// over the multiset's SUPPORT (a duplicate is legal, a drop is not) and its minimum
    /// multiplicity. Asserting shuffle invariance of this function is what stops a later edit from
    /// reintroducing an order dependency that real AWS would fail: it is the one mechanical
    /// defence this axis has against over-strong ordering assertions.
    /// </remarks>
    private static Verdict VerdictOf(IReadOnlyCollection<string> observed)
    {
        var counted = observed.GroupBy(id => id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new Verdict(counted.Keys.ToImmutableHashSet(StringComparer.Ordinal), counted.Count == 0 ? 0 : counted.Values.Min(), observed.Count);
    }
}
