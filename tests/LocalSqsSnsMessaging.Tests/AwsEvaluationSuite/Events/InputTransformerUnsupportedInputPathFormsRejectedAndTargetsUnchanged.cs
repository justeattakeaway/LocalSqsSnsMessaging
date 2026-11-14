// Translated from behavioral/events/check_input_transformer_unsupported_input_path_forms_rejected_and_targets_unchanged.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.input-transformer-unsupported-input-path-forms-rejected-and-targets-unchanged</summary>
[Category("AwsEvaluationSuite")]
public sealed class InputTransformerUnsupportedInputPathFormsRejectedAndTargetsUnchanged
{
    private const string Check = "input-transformer-unsupported-input-path-forms-rejected-and-targets-unchanged";
    private const double LivenessTimeoutS = 120.0;
    private const double DeliveryTimeoutS = 90.0;

    /// <summary>
    /// PutTargets refuses InputPaths with a negative index or bracketed quoted member, naming the
    /// target, and a mixed request installs neither; existing delivery and an accepted `[1]` path keep
    /// working.
    ///
    /// Reference: https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-transform-target-input.html
    /// </summary>
    [Test]
    [DisplayName("events.input-transformer-unsupported-input-path-forms-rejected-and-targets-unchanged")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bus = await CreateBus(ctx, events);
        var source = FidelityContext.UniqueName("sim2real.paths.");
        var rule = (await CreateRule(ctx, events, bus, $$"""{"source": ["{{source}}"]}""")).Split('/')[^1];
        var (queueUrl, queueArn) = await CreateQueue(ctx);
        var put = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "raw", Arn = queueArn }] });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        Target Transformer(string targetId, string path) => new()
        {
            Id = targetId,
            Arn = queueArn,
            InputTransformer = new InputTransformer
            {
                InputPathsMap = new() { ["v"] = path, ["m"] = "$.detail.m" },
                InputTemplate = $$"""{"m": <m>, "id": "{{targetId}}", "v": <v>}""",
            },
        };

        // `[1]` and `a-b` are supported path forms; a negative index and `['k.k']` are not.
        foreach (var (targetId, path) in new[] { ("negative-index", "$.detail.items[-1]"), ("bracket-member", "$.detail['k.k']") })
        {
            var (code, message) = await Error(() => events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [Transformer(targetId, path)] }));
            Ensure(
                (code, message) == ("ValidationException", $"InputPath for target {targetId} is invalid."),
                $"InputPath '{path}' must be refused with ValidationException 'InputPath for target " +
                $"{targetId} is invalid.', got '{code}': '{message}'");
        }

        var (mixedCode, mixedMessage) = await Error(() => events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                Transformer("valid-member", "$.detail.a-b"),
                Transformer("negative-index", "$.detail.items[-1]"),
            ],
        }));
        Ensure(
            (mixedCode, mixedMessage) == ("ValidationException", "InputPath for target negative-index is invalid."),
            $"a request carrying one invalid target must be refused naming it, got '{mixedCode}': '{mixedMessage}'");
        var listed = await ListedIds(events, rule, bus);
        Ensure(
            listed.SequenceEqual(["raw"]),
            $"refused PutTargets calls must install nothing, not even the valid target of a mixed " +
            $"request; the rule lists {Repr(listed)}");

        var ok = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [Transformer("index", "$.detail.items[1]")] });
        Ensure(
            ok.FailedEntryCount == 0,
            $"positive control: the supported index path was refused: {FailedEntries(ok)}");
        listed = await ListedIds(events, rule, bus);
        Ensure(listed.SequenceEqual(["index", "raw"]), $"after the accepted PutTargets the rule lists {Repr(listed)}");

        var live = FidelityContext.UniqueName("live-");
        await PublishUntilLive(ctx, events, queueUrl, new PutEventsRequestEntry
        {
            EventBusName = bus,
            Source = source,
            DetailType = "live",
            Detail = $$"""{"m": "{{live}}", "items": [0, 0]}""",
        }, msgs => msgs.Count(m => m.Body.Contains(live, StringComparison.Ordinal)) >= 2);

        var marker = FidelityContext.UniqueName("path-");
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = bus,
                    Source = source,
                    DetailType = "paths",
                    Detail = $$"""{"m": "{{marker}}", "items": [10, 20, 30], "a-b": "dashed", "k.k": "dotted"}""",
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents refused the event: {string.Join(", ", (resp.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})"))}");

        List<JsonObject> Ours(List<SqsMessage> msgs)
        {
            var output = new List<JsonObject>();
            foreach (var msg in msgs)
            {
                // body.get(...) on a non-object raises AttributeError in Python; AsObject throws here.
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                if (IsString(body["m"], marker) || (body["detail"] is JsonObject detail && IsString(detail["m"], marker)))
                {
                    output.Add(body);
                }
            }

            return output;
        }

        var bodies = Ours(await Drain(ctx, queueUrl, until: ms => Ours(ms).Count >= 2,
            timeoutS: DeliveryTimeoutS, description: $"event {marker} on both targets"));
        var shaped = bodies.Where(b => !b.ContainsKey("detail")).ToList();
        var raw = bodies.Where(b => b.ContainsKey("detail")).ToList();
        Ensure(
            shaped.Count == 1 && JsonNode.DeepEquals(shaped[0], new JsonObject { ["m"] = marker, ["id"] = "index", ["v"] = 20 }),
            $"the accepted `[1]` target must render the second element and be the only transformed " +
            $"delivery; got [{string.Join(", ", shaped.Select(b => b.ToJsonString()))}]");
        Ensure(
            raw.Count == 1 && IsString(raw[0]["detail"]!["m"], marker),
            $"the pre-existing raw target must still deliver the event once, got [{string.Join(", ", raw.Select(b => b.ToJsonString()))}]");
    }

    /// <summary>(code, message) of the ClientError <c>fn</c> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is not null)
        {
            return (error.Code, error.Message ?? "");
        }

        throw Fail($"expected the call to be refused, but it succeeded: {result}");
    }

    private static async Task<List<string>> ListedIds(AmazonEventBridgeClient events, string rule, string bus) =>
        [.. ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = rule, EventBusName = bus })).Targets
            ?? throw new KeyNotFoundException("'Targets'")).Select(t => t.Id).Order(StringComparer.Ordinal)];

    private static string Repr(IEnumerable<string> items) => $"[{string.Join(", ", items.Select(i => $"'{i}'"))}]";

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
