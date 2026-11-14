// Translated from behavioral/sns/check_publish_non_bmp_message_attribute_value_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-non-bmp-message-attribute-value-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishNonBmpMessageAttributeValueRejected
{
    private const string Scenario = "sns-non-bmp-attribute";

    // U+1F680 ROCKET: an astral-plane (non-BMP) codepoint, i.e. a surrogate pair in UTF-16.
    private const string Astral = "\U0001F680";

    // The same idea inside the BMP: multi-byte in UTF-8, single UTF-16 code unit.
    private const string Bmp = "日本語";

    /// <summary>
    /// Publish with an astral-plane codepoint in a String message-attribute value is refused
    /// InvalidParameter and not delivered; the same codepoint in the body is delivered verbatim.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-non-bmp-message-attribute-value-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsValidation, "Publish accepts a String attribute value with characters outside the Basic Multilingual Plane")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx);
        var (queueUrl, queueArn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, queueArn);

        var rejectedMarker = FidelityContext.UniqueName("sim2real-behav-sns-astral-rejected-");
        var controlMarker = FidelityContext.UniqueName("sim2real-behav-sns-astral-control-");
        var controlBody = $"{controlMarker} {Astral}";

        // The documented attribute charset (XML Char) includes #x10000-#x10FFFF, yet Publish refuses it.
        var code = await ErrorCode(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = rejectedMarker,
            MessageAttributes = new() { ["tag"] = new SnsMessageAttributeValue { DataType = "String", StringValue = $"ok-{Astral}" } },
        }));
        Ensure(code == "InvalidParameter",
            "an astral-plane codepoint in a String message-attribute value was not rejected with " +
            $"InvalidParameter: '{code}'");

        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = controlBody,
            MessageAttributes = new() { ["tag"] = new SnsMessageAttributeValue { DataType = "String", StringValue = $"ok-{Bmp}" } },
        });
        var bodies = await DrainUntilContaining(ctx, queueUrl, controlMarker, timeoutS: 90.0);
        bodies.AddRange(await ReceiveAny(ctx, queueUrl, rounds: 1, waitS: 20));

        var matched = bodies.Where(b => b.Contains(controlMarker, StringComparison.Ordinal)).ToList();
        Ensure(matched.Count == 1,
            $"expected exactly one control delivery, got {matched.Count}: {HeadsRepr(bodies)}");
        var envelope = JsonNode.Parse(matched[0])!;
        Ensure((string?)envelope["Message"] == controlBody,
            "the same astral codepoint is legal in the body and must round-trip verbatim: " +
            $"'{envelope["Message"]}' != '{controlBody}'");
        Ensure((string?)envelope["MessageAttributes"]!["tag"]!["Value"] == $"ok-{Bmp}",
            "the BMP attribute value was mangled, so the control does not isolate the astral case: " +
            $"{envelope["MessageAttributes"]!["tag"]!.ToJsonString()}");
        Ensure(!bodies.Any(b => b.Contains(rejectedMarker, StringComparison.Ordinal)),
            $"the refused publish was delivered anyway: {HeadsRepr(bodies)}");
    }

    private static string HeadsRepr(List<string> bodies) =>
        $"[{string.Join(", ", bodies.Select(b => $"'{(b.Length <= 160 ? b : b[..160])}'"))}]";

    /// <summary>The AWS error Code raised by fn(); raise if fn succeeds.</summary>
    private static async Task<string?> ErrorCode<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"the call was expected to be rejected but SUCCEEDED, returning {result}");
        }

        return error.Code;
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately; returns the ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            name += ".fifo";
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic publish to it.</summary>
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
            QueueName = name,
            Attributes = attrs,
            Tags = new() { ["sim2real"] = Scenario, ["sim2real:managed"] = "true" },
        })).QueueUrl;
        ctx.OnCleanup(() => ctx.Sqs.DeleteQueueAsync(url));
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
        var deadline = FidelityTime.StartNew();
        string? pending = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            pending = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation");
            if (pending == "false")
            {
                return subArn;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(1.0));
        }

        throw Fail($"subscription '{subArn}' was still PendingConfirmation='{pending}' after {timeoutS}s");
    }

    /// <summary>
    /// Long-poll until a body containing needle arrives; returns every body seen up to and
    /// including it, so the caller can assert on what else came through.
    /// </summary>
    private static async Task<List<string>> DrainUntilContaining(FidelityContext ctx, string queueUrl, string needle, double timeoutS = 90.0)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 5 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body ?? "");
            }

            if (bodies.Any(b => b.Contains(needle, StringComparison.Ordinal)))
            {
                return bodies;
            }
        }

        throw Fail($"the control publish '{needle}' never arrived within {timeoutS}s, so the absence of the " +
            $"refused publish is not bounded; bodies seen: {HeadsRepr(bodies)}");
    }

    /// <summary>Long-poll a queue `rounds` times and return every body it produced (expected: none).</summary>
    private static async Task<List<string>> ReceiveAny(FidelityContext ctx, string queueUrl, int rounds, int waitS)
    {
        var sqs = ctx.Sqs;
        var bodies = new List<string>();
        for (var i = 0; i < rounds; i++)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = waitS });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body ?? "");
            }
        }

        return bodies;
    }
}
