// Translated from behavioral/sns/check_queue_policy_source_account_condition_decides_delivery.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.queue-policy-source-account-condition-decides-delivery</summary>
[Category("AwsEvaluationSuite")]
public sealed class QueuePolicySourceAccountConditionDecidesDelivery
{
    private const string Check = "queue-policy-source-account-condition-decides-delivery";
    private const int Rounds = 3;

    /// <summary>
    /// SNS presents the topic owner's account as <c>aws:SourceAccount</c> when delivering to SQS: of two
    /// subscribed queues, the one whose policy requires the caller's account receives every message and the
    /// one naming another account receives none until its condition is corrected, after which it receives.
    /// </summary>
    [Test]
    [DisplayName("sns.queue-policy-source-account-condition-decides-delivery")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var sqs = ctx.Sqs;
        const string otherAccount = "111122223333"; // documentation placeholder: an account that is not this one

        var topic = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = FidelityContext.UniqueName("sim2real-behav-sns-srcacct-"),
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = topic }));
        var (goodUrl, goodArn) = await CreateQueue(ctx, "sim2real-behav-sns-acct-ok-");
        var (badUrl, badArn) = await CreateQueue(ctx, "sim2real-behav-sns-acct-no-");
        await SetQueuePolicy(sqs, goodUrl, goodArn, ctx.Account);
        await SetQueuePolicy(sqs, badUrl, badArn, otherAccount);

        foreach (var queue in new[] { goodArn, badArn })
        {
            var sub = (await sns.SubscribeAsync(new SubscribeRequest
            {
                TopicArn = topic, Protocol = "sqs", Endpoint = queue, ReturnSubscriptionArn = true,
            })).SubscriptionArn;
            ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));
        }

        var leaked = new List<string>();
        for (var i = 0; i < Rounds; i++)
        {
            var body = FidelityContext.UniqueName($"sim2real-r{i}-");
            Ensure(!string.IsNullOrEmpty((await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = body })).MessageId),
                "Publish must return a MessageId");
            var got = await DrainUntil(sqs, goodUrl, [body], 60);
            Ensure(got.Contains(body),
                $"the queue admitting aws:SourceAccount = this account must receive round {i}; got {List(got)}");
            leaked.AddRange(await DrainUntil(sqs, badUrl, [], 0));
        }

        leaked.AddRange(await DrainUntil(sqs, badUrl, [], 0));
        Ensure(leaked.Count == 0,
            "the queue whose policy admits SNS only for a different aws:SourceAccount received " +
            $"{leaked.Count} message(s) out of {Rounds} published");

        await SetQueuePolicy(sqs, badUrl, badArn, ctx.Account);
        var clock = FidelityTime.StartNew();
        var received = new List<string>();
        var repaired = false;
        while (clock.Elapsed < TimeSpan.FromSeconds(120))
        {
            var body = FidelityContext.UniqueName("sim2real-repair-");
            await sns.PublishAsync(new PublishRequest { TopicArn = topic, Message = body });
            received = await DrainUntil(sqs, badUrl, [body], 20);
            if (received.Contains(body))
            {
                repaired = true;
                break;
            }
        }

        if (!repaired)
        {
            throw Fail("after its aws:SourceAccount condition named this account, the queue still received " +
                $"nothing published to the topic; last drain {List(received)}");
        }
    }

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(s => $"'{s}'")) + "]";

    private static async Task<(string Url, string Arn)> CreateQueue(FidelityContext ctx, string prefix)
    {
        var sqs = ctx.Sqs;
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = FidelityContext.UniqueName(prefix), Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        return (url, arn);
    }

    private static async Task SetQueuePolicy(AmazonSQSClient sqs, string url, string arn, string account)
    {
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow",
                ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" },
                ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn,
                ["Condition"] = new JsonObject { ["StringEquals"] = new JsonObject { ["aws:SourceAccount"] = account } },
            }),
        }.ToJsonString();
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy } });
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var current = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["Policy"] }))
                .Attributes?.GetValueOrDefault("Policy") ?? "{}";
            if (JsonNode.DeepEquals(JsonNode.Parse(current), JsonNode.Parse(policy)))
            {
                break;
            }

            Ensure(deadline.Elapsed < TimeSpan.FromSeconds(60), "the queue policy never read back as set");
            await FidelityTime.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>
    /// Receive and delete until every body in <c>wanted</c> arrived or the deadline passed (at
    /// least one receive always runs); returns every notification Message drained.
    /// </summary>
    private static async Task<List<string>> DrainUntil(AmazonSQSClient sqs, string url, HashSet<string> wanted, double timeoutS)
    {
        var output = new List<string>();
        var deadline = FidelityTime.StartNew();
        while (true)
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = url, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                output.Add((string)JsonNode.Parse(msg.Body)!["Message"]!);
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = url, ReceiptHandle = msg.ReceiptHandle });
            }

            if ((wanted.Count > 0 && wanted.IsSubsetOf(output)) || deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                return output;
            }
        }
    }
}
