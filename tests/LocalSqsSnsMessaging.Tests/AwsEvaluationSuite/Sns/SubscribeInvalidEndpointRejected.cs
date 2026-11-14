// Translated from behavioral/sns/check_subscribe_invalid_endpoint_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-invalid-endpoint-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeInvalidEndpointRejected
{
    /// <summary>
    /// Subscribe rejects a malformed endpoint for the sqs protocol (must be a queue ARN, not a bare
    /// string), while an otherwise-identical well-formed ARN on the same topic/protocol is accepted and
    /// the rejected call leaves no subscription behind.
    /// </summary>
    [Test]
    [DisplayName("sns.subscribe-invalid-endpoint-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-subbad-"));

        var goodArn = FakeQueueArn(topicArn);
        var goodSubArn = (await ev.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = goodArn })).SubscriptionArn;
        if (!string.IsNullOrEmpty(goodSubArn) && goodSubArn != "pending confirmation")
        {
            ctx.OnCleanup(() => ev.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = goodSubArn }));
        }

        Ensure(!string.IsNullOrEmpty(goodSubArn),
            $"expected the well-formed control endpoint '{goodArn}' to be accepted, got '{goodSubArn}'");

        var (result, error) = await FidelityContext.Capture(() => ev.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = "not-an-arn" }));
        if (error is null)
        {
            throw Fail($"Subscribe with a non-ARN sqs endpoint SUCCEEDED: {result}");
        }

        var code = error.Code;
        var message = error.Message;
        Ensure(code == "InvalidParameter",
            $"expected InvalidParameter for non-ARN sqs endpoint, got {code}");
        Ensure(message == "Invalid parameter: SQS endpoint ARN",
            $"unexpected refusal message for a non-ARN sqs endpoint: '{message}'");

        var subscribedEndpoints = ((await ev.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [])
            .Select(s => s.Endpoint).ToHashSet();
        Ensure(subscribedEndpoints.SetEquals([goodArn]),
            $"expected only the control endpoint '{goodArn}' subscribed to the topic after the " +
            $"rejected call, got {{{string.Join(", ", subscribedEndpoints.Select(e => $"'{e}'"))}}}");
    }

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subs without verifying the queue exists).</summary>
    private static string FakeQueueArn(string topicArn)
    {
        var parts = topicArn.Split(':');
        var name = $"sim2real-behav-sns-subbad-q-{Guid.NewGuid().ToString("N")[..8]}";
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }
}
