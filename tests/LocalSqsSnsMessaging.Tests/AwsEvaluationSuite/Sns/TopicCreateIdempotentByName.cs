// Translated from behavioral/sns/check_topic_create_idempotent_by_name.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-create-idempotent-by-name</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicCreateIdempotentByName
{
    private const string Scenario = "sns-topic-create-idempotent-by-name";

    /// <summary>CreateTopic with an existing Name is idempotent and returns the same ARN.</summary>
    [Test]
    [DisplayName("sns.topic-create-idempotent-by-name")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var ev = ctx.Sns;
        var name = FidelityContext.UniqueName("sim2real-behav-sns-idem-");
        var arn1 = await CreateTopic(ev, ctx, name, tags: TagsKv());
        var resp2 = await ev.CreateTopicAsync(new CreateTopicRequest { Name = name });
        var arn2 = resp2.TopicArn;
        Ensure(arn1 == arn2, $"expected same TopicArn on repeat create, got {arn1} vs {arn2}");
        var topics = new List<Topic>();
        await foreach (var page in ev.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            topics.AddRange(page.Topics ?? []);
        }

        var matches = topics.Where(t => t.TopicArn == arn1).ToList();
        Ensure(matches.Count == 1, $"expected exactly one topic for {name}, found {matches.Count}");
    }

    private static List<Amazon.SimpleNotificationService.Model.Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];

    /// <summary>CreateTopic + register best-effort DeleteTopic cleanup; returns the TopicArn.</summary>
    private static async Task<string> CreateTopic(AmazonSimpleNotificationServiceClient client, FidelityContext ctx, string name,
        Dictionary<string, string>? attributes = null, List<Amazon.SimpleNotificationService.Model.Tag>? tags = null)
    {
        var request = new CreateTopicRequest { Name = name };
        if (attributes is { Count: > 0 })
        {
            request.Attributes = attributes;
        }

        if (tags is { Count: > 0 })
        {
            request.Tags = tags;
        }

        var arn = (await client.CreateTopicAsync(request)).TopicArn;
        ctx.OnCleanup(() => client.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = arn }));
        return arn;
    }
}
