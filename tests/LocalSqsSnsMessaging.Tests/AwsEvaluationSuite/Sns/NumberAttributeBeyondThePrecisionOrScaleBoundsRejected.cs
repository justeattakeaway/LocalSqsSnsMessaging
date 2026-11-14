// Translated from behavioral/sns/check_number_attribute_beyond_the_precision_or_scale_bounds_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.number-attribute-beyond-the-precision-or-scale-bounds-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class NumberAttributeBeyondThePrecisionOrScaleBoundsRejected
{
    private const string Scenario = "sns-number-attribute-beyond-the-precision-or";

    /// <summary>
    /// Publish refuses a Number attribute with 39 digits or magnitude outside 10^-128..10^126
    /// with exact messages and delivers nothing; 38-digit and in-scale values arrive verbatim.
    /// </summary>
    [Test]
    [DisplayName("sns.number-attribute-beyond-the-precision-or-scale-bounds-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish accepts a Number attribute beyond SNS's precision or scale bounds")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, arn);

        var refused = new (string Value, string Want)[]
        {
            ("123456789012345678901234567890123456789",
                "Message attribute 'n' number value [123456789012345678901234567890123456789] " +
                "must have less than 38 precision digits."),
            ("1e127", "Message attribute 'n' number value [1e127] scale must be 10^-128 and 10^126."),
            ("1E+126", "Message attribute 'n' number value [1E+126] scale must be 10^-128 and 10^126."),
            ("1e-129", "Message attribute 'n' number value [1e-129] scale must be 10^-128 and 10^126."),
        };
        foreach (var (value, want) in refused)
        {
            var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn, Message = $"refused {value}",
                MessageAttributes = new() { ["n"] = new SnsMessageAttributeValue { DataType = "Number", StringValue = value } },
            }));
            Ensure((code, message) == ("ParameterValueInvalid", want),
                $"Number value '{value}': expected ParameterValueInvalid '{want}', got " +
                $"'{code}' '{message}'");
        }

        // A float-based parser would round the 38-digit value; it must arrive verbatim.
        string[] accepted = ["12345678901234567890123456789012345678", "9e125", "1e-128"];
        var markers = new Dictionary<string, string>();
        foreach (var value in accepted)
        {
            var marker = FidelityContext.UniqueName("sim2real-behav-sns-numbound-");
            markers[marker] = value;
            await sns.PublishAsync(new PublishRequest
            {
                TopicArn = topicArn, Message = marker,
                MessageAttributes = new() { ["n"] = new SnsMessageAttributeValue { DataType = "Number", StringValue = value } },
            });
        }

        var got = await ReceiveKeyed(ctx, url, EnvelopeMessage, [.. markers.Keys], timeoutS: 90.0,
            description: "every in-bounds Number publish is delivered");
        foreach (var (marker, value) in markers)
        {
            var attr = JsonNode.Parse(got[marker].Body)!["MessageAttributes"]!["n"];
            Ensure(JsonNode.DeepEquals(attr, new JsonObject { ["Type"] = "Number", ["Value"] = value }),
                $"the in-bounds Number '{value}' must be delivered verbatim, got {attr?.ToJsonString()}");
        }

        var leaked = (await Drain(ctx, url, windowS: 10.0)).Where(b => b.Contains("refused", StringComparison.Ordinal)).ToList();
        Ensure(leaked.Count == 0, $"a refused publish must deliver nothing, got {List(leaked)}");
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static string Head(string s, int n) => s.Length <= n ? s : s[..n];

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoQueue"] = "true";
        }

        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name, Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => DeleteQueueIfPresent(ctx, url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    /// <summary>DeleteQueue; a queue the check itself already deleted is the one tolerated outcome.</summary>
    private static async Task DeleteQueueIfPresent(FidelityContext ctx, string url)
    {
        var error = await FidelityContext.CaptureError(() => ctx.Sqs.DeleteQueueAsync(url));
        if (error is not null && error.Code != "AWS.SimpleQueueService.NonExistentQueue")
        {
            throw error.Exception;
        }
    }

    /// <summary>Subscribe an SQS queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeAndConfirm(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => ctx.Sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 1.0,
            description: "the sqs subscription leaves PendingConfirmation");
        return subArn;
    }

    /// <summary>Bounded retry; RAISES on the deadline so a timeout can never read as a pass.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string description)
    {
        var deadline = FidelityTime.StartNew();
        var value = await fn();
        while (!pred(value))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: not satisfied within {timeoutS}s, last value '{value}'");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            value = await fn();
        }

        return value;
    }

    /// <summary>(Error.Code, Error.Message) the call raises. An unexpected success RAISES.</summary>
    private static async Task<(string Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code ?? "", error.Message ?? "");
    }

    /// <summary>
    /// Long-poll until a message keyed <c>key(msg)</c> exists for every wanted key; RAISES on the
    /// deadline. Each message is returned whole (Body plus MessageAttributes).
    /// </summary>
    private static async Task<Dictionary<string, Message>> ReceiveKeyed(FidelityContext ctx, string queueUrl, Func<Message, string?> key,
        HashSet<string> wanted, double timeoutS, string description)
    {
        var sqs = ctx.Sqs;
        var got = new Dictionary<string, Message>();
        var other = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (!wanted.IsSubsetOf(got.Keys))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"], MessageSystemAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                var k = key(msg);
                if (k is not null && wanted.Contains(k) && !got.ContainsKey(k))
                {
                    got[k] = msg;
                }
                else
                {
                    other.Add(Head(msg.Body, 160));
                }
            }

            if (!wanted.IsSubsetOf(got.Keys) && deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{description}: {List(wanted.Except(got.Keys).Order(StringComparer.Ordinal))} never arrived within " +
                    $"{timeoutS}s; other bodies seen: {List(other)}");
            }
        }

        return got;
    }

    /// <summary>The Message field of an SNS notification envelope, or None for a non-envelope body.</summary>
    private static string? EnvelopeMessage(Message msg)
    {
        JsonNode? body;
        try
        {
            body = JsonNode.Parse(msg.Body);
        }
        catch (JsonException)
        {
            return null;
        }

        if (body is JsonObject obj && obj["Type"] is JsonValue type && type.TryGetValue<string>(out var t) && t == "Notification")
        {
            return (string?)obj["Message"];
        }

        return null;
    }

    /// <summary>
    /// Long-poll for the whole window and return EVERY body seen, so the caller can assert on
    /// leakage rather than discard it.
    /// </summary>
    private static async Task<List<string>> Drain(FidelityContext ctx, string queueUrl, double windowS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(Head(msg.Body, 300));
            }
        }

        return bodies;
    }
}
