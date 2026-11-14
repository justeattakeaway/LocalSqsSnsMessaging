// Translated from behavioral/events__sqs/check_events_delivery_and_failure_object_in_quoted_template_string_dead_letters_as_invalid_json.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-delivery-and-failure-object-in-quoted-template-string-dead-letters-as-invalid-json</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsDeliveryAndFailureObjectInQuotedTemplateStringDeadLettersAsInvalidJson
{
    /// <summary><c>json.dumps(_ORDER)</c>.</summary>
    private const string Order = """{"id": "o-17", "qty": 3}""";
    private const string BadTemplate = """{"objq":"<order>","n":<n>}""";
    private const string GoodTemplate = """{"obj":<order>,"n":<n>}""";

    private static Dictionary<string, string> Paths() => new() { ["order"] = "$.detail.order", ["n"] = "$.detail.n" };

    private static RetryPolicy Retry() => new() { MaximumRetryAttempts = 0, MaximumEventAgeInSeconds = 60 };

    private static string BadRender(int n) =>
        // What EventBridge actually hands the target: the object's JSON spliced verbatim between the
        // template's quotes, with no escaping -- so the result is not JSON.
        "{\"objq\":\"" + """{"id":"o-17","qty":3}""" + "\",\"n\":" + n.ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>
    /// An InputTransformer that splices an OBJECT into a quoted JSON string renders invalid JSON;
    /// EventBridge refuses to deliver it, dead-letters it with ERROR_CODE=INVALID_JSON, and a sibling
    /// target on the same rule still receives the same event.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-delivery-and-failure-object-in-quoted-template-string-dead-letters-as-invalid-json")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeDeliveryFailures, "Target DeadLetterConfig is not honoured: an InputTransformer rendering invalid JSON is never dead-lettered with ERROR_CODE=INVALID_JSON")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.edf.");
        var (ruleName, ruleArn) = await Rule(ctx, new JsonObject { ["source"] = new JsonArray(source) });
        var qBad = await Queue(ctx, [ruleArn]);
        var qGood = await Queue(ctx, [ruleArn]);
        var qDlq = await Queue(ctx, [ruleArn]);

        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target
                {
                    Id = "bad", Arn = qBad.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = qDlq.Arn }, RetryPolicy = Retry(),
                    InputTransformer = new InputTransformer { InputPathsMap = Paths(), InputTemplate = BadTemplate },
                },
                new Target
                {
                    Id = "good", Arn = qGood.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = qDlq.Arn }, RetryPolicy = Retry(),
                    InputTransformer = new InputTransformer { InputPathsMap = Paths(), InputTemplate = GoodTemplate },
                },
            ],
        });
        Ensure(
            resp.FailedEntryCount == 0,
            "PutTargets must ACCEPT the quoted-object template (the failure is at delivery), " +
            $"got {Repr(resp.FailedEntries)}");

        var goodNs = new HashSet<int>();
        var dlqMsgs = new List<SqsMessage>();
        var badMsgs = new List<SqsMessage>();
        var counter = 0;

        async Task Emit(int detailN)
        {
            var output = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "Splice", Detail = $$"""{"order": {{Order}}, "n": {{detailN}}}""" }],
            });
            Ensure(output.FailedEntryCount == 0, $"PutEvents rejected the event: [{string.Join(", ", (output.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
        }

        async Task<List<int>> EmitAndCollect()
        {
            counter += 1;
            await Emit(counter);
            foreach (var msg in await Drain(sqs, qGood.Url, seconds: 2))
            {
                var body = JsonNode.Parse(msg.Body)!;
                Ensure(JsonNode.DeepEquals(body["obj"], JsonNode.Parse(Order)), $"healthy sibling rendered {body.ToJsonString()}");
                goodNs.Add((int)body["n"]!);
            }

            dlqMsgs.AddRange(await Drain(sqs, qDlq.Url, seconds: 2));
            badMsgs.AddRange(await Drain(sqs, qBad.Url, seconds: 1));
            var dlqNs = dlqMsgs.Select(m => DlqN(m.Body)).ToHashSet();
            return [.. goodNs.Where(dlqNs.Contains).Order()];
        }

        var common = await PollUntil(
            EmitAndCollect,
            ns => ns.Count >= 1,
            ns => $"[{string.Join(", ", ns)}]",
            timeoutS: 180,
            intervalS: 3,
            description: "one event reaching the healthy sibling AND the DLQ");
        var n = common[0];
        // Keep reading well past the barrier: any delivery to the broken queue would have had time to
        // arrive, because the sibling's copy of the SAME event already did.
        badMsgs.AddRange(await Drain(sqs, qBad.Url, seconds: 10));
        dlqMsgs.AddRange(await Drain(sqs, qDlq.Url, seconds: 2));

        Ensure(
            badMsgs.Count == 0,
            "a target whose rendered input is invalid JSON must receive nothing, got " +
            $"[{string.Join(", ", badMsgs.Select(m => $"'{m.Body}'"))}]");
        Ensure(dlqMsgs.Count > 0, "the DLQ must hold the undeliverable event");
        foreach (var msg in dlqMsgs)
        {
            var attrs = Attrs(msg);
            Ensure(
                attrs.GetValueOrDefault("TARGET_ARN") == qBad.Arn,
                $"only the broken target may dead-letter; DLQ message names '{attrs.GetValueOrDefault("TARGET_ARN")}', " +
                $"broken target is '{qBad.Arn}', healthy is '{qGood.Arn}'");
            Ensure(attrs.GetValueOrDefault("ERROR_CODE") == "INVALID_JSON", $"expected ERROR_CODE=INVALID_JSON, got {AttrsRepr(attrs)}");
            Ensure(
                attrs.GetValueOrDefault("ERROR_MESSAGE") == "Invalid input for target.",
                $"expected ERROR_MESSAGE='Invalid input for target.', got '{attrs.GetValueOrDefault("ERROR_MESSAGE")}'");
            Ensure(attrs.GetValueOrDefault("RULE_ARN") == ruleArn, $"expected RULE_ARN='{ruleArn}', got {AttrsRepr(attrs)}");
        }

        var dlqBodies = dlqMsgs.Select(m => m.Body).ToHashSet(StringComparer.Ordinal);
        Ensure(
            dlqBodies.Contains(BadRender(n)),
            $"the DLQ body must be the rendered (malformed) payload '{BadRender(n)}', got [{string.Join(", ", dlqBodies.Order(StringComparer.Ordinal).Select(b => $"'{b}'"))}]");
        foreach (var body in dlqBodies)
        {
            Ensure(
                !body.Contains("\"source\"", StringComparison.Ordinal) && !body.Contains("\"detail\"", StringComparison.Ordinal),
                $"the DLQ must carry the rendered payload, not the source event envelope: '{body}'");
        }

        // Repair exactly the one broken thing: unquote the placeholder. The same queue must now receive.
        var fixedResp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target
                {
                    Id = "bad", Arn = qBad.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = qDlq.Arn }, RetryPolicy = Retry(),
                    InputTransformer = new InputTransformer { InputPathsMap = Paths(), InputTemplate = GoodTemplate },
                },
            ],
        });
        Ensure(fixedResp.FailedEntryCount == 0, $"repair PutTargets failed: {Repr(fixedResp.FailedEntries)}");
        var repaired = new List<JsonNode>();

        async Task<List<JsonNode>> EmitAndReadBad()
        {
            counter += 1;
            await Emit(counter);
            repaired.AddRange((await Drain(sqs, qBad.Url, seconds: 3)).Select(m => JsonNode.Parse(m.Body)!));
            return repaired;
        }

        await PollUntil(
            EmitAndReadBad,
            bodies => bodies.Count >= 1,
            bodies => $"[{string.Join(", ", bodies.Select(b => b.ToJsonString()))}]",
            timeoutS: 120,
            intervalS: 3,
            description: "the repaired target receiving the unquoted object");
        Ensure(JsonNode.DeepEquals(repaired[0]["obj"], JsonNode.Parse(Order)), $"repaired target rendered {repaired[0].ToJsonString()}");
    }

    /// <summary><c>int(body.rsplit('"n":', 1)[1].rstrip("}"))</c>.</summary>
    private static int DlqN(string body)
    {
        var at = body.LastIndexOf("\"n\":", StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException("IndexError: list index out of range");
        }

        return int.Parse(body[(at + 4)..].TrimEnd('}'), CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string?> Attrs(SqsMessage m) =>
        new((m.MessageAttributes ?? []).Select(kv => KeyValuePair.Create(kv.Key, (string?)kv.Value.StringValue)), StringComparer.Ordinal);

    private static string AttrsRepr(Dictionary<string, string?> attrs) =>
        $"{{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': {(kv.Value is null ? "None" : $"'{kv.Value}'")}"))}}}";

    private static string Repr(List<PutTargetsResultEntry>? entries) =>
        entries is null ? "None" : $"[{string.Join(", ", entries.Select(e => $"{{'TargetId': '{e.TargetId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]";

    // ===== helpers =====

    /// <summary>A bounded retry that RAISES on its deadline; never returns a stale value on timeout.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate, Func<T, string> repr, double timeoutS, double intervalS = 2.0, string description = "condition")
    {
        var started = FidelityTime.StartNew();
        var last = default(T);
        var any = false;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            any = true;
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s; last={(any ? repr(last!) : "None")}");
    }

    private static async Task<(string Name, string Arn)> Rule(FidelityContext ctx, JsonObject pattern)
    {
        var events = ctx.Events;
        var name = FidelityContext.UniqueName("sim2real-edf-");
        var arn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventPattern = pattern.ToJsonString() })).RuleArn;

        async Task Delete()
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = name })).Targets ?? []).Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = name });
        }

        ctx.OnCleanup(Delete);
        return (name, arn);
    }

    /// <summary>A queue whose policy lets EventBridge deliver to it, scoped to exactly these rules.</summary>
    private static async Task<(string Url, string Arn)> Queue(FidelityContext ctx, IEnumerable<string> ruleArns)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-edf-q-"),
            Tags = new() { ["sim2real"] = "events-delivery-and-failure" },
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
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = new JsonArray([.. ruleArns.Select(a => (JsonNode?)a)]) } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>Receive-and-delete for `seconds`. An empty list is a real observation, not a default.</summary>
    private static async Task<List<SqsMessage>> Drain(Amazon.SQS.IAmazonSQS sqs, string url, double seconds)
    {
        var output = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        while (true)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 1,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                output.Add(msg);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }

            if (started.Elapsed >= TimeSpan.FromSeconds(seconds))
            {
                return output;
            }
        }
    }
}
