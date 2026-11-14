// Translated from behavioral/events__sqs/check_events_delivery_and_failure_conflicting_input_and_input_path_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-delivery-and-failure-conflicting-input-and-input-path-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsDeliveryAndFailureConflictingInputAndInputPathRejected
{
    private const string LitA = """{"v":"a"}""";
    private const string LitB = """{"v":"b"}""";

    /// <summary>
    /// Supplying both `Input` and `InputPath` on one target is refused with ValidationException,
    /// and the refused update has NO effect: the stored target and what it delivers are unchanged.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-delivery-and-failure-conflicting-input-and-input-path-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.edf.");
        var (ruleName, ruleArn) = await Rule(ctx, new JsonObject { ["source"] = new JsonArray(source) });
        var q = await Queue(ctx, [ruleArn]);

        var first = await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "t", Arn = q.Arn, Input = LitA }] });
        Ensure(first.FailedEntryCount == 0, $"PutTargets failed entries: {Repr(first.FailedEntries)}");

        Task<List<string>> Deliveries(Func<List<string>, bool> expectBodiesUntil)
        {
            var bodies = new List<string>();

            async Task<List<string>> EmitAndRead()
            {
                var output = await events.PutEventsAsync(new PutEventsRequest
                {
                    Entries = [new PutEventsRequestEntry { Source = source, DetailType = "Conflict", Detail = """{"k": 1}""" }],
                });
                Ensure(output.FailedEntryCount == 0, $"PutEvents rejected the event: [{string.Join(", ", (output.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
                bodies.AddRange((await Drain(sqs, q.Url, seconds: 2)).Select(m => m.Body));
                return bodies;
            }

            return PollUntil(EmitAndRead, expectBodiesUntil, Repr, timeoutS: 150, intervalS: 3, description: "the target delivering");
        }

        var before = await Deliveries(b => b.Count >= 1);
        Ensure(before.ToHashSet().SetEquals([LitA]), $"target must deliver literal A before the refused update, got {Repr(before)}");
        var storedBefore = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName })).Targets ?? [];

        string? code = null;
        string? message = null;
        var err = await FidelityContext.CaptureError(() => events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets = [new Target { Id = "t", Arn = q.Arn, Input = LitB, InputPath = "$.detail" }],
        }));
        if (err is not null)
        {
            (code, message) = (err.Code, err.Message ?? "");
        }

        Ensure(
            code == "ValidationException",
            $"Input together with InputPath must be refused with ValidationException, got {(code is null ? "None" : $"'{code}'")}");
        Ensure(
            message == "Only one of Input, InputPath, or InputTransformer must be provided for target t.",
            $"unexpected ValidationException message '{message}'");

        var storedAfter = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName })).Targets ?? [];
        Ensure(
            Repr(storedAfter) == Repr(storedBefore),
            $"a refused PutTargets must leave the stored target untouched: {Repr(storedBefore)} -> {Repr(storedAfter)}");
        // Effect absent at the destination: after the refusal, deliveries remain literal A.
        await Drain(sqs, q.Url, seconds: 2);
        var after = await Deliveries(b => b.Count >= 2);
        Ensure(
            after.ToHashSet().SetEquals([LitA]),
            $"after the refused update the target must still deliver literal A only, got {Repr(after)}");

        // Positive control: remove the one conflicting field and the update lands.
        var ok = await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "t", Arn = q.Arn, Input = LitB }] });
        Ensure(ok.FailedEntryCount == 0, $"valid update failed: {Repr(ok.FailedEntries)}");
        var switched = await Deliveries(b => b.Contains(LitB));
        Ensure(switched.ToHashSet().IsSubsetOf([LitA, LitB]), $"unexpected bodies after the valid update: {Repr(switched)}");
    }

    private static string Repr(List<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    private static string Repr(List<PutTargetsResultEntry>? entries) =>
        entries is null ? "None" : $"[{string.Join(", ", entries.Select(e => $"{{'TargetId': '{e.TargetId}', 'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]";

    /// <summary>A stored target's fields as boto3 returns them, for comparing and printing.</summary>
    private static string Repr(List<Target> targets) =>
        $"[{string.Join(", ", targets.Select(t =>
        {
            var parts = new List<string> { $"'Id': '{t.Id}'", $"'Arn': '{t.Arn}'" };
            if (t.Input is not null)
            {
                parts.Add($"'Input': '{t.Input}'");
            }

            if (t.InputPath is not null)
            {
                parts.Add($"'InputPath': '{t.InputPath}'");
            }

            if (t.InputTransformer is not null)
            {
                parts.Add($"'InputTransformer': {{'InputPathsMap': {{{string.Join(", ", (t.InputTransformer.InputPathsMap ?? []).Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}, 'InputTemplate': '{t.InputTransformer.InputTemplate}'}}");
            }

            if (t.RoleArn is not null)
            {
                parts.Add($"'RoleArn': '{t.RoleArn}'");
            }

            if (t.DeadLetterConfig is not null)
            {
                parts.Add($"'DeadLetterConfig': {{'Arn': '{t.DeadLetterConfig.Arn}'}}");
            }

            if (t.RetryPolicy is not null)
            {
                parts.Add($"'RetryPolicy': {{'MaximumRetryAttempts': {t.RetryPolicy.MaximumRetryAttempts}, 'MaximumEventAgeInSeconds': {t.RetryPolicy.MaximumEventAgeInSeconds}}}");
            }

            return $"{{{string.Join(", ", parts)}}}";
        }))}]";

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
