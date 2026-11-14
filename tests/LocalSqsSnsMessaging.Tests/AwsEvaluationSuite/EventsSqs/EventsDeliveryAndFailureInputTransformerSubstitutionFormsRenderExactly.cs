// Translated from behavioral/events__sqs/check_events_delivery_and_failure_input_transformer_substitution_forms_render_exactly.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-delivery-and-failure-input-transformer-substitution-forms-render-exactly</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsDeliveryAndFailureInputTransformerSubstitutionFormsRenderExactly
{
    private const string Order = """{"id": "o-17", "qty": 3, "lines": [{"sku": "a"}]}""";

    /// <summary><c>json.dumps(_DETAIL)</c>, byte for byte.</summary>
    private const string Detail = "{\"order\": {\"id\": \"o-17\", \"qty\": 3, \"lines\": [{\"sku\": \"a\"}]}, \"who\": \"alice\", \"flag\": true, \"quo\": \"say \\\"hi\\\"\"}";

    private const string JsonTemplate =
        "{\"quoted\":\"order <id> by <who>\",\"bare\":<id>,\"qty\":<qty>,\"qtyq\":\"<qty>\"," +
        "\"obj\":<order>,\"flag\":<flag>,\"quo\":<quo>,\"quoq\":\"<quo>\",\"missq\":\"x<miss>y\",\"miss\":<miss>}";

    // The plain-text form: the template is a JSON STRING literal, quotes included. (A bare, unquoted
    // sentence is refused by PutTargets.)
    private const string TextTemplate = "\"order <id> by <who> qty <qty> miss[<miss>]\"";

    private const string ExpectedText = "\"order o-17 by alice qty 3 miss[]\"";

    private static Dictionary<string, string> Paths() => new()
    {
        ["id"] = "$.detail.order.id",
        ["qty"] = "$.detail.order.qty",
        ["order"] = "$.detail.order",
        ["who"] = "$.detail.who",
        ["flag"] = "$.detail.flag",
        ["quo"] = "$.detail.quo",
        ["miss"] = "$.detail.nope",
    };

    private static JsonObject ExpectedJson() => new()
    {
        ["quoted"] = "order o-17 by alice",
        ["bare"] = "o-17",
        ["qty"] = 3,
        ["qtyq"] = "3",
        ["obj"] = JsonNode.Parse(Order),
        ["flag"] = true,
        ["quo"] = "say \"hi\"",
        ["quoq"] = "say \"hi\"",
        ["missq"] = "xy",
        ["miss"] = "",
    };

    /// <summary>
    /// Every InputTransformer substitution form (string, number, object, boolean, escaped quotes,
    /// in-string splice, unresolvable path, plain-text template) renders exactly what EventBridge
    /// renders, compared against an unshaped control copy of the same event.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-delivery-and-failure-input-transformer-substitution-forms-render-exactly")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.edf.");
        var (ruleName, ruleArn) = await Rule(ctx, new JsonObject { ["source"] = new JsonArray(source) });
        var qJson = await Queue(ctx, [ruleArn]);
        var qText = await Queue(ctx, [ruleArn]);
        var qRaw = await Queue(ctx, [ruleArn]);

        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target { Id = "json", Arn = qJson.Arn, InputTransformer = new InputTransformer { InputPathsMap = Paths(), InputTemplate = JsonTemplate } },
                new Target { Id = "text", Arn = qText.Arn, InputTransformer = new InputTransformer { InputPathsMap = Paths(), InputTemplate = TextTemplate } },
                new Target { Id = "raw", Arn = qRaw.Arn },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets failed entries: {Repr(resp.FailedEntries)}");

        var got = new Dictionary<string, List<SqsMessage>> { ["json"] = [], ["text"] = [], ["raw"] = [] };
        var queues = new Dictionary<string, string> { ["json"] = qJson.Url, ["text"] = qText.Url, ["raw"] = qRaw.Url };

        async Task<Dictionary<string, int>> EmitAndCollect()
        {
            var output = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "Transform", Detail = Detail }],
            });
            Ensure(output.FailedEntryCount == 0, $"PutEvents rejected the event: [{string.Join(", ", (output.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
            foreach (var (key, url) in queues)
            {
                got[key].AddRange(await Drain(sqs, url, seconds: 2));
            }

            return got.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        // A brand-new rule drops events until its targets go live, so re-emit the SAME event until
        // every target has received at least one copy. Every copy must render identically.
        await PollUntil(
            EmitAndCollect,
            counts => counts.Values.All(n => n >= 1),
            c => $"{{{string.Join(", ", c.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}",
            timeoutS: 180,
            intervalS: 3,
            description: "every target (json, text, raw) receiving the event");
        foreach (var (key, url) in queues)
        {
            got[key].AddRange(await Drain(sqs, url, seconds: 4));
        }

        var raw = JsonNode.Parse(got["raw"][0].Body)!;
        Ensure(
            JsonNode.DeepEquals(raw["detail"], JsonNode.Parse(Detail)),
            $"control target must receive the untouched envelope with the full detail, got {raw.ToJsonString()}");

        foreach (var msg in got["json"])
        {
            // json.loads: a body that is not JSON raises, as in the Python check.
            var body = JsonNode.Parse(msg.Body)!;
            Ensure(
                JsonNode.DeepEquals(body, ExpectedJson()),
                $"JSON template rendered {body.ToJsonString()}; expected {ExpectedJson().ToJsonString()}");
            Ensure(
                body is JsonObject o && !o.ContainsKey("detail") && !o.ContainsKey("source"),
                $"the transformed body must not carry the raw envelope, got {body.ToJsonString()}");
        }

        foreach (var msg in got["text"])
        {
            Ensure(msg.Body == ExpectedText, $"plain-text template rendered '{msg.Body}'; expected '{ExpectedText}'");
        }
    }

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
