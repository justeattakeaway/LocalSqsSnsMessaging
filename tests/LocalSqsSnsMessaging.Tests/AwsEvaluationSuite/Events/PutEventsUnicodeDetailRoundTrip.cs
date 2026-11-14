// Translated from behavioral/events/check_put_events_unicode_detail_round_trip.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-unicode-detail-round-trip</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsUnicodeDetailRoundTrip
{
    private const string Check = "put-events-unicode-detail-round-trip";

    /// <summary>
    /// A PutEvents Detail with multi-byte unicode (Japanese, Cyrillic, emoji, accented Latin) is
    /// delivered to a rule-matched SQS target with every string preserved exactly.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-unicode-detail-round-trip")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-behav-q-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] }))
            .Attributes["QueueArn"];

        var source = FidelityContext.UniqueName("sim2real.unicode.");
        var ruleName = FidelityContext.UniqueName("sim2real-behav-rule-");
        var ruleArn = (await events.PutRuleAsync(new PutRuleRequest
        {
            Name = ruleName,
            EventPattern = $$"""{"source": ["{{source}}"]}""",
            Tags = [new Amazon.EventBridge.Model.Tag { Key = "sim2real-check", Value = Check }],
        })).RuleArn;
        ctx.OnCleanup(() => events.DeleteRuleAsync(new DeleteRuleRequest { Name = ruleName }));

        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            Attributes = new()
            {
                ["Policy"] = new JsonObject
                {
                    ["Version"] = "2012-10-17",
                    ["Statement"] = new JsonArray(new JsonObject
                    {
                        ["Sid"] = "AllowEventsToSendMessage",
                        ["Effect"] = "Allow",
                        ["Principal"] = new JsonObject { ["Service"] = "events.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage",
                        ["Resource"] = queueArn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = ruleArn } },
                    }),
                }.ToJsonString(),
            },
        });

        const string targetId = "sim2real-target";
        await events.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, Targets = [new Target { Id = targetId, Arn = queueArn }] });
        ctx.OnCleanup(() => events.RemoveTargetsAsync(new RemoveTargetsRequest { Rule = ruleName, Ids = [targetId] }));

        var targets = (await events.ListTargetsByRuleAsync(new ListTargetsByRuleRequest { Rule = ruleName })).Targets
            ?? throw new KeyNotFoundException("'Targets'");
        Ensure(
            targets.Any(t => t.Arn == queueArn),
            $"expected target '{queueArn}' in rule targets, got [{string.Join(", ", targets.Select(t => $"'{t.Arn}'"))}]");

        var nonce = FidelityContext.UniqueName("n-");
        (string Key, string Value)[] detail =
        [
            ("nonce", nonce),
            ("japanese", "こんにちは世界"),
            ("cyrillic", "Привет мир"),
            ("emoji", "🚀🎉✨"),
            ("accented", "Café résumé naïve"),
            ("mixed", "AWS-イベント-🔥-тест"),
        ];
        var detailJson = new JsonObject();
        foreach (var (k, v) in detail)
        {
            detailJson[k] = v;
        }

        // json.dumps escapes non-ASCII as \uXXXX; ToJsonString's default encoder does too.
        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = "sim2real-unicode-test", Detail = detailJson.ToJsonString() }],
        });
        Ensure(resp.FailedEntryCount == 0, string.Join(", ", (resp.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})")));

        var delivered = await WaitForDetail(sqs, queueUrl, nonce, timeoutS: 30.0);
        foreach (var (key, value) in detail)
        {
            var got = delivered.TryGetPropertyValue(key, out var node) ? node : throw new KeyNotFoundException($"'{key}'");
            Ensure(
                got is JsonValue v && v.TryGetValue<string>(out var s) && s == value,
                $"field '{key}' drifted: sent '{value}' got {got?.ToJsonString() ?? "None"}");
        }
    }

    private static async Task<JsonObject> WaitForDetail(AmazonSQSClient sqs, string queueUrl, string nonce, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var received = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
            foreach (var msg in received)
            {
                var body = JsonNode.Parse(msg.Body)!.AsObject();
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var detail = (body["detail"] ?? new JsonObject()).AsObject();
                if (detail["nonce"] is JsonValue v && v.TryGetValue<string>(out var s) && s == nonce)
                {
                    return detail;
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.5));
        }

        throw Fail("unicode-detail event was never delivered to the queue");
    }
}
