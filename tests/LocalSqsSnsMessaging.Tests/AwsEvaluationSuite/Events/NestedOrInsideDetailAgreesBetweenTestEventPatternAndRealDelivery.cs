// Translated from behavioral/events/check_nested_or_inside_detail_agrees_between_test_event_pattern_and_real_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.nested-or-inside-detail-agrees-between-test-event-pattern-and-real-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class NestedOrInsideDetailAgreesBetweenTestEventPatternAndRealDelivery
{
    private const string Check = "nested-or-inside-detail-agrees-between-test-event-pattern-and-real-delivery";

    /// <summary>
    /// `$or` nested inside `detail` is accepted, and TestEventPattern's verdict for each branch and
    /// a miss agrees with what a live rule actually delivers to an SQS target.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-create-pattern-operators.html
    /// </summary>
    [Test]
    [DisplayName("events.nested-or-inside-detail-agrees-between-test-event-pattern-and-real-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.nestedor.");
        var pattern = new JsonObject
        {
            ["source"] = new JsonArray(source),
            ["detail"] = new JsonObject
            {
                ["$or"] = new JsonArray(
                    new JsonObject { ["tier"] = new JsonArray("gold") },
                    new JsonObject { ["amount"] = new JsonArray(new JsonObject { ["numeric"] = new JsonArray(">", 1000) }) }),
            },
        };

        PutEventsRequestEntry Entry(string marker, string tier, int amount) => new()
        {
            EventBusName = bus,
            Source = source,
            DetailType = "sim2real-order",
            Detail = new JsonObject { ["marker"] = marker, ["tier"] = tier, ["amount"] = amount }.ToJsonString(),
        };

        JsonObject Envelope(string tier, int amount) => new()
        {
            ["id"] = "11111111-2222-3333-4444-555555555555",
            ["version"] = "0",
            ["detail-type"] = "sim2real-order",
            ["source"] = source,
            ["account"] = ctx.Account,
            ["time"] = "2024-01-01T00:00:00Z",
            ["region"] = ctx.Region,
            ["resources"] = new JsonArray(),
            ["detail"] = new JsonObject { ["marker"] = "x", ["tier"] = tier, ["amount"] = amount },
        };

        // --- establish the synchronous verdict for all three cases before touching a live rule --
        var cases = new (string Label, JsonObject Env, bool Want)[]
        {
            ("first-branch", Envelope("gold", 1), true),
            ("second-branch", Envelope("silver", 5000), true),
            ("neither-branch", Envelope("silver", 1), false),
        };
        foreach (var (label, env, want) in cases)
        {
            var resp = await events.TestEventPatternAsync(new TestEventPatternRequest { EventPattern = pattern.ToJsonString(), Event = env.ToJsonString() });
            Ensure(
                resp.Result == want,
                $"TestEventPattern disagreed with the expected verdict for '{label}': " +
                $"expected Result={want}, got {resp.Result}\n  event = {env.ToJsonString()}");
        }

        // --- a live rule with the same nested $or pattern, aimed at an SQS target ---------------
        var (ruleName, ruleArn) = await PutRule(ctx, events, bus, pattern);
        var (queueUrl, queueArn) = await CreateQueueWithEventbridgePolicy(ctx, ruleArn);
        await PutTarget(ctx, events, bus, ruleName, queueArn);
        // A source-only twin rule on its own queue proves the bus dispatched every entry, so an
        // absence at the primary queue is the pattern's verdict and not a delivery that never ran.
        var (controlName, controlArn) = await PutRule(ctx, events, bus, new JsonObject { ["source"] = new JsonArray(source) });
        var (controlUrl, controlQueueArn) = await CreateQueueWithEventbridgePolicy(ctx, controlArn);
        await PutTarget(ctx, events, bus, controlName, controlQueueArn);

        var firstMarker = FidelityContext.UniqueName("first-branch-");
        var secondMarker = FidelityContext.UniqueName("second-branch-");
        var nearMissMarker = FidelityContext.UniqueName("neither-branch-");
        var put = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                Entry(firstMarker, "gold", 1),
                Entry(secondMarker, "silver", 5000),
                Entry(nearMissMarker, "silver", 1),
            ],
        });
        Ensure(put.FailedEntryCount == 0, $"PutEvents rejected an entry: {string.Join(", ", (put.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})"))}");

        await CollectMarkers(ctx, controlUrl, [firstMarker, secondMarker, nearMissMarker], timeoutS: 90.0);
        var delivered = (await Drain(ctx, queueUrl)).ToHashSet();
        Ensure(
            delivered.SetEquals([firstMarker, secondMarker]),
            $"the live rule's deliveries must match TestEventPattern's verdict: expected " +
            $"{{'{firstMarker}', '{secondMarker}'}} (both $or branches), got {{{string.Join(", ", delivered.Select(m => m is null ? "None" : $"'{m}'"))}}}");
        Ensure(
            !delivered.Contains(nearMissMarker),
            $"an event matching neither $or branch was delivered: '{nearMissMarker}'");
    }

    private static async Task<string> CreateBus(FidelityContext ctx, AmazonEventBridgeClient events)
    {
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        return bus;
    }

    private static async Task<(string Name, string Arn)> PutRule(FidelityContext ctx, AmazonEventBridgeClient events, string bus, JsonObject pattern)
    {
        var name = FidelityContext.UniqueName("sim2real-behav-rule-");
        var arn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = name,
            EventBusName = bus,
            EventPattern = pattern.ToJsonString(),
            State = RuleState.ENABLED,
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => IgnoreNotFound(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus })));
        return (name, arn);
    }

    private static async Task PutTarget(FidelityContext ctx, AmazonEventBridgeClient events, string bus, string ruleName, string targetArn)
    {
        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName,
            EventBusName = bus,
            Targets = [new Target { Id = "sim2real-target", Arn = targetArn }],
        });
        Ensure(resp.FailedEntryCount == 0, $"put_targets failed: {resp.FailedEntryCount}");
        ctx.OnCleanup(() => IgnoreNotFound(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = bus, Ids = ["sim2real-target"] })));
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

    private static string? MarkerOf(JsonNode? body) =>
        body!.AsObject()["detail"] is JsonObject detail && detail["marker"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Drain until every marker in `wanted` has arrived on the queue.</summary>
    private static async Task CollectMarkers(FidelityContext ctx, string queueUrl, HashSet<string> wanted, double timeoutS)
    {
        var sqs = ctx.Sqs;
        var seen = new HashSet<string?>();
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS) && !seen.IsSupersetOf(wanted))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(MarkerOf(JsonNode.Parse(msg.Body)));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        Ensure(
            seen.IsSupersetOf(wanted),
            $"control queue received [{string.Join(", ", seen.OfType<string>().Order(StringComparer.Ordinal).Select(m => $"'{m}'"))}] within {timeoutS}s, " +
            $"expected [{string.Join(", ", wanted.Order(StringComparer.Ordinal).Select(m => $"'{m}'"))}]");
    }

    /// <summary>Receive until one empty long poll; return the markers of every message drained.</summary>
    private static async Task<List<string?>> Drain(FidelityContext ctx, string queueUrl)
    {
        var sqs = ctx.Sqs;
        var markers = new List<string?>();
        while (true)
        {
            var msgs = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
            if (msgs.Count == 0)
            {
                return markers;
            }

            foreach (var msg in msgs)
            {
                markers.Add(MarkerOf(JsonNode.Parse(msg.Body)));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }
    }

    private static async Task IgnoreNotFound(Func<Task> fn)
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw error.Exception;
        }
    }
}
