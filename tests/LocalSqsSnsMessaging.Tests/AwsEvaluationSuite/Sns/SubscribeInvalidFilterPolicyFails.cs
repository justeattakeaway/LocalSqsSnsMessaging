// Translated from behavioral/sns/check_subscribe_invalid_filter_policy_fails.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Net;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-invalid-filter-policy-fails</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeInvalidFilterPolicyFails
{
    /// <summary>
    /// Subscribe with an unparseable FilterPolicy is refused with InvalidParameter (HTTP 400) and an
    /// exact message and creates nothing, while a well-formed FilterPolicy on the same topic and
    /// queue is accepted and auto-confirmed.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-invalid-filter-policy-fails")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsErrorMessages, "An unparseable FilterPolicy is refused with 'Invalid parameter: FilterPolicy: Filter policy is not valid JSON' instead of AWS's Jackson parse-error message")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(ctx, tags: [new() { Key = "sim2real", Value = "true" }]);
        var (_, queueArn) = await CreateQueue(ctx);

        var goodSubArn = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["FilterPolicy"] = new JsonObject { ["event"] = new JsonArray("created") }.ToJsonString() },
            ReturnSubscriptionArn = true,
        })).SubscriptionArn;
        Ensure(!string.IsNullOrEmpty(goodSubArn) && goodSubArn != "pending confirmation",
            $"expected the control subscription auto-confirmed, got '{goodSubArn}'");
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = goodSubArn }));

        var (code, message, status) = await Error(() => sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "sqs",
            Endpoint = queueArn,
            Attributes = new() { ["FilterPolicy"] = "this-is-not-json" },
            ReturnSubscriptionArn = true,
        }));
        Ensure(code == "InvalidParameter", $"expected InvalidParameter, got '{code}' ('{message}')");
        Ensure(status == HttpStatusCode.BadRequest, $"expected HTTP 400 for the refusal, got {(int)status}");
        const string expectedMessage = "Invalid parameter: Attributes Reason: FilterPolicy: failed to parse JSON. Unrecognized token 'this': was expecting (JSON String, Number, Array, Object or token 'null', 'true' or 'false')\n at [Source: REDACTED (`StreamReadFeature.INCLUDE_SOURCE_IN_LOCATION` disabled); line: 1, column: 1]";
        Ensure(message == expectedMessage, $"expected the message '{expectedMessage}', got '{message}'");

        var subs = (await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];
        var subArns = subs.Select(s => s.SubscriptionArn).ToList();
        Ensure(subArns.SequenceEqual([goodSubArn]),
            "the rejected Subscribe must not create a subscription; expected only " +
            $"['{goodSubArn}'], got [{string.Join(", ", subArns.Select(a => $"'{a}'"))}]");
    }

    /// <summary>
    /// Call fn(); return (Error.Code, Error.Message, HTTP status). Raises if the call unexpectedly
    /// succeeds.
    /// </summary>
    private static async Task<(string? Code, string Message, HttpStatusCode Status)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "", error.StatusCode);
    }

    /// <summary>Create a topic, register its teardown, and return its ARN.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, string? name = null, Dictionary<string, string>? attributes = null,
        List<Amazon.SimpleNotificationService.Model.Tag>? tags = null, bool fifo = false)
    {
        var sns = ctx.Sns;
        name ??= FidelityContext.UniqueName("sim2real-behav-sns-topic-");
        if (fifo && !name.EndsWith(".fifo", StringComparison.Ordinal))
        {
            name = $"{name}.fifo";
        }

        var request = new CreateTopicRequest { Name = name };
        var attrs = attributes is not null ? new Dictionary<string, string>(attributes) : [];
        if (fifo)
        {
            attrs.TryAdd("FifoTopic", "true");
        }

        if (attrs.Count > 0)
        {
            request.Attributes = attrs;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
        }

        var arn = (await sns.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>
    /// Create an SQS queue (SQS subscriptions auto-confirm, unlike email/http);
    /// return (queue_url, queue_arn).
    /// </summary>
    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-");
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            name = $"{name}.fifo";
            attrs["FifoQueue"] = "true";
        }

        var request = new CreateQueueRequest { QueueName = name, Tags = new() { ["sim2real"] = "true" } };
        if (attrs.Count > 0)
        {
            request.Attributes = attrs;
        }

        var url = (await sqs.CreateQueueAsync(request)).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        return (url, arn);
    }
}
