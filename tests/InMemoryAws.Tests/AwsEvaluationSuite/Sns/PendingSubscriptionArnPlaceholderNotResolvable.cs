// Translated from behavioral/sns/check_pending_subscription_arn_placeholder_not_resolvable.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static InMemoryAws.Tests.AwsEvaluationSuite.FidelityAssert;

namespace InMemoryAws.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.pending-subscription-arn-placeholder-not-resolvable</summary>
[Category("AwsEvaluationSuite")]
public sealed class PendingSubscriptionArnPlaceholderNotResolvable
{
    private const string Check = "pending-subscription-arn-placeholder-not-resolvable";

    /// <summary>
    /// Subscribe on email returns "pending confirmation", ListSubscriptionsByTopic lists the entry as
    /// "PendingConfirmation", and that placeholder is refused as a SubscriptionArn (InvalidParameter)
    /// by Get/SetSubscriptionAttributes while the pending and a confirmed subscription stay intact.
    /// </summary>
    [Test]
    [DisplayName("sns.pending-subscription-arn-placeholder-not-resolvable")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var topicArn = await CreateTopic(sns, ctx, FidelityContext.UniqueName("sim2real-behav-sns-pending-"));

        // A confirmed control subscription first, so the topic is not trivially empty.
        var confirmed = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "sqs",
            Endpoint = FakeQueueArn(topicArn, FidelityContext.UniqueName("sim2real-behav-sns-pending-q-")),
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = confirmed }));
        Ensure(confirmed.StartsWith(topicArn + ":", StringComparison.Ordinal),
            $"expected a real SubscriptionArn for the sqs control subscription, got '{confirmed}'");

        // `email` requires out-of-band confirmation, so it stays pending for the life of the check;
        // @example.com is IANA-reserved, so no third party is contacted.
        var pendingResp = await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "email", Endpoint = "sim2real-behav-sns@example.com"
        });
        Ensure(pendingResp.SubscriptionArn == "pending confirmation",
            "Subscribe on a confirmation-requiring protocol must return the lowercase literal " +
            $"'pending confirmation', got '{pendingResp.SubscriptionArn}'");

        var listed = await Poll(() => Subs(sns, topicArn), subs => subs.Any(s => s.Protocol == "email"), 30.0, 2.0,
            "the pending email subscription appears in ListSubscriptionsByTopic");
        var emailEntries = listed.Where(s => s.Protocol == "email").ToList();
        Ensure(emailEntries.Count == 1, $"expected exactly one email subscription, got {emailEntries.Count}");
        var placeholder = emailEntries[0].SubscriptionArn;
        Ensure(placeholder == "PendingConfirmation",
            "ListSubscriptionsByTopic must report the PascalCase literal 'PendingConfirmation' " +
            $"as the SubscriptionArn of an unconfirmed subscription, got '{placeholder}'");
        Ensure(emailEntries[0].TopicArn == topicArn, $"expected the pending entry to carry the real TopicArn, got '{emailEntries[0].TopicArn}'");
        Ensure(emailEntries[0].Endpoint == "sim2real-behav-sns@example.com",
            $"expected the pending entry to carry the real Endpoint, got '{emailEntries[0].Endpoint}'");

        // SNS refuses the placeholder with InvalidParameter, not NotFound.
        var (codeGet, msgGet) = await Error(() => sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = placeholder }));
        Ensure(codeGet == "InvalidParameter", $"expected InvalidParameter for GetSubscriptionAttributes('{placeholder}'), got {codeGet}");
        const string expected = "Invalid parameter: SubscriptionArn Reason: An ARN must have at least 6 elements, not 1";
        Ensure(msgGet == expected, $"expected message '{expected}', got '{msgGet}'");
        var (codeSet, msgSet) = await Error(() => sns.SetSubscriptionAttributesAsync(new SetSubscriptionAttributesRequest
        {
            SubscriptionArn = placeholder, AttributeName = "RawMessageDelivery", AttributeValue = "true"
        }));
        Ensure(codeSet == "InvalidParameter", $"expected InvalidParameter for SetSubscriptionAttributes('{placeholder}'), got {codeSet}");
        Ensure(msgSet == expected, $"expected message '{expected}', got '{msgSet}'");

        var still = (await Subs(sns, topicArn)).Where(s => s.Protocol == "email").ToList();
        Ensure(still.Count == 1 && still[0].SubscriptionArn == "PendingConfirmation",
            $"the pending subscription must be untouched by the rejected calls, got {still.Count} email subscription(s)");

        // The confirmed subscription still resolves, so the refusals were about the placeholder.
        var attrs = (await sns.GetSubscriptionAttributesAsync(new GetSubscriptionAttributesRequest { SubscriptionArn = confirmed })).Attributes ?? [];
        Ensure(attrs.GetValueOrDefault("PendingConfirmation") == "false",
            $"expected the sqs control subscription to report PendingConfirmation=false, got '{attrs.GetValueOrDefault("PendingConfirmation")}'");
        Ensure(attrs.GetValueOrDefault("TopicArn") == topicArn,
            $"expected the control subscription's TopicArn == {topicArn}, got '{attrs.GetValueOrDefault("TopicArn")}'");
    }

    /// <summary>CreateTopic (tagged) + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name)
    {
        var arn = (await client.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Tags = [new Tag { Key = "sim2real", Value = Check }, new Tag { Key = "sim2real:managed", Value = "true" }],
        })).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }

    /// <summary>A well-formed SQS queue ARN in the topic's account/region (SNS confirms sqs subscriptions without verifying the queue exists).</summary>
    private static string FakeQueueArn(string topicArn, string name)
    {
        var parts = topicArn.Split(':');
        return $"arn:aws:sqs:{parts[3]}:{parts[4]}:{name}";
    }

    /// <summary>Poll fn() until pred(val); raise on deadline miss.</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {val}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    private static async Task<List<Subscription>> Subs(AmazonSimpleNotificationServiceClient client, string topicArn) =>
        (await client.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];

    /// <summary>Call fn(); return (code, message) of the ClientError it raises. An unexpected success raises.</summary>
    private static async Task<(string? Code, string? Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        return error is null
            ? throw Fail($"expected the call to be refused, but it succeeded and returned {result}")
            : (error.Code, error.Message);
    }
}
