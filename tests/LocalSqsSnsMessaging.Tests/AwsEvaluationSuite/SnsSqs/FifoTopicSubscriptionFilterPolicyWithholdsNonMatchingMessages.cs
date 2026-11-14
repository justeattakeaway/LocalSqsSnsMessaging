// Translated from behavioral/sns__sqs/check_fifo_topic_subscription_filter_policy_withholds_non_matching_messages.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;
using SnsMessageAttributeValue = Amazon.SimpleNotificationService.Model.MessageAttributeValue;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.SnsSqs;

/// <summary>sns__sqs.fifo-topic-subscription-filter-policy-withholds-non-matching-messages</summary>
[Category("AwsEvaluationSuite")]
public sealed class FifoTopicSubscriptionFilterPolicyWithholdsNonMatchingMessages
{
    private const string Check = "fifo-topic-subscription-filter-policy-withholds-non-matching-messages";

    /// <summary>
    /// On a FIFO topic, a subscription's FilterPolicy admits only matching messages to its FIFO
    /// queue, so the later matching message follows the earlier one directly in group order, while an
    /// unfiltered sibling subscription receives every message.
    /// </summary>
    [Test]
    [DisplayName("sns__sqs.fifo-topic-subscription-filter-policy-withholds-non-matching-messages")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateFifoTopic(ctx);
        var (filteredUrl, filteredArn) = await CreateFifoQueueForTopic(ctx, topicArn);
        var (plainUrl, plainArn) = await CreateFifoQueueForTopic(ctx, topicArn);

        var subFiltered = await SubscribeFifo(ctx, topicArn, filteredArn,
            attributes: new() { ["FilterPolicy"] = new JsonObject { ["color"] = new JsonArray("red") }.ToJsonString() });
        var subPlain = await SubscribeFifo(ctx, topicArn, plainArn);
        Ensure(new HashSet<string> { subFiltered, subPlain }.SetEquals(await Subs(sns, topicArn)),
            $"both subscriptions must be listed; got {Repr(await Subs(sns, topicArn))}");

        var group = FidelityContext.UniqueName("sim2real-behav-sns-fifogrp-");
        var redBody = FidelityContext.UniqueName("sim2real-red-");
        var blueBody = FidelityContext.UniqueName("sim2real-blue-");
        var red2Body = FidelityContext.UniqueName("sim2real-red-");
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = redBody,
            MessageGroupId = group,
            MessageDeduplicationId = $"{group}-red",
            MessageAttributes = new() { ["color"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "red" } },
        });

        var plainAfterRed = await DrainUntil(ctx, plainUrl, 1, timeoutS: 60.0);
        var filteredAfterRed = await DrainUntil(ctx, filteredUrl, 1, timeoutS: 60.0);
        Ensure(plainAfterRed.SequenceEqual([redBody]), $"the unfiltered queue's first message must be the red one; got {Repr(plainAfterRed)}");
        Ensure(filteredAfterRed.SequenceEqual([redBody]), $"the filtered queue's first message must be the red one; got {Repr(filteredAfterRed)}");

        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = blueBody,
            MessageGroupId = group,
            MessageDeduplicationId = $"{group}-blue",
            MessageAttributes = new() { ["color"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "blue" } },
        });

        var plainAfterBlue = await DrainUntil(ctx, plainUrl, 2, timeoutS: 60.0, prior: plainAfterRed);
        Ensure(plainAfterBlue.SequenceEqual([redBody, blueBody]),
            $"the unfiltered queue must receive both messages in group order; got {Repr(plainAfterBlue)}");

        // A later matching message in the same group: FIFO order would put a leaked blue before it.
        await sns.PublishAsync(new PublishRequest
        {
            TopicArn = topicArn,
            Message = red2Body,
            MessageGroupId = group,
            MessageDeduplicationId = $"{group}-red2",
            MessageAttributes = new() { ["color"] = new SnsMessageAttributeValue { DataType = "String", StringValue = "red" } },
        });

        var plainAfterRed2 = await DrainUntil(ctx, plainUrl, 3, timeoutS: 60.0, prior: plainAfterBlue);
        Ensure(plainAfterRed2.SequenceEqual([redBody, blueBody, red2Body]),
            $"the unfiltered queue must receive all three messages in group order; got {Repr(plainAfterRed2)}");
        var filteredAfterRed2 = await DrainUntil(ctx, filteredUrl, 2, timeoutS: 60.0, prior: filteredAfterRed);
        Ensure(filteredAfterRed2.SequenceEqual([redBody, red2Body]),
            "the filtered queue's next message must be the second red one, with the non-matching blue " +
            $"withheld; got {Repr(filteredAfterRed2)}");
    }

    private static string Repr(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(v => $"'{v}'"))}]";

    /// <summary>Unwrap the SNS Notification envelope and return its Message, or the body if it is not one.</summary>
    private static string InnerMessage(string body)
    {
        JsonNode? envelope;
        try
        {
            envelope = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        if (envelope is JsonObject o && o["Type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "Notification"
            && o["Message"] is JsonValue m && m.TryGetValue<string>(out var inner))
        {
            return inner;
        }

        return body;
    }

    private static async Task<string> CreateFifoTopic(FidelityContext ctx)
    {
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fifo-") + ".fifo";
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new() { ["FifoTopic"] = "true", ["ContentBasedDeduplication"] = "false" },
            Tags = [new() { Key = "sim2real", Value = Check }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>Create a real FIFO SQS queue whose policy lets exactly this topic publish to it.</summary>
    private static async Task<(string Url, string Arn)> CreateFifoQueueForTopic(FidelityContext ctx, string topicArn)
    {
        var sqs = ctx.Sqs;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-q-") + ".fifo";
        var url = (await sqs.CreateQueueAsync(new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new() { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "false" },
            Tags = new() { ["sim2real"] = Check },
        })).QueueUrl;
        ctx.OnCleanup(() => sqs.DeleteQueueAsync(url));
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = ["QueueArn"] })).Attributes!["QueueArn"];
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(new JsonObject
            {
                ["Effect"] = "Allow", ["Principal"] = new JsonObject { ["Service"] = "sns.amazonaws.com" }, ["Action"] = "sqs:SendMessage",
                ["Resource"] = arn, ["Condition"] = new JsonObject { ["ArnEquals"] = new JsonObject { ["aws:SourceArn"] = topicArn } },
            }),
        };
        await sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = url, Attributes = new() { ["Policy"] = policy.ToJsonString() } });
        return (url, arn);
    }

    private static async Task<List<string>> Subs(AmazonSimpleNotificationServiceClient sns, string topicArn)
    {
        var arns = new List<string>();
        await foreach (var page in sns.Paginators.ListSubscriptionsByTopic(new ListSubscriptionsByTopicRequest { TopicArn = topicArn }).Responses)
        {
            arns.AddRange((page.Subscriptions ?? []).Select(s => s.SubscriptionArn));
        }

        return arns;
    }

    /// <summary>Subscribe a FIFO queue and poll GetSubscriptionAttributes until it is confirmed.</summary>
    private static async Task<string> SubscribeFifo(FidelityContext ctx, string topicArn, string queueArn,
        Dictionary<string, string>? attributes = null, double timeoutS = 60.0)
    {
        var sns = ctx.Sns;
        var request = new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn, ReturnSubscriptionArn = true };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        var subArn = (await sns.SubscribeAsync(request)).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = subArn }));
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

    /// <summary>Long-poll until want_count bodies have arrived in total; returns them in arrival order.</summary>
    private static async Task<List<string>> DrainUntil(FidelityContext ctx, string queueUrl, int wantCount, double timeoutS, List<string>? prior = null)
    {
        var sqs = ctx.Sqs;
        var collected = new List<string>(prior ?? []);
        var deadline = FidelityTime.StartNew();
        while (collected.Count < wantCount && deadline.Elapsed < TimeSpan.FromSeconds(timeoutS))
        {
            var resp = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 10, WaitTimeSeconds = 2 });
            foreach (var msg in resp.Messages ?? [])
            {
                await sqs.DeleteMessageAsync(new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = msg.ReceiptHandle });
                collected.Add(InnerMessage(msg.Body));
            }
        }

        Ensure(collected.Count >= wantCount,
            $"only {collected.Count} of {wantCount} expected message(s) arrived within {timeoutS}s: {Repr(collected)}");
        return collected;
    }
}
