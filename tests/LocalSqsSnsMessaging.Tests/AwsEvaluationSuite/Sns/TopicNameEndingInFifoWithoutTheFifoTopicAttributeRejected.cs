// Translated from behavioral/sns/check_topic_name_ending_in_fifo_without_the_fifo_topic_attribute_rejected.py
// (https://github.com/aws/aws-emulator-evaluation-suite, commit 4df2823).
using Amazon.SimpleNotificationService.Model;
using static LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.FidelityAssert;

namespace LocalSqsSnsMessaging.Tests.AwsEvaluationSuite.Sns;

/// <summary>sns.topic-name-ending-in-fifo-without-the-fifo-topic-attribute-rejected</summary>
[Category("AwsEvaluationSuite")]
public sealed class TopicNameEndingInFifoWithoutTheFifoTopicAttributeRejected
{
    private const string Scenario = "sns-fifo-suffix-standard-topic";

    /// <summary>
    /// CreateTopic of a .fifo name without FifoTopic=true is refused InvalidParameter and creates
    /// no topic (NotFound, not listed); with FifoTopic=true it creates a FIFO topic, and the
    /// unsuffixed name creates a standard one.
    /// </summary>
    [Test]
    [DisplayName("sns.topic-name-ending-in-fifo-without-the-fifo-topic-attribute-rejected")]
    [Timeout(FidelityContext.HangTimeoutMilliseconds)]
    public async Task Run(CancellationToken cancellationToken)
    {
        await using var ctx = new FidelityContext(FidelityContext.BudgetMilliseconds, cancellationToken);
        var sns = ctx.Sns;
        var @base = FidelityContext.UniqueName("sim2real-behav-sns-fifosfx-");
        var name = @base + ".fifo";

        foreach (var attrs in new[] { new Dictionary<string, string>(), new Dictionary<string, string> { ["FifoTopic"] = "false" } })
        {
            var (code, message) = await Error(() => sns.CreateTopicAsync(new CreateTopicRequest { Name = name, Attributes = attrs, Tags = TagsKv() }));
            Ensure((code, message) == ("InvalidParameter", "Invalid parameter: Topic Name"),
                $"CreateTopic of a .fifo name with attributes {{{string.Join(", ", attrs.Select(kv => $"'{kv.Key}': '{kv.Value}'"))}}}: got '{code}' '{message}'");
        }

        var standard = (await sns.CreateTopicAsync(new CreateTopicRequest { Name = @base, Tags = TagsKv() })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = standard }));
        var fifoArn = standard + ".fifo";

        {
            var (code, message) = await Error(() => sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = fifoArn }));
            Ensure((code, message) == ("NotFound", "Topic does not exist"),
                $"the refused name must not exist, GetTopicAttributes gave '{code}' '{message}'");
        }

        var listed = new HashSet<string>();
        await foreach (var page in sns.Paginators.ListTopics(new ListTopicsRequest()).Responses)
        {
            foreach (var t in page.Topics ?? [])
            {
                if (t.TopicArn.StartsWith(standard, StringComparison.Ordinal))
                {
                    listed.Add(t.TopicArn);
                }
            }
        }

        Ensure(listed.SetEquals([standard]),
            $"only the standard topic may list under this run's name, got {listed.Count} topic(s)");

        var created = (await sns.CreateTopicAsync(new CreateTopicRequest
        {
            Name = name,
            Attributes = new() { ["FifoTopic"] = "true" },
            Tags = TagsKv(),
        })).TopicArn;
        ctx.OnCleanup(() => sns.DeleteTopicAsync(new DeleteTopicRequest { TopicArn = created }));
        Ensure(created == fifoArn, "the FIFO topic's ARN must be the standard topic's plus '.fifo'");
        var fifoAttrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = created })).Attributes ?? [];
        var stdAttrs = (await sns.GetTopicAttributesAsync(new GetTopicAttributesRequest { TopicArn = standard })).Attributes ?? [];
        Ensure((fifoAttrs.GetValueOrDefault("FifoTopic"), stdAttrs.GetValueOrDefault("FifoTopic", "false")) == ("true", "false"),
            $"FifoTopic read back as '{fifoAttrs.GetValueOrDefault("FifoTopic")}' on the .fifo topic and " +
            $"'{stdAttrs.GetValueOrDefault("FifoTopic")}' on the standard one");
    }

    /// <summary>The (Code, Message) of the ClientError fn raises. An unexpected success raises too.</summary>
    private static async Task<(string? Code, string Message)> Error<T>(Func<Task<T>> fn)
        where T : class
    {
        var (result, error) = await FidelityContext.Capture(fn);
        if (error is null)
        {
            throw Fail($"expected the call to be refused, but it succeeded: {result}");
        }

        return (error.Code, error.Message ?? "");
    }

    private static List<Tag> TagsKv() =>
        [new() { Key = "sim2real", Value = Scenario }, new() { Key = "sim2real:managed", Value = "true" }];
}
