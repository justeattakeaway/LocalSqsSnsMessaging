// Translated from behavioral/events__sqs/check_events_delivery_and_failure_deleted_queue_target_dead_letters_no_resource_while_sibling_delivers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-delivery-and-failure-deleted-queue-target-dead-letters-no-resource-while-sibling-delivers</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsDeliveryAndFailureDeletedQueueTargetDeadLettersNoResourceWhileSiblingDelivers
{
    private static RetryPolicy Retry() => new() { MaximumRetryAttempts = 0, MaximumEventAgeInSeconds = 60 };

    /// <summary>
    /// When a rule's SQS target queue is deleted out from under it, EventBridge dead-letters each
    /// matched event for THAT target with ERROR_CODE=NO_RESOURCE, while the healthy sibling target on
    /// the same rule keeps receiving the very same events.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.events-delivery-and-failure-deleted-queue-target-dead-letters-no-resource-while-sibling-delivers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeDeliveryFailures, "Target DeadLetterConfig is not honoured: an event for a target whose queue was deleted is dropped instead of being dead-lettered with ERROR_CODE=NO_RESOURCE")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var source = FidelityContext.UniqueName("sim2real.edf.");
        var (ruleName, ruleArn) = await Rule(ctx, new JsonObject { ["source"] = new JsonArray(source) });
        var qVictim = await Queue(ctx, [ruleArn]);
        var qOk = await Queue(ctx, [ruleArn]);
        var qDlq = await Queue(ctx, [ruleArn]);

        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            Targets =
            [
                new Target { Id = "victim", Arn = qVictim.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = qDlq.Arn }, RetryPolicy = Retry() },
                new Target { Id = "ok", Arn = qOk.Arn, DeadLetterConfig = new DeadLetterConfig { Arn = qDlq.Arn }, RetryPolicy = Retry() },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutTargets failed entries: {Repr(resp.FailedEntries)}");

        async Task<string> Emit()
        {
            var output = await events.PutEventsAsync(new PutEventsRequest
            {
                Entries = [new PutEventsRequestEntry { Source = source, DetailType = "Victim", Detail = $$"""{"nonce": "{{Guid.NewGuid():N}}"}""" }],
            });
            Ensure(output.FailedEntryCount == 0, $"PutEvents rejected the event: [{string.Join(", ", (output.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}', 'ErrorMessage': '{e.ErrorMessage}'}}"))}]");
            return output.Entries![0].EventId;
        }

        // Phase 1: both targets live and delivering.
        var seen = new Dictionary<string, List<SqsMessage>> { ["victim"] = [], ["ok"] = [] };

        async Task<Dictionary<string, int>> EmitAndCollectBoth()
        {
            await Emit();
            seen["victim"].AddRange(await Drain(sqs, qVictim.Url, seconds: 2));
            seen["ok"].AddRange(await Drain(sqs, qOk.Url, seconds: 2));
            return seen.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        await PollUntil(
            EmitAndCollectBoth,
            c => c["victim"] >= 1 && c["ok"] >= 1,
            c => $"{{{string.Join(", ", c.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}",
            timeoutS: 180,
            intervalS: 3,
            description: "both targets receiving before the deletion");
        var earlyDlq = await Drain(sqs, qDlq.Url, seconds: 2);
        Ensure(
            earlyDlq.Count == 0,
            $"nothing may dead-letter while both targets exist, got [{string.Join(", ", earlyDlq.Select(m => AttrsRepr(Attrs(m))))}]");

        // Break exactly one thing.
        await sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = qVictim.Url });

        var okById = new Dictionary<string, string>(StringComparer.Ordinal);
        var dlqMsgs = new List<SqsMessage>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        async Task<List<string>> EmitAndCollectFailure()
        {
            emitted.Add(await Emit());
            foreach (var msg in await Drain(sqs, qOk.Url, seconds: 2))
            {
                okById[(string)JsonNode.Parse(msg.Body)!["id"]!] = msg.Body;
            }

            dlqMsgs.AddRange(await Drain(sqs, qDlq.Url, seconds: 2));
            var victimIds = dlqMsgs
                .Where(m => Attrs(m).GetValueOrDefault("TARGET_ARN") == qVictim.Arn)
                .Select(m => (string)JsonNode.Parse(m.Body)!["id"]!)
                .ToHashSet(StringComparer.Ordinal);
            return [.. victimIds.Where(okById.ContainsKey).Where(emitted.Contains).Order(StringComparer.Ordinal)];
        }

        // SQS deletion propagates over time; keep emitting until an event emitted AFTER the deletion
        // is dead-lettered for the victim and delivered to the healthy sibling.
        var joined = await PollUntil(
            EmitAndCollectFailure,
            ids => ids.Count >= 1,
            ids => $"[{string.Join(", ", ids.Select(i => $"'{i}'"))}]",
            timeoutS: 200,
            intervalS: 3,
            description: "a post-deletion event dead-lettered for the victim and delivered to the sibling");
        var eventId = joined[0];
        dlqMsgs.AddRange(await Drain(sqs, qDlq.Url, seconds: 2));

        foreach (var msg in dlqMsgs)
        {
            var a = Attrs(msg);
            var targetArn = a.GetValueOrDefault("TARGET_ARN");
            Ensure(
                targetArn == qVictim.Arn,
                $"only the deleted target may dead-letter; got TARGET_ARN {(targetArn is null ? "None" : $"'{targetArn}'")} " +
                $"(healthy is '{qOk.Arn}')");
        }

        var victimMsg = dlqMsgs.First(m => (string?)JsonNode.Parse(m.Body)!["id"] == eventId);
        var attrs = Attrs(victimMsg);
        Ensure(attrs.GetValueOrDefault("ERROR_CODE") == "NO_RESOURCE", $"expected ERROR_CODE=NO_RESOURCE, got {AttrsRepr(attrs)}");
        Ensure(
            (attrs.GetValueOrDefault("ERROR_MESSAGE") ?? "").Contains("AWS.SimpleQueueService.NonExistentQueue", StringComparison.Ordinal),
            $"ERROR_MESSAGE must name the missing queue error, got '{attrs.GetValueOrDefault("ERROR_MESSAGE")}'");
        Ensure(attrs.GetValueOrDefault("RULE_ARN") == ruleArn, $"expected RULE_ARN='{ruleArn}', got {AttrsRepr(attrs)}");
        Ensure(
            JsonNode.DeepEquals(JsonNode.Parse(victimMsg.Body), JsonNode.Parse(okById[eventId])),
            "the dead-lettered body must be the same envelope the sibling received; " +
            $"DLQ '{victimMsg.Body}' vs sibling '{okById[eventId]}'");
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

        async Task Delete()
        {
            var error = await FidelityContext.CaptureError(() => sqs.DeleteQueueAsync(url));
            if (error is not null && error.Code is not ("AWS.SimpleQueueService.NonExistentQueue" or "QueueDoesNotExist"))
            {
                throw error.Exception; // the victim queue is deleted by the check itself
            }
        }

        ctx.OnCleanup(Delete);
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
