// Translated from behavioral/events__sqs/check_events_pattern_language_wildcard_star_spans_any_run_including_slashes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-pattern-language-wildcard-star-spans-any-run-including-slashes</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsPatternLanguageWildcardStarSpansAnyRunIncludingSlashes
{
    private static readonly JsonObject Rules = Parse("""
        {
            "two-stars": {"detail": {"k": [{"wildcard": "a*c*e"}]}},
            "path": {"detail": {"k": [{"wildcard": "dir/*.png"}]}},
            "no-star": {"detail": {"k": [{"wildcard": "abcd"}]}}
        }
        """);

    private static readonly JsonObject Events = Parse("""
        {
            "ace": {"k": "ace"},
            "abcde": {"k": "abcde"},
            "abcd": {"k": "abcd"},
            "xace": {"k": "xace"},
            "dir-flat": {"k": "dir/b.png"},
            "dir-nested": {"k": "dir/a/b.png"},
            "dir-jpg": {"k": "dir/b.jpg"}
        }
        """);

    // The verdicts real AWS gives, both from TestEventPattern and by live delivery.
    private static readonly Dictionary<string, HashSet<string>> Expected = new()
    {
        ["two-stars"] = ["ace", "abcde"],
        ["path"] = ["dir-flat", "dir-nested"],
        ["no-star"] = ["abcd"],
    };

    /// <summary>
    /// wildcard * matches any run of characters, empty and / included, and the pattern is anchored at
    /// both ends, with TestEventPattern agreeing.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-pattern-language-wildcard-star-spans-any-run-including-slashes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var res = await Matrix(ctx, Rules, Events, Expected);
        AssertMatrix(res, Events, Expected);
        Ensure(
            res.Confirmed["path"].Contains("dir-nested"),
            $"'*' must cross '/' (not a shell glob): path rule delivered {Repr(Sorted(res.Confirmed["path"]))}");
        Ensure(
            !res.Ever["two-stars"].Contains("xace") && !res.Ever["two-stars"].Contains("abcd"),
            $"'a*c*e' is anchored at both ends; it delivered {Repr(Sorted(res.Ever["two-stars"]))}");
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

    private static Dictionary<string, string> ResourceTags() =>
        new() { ["sim2real:managed"] = "true", ["sim2real:prefix"] = "sim2real-xsvc" };

    private sealed record Env(
        AmazonEventBridgeClient Events, AmazonSQSClient Sqs, string Bus, string BusArn, string Source, string Account, string Region);

    /// <summary>A rule row: {name, pattern, url, arn}. arn is the queue's: the Python's **q overrides the rule ARN.</summary>
    private sealed record RuleQueue(string Name, JsonObject Pattern, string Url, string Arn);

    private sealed record MatrixResult(
        Env Env,
        Dictionary<string, RuleQueue> Queues,
        Dictionary<string, Dictionary<string, bool>> Tep,
        Dictionary<string, HashSet<string>> Confirmed,
        Dictionary<string, HashSet<string>> Ever,
        HashSet<string> Control,
        Dictionary<string, bool> Warm,
        List<JsonObject> BodyErrors,
        List<PutEventsResultEntry> PutFailures,
        int Rounds,
        object? Hook);
    /// <summary>A fresh custom bus and a source only this run can emit. Registers the bus teardown.</summary>
    private static async Task<Env> Setup(FidelityContext ctx)
    {
        var events = ctx.Events;
        var account = ctx.Account;
        var bus = FidelityContext.UniqueName($"{Prefix}bus-");
        var busArn = (await events.CreateEventBusAsync(new CreateEventBusRequest
        {
            Name = bus,
            Tags = [.. ResourceTags().Select(kv => new EventBridgeTag { Key = kv.Key, Value = kv.Value })],
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
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = FidelityContext.UniqueName($"{Prefix}q-"), Tags = ResourceTags() })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = url }));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    /// <summary>Queue + rule (on the check's bus) + queue policy scoped to the rule ARN + target t1.</summary>
    private static async Task<RuleQueue> Rule(FidelityContext ctx, Env env, JsonObject pattern)
    {
        var events = env.Events;
        var q = await Queue(ctx, env);
        var name = FidelityContext.UniqueName($"{Prefix}rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventBusName = env.Bus,
            EventPattern = Dumps(pattern),
        })).RuleArn;
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
        await env.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = q.Url, Attributes = new() { ["Policy"] = Dumps(policy) } });
        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = name,
            EventBusName = env.Bus,
            Targets = [new Target { Id = "t1", Arn = q.Arn }],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = env.Bus, Ids = ["t1"], Force = true }));
        Ensure(
            (resp.FailedEntryCount ?? 0) == 0,
            $"PutTargets failed: {Repr(resp.FailedEntries?.Select(e => new Dictionary<string, object?> { ["TargetId"] = e.TargetId, ["ErrorCode"] = e.ErrorCode, ["ErrorMessage"] = e.ErrorMessage }))}");
        return new RuleQueue(name, pattern, q.Url, q.Arn);
    }

    private static JsonObject Detail(string @case, JsonObject detail, JsonNode? rnd)
    {
        var d = (JsonObject)detail.DeepClone();
        d["_case"] = @case;
        d["_round"] = rnd?.DeepClone();
        return d;
    }

    private static async Task<bool> Tep(Env env, JsonObject pattern, string @case, JsonObject detail)
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
        var resp = await env.Events.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = Dumps(pattern), Event = Dumps(@event) });
        return resp.Result ?? throw new KeyNotFoundException("Result");
    }

    /// <summary>One PutEvents round carrying every case. Returns the FailedEntries seen (should be none).</summary>
    private static async Task<List<PutEventsResultEntry>> PutRound(Env env, JsonObject eventsTable, int rnd)
    {
        var entries = eventsTable.Select(kv => new PutEventsRequestEntry
        {
            Source = env.Source,
            DetailType = DetailType,
            EventBusName = env.Bus,
            Detail = Dumps(Detail(kv.Key, kv.Value!.AsObject(), rnd)),
        }).ToList();
        var failed = new List<PutEventsResultEntry>();
        for (var i = 0; i < entries.Count; i += 10)
        {
            var resp = await env.Events.PutEventsAsync(new PutEventsRequest { Entries = entries.GetRange(i, Math.Min(10, entries.Count - i)) });
            failed.AddRange((resp.Entries ?? []).Where(e => e.ErrorCode is not null));
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
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
            })).Messages ?? [];
            foreach (var m in msgs)
            {
                try
                {
                    @out.Add(JsonNode.Parse(m.Body ?? throw new KeyNotFoundException("Body")));
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
                {
                    @out.Add(new JsonObject { ["_unparseable"] = m.Body });
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

        return @out;
    }

    private static async Task<Dictionary<string, List<JsonNode?>>> DrainAll(Env env, Dictionary<string, RuleQueue> queues, double windowS)
    {
        var futs = queues.ToDictionary(kv => kv.Key, kv => Task.Run(() => DrainOne(env.Sqs, kv.Value.Url, windowS)));
        await Task.WhenAll(futs.Values);
        var results = new Dictionary<string, List<JsonNode?>>();
        foreach (var (k, f) in futs)
        {
            results[k] = await f;
        }

        return results;
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
        FidelityContext ctx, JsonObject rules, JsonObject eventsTable, Dictionary<string, HashSet<string>> expected, Env? env = null,
        Func<Env, Dictionary<string, RuleQueue>, Task<object?>>? beforeDelivery = null)
    {
        env ??= await Setup(ctx);
        var patterns = rules.ToDictionary(kv => kv.Key, kv => Scoped(env, kv.Value!.AsObject()));
        var tep = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var (r, p) in patterns)
        {
            tep[r] = [];
            foreach (var (c, d) in eventsTable)
            {
                tep[r][c] = await Tep(env, p, c, d!.AsObject());
            }
        }

        var queues = new Dictionary<string, RuleQueue> { ["__control__"] = await Rule(ctx, env, Scoped(env, [])) };
        foreach (var (r, p) in patterns)
        {
            queues[r] = await Rule(ctx, env, p);
        }

        var hookResult = beforeDelivery is not null ? await beforeDelivery(env, queues) : null;

        // (case, round) pairs received per queue, across every round.
        var got = queues.Keys.ToDictionary(k => k, _ => new HashSet<(string Case, double Round)>());
        var bodyErrors = new List<JsonObject>();
        var putFailures = new List<PutEventsResultEntry>();

        void Absorb(Dictionary<string, List<JsonNode?>> batch)
        {
            foreach (var (k, bodies) in batch)
            {
                foreach (var b in bodies)
                {
                    var det = b is JsonObject bo ? bo["detail"] : null;
                    if (det is not JsonObject detObj || !detObj.ContainsKey("_case"))
                    {
                        bodyErrors.Add(new JsonObject { ["queue"] = k, ["body"] = b?.DeepClone() });
                        continue;
                    }

                    if (!detObj.ContainsKey("_round"))
                    {
                        throw new KeyNotFoundException("_round");
                    }

                    var (@case, round) = (KeyOf(detObj["_case"]), detObj["_round"]);
                    got[k].Add((@case, NumberOf(round)));
                    var want = Detail(@case, eventsTable[@case] as JsonObject ?? [], round);
                    var bObj = (JsonObject)b!;
                    var envOk = StrEquals(bObj["source"], env.Source) && StrEquals(bObj["detail-type"], DetailType)
                        && StrEquals(bObj["account"], env.Account) && StrEquals(bObj["region"], env.Region);
                    if (!PyEquals(detObj, want) || !envOk)
                    {
                        bodyErrors.Add(new JsonObject { ["queue"] = k, ["sent"] = want, ["delivered"] = bObj.DeepClone() });
                    }
                }
            }
        }

        // The barrier waits on the AUTHORED expectation (real AWS's verdict), never on TEP's.
        var expect = rules.ToDictionary(kv => kv.Key, kv => new HashSet<string>(expected[kv.Key]));
        expect["__control__"] = [.. eventsTable.Select(kv => kv.Key)];

        HashSet<string> CasesIn(string k, double round) => [.. got[k].Where(x => x.Round == round).Select(x => x.Case)];

        bool Live(string k)
        {
            var rounds = got[k].Select(x => x.Round).ToHashSet();
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

        // Python's `for rnd in range(1, N + 1)` leaves rnd at N when the loop never breaks.
        rnd = Math.Min(rnd, MaxWarmRounds);
        var warm = queues.Keys.ToDictionary(k => k, Live);

        var confirm = rnd + 1;
        var confirmed = queues.Keys.ToDictionary(k => k, _ => new HashSet<string>());
        if (warm.Values.All(w => w))
        {
            putFailures.AddRange(await PutRound(env, eventsTable, confirm));
            var confirmStarted = FidelityTime.StartNew();
            while (confirmStarted.Elapsed < TimeSpan.FromSeconds(ConfirmBudgetS))
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
            confirmed.Where(kv => kv.Key != "__control__").ToDictionary(kv => kv.Key, kv => kv.Value),
            rules.ToDictionary(kv => kv.Key, kv => got[kv.Key].Select(x => x.Case).ToHashSet()),
            confirmed["__control__"],
            warm,
            bodyErrors,
            putFailures,
            confirm,
            hookResult);
    }

    /// <summary>The shared agreement assertions: barrier, content, TEP == AWS verdict == live delivery.</summary>
    private static void AssertMatrix(MatrixResult res, JsonObject eventsTable, Dictionary<string, HashSet<string>> expected)
    {
        var cases = eventsTable.Select(kv => kv.Key).ToList();
        Ensure(
            res.PutFailures.Count == 0,
            $"PutEvents rejected entries: {Repr(res.PutFailures.Select(e => new Dictionary<string, object?> { ["ErrorCode"] = e.ErrorCode, ["ErrorMessage"] = e.ErrorMessage }))}");
        var cold = Sorted(res.Warm.Where(kv => !kv.Value).Select(kv => kv.Key));
        Ensure(
            cold.Count == 0,
            $"rules {Repr(cold)} never delivered their matching events within {MaxWarmRounds} rounds, so " +
            $"no absence on them is attributable to the matcher; per-rule cases seen: " +
            $"{Repr(res.Ever.ToDictionary(kv => kv.Key, kv => Sorted(kv.Value)))}");
        Ensure(
            res.Control.SetEquals(cases),
            $"the control rule (source only) did not receive every case of the confirmed round " +
            $"{res.Rounds}: missing {Repr(Sorted(cases.Except(res.Control)))}");
        Ensure(
            res.BodyErrors.Count == 0,
            $"a delivered envelope differs from the event that was put: {Repr(res.BodyErrors.Take(3).ToList())}");
        var tepWrong = expected.Keys
            .Select(r => (r, v: cases.Where(c => res.Tep[r][c] != expected[r].Contains(c)).ToDictionary(c => c, c => res.Tep[r][c])))
            .Where(x => x.v.Count > 0)
            .ToDictionary(x => x.r, x => x.v);
        Ensure(
            tepWrong.Count == 0,
            $"TestEventPattern disagreed with the verdicts real AWS returns (rule -> case -> observed): " +
            $"{Repr(tepWrong)}");
        var liveWrong = expected.Keys
            .Where(r => !res.Confirmed[r].SetEquals(expected[r]))
            .ToDictionary(r => r, r => new Dictionary<string, object?> { ["expected"] = Sorted(expected[r]), ["delivered"] = Sorted(res.Confirmed[r]) });
        Ensure(liveWrong.Count == 0, $"live delivery disagreed with the expected match set: {Repr(liveWrong)}");
        var leaked = expected.Keys
            .Where(r => res.Ever[r].Except(expected[r]).Any())
            .ToDictionary(r => r, r => Sorted(res.Ever[r].Except(expected[r])));
        Ensure(
            leaked.Count == 0,
            $"a rule delivered a non-matching case in SOME round (absence violated): {Repr(leaked)}");
        var split = expected.Keys
            .Select(r => (r, v: cases.Where(c => res.Tep[r][c] != res.Confirmed[r].Contains(c)).ToList()))
            .Where(x => x.v.Count > 0)
            .ToDictionary(x => x.r, x => x.v);
        Ensure(
            split.Count == 0,
            $"TestEventPattern and live delivery disagree -- two surfaces of one matcher: {Repr(split)}");
        var pos = expected.Values.Sum(v => v.Count);
        var neg = expected.Values.Sum(v => cases.Count - v.Count);
        Ensure(
            pos > 0 && neg > 0 && expected.Values.All(v => v.Count > 0),
            $"the table must keep both directions and a positive per rule: pos={pos} neg={neg} " +
            $"{Repr(expected.ToDictionary(kv => kv.Key, kv => Sorted(kv.Value)))}");
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Python's sorted() of strings: code-point order.</summary>
    private static List<string> Sorted(IEnumerable<string> items) => [.. items.Order(StringComparer.Ordinal)];

    /// <summary>A loaded _case value as a set key: the string itself, else its JSON text.</summary>
    private static string KeyOf(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : node?.ToJsonString() ?? "null";

    /// <summary>A loaded _round value as a number (Python compares 1 and 1.0 equal).</summary>
    private static double NumberOf(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture)
            : double.NaN;

    private static bool StrEquals(JsonNode? node, string expected) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == expected;

    /// <summary>Python == on two json.loads results: numbers (and bools, which are ints) compare by value.</summary>
    private static bool PyEquals(JsonNode? a, JsonNode? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (JsonObject oa, JsonObject ob):
                return oa.Count == ob.Count && oa.All(kv => ob.ContainsKey(kv.Key) && PyEquals(kv.Value, ob[kv.Key]));
            case (JsonArray aa, JsonArray ab):
                return aa.Count == ab.Count && aa.Zip(ab).All(p => PyEquals(p.First, p.Second));
            case (JsonValue va, JsonValue vb):
                var (ka, kb) = (va.GetValueKind(), vb.GetValueKind());
                if (ka == JsonValueKind.String || kb == JsonValueKind.String)
                {
                    return ka == kb && va.GetValue<string>() == vb.GetValue<string>();
                }

                if (ka == JsonValueKind.Null || kb == JsonValueKind.Null)
                {
                    return ka == kb;
                }

                return NumericOf(va) == NumericOf(vb);
            default:
                return false;
        }
    }

    private static double NumericOf(JsonValue v) => v.GetValueKind() switch
    {
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture),
    };

    /// <summary>json.dumps(x): Python's ", " and ": " separators and ASCII-only escaping, byte for byte.</summary>
    private static string Dumps(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteJson(sb, node);
        return sb.ToString();
    }

    private static void WriteJson(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (k, v) in o)
                {
                    sb.Append(first ? "" : ", ");
                    first = false;
                    WriteJsonString(sb, k);
                    sb.Append(": ");
                    WriteJson(sb, v);
                }

                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    sb.Append(i == 0 ? "" : ", ");
                    WriteJson(sb, a[i]);
                }

                sb.Append(']');
                break;
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteJsonString(sb, v.GetValue<string>());
                        break;
                    case JsonValueKind.True:
                        sb.Append("true");
                        break;
                    case JsonValueKind.False:
                        sb.Append("false");
                        break;
                    case JsonValueKind.Null:
                        sb.Append("null");
                        break;
                    default:
                        // A number, exactly as the table spells it (10.0 stays 10.0).
                        sb.Append(v.ToJsonString());
                        break;
                }

                break;
        }
    }

    private static void WriteJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || ch > 0x7e)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>Python's repr() of the values these messages print.</summary>
    private static string Repr(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => ReprStr(s),
        int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double d => ReprFloat(d),
        JsonNode node => ReprJson(node),
        ITuple t => "(" + string.Join(", ", Enumerable.Range(0, t.Length).Select(i => Repr(t[i]))) + (t.Length == 1 ? ",)" : ")"),
        IDictionary d => "{" + string.Join(", ", d.Keys.Cast<object>().Select(k => $"{Repr(k)}: {Repr(d[k])}")) + "}",
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Repr)) + "]",
        _ => value.ToString() ?? "None",
    };

    private static string ReprJson(JsonNode node) => node switch
    {
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"{ReprStr(kv.Key)}: {(kv.Value is null ? "None" : ReprJson(kv.Value))}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(x => x is null ? "None" : ReprJson(x))) + "]",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => ReprStr(v.GetValue<string>()),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null => "None",
            _ => v.ToJsonString().IndexOfAny(['.', 'e', 'E']) >= 0
                ? ReprFloat(double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture))
                : v.ToJsonString(),
        },
        _ => node.ToJsonString(),
    };

    private static string ReprFloat(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture).Replace("E", "e", StringComparison.Ordinal);
        return s.IndexOfAny(['.', 'e']) >= 0 || !double.IsFinite(d) ? s : s + ".0";
    }

    private static string ReprStr(string s)
    {
        var quote = s.Contains('\'', StringComparison.Ordinal) && !s.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (var ch in s)
        {
            if (ch == quote || ch == '\\')
            {
                sb.Append('\\').Append(ch);
            }
            else if (ch == '\n')
            {
                sb.Append("\\n");
            }
            else if (ch == '\r')
            {
                sb.Append("\\r");
            }
            else if (ch == '\t')
            {
                sb.Append("\\t");
            }
            else if (ch < 0x20 || ch == 0x7f)
            {
                sb.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.Append(quote).ToString();
    }
}
