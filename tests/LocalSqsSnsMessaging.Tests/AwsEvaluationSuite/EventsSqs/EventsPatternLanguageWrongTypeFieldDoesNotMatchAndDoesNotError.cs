// Translated from behavioral/events__sqs/check_events_pattern_language_wrong_type_field_does_not_match_and_does_not_error.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-pattern-language-wrong-type-field-does-not-match-and-does-not-error</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPatternLanguageWrongTypeFieldDoesNotMatchAndDoesNotError
{
    private static readonly List<(string Name, string Fragment)> Rules =
    [
        ("number-5", """{"detail": {"k": [5]}}"""),
        ("string-5", """{"detail": {"k": ["5"]}}"""),
        ("boolean-true", """{"detail": {"k": [true]}}"""),
        ("numeric-gt-0", """{"detail": {"k": [{"numeric": [">", 0]}]}}"""),
        ("prefix-12", """{"detail": {"k": [{"prefix": "12"}]}}"""),
        ("cidr-10/8", """{"detail": {"k": [{"cidr": "10.0.0.0/8"}]}}"""),
    ];

    private static readonly List<(string Name, string Detail)> Events =
    [
        ("int-5", """{"k": 5}"""),
        ("float-5", """{"k": 5.0}"""),
        ("str-5", """{"k": "5"}"""),
        ("bool-true", """{"k": true}"""),
        ("str-true", """{"k": "true"}"""),
        ("int-123", """{"k": 123}"""),
        ("str-123", """{"k": "123"}"""),
        ("str-ip", """{"k": "10.1.2.3"}"""),
        ("object", """{"k": {"x": 1}}"""),
    ];

    // The verdicts real AWS gives, both from TestEventPattern and by live delivery.
    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["number-5"] = ["int-5", "float-5"],
        ["string-5"] = ["str-5"],
        ["boolean-true"] = ["bool-true"],
        ["numeric-gt-0"] = ["int-5", "float-5", "int-123"],
        ["prefix-12"] = ["str-123"],
        ["cidr-10/8"] = ["str-ip"],
    };

    /// <summary>
    /// A pattern on a field of another JSON type does not match and PutEvents accepts every entry,
    /// while 5 and 5.0 both match [5].
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-pattern-language-wrong-type-field-does-not-match-and-does-not-error")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var res = await Matrix(ctx, Rules, Events, Expected);
        AssertMatrix(res, Events, Expected);
        var crossed = Rules
            .Select(r => (r.Name, Values: res.Ever[r.Name].Except(Expected[r.Name]).Order(StringComparer.Ordinal).ToList()))
            .Where(x => x.Values.Count > 0)
            .ToList();
        Ensure(
            crossed.Count == 0,
            $"a value of the wrong JSON type matched: {{{string.Join(", ", crossed.Select(x => $"'{x.Name}': {Repr(x.Values)}"))}}}");
        Ensure(
            res.Confirmed["number-5"].SetEquals(["int-5", "float-5"]),
            $"5 and 5.0 are one number: number-5 delivered {Repr(res.Confirmed["number-5"].Order(StringComparer.Ordinal))}");
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

    private static Dictionary<string, string> Tags() => new() { ["sim2real:managed"] = "true", ["sim2real:prefix"] = "sim2real-xsvc" };

    private sealed record Env(AmazonEventBridgeClient Events, AmazonSQSClient Sqs, string Bus, string BusArn, string Source, string Account, string Region);

    private sealed record RuleQueue(string Name, string Arn, JsonObject Pattern, string Url, string QueueArn);

    private sealed record MatrixResult(
        Env Env,
        Dictionary<string, Dictionary<string, bool>> Tep,
        Dictionary<string, HashSet<string>> Confirmed,
        Dictionary<string, HashSet<string>> Ever,
        HashSet<string> ControlCases,
        Dictionary<string, bool> Warm,
        List<string> BodyErrors,
        List<string> PutFailures,
        int Rounds);

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

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
    private static JsonObject Scoped(Env env, string fragment)
    {
        var pattern = new JsonObject { ["source"] = new JsonArray(env.Source), ["detail-type"] = new JsonArray(DetailType) };
        foreach (var (key, value) in JsonNode.Parse(fragment)!.AsObject())
        {
            pattern[key] = value?.DeepClone();
        }

        return pattern;
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
    private static async Task<RuleQueue> Rule(FidelityContext ctx, Env env, JsonObject pattern)
    {
        var events = env.Events;
        var (url, queueArn) = await Queue(ctx, env);
        var name = FidelityContext.UniqueName($"{Prefix}rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = env.Bus, EventPattern = pattern.ToJsonString() })).RuleArn;
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
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await env.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        var resp = await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, EventBusName = env.Bus, Targets = [new Target { Id = "t1", Arn = queueArn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = env.Bus, Ids = ["t1"], Force = true }));
        Ensure(
            (resp.FailedEntryCount ?? 0) == 0,
            $"PutTargets failed: [{string.Join(", ", (resp.FailedEntries ?? []).Select(f => $"{{'TargetId': '{f.TargetId}', 'ErrorCode': '{f.ErrorCode}', 'ErrorMessage': '{f.ErrorMessage}'}}"))}]");
        return new RuleQueue(name, ruleArn, pattern, url, queueArn);
    }

    private static JsonObject Detail(string @case, string detail, int rnd)
    {
        var d = JsonNode.Parse(detail)!.AsObject();
        d["_case"] = @case;
        d["_round"] = rnd;
        return d;
    }

    private static async Task<bool> Tep(Env env, JsonObject pattern, string @case, string detail)
    {
        var @event = new JsonObject
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
        return (await env.Events.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern.ToJsonString(), Event = @event.ToJsonString() })).Result ?? false;
    }

    /// <summary>One PutEvents round carrying every case. Returns the FailedEntries seen (should be none).</summary>
    private static async Task<List<string>> PutRound(Env env, List<(string Name, string Detail)> eventsTable, int rnd)
    {
        var entries = eventsTable
            .Select(e => new PutEventsRequestEntry { Source = env.Source, DetailType = DetailType, EventBusName = env.Bus, Detail = Detail(e.Name, e.Detail, rnd).ToJsonString() })
            .ToList();
        var failed = new List<string>();
        for (var i = 0; i < entries.Count; i += 10)
        {
            var resp = await env.Events.PutEventsAsync(new PutEventsRequest { Entries = [.. entries.Skip(i).Take(10)] });
            failed.AddRange((resp.Entries ?? []).Where(e => e.ErrorCode is not null).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"));
        }

        return failed;
    }

    private static async Task<List<JsonNode?>> DrainOne(AmazonSQSClient sqs, string url, double windowS)
    {
        var @out = new List<JsonNode?>();
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
                    @out.Add(JsonNode.Parse(m.Body));
                }
                catch (JsonException)
                {
                    @out.Add(new JsonObject { ["_unparseable"] = m.Body });
                }
            }

            if (msgs.Count > 0)
            {
                await sqs.DeleteMessageBatchAsync(new DeleteMessageBatchRequest
                {
                    QueueUrl = url,
                    Entries = [.. msgs.Select((m, i) => new DeleteMessageBatchRequestEntry { Id = i.ToString(System.Globalization.CultureInfo.InvariantCulture), ReceiptHandle = m.ReceiptHandle })],
                });
            }
        }

        return @out;
    }

    private static async Task<Dictionary<string, List<JsonNode?>>> DrainAll(Env env, Dictionary<string, RuleQueue> queues, double windowS)
    {
        var futs = queues.ToDictionary(kv => kv.Key, kv => Task.Run(() => DrainOne(env.Sqs, kv.Value.Url, windowS)));
        await Task.WhenAll(futs.Values);
        return futs.ToDictionary(kv => kv.Key, kv => kv.Value.Result);
    }

    /// <summary>Provision every rule row plus a control rule, then deliver every case in rounds.</summary>
    /// <remarks>
    /// Returns evidence only -- the caller asserts. Keys:
    ///   tep[rule][case]      TestEventPattern's verdict for the rule's exact pattern
    ///   confirmed[rule]      cases delivered to the rule's queue in the CONFIRMED round
    ///   ever[rule]           every case the rule's queue received in ANY round
    ///   control              cases the control rule received in the confirmed round
    ///   warm[rule]           True once the rule was seen delivering (live)
    ///   body_errors          delivered envelopes whose content differs from what was put
    ///   put_failures         FailedEntries from PutEvents (should be empty)
    /// The Python helper's optional env and before_delivery hook are unused by this check and left out.
    /// </remarks>
    private static async Task<MatrixResult> Matrix(
        FidelityContext ctx,
        List<(string Name, string Fragment)> rules,
        List<(string Name, string Detail)> eventsTable,
        Dictionary<string, HashSet<string>> expected)
    {
        var env = await Setup(ctx);
        var patterns = rules.Select(r => (r.Name, Pattern: Scoped(env, r.Fragment))).ToList();
        var tep = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var (r, p) in patterns)
        {
            tep[r] = [];
            foreach (var (c, d) in eventsTable)
            {
                tep[r][c] = await Tep(env, p, c, d);
            }
        }

        var queues = new Dictionary<string, RuleQueue> { [Control] = await Rule(ctx, env, Scoped(env, "{}")) };
        foreach (var (r, p) in patterns)
        {
            queues[r] = await Rule(ctx, env, p);
        }

        // (case, round) pairs received per queue, across every round.
        var got = queues.Keys.ToDictionary(k => k, _ => new HashSet<(string Case, int Round)>());
        var bodyErrors = new List<string>();
        var putFailures = new List<string>();
        var details = eventsTable.ToDictionary(e => e.Name, e => e.Detail);

        void Absorb(Dictionary<string, List<JsonNode?>> batch)
        {
            foreach (var (k, bodies) in batch)
            {
                foreach (var b in bodies)
                {
                    var det = b is JsonObject bo ? bo["detail"] : null;
                    if (det is not JsonObject detObj || !detObj.ContainsKey("_case"))
                    {
                        bodyErrors.Add($"{{'queue': '{k}', 'body': {PyRepr(b)}}}");
                        continue;
                    }

                    var @case = detObj["_case"]!.GetValue<string>();
                    var rnd = detObj["_round"]!.GetValue<int>();
                    got[k].Add((@case, rnd));
                    var want = Detail(@case, details.GetValueOrDefault(@case, "{}"), rnd);
                    var envOk = Str(b, "source") == env.Source && Str(b, "detail-type") == DetailType
                        && Str(b, "account") == env.Account && Str(b, "region") == env.Region;
                    if (!JsonNode.DeepEquals(detObj, want) || !envOk)
                    {
                        bodyErrors.Add($"{{'queue': '{k}', 'sent': {PyRepr(want)}, 'delivered': {PyRepr(b)}}}");
                    }
                }
            }
        }

        // The barrier waits on the AUTHORED expectation (real AWS's verdict), never on TEP's.
        var expect = rules.ToDictionary(r => r.Name, r => expected[r.Name].ToHashSet());
        expect[Control] = [.. eventsTable.Select(e => e.Name)];

        HashSet<string> CasesIn(string k, int rnd) => [.. got[k].Where(g => g.Round == rnd).Select(g => g.Case)];

        bool Live(string k)
        {
            var rounds = got[k].Select(g => g.Round).ToHashSet();
            return expect[k].Count == 0 || rounds.Any(r => expect[k].IsSubsetOf(CasesIn(k, r)));
        }

        var rnd = 0;
        for (rnd = 1; rnd <= MaxWarmRounds; rnd++)
        {
            putFailures.AddRange(await PutRound(env, eventsTable, rnd));
            Absorb(await DrainAll(env, queues, RoundWindowS));
            if (queues.Keys.All(Live))
            {
                break;
            }
        }

        // Python's `for rnd in range(1, N + 1)` leaves rnd at N when it runs out, not N + 1.
        rnd = Math.Min(rnd, MaxWarmRounds);
        var warm = queues.Keys.ToDictionary(k => k, Live);

        var confirm = rnd + 1;
        var confirmed = queues.Keys.ToDictionary(k => k, _ => new HashSet<string>());
        if (warm.Values.All(w => w))
        {
            putFailures.AddRange(await PutRound(env, eventsTable, confirm));
            var deadline = FidelityTime.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(ConfirmBudgetS))
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
            tep,
            confirmed.Where(kv => kv.Key != Control).ToDictionary(kv => kv.Key, kv => kv.Value),
            rules.ToDictionary(r => r.Name, r => got[r.Name].Select(g => g.Case).ToHashSet()),
            confirmed[Control],
            warm,
            bodyErrors,
            putFailures,
            confirm);
    }

    private static string? Str(JsonNode? body, string key) =>
        body is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The shared agreement assertions: barrier, content, TEP == AWS verdict == live delivery.</summary>
    private static void AssertMatrix(MatrixResult res, List<(string Name, string Detail)> eventsTable, Dictionary<string, HashSet<string>> expected)
    {
        var cases = eventsTable.Select(e => e.Name).ToList();
        Ensure(res.PutFailures.Count == 0, $"PutEvents rejected entries: [{string.Join(", ", res.PutFailures)}]");
        var cold = res.Warm.Where(kv => !kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        Ensure(
            cold.Count == 0,
            $"rules {Repr(cold)} never delivered their matching events within {MaxWarmRounds} rounds, so " +
            $"no absence on them is attributable to the matcher; per-rule cases seen: " +
            $"{{{string.Join(", ", res.Ever.Select(kv => $"'{kv.Key}': {Repr(kv.Value.Order(StringComparer.Ordinal))}"))}}}");
        Ensure(
            res.ControlCases.SetEquals(cases),
            $"the control rule (source only) did not receive every case of the confirmed round " +
            $"{res.Rounds}: missing {Repr(cases.Except(res.ControlCases).Order(StringComparer.Ordinal))}");
        Ensure(
            res.BodyErrors.Count == 0,
            $"a delivered envelope differs from the event that was put: [{string.Join(", ", res.BodyErrors.Take(3))}]");
        var tepWrong = expected
            .Select(kv => (Rule: kv.Key, Wrong: cases.Where(c => res.Tep[kv.Key][c] != kv.Value.Contains(c)).ToList()))
            .Where(x => x.Wrong.Count > 0)
            .ToList();
        Ensure(
            tepWrong.Count == 0,
            $"TestEventPattern disagreed with the verdicts real AWS returns (rule -> case -> observed): " +
            $"{{{string.Join(", ", tepWrong.Select(x => $"'{x.Rule}': {{{string.Join(", ", x.Wrong.Select(c => $"'{c}': {(res.Tep[x.Rule][c] ? "True" : "False")}"))}}}"))}}}");
        var liveWrong = expected.Where(kv => !res.Confirmed[kv.Key].SetEquals(kv.Value)).ToList();
        Ensure(
            liveWrong.Count == 0,
            $"live delivery disagreed with the expected match set: " +
            $"{{{string.Join(", ", liveWrong.Select(kv => $"'{kv.Key}': {{'expected': {Repr(kv.Value.Order(StringComparer.Ordinal))}, 'delivered': {Repr(res.Confirmed[kv.Key].Order(StringComparer.Ordinal))}}}"))}}}");
        var leaked = expected
            .Select(kv => (Rule: kv.Key, Extra: res.Ever[kv.Key].Except(kv.Value).Order(StringComparer.Ordinal).ToList()))
            .Where(x => x.Extra.Count > 0)
            .ToList();
        Ensure(
            leaked.Count == 0,
            $"a rule delivered a non-matching case in SOME round (absence violated): " +
            $"{{{string.Join(", ", leaked.Select(x => $"'{x.Rule}': {Repr(x.Extra)}"))}}}");
        var split = expected
            .Select(kv => (Rule: kv.Key, Cases: cases.Where(c => res.Tep[kv.Key][c] != res.Confirmed[kv.Key].Contains(c)).ToList()))
            .Where(x => x.Cases.Count > 0)
            .ToList();
        Ensure(
            split.Count == 0,
            $"TestEventPattern and live delivery disagree -- two surfaces of one matcher: " +
            $"{{{string.Join(", ", split.Select(x => $"'{x.Rule}': {Repr(x.Cases)}"))}}}");
        var pos = expected.Values.Sum(v => v.Count);
        var neg = expected.Values.Sum(v => cases.Count - v.Count);
        Ensure(
            pos > 0 && neg > 0 && expected.Values.All(v => v.Count > 0),
            $"the table must keep both directions and a positive per rule: pos={pos} neg={neg} " +
            $"{{{string.Join(", ", expected.Select(kv => $"'{kv.Key}': {Repr(kv.Value.Order(StringComparer.Ordinal))}"))}}}");
    }
}
