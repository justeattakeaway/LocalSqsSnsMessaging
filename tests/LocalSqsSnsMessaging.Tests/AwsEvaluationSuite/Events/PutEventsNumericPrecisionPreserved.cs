// Translated from behavioral/events/check_put_events_numeric_precision_preserved.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.EventBridge.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Events;

/// <summary>events.put-events-numeric-precision-preserved</summary>
[Category("AwsEvaluationSuite")]
public sealed class PutEventsNumericPrecisionPreserved
{
    private const string Check = "put-events-numeric-precision-preserved";

    /// <summary>
    /// PutEvents Detail integers beyond float64 precision (±int64 max) and the double nearest pi
    /// reach a rule-matched SQS target with their exact values, not rounded through a float.
    /// </summary>
    [Test]
    [DisplayName("events.put-events-numeric-precision-preserved")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var events = ctx.Events;
        var sqs = ctx.Sqs;

        var queueName = FidelityContext.UniqueName("sim2real-behav-q-");
        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = queueName, Tags = new() { ["sim2real-check"] = Check } })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var queueArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] }))
            .Attributes["QueueArn"];

        var source = FidelityContext.UniqueName("sim2real.numeric.");
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

        var nonce = FidelityContext.UniqueName("n-");
        // int64 max fits a signed 64-bit integer but exceeds the 53-bit float64 mantissa, so any
        // float round-trip truncates it. repr(math.pi) round-trips exactly through a double.
        const string bigIntText = "9223372036854775807";
        const string bigDecimalText = "3.141592653589793";
        var detailJson = $$"""{"nonce": "{{nonce}}", "big_int": {{bigIntText}}, "big_decimal": {{bigDecimalText}}, "negative_big_int": -{{bigIntText}}}""";
        Ensure(
            bigIntText != new System.Numerics.BigInteger(double.Parse(bigIntText, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
            $"fixture is not a precision test: '{bigIntText}' round-trips through float64 unchanged");

        var resp = await events.PutEventsAsync(new PutEventsRequest
        {
            Entries = [new PutEventsRequestEntry { Source = source, DetailType = "sim2real-numeric-test", Detail = detailJson }],
        });
        Ensure(resp.FailedEntryCount == 0, string.Join(", ", (resp.Entries ?? []).Select(e => $"({e.EventId}, {e.ErrorCode}, {e.ErrorMessage})")));

        var delivered = await WaitForDetail(sqs, queueUrl, nonce, timeoutS: 30.0);

        var bigInt = decimal.Parse(bigIntText, CultureInfo.InvariantCulture);
        var bigDecimal = decimal.Parse(bigDecimalText, CultureInfo.InvariantCulture);
        Ensure(
            DecimalOf(delivered, "big_int") == bigInt,
            $"big_int drifted: sent '{bigIntText}', delivered {RawOf(delivered, "big_int")}");
        Ensure(
            DecimalOf(delivered, "negative_big_int") == -bigInt,
            $"negative_big_int drifted: sent -'{bigIntText}', delivered {RawOf(delivered, "negative_big_int")}");
        Ensure(
            DecimalOf(delivered, "big_decimal") == bigDecimal,
            $"big_decimal drifted: sent '{bigDecimalText}', delivered {RawOf(delivered, "big_decimal")}");
    }

    /// <summary>
    /// <c>delivered[key]</c> parsed as Python does with <c>parse_float=parse_int=Decimal</c>: a JSON
    /// number's exact value, or <see langword="null"/> (never equal to a Decimal) for anything else.
    /// </summary>
    private static decimal? DecimalOf(JsonElement detail, string key)
    {
        var value = detail.TryGetProperty(key, out var v) ? v : throw new KeyNotFoundException($"'{key}'");
        return value.ValueKind == JsonValueKind.Number
            ? decimal.Parse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;
    }

    private static string RawOf(JsonElement detail, string key) =>
        detail.TryGetProperty(key, out var v) ? v.GetRawText() : "<missing>";

    private static async Task<JsonElement> WaitForDetail(AmazonSQSClient sqs, string queueUrl, string nonce, double timeoutS)
    {
        var started = FidelityTime.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var received = (await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 })).Messages ?? [];
            foreach (var msg in received)
            {
                using var doc = JsonDocument.Parse(msg.Body);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                if (doc.RootElement.TryGetProperty("detail", out var detail)
                    && detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("nonce", out var n)
                    && n.ValueKind == JsonValueKind.String
                    && n.GetString() == nonce)
                {
                    return detail.Clone();
                }
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(0.5));
        }

        throw Fail("numeric-precision event was never delivered to the queue");
    }
}
