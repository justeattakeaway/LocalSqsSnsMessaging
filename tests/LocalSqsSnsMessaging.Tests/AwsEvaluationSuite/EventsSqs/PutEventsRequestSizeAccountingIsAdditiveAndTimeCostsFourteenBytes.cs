// Translated from behavioral/events__sqs/check_put_events_request_size_accounting_is_additive_and_time_costs_fourteen_bytes.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.Runtime;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.put-events-request-size-accounting-is-additive-and-time-costs-fourteen-bytes</summary>
/// <remarks>
/// Entry-size accounting: Time costs 14 bytes; Source, DetailType, Detail and each Resources entry
/// cost their UTF-8 byte length (https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-putevents.html).
/// The documented limit value differs between pages, so the wall is measured, never pinned.
/// </remarks>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsRequestSizeAccountingIsAdditiveAndTimeCostsFourteenBytes
{
    private const int TimeBytes = 14; // documented cost of the Time parameter, in the entry-size accounting
    private const int ProbeCeiling = 1_200_000; // above both documented candidate limits (256KB and 1MB)
    private const int SanityFloor = 100_000; // a measured limit below this is a target bug, not a doc ambiguity
    private const int SanityCeiling = 1_100_000; // ... and above this it is not enforcing any documented limit
    private const int NarrowSlack = 96; // bracket width for the three follow-up probes, in bytes
    private const double LivenessTimeoutS = 120.0;
    private const double DeliveryTimeoutS = 90.0;

    // '{"marker":"<32 hex>","pad":"' + pad + '"}' — the fixed overhead of the Detail JSON wrapper.
    private static readonly int DetailOverhead = "{\"marker\":\"".Length + 32 + "\",\"pad\":\"".Length + "\"}".Length;

    /// <summary>
    /// The PutEvents request-size limit is one number that Source, DetailType, Detail and each
    /// Resources entry all draw down additively, and supplying Time costs exactly 14 bytes of it.
    /// </summary>
    [Test]
    [DisplayName("events__sqs.put-events-request-size-accounting-is-additive-and-time-costs-fourteen-bytes")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var account = ctx.Account;
        var sourceShort = XName("s");
        var sourceLong = sourceShort + new string('z', 200);
        List<string> resources =
        [
            $"arn:aws:events:{ctx.Region}:{account}:event-bus/sim2real-probe-resource-one",
            $"arn:aws:events:{ctx.Region}:{account}:event-bus/sim2real-probe-resource-two",
        ];

        // A bus with NO rules carries the bisection traffic, so ~48 large events are not delivered
        // anywhere. A second bus carries the one at-limit event whose delivery is asserted.
        var (probeBus, _) = await CreateBus(ctx);
        var (ruleBus, _) = await CreateBus(ctx);

        var queue = await CreateQueue(ctx);
        var rule = await PutRuleWithProjectingTarget(ctx, ruleBus, new JsonObject { ["source"] = new JsonArray(sourceShort) }, queue);

        // --- probe 1: the baseline wall, short Source, short DetailType, no Resources, no Time. ---
        var @base = new Shape(sourceShort, "SizeProbe", null, WithTime: false);
        var d1 = await MeasureWide(ctx, probeBus, @base);
        var limit = @base.Overhead() + d1;
        Ensure(
            SanityFloor <= limit && limit <= SanityCeiling,
            $"the measured PutEvents request-size limit is {limit} bytes, outside the plausible range " +
            $"[{SanityFloor}, {SanityCeiling}] that the two contradicting AWS docs bracket (256KB in " +
            "the API reference, 1MB in the user guide) — a target with no limit or a tiny one is what " +
            "this catches");

        // --- probes 2-4: the same wall, reached through different fields. ---
        // DetailType has its own documented field-length cap of 128 characters (API_PutEventsRequestEntry),
        // a constraint independent of the entry-size-in-bytes budget this check measures. Padding it past
        // 128 trips that unrelated validation instead of the size wall, so the pad here stays well under it.
        var longNames = new Shape(sourceLong, "SizeProbe" + new string('y', 100), null, WithTime: false);
        var withResources = new Shape(sourceShort, "SizeProbe", resources, WithTime: false);
        var withTime = new Shape(sourceShort, "SizeProbe", null, WithTime: true);

        var d2 = await MeasureNarrow(ctx, probeBus, longNames, expectedTotal: limit);
        var d3 = await MeasureNarrow(ctx, probeBus, withResources, expectedTotal: limit);
        var d4 = await MeasureNarrow(ctx, probeBus, withTime, expectedTotal: limit);

        var measured = new Dictionary<string, int>
        {
            ["baseline"] = limit,
            ["long source+detail-type"] = longNames.Overhead() + d2,
            ["with 2 resources"] = withResources.Overhead() + d3,
            ["with Time"] = withTime.Overhead() + d4,
        };
        Ensure(
            measured.Values.Distinct().Count() == 1,
            "the request-size limit must be ONE number that every counted field draws down additively " +
            $"per the documented accounting; the four probes measured {{{string.Join(", ", measured.Select(kv => $"'{kv.Key}': {kv.Value}"))}}} — a target that limits " +
            "on the serialised request body, or that forgets to charge for Resources, disagrees here");

        // --- the exact documented cost of Time, expressed as a difference between measurements. ---
        Ensure(
            d1 - d4 == TimeBytes,
            $"supplying Time must cost exactly {TimeBytes} bytes of the request budget: the largest " +
            $"accepted Detail was {d1} bytes without Time and {d4} bytes with it, a difference of " +
            $"{d1 - d4}. This is a fixed documented API constant, not an account quota");

        // --- one byte over the wall is refused, the same way, for every shape. ---
        var shapes = new List<(string Label, Shape Shape, int DetailLen)>
        {
            ("baseline", @base, d1), ("long names", longNames, d2), ("resources", withResources, d3), ("time", withTime, d4),
        };
        var refusals = new Dictionary<string, Refusal>();
        foreach (var (label, shape, shapeDetailLen) in shapes)
        {
            refusals[label] = await RejectShape(ctx, probeBus, shape, shapeDetailLen + 1, label: label);
        }

        var distinct = refusals.Values.ToHashSet();
        Ensure(
            distinct.Count == 1,
            $"one byte past the limit must be refused identically for every entry shape, got {{{string.Join(", ", refusals.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}");
        var (surface, code, status, message) = distinct.First();
        Ensure(
            status == 400,
            $"an over-limit PutEvents must be a 400-class client error, got HTTPStatusCode={status} " +
            $"with surface='{surface}' code={Repr(code)}");
        Ensure(
            !string.IsNullOrEmpty(code),
            $"the over-limit refusal must carry a non-empty error code; got surface='{surface}' " +
            $"status={status}");
        Ensure(
            message == "Total size of the entries in the request is over the limit.",
            $"the over-limit refusal must state the request-size limit; got {Repr(message)} " +
            $"(surface='{surface}' code={Repr(code)})");

        // --- the at-limit entry is not merely accepted, it is really delivered. ---
        // Liveness first: a freshly created rule silently drops events until routing goes live.
        await EmitUntilDelivered(ctx, ruleBus, @base, DetailOverhead, queue.Url);
        var marker = Guid.NewGuid().ToString("N");
        var entry = @base.Entry(marker, d1);
        entry.EventBusName = ruleBus;
        Ensure(
            EntrySize(entry) == limit,
            $"the at-limit entry must weigh exactly the measured limit {limit} by the documented " +
            $"accounting, computed {EntrySize(entry)}");
        var resp = await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
        Ensure(
            resp.FailedEntryCount == 0 && !string.IsNullOrEmpty((resp.Entries ?? []).FirstOrDefault()?.EventId),
            "the at-limit entry must be accepted with an EventId, got " +
            Truncate(PutEventsRepr(resp), 300));
        var delivered = await AwaitProjection(ctx, queue.Url, marker, timeoutS: DeliveryTimeoutS);
        Ensure(
            JsonNode.DeepEquals(delivered, new JsonObject { ["m"] = marker }),
            $"the at-limit event must be delivered through rule {rule.Name} and projected by the input " +
            $"transformer to exactly the marker; the queue delivered {PyRepr(delivered)}");
    }

    // ===== helpers =====
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    private sealed record Queue(string Name, string Url, string Arn);

    private sealed record Refusal(string Surface, string? Code, int Status, string? Message)
    {
        public override string ToString() => $"('{Surface}', {Repr(Code)}, {Status}, {Repr(Message)})";
    }

    /// <summary>One PutEvents entry shape: everything except the Detail length is fixed.</summary>
    /// <remarks>
    /// `overhead()` is the documented entry-size contribution of every field OTHER than Detail, so
    /// `overhead() + len(Detail)` is the entry's documented size.
    /// </remarks>
    private sealed record Shape(string Source, string DetailType, List<string>? Resources, bool WithTime)
    {
        public int Overhead()
        {
            var total = Encoding.UTF8.GetByteCount(Source) + Encoding.UTF8.GetByteCount(DetailType);
            foreach (var resource in Resources ?? [])
            {
                total += Encoding.UTF8.GetByteCount(resource);
            }

            if (WithTime)
            {
                total += TimeBytes;
            }

            return total;
        }

        public PutEventsRequestEntry Entry(string marker, int detailLen)
        {
            Ensure(
                detailLen >= DetailOverhead,
                $"detail_len {detailLen} is below the JSON wrapper's own {DetailOverhead} bytes");
            var detail = "{\"marker\":\"" + marker + "\",\"pad\":\"" + new string('x', detailLen - DetailOverhead) + "\"}";
            Ensure(
                Encoding.UTF8.GetByteCount(detail) == detailLen,
                $"detail construction is wrong: wanted {detailLen} bytes, built " +
                $"{Encoding.UTF8.GetByteCount(detail)}");
            var entry = new PutEventsRequestEntry { Source = Source, DetailType = DetailType, Detail = detail };
            if (Resources is { Count: > 0 })
            {
                entry.Resources = [.. Resources];
            }

            if (WithTime)
            {
                entry.Time = FidelityTime.UtcNow.UtcDateTime;
            }

            return entry;
        }
    }

    /// <summary>The documented PutEventsRequestEntry size for an already-built entry.</summary>
    private static int EntrySize(PutEventsRequestEntry entry)
    {
        var total = Encoding.UTF8.GetByteCount(entry.Source) + Encoding.UTF8.GetByteCount(entry.DetailType);
        total += Encoding.UTF8.GetByteCount(entry.Detail);
        foreach (var resource in entry.Resources ?? [])
        {
            total += Encoding.UTF8.GetByteCount(resource);
        }

        if (entry.Time is not null)
        {
            total += TimeBytes;
        }

        return total;
    }

    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    private static List<Amazon.EventBridge.Model.Tag> XsvcTagsKv() =>
        [.. XsvcResourceTags().Select(kv => new Amazon.EventBridge.Model.Tag { Key = kv.Key, Value = kv.Value })];

    private static string Repr(string? s) => s is null ? "None" : $"'{s}'";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static string PutEventsRepr(PutEventsResponse resp) =>
        $"{{\"FailedEntryCount\": {resp.FailedEntryCount}, \"Entries\": " +
        $"[{string.Join(", ", (resp.Entries ?? []).Select(e => $"{{\"EventId\": \"{e.EventId}\", \"ErrorCode\": \"{e.ErrorCode}\", \"ErrorMessage\": \"{e.ErrorMessage}\"}}"))}]}}";

    /// <summary>Python's repr() of a parsed JSON value, for the assertion messages.</summary>
    private static string PyRepr(JsonNode? node) => node switch
    {
        null => "None",
        JsonObject o => "{" + string.Join(", ", o.Select(kv => $"'{kv.Key}': {PyRepr(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(PyRepr)) + "]",
        JsonValue v when v.TryGetValue<string>(out var s) => $"'{s}'",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "True" : "False",
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// Run a teardown call. With a leak label, an error code outside `tolerated` is reported as a
    /// leak; without one, a refused teardown call is ignored.
    /// </summary>
    private static async Task Tolerate(Func<Task> fn, string tolerated, string leak = "")
    {
        var error = await FidelityContext.CaptureError(fn);
        if (error is not null && leak.Length > 0 && error.Code != tolerated)
        {
            throw new InvalidOperationException($"{leak}: {error.Code}", error.Exception);
        }
    }

    /// <summary>True iff PutEvents fully accepted a single entry of this shape and Detail length.</summary>
    private static async Task<bool> Accepted(FidelityContext ctx, string bus, Shape shape, int detailLen)
    {
        var entry = shape.Entry(Guid.NewGuid().ToString("N"), detailLen);
        entry.EventBusName = bus;
        PutEventsResponse resp;
        try
        {
            resp = await ctx.Events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
        }
        catch (AmazonServiceException)
        {
            return false;
        }

        if ((resp.FailedEntryCount ?? 0) != 0)
        {
            return false;
        }

        var entries = resp.Entries ?? [];
        return entries.Count == 1 && !string.IsNullOrEmpty(entries[0].EventId);
    }

    /// <summary>
    /// ("exception"|"entry", code, http status, message) for an over-limit request; RAISES on
    /// acceptance.
    /// </summary>
    private static async Task<Refusal> RejectShape(FidelityContext ctx, string bus, Shape shape, int detailLen, string label)
    {
        var entry = shape.Entry(Guid.NewGuid().ToString("N"), detailLen);
        entry.EventBusName = bus;
        var (resp, error) = await FidelityContext.Capture(() => ctx.Events.PutEventsAsync(new PutEventsRequest { Entries = [entry] }));
        if (error is not null)
        {
            return new Refusal("exception", error.Code, (int)error.StatusCode, error.Message ?? string.Empty);
        }

        // The Python reads resp.get("FailedEntries"), a member PutEvents responses don't have, so its
        // list is always empty and a per-entry failure is reported as an acceptance below. Kept as is.
        List<PutEventsResultEntry> failed = [];
        if ((resp!.FailedEntryCount ?? 0) != 0 && failed.Count > 0)
        {
            return new Refusal("entry", failed[0].ErrorCode, (int)resp.HttpStatusCode, failed[0].ErrorMessage ?? string.Empty);
        }

        throw Fail(
            $"PutEvents ACCEPTED a request one byte past the measured limit ({label}, entry size " +
            $"{shape.Overhead() + detailLen}) — the limit is not being enforced consistently. Response " +
            $"was {Truncate(PutEventsRepr(resp), 300)}");
    }

    /// <summary>The greatest accepted Detail length for `shape`, bisected over the full plausible range.</summary>
    /// <remarks>
    /// Both ends are established by measurement before the search starts, so a target that refuses
    /// everything and a target that accepts everything both fail here rather than silently returning an
    /// endpoint of the interval.
    /// </remarks>
    private static async Task<int> MeasureWide(FidelityContext ctx, string bus, Shape shape)
    {
        var low = DetailOverhead;
        var high = ProbeCeiling;
        Ensure(
            await Accepted(ctx, bus, shape, low),
            $"positive control failed: a minimal {low}-byte Detail must be accepted on bus {bus}; if it " +
            "is not, every 'refused' verdict in the bisection is meaningless");
        Ensure(
            !await Accepted(ctx, bus, shape, high),
            $"a Detail of {high} bytes was ACCEPTED — that is past both documented candidate limits " +
            "(256KB and 1MB), so the target is enforcing no request-size limit at all");
        return await Bisect(ctx, bus, shape, low, high);
    }

    /// <summary>
    /// The greatest accepted Detail length for `shape`, bisected in a small bracket around the
    /// baseline's measured limit. Cheap, because it avoids re-uploading the full ladder.
    /// </summary>
    /// <remarks>
    /// If the bracket turns out not to contain the wall, both bracket assertions below fail loudly with
    /// the numbers — which is itself the disagreement this check exists to detect.
    /// </remarks>
    private static async Task<int> MeasureNarrow(FidelityContext ctx, string bus, Shape shape, int expectedTotal)
    {
        var centre = expectedTotal - shape.Overhead();
        var low = Math.Max(DetailOverhead, centre - NarrowSlack);
        var high = centre + NarrowSlack;
        Ensure(
            await Accepted(ctx, bus, shape, low),
            $"the bracket for this shape does not start below the wall: a Detail of {low} bytes " +
            $"(entry size {shape.Overhead() + low}) was refused, while the baseline measured a limit of " +
            $"{expectedTotal}. That disagreement IS the finding — the accounting is not additive");
        Ensure(
            !await Accepted(ctx, bus, shape, high),
            $"the bracket for this shape does not end above the wall: a Detail of {high} bytes " +
            $"(entry size {shape.Overhead() + high}) was accepted, while the baseline measured a limit of " +
            $"{expectedTotal}. That disagreement IS the finding — the accounting is not additive");
        return await Bisect(ctx, bus, shape, low, high);
    }

    /// <summary>Bisect a bracket whose low end is known accepted and high end known refused.</summary>
    /// <remarks>Re-probes the answer once, so a non-monotonic target fails loudly instead of being mis-measured.</remarks>
    private static async Task<int> Bisect(FidelityContext ctx, string bus, Shape shape, int low, int high)
    {
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (await Accepted(ctx, bus, shape, mid))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        Ensure(
            await Accepted(ctx, bus, shape, low),
            $"bisection inconsistency: Detail length {low} was accepted during the search and is refused " +
            "on re-probe, so the request-size limit is not stable");
        return low;
    }

    private static async Task<(string Name, string Arn)> CreateBus(FidelityContext ctx)
    {
        var events = ctx.Events;
        var name = XName("bus");
        var arn = (await events.CreateEventBusAsync(new CreateEventBusRequest { Name = name, Tags = XsvcTagsKv() })).EventBusArn;
        ctx.OnCleanup(() => Tolerate(
            () => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }),
            "ResourceNotFoundException",
            leak: $"could not delete event bus {name}"));
        return (name, arn);
    }

    private static async Task<Queue> CreateQueue(FidelityContext ctx)
    {
        var sqs = ctx.Sqs;
        var name = XName("q");
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>A rule whose SQS target carries an InputTransformer projecting only $.detail.marker.</summary>
    private static async Task<(string Name, string Arn)> PutRuleWithProjectingTarget(FidelityContext ctx, string bus, JsonObject pattern, Queue queue)
    {
        var events = ctx.Events;
        var name = XName("rule");
        var arn = (await events.PutRuleAsync(new PutRuleRequest { Name = name, EventBusName = bus, EventPattern = pattern.ToJsonString(), Tags = XsvcTagsKv() })).RuleArn;

        ctx.OnCleanup(async () =>
        {
            var ev = ctx.Events;
            await Tolerate(
                () => ev.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = name, EventBusName = bus, Ids = ["t1"] }),
                "ResourceNotFoundException",
                leak: $"could not remove target t1 from rule {name}");
            await Tolerate(
                () => ev.DeleteRuleAsync(new DeleteRuleRequest { Name = name, EventBusName = bus, Force = true }),
                "ResourceNotFoundException",
                leak: $"could not delete rule {name} on bus {bus}");
        });
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowEventsSend",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = queue.Arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = arn } },
            }),
        };
        await ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.Url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = name,
            EventBusName = bus,
            Targets =
            [
                new Target
                {
                    Id = "t1",
                    Arn = queue.Arn,
                    InputTransformer = new InputTransformer
                    {
                        InputPathsMap = new() { ["m"] = "$.detail.marker" },
                        InputTemplate = """{"m": "<m>"}""",
                    },
                },
            ],
        });
        return (name, arn);
    }

    private static JsonNode? Body(Amazon.SQS.Model.Message msg)
    {
        var raw = msg.Body ?? string.Empty;
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }

    private static async Task<List<JsonNode?>> Drain(FidelityContext ctx, string queueUrl, double windowS, int maxTotal = 25)
    {
        var sqs = ctx.Sqs;
        var seen = new List<JsonNode?>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = wait });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(Body(msg));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return seen;
    }

    private static string? MarkerOf(JsonNode? body) =>
        body is JsonObject o && o["m"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Poll until the projected message for `marker` arrives. RAISES on timeout.</summary>
    private static async Task<JsonObject> AwaitProjection(FidelityContext ctx, string queueUrl, string marker, double timeoutS)
    {
        var strangers = new List<JsonNode?>();
        var started = FidelityTime.StartNew();
        var timeout = TimeSpan.FromSeconds(timeoutS);
        while (started.Elapsed < timeout)
        {
            foreach (var body in await Drain(ctx, queueUrl, windowS: Math.Min(8.0, Math.Max(1.0, (timeout - started.Elapsed).TotalSeconds))))
            {
                if (body is JsonObject o && MarkerOf(o) == marker)
                {
                    return o;
                }

                strangers.Add(body);
            }
        }

        throw Fail(
            $"the projected message for marker '{marker}' never arrived on {queueUrl} within " +
            $"{timeoutS}s (other bodies seen: {Truncate(new JsonArray([.. strangers.Select(s => s?.DeepClone())]).ToJsonString(), 200)})");
    }

    /// <summary>Emit small events with fresh markers until one is delivered, proving the rule is live.</summary>
    /// <remarks>
    /// Re-emitting is required rather than optional: a freshly created rule silently drops events until
    /// the routing goes live, and EventBridge never retries a dropped event. Without this the at-limit
    /// delivery assertion below could fail for a reason that has nothing to do with size.
    /// </remarks>
    private static async Task EmitUntilDelivered(FidelityContext ctx, string bus, Shape shape, int detailLen, string queueUrl)
    {
        var events = ctx.Events;
        var started = FidelityTime.StartNew();
        var attempts = 0;
        while (started.Elapsed < TimeSpan.FromSeconds(LivenessTimeoutS))
        {
            attempts++;
            var marker = Guid.NewGuid().ToString("N");
            var entry = shape.Entry(marker, detailLen);
            entry.EventBusName = bus;
            var resp = await events.PutEventsAsync(new PutEventsRequest { Entries = [entry] });
            Ensure(
                resp.FailedEntryCount == 0,
                $"PutEvents on bus {bus} must be accepted while establishing liveness, got {PutEventsRepr(resp)}");
            foreach (var body in await Drain(ctx, queueUrl, windowS: 8.0))
            {
                if (body is JsonObject && MarkerOf(body) == marker)
                {
                    return;
                }
            }
        }

        throw Fail(
            $"no small control event reached {queueUrl} from bus {bus} after {attempts} PutEvents " +
            $"attempts over {LivenessTimeoutS}s — the rule never went live, so the at-limit delivery " +
            "assertion could not be attempted");
    }
}
