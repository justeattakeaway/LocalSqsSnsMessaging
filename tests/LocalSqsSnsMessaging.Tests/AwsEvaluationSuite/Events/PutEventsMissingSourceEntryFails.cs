// Translated from behavioral/events/check_put_events_missing_source_entry_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-missing-source-entry-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsMissingSourceEntryFails
{
    private const string Check = "put-events-missing-source-entry-fails";

    /// <summary>
    /// A PutEvents entry without Source fails at the entry level with InvalidArgument, no EventId
    /// and no delivery, while a well-formed entry in the same batch gets an EventId and is delivered
    /// to the rule's SQS target.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-missing-source-entry-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var ruleName = FidelityContext.UniqueName("sim2real-evb-rule-");
        var source = FidelityContext.UniqueName("sim2real.source.");
        const string detailType = "sim2real-missing-source-type";

        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{source}}"], "detail-type": ["{{detailType}}"]}""",
            State = RuleState.ENABLED,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));

        var (queueUrl, queueArn) = await CreateQueueWithEventbridgePolicy(ctx, ruleArn);

        await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = "sim2real-target", Arn = queueArn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = ["sim2real-target"] }));

        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(targets.Any(t => t.Arn == queueArn), "target was not registered on the rule");

        var goodMarker = FidelityContext.UniqueName("good-marker-");
        var badMarker = FidelityContext.UniqueName("bad-marker-");
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    Source = source,
                    DetailType = detailType,
                    Detail = $$"""{"marker": "{{goodMarker}}"}""",
                },
                new PutEventsRequestEntry
                {
                    // No Source at all -- entry-level validation, not a bad but present value.
                    DetailType = detailType,
                    Detail = $$"""{"marker": "{{badMarker}}"}""",
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 1, $"expected exactly 1 failed entry, got {Repr(resp)}");
        var entries = resp.Entries ?? throw new KeyNotFoundException("'Entries'");
        Ensure(entries.Count == 2, $"PutEvents must return one result per request entry, got [{string.Join(", ", entries.Select(Repr))}]");

        var (goodEntry, badEntry) = (entries[0], entries[1]);
        Ensure(
            !string.IsNullOrEmpty(goodEntry.EventId),
            $"the well-formed entry (positive control) must carry an EventId, got {Repr(goodEntry)}");
        Ensure(
            goodEntry.ErrorCode is null,
            $"the well-formed entry must not carry an ErrorCode, got {Repr(goodEntry)}");

        Ensure(
            badEntry.ErrorCode == "InvalidArgument",
            $"an entry missing Source must fail with ErrorCode=InvalidArgument, got " +
            $"'{badEntry.ErrorCode}': {Repr(badEntry)}");
        const string expectedMessage = "Parameter Source is not valid. Reason: Source is a required argument.";
        Ensure(
            badEntry.ErrorMessage == expectedMessage,
            $"the per-entry ErrorMessage must be '{expectedMessage}', got " +
            $"'{badEntry.ErrorMessage}'");
        Ensure(
            string.IsNullOrEmpty(badEntry.EventId),
            $"a failed entry must not carry an EventId, got '{badEntry.EventId}'");

        var seenMarkers = await DrainUntilMarkerSeen(ctx, queueUrl, goodMarker, timeoutS: 60.0);
        Ensure(seenMarkers.Contains(goodMarker), "the well-formed entry (positive control) was never delivered");
        Ensure(
            !seenMarkers.Contains(badMarker),
            "the entry missing Source was delivered to the rule's target even though it was rejected " +
            "at PutEvents and never assigned an EventId");
    }

    private static async Task<(string QueueUrl, string QueueArn)> CreateQueueWithEventbridgePolicy(FidelityContext ctx, string ruleArn)
    {
        var sqs = ctx.Sqs;
        var queueName = FidelityContext.UniqueName("sim2real-evb-queue-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] }))
            .Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventBridgeDelivery",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, queueArn);
    }

    private static async Task<HashSet<string>> DrainUntilMarkerSeen(FidelityContext ctx, string queueUrl, string targetMarker, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var seen = new HashSet<string>();
        var started = FidelityTime.StartNew();
        var foundTarget = false;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS) && !foundTarget)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var body = JsonNode.Parse(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var marker = body?["detail"] is JsonObject detail ? (string?)detail["marker"] : null;
                if (!string.IsNullOrEmpty(marker))
                {
                    seen.Add(marker);
                    if (marker == targetMarker)
                    {
                        foundTarget = true;
                    }
                }
            }
        }

        if (!foundTarget)
        {
            throw Fail($"target marker {targetMarker} never arrived within {timeoutS}s");
        }

        return seen;
    }

    private static string Repr(PutEventsResponse resp) =>
        $"{{'FailedEntryCount': {resp.FailedEntryCount}, 'Entries': [{string.Join(", ", (resp.Entries ?? []).Select(Repr))}]}}";

    private static string Repr(PutEventsResultEntry e) =>
        "{" + string.Join(", ", new[]
        {
            e.EventId is null ? null : $"'EventId': '{e.EventId}'",
            e.ErrorCode is null ? null : $"'ErrorCode': '{e.ErrorCode}'",
            e.ErrorMessage is null ? null : $"'ErrorMessage': '{e.ErrorMessage}'",
        }.Where(s => s is not null)) + "}";
}
