// Translated from behavioral/events__sqs/check_events_delivery_and_failure_unquoted_plain_text_template_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-delivery-and-failure-unquoted-plain-text-template-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsDeliveryAndFailureUnquotedPlainTextTemplateRejected
{
    private const string Bare = "order <id> by <who>";
    private const string Quoted = "\"order <id> by <who>\"";

    private static Dictionary<string, string> Paths() => new() { ["id"] = "$.detail.id", ["who"] = "$.detail.who" };

    /// <summary>
    /// PutTargets refuses a bare-sentence InputTemplate with ValidationException and its exact
    /// message and stores no target; the same sentence as a JSON string literal is accepted and
    /// delivers the rendered text.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-delivery-and-failure-unquoted-plain-text-template-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.edf.");
        var (ruleName, ruleArn) = await Rule(ctx, new JsonObject { ["source"] = new JsonArray(source) });
        var qText = await Queue(ctx, [ruleArn]);
        var qRaw = await Queue(ctx, [ruleArn]);

        var ctl = await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "raw", Arn = qRaw.Arn }] });
        Ensure(ctl.FailedEntryCount == 0, $"control PutTargets failed: {Repr(ctl.FailedEntries)}");

        string? code = null;
        string? message = null;
        var err = await FidelityContext.CaptureError(() => events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets = [new Target { Id = "text", Arn = qText.Arn, InputTransformer = new InputTransformer { InputPathsMap = Paths(), InputTemplate = Bare } }],
        }));
        if (err is not null)
        {
            (code, message) = (err.Code, err.Message ?? "");
        }

        Ensure(
            code == "ValidationException",
            $"a bare-sentence InputTemplate must be refused with ValidationException, got {(code is null ? "None" : $"'{code}'")}");
        // The service parses the template with every placeholder read as null.
        var parsed = Bare.Replace("<id>", "null", StringComparison.Ordinal).Replace("<who>", "null", StringComparison.Ordinal);
        var expected = $"Invalid InputTemplate for target text : [Source: (String)\"{parsed}\"; line: 1, column: 1].";
        Ensure(message == expected, $"unexpected ValidationException message '{message}'");
        var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName })).Targets ?? []).Select(t => t.Id).Order(StringComparer.Ordinal).ToList();
        Ensure(ids.SequenceEqual(["raw"]), $"the refused target must not be stored; rule has targets [{string.Join(", ", ids.Select(i => $"'{i}'"))}]");

        var rawGot = new List<SqsMessage>();
        var textGot = new List<SqsMessage>();

        async Task<int> EmitAndCollect()
        {
            var output = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "Text", Detail = """{"id": "o-17", "who": "alice"}""" }],
            });
            Ensure(output.FailedEntryCount == 0, $"PutEvents rejected the event: [{string.Join(", ", (output.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
            rawGot.AddRange(await Drain(sqs, qRaw.Url, seconds: 2));
            textGot.AddRange(await Drain(sqs, qText.Url, seconds: 1));
            return rawGot.Count;
        }

        await PollUntil(EmitAndCollect, n => n >= 2, n => $"{n}", timeoutS: 150, intervalS: 3, description: "the control target receiving two events");
        textGot.AddRange(await Drain(sqs, qText.Url, seconds: 5));
        Ensure(
            textGot.Count == 0,
            $"the refused target's queue must receive nothing, got [{string.Join(", ", textGot.Select(m => $"'{m.Body}'"))}]");

        var ok = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets = [new Target { Id = "text", Arn = qText.Arn, InputTransformer = new InputTransformer { InputPathsMap = Paths(), InputTemplate = Quoted } }],
        });
        Ensure(ok.FailedEntryCount == 0, $"quoted template must be accepted: {Repr(ok.FailedEntries)}");

        var delivered = new List<string>();

        async Task<List<string>> EmitAndReadText()
        {
            await EmitAndCollect();
            delivered.AddRange((await Drain(sqs, qText.Url, seconds: 1)).Select(m => m.Body));
            delivered.AddRange(textGot.Select(m => m.Body));
            textGot.Clear();
            return delivered;
        }

        await PollUntil(
            EmitAndReadText,
            b => b.Count >= 1,
            b => $"[{string.Join(", ", b.Select(x => $"'{x}'"))}]",
            timeoutS: 150,
            intervalS: 3,
            description: "the quoted plain-text target delivering");
        foreach (var body in delivered)
        {
            Ensure(body == "\"order o-17 by alice\"", $"quoted plain-text template delivered '{body}'");
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
