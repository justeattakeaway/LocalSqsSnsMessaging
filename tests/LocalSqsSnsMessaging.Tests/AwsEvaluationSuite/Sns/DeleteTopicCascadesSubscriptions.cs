// Translated from behavioral/sns/check_delete_topic_cascades_subscriptions.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.delete-topic-cascades-subscriptions</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteTopicCascadesSubscriptions
{
    /// <summary>
    /// DeleteTopic makes the topic NotFound for GetTopicAttributes, ListSubscriptionsByTopic and
    /// Subscribe and drops it from ListTopics; its subscription is either still readable or NotFound.
    /// </summary>
    [Test]
    [DisplayName("sns.delete-topic-cascades-subscriptions")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe returns a bare GUID SubscriptionArn instead of '<TopicArn>:<id>'")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-topic-cascade-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        // DeleteTopic below is the subject of the check; registering teardown anyway
        // keeps a mid-check failure from leaking the topic (DeleteTopic is idempotent).
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var (_, queueArn) = await CreateSqsQueueForSns(ctx, "cascade-q-");
        var subArn = (await sns.SubscribeAsync(new SubscribeRequest { TopicArn = arn, Protocol = "sqs", Endpoint = queueArn })).SubscriptionArn;
        Ensure(subArn.StartsWith(arn, StringComparison.Ordinal), subArn);

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });

        // AWS cascades asynchronously: the orphaned subscription stays readable for 120s or more.
        string? code;
        string? message;
        var readError = await FidelityContext.CaptureError(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }));
        if (readError is not null)
        {
            // DeleteTopic "Deletes a topic and all its subscriptions" (Smithy), so a synchronous
            // cascade is equally valid: then the subscription is gone with the exact NotFound.
            (code, message) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = subArn }));
            Ensure((code, message) == ("NotFound", "Subscription does not exist"),
                "orphaned subscription after DeleteTopic: expected it readable or NotFound " +
                $"'Subscription does not exist', got '{code}' '{message}'");
        }

        (code, message) = await Error(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn }));
        Ensure(code == "NotFound", $"GetTopicAttributes after delete: expected NotFound, got {code}");
        Ensure(message == "Topic does not exist", $"GetTopicAttributes after delete: unexpected message '{message}'");

        (code, message) = await Error(() => sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = arn }));
        Ensure(code == "NotFound", $"ListSubscriptionsByTopic after delete: expected NotFound, got {code}");
        Ensure(message == "Topic does not exist", $"ListSubscriptionsByTopic after delete: unexpected message '{message}'");

        (code, message) = await Error(() => sns.SubscribeAsync(new SubscribeRequest { TopicArn = arn, Protocol = "sqs", Endpoint = queueArn }));
        Ensure(code == "NotFound", $"Subscribe to a deleted topic: expected NotFound, got {code}");
        Ensure(message == "Topic does not exist", $"Subscribe to a deleted topic: unexpected message '{message}'");

        async Task<HashSet<string>> AllTopics()
        {
            var found = new HashSet<string>();
            await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
            {
                foreach (var t in page.Topics ?? [])
                {
                    found.Add(t.TopicArn);
                }
            }

            return found;
        }

        var topics = await PollUntil(AllTopics, s => !s.Contains(arn), timeoutS: 60, intervalS: 3);
        Ensure(!topics.Contains(arn), "deleted topic must disappear from ListTopics");
    }

    /// <summary>(Code, Message) of the ClientError fn() raises; an unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be rejected, but it SUCCEEDED and returned {result}");
        }

        return (error.Code, error.Message);
    }

    /// <summary>Poll probe() until predicate(result) is truthy; return the final result.</summary>
    private static async Task<T> PollUntil<T>(Func<Task<T>> probe, Func<T, bool> predicate,
        double timeoutS = 60, double intervalS = 1.0, string description = "condition")
    {
        var deadline = FidelityTime.StartNew();
        T? last = default;
        while (deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            last = await probe();
            if (predicate(last))
            {
                return last;
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
        }

        throw Fail($"{description} not met within {timeoutS}s. Last result: {last}");
    }

    /// <summary>
    /// Create an SQS queue with a policy allowing SNS to deliver to it.
    /// Returns (queue_url, queue_arn).
    /// </summary>
    private static async Task<(string Url, string Arn)> CreateSqsQueueForSns(FidelityContext ctx, string namePrefix = "sqs-target-", bool fifo = false)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-" + namePrefix);
        var attrs = new Dictionary<string, string>();
        if (fifo)
        {
            if (!name.EndsWith(".fifo", StringComparison.Ordinal))
            {
                name += ".fifo";
            }

            attrs["FifoQueue"] = "true";
        }

        var queueUrl = (await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = name, Attributes = attrs })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(queueUrl));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queueUrl, AttributeNames = ["QueueArn"] })).Attributes["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Sid"] = "AllowSNS",
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queueUrl, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (queueUrl, arn);
    }
}
