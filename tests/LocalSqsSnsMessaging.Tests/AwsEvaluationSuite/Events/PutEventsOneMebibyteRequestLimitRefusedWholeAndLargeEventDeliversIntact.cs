// Translated from behavioral/events/check_put_events_one_mebibyte_request_limit_refused_whole_and_large_event_delivers_intact.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-one-mebibyte-request-limit-refused-whole-and-large-event-delivers-intact</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsOneMebibyteRequestLimitRefusedWholeAndLargeEventDeliversIntact
{
    private const string Check = "put-events-one-mebibyte-request-limit-refused-whole-and-large-event-delivers-intact";
    private const int Limit = 1048576; // PutEvents: the entries of one request may total at most 1 MiB
    private const int LegacyLimit = 262144; // the former 256 KiB event size limit
    private const double LivenessTimeoutS = 120.0;
    private const double DeliveryTimeoutS = 90.0;
    private const string OverLimit = "Total size of the entries in the request is over the limit.";

    /// <summary>
    /// PutEvents accepts exactly 1 MiB (Source + DetailType + Detail, UTF-8) and refuses a byte
    /// more, for one entry and for two sharing the limit, and a ~600 KB event reaches an SQS target
    /// intact.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-one-mebibyte-request-limit-refused-whole-and-large-event-delivers-intact")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;

        var bus = await CreateBus(ctx, events);
        var probeSource = FidelityContext.UniqueName("sim2real.size.");
        var deliverSource = FidelityContext.UniqueName("sim2real.large.");
        var ruleArn = await CreateRule(ctx, events, bus, $$"""{"source": ["{{deliverSource}}"]}""");
        var (queueUrl, queueArn) = await CreateQueue(ctx, new() { ["MaximumMessageSize"] = Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var rule = ruleArn.Split('/')[^1];
        var put = await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, EventBusName = bus, Targets = [new Target { Id = "sim2real-q", Arn = queueArn }] });
        Ensure(put.FailedEntryCount == 0, $"PutTargets failed: {FailedEntries(put)}");

        // The boundary, on a source no rule matches.
        var at = await events.PutEventsAsync(new PutEventsRequest { Entries = [Entry(bus, probeSource, "size", Limit, "at")] });
        Ensure(
            at.FailedEntryCount == 0 && !string.IsNullOrEmpty((at.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId),
            $"an entry of exactly {Limit} bytes (1 MiB) must be accepted, got {Repr(at.Entries)}");
        var (code, message) = await Error(
            () => events.PutEventsAsync(new PutEventsRequest { Entries = [Entry(bus, probeSource, "size", Limit + 1, "over")] }));
        Ensure(
            (code, message) == ("ValidationException", OverLimit),
            $"an entry of {Limit + 1} bytes must be refused with ValidationException '{OverLimit}', " +
            $"got '{code}': '{message}'");

        // The limit is the request's, not each entry's: two entries share it additively.
        const int half = 600000;
        var pair = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                Entry(bus, probeSource, "size", half, "first"),
                Entry(bus, probeSource, "size", Limit - half, "second"),
            ],
        });
        Ensure(
            pair.FailedEntryCount == 0 && (pair.Entries ?? throw new KeyNotFoundException("'Entries'")).Count == 2,
            $"two entries totalling exactly {Limit} bytes must both be accepted, got {Repr(pair.Entries)}");
        (code, message) = await Error(() => events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                Entry(bus, probeSource, "size", half, "first"),
                Entry(bus, probeSource, "size", Limit - half + 1, "second"),
            ],
        }));
        Ensure(
            (code, message) == ("ValidationException", OverLimit),
            $"two entries of {half} and {Limit - half + 1} bytes each fit alone but exceed the request " +
            $"limit together; the request must be refused whole with ValidationException " +
            $"'{OverLimit}', got '{code}': '{message}'");

        // Liveness: a just-created rule can drop events until routing is live.
        var marker = FidelityContext.UniqueName("live-");

        bool SmallArrived(List<SqsMessage> msgs) =>
            msgs.Any(m => IsString((JsonNode.Parse(m.Body)!["detail"] ?? throw new KeyNotFoundException("'detail'")).AsObject()["m"], marker));

        await PublishUntilLive(ctx, events, queueUrl, new PutEventsRequestEntry
        {
            EventBusName = bus,
            Source = deliverSource,
            DetailType = "live",
            Detail = $$"""{"m": "{{marker}}"}""",
        }, SmallArrived);

        var bigMarker = FidelityContext.UniqueName("big-");
        var big = Entry(bus, deliverSource, "large", 600000, bigMarker);
        var sent = await events.PutEventsAsync(new PutEventsRequest { Entries = [big] });
        Ensure(sent.FailedEntryCount == 0, $"the large event was refused: {Repr(sent.Entries)}");
        var eventId = (sent.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId ?? throw new KeyNotFoundException("'EventId'");

        bool BigArrived(List<SqsMessage> msgs) =>
            msgs.Any(m => IsString(JsonNode.Parse(m.Body)!["id"] ?? throw new KeyNotFoundException("'id'"), eventId));

        var msgs = await Drain(ctx, queueUrl, until: BigArrived, timeoutS: DeliveryTimeoutS,
            description: $"the {big.Detail.Length}-byte event {eventId} reaching the queue");
        var envelope = msgs.Select(m => JsonNode.Parse(m.Body)!.AsObject()).First(b => IsString(b["id"], eventId));
        Ensure(big.Detail.Length > LegacyLimit, "the large event must exceed the old 256 KiB limit");
        Ensure(
            JsonNode.DeepEquals(envelope["detail"], JsonNode.Parse(big.Detail)),
            $"the large event's detail was not delivered intact: sent {big.Detail.Length} bytes with " +
            $"marker '{bigMarker}', received detail of {envelope["detail"]?.ToJsonString().Length} bytes " +
            $"with marker '{envelope["detail"]?["m"]}'");
        Ensure(
            IsString(envelope["source"], deliverSource) && IsString(envelope["detail-type"], "large"),
            $"the large event arrived with source/detail-type '{envelope["source"]}'/" +
            $"'{envelope["detail-type"]}'");
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

        throw Fail($"expected the call to be refused, but it succeeded: {(result is PutEventsResponse r ? $"{{'FailedEntryCount': {r.FailedEntryCount}, 'Entries': {Repr(r.Entries)}}}" : result)}");
    }

    /// <summary>An entry whose Source + DetailType + Detail weigh exactly <c>size</c> UTF-8 bytes.</summary>
    private static PutEventsRequestEntry Entry(string bus, string source, string detailType, int size, string marker)
    {
        var shell = $$"""{"m":"{{marker}}","pad":""}""";
        var pad = size - source.Length - detailType.Length - shell.Length;
        Ensure(pad >= 0, $"cannot build a {size}-byte entry around '{shell}'");
        var detail = $$"""{"m":"{{marker}}","pad":"{{new string('z', pad)}}"}""";
        var entry = new PutEventsRequestEntry { EventBusName = bus, Source = source, DetailType = detailType, Detail = detail };
        var weight = new[] { entry.Source, entry.DetailType, entry.Detail }.Sum(Encoding.UTF8.GetByteCount);
        Ensure(weight == size, $"entry weighs {weight} bytes, intended {size}");
        return entry;
    }

    private static string Repr(List<PutEventsResultEntry>? entries) =>
        $"[{string.Join(", ", (entries ?? []).Select(e => "{" + string.Join(", ", new[]
        {
            e.EventId is null ? null : $"'EventId': '{e.EventId}'",
            e.ErrorCode is null ? null : $"'ErrorCode': '{e.ErrorCode}'",
            e.ErrorMessage is null ? null : $"'ErrorMessage': '{e.ErrorMessage}'",
        }.Where(s => s is not null)) + "}"))}]";

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
