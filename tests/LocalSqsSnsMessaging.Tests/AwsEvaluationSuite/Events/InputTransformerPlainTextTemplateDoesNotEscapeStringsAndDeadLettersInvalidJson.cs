// Translated from behavioral/events/check_input_transformer_plain_text_template_does_not_escape_strings_and_dead_letters_invalid_json.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.input-transformer-plain-text-template-does-not-escape-strings-and-dead-letters-invalid-json</summary>
[Category("AwsEvaluationSuite")]
public sealed class InputTransformerPlainTextTemplateDoesNotEscapeStringsAndDeadLettersInvalidJson
{
    private const string Check = "input-transformer-plain-text-template-does-not-escape-strings-and-dead-letters-invalid-json";
    private const double LivenessTimeoutS = 120.0;
    private const double DeliveryTimeoutS = 120.0;

    private static readonly (string Key, string Value)[] Tricky =
    [
        ("quote", "say \"hi\""),
        ("newline", "line1\nline2\tend"),
        ("backslash", "a\\z"),
        ("unicode", "café ☃"),
        ("markup", "<a>&'"),
    ];

    private static string TrickyValue(string key) => Tricky.Single(t => t.Key == key).Value;

    /// <summary><c>json.dumps({**_TRICKY, "m": m})</c>, byte for byte (json.dumps escapes non-ASCII).</summary>
    private static string TrickyDetail(string m) =>
        $$"""{"quote": "say \"hi\"", "newline": "line1\nline2\tend", "backslash": "a\\z", "unicode": "café ☃", "markup": "<a>&'", "m": "{{m}}"}""";

    private static JsonObject TrickyObject(string m)
    {
        var o = new JsonObject();
        foreach (var (k, v) in Tricky)
        {
            o[k] = v;
        }

        o["m"] = m;
        return o;
    }

