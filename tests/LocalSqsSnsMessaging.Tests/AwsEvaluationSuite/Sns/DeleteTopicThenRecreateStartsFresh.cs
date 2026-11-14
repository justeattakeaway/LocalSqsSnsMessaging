// Translated from behavioral/sns/check_delete_topic_then_recreate_starts_fresh.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using System.Text.Json.Nodes;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.delete-topic-then-recreate-starts-fresh</summary>
[Category("AwsEvaluationSuite")]
public sealed class DeleteTopicThenRecreateStartsFresh
{
    private const string Check = "delete-topic-then-recreate-starts-fresh";

    /// <summary>Poll fn() until pred(val); raise AssertionError on deadline miss (never a bare sleep).</summary>
    private static async Task<T> Poll<T>(Func<Task<T>> fn, Func<T, bool> pred, double timeoutS, double intervalS, string what, Func<T, string> show)
    {
        var deadline = FidelityTime.StartNew();
        var val = await fn();
        while (!pred(val))
        {
            if (deadline.Elapsed >= TimeSpan.FromSeconds(timeoutS))
            {
                throw Fail($"{what}: condition not met within {timeoutS}s, last value {show(val)}");
            }

            await FidelityTime.Delay(TimeSpan.FromSeconds(intervalS));
            val = await fn();
        }

        return val;
    }

    /// <summary>
    /// A topic recreated under a deleted topic's name gets the same ARN but none of its state:
    /// no DisplayName, tags, AddPermission statement or subscriptions, and zeroed counters.
    /// </summary>
    [Test]
    [DisplayName("sns.delete-topic-then-recreate-starts-fresh")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    [KnownFidelityGap(FidelityGap.SnsSubscriptions, "Subscribe to the ARN of a queue that does not exist is refused with NotFound 'Queue not found.' instead of creating the subscription")]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-fresh-");
        var arn = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new() { ["DisplayName"] = "old" },
            Tags = [new() { Key = "sim2real", Value = Check }, new() { Key = "gen", Value = "1" }],
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        var parts = arn.Split(':');
        var (region, account) = (parts[3], parts[4]);
        var label = FidelityContext.UniqueName("sim2real-behav-lbl-");
        await sns.AddPermissionAsync(new AddPermissionRequest { TopicArn = arn, Label = label, AWSAccountId = [account], ActionName = ["Publish"] });
        var sub = (await sns.SubscribeAsync(new SubscribeRequest
        {
            TopicArn = arn,
            Protocol = "sqs",
            ReturnSubscriptionArn = true,
            Endpoint = $"arn:aws:sqs:{region}:{account}:{FidelityContext.UniqueName("sim2real-behav-sns-fresh-q-")}",
        })).SubscriptionArn;
        ctx.OnCleanup(() => sns.UnsubscribeAsync(new UnsubscribeRequest { SubscriptionArn = sub }));

        await sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn });
        var again = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = name })).TopicArn;
        Ensure(again == arn, "a topic recreated under the same name must get the same ARN");

        var attrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes;
        Ensure(attrs.GetValueOrDefault("DisplayName") == "",
            $"the recreated topic must not inherit DisplayName, got '{attrs.GetValueOrDefault("DisplayName")}'");
        var sids = (JsonNode.Parse(attrs["Policy"])!["Statement"]?.AsArray() ?? []).Select(s => (string?)s?["Sid"]).ToHashSet();
        Ensure(!sids.Contains(label), "the recreated topic must not inherit the old AddPermission statement");
        var tagsNow = (await sns.ListTagsForResourceAsync(new ListTagsForResourceRequest { ResourceArn = arn })).Tags ?? [];
        Ensure(tagsNow.Count == 0, $"the recreated topic must have no tags, got {tagsNow.Count} tag(s)");

        var subs = await Poll(
            async () => (await sns.ListSubscriptionsByTopicAsync(new ListSubscriptionsByTopicRequest { TopicArn = arn })).Subscriptions ?? [],
            v => v.Count == 0, timeoutS: 90.0, intervalS: 3.0,
            what: "the recreated topic lists no subscriptions",
            show: v => "[" + string.Join(", ", v.Select(s => $"{{'SubscriptionArn': '{s.SubscriptionArn}', 'Endpoint': '{s.Endpoint}'}}")) + "]");
        Ensure(subs.Count == 0, "the recreated topic must not inherit the old subscription");
        var counters = await Poll(
            async () =>
            {
                var a = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = arn })).Attributes;
                return (Confirmed: a.GetValueOrDefault("SubscriptionsConfirmed"), Pending: a.GetValueOrDefault("SubscriptionsPending"));
            },
            v => v is ("0", "0"),
            timeoutS: 90.0, intervalS: 3.0, what: "the recreated topic's subscription counters read zero",
            show: Show);
        Ensure(counters is ("0", "0"), Show(counters));
    }

    private static string Show((string? Confirmed, string? Pending) v) =>
        $"{{'SubscriptionsConfirmed': {(v.Confirmed is null ? "None" : $"'{v.Confirmed}'")}, 'SubscriptionsPending': {(v.Pending is null ? "None" : $"'{v.Pending}'")}}}";
}
