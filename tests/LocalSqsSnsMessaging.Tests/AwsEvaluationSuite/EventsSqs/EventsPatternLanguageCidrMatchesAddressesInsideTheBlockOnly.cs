// Translated from behavioral/events__sqs/check_events_pattern_language_cidr_matches_addresses_inside_the_block_only.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-pattern-language-cidr-matches-addresses-inside-the-block-only</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPatternLanguageCidrMatchesAddressesInsideTheBlockOnly
{
    private static Dictionary<string, JsonObject> Rules() => new()
    {
        ["v4-24"] = Obj("""{"detail": {"ip": [{"cidr": "10.0.0.0/24"}]}}"""),
        ["v4-31"] = Obj("""{"detail": {"ip": [{"cidr": "192.0.2.6/31"}]}}"""),
        ["v6-127"] = Obj("""{"detail": {"ip": [{"cidr": "2001:db8::/127"}]}}"""),
    };

    private static Dictionary<string, JsonObject> Events() => new()
    {
        ["net-first"] = Obj("""{"ip": "10.0.0.0"}"""),
        ["net-last"] = Obj("""{"ip": "10.0.0.255"}"""),
        ["net-next"] = Obj("""{"ip": "10.0.1.0"}"""),
        ["pair-below"] = Obj("""{"ip": "192.0.2.5"}"""),
        ["pair-low"] = Obj("""{"ip": "192.0.2.6"}"""),
        ["pair-high"] = Obj("""{"ip": "192.0.2.7"}"""),
        ["pair-above"] = Obj("""{"ip": "192.0.2.8"}"""),
        ["v6-low"] = Obj("""{"ip": "2001:db8::"}"""),
        ["v6-high"] = Obj("""{"ip": "2001:db8::1"}"""),
        ["v6-above"] = Obj("""{"ip": "2001:db8::2"}"""),
        ["not-ip"] = Obj("""{"ip": "ten.zero.zero.one"}"""),
        ["number"] = Obj("""{"ip": 10}"""),
    };

    // The verdicts real AWS gives, both from TestEventPattern and by live delivery.
    // (A /32 or /128 host mask is REFUSED by real AWS -- "IPv4 mask bits must be < 32" -- so the
    // narrowest legal blocks are /31 and /127; the refusal is graded by the malformed-pattern check.)
    private static Dictionary<string, HashSet<string>> Expected() => new()
    {
        ["v4-24"] = ["net-first", "net-last"],
        ["v4-31"] = ["pair-low", "pair-high"],
        ["v6-127"] = ["v6-low", "v6-high"],
    };

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// cidr matches exactly the addresses inside the block (IPv4 /24 and /31, IPv6 /127) and nothing
    /// else, a non-address value included, with TestEventPattern agreeing.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-pattern-language-cidr-matches-addresses-inside-the-block-only")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var (rules, eventsTable, expected) = (Rules(), Events(), Expected());
        var res = await Matrix(ctx, rules, eventsTable, expected);
        AssertMatrix(res, eventsTable, expected);
        var edges = new Dictionary<string, HashSet<string>>
        {
            ["v4-24"] = ["net-next"],
            ["v4-31"] = ["pair-below", "pair-above"],
            ["v6-127"] = ["v6-above"],
        };
        var spill = edges.Where(kv => kv.Value.Overlaps(res.Ever[kv.Key])).ToList();
        Ensure(
            spill.Count == 0,
            $"an address just outside a block was delivered: {{{string.Join(", ", spill.Select(kv => $"'{kv.Key}': {Sorted(kv.Value.Intersect(res.Ever[kv.Key]))}"))}}}");
        var junk = rules.Keys.Where(r => res.Ever[r].Overlaps(["not-ip", "number"])).Order(StringComparer.Ordinal).ToList();
        Ensure(junk.Count == 0, $"a non-address value matched a cidr rule: {Sorted(junk)}");
    }

    // One custom bus per check, one rule per pattern row, one SQS queue per rule, and one CONTROL rule
    // that matches this check's source alone. Every row's events ride in every PutEvents round, so the
    // control queue and each positive-expected row queue act as the differential barrier for that
    // row's absences: a non-matching event is only called "not delivered" in a round in which the
    // same rule has provably delivered its matching events from the same PutEvents batch.
    private const string Prefix = "sim2real-xsvc-";
    private const string DetailType = "sim2real.pattern-language";
    private const double RoundWindowS = 5.0;
    private const int MaxWarmRounds = 16;
    private const double ConfirmBudgetS = 90.0;
    private const double SettleS = 6.0;
    private const string Control = "__control__";

    private static readonly JsonSerializerOptions PyJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real:prefix"] = "sim2real-xsvc" };

    private sealed record Env(AmazonEventBridgeClient Events, AmazonSQSClient Sqs, string Bus, string BusArn, string Source, string Account, string Region);

    private sealed record RuleInfo(string Name, string Arn, JsonObject Pattern, string Url, string QueueArn);

    private sealed record MatrixResult(
        Env Env,
        Dictionary<string, RuleInfo> Queues,
        Dictionary<string, Dictionary<string, bool>> Tep,
        Dictionary<string, HashSet<string>> Confirmed,
        Dictionary<string, HashSet<string>> Ever,
        HashSet<string> Control,
        Dictionary<string, bool> Warm,
        List<string> BodyErrors,
        List<PutEventsResultEntry> PutFailures,
        int Rounds,
        object? Hook);

    /// <summary><c>json.dumps(node)</c>: Python's <c>", "</c> and <c>": "</c> separators.</summary>
    private static string PyDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => JsonSerializer.Serialize(kv.Key, PyJsonOptions) + ": " + PyDumps(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyDumps)) + "]",
        _ => node.ToJsonString(PyJsonOptions),
    };

    private static string Sorted(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Order(StringComparer.Ordinal).Select(v => $"'{v}'"))}]";

    private static string Py(bool value) => value ? "True" : "False";

    /// <summary>A fresh custom bus and a source only this run can emit. Registers the bus teardown.</summary>
    private static async Task<Env> Setup(FidelityContext ctx)
    {
        var events = ctx.Events;
        var account = ctx.Account;
        var bus = FidelityContext.UniqueName($"{Prefix}bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Tags = [.. Tags().Select(kv => new Amazon.EventBridge.Model.Tag { Key = kv.Key, Value = kv.Value })],
        })).EventBusArn;
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var source = FidelityContext.UniqueName("sim2real.plang.");
        return new Env(events, ctx.Sqs, bus, busArn, source, account, ctx.Region);
    }

    /// <summary>A row's matcher, scoped to this run's own source and detail-type.</summary>
    private static JsonObject Scoped(Env env, JsonObject fragment)
    {
        var scoped = new JsonObject { ["source"] = new JsonArray(env.Source), ["detail-type"] = new JsonArray(DetailType) };
        foreach (var (key, value) in fragment)
        {
            scoped[key] = value?.DeepClone();
        }

        return scoped;
    }

    private static async Task<(string Url, string Arn)> Queue(FidelityContext ctx, Env env)
    {
        var sqs = env.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName($"{Prefix}q-"), Tags = Tags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    /// <summary>Queue + rule (on the check's bus) + queue policy scoped to the rule ARN + target t1.</summary>
    private static async Task<RuleInfo> Rule(FidelityContext ctx, Env env, JsonObject pattern)
    {
        var events = env.Events;
        var q = await Queue(ctx, env);
        var name = FidelityContext.UniqueName($"{Prefix}rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = env.Bus, EventPattern = PyDumps(pattern) })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = env.Bus, Force = true }));
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowRule",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = q.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await env.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = q.Url, Attributes = new() { ["Policy"] = PyDumps(policy) } });
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = env.Bus, Targets = [new Target { Id = "t1", Arn = q.Arn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = env.Bus, Ids = ["t1"], Force = true }));
        Ensure(
            (resp.FailedEntryCount ?? 0) == 0,
            $"PutTargets failed: [{string.Join(", ", (resp.FailedEntries ?? []).Select(e => $"{{'TargetId': '{e.TargetId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
        return new RuleInfo(name, ruleArn, pattern, q.Url, q.Arn);
    }

    private static JsonObject Detail(string @case, JsonObject detail, int rnd)
    {
        var d = (JsonObject)detail.DeepClone();
        d["_case"] = @case;
        d["_round"] = rnd;
        return d;
    }

    private static async Task<bool> Tep(Env env, JsonObject pattern, string @case, JsonObject detail)
    {
        var ev = new JsonObject
        {
            ["version"] = "0",
            ["id"] = "7f3c1a52-0000-4000-8000-000000000000",
            ["detail-type"] = DetailType,
            ["source"] = env.Source,
            ["account"] = env.Account,
            ["time"] = "2026-01-01T00:00:00Z",
            ["region"] = env.Region,
            ["resources"] = new JsonArray(),
            ["detail"] = Detail(@case, detail, 0),
        };
        return (await env.Events.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = PyDumps(pattern), Event = PyDumps(ev) })).Result ?? false;
    }

    /// <summary>One PutEvents round carrying every case. Returns the FailedEntries seen (should be none).</summary>
    private static async Task<List<PutEventsResultEntry>> PutRound(Env env, Dictionary<string, JsonObject> eventsTable, int rnd)
    {
        var entries = eventsTable.Select(kv => new PutEventsRequestEntry
        {
            Source = env.Source,
            DetailType = DetailType,
            EventBusName = env.Bus,
            Detail = PyDumps(Detail(kv.Key, kv.Value, rnd)),
        }).ToList();
        var failed = new List<PutEventsResultEntry>();
        for (var i = 0; i < entries.Count; i += 10)
        {
            var resp = await env.Events.PutEventsAsync(new PutEventsRequest { Entries = entries.Skip(i).Take(10).ToList() });
            failed.AddRange((resp.Entries ?? []).Where(e => e.ErrorCode is not null));
        }

        return failed;
    }

    private static async Task<List<JsonNode?>> DrainOne(AmazonSQSClient sqs, string url, double windowS)
    {
        var output = new List<JsonNode?>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window)
        {
            var wait = Math.Max(0, Math.Min(2, (int)(window - started.Elapsed).TotalSeconds));
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = wait })).Messages ?? [];
            foreach (var m in msgs)
            {
                try
                {
                    output.Add(JsonNode.Parse(m.Body));
                }
                catch (JsonException)
                {
                    output.Add(new JsonObject { ["_unparseable"] = m.Body });
                }
            }

            if (msgs.Count > 0)
            {
                await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = url,
                    Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = i.ToString(CultureInfo.InvariantCulture), ReceiptHandle = m.ReceiptHandle })],
                });
            }
        }

        return output;
    }

    private static async Task<Dictionary<string, List<JsonNode?>>> DrainAll(Env env, Dictionary<string, RuleInfo> queues, double windowS)
    {
        var futs = queues.ToDictionary(kv => kv.Key, kv => Task.Run(() => DrainOne(env.Sqs, kv.Value.Url, windowS)));
        await Task.WhenAll(futs.Values);
        return futs.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
    }

    /// <summary>
    /// Provision every rule row plus a control rule, then deliver every case in rounds.
    /// </summary>
    /// <remarks>
    /// Returns evidence only -- the caller asserts. Keys:
    ///   tep[rule][case]      TestEventPattern's verdict for the rule's exact pattern
    ///   confirmed[rule]      cases delivered to the rule's queue in the CONFIRMED round
    ///   ever[rule]           every case the rule's queue received in ANY round
    ///   control              cases the control rule received in the confirmed round
    ///   warm[rule]           True once the rule was seen delivering (live)
    ///   body_errors          delivered envelopes whose content differs from what was put
    ///   put_failures         FailedEntries from PutEvents (should be empty)
    /// </remarks>
    private static async Task<MatrixResult> Matrix(
        FidelityContext ctx,
        Dictionary<string, JsonObject> rules,
        Dictionary<string, JsonObject> eventsTable,
        Dictionary<string, HashSet<string>> expected,
        Env? env = null,
        Func<Env, Dictionary<string, RuleInfo>, Task<object?>>? beforeDelivery = null)
    {
        env ??= await Setup(ctx);
        var patterns = rules.ToDictionary(kv => kv.Key, kv => Scoped(env, kv.Value));
        var tep = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var (r, p) in patterns)
        {
            tep[r] = [];
            foreach (var (c, d) in eventsTable)
            {
                tep[r][c] = await Tep(env, p, c, d);
            }
        }

        var queues = new Dictionary<string, RuleInfo> { [Control] = await Rule(ctx, env, Scoped(env, [])) };
        foreach (var (r, p) in patterns)
        {
            queues[r] = await Rule(ctx, env, p);
        }

        var hookResult = beforeDelivery is not null ? await beforeDelivery(env, queues) : null;

        // (case, round) pairs received per queue, across every round.
        var got = queues.Keys.ToDictionary(k => k, _ => new HashSet<(string Case, int Round)>());
        var bodyErrors = new List<string>();
        var putFailures = new List<PutEventsResultEntry>();

        void Absorb(Dictionary<string, List<JsonNode?>> batch)
        {
            foreach (var (k, bodies) in batch)
            {
                foreach (var b in bodies)
                {
                    var det = b is JsonObject bo ? bo["detail"] as JsonObject : null;
                    if (det is null || !det.ContainsKey("_case"))
                    {
                        bodyErrors.Add($"{{'queue': '{k}', 'body': {b?.ToJsonString() ?? "None"}}}");
                        continue;
                    }

                    var (@case, rnd) = ((string)det["_case"]!, (int)det["_round"]!);
                    got[k].Add((@case, rnd));
                    var want = Detail(@case, eventsTable.GetValueOrDefault(@case) ?? [], rnd);
                    var envOk = (string?)b!["source"] == env.Source && (string?)b["detail-type"] == DetailType
                        && (string?)b["account"] == env.Account && (string?)b["region"] == env.Region;
                    if (!JsonNode.DeepEquals(det, want) || !envOk)
                    {
                        bodyErrors.Add($"{{'queue': '{k}', 'sent': {want.ToJsonString()}, 'delivered': {b.ToJsonString()}}}");
                    }
                }
            }
        }

        // The barrier waits on the AUTHORED expectation (real AWS's verdict), never on TEP's.
        var expect = rules.Keys.ToDictionary(k => k, k => new HashSet<string>(expected[k], StringComparer.Ordinal));
        expect[Control] = new HashSet<string>(eventsTable.Keys, StringComparer.Ordinal);

        HashSet<string> CasesIn(string k, int rnd) => got[k].Where(x => x.Round == rnd).Select(x => x.Case).ToHashSet(StringComparer.Ordinal);

        bool Live(string k)
        {
            var rounds = got[k].Select(x => x.Round).ToHashSet();
            return expect[k].Count == 0 || rounds.Any(r => expect[k].IsSubsetOf(CasesIn(k, r)));
        }

        var round = 0;
        for (var r = 1; r <= MaxWarmRounds; r++)
        {
            round = r;
            putFailures.AddRange(await PutRound(env, eventsTable, r));
            Absorb(await DrainAll(env, queues, RoundWindowS));
            if (queues.Keys.All(Live))
            {
                break;
            }
        }

        var warm = queues.Keys.ToDictionary(k => k, Live);

        var confirm = round + 1;
        var confirmed = queues.Keys.ToDictionary(k => k, _ => new HashSet<string>(StringComparer.Ordinal));
        if (warm.Values.All(w => w))
        {
            putFailures.AddRange(await PutRound(env, eventsTable, confirm));
            var started = FidelityTime.StartNew();
            while (started.Elapsed < TimeSpan.FromSeconds(ConfirmBudgetS))
            {
                Absorb(await DrainAll(env, queues, RoundWindowS));
                if (queues.Keys.All(k => expect[k].IsSubsetOf(CasesIn(k, confirm))))
                {
                    break;
                }
            }

            Absorb(await DrainAll(env, queues, SettleS));
            confirmed = queues.Keys.ToDictionary(k => k, k => CasesIn(k, confirm));
        }

        return new MatrixResult(
            env,
            queues,
            tep,
            confirmed.Where(kv => kv.Key != Control).ToDictionary(kv => kv.Key, kv => kv.Value),
            rules.Keys.ToDictionary(k => k, k => got[k].Select(x => x.Case).ToHashSet(StringComparer.Ordinal)),
            confirmed[Control],
            warm,
            bodyErrors,
            putFailures,
            confirm,
            hookResult);
    }

    /// <summary>The shared agreement assertions: barrier, content, TEP == AWS verdict == live delivery.</summary>
    private static void AssertMatrix(MatrixResult res, Dictionary<string, JsonObject> eventsTable, Dictionary<string, HashSet<string>> expected)
    {
        Ensure(
            res.PutFailures.Count == 0,
            $"PutEvents rejected entries: [{string.Join(", ", res.PutFailures.Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
        var cold = res.Warm.Where(kv => !kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            cold.Count == 0,
            $"rules {Sorted(cold)} never delivered their matching events within {MaxWarmRounds} rounds, so " +
            "no absence on them is attributable to the matcher; per-rule cases seen: " +
            $"{{{string.Join(", ", res.Ever.Select(kv => $"'{kv.Key}': {Sorted(kv.Value)}"))}}}");
        Ensure(
            res.Control.SetEquals(eventsTable.Keys),
            "the control rule (source only) did not receive every case of the confirmed round " +
            $"{res.Rounds}: missing {Sorted(eventsTable.Keys.Except(res.Control))}");
        Ensure(
            res.BodyErrors.Count == 0,
            $"a delivered envelope differs from the event that was put: [{string.Join(", ", res.BodyErrors.Take(3))}]");
        var tepWrong = expected.Keys
            .Select(r => (r, wrong: eventsTable.Keys.Where(c => res.Tep[r][c] != expected[r].Contains(c)).ToDictionary(c => c, c => res.Tep[r][c])))
            .Where(x => x.wrong.Count > 0)
            .ToList();
        Ensure(
            tepWrong.Count == 0,
            "TestEventPattern disagreed with the verdicts real AWS returns (rule -> case -> observed): " +
            $"{{{string.Join(", ", tepWrong.Select(x => $"'{x.r}': {{{string.Join(", ", x.wrong.Select(kv => $"'{kv.Key}': {Py(kv.Value)}"))}}}"))}}}");
        var liveWrong = expected.Keys.Where(r => !res.Confirmed[r].SetEquals(expected[r])).ToList();
        Ensure(
            liveWrong.Count == 0,
            "live delivery disagreed with the expected match set: " +
            $"{{{string.Join(", ", liveWrong.Select(r => $"'{r}': {{'expected': {Sorted(expected[r])}, 'delivered': {Sorted(res.Confirmed[r])}}}"))}}}");
        var leaked = expected.Keys.Where(r => res.Ever[r].Except(expected[r]).Any()).ToList();
        Ensure(
            leaked.Count == 0,
            "a rule delivered a non-matching case in SOME round (absence violated): " +
            $"{{{string.Join(", ", leaked.Select(r => $"'{r}': {Sorted(res.Ever[r].Except(expected[r]))}"))}}}");
        var split = expected.Keys
            .Select(r => (r, cases: eventsTable.Keys.Where(c => res.Tep[r][c] != res.Confirmed[r].Contains(c)).ToList()))
            .Where(x => x.cases.Count > 0)
            .ToList();
        Ensure(
            split.Count == 0,
            "TestEventPattern and live delivery disagree -- two surfaces of one matcher: " +
            $"{{{string.Join(", ", split.Select(x => $"'{x.r}': [{string.Join(", ", x.cases.Select(c => $"'{c}'"))}]"))}}}");
        var pos = expected.Values.Sum(v => v.Count);
        var neg = expected.Values.Sum(v => eventsTable.Count - v.Count);
        Ensure(
            pos > 0 && neg > 0 && expected.Values.All(v => v.Count > 0),
            $"the table must keep both directions and a positive per rule: pos={pos} neg={neg} " +
            $"{{{string.Join(", ", expected.Select(kv => $"'{kv.Key}': {Sorted(kv.Value)}"))}}}");
    }
}
