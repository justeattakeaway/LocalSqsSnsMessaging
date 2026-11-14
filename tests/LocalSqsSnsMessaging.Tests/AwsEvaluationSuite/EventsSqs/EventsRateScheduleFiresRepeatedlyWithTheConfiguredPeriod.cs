// Translated from behavioral/events__sqs/check_events_rate_schedule_fires_repeatedly_with_the_configured_period.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.EventsSqs;

/// <summary>events__sqs.events-rate-schedule-fires-repeatedly-with-the-configured-period</summary>
[Category("AwsEvaluationSuite")]
public sealed class EventsRateScheduleFiresRepeatedlyWithTheConfiguredPeriod
{
    private const double PeriodS = 60.0;
    private const string XsvcPrefix = "sim2real-xsvc-";
    private const string ManagedTagKey = "sim2real:managed";

    /// <summary>
    /// A rate(1 minute) rule really fires as a CADENCE (consecutive deliveries about one period
    /// apart), and DisableRule really stops it: no delivery after two further scheduled slots.
    /// </summary>
    // timing:real-time — schedule: 60 s https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-create-rule-schedule.html
    [Test]
    [DisplayName("events__sqs.events-rate-schedule-fires-repeatedly-with-the-configured-period")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [Category(ApplyDefaultCategoryAttribute.TimeBasedCategoryName)]
    [KnownFidelityGap(FidelityGap.EventBridgeSchedules, "A rate(1 minute) rule is accepted but never fires: no scheduled event reaches its SQS target in 300 s, where AWS delivers one a minute")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.RealTimeBudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var queue = await CreateQueue(ctx);

        // Schedules are only permitted on the DEFAULT bus, so no bus is created here.
        var rule = XName("sched");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest { Name = rule, ScheduleExpression = "rate(1 minute)", State = RuleState.ENABLED })).RuleArn;
        ctx.OnCleanup(() => ctx.Events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, Force = true }));
        await SetQueuePolicy(ctx, queue.Url, AllowServiceSend(queue.Arn, "events.amazonaws.com", ruleArn));
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = rule, Targets = [new Target { Id = "t1", Arn = queue.Arn }] });
        ctx.OnCleanup(() => ctx.Events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, Ids = ["t1"], Force = true }));

        var fired = new List<JsonObject>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(300.0) && fired.Count < 3)
        {
            foreach (var msg in await Collect(ctx, queue.Url, windowS: 15.0, maxTotal: 50))
            {
                if (Body(msg) is JsonObject body && NamesResource(body, ruleArn))
                {
                    fired.Add(body);
                }
            }
        }

        Ensure(
            fired.Count >= 3,
            $"the rate(1 minute) rule {rule} delivered only {fired.Count} scheduled event(s) within 300 s, " +
            $"so no cadence is observable: [{string.Join(", ", fired.Select(PyRepr))}]");

        foreach (var env in fired)
        {
            Ensure(
                Str(env, "detail-type") == "Scheduled Event",
                $"a scheduled delivery carried detail-type={PyRepr(env["detail-type"])}, expected " +
                $"'Scheduled Event': {PyRepr(env)}");
            Ensure(
                env["detail"] is JsonObject { Count: 0 },
                $"a scheduled delivery carried a non-empty detail {PyRepr(env["detail"])}; a scheduled " +
                $"event has no payload: {PyRepr(env)}");
            Ensure(
                JsonNode.DeepEquals(env["resources"], new JsonArray(ruleArn)),
                $"a scheduled delivery named resources={PyRepr(env["resources"])}, expected exactly " +
                $"['{ruleArn}'] -- the envelope must attribute the firing to the rule that caused it");
            Ensure(
                Str(env, "source") == "aws.events",
                $"a scheduled delivery carried source={PyRepr(env["source"])}, expected 'aws.events': " +
                $"{PyRepr(env)}");
        }

        var stamps = fired.Select(env => Epoch(Str(env, "time")!)).ToList();
        var times = fired.Select(env => Str(env, "time")!).ToList();
        Ensure(
            stamps.SequenceEqual(stamps.Order()) && stamps.Distinct().Count() == stamps.Count,
            $"the scheduled firings' envelope 'time' values are not strictly increasing: {Repr(times)}");
        var gaps = stamps.Zip(stamps.Skip(1), (a, b) => Math.Round(b - a, 1)).ToList();
        var bad = gaps.Where(g => !(0.5 * PeriodS <= g && g <= 2.0 * PeriodS)).ToList();
        Ensure(
            bad.Count == 0,
            $"consecutive scheduled firings were {Floats(gaps)} seconds apart; every gap must fall within " +
            $"{Float(0.5 * PeriodS)}-{Float(2.0 * PeriodS)}s of the configured rate(1 minute). The band is wide on " +
            "purpose -- it compares two observed timestamps to each other, not to a latency budget -- so " +
            $"{Floats(bad)} means the rate expression is not driving the cadence at all. Observed times: " +
            $"{Repr(times)}");

        await events.DisableRuleAsync(new DisableRuleRequest { Name = rule });
        var state = (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })).State?.Value;
        Ensure(
            state == "DISABLED",
            $"DisableRule did not take effect: rule {rule} reports State='{state}', expected 'DISABLED'");
        // Drain whatever the scheduler had already handed to SQS before the disable landed.
        await Collect(ctx, queue.Url, windowS: 15.0, maxTotal: 50);

        // Watch until two FURTHER scheduled slots, predicted from the last observed firing, have passed.
        // Deriving the window from the observed cadence is what makes the emptiness meaningful: at least
        // two deliveries were due in it, so zero is suppression and not a short window.
        var watchUntilEpoch = stamps[^1] + 2 * PeriodS + 10.0;
        var late = new List<JsonObject>();
        while (FidelityTime.UtcNow.ToUnixTimeMilliseconds() / 1000.0 < watchUntilEpoch)
        {
            foreach (var msg in await Collect(ctx, queue.Url, windowS: 15.0, maxTotal: 50))
            {
                if (Body(msg) is JsonObject body && NamesResource(body, ruleArn))
                {
                    late.Add(body);
                }
            }
        }

        Ensure(
            late.Count == 0,
            $"the DISABLED rule {rule} kept firing: {late.Count} scheduled event(s) arrived in the window " +
            $"ending at epoch {Float(watchUntilEpoch)}, which covered two further scheduled slots after the " +
            $"last firing observed while it was ENABLED ('{times[^1]}'). Late envelopes: " +
            $"[{string.Join(", ", late.Select(e => PyRepr(e["time"])))}]");
        Ensure(
            (await events.DescribeRuleAsync(new DescribeRuleRequest { Name = rule })).State?.Value == "DISABLED",
            $"rule {rule} did not stay DISABLED for the duration of the absence window");
    }

    /// <summary>rule_arn in (body.get("resources") or []).</summary>
    private static bool NamesResource(JsonObject body, string ruleArn) =>
        body["resources"] is JsonArray resources && resources.Any(r => r is JsonValue v && v.TryGetValue<string>(out var s) && s == ruleArn);

    private static string? Str(JsonObject body, string key) =>
        body[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Repr(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(i => $"'{i}'")) + "]";

    /// <summary>A Python float's repr (always with a decimal point).</summary>
    private static string Float(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.Contains('.', StringComparison.Ordinal) || s.Contains('E', StringComparison.Ordinal) ? s : s + ".0";
    }

    private static string Floats(IEnumerable<double> ds) => "[" + string.Join(", ", ds.Select(Float)) + "]";

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
    /// Parse an EventBridge envelope 'time' (RFC3339, always Z-suffixed) to epoch seconds.
    /// </summary>
    /// <remarks>
    /// Parsed structurally rather than by string surgery so a change of offset form does not silently
    /// turn into a wrong gap.
    /// </remarks>
    private static double Epoch(string iso) =>
        DateTimeOffset.Parse(iso.Replace("Z", "+00:00", StringComparison.Ordinal), CultureInfo.InvariantCulture).ToUnixTimeMilliseconds() / 1000.0;

    private sealed record Queue(string Name, string Url, string Arn);

    private static Dictionary<string, string> XsvcResourceTags() =>
        new() { [ManagedTagKey] = "true", ["sim2real:prefix"] = XsvcPrefix.TrimEnd('-') };

    /// <summary>A unique, prefix-tagged resource name: sim2real-xsvc-&lt;kind&gt;-&lt;uuid&gt;.</summary>
    private static string XName(string kind) => FidelityContext.UniqueName($"{XsvcPrefix}{kind}-");

    /// <summary>Create an SQS queue (registers delete_queue teardown). Returns {name,url,arn}.</summary>
    private static async Task<Queue> CreateQueue(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attrs = null)
    {
        var baseName = XName("q");
        var name = fifo ? $"{baseName}.fifo" : baseName;
        var sqs = ctx.Sqs;
        var createAttrs = new Dictionary<string, string>(attrs ?? []);
        if (fifo)
        {
            createAttrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = createAttrs, Tags = XsvcResourceTags() })).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        return new Queue(name, url, arn);
    }

    /// <summary>Parse an SQS message body as JSON, falling back to the raw string.</summary>
    private static JsonNode? Body(SqsMessage msg)
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

    /// <summary>
    /// Drain a queue over a bounded window with long-polling ReceiveMessage(MaxNumberOfMessages=10)
    /// and return EVERY message seen. Never raises and never swallows an assertion.
    /// </summary>
    /// <remarks>
    /// This is the NEGATIVE observable. An empty return is only meaningful once a positive control
    /// has already been observed elsewhere in the check -- that is what proves enough wall-clock and
    /// enough propagation elapsed for a delivery to have happened if the emulator were going to make
    /// one. Used alone it would pass against an emulator that delivers nothing at all.
    /// </remarks>
    private static async Task<List<SqsMessage>> Collect(FidelityContext ctx, string queueUrl, double windowS = 30.0, int maxTotal = 10, bool keep = false)
    {
        var sqs = ctx.Sqs;
        var seen = new List<SqsMessage>();
        var started = FidelityTime.StartNew();
        var window = TimeSpan.FromSeconds(windowS);
        while (started.Elapsed < window && seen.Count < maxTotal)
        {
            var wait = Math.Min(5, Math.Max(1, (int)(window - started.Elapsed).TotalSeconds));
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = wait,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                seen.Add(msg);
                if (!keep)
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                }
            }
        }

        return seen;
    }


    /// <summary>A queue policy granting one AWS service SendMessage on this queue, scoped by SourceArn.</summary>
    private static JsonObject AllowServiceSend(string queueArn, string service, string sourceArn) => new()
    {
        ["Version"] = "2012-10-17",
        ["Statement"] = new JsonArray(new JsonObject
        {
            ["Sid"] = "AllowServiceSend",
            ["Effect"] = "Allow",
            ["Principal"] = new JsonObject { ["Service"] = service },
            ["Action"] = "sqs:SendMessage",
            ["Resource"] = queueArn,
            ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = sourceArn } },
        }),
    };

    private static Task SetQueuePolicy(FidelityContext ctx, string queueUrl, JsonObject policy) =>
        ctx.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new() { ["Policy"] = policy.ToJsonString() },
        });
}
