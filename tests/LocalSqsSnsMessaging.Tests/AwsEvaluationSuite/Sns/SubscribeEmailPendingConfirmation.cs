// Translated from behavioral/sns/check_subscribe_email_pending_confirmation.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.subscribe-email-pending-confirmation</summary>
[Category("AwsEvaluationSuite")]
public sealed class SubscribeEmailPendingConfirmation
{
    private const string Scenario = "subscribe-email-pending-confirmation";

    /// <summary>Subscribing an email endpoint stays PendingConfirmation until ConfirmSubscription.</summary>
    [Test]
    [DisplayName("sns.subscribe-email-pending-confirmation")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var topicArn = await CreateTopic(ev, ctx, FidelityContext.UniqueName("sim2real-behav-sns-subemail-"), TagsKv());
        var resp = await ev.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = topicArn, Protocol = "email", Endpoint = "fidelity-check@example.com"
        });
        var subArn = resp.SubscriptionArn;
        Ensure(subArn == "pending confirmation",
            $"expected literal 'pending confirmation' SubscriptionArn for email protocol, got '{subArn}'");
        var subs = (await ev.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];
        var emailEntries = subs.Where(s => s.Protocol == "email").ToList();
        Ensure(emailEntries.Count == 1, $"expected exactly one email subscription, got {emailEntries.Count}");
        var listedArn = emailEntries[0].SubscriptionArn;
        Ensure(listedArn == "PendingConfirmation",
            "expected ListSubscriptionsByTopic to report the PascalCase literal 'PendingConfirmation' " +
            $"for the unconfirmed subscription, got '{listedArn}'");
    }

    private static List<Tag> TagsKv() =>
        [new Tag { Key = "sim2real", Value = Scenario }, new Tag { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name, List<Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (tags is not null)
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