    /// <summary>
    /// A JSON InputTemplate escapes the strings it splices so they reach the target intact; a
    /// plain-text template splices them raw, so a quote or newline renders invalid JSON that is
    /// dead-lettered with ERROR_CODE=INVALID_JSON until the target is re-put with a JSON template.
    /// </summary>
    [Test]
    [DisplayName("events.input-transformer-plain-text-template-does-not-escape-strings-and-dead-letters-invalid-json")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.escape.");
        var ruleArn = await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""");
        var rule = ruleArn.Split('/')[^1];
        var (goodUrl, goodArn) = await CreateQueue(ctx);
        var (brokenUrl, brokenArn) = await CreateQueue(ctx);
        var (dlqUrl, dlqArn) = await CreateQueue(ctx);

        var paths = Tricky.ToDictionary(t => t.Key, t => $"$.detail.{t.Key}");
        paths["m"] = "$.detail.m";
        const string jsonTemplate =
            "{\"m\": <m>, \"quote\": <quote>, \"newline\": <newline>, \"backslash\": <backslash>, " +
            "\"unicode\": <unicode>, \"markup\": <markup>, \"quoteText\": \"q=<quote>\", " +
            "\"newlineText\": \"n=<newline>\"}";
        Target Failure(Target t)
        {
            t.DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn };
            t.RetryPolicy = new RetryPolicy { MaximumRetryAttempts = 0 };
            return t;
        }

        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "raw", Arn = goodArn },
                new Target { Id = "json", Arn = goodArn, InputTransformer = new InputTransformer { InputPathsMap = paths, InputTemplate = jsonTemplate } },
                Failure(new Target
                {
                    Id = "text-quote",
                    Arn = brokenArn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new() { ["m"] = "$.detail.m", ["v"] = "$.detail.quote" },
                        InputTemplate = "\"<m> q=<v>\"",
                    },
                }),
                Failure(new Target
                {
                    Id = "text-newline",
                    Arn = brokenArn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new() { ["m"] = "$.detail.m", ["v"] = "$.detail.newline" },
                        InputTemplate = "\"<m> n=<v>\"",
                    },
                }),
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        var live = FidelityContext.UniqueName("live-");
        await PublishUntilLive(ctx, events, goodUrl, new PutEventsRequestEntry
        {
            EventBusName = bus,
            Source = source,
            DetailType = "live",
            Detail = $$"""{"m": "{{live}}", "quote": "", "newline": "", "backslash": "", "unicode": "", "markup": ""}""",
        }, msgs => msgs.Any(m => m.Body.Contains(live, StringComparison.Ordinal)));
        await PurgeUntilEmpty(ctx, brokenUrl);

        var marker = FidelityContext.UniqueName("esc-");
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = "escape", Detail = TrickyDetail(marker) }],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents refused the event: {string.Join(", ", (resp.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})"))}");

        Dictionary<string, JsonObject> Good(List<SqsMessage> msgs)
        {
            var output = new Dictionary<string, JsonObject>();
            foreach (var msg in msgs)
            {
                // json.loads raises on an invalid body; body.get on a non-object raises AttributeError.
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                if (IsString(body["m"], marker))
                {
                    output["json"] = body;
                }
                else if (body["detail"] is JsonObject detail && IsString(detail["m"], marker))
                {
                    output["raw"] = body;
                }
            }

            return output;
        }

        var good = Good(await Drain(ctx, goodUrl, until: ms => Good(ms).Count == 2,
            timeoutS: DeliveryTimeoutS, description: "the JSON and raw targets"));
        Ensure(
            JsonNode.DeepEquals(good["raw"]["detail"], TrickyObject(marker)),
            $"control: the raw target received detail {good["raw"]["detail"]?.ToJsonString()}");
        var expected = TrickyObject(marker);
        expected["quoteText"] = $"q={TrickyValue("quote")}";
        expected["newlineText"] = $"n={TrickyValue("newline")}";
        Ensure(
            JsonNode.DeepEquals(good["json"], expected),
            $"the JSON template must escape every spliced string so the body parses back to the " +
            $"source values: expected {expected.ToJsonString()}, got {good["json"].ToJsonString()}");

        List<SqsMessage> Ours(List<SqsMessage> msgs) => [.. msgs.Where(m => m.Body.Contains(marker, StringComparison.Ordinal))];

        var letters = Ours(await Drain(ctx, dlqUrl, until: ms => Ours(ms).Count >= 2,
            timeoutS: DeliveryTimeoutS,
            description: "both plain-text targets dead-lettering the event"));
        var bodies = letters.Select(m => m.Body).Order(StringComparer.Ordinal).ToList();
        Ensure(
            bodies.SequenceEqual(new[] { $"\"{marker} n={TrickyValue("newline")}\"", $"\"{marker} q={TrickyValue("quote")}\"" }.Order(StringComparer.Ordinal)),
            $"the dead letters must hold the rendered, unescaped plain-text bodies; got [{string.Join(", ", bodies.Select(b => JsonSerializer.Serialize(b)))}]");
        foreach (var msg in letters)
        {
            var attrs = (msg.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => kv.Value.StringValue);
            string? Attr(string key) => attrs.GetValueOrDefault(key);
            Ensure(Attr("ERROR_CODE") == "INVALID_JSON", $"dead letter attributes: {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
            Ensure(
                Attr("ERROR_MESSAGE") == "Invalid input for target.",
                $"dead letter ERROR_MESSAGE was '{Attr("ERROR_MESSAGE")}'");
            Ensure(
                (Attr("RULE_ARN"), Attr("TARGET_ARN")) == (ruleArn, brokenArn),
                $"the dead letter names rule '{Attr("RULE_ARN")}' / target " +
                $"'{Attr("TARGET_ARN")}', expected '{ruleArn}' / '{brokenArn}'");
        }

        var leaked = Ours(await ReceiveFor(ctx, brokenUrl, 10.0));
        Ensure(
            leaked.Count == 0,
            $"the plain-text targets rendered invalid JSON, yet their queue received " +
            $"[{string.Join(", ", leaked.Select(m => JsonSerializer.Serialize(m.Body)))}]");

        // Repair the one broken thing: the same target Id, now with a JSON template.
        var repaired = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                Failure(new Target
                {
                    Id = "text-quote",
                    Arn = brokenArn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new() { ["m"] = "$.detail.m", ["v"] = "$.detail.quote" },
                        InputTemplate = """{"m": <m>, "q": <v>}""",
                    },
                }),
            ],
        });
        Ensure(repaired.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(repaired)}");
        var fixedMarker = FidelityContext.UniqueName("fix-");
        await PublishUntilLive(ctx, events, brokenUrl, new PutEventsRequestEntry
        {
            EventBusName = bus,
            Source = source,
            DetailType = "escape",
            Detail = TrickyDetail(fixedMarker),
        }, msgs => msgs.Any(m => IsJson(m.Body) && IsString(JsonNode.Parse(m.Body)!.AsObject()["m"], fixedMarker)));
    }

    private static bool IsJson(string text)
    {
        try
        {
            JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }

    private static async Task<List<SqsMessage>> ReceiveFor(FidelityContext ctx, string queueUrl, double seconds)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return seen;
    }

    /// <summary>Drop the liveness traffic so only this event's outcome is read later.</summary>
    private static Task PurgeUntilEmpty(FidelityContext ctx, string queueUrl) => ReceiveFor(ctx, queueUrl, 5.0);

    private static bool IsString(JsonNode? node, string expected) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s == expected;

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
