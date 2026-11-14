// Translated from behavioral/sns__sqs/check_a_message_filtered_out_at_publish_time_is_never_redelivered_when_the_policy_is_relaxed.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.a-message-filtered-out-at-publish-time-is-never-redelivered-when-the-policy-is-relaxed</summary>
[Category("AwsEvaluationSuite")]
public sealed class AMessageFilteredOutAtPublishTimeIsNeverRedeliveredWhenThePolicyIsRelaxed
{
    private const string Check = "a-message-filtered-out-at-publish-time-is-never-redelivered-when-the-policy-is-relaxed";

    // json.dumps(_NARROW) and json.dumps(_WIDE), byte for byte.
    private const string Narrow = """{"kind": ["wanted"]}""";
    private const string Wide = """{"kind": ["wanted", "unwanted"]}""";
    private const int PolicyPropagationWaitS = 900; // AWS's own documented "up to 15 minutes"

    /// <summary>A bounded retry that RAISES on its deadline; never returns its last value on timeout.</summary>
    private static async Task<List<string>> PollUntil(Func<Task<List<string>>> probe, Func<List<string>, bool> predicate,
        double timeoutS, double intervalS = 3.0, string description = "condition")
    {
        var deadline = FidelityTime.StartNew();
        List<string>? last = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {(last is null ? "None" : Show(last))}");
    }

