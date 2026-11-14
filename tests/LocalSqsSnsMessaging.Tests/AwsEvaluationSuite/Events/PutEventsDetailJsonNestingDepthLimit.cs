// Translated from behavioral/events/check_put_events_detail_json_nesting_depth_limit.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-detail-json-nesting-depth-limit</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsDetailJsonNestingDepthLimit
{
    private const int DocDepth = 1000; // documented in the PutEvents API reference
    // Bisection ceiling: well above the documented limit; at ~6 bytes per level the probe stays far
    // below the request-size limit, so a refusal is for depth, not size.
    private const int Ceiling = 4096;
    private const string Source = "sim2real.nesting";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// The deepest Detail PutEvents accepts is the same for nested objects, arrays and a mixed
    /// chain, is the documented 1000 levels (within one), and one level deeper is refused identically.
    ///
    /// Reference: https://docs.aws.amazon.com/cli/latest/reference/events/put-events.html
    /// </summary>
    [Test]
    [DisplayName("events.put-events-detail-json-nesting-depth-limit")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var bus = FidelityContext.UniqueName("sim2real-behav-bus-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = TagsKv() });
        ctx.OnCleanup(() => DeleteBusQuietly(ctx, bus));

        var shapes = new (string Label, Func<int, string> Build)[] { ("objects", Objects), ("arrays", Arrays), ("mixed", Mixed) };
        var measured = new Dictionary<string, int>();
        var rejectShapes = new Dictionary<string, (string Surface, string? Code, string Message)>();

        foreach (var (label, build) in shapes)
        {
            // Positive control per shape: without it the bisection could measure broken wiring.
            Ensure(
                await Accepted(events, bus, build(1)),
                $"positive control failed: a depth-1 {label} Detail must be accepted by PutEvents on " +
                $"bus {bus}; if this fails, every 'rejected' verdict below is meaningless");
            Ensure(
                !await Accepted(events, bus, build(Ceiling)),
                $"a {label} Detail nested {Ceiling} levels deep was ACCEPTED — real EventBridge " +
                $"documents a {DocDepth}-level limit (H1), so a target with no depth limit at all is " +
                $"what this measures");
            var depth = await MaxAcceptedDepth(events, bus, build);
            measured[label] = depth;
            // Confirm the bisection landed on a real edge rather than on a non-monotonic patch.
            Ensure(
                await Accepted(events, bus, build(depth)),
                $"bisection inconsistency for {label}: depth {depth} was accepted during the search but " +
                $"is refused on re-probe — the limit is not stable");
            rejectShapes[label] = await RejectShape(events, bus, build(depth + 1), label: $"{label}@{depth + 1}");
        }

        // The limit is a property of depth, not container kind; a parser's object-only guard differs.
        Ensure(
            measured.Values.Distinct().Count() == 1,
            $"the nesting-depth limit must be a property of depth, not of container kind: measured " +
            $"maximum accepted depth per shape was {Repr(measured)} — a target whose limit differs between " +
            $"nested objects, nested arrays and an alternating chain is applying its parser's guard, " +
            $"not EventBridge's (H3)");
        var measuredDepth = measured["objects"];

        // +-1 because the docs do not say whether the outermost container counts.
        Ensure(
            measuredDepth >= DocDepth - 1 && measuredDepth <= DocDepth + 1,
            $"the maximum accepted nesting depth must be the documented {DocDepth} levels (±1 for the " +
            $"undocumented question of whether the outermost container counts); measured {measuredDepth} " +
            $"across shapes {Repr(measured)} (H2)");

        // A too-deep Detail may be a request-level ClientError or a per-entry ErrorCode; neither is
        // documented, so the check only requires all three shapes to be refused the same way.
        var distinct = rejectShapes.Values.Distinct().ToList();
        Ensure(
            distinct.Count == 1,
            $"one level past the limit must be refused identically for every container shape, got " +
            $"{{{string.Join(", ", rejectShapes.Select(kv => $"'{kv.Key}': {kv.Value}"))}}} (H4)");
        var (surface, code, message) = distinct[0];
        Ensure(
            !string.IsNullOrEmpty(code),
            $"the too-deep refusal must carry a non-empty error code; got surface='{surface}' " +
            $"code='{code}' message='{message}' (H4)");
    }

    private static List<Tag> TagsKv() =>
    [
        new Tag { Key = ManagedTagKey, Value = "true" },
        new Tag { Key = "sim2real:prefix", Value = "sim2real-behav" },
    ];

    /// <summary>
    /// `depth` nested JSON objects. Built as text, never via json.dumps, so this process's own
    /// recursion limit is not what is being measured.
    /// </summary>
    private static string Objects(int depth) =>
        new StringBuilder().Insert(0, "{\"a\":", depth).Append("null").Append('}', depth).ToString();

    /// <summary>One object wrapper plus depth-1 nested arrays: the same total container count as _objects.</summary>
    private static string Arrays(int depth)
    {
        var inner = depth > 1 ? new string('[', depth - 1) + "null" + new string(']', depth - 1) : "null";
        return "{\"a\":" + inner + "}";
    }

    /// <summary>An alternating object/array chain of exactly `depth` containers, outermost an object.</summary>
    private static string Mixed(int depth)
    {
        var opens = new StringBuilder();
        var closes = new List<string>();
        for (var level = 0; level < depth; level++)
        {
            if (level % 2 == 0)
            {
                opens.Append("{\"a\":");
                closes.Add("}");
            }
            else
            {
                opens.Append('[');
                closes.Add("]");
            }
        }

        closes.Reverse();
        return opens.Append("null").Append(string.Concat(closes)).ToString();
    }

    /// <summary>
    /// True iff PutEvents fully accepted the entry: no ClientError, no failed entry, an EventId back.
    ///
    /// A 200 with no EventId is not an acceptance.
    /// </summary>
    private static async Task<bool> Accepted(AmazonEventBridgeClient events, string bus, string detail)
    {
        var (resp, error) = await FidelityContext.Capture(() => events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = Source, DetailType = "NestingProbe", Detail = detail, EventBusName = bus }],
        }));
        if (error is not null)
        {
            return false;
        }

        if ((resp!.FailedEntryCount ?? 0) != 0)
        {
            return false;
        }

        var entries = resp.Entries ?? [];
        return entries.Count == 1 && !string.IsNullOrEmpty(entries[0].EventId);
    }

    /// <summary>
    /// ("exception"|"entry", code, message) for a Detail expected to be refused; RAISES on acceptance.
    ///
    /// Raising, rather than returning a falsy sentinel, keeps an acceptance from passing as a mismatch.
    /// </summary>
    private static async Task<(string Surface, string? Code, string Message)> RejectShape(AmazonEventBridgeClient events, string bus, string detail, string label)
    {
        var (resp, error) = await FidelityContext.Capture(() => events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = Source, DetailType = "NestingProbe", Detail = detail, EventBusName = bus }],
        }));
        if (error is not null)
        {
            return ("exception", error.Code, error.Message ?? "");
        }

        var entries = resp!.Entries ?? [];
        if ((resp.FailedEntryCount ?? 0) != 0 && entries.Count > 0 && string.IsNullOrEmpty(entries[0].EventId))
        {
            return ("entry", entries[0].ErrorCode, entries[0].ErrorMessage ?? "");
        }

        var rendered = $"{{\"FailedEntryCount\": {resp.FailedEntryCount}, \"Entries\": [{string.Join(", ", entries.Select(e => $"{{\"EventId\": \"{e.EventId}\"}}"))}]}}";
        throw Fail(
            $"PutEvents ACCEPTED the over-deep Detail probe {label} — expected a refusal one level past " +
            $"the measured maximum. Response was {rendered[..Math.Min(300, rendered.Length)]}");
    }

    /// <summary>
    /// Bisect [1, _CEILING] for the greatest depth PutEvents accepts.
    ///
    /// The caller has already proved depth 1 is accepted and depth _CEILING is refused, so the interval
    /// is bracketed on both sides before the search starts and the loop is bounded by log2(_CEILING).
    /// </summary>
    private static async Task<int> MaxAcceptedDepth(AmazonEventBridgeClient events, string bus, Func<int, string> build)
    {
        var (low, high) = (1, Ceiling); // accepted, refused
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (await Accepted(events, bus, build(mid)))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static async Task DeleteBusQuietly(FidelityContext ctx, string name)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = name }));
        if (error is not null && error.Code != "ResourceNotFoundException")
        {
            throw new InvalidOperationException($"could not delete event bus {name}: {error}", error.Exception);
        }
    }

    private static string Repr(Dictionary<string, int> measured) =>
        $"{{{string.Join(", ", measured.Select(kv => $"'{kv.Key}': {kv.Value}"))}}}";
}
