// Translated from behavioral/events/check_queue_policy_deny_and_missing_allow_both_dead_letter_as_no_resource_without_retries.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.EventBridge;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using Tag = Amazon.EventBridge.Model.Tag;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.queue-policy-deny-and-missing-allow-both-dead-letter-as-no-resource-without-retries</summary>
[Category("AwsEvaluationSuite")]
public sealed partial class QueuePolicyDenyAndMissingAllowBothDeadLetterAsNoResourceWithoutRetries
{
    private const string Check = "queue-policy-deny-and-missing-allow-both-dead-letter-as-no-resource-without-retries";
    private static readonly string[] DeadLetterKeys = ["ERROR_CODE", "ERROR_MESSAGE", "RULE_ARN", "TARGET_ARN"];

    // SQS's own errors as EventBridge records them; the minted request id is shaped.
    private const string NonExistentQueue =
        "The specified queue does not exist or you do not have access to it. (Service: AmazonSQS; " +
        "Status Code: 400; Error Code: AWS.SimpleQueueService.NonExistentQueue; Request ID: <id>; Proxy: null)";

    private static RetryPolicy Retry() => new() { MaximumRetryAttempts = 185, MaximumEventAgeInSeconds = 86400 };

    /// <summary>
    /// A queue whose policy denies events.amazonaws.com and a queue with no policy are both dead-lettered once,
    /// without retries, as NO_RESOURCE with SQS's NonExistentQueue message and the rule's ARN, while a healthy
    /// sibling receives the event; replacing the Deny with an Allow lets the next event land.
    /// </summary>
    [Test]
    [DisplayName("events.queue-policy-deny-and-missing-allow-both-dead-letter-as-no-resource-without-retries")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.EventBridgeUngrantedTargets, "Kept deliberately: the queue whose policy denies EventBridge dead-letters as NO_RESOURCE, but the queue with no policy still receives the event; AWS dead-letters it too")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var bus = FidelityContext.UniqueName("sim2real-evb-dlq-");
        await events.CreateEventBusAsync(new CreateEventBusRequest { Name = bus, Tags = [new Tag { Key = "sim2real", Value = Check }] });
        ctx.OnCleanup(() => events.DeleteEventBusAsync(new DeleteEventBusRequest { Name = bus }));
        var source = FidelityContext.UniqueName("sim2real.deny.");
        var rule = FidelityContext.UniqueName("sim2real-evb-denyrule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = rule,
            EventBusName = bus,
            EventPattern = $$"""{"source": ["{{source}}"]}""",
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = rule, EventBusName = bus }));

        var (deniedUrl, deniedArn) = await Queue(ctx, sqs, "deny");
        var (bareUrl, bareArn) = await Queue(ctx, sqs, "bare");
        var (healthyUrl, healthyArn) = await Queue(ctx, sqs, "ok");
        var (dlqUrl, dlqArn) = await Queue(ctx, sqs, "dlq");
        await SetPolicy(sqs, deniedUrl, deniedArn, ruleArn, "Deny");
        await SetPolicy(sqs, healthyUrl, healthyArn, ruleArn, "Allow");
        await SetPolicy(sqs, dlqUrl, dlqArn, ruleArn, "Allow");

        List<string> ids = ["denied", "bare", "healthy"];
        var resp = await events.PutTargetsAsync(new PutTargetsRequest
        {
            Rule = rule,
            EventBusName = bus,
            Targets =
            [
                new Target { Id = "denied", Arn = deniedArn, RetryPolicy = Retry(), DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn } },
                new Target { Id = "bare", Arn = bareArn, RetryPolicy = Retry(), DeadLetterConfig = new DeadLetterConfig { Arn = dlqArn } },
                new Target { Id = "healthy", Arn = healthyArn },
            ],
        });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = rule, EventBusName = bus, Ids = ids }));
        Ensure(resp.FailedEntryCount == 0, $"PutTargets reported {resp.FailedEntries?.Count} failed entries");

        var eventId = await Put(events, bus, source, FidelityContext.UniqueName("m-"));
        var (healthy, _) = await Await(sqs, healthyUrl, (b, _) => (string?)b?["id"] == eventId, 90.0);

        var records = new Dictionary<string, (JsonNode? Body, Dictionary<string, string?> Attrs)>(StringComparer.Ordinal);
        var deadline = FidelityTime.StartNew();
        while (records.Count < 2 && deadline.Elapsed < TimeSpan.FromSeconds(150.0))
        {
            foreach (var (b, a) in await Receive(sqs, dlqUrl))
            {
                if ((string?)b?["id"] == eventId)
                {
                    records[a.GetValueOrDefault("TARGET_ARN") ?? ""] = (b, a);
                }
            }
        }

        Ensure(
            records.Keys.ToHashSet().SetEquals([deniedArn, bareArn]),
            $"dead-letter records arrived for {records.Count} target(s), expected the denying and the bare queue");

        var (body, attrs) = records[deniedArn];
        Ensure(attrs.Keys.Order(StringComparer.Ordinal).SequenceEqual(DeadLetterKeys), $"the denied target's dead-letter attributes were {Sorted(attrs.Keys)}");
        Ensure(attrs["ERROR_CODE"] == "NO_RESOURCE", $"an explicit queue-policy Deny dead-lettered as '{attrs["ERROR_CODE"]}'");
        Ensure(Shaped(attrs["ERROR_MESSAGE"]) == NonExistentQueue, $"the denied target's ERROR_MESSAGE was '{Shaped(attrs["ERROR_MESSAGE"])}'");
        Ensure(attrs["RULE_ARN"] == ruleArn, "the denied target's dead-letter RULE_ARN does not name the rule");
        Ensure(JsonNode.DeepEquals(body, healthy), "the denied target's dead-lettered body differs from the delivered event");

        (body, attrs) = records[bareArn];
        Ensure(attrs.Keys.Order(StringComparer.Ordinal).SequenceEqual(DeadLetterKeys), $"the bare target's dead-letter attributes were {Sorted(attrs.Keys)}");
        Ensure(attrs["ERROR_CODE"] == "NO_RESOURCE", $"a queue with no policy dead-lettered as '{attrs["ERROR_CODE"]}'");
        Ensure(Shaped(attrs["ERROR_MESSAGE"]) == NonExistentQueue, $"the bare target's ERROR_MESSAGE was '{Shaped(attrs["ERROR_MESSAGE"])}'");
        Ensure(attrs["RULE_ARN"] == ruleArn, "the bare target's dead-letter RULE_ARN does not name the rule");
        Ensure(JsonNode.DeepEquals(body, healthy), "the bare target's dead-lettered body differs from the delivered event");

        foreach (var (label, url) in new[] { ("denying", deniedUrl), ("bare", bareUrl) })
        {
            var left = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 })).Messages ?? [];
            Ensure(left.Count == 0, $"the {label} queue holds {left.Count} message(s)");
        }

        // Restore: the one broken thing is repaired, and the same target now receives.
        await SetPolicy(sqs, deniedUrl, deniedArn, ruleArn, "Allow");
        var got = await ResendUntil(events, sqs, bus, source, FidelityContext.UniqueName("m2-"), deniedUrl, timeoutS: 120.0);
        Ensure((string?)got?["source"] == source, $"event delivered after the repair had source '{got?["source"]}'");
    }

    private static string Shaped(string? message) => RequestIdPattern().Replace(message ?? "", "Request ID: <id>;");

    [GeneratedRegex("Request ID: [^;]+;")]
    private static partial Regex RequestIdPattern();

    private static async Task<(string Url, string Arn)> Queue(FidelityContext ctx, AmazonSQSClient sqs, string label)
    {
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName($"sim2real-evb-{label}-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        return (url, arn);
    }

    private static Task SetPolicy(AmazonSQSClient sqs, string url, string arn, string ruleArn, string effect) =>
        sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = url,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Effect"] = effect,
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = arn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
                    }),
                }.ToJsonString(),
            },
        });

    private static async Task<string> Put(AmazonEventBridgeClient events, string bus, string source, string marker)
    {
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries =
            [
                new PutEventsRequestEntry
                {
                    EventBusName = bus,
                    Source = source,
                    DetailType = "sim2real-dlq",
                    Detail = $$"""{"marker": "{{marker}}"}""",
                },
            ],
        });
        Ensure(resp.FailedEntryCount == 0, $"PutEvents rejected the entry: {string.Join(", ", (resp.Entries ?? []).Select(e => $"{{'ErrorCode': '{e.ErrorCode}'}}"))}");
        return (resp.Entries ?? throw new KeyNotFoundException("'Entries'"))[0].EventId;
    }

    private static async Task<List<(JsonNode? Body, Dictionary<string, string?> Attrs)>> Receive(AmazonSQSClient sqs, string url)
    {
        var output = new List<(JsonNode?, Dictionary<string, string?>)>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 5,
            MessageAttributeNames = ["All"],
        });
        foreach (var m in resp.Messages ?? [])
        {
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
            var attrs = (m.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => (string?)kv.Value.StringValue, StringComparer.Ordinal);
            output.Add((JsonNode.Parse(m.Body), attrs));
        }

        return output;
    }

    private static async Task<(JsonNode? Body, Dictionary<string, string?> Attrs)> Await(
        AmazonSQSClient sqs, string url, Func<JsonNode?, Dictionary<string, string?>, bool> match, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            foreach (var (body, attrs) in await Receive(sqs, url))
            {
                if (match(body, attrs))
                {
                    return (body, attrs);
                }
            }
        }

        throw Fail($"no matching message reached the queue within {timeoutS:0.0}s");
    }

    /// <summary>Send events until one lands on <c>url</c> (queue-policy propagation); returns its body.</summary>
    private static async Task<JsonNode?> ResendUntil(
        AmazonEventBridgeClient events, AmazonSQSClient sqs, string bus, string source, string marker, string url, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var n = 0;
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            n++;
            sent.Add(await Put(events, bus, source, $"{marker}-{n}"));
            foreach (var (body, _) in await Receive(sqs, url))
            {
                if (body?["id"] is JsonValue id && sent.Contains((string?)id ?? ""))
                {
                    return body;
                }
            }
        }

        throw Fail($"after the queue policy was repaired no event reached the target in {timeoutS:0.0}s");
    }

    private static string Sorted(IEnumerable<string> items) => "[" + string.Join(", ", items.Order(StringComparer.Ordinal).Select(i => $"'{i}'")) + "]";
}
