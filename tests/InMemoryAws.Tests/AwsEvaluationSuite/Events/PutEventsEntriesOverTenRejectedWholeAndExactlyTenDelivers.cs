// Translated from behavioral/events/check_put_events_entries_over_ten_rejected_whole_and_exactly_ten_delivers.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit abd0811).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;
using EventBridgeTag = Amazon.EventBridge.Model.Tag;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-entries-over-ten-rejected-whole-and-exactly-ten-delivers</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsEntriesOverTenRejectedWholeAndExactlyTenDelivers
{
    private const string Check = "put-events-entries-over-ten-rejected-whole-and-exactly-ten-delivers";
    private const int MaxEntries = 10;
    private const double LivenessTimeoutS = 120.0;
    private const double DrainTimeoutS = 90.0;
    private const string DetailType = "sim2real-entry-count";

    /// <summary>
    /// PutEvents accepts a request of exactly 10 entries and refuses one of 11 in full, with
    /// not one of the 11 reaching a rule's target.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-entries-over-ten-rejected-whole-and-exactly-ten-delivers")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new EventBridgeTag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var source = FidelityContext.UniqueName("sim2real.entrycount.");

        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventBusName = bus,
            EventPattern = $$"""{"source": ["{{source}}"]}""",
            State = RuleState.ENABLED,
        })).RuleArn;
        ctx.OnCleanup(async () =>
        {
            var ids = ((await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName, EventBusName = bus })).Targets ?? [])
                .Select(t => t.Id).ToList();
            if (ids.Count > 0)
            {
                await events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, EventBusName = bus, Ids = ids });
            }

            await events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName, EventBusName = bus });
        });

        var queueName = FidelityContext.UniqueName("sim2real-behav-q-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Tags = new() { ["sim2real"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(new DeleteQueueRequest { QueueUrl = queueUrl }));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queueArn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        var put = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = ruleName, EventBusName = bus, Targets = [new Target { Id = "sim2real-q", Arn = queueArn }],
        });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {string.Join(", ", (put.FailedEntries ?? []).Select(e => $"{e.TargetId}: {e.ErrorCode} {e.ErrorMessage}"))}");

        // A just-registered target can drop the first few events before delivery is live.
        await WaitForRoutingLive(events, sqs, bus, source, queueUrl);

        // 11 entries, each with its own marker: the whole request must be refused, none delivered.
        var overMarkers = Enumerable.Range(0, MaxEntries + 1).Select(i => FidelityContext.UniqueName($"over-{i}-")).ToList();
        var (code, message) = await Error(() => events.PutEventsAsync(new PutEventsRequest { Entries = [.. overMarkers.Select(m => Entry(bus, source, m))] }));
        var expected = TooManyEntriesError(bus, source, overMarkers);
        Ensure(
            (code, message) == expected,
            $"a PutEvents request of {MaxEntries + 1} entries must be refused with " +
            $"('{expected.Code}', '{expected.Message}'), got ('{code}', '{message}')");

        var start = FidelityTime.StartNew();
        var seen = await Drain(sqs, queueUrl, until: _ => false, timeoutS: 15.0, allowEmpty: true);
        var overLatencyS = Math.Max(start.Elapsed.TotalSeconds, 1.0);
        Ensure(
            !seen.Any(m => overMarkers.Contains(MarkerOf(m))),
            "an entry from the refused 11-entry request reached the target");

        // Exactly 10 entries, as the positive control, must succeed and every one must arrive.
        var okMarkers = Enumerable.Range(0, MaxEntries).Select(i => FidelityContext.UniqueName($"ok-{i}-")).ToList();
        var okStart = FidelityTime.StartNew();
        var resp = await events.PutEventsAsync(new PutEventsRequest { Entries = [.. okMarkers.Select(m => Entry(bus, source, m))] });
        Ensure((resp.FailedEntryCount ?? 0) == 0, $"a 10-entry request must be fully accepted, got FailedEntryCount={resp.FailedEntryCount}");
        Ensure(resp.Entries.Count == MaxEntries, $"expected {MaxEntries} results, got {resp.Entries.Count}");
        var eventIds = resp.Entries.Select(e => e.EventId).ToList();
        Ensure(
            eventIds.All(id => !string.IsNullOrEmpty(id)) && eventIds.Distinct(StringComparer.Ordinal).Count() == MaxEntries,
            $"each of the {MaxEntries} entries must get its own non-empty EventId, got {Py.Repr(eventIds)}");

        var arrived = await Drain(
            sqs, queueUrl,
            until: msgs => msgs.Select(MarkerOf).ToHashSet(StringComparer.Ordinal).IsSupersetOf(okMarkers),
            timeoutS: DrainTimeoutS);
        var okLatencyS = okStart.Elapsed.TotalSeconds;
        var arrivedMarkers = arrived.Select(MarkerOf).ToHashSet(StringComparer.Ordinal);
        Ensure(
            arrivedMarkers.SetEquals(okMarkers),
            $"the 10 accepted entries must all arrive exactly once, got markers {Py.Repr(arrivedMarkers.Order(StringComparer.Ordinal).ToList())}");

        // Hold a further window, sized off whichever leg is slower, to catch a target that
        // eventually (and wrongly) delivers an entry from the refused 11-entry request late.
        var holdS = Math.Max(15.0, 3.0 * Math.Max(overLatencyS, okLatencyS));
        var late = await Drain(sqs, queueUrl, until: _ => false, timeoutS: holdS, allowEmpty: true);
        Ensure(
            !late.Any(m => overMarkers.Contains(MarkerOf(m))),
            $"an entry from the refused 11-entry request arrived late, within a {holdS.ToString("F1", CultureInfo.InvariantCulture)}s hold");
    }

    private static PutEventsRequestEntry Entry(string bus, string source, string marker) => new()
    {
        EventBusName = bus, Source = source, DetailType = DetailType, Detail = DetailOf(marker),
    };

    private static string DetailOf(string marker) => $$"""{"m": "{{marker}}"}""";

    /// <summary>
    /// The over-limit ValidationException echoes every rejected entry as a Java object dump;
    /// this renders one entry in that same form, from only the values the check sent.
    /// </summary>
    private static string RenderedEntry(string bus, string source, string marker) =>
        $"PutEventsRequestEntry(time=null, source={source}, resources=null, " +
        $"detailType={DetailType}, eventGroupId=null, detail={DetailOf(marker)}, eventBusName={bus}, " +
        "traceHeader=null, kmsKeyIdentifier=null, internalMetadata=null)";

    private static (string? Code, string? Message) TooManyEntriesError(string bus, string source, List<string> markers)
    {
        var rendered = string.Join(", ", markers.Select(m => RenderedEntry(bus, source, m)));
        return (
            "ValidationException",
            $"1 validation error detected: Value '[{rendered}]' at 'entries' failed to satisfy " +
            $"constraint: Member must have length less than or equal to {MaxEntries}");
    }

    private static string MarkerOf(Message m) => JsonNode.Parse(m.Body)!["detail"]!["m"]!.GetValue<string>();

    /// <summary>
    /// Receive (and delete) until <paramref name="until"/> holds; raise on timeout unless
    /// <paramref name="allowEmpty"/> permits ending the window with nothing new having arrived.
    /// </summary>
    private static async Task<List<Message>> Drain(IAmazonSQS sqs, string queueUrl, Func<List<Message>, bool> until, double timeoutS, bool allowEmpty = false)
    {
        var seen = new List<Message>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
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

        if (allowEmpty)
        {
            return seen;
        }

        throw Fail($"condition not met within {timeoutS}s; saw {seen.Count} message(s)");
    }

    /// <summary>
    /// Re-publish a throwaway entry until it reaches the queue, proving the rule is routing
    /// before the graded entries are sent.
    /// </summary>
    private static async Task WaitForRoutingLive(IAmazonEventBridge events, IAmazonSQS sqs, string bus, string source, string queueUrl)
    {
        var marker = FidelityContext.UniqueName("live-");
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(LivenessTimeoutS))
        {
            await events.PutEventsAsync(new PutEventsRequest { Entries = [Entry(bus, source, marker)] });
            var window = FidelityTime.StartNew();
            while (window.Elapsed < TimeSpan.FromSeconds(10))
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                    if (MarkerOf(msg) == marker)
                    {
                        return;
                    }
                }
            }
        }

        throw Fail($"the rule never routed a liveness event within {LivenessTimeoutS}s");
    }

    /// <summary>(code, message) of the ClientError <paramref name="fn"/> raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "");
    }
}
