// Translated from behavioral/sns__sqs/check_message_attribute_name_outside_the_allowed_charset_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.message-attribute-name-outside-the-allowed-charset-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class MessageAttributeNameOutsideTheAllowedCharsetRejected
{
    private const string Check = "message-attribute-name-outside-the-allowed-charset-rejected";
    private const string Allowed = "alphanumeric characters, hyphens, underscores, or dots";

    /// <summary>
    /// Publish refuses a message attribute name holding a space, tab, '#', '/', ':' or '~' with ParameterValueInvalid
    /// naming the character as an unpadded uppercase hex reference ('#x20', '#x9', '#x2F') and delivers nothing; a
    /// non-ASCII letter and every allowed punctuation mark are delivered under the published names.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.message-attribute-name-outside-the-allowed-charset-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;

        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-xsvc-sns-attrchar-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(Teardown(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }), "NotFound"));
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName("sim2real-xsvc-sns-attrchar-"),
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(Teardown(() => sqs.DeleteQueueAsync(url),
            "AWS.SimpleQueueService.NonExistentQueue", "QueueDoesNotExist"));
        var qArn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
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
                        ["Effect"] = "Allow", ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                        ["Action"] = "sqs:SendMessage", ["Resource"] = qArn,
                        ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topic } },
                    }),
                }.ToJsonString(),
            },
        });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topic,
            Protocol = "sqs",
            Endpoint = qArn,
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        ctx.OnCleanup(Teardown(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }), "NotFound"));

        var refused = new Dictionary<string, string>
        {
            ["a b"] = "#x20", ["a\tb"] = "#x9", ["a#b"] = "#x23", ["a/b"] = "#x2F",
            ["a:b"] = "#x3A", ["a~b"] = "#x7E",
        };
        var wrong = new Dictionary<string, (string? Code, string? Message)>();
        foreach (var (name, @ref) in refused)
        {
            var got = await Error(() => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topic,
                Message = $"refused-attrchar-{@ref}",
                MessageAttributes = new() { [name] = new SnsMessageAttributeValue { DataType = "String", StringValue = "v" } },
            }));
            var want = ("ParameterValueInvalid",
                $"Invalid non-alphanumeric character '{@ref}' was found in the message " +
                $"attribute name. Can only include {Allowed}.");
            if (got != want)
            {
                wrong[name] = got;
            }
        }

        Ensure(wrong.Count == 0,
            $"attribute names outside the charset were not refused as AWS does: {{{string.Join(", ", wrong.Select(kv => $"'{kv.Key}': ('{kv.Value.Code}', '{kv.Value.Message}')"))}}}");

        // Positive control on the same subscription: allowed punctuation and a non-ASCII letter.
        var marker = FidelityContext.UniqueName("sim2real-attrchar-ok-");
        var accepted = new Dictionary<string, string> { ["A-b_c.d9"] = "punct", ["café"] = "letter" };
        var clock = FidelityTime.StartNew();
        var start = clock.Elapsed;
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topic,
            Message = marker,
            MessageAttributes = accepted.ToDictionary(kv => kv.Key, kv => new SnsMessageAttributeValue { DataType = "String", StringValue = kv.Value }),
        });

        var seen = new List<JsonObject>();
        var deadline = clock.Elapsed + TimeSpan.FromSeconds(90.0);
        while (clock.Elapsed < deadline && !seen.Any(d => (string?)d["Message"] == marker))
        {
            seen.AddRange(await DrainOnce(sqs, url));
        }

        var delivered = seen.Where(d => (string?)d["Message"] == marker).ToList();
        Ensure(delivered.Count > 0, $"the accepted publish never reached the queue; saw {seen.Count} other message(s)");
        var latency = clock.Elapsed - start;
        var attrs = delivered[0]["MessageAttributes"];
        var wantAttrs = new JsonObject();
        foreach (var (n, v) in accepted)
        {
            wantAttrs[n] = new JsonObject { ["Type"] = "String", ["Value"] = v };
        }

        Ensure(JsonNode.DeepEquals(attrs, wantAttrs), $"envelope MessageAttributes were {attrs?.ToJsonString() ?? "None"}, expected {wantAttrs.ToJsonString()}");

        // The control's arrival is the barrier; keep draining for max(10 s, 3x its latency).
        var windowEnd = clock.Elapsed + TimeSpan.FromSeconds(Math.Max(10.0, 3.0 * latency.TotalSeconds));
        while (clock.Elapsed < windowEnd)
        {
            seen.AddRange(await DrainOnce(sqs, url));
        }

        var leaked = seen.Select(d => d["Message"]?.ToString() ?? "")
            .Where(m => m.StartsWith("refused-attrchar-", StringComparison.Ordinal)).ToList();
        Ensure(leaked.Count == 0, $"refused publishes were delivered: [{string.Join(", ", leaked.Select(l => $"'{l}'"))}]");
    }

    private static async Task<List<JsonObject>> DrainOnce(AmazonSQSClient sqs, string url)
    {
        var @out = new List<JsonObject>();
        var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
        foreach (var m in resp.Messages ?? [])
        {
            @out.Add(JsonNode.Parse(m.Body)!.AsObject());
            await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = m.ReceiptHandle });
        }

        return @out;
    }

    /// <summary>(code, message) of the ClientError fn raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the publish to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>A cleanup running undo() where a failed delete raises; only not_found means gone.</summary>
    private static Func<Task> Teardown(Func<Task> undo, params string[] notFound) => async () =>
    {
        try
        {
            await undo();
        }
        catch (AmazonServiceException exc) when (notFound.Contains(exc.ErrorCode))
        {
        }
    };
}
