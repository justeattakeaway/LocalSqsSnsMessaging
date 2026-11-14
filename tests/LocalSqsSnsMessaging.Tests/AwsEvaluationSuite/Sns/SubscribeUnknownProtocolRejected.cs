// Translated from behavioral/sns/check_subscribe_unknown_protocol_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-unknown-protocol-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeUnknownProtocolRejected
{
    /// <summary>
    /// Subscribe must reject a protocol string that is not a supported SNS protocol, while
    /// leaving the topic otherwise able to accept a real subscription.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-unknown-protocol-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to a well-formed SQS ARN of a queue that does not exist is refused with NotFound 'Queue not found.' instead of creating the subscription")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-badproto-"));

        // Positive control: a supported protocol on the same topic must be accepted, so a
        // rejection below cannot be explained by "this topic rejects every subscribe".
        var goodEndpoint = FakeQueueArn(topicArn);
        var goodSubArn = (await ev.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = goodEndpoint })).SubscriptionArn;
        Ensure(!string.IsNullOrEmpty(goodSubArn), "positive control: sqs subscribe on a healthy topic must succeed");
        ctx.OnCleanup(() => ev.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = goodSubArn }));

        var (result, error) = await FidelityContext.Capture(() => ev.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn,
            Protocol = "carrier-pigeon",
            Endpoint = "somewhere",
        }));
        if (error is null)
        {
            throw Fail($"Subscribe with an unsupported protocol SUCCEEDED: {{'SubscriptionArn': '{result!.SubscriptionArn}'}}");
        }

        var (code, message) = (error.Code, error.Message);
        Ensure(code == "InvalidParameter", $"expected InvalidParameter for unsupported protocol, got {code}");
        Ensure(message == "Invalid parameter: Amazon SNS does not support this protocol string: carrier-pigeon",
            $"unexpected refusal message for an unsupported protocol: '{message}'");

        // Independent read at the destination: the rejected subscribe must have left no trace —
        // only the positive control's subscription is listed for this topic.
        var subs = (await ev.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];
        var endpoints = subs.Select(s => s.Endpoint).ToList();
        Ensure(endpoints.SequenceEqual([goodEndpoint]),
            $"rejected subscribe must not appear in ListSubscriptionsByTopic, got endpoints [{string.Join(", ", endpoints.Select(e => $"'{e}'"))}]");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subs without verifying the queue exists).</summary>
    private static string FakeQueueArn(string topicArn, string? name = null)
    {
        var parts = topicArn.Split(':');
        name ??= $"sim2real-behav-sns-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }
}
