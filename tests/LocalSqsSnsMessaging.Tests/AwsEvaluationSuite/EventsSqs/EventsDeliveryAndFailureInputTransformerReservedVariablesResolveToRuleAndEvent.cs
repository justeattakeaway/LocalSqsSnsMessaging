// Translated from behavioral/events__sqs/check_events_delivery_and_failure_input_transformer_reserved_variables_resolve_to_rule_and_event.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-delivery-and-failure-input-transformer-reserved-variables-resolve-to-rule-and-event</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsDeliveryAndFailureInputTransformerReservedVariablesResolveToRuleAndEvent
{
    private const string Template =
        "{\"rule\":<aws.events.rule-name>,\"arn\":<aws.events.rule-arn>," +
        "\"ing\":<aws.events.event.ingestion-time>,\"ev\":<aws.events.event>," +
        "\"evj\":<aws.events.event.json>}";

    private static DateTimeOffset ParseTs(string value) =>
        DateTimeOffset.Parse(value.Replace("Z", "+00:00", StringComparison.Ordinal), CultureInfo.InvariantCulture);

    /// <summary>
    /// The reserved InputTransformer variables resolve to THIS rule and THIS event at delivery: rule
    /// name and ARN, the full event, the event without detail, and the bus's ingestion time.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-delivery-and-failure-input-transformer-reserved-variables-resolve-to-rule-and-event")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.edf.");
        var (ruleName, ruleArn) = await Rule(ctx, new JsonObject { ["source"] = new JsonArray(source) });
        var qTx = await Queue(ctx, [ruleArn]);
        var qRaw = await Queue(ctx, [ruleArn]);
        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target { Id = "tx", Arn = qTx.Arn, InputTransformer = new InputTransformer { InputTemplate = Template } },
                new Target { Id = "raw", Arn = qRaw.Arn },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets failed entries: {Repr(resp.FailedEntries)}");

        var now = FidelityTime.UtcNow.AddDays(-1);
        var past = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);
        var txById = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var rawById = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        async Task<List<string>> EmitAndCollect()
        {
            var output = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries =
                [
                    new PutEventsRequestEntry
                    {
                        Source = source,
                        DetailType = "Reserved",
                        Time = past.UtcDateTime,
                        Detail = $$"""{"nonce": "{{Guid.NewGuid():N}}"}""",
                    },
                ],
            });
            Ensure(output.FailedEntryCount == 0, $"PutEvents rejected the event: [{string.Join(", ", (output.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
            foreach (var msg in await Drain(sqs, qTx.Url, seconds: 2))
            {
                var body = JsonNode.Parse(msg.Body)!;
                txById[(string)body["evj"]!["id"]!] = body;
            }

            foreach (var msg in await Drain(sqs, qRaw.Url, seconds: 2))
            {
                var body = JsonNode.Parse(msg.Body)!;
                rawById[(string)body["id"]!] = body;
            }

            return [.. txById.Keys.Where(rawById.ContainsKey).Order(StringComparer.Ordinal)];
        }

        var joined = await PollUntil(
            EmitAndCollect,
            ids => ids.Count >= 1,
            ids => $"[{string.Join(", ", ids.Select(i => $"'{i}'"))}]",
            timeoutS: 180,
            intervalS: 3,
            description: "one event delivered to both the transformed and the control target");
        var eventId = joined[0];
        var (tx, raw) = (txById[eventId], rawById[eventId]);

        Ensure((string?)tx["rule"] == ruleName, $"<aws.events.rule-name> rendered '{tx["rule"]}', rule is '{ruleName}'");
        Ensure((string?)tx["arn"] == ruleArn, $"<aws.events.rule-arn> rendered '{tx["arn"]}', PutRule returned '{ruleArn}'");
        Ensure(
            JsonNode.DeepEquals(tx["evj"], raw),
            "<aws.events.event.json> must equal the full envelope delivered to the control target; " +
            $"got {tx["evj"]?.ToJsonString()} vs control {raw.ToJsonString()}");
        Ensure(tx["evj"] is JsonObject evj && evj.ContainsKey("detail"), $"<aws.events.event.json> must include detail, got {tx["evj"]?.ToJsonString()}");
        Ensure(
            tx["ev"] is JsonObject ev && (string?)ev["id"] == eventId,
            $"<aws.events.event> must be the same event (id '{eventId}'), got {tx["ev"]?.ToJsonString()}");
        Ensure(
            !((JsonObject)tx["ev"]!).ContainsKey("detail"),
            $"<aws.events.event> is documented WITHOUT the detail field, got {tx["ev"]?.ToJsonString()}");

        Ensure(
            ParseTs((string)raw["time"]!) == past,
            $"the envelope must carry the explicit past Time '{past.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}', got '{raw["time"]}'");
        var ingested = ParseTs((string)tx["ing"]!);
        Ensure(
            ingested - past > TimeSpan.FromHours(1),
            $"<aws.events.event.ingestion-time> '{tx["ing"]}' must be the bus receive time, not the " +
            $"event's own time '{raw["time"]}'");
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