    private static async Task<(string Url, string Arn)> MakeQueue(FidelityContext ctx, string name, string topicArn, string checkTag)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real-check"] = checkTag } })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Sid"] = "AllowSns",
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = arn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
                    }),
                }.ToJsonString(),
            },
        });
        return (url, arn);
    }

    /// <summary>
    /// Returns the parsed nonces seen. `.get("Messages", [])` feeds a loop whose result is asserted
    /// by exact membership — the sanctioned polarity for an empty default.
    /// </summary>
    private static async Task<List<string>> Drain(FidelityContext ctx, string url, double seconds)
    {
        var sqs = ctx.Sqs;
        var output = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                var envelope = JsonNode.Parse(msg.Body)!;
                output.Add((string)JsonNode.Parse((string)envelope["Message"]!)!["nonce"]!);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }
        }

        return output;
    }

    private static async Task<int> QueueDepth(FidelityContext ctx, string url)
    {
        var attrs = (await ctx.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url,
            AttributeNames =
            [
                "ApproximateNumberOfMessages",
                "ApproximateNumberOfMessagesNotVisible",
                "ApproximateNumberOfMessagesDelayed",
            ],
        })).Attributes ?? [];
        return attrs.Values.Sum(v => int.Parse(v, CultureInfo.InvariantCulture));
    }

    private static async Task<string> Subscribe(FidelityContext ctx, string topicArn, string queueArn, Dictionary<string, string> attributes)
    {
        var arn = (await ctx.Sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = attributes,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = arn }));
        return arn;
    }

    private static string Show(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(v => $"'{v}'")) + "]";

    private static string F(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Hex10() => Guid.NewGuid().ToString("N")[..10];

    // ===== the check =====

    /// <summary>
    /// A message a subscription filter rejected at publish time is never delivered, not even after
    /// the filter is widened to admit it — while the widened filter demonstrably admits new messages.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.a-message-filtered-out-at-publish-time-is-never-redelivered-when-the-policy-is-relaxed")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var checkTag = Check;
        var suffix = Hex10();

        var topicName = FidelityContext.UniqueName("sim2real-nd-noretro-");
        var topicArn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = topicName,
            Tags = [new() { Key = "sim2real-check", Value = checkTag }],
        })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topicArn }));

        var (subjectUrl, subjectArn) = await MakeQueue(ctx, $"sim2real-nd-noretro-subj-{suffix}", topicArn, checkTag);
        var (twinUrl, twinArn) = await MakeQueue(ctx, $"sim2real-nd-noretro-twin-{suffix}", topicArn, checkTag);

        var subjectSub = await Subscribe(ctx, topicArn, subjectArn,
            new() { ["FilterPolicy"] = Narrow, ["RawMessageDelivery"] = "false" });
        // The twin has NO filter policy, so it does the same work at the same instant and every absence
        // on the subject is attributable to the one thing that differs.
        var twinSub = await Subscribe(ctx, topicArn, twinArn, new() { ["RawMessageDelivery"] = "false" });
        var narrowStored = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subjectSub })).Attributes ?? [];
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(narrowStored["FilterPolicy"]), JsonNode.Parse(Narrow)),
            $"the subject subscription stored FilterPolicy='{narrowStored.GetValueOrDefault("FilterPolicy")}', " +
            $"expected '{Narrow}' — an unfiltered subject would make the absence below " +
            "unfalsifiable");
        Ensure(!((await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = twinSub })).Attributes ?? []).ContainsKey("FilterPolicy"),
            $"the TWIN subscription {twinSub} carries a FilterPolicy, so it is not an unfiltered " +
            "control and cannot establish that each publish reached SNS");

        async Task<string> Publish(string kind, string nonce)
        {
            var resp = await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn,
                Message = $$"""{"nonce": "{{nonce}}"}""",
                MessageAttributes = new() { ["kind"] = new SnsMessageAttributeValue { DataType = "String", StringValue = kind } },
            });
            Ensure(!string.IsNullOrEmpty(resp.MessageId), $"Publish returned no MessageId for '{nonce}': {resp}");
            return resp.MessageId;
        }

        // BEAT 1 — the narrow policy admits its own kind, on this exact wiring.
        var warm = $"warm-{Hex10()}";
        var t0 = FidelityTime.StartNew();
        await Publish("wanted", warm);
        var got = await PollUntil(
            () => Drain(ctx, subjectUrl, seconds: 4),
            nonces => nonces.Contains(warm),
            timeoutS: 120,
            intervalS: 0,
            description:
                $"the narrow policy {Narrow} admitted kind=wanted to {subjectUrl} — the " +
                "positive control that makes the withholding below evidence");
        var latency = Math.Max(t0.Elapsed.TotalSeconds, 1.0);
        Ensure(got.SequenceEqual([warm]),
            $"the subject queue carried {Show(got)} on the warm-up, expected exactly ['{warm}']");
        await Drain(ctx, twinUrl, seconds: Math.Max(latency, 6.0)); // clear the twin's warm-up copy

        // BEAT 2 — the withheld message. Nothing structural stops it later; only SNS's publish-time
        // evaluation does, which is precisely the property under test.
        var withheld = $"withheld-{Hex10()}";
        await Publish("unwanted", withheld);
        var twinSaw = await PollUntil(
            () => Drain(ctx, twinUrl, seconds: 4),
            nonces => nonces.Contains(withheld),
            timeoutS: 120,
            intervalS: 0,
            description:
                $"the UNFILTERED twin received '{withheld}', proving the publish reached SNS and fanned " +
                "out — so the subject's emptiness is the filter policy's doing");
        Ensure(twinSaw.Contains(withheld), $"the twin never received '{withheld}': {Show(twinSaw)}");
        var hold = Math.Max(3 * latency, 15.0);
        var early = await Drain(ctx, subjectUrl, seconds: hold);
        Ensure(early.Count == 0,
            $"the subject subscription received {Show(early)} during a {F(hold, 0)}s hold (3x the " +
            $"{F(latency, 0)}s measured latency) for a message its policy {Narrow} rejects");
        Ensure(await QueueDepth(ctx, subjectUrl) == 0,
            $"the subject queue reports depth {await QueueDepth(ctx, subjectUrl)} behind an empty receive — " +
            "the second independent observable disagrees with the first");

        // BEAT 3 — widen the policy. Everything from here on is about the withheld message NOT coming
        // back, and about the widened policy nonetheless working.
        await sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = subjectSub,
            AttributeName = "FilterPolicy",
            AttributeValue = Wide,
        });
        var wideStored = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subjectSub })).Attributes ?? [];
        Ensure(JsonNode.DeepEquals(JsonNode.Parse(wideStored["FilterPolicy"]), JsonNode.Parse(Wide)),
            $"SetSubscriptionAttributes did not store the widened policy: " +
            $"'{wideStored.GetValueOrDefault("FilterPolicy")}', expected '{Wide}'");

        // The restore beat, robust to AWS's documented 15-minute filter-policy propagation: re-publish a
        // FRESH nonce on every attempt and record everything the subject queue ever sees.
        var seen = new List<string>();
        var probes = new List<string>();

        async Task<List<string>> Probe()
        {
            var nonce = $"probe-{Hex10()}";
            probes.Add(nonce);
            await Publish("unwanted", nonce);
            seen.AddRange(await Drain(ctx, subjectUrl, seconds: 6));
            return seen;
        }

        await PollUntil(
            Probe,
            nonces => probes.Any(nonces.Contains),
            timeoutS: PolicyPropagationWaitS,
            intervalS: 10,
            description:
                $"the widened policy {Wide} began admitting kind=unwanted within AWS's own " +
                $"documented {PolicyPropagationWaitS}s filter-policy propagation ceiling — the " +
                "restore beat, without which 'the withheld message never came back' would also hold for " +
                "a subscription that stopped delivering entirely");
        Ensure(probes.Any(seen.Contains),
            $"the widened policy never admitted any of the {probes.Count} probe publishes: seen={Show(seen)}");

        // THE CLAIM. The message rejected at publish time is absent from EVERYTHING the queue ever
        // received, including after the widening that admits an identical message.
        seen.AddRange(await Drain(ctx, subjectUrl, seconds: hold));
        Ensure(!seen.Contains(withheld),
            $"the message '{withheld}', rejected by the narrow policy at publish time, was delivered " +
            $"after the policy widened. Everything the subject queue received: {Show(seen)}. SNS evaluates a " +
            "filter policy against the INCOMING message; there is no backlog to flush, and a target that " +
            "flushes one is holding customer messages the customer was told were dropped");
        Ensure(!seen.Contains(warm),
            $"the warm-up message '{warm}' was delivered a SECOND time after the policy widened " +
            $"(everything seen: {Show(seen)}) — an already-delivered message must not be re-evaluated either");
        Ensure(await QueueDepth(ctx, subjectUrl) == 0,
            $"the subject queue reports depth {await QueueDepth(ctx, subjectUrl)} at the end of the check — " +
            $"something is still in flight, so the absence of '{withheld}' is not yet stable");
    }
}
