// Translated from behavioral/events__sqs/check_events_delivery_and_failure_static_input_and_input_path_replace_the_event.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-delivery-and-failure-static-input-and-input-path-replace-the-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsDeliveryAndFailureStaticInputAndInputPathReplaceTheEvent
{
    private const string Literal = """{"fixed": "literal", "n": 1}""";

    /// <summary><c>json.dumps(_ORDER)</c>.</summary>
    private const string Order = """{"id": "o-17", "qty": 3, "lines": [{"sku": "a"}]}""";

    /// <summary>
    /// A static `Input` replaces the event with a literal, delivered verbatim; `InputPath` replaces
    /// it with the selected subtree -- an object as the object, a string as a JSON string.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-delivery-and-failure-static-input-and-input-path-replace-the-event")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.edf.");
        var (ruleName, ruleArn) = await Rule(ctx, new JsonObject { ["source"] = new JsonArray(source) });
        var qLit = await Queue(ctx, [ruleArn]);
        var qObj = await Queue(ctx, [ruleArn]);
        var qStr = await Queue(ctx, [ruleArn]);
        var qRaw = await Queue(ctx, [ruleArn]);

        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target { Id = "lit", Arn = qLit.Arn, Input = Literal },
                new Target { Id = "obj", Arn = qObj.Arn, InputPath = "$.detail.order" },
                new Target { Id = "str", Arn = qStr.Arn, InputPath = "$.detail.who" },
                new Target { Id = "raw", Arn = qRaw.Arn },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets failed entries: {Repr(resp.FailedEntries)}");

        var queues = new Dictionary<string, string> { ["lit"] = qLit.Url, ["obj"] = qObj.Url, ["str"] = qStr.Url, ["raw"] = qRaw.Url };
        var got = queues.Keys.ToDictionary(k => k, _ => new List<string>());
        var nonces = new HashSet<string>(StringComparer.Ordinal);

        async Task<Dictionary<string, int>> EmitAndCollect()
        {
            var nonce = Guid.NewGuid().ToString("N");
            nonces.Add(nonce);
            var output = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "Replace", Detail = $$"""{"order": {{Order}}, "who": "alice", "nonce": "{{nonce}}"}""" }],
            });
            Ensure(output.FailedEntryCount == 0, $"PutEvents rejected the event: [{string.Join(", ", (output.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
            foreach (var (key, url) in queues)
            {
                got[key].AddRange((await Drain(sqs, url, seconds: 2)).Select(m => m.Body));
            }

            return got.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        await PollUntil(
            EmitAndCollect,
            counts => counts.Values.All(n => n >= 1),
            c => $"{{{string.Join(", ", c.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}",
            timeoutS: 180,
            intervalS: 3,
            description: "every target (lit, obj, str, raw) receiving the event");

        foreach (var body in got["raw"])
        {
            var env = JsonNode.Parse(body)!;
            Ensure(
                (string?)env["source"] == source && nonces.Contains((string)env["detail"]!["nonce"]!),
                $"control target must receive the full envelope of an emitted event, got {env.ToJsonString()}");
        }

        foreach (var body in got["lit"])
        {
            Ensure(body == Literal, $"static Input must be delivered verbatim; expected '{Literal}', got '{body}'");
        }

        foreach (var body in got["obj"])
        {
            Ensure(JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(Order)), $"InputPath $.detail.order must deliver exactly that object, got '{body}'");
        }

        foreach (var body in got["str"])
        {
            Ensure(body == "\"alice\"", $"InputPath $.detail.who must deliver the JSON string '\"alice\"', got '{body}'");
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
