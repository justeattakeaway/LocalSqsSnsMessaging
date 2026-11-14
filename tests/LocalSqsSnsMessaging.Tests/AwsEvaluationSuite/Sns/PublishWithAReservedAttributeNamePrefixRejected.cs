// Translated from behavioral/sns/check_publish_with_a_reserved_attribute_name_prefix_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsMessage = Amazon.SQS.Model.Message;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.publish-with-a-reserved-attribute-name-prefix-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class PublishWithAReservedAttributeNamePrefixRejected
{
    private const string Scenario = "sns-reserved-attr-name";

    /// <summary>
    /// A message attribute named with the reserved "AWS." prefix is refused at Publish with an exact
    /// error and nothing is delivered, while the same publish with an unreserved name is delivered
    /// carrying that attribute.
    /// </summary>
    [Test]
    [DisplayName("sns.publish-with-a-reserved-attribute-name-prefix-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        var topicArn = await CreateTopic(ctx);
        var (url, arn) = await CreateQueueForTopic(ctx, topicArn);
        await SubscribeAndConfirm(ctx, topicArn, arn, attributes: new() { ["RawMessageDelivery"] = "true" });

        var refused = FidelityContext.UniqueName("sim2real-behav-reserved-refused-");
        var (code, message) = await Error(() => sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = refused,
            MessageAttributes = new() { ["AWS.tenant"] = S("blue") },
        }));
        Ensure(code == "ParameterValueInvalid",
            $"a reserved attribute name gave '{code}', expected 'ParameterValueInvalid'; " +
            $"message '{message}'");
        Ensure(message == "Message attribute names starting with 'AWS.' or 'Amazon.' are reserved for use by Amazon.", $"unexpected refusal message '{message}'");

        var accepted = FidelityContext.UniqueName("sim2real-behav-reserved-accepted-");
        var clock = FidelityTime.StartNew();
        var started = clock.Elapsed;
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = accepted,
            MessageAttributes = new() { ["app.tenant"] = S("blue") },
        });
        SqsMessage? got = null;
        var deadline = clock.Elapsed + TimeSpan.FromSeconds(120.0);
        var bodies = new List<string>();
        while (got is null)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                bodies.Add(msg.Body);
                if (msg.Body == accepted)
                {
                    got = msg;
                }
            }

            if (got is null && clock.Elapsed >= deadline)
            {
                throw Fail($"the unreserved publish never arrived; received {Repr(bodies)}");
            }
        }

        var attrs = (got.MessageAttributes ?? []).ToDictionary(kv => kv.Key, kv => kv.Value.StringValue);
        Ensure(attrs.Count == 1 && attrs.GetValueOrDefault("app.tenant") == "blue",
            $"the delivered attributes are {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}");
        var latency = Math.Max((clock.Elapsed - started).TotalSeconds, 1.0);
        var late = (await Drain(ctx, new() { ["queue"] = url }, windowS: Math.Max(3.0 * latency, 20.0)))["queue"];
        Ensure(!bodies.Concat(late).Contains(refused), "the refused publish must not be delivered, but it arrived");
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    /// <summary>A String-typed message attribute value.</summary>
    private static SnsMessageAttributeValue S(string value) => new() { DataType = "String", StringValue = value };

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic (tagged + prefix-named) and register DeleteTopic immediately.</summary>
    private static async Task<string> CreateTopic(FidelityContext ctx, bool fifo = false, Dictionary<string, string>? attributes = null)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-t-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
            attrs["FifoTopic"] = "true";
        }

        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => ctx.Sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real SQS queue whose policy lets exactly this topic send to it.</summary>
    private static async Task<(string Url, string Arn)> CreateQueueForTopic(FidelityContext ctx, string topicArn, bool fifo = false,
        Dictionary<string, string>? attributes = null)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + (fifo ? ".fifo" : "");
        var attrs = new Dictionary<string, string>(attributes ?? []);
        if (fifo)
        {
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
        await PollUntil(
            async () => (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }))
                .Attributes?.GetValueOrDefault("PendingConfirmation"),
            pending => pending == "false",
            timeoutS: timeoutS, intervalS: 2.0,
            description: "the new subscription leaves PendingConfirmation");
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

    /// <summary>
    /// Alternate long polls across the queues for one shared window and return every body
    /// each produced. The empty lists feed an equality-to-empty assertion.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> Drain(FidelityContext ctx, Dictionary<string, string> queues, double windowS)
    {
        var sqs = ctx.Sqs;
        var @out = queues.Keys.ToDictionary(label => label, _ => new List<string>());
        var deadline = FidelityTime.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(windowS))
        {
            foreach (var (label, url) in queues)
            {
                var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
                foreach (var msg in resp.Messages ?? [])
                {
                    await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
                    @out[label].Add(msg.Body);
                }
            }
        }

        return @out;
    }

    /// <summary>
    /// (Error.Code, Error.Message) of the ClientError fn raises. An unexpected success
    /// raises too, naming what came back, so it can never read as a code mismatch.
    /// </summary>
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
}
