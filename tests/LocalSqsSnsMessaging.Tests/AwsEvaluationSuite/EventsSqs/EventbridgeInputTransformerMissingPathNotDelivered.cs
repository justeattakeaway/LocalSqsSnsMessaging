// Translated from behavioral/events__sqs/check_eventbridge_input_transformer_missing_path_not_delivered.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.eventbridge-input-transformer-missing-path-not-delivered</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventbridgeInputTransformerMissingPathNotDelivered
{
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// An InputTransformer whose input path is absent from the event delivers the template with the
    /// EMPTY STRING substituted for the unresolvable placeholder, never the raw, untransformed event,
    /// for a missing leaf and a missing intermediate object alike.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.eventbridge-input-transformer-missing-path-not-delivered")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeInput, "InputTransformer substitutes a string path value without its JSON quotes ({\"who\": alice, ...}), so even the control body is not the JSON the template describes and never matches")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var source = XName("src");
        var q = await CreateQueue(ctx);
        var pathsMap = new Dictionary<string, string> { ["who"] = "$.detail.who", ["deep"] = "$.detail.user.name" };
        const string template = """{"who": <who>, "deep": <deep>, "seen": "transformed"}""";
        await PutRuleWithSqsTarget(
            ctx,
            new JsonObject { ["source"] = new JsonArray(source) },
            q,
            target: new Target
            {
                InputTransformer = new InputTransformer
                {
                    InputPathsMap = new Dictionary<string, string>(pathsMap),
                    InputTemplate = template,
                },
            });

        // Unresolvable-path event: matches the rule pattern, but neither $.detail.who (missing leaf)
        // nor $.detail.user.name (missing intermediate object) can be resolved from it.
        var unresolvable = new PutEventsRequestEntry
        {
            Source = source,
            DetailType = "TransformProbe",
            Detail = """{"other": "no-who-field"}""",
        };
        var control = new PutEventsRequestEntry
        {
            Source = source,
            DetailType = "TransformProbe",
            Detail = """{"who": "alice", "user": {"name": "bob"}}""",
        };
        var controlBody = JsonNode.Parse("""{"who": "alice", "deep": "bob", "seen": "transformed"}""");
        var substitutedBody = JsonNode.Parse("""{"who": "", "deep": "", "seen": "transformed"}""");

        // The control is emitted FIRST and on its own, so the rule is proven live before the
        // unresolvable event is measured against it: a brand-new rule silently drops events until the
        // target path goes live, and that drop window is indistinguishable from a transform failure.
        var (ctl, seenDuringControl) = await EmitUntil(
            ctx,
            [control],
            q.Url,
            b => b is JsonObject o && IsString(o["who"], "alice"));
        Ensure(
            ctl.Count >= 1,
            "control: an event carrying both input paths must transform and be delivered");
        Ensure(
            JsonNode.DeepEquals(Body(ctl[0]), controlBody),
            $"transformed control body is wrong: {Body(ctl[0])?.ToJsonString()}");

        // The unresolvable-path event IS delivered on that same live rule, and its body is the
        // template with the empty string substituted -- not null, not the literal placeholder text.
        var (subbed, seenDuringSub) = await EmitUntil(
            ctx,
            [unresolvable],
            q.Url,
            b => b is JsonObject o && IsString(o["seen"], "transformed") && IsString(o["who"], ""));
        Ensure(
            JsonNode.DeepEquals(Body(subbed[0]), substitutedBody),
            "an unresolvable input path must be substituted with the empty string, both for a " +
            "missing leaf ($.detail.who) and for a missing intermediate object " +
            $"($.detail.user.name); got {Body(subbed[0])?.ToJsonString()}");

        var delivered = ctl.Concat(seenDuringControl).Concat(subbed).Concat(seenDuringSub).Select(Body).ToList();
        delivered.AddRange((await Collect(ctx, q.Url, windowS: 20.0)).Select(Body));

        // The NEGATIVE: the raw event is never what arrives. Every message on the queue is one of the
        // two transformed shapes, so there is no raw-event or envelope fallback hiding alongside.
        Ensure(
            delivered.All(b => b is JsonObject),
            $"every delivery must be the JSON the template produced, got [{string.Join(", ", delivered.Select(b => b?.ToJsonString()))}]");
        foreach (var body in delivered.Cast<JsonObject>())
        {
            foreach (var envelopeKey in new[] { "detail", "detail-type", "source", "other" })
            {
                Ensure(
                    !body.ContainsKey(envelopeKey),
                    "the raw event must never be delivered when a path is unresolvable: " +
                    $"'{envelopeKey}' appeared in {body.ToJsonString()}");
            }
        }

        var distinct = delivered.Select(SortedDumps).ToHashSet(StringComparer.Ordinal);
        Ensure(
            distinct.SetEquals([SortedDumps(controlBody), SortedDumps(substitutedBody)]),
            "the queue must carry exactly the two transformed shapes and nothing else, got " +
            $"[{string.Join(", ", distinct.Order(StringComparer.Ordinal).Select(d => $"'{d}'"))}]");

        // ---- configuration-time negative: an undeclared template placeholder IS refused ----
        var negRule = XName("rule-undeclared");
        await events.PutRuleAsync(new PutRuleRequest
        {
            Name = negRule, EventPattern = new JsonObject { ["source"] = new JsonArray(source + ".never") }.ToJsonString(),
        });
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = negRule, Force = true }));

        var err = await FidelityContext.CaptureError(() => events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = negRule,
            Targets =
            [
                new Target
                {
                    Id = "t1",
                    Arn = q.Arn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new Dictionary<string, string> { ["who"] = "$.detail.who" },
                        InputTemplate = """{"who": <who>, "nope": <missing>}""",
                    },
                },
            ],
        }));
        if (err is null)
        {
            throw Fail("an InputTemplate placeholder that InputPathsMap does not declare must be refused");
        }

        Ensure(
            err.Code == "ValidationException",
            "expected ValidationException for an undeclared placeholder, got " +
            $"{err.Code}: '{err.Message}'");
        Ensure(
            err.Message == "InputTemplate for target t1 contains invalid placeholder missing.",
            "the refusal must name the target and the offending placeholder, got " +
            $"'{err.Message}'");
        // effect absent: the refused PutTargets attached nothing
        var refusedTargets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = negRule })).Targets ?? [];
        Ensure(
            refusedTargets.Count == 0,
            $"a refused PutTargets must attach no target: [{string.Join(", ", refusedTargets.Select(t => t.Id))}]");

        // control: declaring that placeholder makes the identical target acceptable, so the refusal
        // was about the undeclared name and not about the target or the template's shape.
        var ok = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = negRule,
            Targets =
            [
                new Target
                {
                    Id = "t1",
                    Arn = q.Arn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new Dictionary<string, string> { ["who"] = "$.detail.who", ["missing"] = "$.detail.absent" },
                        InputTemplate = """{"who": <who>, "nope": <missing>}""",
                    },
                },
            ],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = negRule, Ids = ["t1"] }));
        Ensure(ok.FailedEntryCount == 0, $"control PutTargets reported failures: FailedEntryCount={ok.FailedEntryCount}");
        var attached = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = negRule })).Targets ?? [];
        Ensure(
            attached.Select(t => t.Id).SequenceEqual(["t1"]),
            $"[{string.Join(", ", attached.Select(t => t.Id))}]");

        // ... and the symmetry is one-way: a declared path the template never references is fine, so
        // the rule is "every placeholder must be declared", not "map and template must correspond".
        var unused = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = negRule,
            Targets =
            [
                new Target
                {
                    Id = "t1",
                    Arn = q.Arn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new Dictionary<string, string> { ["who"] = "$.detail.who", ["unused"] = "$.detail.zzz" },
                        InputTemplate = """{"who": <who>}""",
                    },
                },
            ],
        });
        Ensure(
            unused.FailedEntryCount == 0,
            $"an InputPathsMap entry the template never uses must be accepted: FailedEntryCount={unused.FailedEntryCount}");
    }

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == expected;

    /// <summary><c>json.dumps(value, sort_keys=True)</c>.</summary>
    private static string SortedDumps(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{JsonSerializer.Serialize(kv.Key)}: {SortedDumps(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(SortedDumps)) + "]",
        _ => node.ToJsonString(),
    };

    private sealed record Queue(string Name, string Url, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attrs = null)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var createAttrs = new Dictionary<string, string>(attrs ?? []);
        if (fifo)
        {
            createAttrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = createAttrs, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>Parse an SQS message body as JSON, falling back to the raw string.</summary>
    private static JsonNode? Body(SqsMessage msg)
    {
        var raw = msg.Body ?? string.Empty;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    /// <summary>
    /// Drain a queue over a bounded window with long-polling ReceiveMessage(MaxNumberOfMessages=10)
    /// and return EVERY message seen. Never raises and never swallows an assertion.
    /// </summary>
    /// <remarks>
    /// This is the NEGATIVE observable. An empty return is only meaningful once a positive control
    /// has already been observed elsewhere in the check -- that is what proves enough wall-clock and
    /// enough propagation elapsed for a delivery to have happened if the emulator were going to make
    /// one. Used alone it would pass against an emulator that delivers nothing at all.
    /// </remarks>
    private static async Task<List<SqsMessage>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10, bool keep = false)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                if (!keep)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Poll a queue until a message whose parsed body satisfies predicate() arrives.
    /// Returns (matched, others) -- every non-matching message seen along the way is returned too so
    /// the caller can assert on leakage instead of silently discarding it.
    /// </summary>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> WaitFor(
        FidelityContext ctx, string queueUrl, Func<JsonNode?, bool> predicate, double timeoutS = 60.0)
    {
        var matched = new List<SqsMessage>();
        var others = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS) && matched.Count == 0)
        {
            foreach (var msg in await Collect(ctx, queueUrl, windowS: Math.Min(8.0, timeoutS)))
            {
                (predicate(Body(msg)) ? matched : others).Add(msg);
            }
        }

        return (matched, others);
    }

    /// <summary>A queue policy granting one AWS service SendMessage on this queue, scoped by SourceArn.</summary>
    private static JsonObject AllowServiceSend(string queueArn, string service, string sourceArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowServiceSend",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = service },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn } },
        }),
    };

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });

    /// <summary>
    /// Create an EventBridge rule for `pattern`, grant the rule SendMessage on `queue`, and attach
    /// the queue as target t1. Registers remove_targets + delete_rule teardown (reverse order of
    /// creation). Returns the rule name.
    /// </summary>
    private static async Task<string> PutRuleWithSqsTarget(FidelityContext ctx, JsonObject pattern, Queue queue, string? bus = null, Target? target = null)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventPattern = pattern.ToJsonString(),
            EventBusName = bus,
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }));
        await SetQueuePolicy(ctx, queue.Url, AllowServiceSend(queue.Arn, "events.amazonaws.com", ruleArn));
        var tgt = target ?? new Target();
        tgt.Id = "t1";
        tgt.Arn = queue.Arn;
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = name, Targets = [tgt], EventBusName = bus });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ["t1"], EventBusName = bus }));
        return name;
    }

    /// <summary>
    /// PutEvents ALL of `entries` together, repeatedly, until a message whose parsed body satisfies
    /// `predicate` lands on `queue_url`. Returns (matched, others).
    /// </summary>
    /// <remarks>
    /// Emitting every leg on every attempt is what makes the negative sound: a freshly created
    /// rule/target silently drops events until the path goes live and EventBridge never retries a
    /// dropped event, so the control has to be re-emitted -- and if the non-matching legs ride along
    /// on each attempt then, by the time the control arrives, every non-matching event has had at
    /// least as long to arrive as the control did. Non-delivery is therefore suppression, not lag.
    /// </remarks>
    private static async Task<(List<SqsMessage> Matched, List<SqsMessage> Others)> EmitUntil(
        FidelityContext ctx, List<PutEventsRequestEntry> entries, string queueUrl, Func<JsonNode?, bool> predicate,
        int attempts = 10, double pollS = 8.0)
    {
        var events = ctx.Events;
        var others = new List<SqsMessage>();
        for (var i = 0; i < attempts; i++)
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [.. entries] });
            var (got, extra) = await WaitFor(ctx, queueUrl, predicate, timeoutS: pollS);
            others.AddRange(extra);
            if (got.Count > 0)
            {
                return (got, others);
            }
        }

        throw Fail(
            $"control event never delivered to {queueUrl} after {attempts} PutEvents attempts " +
            $"(non-matching messages seen meanwhile: {others.Count}) -- the positive leg of this " +
            "negative-delivery check could not be established, so the negative is unprovable");
    }
}
